using System;
using System.Collections.Generic;
using System.Linq;
using ADOFAI;
using UnityEngine;

namespace Angle
{
    internal static class PatternRhythmLayout
    {
        private const double Pi = (float)Math.PI;
        private const double Circle = 2 * Pi;
        private const double AngleTolerance = 0.000002;
        internal const int SeamSize = 3;
        private const int Lookback = 12;
        private const int VerificationLimit = 4;
        private const int SearchWidth = 32;
        private const double StraightTolerance = Math.PI / 36;

        internal readonly struct PressGroup
        {
            internal readonly int First;
            internal readonly int Last;
            internal readonly int Presses;

            internal PressGroup(int first, int last, int presses)
            {
                First = first;
                Last = last;
                Presses = presses;
            }

            internal bool Contains(int index)
            {
                return index >= First && index <= Last;
            }
        }

        private sealed class Step
        {
            internal Step Parent;
            internal double Entry;
            internal double Exit;
            internal bool Flipped;
            internal Vector2 Position;
            internal double Score;
            internal int InsertBefore;
        }

        internal static float ExitHeading(scrFloor floor)
        {
            return floor.midSpin ? 999f : Heading(floor.exitangle);
        }

        internal static int FirstRealTrack(IList<scrFloor> floors, int anchor, int length)
        {
            int index = 0;
            while (index < length && floors[anchor + index].midSpin)
            {
                index++;
            }
            return index;
        }

        internal static bool MatchesPreset(scrFloor floor, float direction)
        {
            return !floor.midSpin
                && Math.Abs(PatternPasteGeometry.SignedDifference(direction, ExitHeading(floor))) <= 0.001f;
        }

        internal static int MapFloor(int id, int[] midspins)
        {
            return id + midspins.Count(index => index <= id);
        }

        private static bool CanInsertMidspin(IList<scrFloor> floors, int id)
        {
            if (id <= 0 || id >= floors.Count)
            {
                return false;
            }
            scrFloor previous = floors[id - 1];
            scrFloor next = floors[id];
            return !previous.midSpin && !next.midSpin && previous.numPlanets == 2 && next.numPlanets == 2
                && previous.holdLength < 0 && next.holdLength < 0 && next.tapsNeeded == 1
                && !previous.freeroam && !next.freeroam
                && !previous.shouldBeAutoPlayed && !next.shouldBeAutoPlayed
                && !previous.auto && !next.auto;
        }

        private static float Heading(double exit)
        {
            return PatternPasteGeometry.Normalize(90f - (float)(exit * (180 / Pi)));
        }

        private static double Radians(float heading)
        {
            return (90f - heading) * ((float)Pi / 180f);
        }

        private static Vector2 Vector(double angle)
        {
            return new Vector2((float)Math.Sin(angle), (float)Math.Cos(angle));
        }

        private static double Normalize(double angle)
        {
            return scrMisc.mod(angle, Circle);
        }

        private static double AngleDistance(double left, double right)
        {
            return Math.Abs(Normalize(left - right + Pi) - Pi);
        }

        internal static bool TryApply(scnEditor editor, int anchor, int approach, int length, int count, float direction,
            double[] turns, Func<int, bool> timingDiffers, PressGroup? group, bool allowMidspin, bool joinAfterDouble,
            Func<int[], bool> verify, out int[] midspins)
        {
            midspins = Array.Empty<int>();
            length += approach;
            if (count < length)
            {
                return false;
            }
            List<scrFloor> floors = editor.floors;
            int first = approach + FirstRealTrack(floors, anchor + approach, length - approach);
            if (first >= length || !MatchesPreset(floors[anchor + first], direction))
            {
                return false;
            }
            int continuation = joinAfterDouble ? group.Value.Last : first;
            if (joinAfterDouble && (group.Value.Presses != 2 || group.Value.First != first + 1
                || continuation >= length || floors[anchor + continuation].midSpin
                || !timingDiffers(first)))
            {
                return false;
            }
            float continuationHeading = ExitHeading(floors[anchor + continuation]);
            int headEnd = Math.Min(length, first + SeamSize + 1);
            int tailStart = Math.Max(first + 1, length - SeamSize);
            bool headNeedsRepair = Enumerable.Range(0, headEnd).Any(timingDiffers);
            bool tailNeedsRepair = count > length && Enumerable.Range(length, count - length).Any(timingDiffers);
            bool[] movable = new bool[count];
            int localApproachStart = Math.Max(0, approach - 2);
            for (int index = 0; index < approach; index++)
            {
                scrFloor floor = floors[anchor + index];
                movable[index] = ((headNeedsRepair && index >= localApproachStart) || group?.Contains(index) == true)
                    && !floor.midSpin && floor.holdLength < 0 && !floor.freeroam;
            }
            for (int index = first + 1; index < length; index++)
            {
                movable[index] = !floors[anchor + index].midSpin
                    && ((headNeedsRepair && index < headEnd) || (tailNeedsRepair && index >= tailStart)
                        || group?.Contains(index) == true);
            }
            if (joinAfterDouble)
            {
                movable[first] = true;
                movable[continuation] = false;
                for (int index = continuation + 1; index < tailStart; index++)
                {
                    movable[index] = false;
                }
            }
            if (!headNeedsRepair && !tailNeedsRepair)
            {
                return false;
            }
            var specialInputFloors = new HashSet<int>(editor.events.Where(evt => evt.active
                && (evt.eventType == LevelEventType.ScaleMargin || evt.eventType == LevelEventType.Checkpoint
                    || evt.eventType == LevelEventType.SetConditionalEvents)).Select(evt => evt.floor));
            var floorEvents = editor.events
                .Where(evt => evt.eventType == LevelEventType.Twirl || evt.eventType == LevelEventType.FreeRoam)
                .ToLookup(evt => evt.floor);
            bool rootMayTwirl = anchor != 0 || new LevelEvent(0, LevelEventType.Twirl).info.allowFirstFloorCheck;
            bool[] mayToggle = Enumerable.Range(anchor, count + 1).Select(id =>
                floorEvents[id].Any(evt => evt.active && evt.eventType == LevelEventType.Twirl)
                || ((id != 0 || rootMayTwirl)
                    && !floorEvents[id].Any(evt => evt.active && evt.eventType == LevelEventType.FreeRoam))).ToArray();
            Vector2 target = Vector(Radians(direction));
            Vector2 origin = floors[anchor].startPos;
            float scale = Math.Max(scrController.instance.tileSize, 0.001f);
            Vector2[] preceding = Enumerable.Range(Math.Max(0, anchor - Lookback), Math.Min(anchor, Lookback))
                .Select(id => ((Vector2)floors[id].startPos - origin) / scale).Reverse().ToArray();
            Vector2 flow = Vector2.zero;
            for (int index = 0; index < preceding.Length; index++)
            {
                Vector2 previous = index == 0 ? Vector2.zero : preceding[index - 1];
                flow += (previous - preceding[index]) / (1 + index * 0.14f);
            }
            flow = flow.normalized;
            var candidates = new List<Step> { new Step { Entry = floors[anchor].entryangle, InsertBefore = -1 } };
            for (int index = 0; index < count; index++)
            {
                scrFloor floor = floors[anchor + index];
                bool adjustAngle = movable[index];
                bool inGroup = group?.Contains(index) == true;
                bool insertable = allowMidspin && inGroup && index > group.Value.First && index <= length
                    && (!joinAfterDouble || index == continuation)
                    && !specialInputFloors.Contains(anchor + index)
                    && CanInsertMidspin(floors, anchor + index);
                bool seam = adjustAngle || inGroup || (index >= localApproachStart && index <= first)
                    || (index == headEnd && headNeedsRepair)
                    || index >= length;
                bool scoreJoin = (headNeedsRepair && index >= localApproachStart && index <= headEnd + 3)
                    || (tailNeedsRepair && index >= tailStart) || (group.HasValue
                        && (inGroup || (index > group.Value.Last && index <= group.Value.Last + 3)));
                var next = new List<Step>();
                foreach (Step parent in candidates)
                {
                    int spinChoices = seam && !floor.midSpin ? 2 : 1;
                    bool mayInsert = insertable && parent.InsertBefore < 0;
                    int choices = spinChoices * (mayInsert ? 2 : 1);
                    for (int choice = 0; choice < choices; choice++)
                    {
                        bool insert = choice >= spinChoices;
                        double incoming = insert ? Normalize((float)parent.Entry + Pi) : parent.Entry;
                        bool flip = floor.midSpin && seam ? parent.Flipped : choice % spinChoices != 0;
                        bool toggle = flip != parent.Flipped;
                        if (toggle && !mayToggle[index])
                        {
                            continue;
                        }
                        bool clockwise = !floor.isCCW ^ flip;
                        double exit = floor.midSpin ? (float)incoming
                            : adjustAngle ? Normalize(incoming + (clockwise ? turns[index] : -turns[index]))
                            : floor.exitangle;
                        if (!adjustAngle && !floor.midSpin
                            && AngleDistance(scrMisc.GetAngleMoved(incoming, exit, clockwise), turns[index]) > AngleTolerance)
                        {
                            continue;
                        }
                        double nextEntry = (exit + Pi) % Circle;
                        if (index + 1 == count && flip && !mayToggle[count])
                        {
                            continue;
                        }
                        Vector2 vector = Vector(exit);
                        Vector2 position = parent.Position + vector + (insert ? Vector(parent.Entry) : Vector2.zero);
                        bool changed = adjustAngle && AngleDistance(exit, floor.exitangle) > AngleTolerance;
                        double bend = AngleDistance(exit, (insert ? incoming : parent.Entry) + Pi);
                        if (joinAfterDouble && index == first && bend > StraightTolerance)
                        {
                            continue;
                        }
                        if (changed && ((turns[index] > Pi / 3 + AngleTolerance && bend > 3 * Pi / 4)
                            || Retraces(parent, position, preceding, index)))
                        {
                            continue;
                        }
                        if (insert && Vector2.Dot(vector, Vector(parent.Entry)) > 0.93f)
                        {
                            continue;
                        }
                        double score = parent.Score - (toggle ? 0.85 : 0) - (insert ? 1.5 : 0);
                        if (adjustAngle)
                        {
                            score += 0.55 * Vector2.Dot(vector, target)
                                + Vector2.Dot(vector, Vector(floor.exitangle))
                                + (index < headEnd ? 0.45 * Vector2.Dot(vector, flow) : 0);
                            if (changed)
                            {
                                score -= 1.35;
                            }
                        }
                        if (scoreJoin && !floor.midSpin)
                        {
                            double oldBend = AngleDistance(floor.exitangle, floor.entryangle + Pi);
                            score -= (oldBend <= StraightTolerance && bend > StraightTolerance ? 2.25 : 0)
                                + 1.3 * (bend * bend - oldBend * oldBend) / (Pi * Pi);
                            score -= OverlapCost(parent, position, preceding, index);
                        }
                        if (insert)
                        {
                            score -= OverlapCost(parent, parent.Position + Vector(parent.Entry), preceding, index);
                        }
                        else if (index == length && length > 0)
                        {
                            score += 0.35 * Vector2.Dot(Vector(parent.Exit), vector);
                        }
                        next.Add(new Step
                        {
                            Parent = parent, Entry = nextEntry, Exit = exit,
                            Flipped = flip, Position = position, Score = score,
                            InsertBefore = insert ? anchor + index : parent.InsertBefore
                        });
                    }
                }
                candidates = next.Count <= SearchWidth ? next : next
                    .GroupBy(item => item.InsertBefore >= 0)
                    .SelectMany(bucket => bucket.OrderByDescending(item => item.Score)
                        .Take(allowMidspin ? SearchWidth / 2 : SearchWidth)).ToList();
                if (candidates.Count == 0)
                {
                    return false;
                }
            }

            List<Step[]> layouts = new List<Step[]>();
            foreach (Step candidate in candidates.Where(item => !allowMidspin || item.InsertBefore >= 0)
                .OrderByDescending(item => item.Score).Take(VerificationLimit))
            {
                var sequence = new Step[count];
                Step step = candidate;
                for (int index = count - 1; index >= 0; index--, step = step.Parent)
                {
                    sequence[index] = step;
                }
                layouts.Add(sequence);
            }
            return layouts.Count > 0 && ApplyCandidates(editor, anchor, length, count, movable, layouts,
                inserted => MatchesPreset(editor.floors[MapFloor(anchor + continuation, inserted)], continuationHeading)
                    && verify(inserted), out midspins);
        }

        private static bool Retraces(Step parent, Vector2 position, Vector2[] preceding, int index)
        {
            Step older = parent.Parent;
            for (int age = 1; older != null && age <= Lookback; age++, older = older.Parent)
            {
                if ((position - older.Position).sqrMagnitude < 0.0025f)
                {
                    return true;
                }
            }
            return index < Lookback && preceding.Take(Math.Max(0, Lookback - index))
                .Any(point => (position - point).sqrMagnitude < 0.0025f);
        }

        private static double OverlapCost(Step parent, Vector2 position, Vector2[] preceding, int index)
        {
            double cost = 0;
            Step older = parent.Parent;
            for (int age = 1; older != null && age <= Lookback; age++, older = older.Parent)
            {
                cost += NearTile(position, older.Position);
            }
            for (int age = 0; age < preceding.Length && index + age < Lookback; age++)
            {
                cost += NearTile(position, preceding[age]);
            }
            return cost;
        }

        private static double NearTile(Vector2 left, Vector2 right)
        {
            float distance = (left - right).sqrMagnitude;
            return distance < 0.09f ? 1.35 : distance < 0.49f ? 0.45 : 0;
        }

        private static bool ApplyCandidates(scnEditor editor, int anchor, int length, int count,
            bool[] movable, List<Step[]> candidates, Func<int[], bool> verify, out int[] midspins)
        {
            midspins = Array.Empty<int>();
            LevelData level = editor.levelData;
            List<float> beforeAngles = level.angleData;
            string beforePath = level.pathData;
            bool wasLegacy = level.isOldLevel;
            LevelEvent[] beforeEvents = editor.events.ToArray();
            var beforeDecorations = level.decorations;
            int lastFloor = editor.floors.Count - 1;
            var disabledTwirls = beforeEvents.Where(evt => evt.eventType == LevelEventType.Twirl && !evt.active)
                .ToLookup(evt => evt.floor);
            float[] baseAngles = wasLegacy ? editor.floors.Take(editor.floors.Count - 1).Select(ExitHeading).ToArray()
                : beforeAngles.ToArray();
            foreach (Step[] steps in candidates)
            {
                int insertBefore = steps[steps.Length - 1].InsertBefore;
                var angles = new List<float>(baseAngles);
                for (int index = 0; index < length; index++)
                {
                    if (movable[index])
                    {
                        angles[anchor + index] = Heading(steps[index].Exit);
                    }
                }
                if (insertBefore >= 0)
                {
                    angles.Insert(insertBefore, 999f);
                }
                if (wasLegacy && PatternPasteFeature.TryLegacyCharacters(angles, out string path))
                {
                    level.isOldLevel = true;
                    level.pathData = path;
                    level.angleData = beforeAngles;
                }
                else
                {
                    level.isOldLevel = false;
                    level.pathData = "";
                    level.angleData = angles;
                }
                editor.events.Clear();
                var transitions = new HashSet<int>();
                bool previous = false;
                for (int index = 0; index <= count; index++)
                {
                    bool flip = index < count && steps[index].Flipped;
                    if (flip != previous)
                    {
                        transitions.Add(anchor + index);
                    }
                    previous = flip;
                }
                foreach (LevelEvent evt in beforeEvents)
                {
                    if (evt.eventType != LevelEventType.Twirl || !transitions.Contains(evt.floor))
                    {
                        editor.events.Add(evt);
                        continue;
                    }
                    if (evt.active)
                    {
                        transitions.Remove(evt.floor);
                    }
                    else
                    {
                        editor.events.Add(evt);
                    }
                }
                foreach (int id in transitions)
                {
                    LevelEvent inactive = disabledTwirls[id].FirstOrDefault();
                    LevelEvent twirl = inactive?.Copy() ?? new LevelEvent(id, LevelEventType.Twirl);
                    if (inactive != null)
                    {
                        editor.events.Remove(inactive);
                        twirl.active = true;
                    }
                    editor.events.Add(twirl);
                }
                level.decorations = beforeDecorations;
                if (insertBefore >= 0)
                {
                    var mappedEvents = editor.events.Select(evt => MoveEvent(evt, insertBefore, lastFloor)).ToArray();
                    editor.events.Clear();
                    editor.events.AddRange(mappedEvents);
                    level.decorations = new DecorationsArray<LevelEvent>();
                    level.decorations.AddRange(beforeDecorations.Select(evt => MoveEvent(evt, insertBefore, lastFloor)));
                }
                editor.RemakePath();
                int[] inserted = insertBefore < 0 ? Array.Empty<int>() : new[] { insertBefore };
                if (verify(inserted))
                {
                    midspins = inserted;
                    if (inserted.Length > 0)
                    {
                        editor.UpdateDecorationObjects();
                        editor.propertyControlDecorationsList?.OnDecorationUpdate();
                    }
                    return true;
                }
            }
            level.angleData = beforeAngles;
            level.pathData = beforePath;
            level.isOldLevel = wasLegacy;
            level.decorations = beforeDecorations;
            editor.events.Clear();
            editor.events.AddRange(beforeEvents);
            editor.RemakePath();
            return false;
        }

        private static LevelEvent MoveEvent(LevelEvent evt, int insertBefore, int lastFloor)
        {
            LevelEvent moved = evt.Copy();
            if (evt.floor >= insertBefore)
            {
                moved.floor++;
            }
            foreach (string key in evt.info.propertiesInfo.Keys)
            {
                if (!evt.TryGet(key, out Tuple<int, TileRelativeTo> tile))
                {
                    continue;
                }
                int oldTarget;
                int originDelta;
                switch (tile.Item2)
                {
                    case TileRelativeTo.Start:
                        oldTarget = tile.Item1;
                        originDelta = 0;
                        break;
                    case TileRelativeTo.End:
                        oldTarget = lastFloor + tile.Item1;
                        originDelta = 1;
                        break;
                    case TileRelativeTo.ThisTile:
                        oldTarget = evt.floor + tile.Item1;
                        originDelta = moved.floor - evt.floor;
                        break;
                    default:
                        continue;
                }
                int delta = (oldTarget >= insertBefore ? 1 : 0) - originDelta;
                if (delta != 0)
                {
                    moved[key] = Tuple.Create(tile.Item1 + delta, tile.Item2);
                }
            }
            return moved;
        }

    }
}
