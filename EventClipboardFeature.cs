using System.Collections.Generic;
using System.Linq;
using ADOFAI;
using ADOFAI.Editor.Actions;
using HarmonyLib;
using UnityEngine;
using UnityEngine.EventSystems;

namespace Angle
{
    internal enum EventSelectionGesture
    {
        Replace,
        Range,
        Toggle
    }

    internal static class EventClipboardFeature
    {
        private static readonly HashSet<LevelEvent> Selection = new HashSet<LevelEvent>();
        private static readonly System.Reflection.FieldInfo RefreshBackground = AccessTools.Field(
            typeof(scnEditor), "refreshBgSprites");
        private static readonly System.Action<scnEditor, scrFloor> FlashTile =
            AccessTools.MethodDelegate<System.Action<scnEditor, scrFloor>>(
                AccessTools.Method(typeof(scnEditor), "FlashTile"));
        private static InspectorPanel selectedPanel;
        private static LevelData selectedLevel;
        private static scrFloor selectedFloor;
        private static LevelEvent primary;
        private static LevelEvent rangeAnchor;
        private static bool explicitSelection;
        private static bool updatingInspector;
        private static scnEditor clipboardOwner;
        private static object clipboardEntry;

        internal static bool Shift => RDInput.holdingShift;
        internal static bool Control => RDInput.holdingControl;

        internal static bool HasSelectionModifier => Shift || Control;
        internal static EventSelectionGesture CurrentGesture => Shift ? EventSelectionGesture.Range
            : Control ? EventSelectionGesture.Toggle : EventSelectionGesture.Replace;

        internal static void Reset()
        {
            EventPagePicker.Reset();
            scnEditor editor = scnEditor.instance;
            if (IsTileInspector(editor, selectedPanel) && selectedFloor == editor.selectedFloors[0]
                && ReferenceEquals(selectedLevel, editor.levelData))
            {
                SelectOnly(primary);
                RefreshTabs(selectedPanel, GetTileEvents(editor));
            }
            if (OwnsClipboard(clipboardOwner))
            {
                clipboardOwner.clipboard.Clear();
                clipboardOwner.clipboardContent = scnEditor.ClipboardContent.None;
            }
            selectedPanel = null;
            selectedLevel = null;
            selectedFloor = null;
            SelectOnly(null);
            clipboardOwner = null;
            clipboardEntry = null;
        }

        private static bool IsTileInspector(scnEditor editor, InspectorPanel panel)
        {
            return editor != null && editor.SelectionIsSingle() && panel != null
                && panel == editor.levelEventsPanel;
        }

        private static List<LevelEvent> GetTileEvents(scnEditor editor)
        {
            int floor = editor.selectedFloors[0].seqID;
            return editor.events.FindAll(ev => ev.floor == floor && !ev.IsDecoration);
        }

        private static void SelectOnly(LevelEvent current)
        {
            explicitSelection = false;
            Selection.Clear();
            primary = rangeAnchor = current;
            if (current != null)
            {
                Selection.Add(current);
            }
        }

        internal static void BeforePlainClick()
        {
            selectedFloor = null;
            SelectOnly(null);
        }

        private static void SyncSelection(scnEditor editor, InspectorPanel panel, List<LevelEvent> events,
            bool followInspector = false)
        {
            LevelEvent current = panel.selectedEventType != LevelEventType.None && events.Contains(panel.selectedEvent)
                ? panel.selectedEvent : null;
            if (selectedPanel != panel || !ReferenceEquals(selectedLevel, editor.levelData)
                || selectedFloor != editor.selectedFloors[0])
            {
                selectedPanel = panel;
                selectedLevel = editor.levelData;
                selectedFloor = editor.selectedFloors[0];
                SelectOnly(current);
            }
            Selection.IntersectWith(events);
            if (followInspector && !explicitSelection && Selection.Count <= 1 && !HasSelectionModifier && current != primary)
            {
                SelectOnly(current);
            }
            if (!Selection.Contains(primary))
            {
                primary = Selection.Contains(current) ? current : events.FirstOrDefault(Selection.Contains);
            }
            if (!events.Contains(rangeAnchor))
            {
                rangeAnchor = primary;
            }
        }

        internal static void OnShowPanel(InspectorPanel panel)
        {
            scnEditor editor = scnEditor.instance;
            if (updatingInspector || editor == null || panel != editor.levelEventsPanel)
            {
                return;
            }
            if (!IsTileInspector(editor, panel))
            {
                selectedFloor = null;
                SelectOnly(null);
                EventPagePicker.Close();
                return;
            }
            List<LevelEvent> events = GetTileEvents(editor);
            SyncSelection(editor, panel, events, followInspector: true);
            if (explicitSelection || Selection.Count > 1 || (primary != null && primary != panel.selectedEvent))
            {
                RefreshTabs(panel, events);
            }
            EventPagePicker.Sync(editor);
        }

        private static void RefreshTabs(InspectorPanel panel, List<LevelEvent> events)
        {
            foreach (Transform child in panel.tabs)
            {
                InspectorTab tab = child.GetComponent<InspectorTab>();
                if (tab == null || !child.gameObject.activeSelf)
                {
                    continue;
                }
                List<LevelEvent> group = events.FindAll(ev => ev.eventType == tab.levelEventType);
                int index = group.IndexOf(primary);
                if (index < 0)
                {
                    index = group.FindIndex(Selection.Contains);
                }
                tab.SetSelected(index >= 0);
                if (index >= 0 && tab.cycleButtons != null)
                {
                    tab.eventIndex = index;
                    tab.cycleButtons.text.text = $"{index + 1}/{group.Count}";
                }
            }
        }

        internal static bool TrySelectTab(InspectorTab tab, int offset, EventSelectionGesture? gesture = null)
        {
            scnEditor editor = scnEditor.instance;
            EventSelectionGesture selection = gesture ?? CurrentGesture;
            if (selection == EventSelectionGesture.Replace || tab == null || !IsTileInspector(editor, tab.panel))
            {
                return false;
            }
            List<LevelEvent> group = editor.GetSelectedFloorEvents(tab.levelEventType);
            if (group == null || group.Count == 0)
            {
                return false;
            }
            int index = (Mathf.Clamp(tab.eventIndex, 0, group.Count - 1) + offset + group.Count) % group.Count;
            return SelectEvent(editor, group[index], selection);
        }

        internal static List<LevelEvent> GetPickerEvents(scnEditor editor, InspectorTab tab)
        {
            if (!IsTileInspector(editor, tab.panel))
            {
                return null;
            }
            List<LevelEvent> events = GetTileEvents(editor);
            SyncSelection(editor, tab.panel, events);
            return events.FindAll(ev => ev.eventType == tab.levelEventType);
        }

        internal static bool IsSelected(LevelEvent ev) => Selection.Contains(ev);
        internal static bool HasMultipleSelectedEvents(scnEditor editor)
        {
            return Selection.Count > 1 && IsTileInspector(editor, selectedPanel)
                && selectedFloor == editor.selectedFloors[0] && ReferenceEquals(selectedLevel, editor.levelData);
        }

        internal static bool TrySelectEvent(scnEditor editor, LevelEvent clicked)
        {
            EventSelectionGesture gesture = CurrentGesture;
            return gesture != EventSelectionGesture.Replace && SelectEvent(editor, clicked, gesture);
        }

        internal static bool SelectEvent(scnEditor editor, LevelEvent clicked, EventSelectionGesture gesture)
        {
            if (editor == null || !IsTileInspector(editor, editor.levelEventsPanel))
            {
                return false;
            }
            InspectorPanel panel = editor.levelEventsPanel;
            List<LevelEvent> events = GetTileEvents(editor);
            if (!events.Contains(clicked))
            {
                return false;
            }
            SyncSelection(editor, panel, events);
            List<LevelEvent> ordered = GetDisplayOrder(panel, events);
            int end = ordered.IndexOf(clicked);
            int start = ordered.IndexOf(rangeAnchor);
            if (gesture == EventSelectionGesture.Range)
            {
                if (start >= 0 && end >= 0)
                {
                    Selection.UnionWith(ordered.GetRange(System.Math.Min(start, end), System.Math.Abs(end - start) + 1));
                }
                else
                {
                    Selection.Add(clicked);
                    rangeAnchor = clicked;
                }
                primary = clicked;
            }
            else if (gesture == EventSelectionGesture.Toggle)
            {
                if (Selection.Add(clicked))
                {
                    primary = clicked;
                }
                else
                {
                    Selection.Remove(clicked);
                    if (primary == clicked)
                    {
                        primary = ordered.FirstOrDefault(Selection.Contains) ?? events.FirstOrDefault(Selection.Contains);
                    }
                }
                rangeAnchor = primary;
            }
            else
            {
                SelectOnly(clicked);
            }
            if (gesture != EventSelectionGesture.Replace)
            {
                explicitSelection = true;
            }
            if (explicitSelection && EventPagePicker.BlocksEditorPointer(editor))
            {
                RefreshTabs(panel, events);
                EventPagePicker.Sync(editor);
            }
            else
            {
                ShowSelection(editor, panel, events);
            }
            return true;
        }

        internal static void CollapseSelection(scnEditor editor)
        {
            InspectorPanel panel = editor.levelEventsPanel;
            if (IsTileInspector(editor, panel))
            {
                List<LevelEvent> events = GetTileEvents(editor);
                SyncSelection(editor, panel, events);
                SelectOnly(primary);
                RefreshTabs(panel, events);
            }
        }

        internal static EventIndicator FindIndicator(GameObject[] objects)
        {
            if (objects != null)
            {
                foreach (GameObject hit in objects)
                {
                    EventIndicator indicator = hit.transform.parent?.GetComponent<EventIndicator>();
                    if (indicator != null)
                    {
                        return indicator;
                    }
                }
            }
            return null;
        }

        private static List<LevelEvent> GetDisplayOrder(InspectorPanel panel, List<LevelEvent> events)
        {
            var ordered = new List<LevelEvent>(events.Count);
            foreach (Transform child in panel.tabs)
            {
                InspectorTab tab = child.GetComponent<InspectorTab>();
                if (tab != null && child.gameObject.activeSelf)
                {
                    ordered.AddRange(events.Where(ev => ev.eventType == tab.levelEventType));
                }
            }
            return ordered;
        }

        private static void ShowSelection(scnEditor editor, InspectorPanel panel, List<LevelEvent> events)
        {
            int cachedIndex = editor.cacheSelectedEventIndex;
            updatingInspector = true;
            editor.cacheSelectedEventIndex = 0;
            try
            {
                if (primary == null)
                {
                    panel.ShowPanel(LevelEventType.None);
                    panel.selectedEvent = null;
                    panel.ShowInspector(false);
                }
                else
                {
                    panel.ShowPanelOfEvent(primary);
                    panel.ShowInspector(true, forceAction: true);
                }
                RefreshTabs(panel, events);
            }
            finally
            {
                editor.cacheSelectedEventIndex = cachedIndex;
                updatingInspector = false;
            }
            EventPagePicker.Sync(editor);
        }

        internal static bool TryGetIndicatorSelection(EventIndicator indicator, scnEditor editor, out bool selected)
        {
            selected = Selection.Contains(indicator.evnt);
            return explicitSelection && indicator.evnt != null && selectedFloor == indicator.floor
                && IsTileInspector(editor, selectedPanel) && selectedFloor == editor.selectedFloors[0]
                && ReferenceEquals(selectedLevel, editor.levelData);
        }

        internal static bool TryCopy(scnEditor editor, bool eventsOnly)
        {
            InspectorPanel panel = editor.levelEventsPanel;
            if (!IsTileInspector(editor, panel))
            {
                return false;
            }
            List<LevelEvent> events = GetTileEvents(editor);
            if (events.Count == 0 && !eventsOnly)
            {
                return false;
            }
            SyncSelection(editor, panel, events);
            List<LevelEvent> copied = events.Where(Selection.Contains).Select(ev => ev.Copy()).ToList();
            if (copied.Count == 0)
            {
                editor.ShowNotification(AngleMod.L("请先选中要复制的事件", "Select the events to copy"));
                return true;
            }
            clipboardOwner = editor;
            clipboardEntry = new scnEditor.FloorData('R', 0f, copied, new List<LevelEvent>());
            NoteSaveFeature.ClearPatternSelection();
            editor.clipboard.Clear();
            editor.clipboard.Add(clipboardEntry);
            editor.clipboardContent = scnEditor.ClipboardContent.Floors;
            FlashTile(editor, editor.selectedFloors[0]);
            EventPagePicker.Close();
            return true;
        }

        internal static bool HasEventClipboard(scnEditor editor)
        {
            return OwnsClipboard(editor) && string.IsNullOrEmpty(NoteSaveFeature.SelectedPatternPath);
        }

        private static bool OwnsClipboard(scnEditor editor)
        {
            return editor != null && editor == clipboardOwner && editor.clipboardContent == scnEditor.ClipboardContent.Floors
                && editor.clipboard.Count == 1 && ReferenceEquals(editor.clipboard[0], clipboardEntry);
        }

        internal static bool TryPaste(scnEditor editor)
        {
            if (!HasEventClipboard(editor))
            {
                return false;
            }
            EventPagePicker.Close();
            if (editor.SelectionIsEmpty())
            {
                editor.ShowNotification(AngleMod.L("请先选中要粘贴到的轨道", "Select a tile to paste events onto"));
                return true;
            }
            var targets = editor.selectedFloors.ToDictionary(floor => floor.seqID, floor => new HashSet<LevelEventType>());
            foreach (LevelEvent existing in editor.events)
            {
                if (targets.TryGetValue(existing.floor, out HashSet<LevelEventType> types))
                {
                    types.Add(existing.eventType);
                }
            }
            var additions = new List<LevelEvent>();
            int skipped = 0;
            foreach (var target in targets)
            {
                foreach (LevelEvent copied in ((scnEditor.FloorData)clipboardEntry).levelEventData)
                {
                    if ((target.Key == 0 && !copied.info.allowFirstFloorCheck) || !CanAppend(copied.eventType, target.Value))
                    {
                        skipped++;
                        continue;
                    }
                    LevelEvent addition = copied.Copy();
                    addition.floor = target.Key;
                    additions.Add(addition);
                    target.Value.Add(addition.eventType);
                }
            }
            if (additions.Count > 0)
            {
                using (new SaveStateScope(editor, clearRedo: true))
                {
                    editor.events.AddRange(additions);
                    if (additions.Any(editor.EventHasBackgroundSprite))
                    {
                        RefreshBackground?.SetValue(editor, true);
                    }
                    editor.ApplyEventsToFloors();
                    if (editor.SelectionIsSingle())
                    {
                        InspectorPanel panel = editor.levelEventsPanel;
                        panel.ShowTabsForFloor(editor.selectedFloors[0].seqID);
                        editor.ShowEventIndicators(editor.selectedFloors[0]);
                        SelectOnly(additions[0]);
                        Selection.UnionWith(additions);
                        explicitSelection = additions.Count > 1;
                        ShowSelection(editor, panel, GetTileEvents(editor));
                    }
                }
            }
            if (skipped > 0)
            {
                editor.ShowNotification(AngleMod.L($"已跳过 {skipped} 个不能添加到目标轨道的事件",
                    $"Skipped {skipped} events that cannot be added to the target tiles"));
            }
            return true;
        }

        private static bool CanAppend(LevelEventType type, HashSet<LevelEventType> existing)
        {
            if (!EditorConstants.soloTypes.Contains(type))
            {
                return true;
            }
            return !existing.Contains(type)
                && !(type == LevelEventType.Hold && existing.Contains(LevelEventType.Pause))
                && !(type == LevelEventType.Pause && existing.Contains(LevelEventType.Hold))
                && !(type == LevelEventType.FreeRoam && existing.Contains(LevelEventType.Twirl))
                && !(type == LevelEventType.Twirl && existing.Contains(LevelEventType.FreeRoam));
        }
    }

    [HarmonyPatch(typeof(InspectorTab), nameof(InspectorTab.OnPointerClick))]
    internal static class EventClipboardTabPatch
    {
        private static bool Prefix(InspectorTab __instance, PointerEventData eventData)
        {
            if (eventData.button != PointerEventData.InputButton.Left || !__instance.panel.floorPanel)
            {
                return true;
            }
            if (EventPagePicker.IsCancellingSelectionClick)
            {
                return false;
            }
            if (EventClipboardFeature.TrySelectTab(__instance, 0))
            {
                return false;
            }
            if (!EventClipboardFeature.HasSelectionModifier)
            {
                EventClipboardFeature.BeforePlainClick();
            }
            return true;
        }
    }

    [HarmonyPatch(typeof(CycleButtons), nameof(CycleButtons.CycleEvent))]
    internal static class EventClipboardCyclePatch
    {
        private static bool Prefix(CycleButtons __instance, bool next)
        {
            if (!Input.GetMouseButtonUp(0) || __instance.panel != scnEditor.instance?.levelEventsPanel)
            {
                return true;
            }
            if (EventPagePicker.IsCancellingSelectionClick)
            {
                return false;
            }
            if (EventClipboardFeature.TrySelectTab(__instance.tab, next ? 1 : -1))
            {
                return false;
            }
            if (!EventClipboardFeature.HasSelectionModifier)
            {
                EventClipboardFeature.BeforePlainClick();
            }
            return true;
        }
    }

    [HarmonyPatch(typeof(InspectorPanel), nameof(InspectorPanel.ShowPanel))]
    internal static class EventClipboardPanelPatch
    {
        private static void Postfix(InspectorPanel __instance) => EventClipboardFeature.OnShowPanel(__instance);
    }

    [HarmonyPatch(typeof(scnEditor), "DragEventIndicatorStart")]
    internal static class EventClipboardIndicatorClickPatch
    {
        private static bool Prefix(scnEditor __instance, GameObject[] objects,
            ref EventIndicator ___draggedEvIndicator, ref bool ___cancelDrag)
        {
            if (EventPagePicker.BlocksEditorPointer(__instance)
                || (EventSystem.current != null && EventSystem.current.IsPointerOverGameObject()))
            {
                ___draggedEvIndicator = null;
                return false;
            }
            if (objects == null || RDInput.holdingAlt)
            {
                return true;
            }
            EventIndicator indicator = EventClipboardFeature.FindIndicator(objects);
            if (indicator == null)
            {
                return true;
            }
            if (EventClipboardFeature.TrySelectEvent(__instance, indicator.evnt))
            {
                ___draggedEvIndicator = indicator;
                ___cancelDrag = true;
                return false;
            }
            if (!EventClipboardFeature.HasSelectionModifier)
            {
                EventClipboardFeature.BeforePlainClick();
            }
            return true;
        }
    }

    [HarmonyPatch(typeof(EventIndicator), "LateUpdate")]
    internal static class EventClipboardIndicatorHighlightPatch
    {
        private static void Postfix(EventIndicator __instance)
        {
            scnEditor editor = scnEditor.instance;
            if (!EventClipboardFeature.TryGetIndicatorSelection(__instance, editor, out bool selected))
            {
                return;
            }
            Color color = selected ? editor.vfxIconColor : Color.black;
            color.a = __instance.evnt.active ? 1f : 0.33f;
            __instance.icon.color = color;
            int order = selected ? editor.events.Count + 1 : __instance.order;
            __instance.circle.sortingOrder = 1 + order * 2;
            __instance.icon.sortingOrder = __instance.circle.sortingOrder + 1;
        }
    }

    [HarmonyPatch(typeof(CopyFloorEditorAction), nameof(CopyFloorEditorAction.Execute))]
    internal static class EventClipboardCopyTilePatch
    {
        private static bool Prefix(scnEditor editor) => !EventClipboardFeature.TryCopy(editor, eventsOnly: false);
    }

    [HarmonyPatch(typeof(CopyEventsEditorAction), nameof(CopyEventsEditorAction.Execute))]
    internal static class EventClipboardCopyEventsPatch
    {
        private static bool Prefix(scnEditor editor) => !EventClipboardFeature.TryCopy(editor, eventsOnly: true);
    }

    [HarmonyPatch(typeof(PasteEventsEditorAction), nameof(PasteEventsEditorAction.Execute))]
    internal static class EventClipboardPasteEventsPatch
    {
        private static bool Prefix(scnEditor editor) => !EventClipboardFeature.TryPaste(editor);
    }

    [HarmonyPatch(typeof(PasteFloorWithoutDecorationsEditorAction), nameof(PasteFloorWithoutDecorationsEditorAction.Execute))]
    internal static class EventClipboardPasteWithoutDecorationsPatch
    {
        private static bool Prefix(scnEditor editor) => !EventClipboardFeature.TryPaste(editor);
    }
}
