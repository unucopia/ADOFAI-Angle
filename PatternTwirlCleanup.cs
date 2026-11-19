using System;
using System.Collections.Generic;
using System.Linq;
using ADOFAI;

namespace Angle
{
    internal sealed class PatternTwirlCleanup
    {
        private const double Pi = (float)Math.PI;
        private const double AngleTolerance = 0.000001;
        private const double TimeTolerance = 0.00001;
        private readonly int first;
        private readonly int last;
        private readonly Dictionary<int, int> authored = new Dictionary<int, int>();

        private PatternTwirlCleanup(int first, int length)
        {
            this.first = first;
            last = first + length - 1;
        }

        internal static PatternTwirlCleanup ForSelection(scnEditor editor, int first, int length)
        {
            var cleanup = new PatternTwirlCleanup(first, length);
            cleanup.RememberExisting(editor.events, insertedCount: 0, overwrite: false);
            return cleanup;
        }

        internal static PatternTwirlCleanup ForPaste(scnEditor editor, int anchor, int length, bool overwrite = false)
        {
            var cleanup = new PatternTwirlCleanup(anchor + 1, length);
            cleanup.RememberExisting(editor.events, overwrite ? 0 : length, overwrite);
            return cleanup;
        }

        private void RememberExisting(IEnumerable<LevelEvent> events, int insertedCount, bool overwrite)
        {
            foreach (LevelEvent evt in events)
            {
                if (evt.eventType == LevelEventType.Twirl && (!overwrite || evt.floor < first || evt.floor > last))
                {
                    AddAuthored(evt.floor < first ? evt.floor : evt.floor + insertedCount, 1);
                }
            }
        }

        internal void RememberCopied(int floor, IEnumerable<LevelEvent> events)
        {
            if (events != null)
            {
                AddAuthored(floor, events.Count(evt => evt.eventType == LevelEventType.Twirl));
            }
        }

        private void AddAuthored(int floor, int count)
        {
            if (count > 0)
            {
                authored.TryGetValue(floor, out int previous);
                authored[floor] = previous + count;
            }
        }

        private static bool IsActiveTwirl(LevelEvent evt)
        {
            return evt.active && evt.eventType == LevelEventType.Twirl;
        }

        internal void RemoveRedundant(scnEditor editor, int[] midspins)
        {
            int firstEdited = PatternRhythmLayout.MapFloor(first, midspins);
            int lastEdited = PatternRhythmLayout.MapFloor(last, midspins);
            var originalCounts = authored.ToDictionary(
                pair => PatternRhythmLayout.MapFloor(pair.Key, midspins), pair => pair.Value);
            var rotations = editor.events.Where(IsActiveTwirl).GroupBy(evt => evt.floor)
                .OrderBy(group => group.Key).ToArray();
            int start = firstEdited;
            int end = lastEdited;
            bool hasGenerated = false;
            foreach (var group in rotations)
            {
                if (group.Count() > AuthoredCount(originalCounts, group.Key))
                {
                    hasGenerated = true;
                    start = Math.Min(start, group.Key);
                    end = Math.Max(end, group.Key);
                }
            }
            if (!hasGenerated)
            {
                return;
            }

            var barriers = new HashSet<int>(editor.events
                .Where(evt => evt.active && evt.eventType != LevelEventType.Twirl).Select(evt => evt.floor));
            var run = new List<(LevelEvent Twirl, bool Generated)>();
            var remove = new HashSet<LevelEvent>();
            int previousFloor = -1;
            foreach (var group in rotations)
            {
                int floor = group.Key;
                if (floor < start || floor > end)
                {
                    continue;
                }
                int manual = AuthoredCount(originalCounts, floor);
                if (group.Any(evt => evt.locked) || (manual > 0 && (floor < firstEdited || floor > lastEdited)))
                {
                    MarkRedundant(run, remove);
                    run.Clear();
                    previousFloor = -1;
                    continue;
                }
                if (previousFloor >= 0 && !CanCancelBetween(editor.floors, barriers, previousFloor, floor))
                {
                    MarkRedundant(run, remove);
                    run.Clear();
                }
                int index = 0;
                foreach (LevelEvent evt in group)
                {
                    run.Add((evt, index++ >= manual));
                }
                previousFloor = floor;
            }
            MarkRedundant(run, remove);
            if (remove.Count > 0)
            {
                ApplyAndVerify(editor, remove);
            }
        }

        private static int AuthoredCount(Dictionary<int, int> counts, int floor)
        {
            return counts.TryGetValue(floor, out int count) ? count : 0;
        }

        private static bool CanCancelBetween(IList<scrFloor> floors, HashSet<int> barriers, int first, int last)
        {
            for (int id = first; id < last; id++)
            {
                scrFloor floor = floors[id];
                if (id == 0 || barriers.Contains(id) || floor.midSpin || floor.numPlanets != 2
                    || floor.holdLength >= 0 || floor.freeroam)
                {
                    return false;
                }
                double turn = scrMisc.GetAngleMoved(floor.entryangle, floor.exitangle, !floor.isCCW);
                if (!(Math.Abs(turn - Pi) <= AngleTolerance || turn <= AngleTolerance || turn >= 2 * Pi - AngleTolerance))
                {
                    return false;
                }
            }
            return true;
        }

        private static void MarkRedundant(List<(LevelEvent Twirl, bool Generated)> run, HashSet<LevelEvent> remove)
        {
            if (run.Count < 2)
            {
                return;
            }
            int generated = run.Count(entry => entry.Generated);
            if (generated == 0)
            {
                return;
            }
            LevelEvent pairedWithAuthored = null;
            if (generated % 2 == 1)
            {
                LevelEvent previousGenerated = null;
                LevelEvent previousAuthored = null;
                LevelEvent authoredPartner = null;
                int distance = int.MaxValue;
                foreach (var entry in run)
                {
                    LevelEvent other = entry.Generated ? previousAuthored : previousGenerated;
                    if (other != null && entry.Twirl.floor - other.floor < distance)
                    {
                        distance = entry.Twirl.floor - other.floor;
                        pairedWithAuthored = entry.Generated ? entry.Twirl : other;
                        authoredPartner = entry.Generated ? other : entry.Twirl;
                    }
                    if (entry.Generated)
                    {
                        previousGenerated = entry.Twirl;
                    }
                    else
                    {
                        previousAuthored = entry.Twirl;
                    }
                }
                if (authoredPartner != null)
                {
                    remove.Add(authoredPartner);
                    remove.Add(pairedWithAuthored);
                }
            }

            LevelEvent pending = null;
            foreach (var entry in run)
            {
                if (!entry.Generated || ReferenceEquals(entry.Twirl, pairedWithAuthored))
                {
                    continue;
                }
                if (pending == null)
                {
                    pending = entry.Twirl;
                }
                else
                {
                    remove.Add(pending);
                    remove.Add(entry.Twirl);
                    pending = null;
                }
            }
        }

        private static void ApplyAndVerify(scnEditor editor, HashSet<LevelEvent> remove)
        {
            var timeline = editor.floors.Select(floor =>
                (floor.entryTime, floor.entryangle, floor.exitangle, floor.speed, floor.tapsNeeded, floor.midSpin)).ToArray();
            LevelEvent[] events = editor.events.ToArray();
            bool keep = false;
            editor.events.RemoveAll(remove.Contains);
            try
            {
                editor.RemakePath();
                keep = editor.floors.Count == timeline.Length;
                for (int index = 0; keep && index < timeline.Length; index++)
                {
                    scrFloor floor = editor.floors[index];
                    var before = timeline[index];
                    keep = Math.Abs(floor.entryTime - before.entryTime) <= TimeTolerance
                        && Math.Abs(floor.entryangle - before.entryangle) <= AngleTolerance
                        && Math.Abs(floor.exitangle - before.exitangle) <= AngleTolerance
                        && floor.speed == before.speed && floor.midSpin == before.midSpin
                        && floor.tapsNeeded == before.tapsNeeded;
                }
            }
            finally
            {
                if (!keep)
                {
                    editor.events.Clear();
                    editor.events.AddRange(events);
                    editor.RemakePath();
                }
            }
        }
    }
}
