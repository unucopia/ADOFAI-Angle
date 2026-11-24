using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using ADOFAI;
using ADOFAI.Editor;
using ADOFAI.Editor.Actions;
using HarmonyLib;
using UnityEngine;

namespace Angle
{
    internal static class PatternPasteFeature
    {
        internal sealed class PasteState
        {
            internal List<object> clipboard;
            internal bool previousBackwardPermission;
            internal IDisposable conversionScope;
            internal PatternRhythm rhythm;
            internal PatternTwirlCleanup twirlCleanup;
            internal LevelData before;
            internal int rhythmIntervalCount;
            internal int initialFloorCount;
            internal int anchor;
            internal int restoreFirst;
            internal int restoreLast;
        }

        private static readonly FieldInfo KeybindEditor =
            AccessTools.Field(typeof(EditorKeybindManager), "editor");
        private static readonly FieldInfo PresetCharacter =
            AccessTools.Field(typeof(CreateFloorWithCharOrAngleEditorAction), "chara");

        private static scnEditor sourceEditor;
        private static object[] sourceClipboard;
        private static float? referenceAngle;
        private static bool? referenceIsCCW;
        private static PatternRhythm copiedRhythm;
        private static string copiedPatternPath;
        private static DateTime copiedPatternWriteTime;
        private static float? forcedDirection;
        internal static float? ForcedDirection => forcedDirection;
        private static EditorKeybindManager tabManager;
        private static EditorAction[] pendingTabActions;
        private static bool tabUsedForChord;
        internal static bool AllowBackwardPaste { get; private set; }

        internal static void Reset()
        {
            sourceEditor = null;
            sourceClipboard = null;
            referenceAngle = null;
            referenceIsCCW = null;
            copiedRhythm = null;
            copiedPatternPath = null;
            forcedDirection = null;
            AllowBackwardPaste = false;
            ClearTabChord();
        }

        internal static void SetCopyReference(
            NotePatternFile pattern, scnEditor editor, scrFloor singleFloor = null)
        {
            if (editor == null || editor.selectedFloors == null
                || (singleFloor == null && editor.selectedFloors.Count == 0) || editor.floors == null)
            {
                return;
            }
            int previousID = Math.Max(0, (singleFloor ?? editor.selectedFloors[0]).seqID - 1);
            if (previousID >= editor.floors.Count)
            {
                return;
            }
            scrFloor previous = editor.floors[previousID];
            pattern.referenceAngle = IncomingDirection(previous);
            pattern.referenceIsCCW = previous.isCCW;
        }

        internal static void RememberCopiedSource(scnEditor editor, scrFloor singleFloor = null)
        {
            if (editor == null
                || editor.clipboardContent != scnEditor.ClipboardContent.Floors)
            {
                return;
            }
            if (editor.levelData != null && editor.levelData.isOldLevel)
            {
                // Native legacy copying reports 0 for relative path letters.
                // The generated floor's entry angle contains the real heading.
                int count = singleFloor != null ? 1 : editor.selectedFloors.Count;
                for (int index = 0; index < Math.Min(editor.clipboard.Count, count); index++)
                {
                    if (!(editor.clipboard[index] is scnEditor.FloorData))
                    {
                        continue;
                    }
                    scnEditor.FloorData data = (scnEditor.FloorData)editor.clipboard[index];
                    scrFloor floor = singleFloor ?? editor.selectedFloors[index];
                    data.floatDirection = floor.stringDirection == '!'
                        ? 999f : IncomingDirection(floor);
                    editor.clipboard[index] = data;
                }
            }
            NotePatternFile source = new NotePatternFile();
            SetCopyReference(source, editor, singleFloor);
            RememberSource(editor, source);
            int[] ids = singleFloor != null ? new[] { singleFloor.seqID }
                : editor.selectedFloors.Select(floor => floor.seqID).ToArray();
            if (ids.Length > 0 && ids.Length == editor.clipboard.Count
                && ids.Where((id, index) => id != ids[0] + index).Any() == false)
            {
                copiedRhythm = PatternRhythm.Capture(editor, ids[0], ids.Length);
            }
        }

        internal static void RememberSource(
            scnEditor editor, NotePatternFile pattern)
        {
            sourceEditor = editor;
            sourceClipboard = editor.clipboard.ToArray();
            referenceAngle = pattern.referenceAngle;
            referenceIsCCW = pattern.referenceIsCCW;
            copiedRhythm = null;
            copiedPatternPath = null;
        }

        internal static void AssociateSavedCopy(string fullPath, DateTime writeTime)
        {
            copiedPatternPath = fullPath;
            copiedPatternWriteTime = writeTime;
        }

        internal static bool IsLiveSavedCopy(scnEditor editor, string fullPath, DateTime writeTime)
        {
            return copiedPatternPath == fullPath && copiedPatternWriteTime == writeTime
                && SourceMatches(editor);
        }

        internal static PatternRhythm GetCopiedRhythm(scnEditor editor)
        {
            return AngleMod.RhythmPreservationEnabled && copiedRhythm != null && SourceMatches(editor)
                ? copiedRhythm.ForPaste(editor, editor.selectedFloors[0].seqID, forcedDirection.HasValue)
                : null;
        }

        private static bool SourceMatches(scnEditor editor)
        {
            return sourceEditor == editor && sourceClipboard != null
                && sourceClipboard.Length == editor.clipboard.Count
                && sourceClipboard.Where((item, index) =>
                    !ReferenceEquals(item, editor.clipboard[index])).Any() == false;
        }

        private static float IncomingDirection(scrFloor floor)
        {
            // Use the actual entry angle, including consecutive midspins.
            return PatternPasteGeometry.Normalize(
                270f - (float)(floor.entryangle * (180.0 / Math.PI)));
        }

        private static PatternPasteTransform CreateTransform(
            scnEditor editor, IEnumerable<float> angles,
            float? sourceReference, bool? sourceCCW)
        {
            float first = angles.FirstOrDefault(PatternPasteGeometry.IsDirection);
            scrFloor target = editor.selectedFloors[0];
            return PatternPasteGeometry.Create(
                first, sourceReference, sourceCCW,
                IncomingDirection(target), target.isCCW, forcedDirection);
        }

        internal static PatternPasteTransform TransformForPattern(
            scnEditor editor, NotePatternFile pattern)
        {
            return CreateTransform(editor,
                pattern.floors.Select(floor => floor.floatDirection),
                pattern.referenceAngle, pattern.referenceIsCCW);
        }

        internal static bool NeedsSpinCompensation(scnEditor editor,
            bool? sourceCCW, PatternPasteTransform transform, int count)
        {
            if (!AngleMod.RhythmPreservationEnabled || !forcedDirection.HasValue || count < 2)
            {
                return false;
            }
            bool targetCCW = editor.selectedFloors[0].isCCW;
            return ((sourceCCW ?? targetCCW) ^ transform.IsMirrored) != targetCCW;
        }

        private static void ToggleTwirl(
            ICollection<LevelEvent> events, int floor, bool matchFloor)
        {
            LevelEvent existing = events.FirstOrDefault(levelEvent =>
                levelEvent.eventType == LevelEventType.Twirl
                && levelEvent.active
                && (!matchFloor || levelEvent.floor == floor));
            if (existing != null)
            {
                events.Remove(existing);
            }
            else
            {
                events.Add(new LevelEvent(floor, LevelEventType.Twirl));
            }
        }

        internal static List<LevelEvent> DirectionEvents(
            List<LevelEvent> events, int index, int count, bool compensate)
        {
            if (!compensate || (index != 0 && index != count - 1))
            {
                return events;
            }
            List<LevelEvent> adjusted = events == null
                ? new List<LevelEvent>() : new List<LevelEvent>(events);
            // Invert spin for the interior, then cancel the extra inversion
            // at the last floor, keeping the unselected tail's spin unchanged.
            ToggleTwirl(adjusted, -1, false);
            return adjusted;
        }

        private static void TurnSelection(scnEditor editor, float direction)
        {
            List<int> selected = editor.selectedFloors
                .Where(floor => floor != null && floor.seqID > 0)
                .Select(floor => floor.seqID).Distinct().OrderBy(id => id).ToList();
            if (selected.Count == 0)
            {
                return;
            }
            int firstID = selected[0];
            int lastID = selected[selected.Count - 1];
            if (lastID - firstID + 1 != selected.Count)
            {
                editor.ShowNotification(AngleMod.L(
                    "请选择连续的一段轨道", "Select a continuous track section"));
                return;
            }
            List<float> original = editor.levelData.isOldLevel
                ? editor.floors.Skip(1).Select(floor =>
                    floor.stringDirection == '!' ? 999f : IncomingDirection(floor)).ToList()
                : new List<float>(editor.levelData.angleData);
            float[] selectedAngles = selected.Select(id => original[id - 1]).ToArray();
            if (!selectedAngles.Any(PatternPasteGeometry.IsDirection))
            {
                editor.ShowNotification(AngleMod.L(
                    "选区只有中旋，没有可调整的方向", "This selection contains only midspins"));
                return;
            }
            PatternPasteTransform transform = PatternPasteGeometry.Create(
                selectedAngles.First(PatternPasteGeometry.IsDirection),
                null, null, 0f, false, direction);
            if (transform.IsIdentity)
            {
                editor.ShowNotification(AngleMod.L(
                    "选区已经朝向这个方向", "The selection already faces this direction"));
                return;
            }
            foreach (int id in selected)
            {
                original[id - 1] = transform.Apply(original[id - 1]);
            }
            string legacyPath = null;
            bool keepLegacy = editor.levelData.isOldLevel
                && TryLegacyCharacters(original, out legacyPath);
            bool preserveRhythm = AngleMod.RhythmPreservationEnabled;
            PatternRhythm rhythm = preserveRhythm ? PatternRhythm.Capture(editor, firstID, selected.Count) : null;
            LevelData before = rhythm == null ? null : editor.levelData.Copy();
            bool compensateSpin = preserveRhythm && transform.IsMirrored && selected.Count > 1;
            PatternTwirlCleanup twirlCleanup = rhythm != null || compensateSpin
                ? PatternTwirlCleanup.ForSelection(editor, firstID, selected.Count) : null;
            bool reshaped = false;
            int[] midspins = Array.Empty<int>();
            using (new SaveStateScope(editor, clearRedo: true))
            {
                if (editor.levelData.isOldLevel && !keepLegacy)
                {
                    PromoteLegacyLevel(editor);
                }
                if (keepLegacy)
                {
                    editor.levelData.pathData = legacyPath;
                }
                else
                {
                    editor.levelData.angleData = original;
                }
                if (compensateSpin)
                {
                    ToggleTwirl(editor.events, firstID, true);
                    ToggleTwirl(editor.events, lastID, true);
                }
                editor.RemakePath();
                if (rhythm != null && !rhythm.Preserve(editor, PatternRhythm.ThroughExit, direction, out reshaped, out midspins))
                {
                    PatternRhythm.Restore(editor, before, firstID, lastID);
                    return;
                }
                twirlCleanup?.RemoveRedundant(editor, midspins);
                firstID = PatternRhythmLayout.MapFloor(firstID - 1, midspins) + 1;
                lastID = PatternRhythmLayout.MapFloor(lastID, midspins);
                if (firstID == lastID)
                {
                    editor.SelectFloor(editor.floors[firstID]);
                }
                else
                {
                    editor.MultiSelectFloors(editor.floors[firstID],
                        editor.floors[lastID], setSelectPoint: true);
                }
            }
            editor.ShowNotification(reshaped ? ReshapedNotice(midspins.Length > 0) : AngleMod.L(
                "选中轨道已转向；可继续按方向键调整，Ctrl+Z 撤销",
                "Selection redirected; choose another direction or Ctrl+Z to undo"));
        }

        internal static string ReshapedNotice(bool midspinAdded)
        {
            return midspinAdded ? AngleMod.L("已在附近的偶数押补中旋，节奏不变",
                "Added a midspin to a nearby even burst; rhythm unchanged")
                : AngleMod.L("已按预设方向转向，仅调整了接缝，判定时间不变",
                "Turned to the chosen direction; only the seams were adjusted to preserve timing");
        }

        internal static bool TryLegacyCharacters(
            IEnumerable<float> angles, out string path)
        {
            const string directions = "REUQLZDCBTGFJHNMpoqWxVYA!";
            List<char> characters = new List<char>();
            foreach (float angle in angles)
            {
                bool found = false;
                foreach (char character in directions)
                {
                    float candidate;
                    if (!FloorHelper.TryGetPathInDegrees(character, out candidate))
                    {
                        continue;
                    }
                    float difference = Math.Abs(
                        PatternPasteGeometry.Normalize(angle) - candidate);
                    if (angle == 999f ? candidate == 999f
                        : candidate != 999f && Math.Min(difference, 360f - difference) < 0.001f)
                    {
                        characters.Add(character);
                        found = true;
                        break;
                    }
                }
                if (!found)
                {
                    path = null;
                    return false;
                }
            }
            path = new string(characters.ToArray());
            return true;
        }

        internal static void PromoteLegacyLevel(scnEditor editor)
        {
            if (!editor.levelData.isOldLevel)
            {
                return;
            }
            List<float> angles = new List<float>();
            for (int index = 1; index < editor.floors.Count; index++)
            {
                scrFloor floor = editor.floors[index];
                angles.Add(floor.stringDirection == '!'
                    ? 999f : IncomingDirection(floor));
            }
            editor.levelData.angleData = angles;
            editor.levelData.pathData = "";
            editor.levelData.isOldLevel = false;
        }

        internal static void ResolveLegacyPatternAngles(NotePatternFile pattern)
        {
            if (pattern == null || pattern.version >= 3)
            {
                return;
            }
            float previous = pattern.referenceAngle ?? 0f;
            bool ccw = pattern.referenceIsCCW ?? false;
            foreach (NoteFloorSave floor in pattern.floors)
            {
                char direction = string.IsNullOrEmpty(floor.stringDirection)
                    ? '?' : floor.stringDirection[0];
                if ("thyj56789".IndexOf(direction) >= 0)
                {
                    double entry = (270.0 - previous) * Math.PI / 180.0;
                    double exit = FloorHelper.PathIdToRadians(direction, entry, !ccw);
                    floor.floatDirection = PatternPasteGeometry.Normalize(
                        90f - (float)(exit * 180.0 / Math.PI));
                }
                previous = floor.floatDirection == 999f
                    ? PatternPasteGeometry.Normalize(previous + 180f)
                    : floor.floatDirection;
                foreach (LevelEvent levelEvent in NoteSaveFeature.DecodeEvents(floor.levelEventData))
                {
                    if (levelEvent.eventType == LevelEventType.Twirl)
                    {
                        ccw = !ccw;
                    }
                }
            }
            pattern.version = 3;
        }

        internal static PasteState BeginPaste(scnEditor editor)
        {
            if (editor == null || editor.levelData == null
                || !editor.SelectionIsSingle()
                || editor.clipboardContent != scnEditor.ClipboardContent.Floors
                || editor.clipboard.Count == 0
                || editor.clipboard.Any(item => !(item is scnEditor.FloorData)))
            {
                return null;
            }
            bool hasSource = SourceMatches(editor);
            PatternPasteTransform transform = CreateTransform(editor,
                editor.clipboard.Cast<scnEditor.FloorData>()
                    .Select(floor => floor.floatDirection),
                hasSource ? referenceAngle : null,
                hasSource ? referenceIsCCW : null);
            PasteState state = new PasteState
            {
                clipboard = new List<object>(editor.clipboard),
                previousBackwardPermission = AllowBackwardPaste,
                initialFloorCount = editor.floors.Count,
                anchor = editor.selectedFloors[0].seqID
            };
            state.rhythm = GetCopiedRhythm(editor);
            if (state.rhythm != null)
            {
                state.before = editor.levelData.Copy();
                state.rhythmIntervalCount = state.rhythm.PasteIntervalCount(editor, state.clipboard.Count);
                state.restoreFirst = state.restoreLast = state.anchor;
                scnEditor.LevelState outerUndo = editor.changingState > 0 ? editor.undoStates.LastOrDefault() : null;
                if (outerUndo?.data != null && outerUndo.selectedFloors.Count > 1
                    && outerUndo.selectedFloors[0] == state.anchor + 1)
                {
                    state.before = outerUndo.data.Copy();
                    state.restoreFirst = outerUndo.selectedFloors[0];
                    state.restoreLast = outerUndo.selectedFloors.Last();
                }
            }
            List<object> transformed = new List<object>();
            bool compensateSpin = NeedsSpinCompensation(editor,
                hasSource ? referenceIsCCW : null, transform, state.clipboard.Count);
            if (state.rhythm != null || compensateSpin)
            {
                state.twirlCleanup = PatternTwirlCleanup.ForPaste(editor, state.anchor, state.clipboard.Count);
            }
            float[] angles = state.clipboard.Cast<scnEditor.FloorData>()
                .Select(floor => transform.Apply(floor.floatDirection)).ToArray();
            string legacyPath = null;
            bool keepLegacy = editor.levelData.isOldLevel
                && TryLegacyCharacters(angles, out legacyPath);
            for (int index = 0; index < state.clipboard.Count; index++)
            {
                scnEditor.FloorData floor = (scnEditor.FloorData)state.clipboard[index];
                state.twirlCleanup?.RememberCopied(state.anchor + index + 1, floor.levelEventData);
                transformed.Add(new scnEditor.FloorData(
                    keepLegacy ? legacyPath[index] : floor.stringDirection,
                    angles[index],
                    DirectionEvents(floor.levelEventData, index,
                        state.clipboard.Count, compensateSpin), floor.attachedDecorations));
            }
            try
            {
                if (editor.levelData.isOldLevel && !keepLegacy)
                {
                    // Enclose conversion and native paste in one undo step.
                    state.conversionScope = new SaveStateScope(editor);
                    PromoteLegacyLevel(editor);
                }
                editor.clipboard.Clear();
                editor.clipboard.AddRange(transformed);
                AllowBackwardPaste = true;
                return state;
            }
            catch
            {
                EndPaste(editor, state);
                throw;
            }
        }

        internal static void EndPaste(scnEditor editor, PasteState state)
        {
            if (state == null)
            {
                return;
            }
            AllowBackwardPaste = state.previousBackwardPermission;
            editor.clipboard.Clear();
            editor.clipboard.AddRange(state.clipboard);
            if (state.conversionScope != null)
            {
                state.conversionScope.Dispose();
                state.conversionScope = null;
            }
        }

        internal static void PreservePastedRhythm(scnEditor editor, PasteState state)
        {
            if (state == null || (state.rhythm == null && state.twirlCleanup == null)
                || editor.floors.Count != state.initialFloorCount + state.clipboard.Count)
            {
                return;
            }
            using (new SaveStateScope(editor, skipSaving: true))
            {
                int selected = state.anchor + state.clipboard.Count;
                bool reshaped = false;
                int[] midspins = Array.Empty<int>();
                if (state.rhythm != null && !state.rhythm.Preserve(editor, state.rhythmIntervalCount, forcedDirection,
                    out reshaped, out midspins))
                {
                    PatternRhythm.Restore(editor, state.before, state.restoreFirst, state.restoreLast,
                        refreshDecorations: true);
                    return;
                }
                state.twirlCleanup?.RemoveRedundant(editor, midspins);
                editor.SelectFloor(editor.floors[PatternRhythmLayout.MapFloor(selected, midspins)]);
                if (reshaped)
                {
                    editor.ShowNotification(ReshapedNotice(midspins.Length > 0));
                }
            }
        }

        private static void ClearTabChord()
        {
            tabManager = null;
            pendingTabActions = null;
            tabUsedForChord = false;
        }

        internal static bool TryHandleDirectionShortcut(EditorKeybindManager manager)
        {
            if (AngleMod.capturingHotkey
                || RDInput.holdingControl || RDInput.holdingAlt)
            {
                ClearTabChord();
                return false;
            }
            scnEditor editor = (scnEditor)KeybindEditor.GetValue(manager);
            if (editor == null || editor.playMode || editor.levelData == null
                || editor.userIsEditingAnInputField)
            {
                ClearTabChord();
                return false;
            }
            if (tabManager != null && tabManager != manager)
            {
                ClearTabChord();
            }
            if (!Input.GetKey(KeyCode.Tab) && pendingTabActions != null)
            {
                EditorAction[] actions = pendingTabActions;
                bool singleTap = !tabUsedForChord && Input.GetKeyUp(KeyCode.Tab);
                ClearTabChord();
                if (singleTap)
                {
                    foreach (EditorAction action in actions)
                    {
                        action.Execute(editor);
                    }
                    return true;
                }
            }
            KeyModifier presetModifiers = RDInput.holdingShift
                ? KeyModifier.Shift : KeyModifier.None;
            if (Input.GetKey(KeyCode.BackQuote))
            {
                presetModifiers |= KeyModifier.BackQuote;
            }
            if (Input.GetKeyDown(KeyCode.Tab))
            {
                // Delay the native Tab action until release. A directional
                // chord consumes it, so pasting never inserts an extra midspin.
                pendingTabActions = manager
                    .Where(entry => entry.Key.key == KeyCode.Tab
                        && entry.Key.modifierMask == presetModifiers)
                    .SelectMany(entry => entry.Value).ToArray();
                tabManager = manager;
                tabUsedForChord = false;
            }
            if (!Input.GetKey(KeyCode.Tab) || pendingTabActions == null)
            {
                return false;
            }
            // Inspect the native actions so 15/30/45-degree variants always
            // follow the game's mapping instead of a separate hard-coded map.
            foreach (KeyValuePair<EditorKeybind, List<EditorAction>> entry in manager)
            {
                if (entry.Key.modifierMask != presetModifiers
                    || !Input.GetKeyDown(entry.Key.key))
                {
                    continue;
                }
                foreach (CreateFloorWithCharOrAngleEditorAction action in
                    entry.Value.OfType<CreateFloorWithCharOrAngleEditorAction>())
                {
                    float angle;
                    if (FloorHelper.TryGetPathInDegrees(
                            (char)PresetCharacter.GetValue(action), out angle)
                        && PatternPasteGeometry.IsDirection(angle))
                    {
                        tabUsedForChord = true;
                        PasteInDirection(editor, angle);
                        return true;
                    }
                }
            }
            if (Input.GetKeyDown(KeyCode.Tab))
            {
                return true;
            }
            if (Input.anyKeyDown)
            {
                tabUsedForChord = true;
            }
            return false;
        }

        private static void PasteInDirection(scnEditor editor, float angle)
        {
            if (editor.SelectionIsEmpty())
            {
                editor.ShowNotification(AngleMod.L(
                    "请先选择粘贴位置", "Select a destination floor first"));
                return;
            }
            if (editor.selectedFloors.Count > 1)
            {
                TurnSelection(editor, angle);
                return;
            }
            float? previousDirection = forcedDirection;
            forcedDirection = angle;
            try
            {
                if (NoteSaveFeature.OverwritePasteEnabled
                    && NoteSaveFeature.TryOverwritePaste(editor))
                {
                    return;
                }
                NoteSaveFeature.PrepareSavedPatternForPaste(editor);
                if (EventClipboardFeature.HasEventClipboard(editor))
                {
                    editor.ShowNotification(AngleMod.L(
                        "当前复制的是事件，请按 Ctrl+V 粘贴",
                        "The clipboard contains events. Press Ctrl+V to paste them."));
                    return;
                }
                if (editor.clipboardContent != scnEditor.ClipboardContent.Floors
                    || editor.clipboard.Count == 0)
                {
                    editor.ShowNotification(AngleMod.L(
                        "请先复制或选择一个键型", "Copy or select a pattern first"));
                    return;
                }
                if (editor.clipboard.Count == 1 && editor.SelectionIsSingle())
                {
                    // Ctrl+V keeps native event-only paste for a single copied
                    // floor; the directional shortcut explicitly inserts it.
                    editor.PasteFloors();
                }
                else
                {
                    new PasteFloorEditorAction().Execute(editor);
                }
            }
            finally
            {
                forcedDirection = previousDirection;
            }
        }
    }

    [HarmonyPatch(typeof(scnEditor), "PasteFloors")]
    internal static class PatternPasteFloorsPatch
    {
        private static void Prefix(
            scnEditor __instance, out PatternPasteFeature.PasteState __state)
        {
            __state = PatternPasteFeature.BeginPaste(__instance);
        }

        private static void Postfix(scnEditor __instance, PatternPasteFeature.PasteState __state)
        {
            PatternPasteFeature.PreservePastedRhythm(__instance, __state);
        }

        private static Exception Finalizer(scnEditor __instance,
            PatternPasteFeature.PasteState __state, Exception __exception)
        {
            PatternPasteFeature.EndPaste(__instance, __state);
            return __exception;
        }
    }

    [HarmonyPatch]
    internal static class PatternPasteBackwardCheckPatch
    {
        private static IEnumerable<MethodBase> TargetMethods()
        {
            yield return AccessTools.Method(typeof(scnEditor), "FloorPointsBackwards", new[] { typeof(float) });
            yield return AccessTools.Method(typeof(scnEditor), "FloorPointsBackwards", new[] { typeof(char) });
        }

        private static bool Prefix(ref bool __result)
        {
            if (!PatternPasteFeature.AllowBackwardPaste)
            {
                return true;
            }
            // Only the active paste may bypass the native guard. Ordinary
            // direction keys must retain their original delete-floor behavior.
            __result = false;
            return false;
        }
    }

    [HarmonyPatch]
    internal static class PatternPasteNewClipboardPatch
    {
        private static IEnumerable<MethodBase> TargetMethods()
        {
            yield return AccessTools.Method(typeof(scnEditor), "CopyFloor");
            yield return AccessTools.Method(typeof(scnEditor), "CopyDecoration");
        }

        private static void Prefix(bool clearClipboard)
        {
            if (clearClipboard)
            {
                NoteSaveFeature.ClearPatternSelection();
            }
        }

        private static void Postfix(scnEditor __instance, MethodBase __originalMethod, object[] __args, bool clearClipboard)
        {
            if (clearClipboard && __originalMethod.Name == "CopyFloor")
            {
                PatternPasteFeature.RememberCopiedSource(__instance, (scrFloor)__args[0]);
            }
        }
    }
}
