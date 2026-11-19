using System;
using System.Collections.Generic;
using System.Linq;
using ADOFAI;

namespace Angle
{
    internal sealed class PatternRhythm
    {
        private const double TimeTolerance = 0.00001;
        private const double ShortTurnTolerance = Math.PI / 18000;
        private const double PseudoGapSeconds = 0.075;
        private const double BurstSeparation = 1.45;
        private const int BurstSearchDistance = 8;
        private const int ApproachRepairSize = 2;
        private readonly LevelData sourceLevel;
        private readonly string sourcePath;
        private readonly double anchorEntryAngle;
        private readonly double precedingInterval;
        private readonly int anchor;
        private readonly int floorCount;
        private readonly int patternLength;
        private readonly double[] intervals;
        private readonly double[] rawTurns;
        private readonly InputTiming[] inputs;
        private readonly double[] approachIntervals;
        private readonly double[] approachTurns;
        private readonly InputTiming[] approachInputs;
        private readonly double rootCountdown;
        private readonly float baseBpm;
        private readonly bool relocated;
        private readonly PressMoment[] sourcePresses;
        private readonly bool pressContextStartsAtChartStart;
        private readonly bool pressContextEndsAtChartEnd;

        private struct PressMoment
        {
            internal int Floor;
            internal double Time;
            internal double GapBeats;
            internal bool Single;
            internal bool OrdinaryGap;
        }

        private struct InputTiming
        {
            private readonly int taps;
            private readonly int hold;
            private readonly int planets;
            private readonly bool midspin;
            private readonly bool autoplay;
            private readonly bool auto;
            private readonly bool freeRoam;
            private readonly float freeRoamSpeed;
            private readonly float freeRoamBeats;
            internal readonly float Speed;

            internal InputTiming(scrFloor floor)
            {
                taps = floor.tapsNeeded;
                hold = floor.holdLength;
                planets = floor.numPlanets;
                midspin = floor.midSpin;
                autoplay = floor.shouldBeAutoPlayed;
                auto = floor.auto;
                freeRoam = floor.freeroam;
                freeRoamSpeed = floor.speed;
                Speed = floor.speed;
                freeRoamBeats = floor.extraBeats;
            }

            internal bool Matches(scrFloor floor, bool verifyFreeRoamTiming)
            {
                return taps == floor.tapsNeeded && hold == floor.holdLength && planets == floor.numPlanets
                    && midspin == floor.midSpin
                    && autoplay == floor.shouldBeAutoPlayed && auto == floor.auto && freeRoam == floor.freeroam
                    && (!freeRoam || !verifyFreeRoamTiming || (Near(freeRoamSpeed, floor.speed)
                        && Near(freeRoamBeats, floor.extraBeats)));
            }

            internal int PressesTo(InputTiming next)
            {
                return next.auto ? 0 : Math.Max(0, next.taps - (midspin ? 1 : 0));
            }

            internal InputTiming WithMidspin(bool value)
            {
                return new InputTiming(this, value);
            }

            private InputTiming(InputTiming input, bool value)
            {
                this = input;
                midspin = value;
            }

            internal bool IsMidspin => midspin;
            internal bool IsOrdinary => hold < 0 && planets == 2 && !auto && !autoplay && !freeRoam;
        }

        private PatternRhythm(scnEditor editor, int anchor, int count)
        {
            sourceLevel = editor.levelData;
            sourcePath = ADOBase.levelPath;
            anchorEntryAngle = editor.floors[anchor].entryangle;
            precedingInterval = anchor > 0 ? Interval(editor, anchor - 1) : 0;
            this.anchor = anchor;
            baseBpm = editor.levelData.bpm;
            rootCountdown = Countdown();
            CaptureApproach(editor, anchor, out approachIntervals, out approachTurns, out approachInputs);
            sourcePresses = CapturePressMoments(editor, anchor, count,
                out pressContextStartsAtChartStart, out pressContextEndsAtChartEnd);
            patternLength = count;
            floorCount = editor.floors.Count;
            int last = Math.Min(anchor + count + 2, floorCount - 1);
            while (last < floorCount - 1 && editor.floors[last - 1].midSpin)
            {
                last++;
            }
            intervals = Enumerable.Range(anchor, last - anchor)
                .Select(id => Interval(editor, id)).ToArray();
            rawTurns = Enumerable.Range(anchor, intervals.Length).Select(id => RawTurn(editor.floors[id])).ToArray();
            inputs = Enumerable.Range(anchor, intervals.Length + 1)
                .Select(id => new InputTiming(editor.floors[id])).ToArray();
        }

        private PatternRhythm(PatternRhythm source, scnEditor editor, int anchor)
        {
            this.anchor = anchor;
            relocated = true;
            sourcePresses = source.sourcePresses;
            pressContextStartsAtChartStart = source.pressContextStartsAtChartStart;
            pressContextEndsAtChartEnd = source.pressContextEndsAtChartEnd;
            patternLength = source.patternLength;
            baseBpm = editor.levelData.bpm;
            rootCountdown = Countdown();
            CaptureApproach(editor, anchor, out approachIntervals, out approachTurns, out approachInputs);
            precedingInterval = approachIntervals.LastOrDefault();
            intervals = source.intervals.Select((time, index) =>
                (time - (index == 0 && source.anchor == 0 ? source.rootCountdown : 0))
                * source.baseBpm / baseBpm + (index == 0 && anchor == 0 ? rootCountdown : 0)).ToArray();
            rawTurns = source.rawTurns.Take(intervals.Length).ToArray();
            inputs = source.inputs.Take(intervals.Length + 1).ToArray();
            inputs[0] = new InputTiming(editor.floors[anchor]).WithMidspin(inputs[0].IsMidspin);
        }

        private static void CaptureApproach(scnEditor editor, int anchor, out double[] times,
            out double[] turns, out InputTiming[] approach)
        {
            int count = Math.Min(anchor, ApproachRepairSize);
            int start = anchor - count;
            times = Enumerable.Range(start, count).Select(id => Interval(editor, id)).ToArray();
            turns = Enumerable.Range(start, count).Select(id => RawTurn(editor.floors[id])).ToArray();
            approach = Enumerable.Range(start, count).Select(id => new InputTiming(editor.floors[id])).ToArray();
        }

        private static PressMoment[] CapturePressMoments(scnEditor editor, int anchor, int length,
            out bool startsAtChartStart, out bool endsAtChartEnd)
        {
            List<scrFloor> floors = editor.floors;
            int start = Math.Max(0, anchor - BurstSearchDistance - 2);
            while (start > 0)
            {
                bool hasPress = new InputTiming(floors[start - 1]).PressesTo(new InputTiming(floors[start])) > 0;
                start--;
                if (hasPress)
                {
                    break;
                }
            }
            startsAtChartStart = start == 0;
            endsAtChartEnd = true;
            double pitch = Math.Max(editor.levelData.pitch / 100d, 0.01);
            double time = 0;
            double beats = 0;
            bool ordinary = true;
            var presses = new List<PressMoment>();
            InputTiming current = new InputTiming(floors[start]);
            for (int id = start; id < floors.Count - 1; id++)
            {
                InputTiming next = new InputTiming(floors[id + 1]);
                double interval = Interval(editor, id);
                time += interval;
                beats += interval * editor.levelData.bpm * current.Speed / 60;
                ordinary &= current.IsOrdinary && next.IsOrdinary;
                int taps = current.PressesTo(next);
                current = next;
                if (taps <= 0)
                {
                    continue;
                }
                int floor = id + 1 - anchor;
                presses.Add(new PressMoment
                {
                    Floor = floor,
                    Time = time / pitch,
                    GapBeats = beats,
                    Single = taps == 1 && next.IsOrdinary,
                    OrdinaryGap = ordinary
                });
                if (floor > length)
                {
                    endsAtChartEnd = id + 1 == floors.Count - 1;
                    break;
                }
                beats = 0;
                ordinary = true;
            }
            return presses.ToArray();
        }

        private static double RawTurn(scrFloor floor)
        {
            return scrMisc.GetAngleMoved(floor.entryangle, floor.exitangle, !floor.isCCW);
        }

        private static double Countdown()
        {
            scrConductor conductor = scrConductor.instance;
            return conductor.crotchetAtStart * (conductor.adjustedCountdownTicks - 1f);
        }

        internal PatternRhythm ForPaste(scnEditor editor, int target, bool directed)
        {
            if (Matches(editor, target))
            {
                return this;
            }
            return directed && baseBpm > 0 && editor.levelData.bpm > 0
                ? new PatternRhythm(this, editor, target) : null;
        }

        internal static PatternRhythm Capture(scnEditor editor, int first, int count)
        {
            return first > 0 && count > 0 && first <= editor.floors.Count - count
                ? new PatternRhythm(editor, first - 1, count) : null;
        }

        internal bool Matches(scnEditor editor, int target)
        {
            return anchor == target && Near(0, Math.Sin((editor.floors[target].entryangle - anchorEntryAngle) / 2))
                && (ReferenceEquals(sourceLevel, editor.levelData)
                    || (!string.IsNullOrEmpty(sourcePath) && sourcePath == ADOBase.levelPath));
        }

        internal int PasteIntervalCount(scnEditor editor, int count)
        {
            return !relocated && floorCount - count == editor.floors.Count ? intervals.Length : count;
        }

        private static double Interval(scnEditor editor, int id)
        {
            return editor.floors[id + 1].entryTime - editor.floors[id].entryTime;
        }

        private static bool Near(double left, double right)
        {
            return Math.Abs(left - right) <= TimeTolerance;
        }

        private static bool PositiveFloat(double value)
        {
            return value > 0 && value <= float.MaxValue && (float)value > 0;
        }

        internal const int ThroughExit = int.MaxValue;

        internal bool Preserve(scnEditor editor, int intervalCount, float? direction, out bool reshaped,
            out int[] midspins)
        {
            reshaped = false;
            midspins = Array.Empty<int>();
            int captured = relocated ? Math.Min(intervals.Length, patternLength) : intervals.Length;
            int count = Math.Min(captured,
                Math.Min(intervalCount, editor.floors.Count - anchor - 1));
            if (!InputsMatch(editor, count, verifyFreeRoamTiming: false))
            {
                return false;
            }
            int firstReal = PatternRhythmLayout.FirstRealTrack(editor.floors, anchor, Math.Min(patternLength, count));
            if (direction.HasValue && (firstReal >= count
                || !PatternRhythmLayout.MatchesPreset(editor.floors[anchor + firstReal], direction.Value)))
            {
                return false;
            }
            if (TimingMatches(editor, count) || TryMatchWithTwirls(editor, count, direction.HasValue, firstReal))
            {
                return true;
            }
            if (direction.HasValue && AngleMod.settings?.autoLayoutOptimization != false
                && TryMatchLayout(editor, count, firstReal, direction.Value, out midspins))
            {
                reshaped = true;
                return true;
            }
            return AngleMod.settings?.allowRhythmSpeedEvents == true
                && TryMatchWithSpeed(editor, count, direction.HasValue, firstReal);
        }

        private bool TryMatchLayout(scnEditor editor, int count, int firstReal, float direction, out int[] midspins)
        {
            bool entryChanged = Enumerable.Range(0, firstReal + 1)
                .Any(index => !Near(Interval(editor, anchor + index), intervals[index]));
            int lead = entryChanged ? Math.Min(ApproachRepairSize, approachIntervals.Length) : 0;
            if (TryLayout(editor, count, direction, 0, null, false, out midspins)
                || (lead > 0 && TryLayout(editor, count, direction, lead, null, false, out midspins)))
            {
                return true;
            }
            foreach (PatternRhythmLayout.PressGroup group in ForwardPressGroups(editor, count, firstReal).Take(1))
            {
                if (TryLayout(editor, count, direction, lead, group, false, out midspins)
                    || TryLayout(editor, count, direction, lead, group, true, out midspins)
                    || (entryChanged && (TryLayout(editor, count, direction, 0, group, false, out midspins, joinAfterDouble: true)
                        || TryLayout(editor, count, direction, 0, group, true, out midspins, joinAfterDouble: true))))
                {
                    return true;
                }
            }
            return false;
        }

        private bool TryLayout(scnEditor editor, int count, float direction, int lead,
            PatternRhythmLayout.PressGroup? group, bool allowMidspin, out int[] midspins, bool joinAfterDouble = false)
        {
            int start = anchor - lead;
            int skip = approachIntervals.Length - lead;
            double[] turns = lead == 0 ? rawTurns : approachTurns.Skip(skip).Concat(rawTurns).ToArray();
            double[] times = lead == 0 ? intervals : approachIntervals.Skip(skip).Concat(intervals).ToArray();
            PatternRhythmLayout.PressGroup? shifted = group.HasValue
                ? new PatternRhythmLayout.PressGroup(group.Value.First + lead, group.Value.Last + lead, group.Value.Presses)
                : (PatternRhythmLayout.PressGroup?)null;
            return PatternRhythmLayout.TryApply(editor, start, lead, patternLength, count + lead,
                direction, turns, index => !Near(Interval(editor, start + index), times[index]), shifted, allowMidspin,
                joinAfterDouble,
                inserted => TimingMatches(editor, count, inserted, lead), out midspins);
        }

        private IEnumerable<PatternRhythmLayout.PressGroup> ForwardPressGroups(scnEditor editor, int count, int firstReal)
        {
            PressMoment[] presses = sourcePresses;
            int seam = Enumerable.Range(0, count).Where(index =>
                IsSeam(index, firstReal) && !Near(Interval(editor, anchor + index), intervals[index]))
                .DefaultIfEmpty(-1).First();
            if (seam < 0)
            {
                return Enumerable.Empty<PatternRhythmLayout.PressGroup>();
            }
            var groups = new List<KeyValuePair<int, PatternRhythmLayout.PressGroup>>();
            for (int first = 0; first < presses.Length;)
            {
                int last = first;
                double largest = 0;
                double smallest = double.PositiveInfinity;
                while (last + 1 < presses.Length)
                {
                    PressMoment left = presses[last];
                    PressMoment right = presses[last + 1];
                    double gap = right.Time - left.Time;
                    if (!left.Single || !right.Single || !right.OrdinaryGap || gap <= TimeTolerance
                        || gap > PseudoGapSeconds + TimeTolerance || right.GapBeats > 1d / 3 + TimeTolerance
                        || Math.Max(largest, gap) > BurstSeparation * Math.Min(smallest, gap))
                    {
                        break;
                    }
                    largest = Math.Max(largest, gap);
                    smallest = Math.Min(smallest, gap);
                    last++;
                }
                int total = last - first + 1;
                bool boundedStart = first > 0
                    ? presses[first].Time - presses[first - 1].Time >= largest * BurstSeparation
                    : pressContextStartsAtChartStart;
                bool boundedEnd = last + 1 < presses.Length
                    ? presses[last + 1].Time - presses[last].Time >= largest * BurstSeparation
                    : pressContextEndsAtChartEnd;
                int start = presses[first].Floor;
                int end = presses[last].Floor;
                if (total >= 2 && total <= 8 && total % 2 == 0 && boundedStart && boundedEnd
                    && start >= 1 && end - start <= BurstSearchDistance && end <= patternLength)
                {
                    if (seam < end)
                    {
                        int distance = Math.Max(0, start - seam - 1);
                        groups.Add(new KeyValuePair<int, PatternRhythmLayout.PressGroup>(distance,
                            new PatternRhythmLayout.PressGroup(start, end, total)));
                    }
                }
                first = last + 1;
            }
            return groups.OrderBy(item => item.Value.Presses == 2 ? 0 : 1).ThenBy(item => item.Key)
                .Select(item => item.Value);
        }

        private static bool IsShortTurn(double[] turns, int index)
        {
            return index >= 0 && index < turns.Length && turns[index] > 0.000001
                && turns[index] <= Math.PI / 3 + 0.000001;
        }

        private bool TimingMatches(scnEditor editor, int count)
        {
            return TimingMatches(editor, count, Array.Empty<int>(), 0);
        }

        private bool TimingMatches(scnEditor editor, int count, int[] midspins, int lead)
        {
            List<scrFloor> floors = editor.floors;
            if (lead == 0 && anchor > 0 && midspins.Length > 0 && midspins[0] == anchor)
            {
                int mappedAnchor = PatternRhythmLayout.MapFloor(anchor, midspins);
                if (!IntervalAndPressesMatch(floors, anchor - 1, mappedAnchor, precedingInterval,
                    approachInputs[approachInputs.Length - 1].PressesTo(inputs[0])))
                {
                    return false;
                }
            }
            for (int index = -lead; index <= count; index++)
            {
                int id = PatternRhythmLayout.MapFloor(anchor + index, midspins);
                InputTiming input = index < 0 ? approachInputs[approachInputs.Length + index] : inputs[index];
                if (!input.Matches(floors[id], verifyFreeRoamTiming: true))
                {
                    return false;
                }
                if (index < count)
                {
                    int next = PatternRhythmLayout.MapFloor(anchor + index + 1, midspins);
                    InputTiming nextInput = index < -1 ? approachInputs[approachInputs.Length + index + 1]
                        : inputs[index + 1];
                    double interval = index < 0 ? approachIntervals[approachIntervals.Length + index] : intervals[index];
                    double[] turns = index < 0 ? approachTurns : rawTurns;
                    int turnIndex = index < 0 ? approachTurns.Length + index : index;
                    if (IsShortTurn(turns, turnIndex)
                        && Math.Abs(RawTurn(floors[id]) - turns[turnIndex]) > ShortTurnTolerance)
                    {
                        return false;
                    }
                    if (!IntervalAndPressesMatch(floors, id, next, interval, input.PressesTo(nextInput)))
                    {
                        return false;
                    }
                }
            }
            for (int index = 0; index < midspins.Length; index++)
            {
                int id = midspins[index] + index;
                if (!editor.floors[id].midSpin || !Near(Interval(editor, id), 0))
                {
                    return false;
                }
            }
            return true;
        }

        private static bool IntervalAndPressesMatch(IList<scrFloor> floors, int from, int to,
            double interval, int presses)
        {
            double targetTime = floors[from].entryTime + interval;
            if (!Near(floors[to].entryTime, targetTime))
            {
                return false;
            }
            for (int id = from; id < to; id++)
            {
                scrFloor next = floors[id + 1];
                int manual = next.auto ? 0 : Math.Max(0, next.tapsNeeded - (floors[id].midSpin ? 1 : 0));
                if (manual == 0)
                {
                    continue;
                }
                if (!Near(next.entryTime, targetTime))
                {
                    return false;
                }
                presses -= manual;
            }
            return presses == 0;
        }

        private bool IsSeam(int index, int firstReal)
        {
            return index <= firstReal + PatternRhythmLayout.SeamSize
                || index >= patternLength - PatternRhythmLayout.SeamSize;
        }

        private bool TryMatchWithTwirls(scnEditor editor, int count, bool restrictToSeams, int firstReal)
        {
            List<int> transitions = new List<int>();
            bool previousFlip = false;
            for (int index = 0; index < count; index++)
            {
                int id = anchor + index;
                bool flip = !Near(Interval(editor, id), intervals[index]);
                if (flip && restrictToSeams && !IsSeam(index, firstReal))
                {
                    return false;
                }
                if (flip && !Near(OppositeSpinInterval(editor, id), intervals[index]))
                {
                    return false;
                }
                if (flip != previousFlip)
                {
                    transitions.Add(id);
                }
                previousFlip = flip;
            }
            if (previousFlip)
            {
                transitions.Add(anchor + count);
            }
            if (transitions.Count == 0)
            {
                return false;
            }

            Dictionary<int, List<LevelEvent>> eventsByFloor = editor.events
                .Where(evt => evt.eventType == LevelEventType.Twirl || evt.eventType == LevelEventType.FreeRoam)
                .GroupBy(evt => evt.floor).ToDictionary(group => group.Key, group => group.ToList());
            List<LevelEvent> add = new List<LevelEvent>();
            List<LevelEvent> disable = new List<LevelEvent>();
            List<LevelEvent> enable = new List<LevelEvent>();
            foreach (int id in transitions)
            {
                eventsByFloor.TryGetValue(id, out List<LevelEvent> events);
                LevelEvent twirl = events?.FirstOrDefault(evt => evt.eventType == LevelEventType.Twirl && evt.active);
                if (twirl != null)
                {
                    disable.Add(twirl);
                    continue;
                }
                if (events != null && events.Any(evt => evt.eventType == LevelEventType.FreeRoam && evt.active))
                {
                    return false;
                }
                LevelEvent inactiveTwirl = events?.FirstOrDefault(evt => evt.eventType == LevelEventType.Twirl);
                twirl = inactiveTwirl ?? new LevelEvent(id, LevelEventType.Twirl);
                if (id == 0 && !twirl.info.allowFirstFloorCheck)
                {
                    return false;
                }
                if (inactiveTwirl == null)
                {
                    add.Add(twirl);
                }
                else
                {
                    enable.Add(twirl);
                }
            }
            foreach (LevelEvent evt in disable)
            {
                evt.active = false;
            }
            foreach (LevelEvent evt in enable)
            {
                evt.active = true;
            }
            editor.events.AddRange(add);
            editor.RemakePath();
            if (TimingMatches(editor, count))
            {
                foreach (LevelEvent evt in disable)
                {
                    editor.events.Remove(evt);
                }
                return true;
            }
            foreach (LevelEvent evt in disable)
            {
                evt.active = true;
            }
            foreach (LevelEvent evt in enable)
            {
                evt.active = false;
            }
            foreach (LevelEvent evt in add)
            {
                editor.events.Remove(evt);
            }
            editor.RemakePath();
            return false;
        }

        private static double OppositeSpinInterval(scnEditor editor, int id)
        {
            scrFloor floor = editor.floors[id];
            double bpm = editor.levelData.bpm;
            bool clockwise = floor.isCCW;
            if (id == 0)
            {
                scrConductor conductor = scrConductor.instance;
                return conductor.crotchetAtStart * (conductor.adjustedCountdownTicks - 1f)
                    + scrMisc.GetTimeBetweenAngles(floor.entryangle, floor.exitangle, floor.speed, bpm, clockwise);
            }
            double shift = scrMisc.GetInverseAnglePerBeatMultiplanet(floor.numPlanets);
            double offset = floor.midSpin ? 0 : shift;
            if (editor.floors[id - 1].midSpin && floor.numPlanets > 2)
            {
                offset -= 2d * (double)(float)Math.PI + shift;
            }
            offset *= clockwise ? 1 : -1;
            double entry = floor.entryangle + offset;
            double exit = floor.exitangle + (floor.midSpin ? offset : 0);
            double angle = scrMisc.GetAngleMoved(entry, exit, clockwise);
            double beat = scrMisc.GetTimeBetweenAngles(0, (float)Math.PI, floor.speed, bpm, false);
            double turn = angle <= 0.000001 || angle >= 2d * (double)(float)Math.PI - 0.000001
                ? (floor.midSpin ? 0 : 2 * beat)
                : scrMisc.GetTimeBetweenAngles(entry, exit, floor.speed, bpm, clockwise);
            return turn + (Math.Max(floor.holdLength, 0) * 2d + floor.extraBeats) * beat;
        }

        private bool TryMatchWithSpeed(scnEditor editor, int count, bool restrictToSeams, int firstReal)
        {
            Dictionary<int, double> scales = new Dictionary<int, double>();
            for (int index = 0; index < count; index++)
            {
                int id = anchor + index;
                double actual = Interval(editor, id);
                double expected = intervals[index];
                if (Near(actual, expected))
                {
                    continue;
                }
                if (restrictToSeams && !IsSeam(index, firstReal))
                {
                    return false;
                }
                if (id == 0)
                {
                    scrConductor conductor = scrConductor.instance;
                    double countdown = conductor.crotchetAtStart * (conductor.adjustedCountdownTicks - 1f);
                    actual -= countdown;
                    expected -= countdown;
                    if (Near(actual, expected))
                    {
                        continue;
                    }
                }
                if (actual <= 0 || expected <= 0 || !PositiveFloat(actual / expected))
                {
                    return false;
                }
                scales.Add(id, actual / expected);
            }
            if (scales.Count == 0)
            {
                return InputsMatch(editor, count, verifyFreeRoamTiming: true);
            }

            Dictionary<int, List<LevelEvent>> speeds = editor.events
                .Where(evt => evt.active && evt.eventType == LevelEventType.SetSpeed)
                .GroupBy(evt => evt.floor).ToDictionary(group => group.Key,
                    group => group.OrderBy(evt => evt.GetFloat("angleOffset")).ToList());
            List<LevelEvent> additions = new List<LevelEvent>();
            List<KeyValuePair<LevelEvent, float>> adjustments = new List<KeyValuePair<LevelEvent, float>>();
            float inheritedBpm = editor.levelData.bpm;
            double previousScale = 1;
            int last = Math.Min(scales.Keys.Max() + 1, editor.floors.Count - 1);
            for (int id = 0; id <= last; id++)
            {
                double scale = scales.TryGetValue(id, out double correction) ? correction : 1;
                speeds.TryGetValue(id, out List<LevelEvent> speedEvents);
                if (scale != previousScale)
                {
                    float offset = speedEvents == null ? 0 : Math.Min(0, speedEvents[0].GetFloat("angleOffset"));
                    LevelEvent first = speedEvents?.FirstOrDefault();
                    bool setsInitialBpm = first != null && first.GetFloat("angleOffset") == offset
                        && (SpeedType)first["speedType"] == SpeedType.Bpm;
                    if (!setsInitialBpm)
                    {
                        double bpm = inheritedBpm * scale;
                        if (!PositiveFloat(bpm))
                        {
                            return false;
                        }
                        LevelEvent speed = new LevelEvent(id, LevelEventType.SetSpeed);
                        speed["speedType"] = SpeedType.Bpm;
                        speed["beatsPerMinute"] = (float)bpm;
                        speed["angleOffset"] = offset;
                        additions.Add(speed);
                    }
                }
                if (speedEvents != null)
                {
                    foreach (LevelEvent speed in speedEvents)
                    {
                        if ((SpeedType)speed["speedType"] == SpeedType.Bpm)
                        {
                            inheritedBpm = speed.GetFloat("beatsPerMinute");
                            if (scale != 1)
                            {
                                double bpm = inheritedBpm * scale;
                                if (!PositiveFloat(bpm))
                                {
                                    return false;
                                }
                                adjustments.Add(new KeyValuePair<LevelEvent, float>(speed, (float)bpm));
                            }
                        }
                        else
                        {
                            inheritedBpm *= speed.GetFloat("bpmMultiplier");
                        }
                    }
                }
                previousScale = scale;
            }
            foreach (KeyValuePair<LevelEvent, float> adjustment in adjustments)
            {
                adjustment.Key["beatsPerMinute"] = adjustment.Value;
            }
            editor.events.InsertRange(0, additions);
            editor.RemakePath();
            return TimingMatches(editor, count);
        }

        private bool InputsMatch(scnEditor editor, int count, bool verifyFreeRoamTiming)
        {
            return Enumerable.Range(0, count + 1).All(index =>
                inputs[index].Matches(editor.floors[anchor + index], verifyFreeRoamTiming));
        }

        internal static void Restore(scnEditor editor, LevelData before, int first, int last,
            bool refreshDecorations = false)
        {
            LevelData level = editor.levelData;
            level.pathData = before.pathData;
            level.angleData = before.angleData;
            level.isOldLevel = before.isOldLevel;
            level.levelEvents = before.levelEvents;
            if (refreshDecorations)
            {
                level.decorations = before.decorations;
            }
            editor.DeselectFloors();
            editor.RemakePath();
            if (refreshDecorations)
            {
                editor.UpdateDecorationObjects();
            }
            if (first == last)
            {
                editor.SelectFloor(editor.floors[first]);
            }
            else
            {
                editor.MultiSelectFloors(editor.floors[first], editor.floors[last], setSelectPoint: true);
            }
            AngleMod.NotifyCannotPreserveTiming(editor);
        }
    }
}
