using System.Collections.Generic;
using System.Linq;
using ADOFAI;
using HarmonyLib;
using TMPro;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;

namespace Angle
{
    internal static class EventPagePicker
    {
        private const float RowHeight = 30f;
        private const float RowSpacing = 5f;
        private const float Padding = 20f;
        private const float Width = 280f;
        private const int VisibleRows = 8;
        private static readonly List<EventPageCounterHit> Counters = new List<EventPageCounterHit>();
        private static readonly System.Func<scnEditor, GameObject[]> ObjectsAtMouse =
            AccessTools.MethodDelegate<System.Func<scnEditor, GameObject[]>>(
                AccessTools.Method(typeof(scnEditor), "ObjectsAtMouse"));
        private static Canvas popupCanvas;
        private static scnEditor owner;
        private static scrFloor sourceFloor;
        private static LevelData sourceLevel;
        private static InspectorTab sourceTab;
        private static List<LevelEvent> sourceEvents;
        private static System.Action refreshRows;
        private static RectTransform menu;
        private static ScrollRect scroll;
        private static List<Button> eventButtons;
        private static bool pointerPressed;
        private static bool plainPress;
        private static bool cancellingSelection;
        private static Vector2 pressPosition;
        private static EventSelectionGesture pressGesture;
        private static Button pressedButton;

        internal static void Bind(InspectorTab tab)
        {
            CycleButtons cycle = tab.cycleButtons;
            if (cycle == null || !cycle.gameObject.activeSelf || cycle.text == null
                || scnEditor.instance == null || tab.panel != scnEditor.instance.levelEventsPanel
                || cycle.text.GetComponentInChildren<EventPageCounterHit>(true) != null)
            {
                return;
            }
            Counters.RemoveAll(counter => counter == null);
            Image hit = AddImage(cycle.text.rectTransform, "Angle event page hit area", Color.clear);
            Stretch(hit.rectTransform);
            hit.canvasRenderer.cullTransparentMesh = false;
            EventPageCounterHit counter = hit.gameObject.AddComponent<EventPageCounterHit>();
            counter.Tab = tab;
            Counters.Add(counter);
        }

        internal static void Reset()
        {
            Close();
            foreach (EventPageCounterHit counter in Counters)
            {
                if (counter != null)
                {
                    counter.gameObject.SetActive(false);
                    Object.Destroy(counter.gameObject);
                }
            }
            Counters.Clear();
        }

        internal static void Close()
        {
            if (popupCanvas != null)
            {
                popupCanvas.gameObject.SetActive(false);
                Object.Destroy(popupCanvas.gameObject);
            }
            popupCanvas = null;
            owner = null;
            sourceFloor = null;
            sourceLevel = null;
            sourceTab = null;
            sourceEvents = null;
            refreshRows = null;
            menu = null;
            scroll = null;
            eventButtons = null;
            pointerPressed = false;
            cancellingSelection = false;
            pressedButton = null;
        }

        internal static bool BlocksEditorPointer(scnEditor editor) => editor == owner && popupCanvas != null;

        internal static bool IsCancellingSelectionClick => cancellingSelection
            && (Input.GetMouseButton(0) || Input.GetMouseButtonUp(0));

        private static void SelectOutsideEvent(Vector2 position)
        {
            var hits = new List<RaycastResult>();
            if (EventSystem.current != null)
            {
                EventSystem.current.RaycastAll(new PointerEventData(EventSystem.current) { position = position }, hits);
            }
            foreach (RaycastResult hit in hits)
            {
                Transform target = hit.gameObject.transform;
                if (target.IsChildOf(popupCanvas.transform))
                {
                    continue;
                }
                InspectorTab tab = target.GetComponentInParent<InspectorTab>();
                if (tab != null && tab.panel == owner.levelEventsPanel)
                {
                    CycleButtons cycle = tab.cycleButtons;
                    int offset = 0;
                    if (cycle != null && cycle.prevButton != null && target.IsChildOf(cycle.prevButton.transform))
                    {
                        offset = -1;
                    }
                    else if (cycle != null && cycle.nextButton != null && target.IsChildOf(cycle.nextButton.transform))
                    {
                        offset = 1;
                    }
                    EventClipboardFeature.TrySelectTab(tab, offset, pressGesture);
                }
                return;
            }
            EventIndicator indicator = EventClipboardFeature.FindIndicator(ObjectsAtMouse(owner));
            if (indicator != null)
            {
                EventClipboardFeature.SelectEvent(owner, indicator.evnt, pressGesture);
            }
        }

        internal static void Sync(scnEditor editor)
        {
            if (popupCanvas == null || editor != owner)
            {
                return;
            }
            if (!IsCurrentFloor(editor) || sourceTab == null
                || !sourceEvents.SequenceEqual(editor.GetSelectedFloorEvents(sourceTab.levelEventType)))
            {
                Close();
                return;
            }
            refreshRows?.Invoke();
        }

        internal static bool HandleMouse(scnEditor editor)
        {
            if (!BlocksEditorPointer(editor))
            {
                if (Input.GetMouseButtonDown(0))
                {
                    cancellingSelection = !EventClipboardFeature.HasSelectionModifier && !RDInput.holdingAlt
                        && EventClipboardFeature.HasMultipleSelectedEvents(editor);
                    if (cancellingSelection)
                    {
                        EventClipboardFeature.CollapseSelection(editor);
                    }
                }
                return IsCancellingSelectionClick;
            }
            if (!IsCurrentFloor(editor) || editor.playMode || IsNativePopupOpen(editor))
            {
                Close();
                return false;
            }

            Vector2 position = Input.mousePosition;
            if (Input.GetMouseButtonDown(0))
            {
                pointerPressed = true;
                pressPosition = position;
                pressGesture = EventClipboardFeature.CurrentGesture;
                plainPress = pressGesture == EventSelectionGesture.Replace && !RDInput.holdingAlt;
                pressedButton = ButtonAt(position);
            }
            if (pointerPressed && Input.GetMouseButtonUp(0))
            {
                pointerPressed = false;
                float dragThreshold = EventSystem.current != null ? EventSystem.current.pixelDragThreshold : 5f;
                if ((position - pressPosition).sqrMagnitude <= dragThreshold * dragThreshold)
                {
                    CompleteClick(position);
                }
            }
            if (popupCanvas != null && Contains(scroll.viewport, position))
            {
                float distance = scroll.content.rect.height - scroll.viewport.rect.height;
                float wheel = Input.mouseScrollDelta.y;
                if (distance > 0f && wheel != 0f)
                {
                    scroll.verticalNormalizedPosition = Mathf.Clamp01(scroll.verticalNormalizedPosition
                        + wheel * (RowHeight + RowSpacing) / distance);
                }
            }
            return true;
        }

        private static void CompleteClick(Vector2 position)
        {
            if (plainPress && EventClipboardFeature.HasMultipleSelectedEvents(owner))
            {
                Close();
                return;
            }
            if (pressedButton != null && pressedButton == ButtonAt(position))
            {
                if (plainPress || pressGesture != EventSelectionGesture.Replace)
                {
                    int index = eventButtons.IndexOf(pressedButton);
                    EventClipboardFeature.SelectEvent(owner, sourceEvents[index], pressGesture);
                }
                return;
            }
            if (Contains(menu, pressPosition) || Contains(menu, position))
            {
                return;
            }
            if (plainPress)
            {
                Close();
            }
            else if (pressGesture != EventSelectionGesture.Replace)
            {
                SelectOutsideEvent(position);
            }
        }

        private static Button ButtonAt(Vector2 position)
        {
            return Contains(scroll.viewport, position)
                ? eventButtons.Find(button => Contains((RectTransform)button.transform, position)) : null;
        }

        private static bool Contains(RectTransform target, Vector2 screenPosition)
        {
            Canvas canvas = popupCanvas.rootCanvas;
            Camera eventCamera = canvas.renderMode == RenderMode.ScreenSpaceOverlay ? null : canvas.worldCamera;
            return RectTransformUtility.RectangleContainsScreenPoint(target, screenPosition, eventCamera);
        }

        private static bool IsNativePopupOpen(scnEditor editor) => editor.popupBlocker != null
            && editor.popupBlocker.gameObject.activeSelf;

        internal static bool DismissForEscape(scnEditor editor)
        {
            if (popupCanvas == null || editor != owner)
            {
                return false;
            }
            bool nativePopup = IsNativePopupOpen(editor);
            bool escape = !nativePopup && Input.GetKeyDown(KeyCode.Escape);
            if (!IsCurrentFloor(editor) || editor.playMode || nativePopup || escape)
            {
                Close();
            }
            return escape;
        }

        internal static void Open(InspectorTab tab)
        {
            scnEditor editor = scnEditor.instance;
            if (editor == null || editor.playMode || editor.levelEditorCanvas == null
                || editor.fileActionsPanel == null || editor.buttonNew == null || IsNativePopupOpen(editor))
            {
                return;
            }
            Transform background = editor.fileActionsPanel.transform.Find("background");
            Image panelTemplate = background != null ? background.GetComponent<Image>() : null;
            Button template = editor.buttonNew;
            TMP_Text templateLabel = template.GetComponentInChildren<TMP_Text>(true);
            if (panelTemplate == null || templateLabel == null)
            {
                return;
            }
            List<LevelEvent> events = EventClipboardFeature.GetPickerEvents(editor, tab);
            if (events == null || events.Count < 2)
            {
                return;
            }
            Close();
            owner = editor;
            sourceFloor = editor.selectedFloors[0];
            sourceLevel = editor.levelData;
            sourceTab = tab;
            sourceEvents = events;

            RectTransform host = (RectTransform)editor.levelEditorCanvas.transform;
            Image clickShield = AddImage(host, "Angle event pages", Color.clear);
            clickShield.gameObject.SetActive(false);
            clickShield.canvasRenderer.cullTransparentMesh = false;
            Stretch(clickShield.rectTransform);
            popupCanvas = clickShield.gameObject.AddComponent<Canvas>();
            popupCanvas.overrideSorting = true;
            Canvas tabCanvas = tab.cycleButtons.text.canvas ?? editor.levelEditorCanvas;
            popupCanvas.sortingLayerID = tabCanvas.sortingLayerID;
            popupCanvas.sortingOrder = Mathf.Max(tabCanvas.sortingOrder, editor.levelEditorCanvas.sortingOrder) + 1;
            popupCanvas.gameObject.AddComponent<GraphicRaycaster>();
            menu = new GameObject("Event list", typeof(RectTransform)).GetComponent<RectTransform>();
            menu.SetParent(clickShield.rectTransform, false);

            float stride = RowHeight + RowSpacing;
            float viewportHeight = Mathf.Min(events.Count, VisibleRows) * stride - RowSpacing;
            float panelHeight = viewportHeight + Padding * 2f;
            menu.anchorMin = menu.anchorMax = host.pivot;
            menu.pivot = new Vector2(0f, 1f);
            menu.sizeDelta = new Vector2(Width, panelHeight);
            Vector3 anchor = tab.cycleButtons.text.rectTransform.TransformPoint(tab.cycleButtons.text.textBounds.center);
            Vector3 local = host.InverseTransformPoint(anchor);
            Rect area = host.rect;
            menu.anchoredPosition = new Vector2(
                Mathf.Clamp(local.x - Width - 36f, area.xMin, area.xMax - Width),
                Mathf.Clamp(local.y + RowHeight / 2f, area.yMin + panelHeight, area.yMax));
            AddFrameHalf(panelTemplate, menu, panelHeight, top: false);
            AddFrameHalf(panelTemplate, menu, panelHeight, top: true);

            Image viewport = AddImage(menu, "Event list viewport", Color.clear);
            viewport.canvasRenderer.cullTransparentMesh = false;
            PlaceRow(viewport.rectTransform, Padding, viewportHeight, Padding);
            viewport.gameObject.AddComponent<RectMask2D>();
            var content = new GameObject("Events", typeof(RectTransform)).GetComponent<RectTransform>();
            content.SetParent(viewport.rectTransform, false);
            PlaceRow(content, 0f, events.Count * stride - RowSpacing, 0f);
            scroll = viewport.gameObject.AddComponent<ScrollRect>();
            scroll.viewport = viewport.rectTransform;
            scroll.content = content;
            scroll.horizontal = false;
            scroll.movementType = ScrollRect.MovementType.Clamped;
            scroll.scrollSensitivity = 0f;

            ColorBlock nativeColors = template.colors;
            eventButtons = new List<Button>(events.Count);
            void RefreshSelection()
            {
                for (int i = 0; i < eventButtons.Count; i++)
                {
                    ColorBlock colors = nativeColors;
                    Color tint = EventClipboardFeature.IsSelected(events[i]) ? nativeColors.normalColor : nativeColors.pressedColor;
                    colors.normalColor = colors.highlightedColor = colors.selectedColor = tint;
                    eventButtons[i].colors = colors;
                }
            }

            for (int i = 0; i < events.Count; i++)
            {
                eventButtons.Add(AddMenuButton(template, content, i * stride, 0f,
                    DescribeEvent(events[i], i + 1, events.Count)));
            }
            refreshRows = RefreshSelection;
            refreshRows();
            clickShield.gameObject.SetActive(true);
            int overflow = events.Count - VisibleRows;
            if (overflow > 0)
            {
                Canvas.ForceUpdateCanvases();
                scroll.verticalNormalizedPosition = 1f - Mathf.Clamp01((tab.eventIndex - VisibleRows + 1f) / overflow);
            }
        }

        private static bool IsCurrentFloor(scnEditor editor)
        {
            return editor != null && editor == owner && ReferenceEquals(editor.levelData, sourceLevel)
                && editor.SelectionIsSingle() && editor.selectedFloors[0] == sourceFloor;
        }

        private static string DescribeEvent(LevelEvent ev, int index, int count)
        {
            var parts = new List<string> { $"{index}/{count}" };
            string sound = ev.GetString("hitsound");
            string tag = ev.GetString("eventTag");
            if (!string.IsNullOrWhiteSpace(sound))
            {
                parts.Add(sound);
            }
            if (!string.IsNullOrWhiteSpace(tag))
            {
                parts.Add(tag);
            }
            return string.Join("   ", parts);
        }

        private static Image AddImage(RectTransform parent, string name, Color color)
        {
            Image image = new GameObject(name, typeof(RectTransform), typeof(Image)).GetComponent<Image>();
            image.gameObject.layer = parent.gameObject.layer;
            image.rectTransform.SetParent(parent, false);
            image.color = color;
            return image;
        }

        private static void AddFrameHalf(Image template, RectTransform parent, float height, bool top)
        {
            var clip = new GameObject(top ? "Upper menu border" : "Lower menu border", typeof(RectTransform),
                typeof(RectMask2D));
            clip.layer = template.gameObject.layer;
            var clipRect = (RectTransform)clip.transform;
            clipRect.SetParent(parent, false);
            clipRect.anchorMin = new Vector2(0f, top ? 0.5f : 0f);
            clipRect.anchorMax = new Vector2(1f, top ? 1f : 0.5f);
            clipRect.offsetMin = clipRect.offsetMax = Vector2.zero;
            Image frame = Object.Instantiate(template, clipRect, false);
            RectTransform frameRect = frame.rectTransform;
            frameRect.pivot = new Vector2(0.5f, 0.5f);
            Stretch(frameRect);
            float halfHeight = height / 2f;
            frameRect.offsetMin = new Vector2(0f, top ? -halfHeight : 0f);
            frameRect.offsetMax = new Vector2(0f, top ? 0f : halfHeight);
            if (top)
            {
                Vector3 scale = frameRect.localScale;
                scale.y = -scale.y;
                frameRect.localScale = scale;
            }
            frame.gameObject.SetActive(true);
        }

        private static Button AddMenuButton(Button template, RectTransform parent, float top, float padding, string text)
        {
            Button button = Object.Instantiate(template, parent, false);
            button.name = "Event list button";
            PlaceRow((RectTransform)button.transform, top, RowHeight, padding);
            button.onClick = new Button.ButtonClickedEvent();
            TMP_Text label = button.GetComponentInChildren<TMP_Text>(true);
            Stretch(label.rectTransform, 8f, 0f);
            label.richText = false;
            label.textWrappingMode = TextWrappingModes.NoWrap;
            label.overflowMode = TextOverflowModes.Ellipsis;
            label.raycastTarget = false;
            label.text = text;
            return button;
        }

        private static void Stretch(RectTransform rect, float padding = 0f) => Stretch(rect, padding, padding);

        private static void Stretch(RectTransform rect, float horizontal, float vertical)
        {
            rect.anchorMin = Vector2.zero;
            rect.anchorMax = Vector2.one;
            rect.offsetMin = new Vector2(horizontal, vertical);
            rect.offsetMax = new Vector2(-horizontal, -vertical);
        }

        private static void PlaceRow(RectTransform rect, float top, float height, float padding)
        {
            rect.anchorMin = new Vector2(0f, 1f);
            rect.anchorMax = Vector2.one;
            rect.pivot = new Vector2(0.5f, 1f);
            rect.offsetMin = new Vector2(padding, -top - height);
            rect.offsetMax = new Vector2(-padding, -top);
        }
    }

    internal sealed class EventPageCounterHit : MonoBehaviour, ICanvasRaycastFilter, IPointerClickHandler
    {
        internal InspectorTab Tab;

        public bool IsRaycastLocationValid(Vector2 point, Camera eventCamera)
        {
            if (Tab == null)
            {
                return false;
            }
            TMP_Text label = Tab.cycleButtons.text;
            if (!RectTransformUtility.ScreenPointToLocalPointInRectangle(label.rectTransform, point, eventCamera, out Vector2 local))
            {
                return false;
            }
            Bounds bounds = label.textBounds;
            return Mathf.Abs(local.x - bounds.center.x) <= bounds.extents.x + 3f
                && Mathf.Abs(local.y - bounds.center.y) <= bounds.extents.y + 3f;
        }

        public void OnPointerClick(PointerEventData eventData)
        {
            if (Tab == null)
            {
                return;
            }
            if (eventData.button == PointerEventData.InputButton.Left)
            {
                if (!EventPagePicker.IsCancellingSelectionClick)
                {
                    EventPagePicker.Open(Tab);
                }
            }
            else
            {
                Tab.OnPointerClick(eventData);
            }
        }
    }

    [HarmonyPatch(typeof(InspectorTab), nameof(InspectorTab.SetSelected))]
    internal static class EventPagePickerTabPatch
    {
        private static void Postfix(InspectorTab __instance, bool selected)
        {
            if (selected)
            {
                EventPagePicker.Bind(__instance);
            }
        }
    }

    [HarmonyPatch(typeof(scnEditor), "HandleKeyboardActions")]
    internal static class EventPagePickerEscapePatch
    {
        private static bool Prefix(scnEditor __instance) => !EventPagePicker.DismissForEscape(__instance);
    }

    [HarmonyPatch(typeof(scnEditor), "HandleMouseActions")]
    internal static class EventPagePickerMousePatch
    {
        private static bool Prefix(scnEditor __instance) => !EventPagePicker.HandleMouse(__instance);
    }
}
