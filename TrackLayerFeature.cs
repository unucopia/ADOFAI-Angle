using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using HarmonyLib;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace Angle
{
    internal static class TrackLayerFeature
    {
        private static readonly FieldInfo ShowingPopup = AccessTools.Field(typeof(scnEditor), "showingPopup");
        private static readonly FieldInfo ShowingFileActions = AccessTools.Field(typeof(scnEditor), "showingFileActions");
        private static readonly FieldInfo ShowingFindFloor = AccessTools.Field(typeof(scnEditor), "showingFindFloorPanel");
        private static readonly Vector3[] TitleCorners = new Vector3[4];
        private static scnEditor owner;
        private static scrFloor[] stack = Array.Empty<scrFloor>();
        private static RectTransform root;
        private static TextMeshProUGUI caption;
        private static scrFloor raisedFloor;
        private static int previousOrder;
        private static int raisedOrder;

        internal static void Capture(scnEditor editor, GameObject[] objects)
        {
            if (owner != editor)
            {
                Destroy();
                owner = editor;
            }
            Clear();
            if (objects == null || !CanShow(editor) || !editor.SelectionIsSingle())
            {
                return;
            }

            HashSet<scrFloor> candidates = new HashSet<scrFloor>();
            foreach (GameObject hitObject in objects)
            {
                if (hitObject == null)
                {
                    continue;
                }
                scrFloor floor = hitObject.GetComponent<scrFloor>();
                if (floor == null && editor.levelData.isOldLevel && hitObject.transform.parent != null)
                {
                    floor = hitObject.transform.parent.GetComponent<scrFloor>();
                }
                if (IsValidFloor(editor, floor))
                {
                    candidates.Add(floor);
                }
            }
            if (candidates.Count > 1 && candidates.Contains(editor.selectedFloors[0]))
            {
                stack = candidates.OrderBy(floor => floor.seqID).ToArray();
                Update();
            }
        }

        internal static void Update()
        {
            scnEditor editor = scnEditor.instance;
            if (editor == null || editor != owner)
            {
                if (!ReferenceEquals(owner, null))
                {
                    Destroy();
                }
                return;
            }
            if (stack.Length < 2)
            {
                return;
            }
            if (!CanShow(editor))
            {
                RestoreOrder();
                Hide();
                return;
            }
            int selected = SelectedIndex(editor);
            if (selected < 0 || stack.Any(floor => !IsValidFloor(editor, floor)))
            {
                Clear();
                return;
            }
            if (root == null)
            {
                Build(editor);
            }

            string text = $"{selected + 1}/{stack.Length} · #{stack[selected].seqID}";
            if (caption.text != text)
            {
                caption.text = text;
            }
            Place(editor);
            root.gameObject.SetActive(true);
            Raise(stack[selected]);
        }

        internal static void ClearFor(scnEditor editor)
        {
            if (owner == editor)
            {
                Clear();
            }
        }

        internal static void RestoreOrderFor(scnEditor editor)
        {
            if (owner == editor)
            {
                RestoreOrder();
            }
        }

        internal static void Destroy()
        {
            Clear();
            if (root != null)
            {
                UnityEngine.Object.Destroy(root.gameObject);
            }
            root = null;
            caption = null;
            owner = null;
        }

        private static bool CanShow(scnEditor editor)
        {
            return editor != null && editor.initialized && !editor.playMode
                && !NoteSavePreview.IsActive
                && editor.levelEditorCanvas != null && editor.levelEditorCanvas.isActiveAndEnabled
                && editor.filenameText != null && editor.filenameText.gameObject.activeInHierarchy
                && (editor.prefsContainer == null || !editor.prefsContainer.gameObject.activeSelf)
                && !Flag(ShowingPopup, editor) && !Flag(ShowingFileActions, editor)
                && !Flag(ShowingFindFloor, editor);
        }

        private static bool Flag(FieldInfo field, scnEditor editor)
        {
            return field != null && (bool)field.GetValue(editor);
        }

        private static bool IsValidFloor(scnEditor editor, scrFloor floor)
        {
            return floor != null && floor.enabled && floor.gameObject.activeInHierarchy && !floor.isFake
                && floor.seqID >= 0 && floor.seqID < editor.floors.Count
                && editor.floors[floor.seqID] == floor && floor.floorRenderer != null
                && floor.floorRenderer.renderer != null;
        }

        private static int SelectedIndex(scnEditor editor)
        {
            return editor.SelectionIsSingle() ? Array.IndexOf(stack, editor.selectedFloors[0]) : -1;
        }

        private static void SwitchLayer(int direction)
        {
            scnEditor editor = owner;
            if (!CanShow(editor) || stack.Length < 2)
            {
                return;
            }
            int selected = SelectedIndex(editor);
            int target = (selected + direction + stack.Length) % stack.Length;
            if (selected < 0 || !IsValidFloor(editor, stack[target]))
            {
                Clear();
                return;
            }

            RestoreOrder();
            using (new SaveStateScope(editor, skipSaving: true))
            {
                editor.SelectFloor(stack[target], cameraJump: false);
            }
            Update();
        }

        private static void Clear()
        {
            RestoreOrder();
            stack = Array.Empty<scrFloor>();
            Hide();
        }

        private static void Hide()
        {
            if (root != null)
            {
                root.gameObject.SetActive(false);
            }
        }

        private static void Raise(scrFloor floor)
        {
            if (raisedFloor == floor && floor.floorRenderer.sortingOrder == raisedOrder)
            {
                return;
            }
            RestoreOrder();
            int highestOrder = stack.Max(candidate => candidate.floorRenderer.sortingOrder);
            previousOrder = floor.floorRenderer.sortingOrder;
            raisedOrder = Math.Min(short.MaxValue - 10, highestOrder + 12);
            if (raisedOrder <= previousOrder)
            {
                return;
            }
            raisedFloor = floor;
            floor.SetSortingOrder(raisedOrder);
        }

        private static void RestoreOrder()
        {
            if (raisedFloor != null && raisedFloor.floorRenderer != null
                && raisedFloor.floorRenderer.renderer != null
                && raisedFloor.floorRenderer.sortingOrder == raisedOrder)
            {
                raisedFloor.SetSortingOrder(previousOrder);
            }
            raisedFloor = null;
        }

        private static void Build(scnEditor editor)
        {
            GameObject panel = new GameObject("AngleTrackLayers", typeof(RectTransform));
            panel.SetActive(false);
            root = panel.GetComponent<RectTransform>();
            root.SetParent(editor.levelEditorCanvas.transform, false);
            root.pivot = new Vector2(0.5f, 1f);
            root.sizeDelta = new Vector2(176f, 30f);
            root.SetAsLastSibling();

            caption = AddLabel(root, editor.filenameText, "Layer number", string.Empty);
            caption.enableAutoSizing = true;
            caption.fontSizeMin = 10f;
            caption.fontSizeMax = 15f;
            caption.rectTransform.offsetMin = new Vector2(31f, 0f);
            caption.rectTransform.offsetMax = new Vector2(-31f, 0f);
            AddButton(editor, editor.buttonPrevPage, -1);
            AddButton(editor, editor.buttonNextPage, 1);
        }

        private static void Place(scnEditor editor)
        {
            editor.filenameText.rectTransform.GetWorldCorners(TitleCorners);
            Vector3 bottom = (TitleCorners[0] + TitleCorners[3]) * 0.5f;
            if (editor.buttonFileActionDropdown != null
                && editor.buttonFileActionDropdown.transform is RectTransform dropdown)
            {
                dropdown.GetWorldCorners(TitleCorners);
                if (TitleCorners[0].y < bottom.y)
                {
                    bottom.y = TitleCorners[0].y;
                }
            }
            root.position = bottom - root.parent.TransformVector(Vector3.up * 14f);
        }

        private static void AddButton(scnEditor editor, Button template, int direction)
        {
            GameObject buttonObject = new GameObject(direction < 0 ? "Previous layer" : "Next layer",
                typeof(RectTransform), typeof(Image), typeof(Button));
            RectTransform rect = buttonObject.GetComponent<RectTransform>();
            rect.SetParent(root, false);
            float side = direction < 0 ? 0f : 1f;
            rect.anchorMin = rect.anchorMax = rect.pivot = new Vector2(side, 0.5f);
            rect.sizeDelta = new Vector2(30f, 30f);

            Image hitTarget = buttonObject.GetComponent<Image>();
            hitTarget.color = Color.clear;
            hitTarget.canvasRenderer.cullTransparentMesh = false;
            Button button = buttonObject.GetComponent<Button>();
            button.navigation = new Navigation { mode = Navigation.Mode.None };
            if (template != null)
            {
                button.colors = template.colors;
            }
            button.onClick.AddListener(() => SwitchLayer(direction));

            Sprite sprite = editor.fileSpriteDown;
            float angle = direction * 90f;
            if (sprite == null && editor.fileSpriteUp != null)
            {
                sprite = editor.fileSpriteUp;
                angle = -angle;
            }
            if (sprite == null && editor.fileArrow != null)
            {
                sprite = editor.fileArrow.sprite;
            }
            GameObject arrowObject = new GameObject("Arrow", typeof(RectTransform), typeof(Image));
            RectTransform arrowRect = arrowObject.GetComponent<RectTransform>();
            arrowRect.SetParent(rect, false);
            arrowRect.localRotation = Quaternion.Euler(0f, 0f, angle);
            const float TriangleSide = 16f;
            arrowRect.sizeDelta = new Vector2(TriangleSide, TriangleSide * Mathf.Sqrt(3f) / 2f);
            Image arrow = arrowObject.GetComponent<Image>();
            arrow.sprite = sprite;
            arrow.enabled = sprite != null;
            arrow.raycastTarget = false;
            button.targetGraphic = arrow;
        }

        private static TextMeshProUGUI AddLabel(RectTransform parent, TMP_Text nativeLabel, string name, string text)
        {
            GameObject label = new GameObject(name, typeof(RectTransform), typeof(TextMeshProUGUI));
            RectTransform rect = label.GetComponent<RectTransform>();
            rect.SetParent(parent, false);
            rect.anchorMin = Vector2.zero;
            rect.anchorMax = Vector2.one;
            rect.offsetMin = rect.offsetMax = Vector2.zero;
            TextMeshProUGUI result = label.GetComponent<TextMeshProUGUI>();
            result.font = nativeLabel.font;
            result.color = nativeLabel.color;
            result.fontSize = 15f;
            result.alignment = TextAlignmentOptions.Center;
            result.raycastTarget = false;
            result.text = text;
            return result;
        }
    }

    [HarmonyPatch(typeof(scnEditor), "CycleObjectSelection")]
    internal static class TrackLayerSelectionPatch
    {
        private static void Prefix(scnEditor __instance)
        {
            TrackLayerFeature.RestoreOrderFor(__instance);
        }

        private static void Postfix(scnEditor __instance, GameObject[] ___foundObjects)
        {
            TrackLayerFeature.Capture(__instance, ___foundObjects);
        }
    }

    [HarmonyPatch]
    internal static class TrackLayerResetPatch
    {
        private static IEnumerable<MethodBase> TargetMethods()
        {
            yield return AccessTools.Method(typeof(scnEditor), nameof(scnEditor.RemakePath));
            yield return AccessTools.Method(typeof(scnEditor), nameof(scnEditor.Play));
        }

        private static void Prefix(scnEditor __instance)
        {
            TrackLayerFeature.ClearFor(__instance);
        }
    }

    [HarmonyPatch(typeof(scnEditor), nameof(scnEditor.ApplyEventsToFloors))]
    internal static class TrackLayerEventsPatch
    {
        private static void Prefix(scnEditor __instance)
        {
            TrackLayerFeature.RestoreOrderFor(__instance);
        }
    }
}
