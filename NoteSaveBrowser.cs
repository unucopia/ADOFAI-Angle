using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using ADOFAI;
using ADOFAI.LevelEditor.Controls;
using Newtonsoft.Json;
using TMPro;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;

namespace Angle
{
    internal static class NoteSaveBrowser
    {
        private enum PatternSortMode
        {
            Name,
            Created,
            Modified
        }

        [Serializable]
        private sealed class PinFileData
        {
            public List<string> paths = new List<string>();
        }

        [Serializable]
        private sealed class BrowserSettingsData
        {
            public string sortMode = "Name";
            public bool descending;
        }

        private const float FolderRowHeight = 44f;
        private const float TreeRowHeight = 28f;
        private const float TreeIndent = 16f;
        private const float TreePadding = 8f;
        private const float ContextRowHeight = 36f;
        private const float ContextRowSpacing = 5f;
        private const float PatternGridSpacing = 8f;
        private const float PatternCardWidth = 130f;
        private const float PatternCardHeight = 140f;
        private const float SearchRowHeight = 30f;

        private static GameObject contextMenu;
        private static NoteSaveTreeEntry draggedEntry;
        private static NoteSaveTreeEntry highlightedFolder;
        private static GameObject dragGhost;
        private static RectTransform dragGhostRect;
        private static Sprite chevronRightSprite;
        private static Sprite dragGhostSprite;
        private static Sprite pinMarkerSprite;
        private static Sprite roundedBoxSprite;
        private static Button overwriteButton;
        private static GameObject overwriteTooltip;
        private static PatternSortMode patternSortMode =
            PatternSortMode.Name;
        private static bool patternSortDescending;
        private static TMP_Text sortButtonLabel;
        private static readonly List<string> pinnedPaths =
            new List<string>();
        private static bool pinsLoaded;
        private static bool browserSettingsLoaded;
        private static readonly Dictionary<string, ThumbnailCacheEntry>
            thumbnailCache =
                new Dictionary<string, ThumbnailCacheEntry>(
                    StringComparer.OrdinalIgnoreCase);
        private static string searchQuery = "";
        private static TMP_InputField searchInput;
        private static Button clearSearchButton;
        private static RectTransform searchResultsRoot;
        private static string renderedSearchQuery = "";

        private sealed class ThumbnailCacheEntry
        {
            internal long FileTicks;
            internal RenderTexture Texture;
        }

        internal static void Rebuild(RectTransform content)
        {
            CloseContextMenu();
            HideOverwriteTooltip();
            ResetDrag();
            if (NoteSaveNativeUI.Panel != null
                && NoteSaveNativeUI.Panel.contentLayout != null)
            {
                NoteSaveNativeUI.Panel.contentLayout.spacing = 4f;
                NoteSaveNativeUI.Panel.contentLayout.childControlWidth = true;
                NoteSaveNativeUI.Panel.contentLayout.childForceExpandWidth = true;
            }
            EnsureBlankArea(content);
            AddSearchBar(content);
            searchResultsRoot = CreateResultsRoot(content);
            renderedSearchQuery = null;
            RenderCurrentResults();
        }

        internal static void UpdateResponsiveLayout()
        {
        }

        internal static void Close()
        {
            CloseContextMenu();
            HideOverwriteTooltip();
            ResetDrag();
            searchInput = null;
            clearSearchButton = null;
            searchResultsRoot = null;
        }

        internal static void ResetResponsiveSizing()
        {
        }

        private static void EnsureBlankArea(RectTransform content)
        {
            Image background = content.GetComponent<Image>();
            if (background == null)
            {
                background = content.gameObject.AddComponent<Image>();
            }
            background.color = Color.clear;
            background.raycastTarget = true;

            NoteSaveBlankArea blank = content.GetComponent<NoteSaveBlankArea>();
            if (blank == null)
            {
                blank = content.gameObject.AddComponent<NoteSaveBlankArea>();
            }
        }

        private static void AddSearchBar(RectTransform content)
        {
            searchInput = null;
            clearSearchButton = null;
            GameObject rowObject = new GameObject(
                "PatternSearch",
                typeof(RectTransform),
                typeof(LayoutElement));
            RectTransform rowRect = rowObject.GetComponent<RectTransform>();
            rowRect.SetParent(content, false);
            LayoutElement layout = rowObject.GetComponent<LayoutElement>();
            layout.preferredHeight = SearchRowHeight;
            layout.minHeight = SearchRowHeight;
            layout.flexibleWidth = 1f;
            CreateSortButton(rowRect);
            CreateOverwriteButton(rowRect);

            scnEditor editor = scnEditor.instance;
            TMP_InputField source = editor == null
                ? null
                : editor.floorButtonArbitrary;
            GameObject inputObject = null;
            if (source != null)
            {
                inputObject = UnityEngine.Object.Instantiate(
                    source.gameObject,
                    rowRect,
                    false);
                inputObject.name = "PatternSearchInput";
                inputObject.SetActive(true);
                foreach (scrTextChanger changer in
                    inputObject.GetComponentsInChildren<scrTextChanger>(
                        true))
                {
                    UnityEngine.Object.Destroy(changer);
                }
                foreach (MonoBehaviour behaviour in
                    inputObject.GetComponents<MonoBehaviour>())
                {
                    if (behaviour != null
                        && behaviour.GetType().Name.IndexOf(
                            "NumberInput",
                            StringComparison.Ordinal) >= 0)
                    {
                        behaviour.enabled = false;
                    }
                }
                RectTransform inputRect =
                    inputObject.GetComponent<RectTransform>();
                inputRect.anchorMin = Vector2.zero;
                inputRect.anchorMax = Vector2.one;
                inputRect.offsetMin = new Vector2(48f, 2f);
                inputRect.offsetMax = new Vector2(-52f, -2f);
                searchInput = inputObject.GetComponent<TMP_InputField>();
            }

            if (searchInput != null)
            {
                searchInput.contentType =
                    TMP_InputField.ContentType.Standard;
                searchInput.lineType =
                    TMP_InputField.LineType.SingleLine;
                searchInput.characterLimit = 96;
                searchInput.onValueChanged.RemoveAllListeners();
                searchInput.onSubmit.RemoveAllListeners();
                searchInput.onEndEdit.RemoveAllListeners();
                searchInput.onValueChanged.AddListener(
                    delegate(string value)
                    {
                        string previousEffective =
                            (searchQuery ?? "").Trim();
                        searchQuery = value ?? "";
                        if (clearSearchButton != null)
                        {
                            clearSearchButton.gameObject.SetActive(
                                !string.IsNullOrWhiteSpace(searchQuery));
                        }
                        if (!string.Equals(
                            previousEffective,
                            searchQuery.Trim(),
                            StringComparison.Ordinal))
                        {
                            RenderCurrentResults();
                        }
                    });
                searchInput.onSubmit.AddListener(delegate
                {
                    if (!string.Equals(
                        (searchQuery ?? "").Trim(),
                        renderedSearchQuery ?? "",
                        StringComparison.Ordinal))
                    {
                        RenderCurrentResults();
                    }
                });
                searchInput.SetTextWithoutNotify(searchQuery);
                TMP_Text placeholder =
                    ConfigureSearchPlaceholder(
                        searchInput,
                        inputObject);
                if (placeholder != null)
                {
                    if (searchInput.textComponent != null)
                    {
                        placeholder.font =
                            searchInput.textComponent.font;
                        placeholder.fontSize = Mathf.Min(
                            14f,
                            searchInput.textComponent.fontSize);
                    }
                    placeholder.color =
                        new Color(1f, 1f, 1f, 0.45f);
                }
                if (searchInput.textViewport != null)
                {
                    Vector2 textOffset =
                        searchInput.textViewport.offsetMax;
                    textOffset.x = -27f;
                    searchInput.textViewport.offsetMax = textOffset;
                }
                Image inputBackground =
                    inputObject.GetComponent<Image>();
                if (inputBackground != null)
                {
                    inputBackground.color =
                        new Color(1f, 1f, 1f, 0.14f);
                }

                clearSearchButton = CreateSearchClearButton(
                    rowRect,
                    searchInput);
                clearSearchButton.gameObject.SetActive(
                    !string.IsNullOrWhiteSpace(searchQuery));
            }
        }

        private static void CreateSortButton(RectTransform row)
        {
            EnsureBrowserSettingsLoaded();
            GameObject buttonObject = new GameObject(
                "PatternSort",
                typeof(RectTransform),
                typeof(CanvasRenderer),
                typeof(Image),
                typeof(Button));
            RectTransform buttonRect =
                buttonObject.GetComponent<RectTransform>();
            buttonRect.SetParent(row, false);
            buttonRect.anchorMin = new Vector2(0f, 0.5f);
            buttonRect.anchorMax = new Vector2(0f, 0.5f);
            buttonRect.pivot = new Vector2(0f, 0.5f);
            buttonRect.anchoredPosition = new Vector2(8f, 0f);
            buttonRect.sizeDelta = new Vector2(34f, 25f);

            Image image = buttonObject.GetComponent<Image>();
            EnsureRoundedBoxSprite();
            image.sprite = roundedBoxSprite;
            image.type = Image.Type.Sliced;
            image.color = new Color(1f, 1f, 1f, 0.14f);
            image.raycastTarget = true;
            Button button = buttonObject.GetComponent<Button>();
            button.targetGraphic = image;
            ColorBlock colors = button.colors;
            colors.normalColor = new Color(1f, 1f, 1f, 0.14f);
            colors.highlightedColor = new Color(1f, 1f, 1f, 0.2f);
            colors.pressedColor = new Color(1f, 1f, 1f, 0.3f);
            colors.selectedColor = colors.normalColor;
            button.colors = colors;

            GameObject textObject = new GameObject(
                "Label",
                typeof(RectTransform),
                typeof(CanvasRenderer),
                typeof(TextMeshProUGUI));
            RectTransform textRect =
                textObject.GetComponent<RectTransform>();
            textRect.SetParent(buttonRect, false);
            textRect.anchorMin = Vector2.zero;
            textRect.anchorMax = Vector2.one;
            textRect.offsetMin = Vector2.zero;
            textRect.offsetMax = Vector2.zero;
            sortButtonLabel =
                textObject.GetComponent<TextMeshProUGUI>();
            sortButtonLabel.font =
                NoteSaveNativeUI.Panel == null
                    || NoteSaveNativeUI.Panel.inspectorPanel == null
                    ? null
                    : NoteSaveNativeUI.Panel.inspectorPanel.title.font;
            sortButtonLabel.fontSize = 11f;
            sortButtonLabel.alignment = TextAlignmentOptions.Center;
            sortButtonLabel.color = new Color(1f, 1f, 1f, 0.78f);
            sortButtonLabel.raycastTarget = false;
            UpdateSortButtonLabel();

            button.onClick.RemoveAllListeners();
            button.onClick.AddListener(delegate
            {
                ClearUiSelection();
                Vector2 screenPosition =
                    RectTransformUtility.WorldToScreenPoint(
                        null,
                        buttonRect.TransformPoint(
                            new Vector3(0f, 0f, 0f)));
                ShowSortContextMenu(screenPosition);
            });
        }

        private static void CreateOverwriteButton(RectTransform row)
        {
            EnsureRoundedBoxSprite();
            GameObject buttonObject = new GameObject(
                "OverwritePaste",
                typeof(RectTransform),
                typeof(CanvasRenderer),
                typeof(Image),
                typeof(Button));
            RectTransform buttonRect =
                buttonObject.GetComponent<RectTransform>();
            buttonRect.SetParent(row, false);
            buttonRect.anchorMin = new Vector2(1f, 0.5f);
            buttonRect.anchorMax = new Vector2(1f, 0.5f);
            buttonRect.pivot = new Vector2(1f, 0.5f);
            buttonRect.anchoredPosition = new Vector2(-8f, 0f);
            buttonRect.sizeDelta = new Vector2(42f, 25f);

            Image image = buttonObject.GetComponent<Image>();
            image.sprite = roundedBoxSprite;
            image.type = Image.Type.Sliced;
            image.raycastTarget = true;
            overwriteButton = buttonObject.GetComponent<Button>();
            overwriteButton.targetGraphic = image;

            GameObject textObject = new GameObject(
                "Label",
                typeof(RectTransform),
                typeof(CanvasRenderer),
                typeof(TextMeshProUGUI));
            RectTransform textRect =
                textObject.GetComponent<RectTransform>();
            textRect.SetParent(buttonRect, false);
            textRect.anchorMin = Vector2.zero;
            textRect.anchorMax = Vector2.one;
            textRect.offsetMin = Vector2.zero;
            textRect.offsetMax = Vector2.zero;
            TextMeshProUGUI label =
                textObject.GetComponent<TextMeshProUGUI>();
            label.font =
                NoteSaveNativeUI.Panel == null
                    || NoteSaveNativeUI.Panel.inspectorPanel == null
                    ? null
                    : NoteSaveNativeUI.Panel.inspectorPanel.title.font;
            label.fontSize = 12f;
            label.alignment = TextAlignmentOptions.Center;
            label.raycastTarget = false;
            label.text = AngleMod.L("覆盖", "Overwrite");

            overwriteButton.onClick.RemoveAllListeners();
            overwriteButton.onClick.AddListener(delegate
            {
                ClearUiSelection();
                NoteSaveFeature.SetOverwritePasteEnabled(
                    !NoteSaveFeature.OverwritePasteEnabled);
                UpdateOverwriteButtonStyle();
            });
            OverwriteTooltipTrigger trigger =
                buttonObject.AddComponent<OverwriteTooltipTrigger>();
            trigger.Configure(buttonRect);
            UpdateOverwriteButtonStyle();
        }

        private static void UpdateOverwriteButtonStyle()
        {
            if (overwriteButton == null)
            {
                return;
            }
            bool enabled = NoteSaveFeature.OverwritePasteEnabled;
            Image image = overwriteButton.targetGraphic as Image;
            if (image != null)
            {
                image.color = enabled
                    ? new Color(1f, 0.8f, 0.3f, 0.24f)
                    : new Color(1f, 1f, 1f, 0.12f);
            }
            TextMeshProUGUI label =
                overwriteButton.GetComponentInChildren<
                    TextMeshProUGUI>(true);
            if (label != null)
            {
                label.color = enabled
                    ? new Color(1f, 0.86f, 0.42f, 1f)
                    : new Color(1f, 1f, 1f, 0.7f);
            }
        }

        private static void EnsureRoundedBoxSprite()
        {
            if (roundedBoxSprite != null)
            {
                return;
            }
            const int size = 64;
            const float radius = 14f;
            Texture2D texture = new Texture2D(
                size,
                size,
                TextureFormat.RGBA32,
                false);
            for (int y = 0; y < size; y++)
            {
                for (int x = 0; x < size; x++)
                {
                    int hits = 0;
                    const int samples = 4;
                    for (int sampleY = 0;
                        sampleY < samples;
                        sampleY++)
                    {
                        for (int sampleX = 0;
                            sampleX < samples;
                            sampleX++)
                        {
                            float px =
                                x + (sampleX + 0.5f) / samples;
                            float py =
                                y + (sampleY + 0.5f) / samples;
                            float nearestX = Mathf.Clamp(
                                px,
                                radius,
                                size - radius);
                            float nearestY = Mathf.Clamp(
                                py,
                                radius,
                                size - radius);
                            if (Vector2.Distance(
                                    new Vector2(px, py),
                                    new Vector2(
                                        nearestX,
                                        nearestY))
                                <= radius)
                            {
                                hits++;
                            }
                        }
                    }
                    texture.SetPixel(
                        x,
                        y,
                        new Color(
                            1f,
                            1f,
                            1f,
                            hits / (float)(samples * samples)));
                }
            }
            texture.Apply();
            texture.filterMode = FilterMode.Bilinear;
            texture.wrapMode = TextureWrapMode.Clamp;
            roundedBoxSprite = Sprite.Create(
                texture,
                new Rect(0f, 0f, size, size),
                new Vector2(0.5f, 0.5f),
                100f,
                0,
                SpriteMeshType.FullRect,
                new Vector4(18f, 18f, 18f, 18f));
        }

        internal static void ShowOverwriteTooltip(
            RectTransform anchor)
        {
            HideOverwriteTooltip();
            scnEditor editor = scnEditor.instance;
            if (editor == null
                || editor.levelEditorCanvas == null
                || anchor == null)
            {
                return;
            }
            EnsureRoundedBoxSprite();
            overwriteTooltip = new GameObject(
                "OverwriteTooltip",
                typeof(RectTransform),
                typeof(CanvasRenderer),
                typeof(Image));
            RectTransform tooltipRect =
                overwriteTooltip.GetComponent<RectTransform>();
            tooltipRect.SetParent(
                editor.levelEditorCanvas.transform,
                false);
            tooltipRect.anchorMin = new Vector2(0f, 1f);
            tooltipRect.anchorMax = new Vector2(0f, 1f);
            tooltipRect.pivot = new Vector2(0f, 1f);
            tooltipRect.sizeDelta = new Vector2(292f, 102f);
            Image background = overwriteTooltip.GetComponent<Image>();
            background.sprite = roundedBoxSprite;
            background.type = Image.Type.Sliced;
            background.color = new Color(0.02f, 0.027f, 0.045f, 0.96f);
            background.raycastTarget = false;

            GameObject textObject = new GameObject(
                "Text",
                typeof(RectTransform),
                typeof(CanvasRenderer),
                typeof(TextMeshProUGUI));
            RectTransform textRect =
                textObject.GetComponent<RectTransform>();
            textRect.SetParent(tooltipRect, false);
            textRect.anchorMin = Vector2.zero;
            textRect.anchorMax = Vector2.one;
            textRect.offsetMin = new Vector2(12f, 8f);
            textRect.offsetMax = new Vector2(-12f, -8f);
            TextMeshProUGUI text =
                textObject.GetComponent<TextMeshProUGUI>();
            text.font =
                NoteSaveNativeUI.Panel == null
                    || NoteSaveNativeUI.Panel.inspectorPanel == null
                    ? null
                    : NoteSaveNativeUI.Panel.inspectorPanel.title.font;
            text.fontSize = 13f;
            text.alignment = TextAlignmentOptions.TopLeft;
            text.color = new Color(1f, 1f, 1f, 0.88f);
            text.raycastTarget = false;
            text.textWrappingMode = TextWrappingModes.Normal;
            text.text =
                "<b>" + AngleMod.L("覆盖粘贴", "Overwrite paste") + "</b>\n"
                + AngleMod.L(
                    "开启后，粘贴会用保存键型覆盖选中轨道之后的对应轨道。仅覆盖旋转、暂停节拍、检查点、设置速度、自动播放格子和长按事件；其他目标事件保持不变。",
                    "When enabled, pasted floors replace the matching floors after the selected floor. Only Twirl, Pause, Checkpoint, Set Speed, Auto Play tiles and Hold are overwritten; other destination events are kept.");

            RectTransform canvasRect = editor.levelEditorCanvas
                .GetComponent<RectTransform>();
            Camera eventCamera =
                editor.levelEditorCanvas.renderMode
                    == RenderMode.ScreenSpaceOverlay
                    ? null
                    : editor.levelEditorCanvas.worldCamera;
            Vector2 screenPosition =
                RectTransformUtility.WorldToScreenPoint(
                    null,
                    anchor.TransformPoint(
                        new Vector3(
                            anchor.rect.xMax,
                            anchor.rect.yMax,
                            0f)));
            Vector2 local;
            RectTransformUtility.ScreenPointToLocalPointInRectangle(
                canvasRect,
                screenPosition,
                eventCamera,
                out local);
            Rect bounds = canvasRect.rect;
            Vector2 anchoredPosition = new Vector2(
                local.x - bounds.xMin + 6f,
                local.y - bounds.yMax - 6f);
            anchoredPosition.x = Mathf.Clamp(
                anchoredPosition.x,
                4f,
                bounds.width - tooltipRect.sizeDelta.x - 4f);
            anchoredPosition.y = Mathf.Clamp(
                anchoredPosition.y,
                -(bounds.height - tooltipRect.sizeDelta.y - 4f),
                -4f);
            tooltipRect.anchoredPosition = anchoredPosition;
            overwriteTooltip.transform.SetAsLastSibling();
        }

        internal static void HideOverwriteTooltip()
        {
            if (overwriteTooltip != null)
            {
                UnityEngine.Object.Destroy(overwriteTooltip);
                overwriteTooltip = null;
            }
        }

        private static void UpdateSortButtonLabel()
        {
            if (sortButtonLabel == null)
            {
                return;
            }
            string mode = patternSortMode == PatternSortMode.Name
                ? AngleMod.L("名", "N")
                : patternSortMode == PatternSortMode.Created
                    ? AngleMod.L("生", "C")
                    : AngleMod.L("改", "M");
            sortButtonLabel.text =
                mode + (patternSortDescending ? "↓" : "↑");
        }

        private static void ShowSortContextMenu(
            Vector2 screenPosition)
        {
            ShowContextMenu(
                screenPosition,
                new[]
                {
                    new ContextItem(
                        SortModeText(
                            PatternSortMode.Name,
                            AngleMod.L("按名称排序", "Sort by name")),
                        delegate
                        {
                            patternSortMode = PatternSortMode.Name;
                            RefreshAfterSortChange();
                        }),
                    new ContextItem(
                        SortModeText(
                            PatternSortMode.Created,
                            AngleMod.L("按创建日期排序", "Sort by created")),
                        delegate
                        {
                            patternSortMode = PatternSortMode.Created;
                            RefreshAfterSortChange();
                        }),
                    new ContextItem(
                        SortModeText(
                            PatternSortMode.Modified,
                            AngleMod.L("按修改日期排序", "Sort by modified")),
                        delegate
                        {
                            patternSortMode = PatternSortMode.Modified;
                            RefreshAfterSortChange();
                        }),
                    new ContextItem(
                        patternSortDescending
                            ? AngleMod.L("倒序：开", "Descending: On")
                            : AngleMod.L("倒序：关", "Descending: Off"),
                        delegate
                        {
                            patternSortDescending =
                                !patternSortDescending;
                            RefreshAfterSortChange();
                        })
                });
        }

        private static string SortModeText(
            PatternSortMode mode,
            string text)
        {
            return patternSortMode == mode
                ? text + "  ✓"
                : text;
        }

        private static void RefreshAfterSortChange()
        {
            SaveBrowserSettings();
            UpdateSortButtonLabel();
            renderedSearchQuery = null;
            RenderCurrentResults();
        }

        private static TMP_Text ConfigureSearchPlaceholder(
            TMP_InputField input,
            GameObject inputObject)
        {
            if (input == null)
            {
                return null;
            }
            string text = AngleMod.L("查找...", "Search...");
            TMP_Text existing = input.placeholder as TMP_Text;
            if (existing == null && inputObject != null)
            {
                foreach (TMP_Text candidate in
                    inputObject.GetComponentsInChildren<TMP_Text>(true))
                {
                    if (candidate != input.textComponent
                        && candidate.name.IndexOf(
                            "Placeholder",
                            StringComparison.OrdinalIgnoreCase) >= 0)
                    {
                        existing = candidate;
                        break;
                    }
                }
            }

            if (existing == null)
            {
                GameObject placeholderObject = new GameObject(
                    "SearchPlaceholder",
                    typeof(RectTransform),
                    typeof(CanvasRenderer),
                    typeof(TextMeshProUGUI));
                RectTransform placeholderRect =
                    placeholderObject.GetComponent<RectTransform>();
                Transform parent = input.textViewport != null
                    ? input.textViewport
                    : input.transform;
                placeholderRect.SetParent(parent, false);
                placeholderRect.anchorMin = Vector2.zero;
                placeholderRect.anchorMax = Vector2.one;
                placeholderRect.offsetMin = Vector2.zero;
                placeholderRect.offsetMax = Vector2.zero;
                existing =
                    placeholderObject.GetComponent<TextMeshProUGUI>();
                existing.font = input.textComponent == null
                    ? null
                    : input.textComponent.font;
                existing.fontSize = input.textComponent == null
                    ? 13f
                    : Mathf.Min(14f, input.textComponent.fontSize);
                existing.alignment =
                    TextAlignmentOptions.MidlineLeft;
                existing.textWrappingMode =
                    TextWrappingModes.NoWrap;
                existing.overflowMode =
                    TextOverflowModes.Ellipsis;
                existing.raycastTarget = false;
            }

            input.placeholder = existing;
            existing.text = text;
            if (inputObject != null)
            {
                foreach (TMP_Text candidate in
                    inputObject.GetComponentsInChildren<TMP_Text>(true))
                {
                    if (candidate != input.textComponent
                        && candidate != existing)
                    {
                        candidate.text = text;
                    }
                }
            }
            return existing;
        }

        private static Button CreateSearchClearButton(
            RectTransform row,
            TMP_InputField input)
        {
            GameObject buttonObject = new GameObject(
                "ClearSearch",
                typeof(RectTransform),
                typeof(CanvasRenderer),
                typeof(Image),
                typeof(Button));
            RectTransform buttonRect =
                buttonObject.GetComponent<RectTransform>();
            buttonRect.SetParent(row, false);
            buttonRect.anchorMin = new Vector2(1f, 0.5f);
            buttonRect.anchorMax = new Vector2(1f, 0.5f);
            buttonRect.pivot = new Vector2(1f, 0.5f);
            buttonRect.anchoredPosition = new Vector2(-7f, 0f);
            buttonRect.sizeDelta = new Vector2(22f, 22f);

            Image image = buttonObject.GetComponent<Image>();
            image.color = new Color(1f, 1f, 1f, 0.08f);
            image.raycastTarget = true;
            Button button = buttonObject.GetComponent<Button>();
            button.targetGraphic = image;
            ColorBlock colors = button.colors;
            colors.normalColor = new Color(1f, 1f, 1f, 0.08f);
            colors.highlightedColor = new Color(1f, 1f, 1f, 0.2f);
            colors.pressedColor = new Color(1f, 1f, 1f, 0.3f);
            colors.selectedColor = colors.normalColor;
            button.colors = colors;

            GameObject textObject = new GameObject(
                "Glyph",
                typeof(RectTransform),
                typeof(CanvasRenderer),
                typeof(TextMeshProUGUI));
            RectTransform textRect =
                textObject.GetComponent<RectTransform>();
            textRect.SetParent(buttonRect, false);
            textRect.anchorMin = Vector2.zero;
            textRect.anchorMax = Vector2.one;
            textRect.offsetMin = Vector2.zero;
            textRect.offsetMax = Vector2.zero;
            TextMeshProUGUI glyph =
                textObject.GetComponent<TextMeshProUGUI>();
            glyph.font = input != null && input.textComponent != null
                ? input.textComponent.font
                : null;
            glyph.fontSize = 16f;
            glyph.alignment = TextAlignmentOptions.Center;
            glyph.color = new Color(1f, 1f, 1f, 0.8f);
            glyph.raycastTarget = false;
            glyph.text = "×";

            button.onClick.RemoveAllListeners();
            button.onClick.AddListener(delegate
            {
                string previousEffective =
                    (searchQuery ?? "").Trim();
                searchQuery = "";
                if (searchInput != null)
                {
                    searchInput.SetTextWithoutNotify("");
                    searchInput.Select();
                    searchInput.ActivateInputField();
                }
                buttonObject.SetActive(false);
                if (!string.Equals(
                    previousEffective,
                    "",
                    StringComparison.Ordinal))
                {
                    RenderCurrentResults();
                }
            });
            return button;
        }

        private static RectTransform CreateResultsRoot(
            RectTransform content)
        {
            GameObject rootObject = new GameObject(
                "PatternResults",
                typeof(RectTransform),
                typeof(CanvasRenderer),
                typeof(Image),
                typeof(VerticalLayoutGroup),
                typeof(ContentSizeFitter),
                typeof(LayoutElement),
                typeof(NoteSaveBlankArea));
            RectTransform root = rootObject.GetComponent<RectTransform>();
            root.SetParent(content, false);
            Image background = rootObject.GetComponent<Image>();
            background.color = Color.clear;
            background.raycastTarget = true;
            VerticalLayoutGroup layout =
                rootObject.GetComponent<VerticalLayoutGroup>();
            layout.spacing = 4f;
            layout.padding = new RectOffset(0, 0, 0, 0);
            layout.childControlWidth = true;
            layout.childControlHeight = true;
            layout.childForceExpandWidth = true;
            layout.childForceExpandHeight = false;
            ContentSizeFitter fitter =
                rootObject.GetComponent<ContentSizeFitter>();
            fitter.horizontalFit = ContentSizeFitter.FitMode.Unconstrained;
            fitter.verticalFit = ContentSizeFitter.FitMode.PreferredSize;
            LayoutElement element =
                rootObject.GetComponent<LayoutElement>();
            element.flexibleWidth = 1f;
            return root;
        }

        private static void RenderCurrentResults()
        {
            if (searchResultsRoot == null)
            {
                return;
            }
            for (int index = searchResultsRoot.childCount - 1;
                index >= 0;
                index--)
            {
                GameObject child =
                    searchResultsRoot.GetChild(index).gameObject;
                child.transform.SetParent(null, false);
                UnityEngine.Object.Destroy(child);
            }

            string query = (searchQuery ?? "").Trim();
            if (renderedSearchQuery != null
                && string.Equals(
                    query,
                    renderedSearchQuery,
                    StringComparison.Ordinal))
            {
                return;
            }
            renderedSearchQuery = query;
            if (query.Length == 0)
            {
                RenderDirectory(
                    searchResultsRoot,
                    NoteSaveFeature.RootPath,
                    0);
            }
            else
            {
                RenderSearchResults(searchResultsRoot, query);
            }
            LayoutRebuilder.ForceRebuildLayoutImmediate(
                searchResultsRoot);
        }

        private static void RenderSearchResults(
            RectTransform content,
            string query)
        {
            string[] terms = query.Split(
                new[] { ' ', '\t' },
                StringSplitOptions.RemoveEmptyEntries);
            List<string> matches = Directory.GetFiles(
                    NoteSaveFeature.RootPath,
                    "*" + NoteSaveFeature.PatternExtension,
                    SearchOption.AllDirectories)
                .Where(path =>
                {
                    string name = Path.GetFileNameWithoutExtension(path);
                    return terms.All(term =>
                        name.IndexOf(
                            term,
                            StringComparison.OrdinalIgnoreCase) >= 0);
                })
                .ToList();
            matches = SortPatternPaths(matches);

            if (matches.Count == 0)
            {
                AddMessageRow(
                    content,
                    AngleMod.L(
                        "没有找到匹配的键型",
                        "No matching patterns"));
                return;
            }

            AddMessageRow(
                content,
                string.Format(
                    AngleMod.L(
                        "找到 {0} 个键型",
                        "{0} patterns found"),
                    matches.Count));
            foreach (IGrouping<string, string> group in matches.GroupBy(
                path => Path.GetDirectoryName(path)))
            {
                string relativeFolder =
                    NoteSaveFeature.ToRelativePath(group.Key);
                AddSearchFolderHeader(content, relativeFolder);
                AddPatternGrid(content, group.ToList(), 1);
            }
        }

        private static void AddMessageRow(
            RectTransform content,
            string text)
        {
            GameObject rowObject = new GameObject(
                "SearchMessage",
                typeof(RectTransform),
                typeof(CanvasRenderer),
                typeof(TextMeshProUGUI),
                typeof(LayoutElement));
            RectTransform rowRect =
                rowObject.GetComponent<RectTransform>();
            rowRect.SetParent(content, false);
            LayoutElement layout = rowObject.GetComponent<LayoutElement>();
            layout.preferredHeight = 42f;
            layout.minHeight = 42f;
            TextMeshProUGUI label =
                rowObject.GetComponent<TextMeshProUGUI>();
            label.font =
                NoteSaveNativeUI.Panel == null
                    || NoteSaveNativeUI.Panel.inspectorPanel == null
                    ? null
                    : NoteSaveNativeUI.Panel.inspectorPanel.title.font;
            label.fontSize = 14f;
            label.alignment = TextAlignmentOptions.MidlineLeft;
            label.color = new Color(1f, 1f, 1f, 0.58f);
            label.raycastTarget = false;
            label.text = text;
            rowRect.offsetMin = new Vector2(TreePadding, 0f);
            rowRect.offsetMax = new Vector2(-TreePadding, 0f);
        }

        private static void AddSearchFolderHeader(
            RectTransform content,
            string relativeFolder)
        {
            GameObject rowObject = new GameObject(
                "SearchFolderHeader",
                typeof(RectTransform),
                typeof(CanvasRenderer),
                typeof(TextMeshProUGUI),
                typeof(LayoutElement));
            RectTransform rowRect =
                rowObject.GetComponent<RectTransform>();
            rowRect.SetParent(content, false);
            LayoutElement layout = rowObject.GetComponent<LayoutElement>();
            layout.preferredHeight = 28f;
            layout.minHeight = 28f;
            TextMeshProUGUI label =
                rowObject.GetComponent<TextMeshProUGUI>();
            label.font =
                NoteSaveNativeUI.Panel == null
                    || NoteSaveNativeUI.Panel.inspectorPanel == null
                    ? null
                    : NoteSaveNativeUI.Panel.inspectorPanel.title.font;
            label.fontSize = 13f;
            label.fontStyle = FontStyles.Bold;
            label.alignment = TextAlignmentOptions.MidlineLeft;
            label.color = new Color(1f, 1f, 1f, 0.66f);
            label.raycastTarget = false;
            label.overflowMode = TextOverflowModes.Ellipsis;
            label.text = string.IsNullOrEmpty(relativeFolder)
                ? AngleMod.L("根目录", "Root")
                : relativeFolder.Replace("/", " › ");
            rowRect.offsetMin = new Vector2(TreePadding + 4f, 0f);
            rowRect.offsetMax = new Vector2(-TreePadding, 0f);
        }

        private static List<string> SortDirectories(
            IEnumerable<string> directories)
        {
            EnsureBrowserSettingsLoaded();
            LoadPins();
            List<string> all = directories.Distinct(
                    StringComparer.OrdinalIgnoreCase)
                .ToList();
            List<string> pinned = all
                .Where(IsPinned)
                .OrderBy(GetPinOrder)
                .ToList();
            List<string> rest = all
                .Where(path => !IsPinned(path))
                .ToList();
            rest.Sort((left, right) =>
            {
                if (patternSortMode == PatternSortMode.Name
                    && !patternSortDescending)
                {
                    bool leftDefault = string.Equals(
                        Path.GetFileName(left),
                        "Default",
                        StringComparison.OrdinalIgnoreCase);
                    bool rightDefault = string.Equals(
                        Path.GetFileName(right),
                        "Default",
                        StringComparison.OrdinalIgnoreCase);
                    if (leftDefault != rightDefault)
                    {
                        return leftDefault ? -1 : 1;
                    }
                }
                return ComparePaths(left, right, directory: true);
            });
            pinned.AddRange(rest);
            return pinned;
        }

        private static List<string> SortPatternPaths(
            IEnumerable<string> paths)
        {
            EnsureBrowserSettingsLoaded();
            LoadPins();
            List<string> all = paths.Distinct(
                    StringComparer.OrdinalIgnoreCase)
                .ToList();
            List<string> pinned = all
                .Where(IsPinned)
                .OrderBy(GetPinOrder)
                .ToList();
            List<string> rest = all
                .Where(path => !IsPinned(path))
                .ToList();
            rest.Sort(
                (left, right) => ComparePaths(
                    left,
                    right,
                    directory: false));
            pinned.AddRange(rest);
            return pinned;
        }

        private static int ComparePaths(
            string left,
            string right,
            bool directory)
        {
            int result;
            switch (patternSortMode)
            {
                case PatternSortMode.Created:
                    result = (directory
                            ? Directory.GetCreationTimeUtc(left)
                            : File.GetCreationTimeUtc(left))
                        .CompareTo(directory
                            ? Directory.GetCreationTimeUtc(right)
                            : File.GetCreationTimeUtc(right));
                    break;
                case PatternSortMode.Modified:
                    result = (directory
                            ? Directory.GetLastWriteTimeUtc(left)
                            : File.GetLastWriteTimeUtc(left))
                        .CompareTo(directory
                            ? Directory.GetLastWriteTimeUtc(right)
                            : File.GetLastWriteTimeUtc(right));
                    break;
                default:
                    result = string.Compare(
                        Path.GetFileName(left),
                        Path.GetFileName(right),
                        StringComparison.OrdinalIgnoreCase);
                    break;
            }
            if (result == 0)
            {
                result = string.Compare(
                    Path.GetFileName(left),
                    Path.GetFileName(right),
                    StringComparison.OrdinalIgnoreCase);
            }
            return patternSortDescending ? -result : result;
        }

        private static string PinFilePath => Path.Combine(
            NoteSaveFeature.RootPath,
            ".angle-pins.json");

        private static string BrowserSettingsPath => Path.Combine(
            NoteSaveFeature.RootPath,
            ".angle-browser.json");

        private static void EnsureBrowserSettingsLoaded()
        {
            if (browserSettingsLoaded)
            {
                return;
            }
            browserSettingsLoaded = true;
            try
            {
                if (!File.Exists(BrowserSettingsPath))
                {
                    return;
                }
                BrowserSettingsData data =
                    JsonConvert.DeserializeObject<BrowserSettingsData>(
                        File.ReadAllText(BrowserSettingsPath));
                if (data == null)
                {
                    return;
                }
                PatternSortMode mode;
                if (Enum.TryParse(
                    data.sortMode,
                    true,
                    out mode))
                {
                    patternSortMode = mode;
                }
                patternSortDescending = data.descending;
            }
            catch
            {
            }
        }

        private static void SaveBrowserSettings()
        {
            try
            {
                File.WriteAllText(
                    BrowserSettingsPath,
                    JsonConvert.SerializeObject(
                        new BrowserSettingsData
                        {
                            sortMode = patternSortMode.ToString(),
                            descending = patternSortDescending
                        },
                        Formatting.Indented),
                    new UTF8Encoding(false));
            }
            catch
            {
            }
        }

        private static void LoadPins()
        {
            if (pinsLoaded)
            {
                return;
            }
            pinsLoaded = true;
            pinnedPaths.Clear();
            try
            {
                if (!File.Exists(PinFilePath))
                {
                    return;
                }
                PinFileData data = JsonConvert.DeserializeObject<PinFileData>(
                    File.ReadAllText(PinFilePath));
                if (data == null || data.paths == null)
                {
                    return;
                }
                foreach (string path in data.paths)
                {
                    string normalized = (path ?? "")
                        .Replace('\\', '/')
                        .Trim('/');
                    if (!string.IsNullOrEmpty(normalized)
                        && !pinnedPaths.Contains(
                            normalized,
                            StringComparer.OrdinalIgnoreCase))
                    {
                        pinnedPaths.Add(normalized);
                    }
                }
            }
            catch
            {
            }
        }

        private static void SavePins()
        {
            try
            {
                File.WriteAllText(
                    PinFilePath,
                    JsonConvert.SerializeObject(
                        new PinFileData { paths = pinnedPaths },
                        Formatting.Indented),
                    new UTF8Encoding(false));
            }
            catch
            {
            }
        }

        private static string PinRelativePath(string fullPath)
        {
            return NoteSaveFeature.ToRelativePath(fullPath) ?? "";
        }

        private static bool IsPinned(string fullPath)
        {
            LoadPins();
            string relative = PinRelativePath(fullPath);
            return pinnedPaths.Contains(
                relative,
                StringComparer.OrdinalIgnoreCase);
        }

        private static int GetPinOrder(string fullPath)
        {
            LoadPins();
            string relative = PinRelativePath(fullPath);
            for (int index = 0; index < pinnedPaths.Count; index++)
            {
                if (string.Equals(
                    pinnedPaths[index],
                    relative,
                    StringComparison.OrdinalIgnoreCase))
                {
                    return index;
                }
            }
            return int.MaxValue;
        }

        private static void TogglePin(string fullPath)
        {
            LoadPins();
            string relative = PinRelativePath(fullPath);
            if (string.IsNullOrEmpty(relative))
            {
                return;
            }
            int existing = pinnedPaths.FindIndex(path =>
                string.Equals(
                    path,
                    relative,
                    StringComparison.OrdinalIgnoreCase));
            if (existing >= 0)
            {
                pinnedPaths.RemoveAt(existing);
            }
            else
            {
                pinnedPaths.Add(relative);
            }
            SavePins();
            NoteSaveFeature.RequestNativeRebuild();
        }

        private static void AddPinnedMarker(
            RectTransform parent,
            string fullPath,
            Vector2 anchor,
            Vector2 position,
            Vector2 size)
        {
            if (!IsPinned(fullPath))
            {
                return;
            }
            EnsurePinMarkerSprite();
            GameObject markerObject = new GameObject(
                "PinnedMarker",
                typeof(RectTransform),
                typeof(CanvasRenderer),
                typeof(Image));
            RectTransform markerRect =
                markerObject.GetComponent<RectTransform>();
            markerRect.SetParent(parent, false);
            markerRect.anchorMin = anchor;
            markerRect.anchorMax = anchor;
            markerRect.pivot = anchor;
            markerRect.anchoredPosition = position;
            markerRect.sizeDelta = size;
            Image marker = markerObject.GetComponent<Image>();
            marker.sprite = pinMarkerSprite;
            marker.color = new Color(1f, 0.82f, 0.32f, 0.9f);
            marker.preserveAspect = true;
            marker.raycastTarget = false;
        }

        private static void EnsurePinMarkerSprite()
        {
            if (pinMarkerSprite != null)
            {
                return;
            }
            const int size = 64;
            Vector2[] star = new Vector2[10];
            Vector2 center = new Vector2(32f, 32f);
            for (int index = 0; index < star.Length; index++)
            {
                float angle = -Mathf.PI * 0.5f
                    + index * Mathf.PI / 5f;
                float radius = index % 2 == 0 ? 29f : 12.5f;
                star[index] = center + new Vector2(
                    Mathf.Cos(angle) * radius,
                    Mathf.Sin(angle) * radius);
            }
            Texture2D texture = new Texture2D(
                size,
                size,
                TextureFormat.RGBA32,
                false);
            for (int y = 0; y < size; y++)
            {
                for (int x = 0; x < size; x++)
                {
                    int hits = 0;
                    const int samples = 4;
                    for (int sampleY = 0;
                        sampleY < samples;
                        sampleY++)
                    {
                        for (int sampleX = 0;
                            sampleX < samples;
                            sampleX++)
                        {
                            Vector2 point = new Vector2(
                                x + (sampleX + 0.5f) / samples,
                                y + (sampleY + 0.5f) / samples);
                            if (PointInsidePolygon(point, star))
                            {
                                hits++;
                            }
                        }
                    }
                    texture.SetPixel(
                        x,
                        y,
                        new Color(
                            1f,
                            1f,
                            1f,
                            hits / (float)(samples * samples)));
                }
            }
            texture.Apply();
            texture.filterMode = FilterMode.Bilinear;
            texture.wrapMode = TextureWrapMode.Clamp;
            pinMarkerSprite = Sprite.Create(
                texture,
                new Rect(0f, 0f, size, size),
                new Vector2(0.5f, 0.5f),
                size);
        }

        private static bool PointInsidePolygon(
            Vector2 point,
            Vector2[] polygon)
        {
            bool inside = false;
            for (int current = 0, previous = polygon.Length - 1;
                current < polygon.Length;
                previous = current++)
            {
                Vector2 a = polygon[current];
                Vector2 b = polygon[previous];
                if ((a.y > point.y) != (b.y > point.y)
                    && point.x < (b.x - a.x)
                        * (point.y - a.y)
                        / (b.y - a.y)
                        + a.x)
                {
                    inside = !inside;
                }
            }
            return inside;
        }

        private static void RenderDirectory(
            RectTransform content,
            string folder,
            int depth)
        {
            List<string> directories = SortDirectories(
                Directory.GetDirectories(folder));
            foreach (string directory in directories)
            {
                string relative = NoteSaveFeature.ToRelativePath(directory);
                bool expanded = NoteSaveFeature.IsFolderExpanded(relative);
                GameObject childrenObject = null;
                Button row = null;
                row = AddFolderTreeRow(
                    content,
                    Path.GetFileName(directory),
                    depth,
                    expanded,
                    delegate
                    {
                        ClearUiSelection();
                        bool next = !NoteSaveFeature.IsFolderExpanded(relative);
                        NoteSaveFeature.SetFolderExpanded(relative, next);
                        childrenObject.SetActive(next);
                        Transform chevron = row.transform.Find("Chevron");
                        if (chevron != null)
                        {
                            chevron.localEulerAngles = next
                                ? new Vector3(0f, 0f, -90f)
                                : Vector3.zero;
                        }
                        LayoutRebuilder.ForceRebuildLayoutImmediate(content);
                    });

                NoteSaveTreeEntry entry =
                    row.gameObject.AddComponent<NoteSaveTreeEntry>();
                entry.Configure(
                    true,
                    directory,
                    relative,
                    delegate
                    {
                        NoteSaveFeature.SelectFolder(relative);
                        RefreshSelection();
                    },
                    delegate
                    {
                        NoteSaveFeature.SelectFolder(relative);
                        NoteSaveFeature.BeginRenameFolder(directory);
                    },
                    delegate(PointerEventData eventData)
                    {
                        NoteSaveFeature.SelectFolder(relative);
                        RefreshSelection();
                        ShowFolderContextMenu(eventData.position, directory, relative);
                    });

                ApplyTreeRowStyle(
                    row,
                    NoteSaveFeature.CurrentFolder == relative);
                AddPinnedMarker(
                    row.GetComponent<RectTransform>(),
                    directory,
                    new Vector2(1f, 0.5f),
                    new Vector2(-8f, 0f),
                    new Vector2(11f, 11f));

                childrenObject = new GameObject(
                    "FolderChildren_" + Path.GetFileName(directory),
                    typeof(RectTransform),
                    typeof(VerticalLayoutGroup),
                    typeof(ContentSizeFitter),
                    typeof(LayoutElement));
                RectTransform childrenRect =
                    childrenObject.GetComponent<RectTransform>();
                childrenRect.SetParent(content, false);
                VerticalLayoutGroup childrenLayout =
                    childrenObject.GetComponent<VerticalLayoutGroup>();
                childrenLayout.spacing = 4f;
                childrenLayout.padding = new RectOffset(0, 0, 0, 0);
                childrenLayout.childControlWidth = true;
                childrenLayout.childControlHeight = true;
                childrenLayout.childForceExpandWidth = true;
                childrenLayout.childForceExpandHeight = false;
                ContentSizeFitter childrenFitter =
                    childrenObject.GetComponent<ContentSizeFitter>();
                childrenFitter.horizontalFit = ContentSizeFitter.FitMode.Unconstrained;
                childrenFitter.verticalFit = ContentSizeFitter.FitMode.PreferredSize;
                LayoutElement childrenElement =
                    childrenObject.GetComponent<LayoutElement>();
                childrenElement.flexibleWidth = 1f;

                RenderDirectory(childrenRect, directory, depth + 1);
                childrenObject.SetActive(expanded);
            }

            List<string> patterns = SortPatternPaths(
                Directory.GetFiles(
                    folder,
                    "*" + NoteSaveFeature.PatternExtension));
            if (patterns.Count > 0)
            {
                AddPatternGrid(content, patterns, depth + 1);
            }
        }

        private static void AddPatternGrid(
            RectTransform content,
            List<string> paths,
            int depth)
        {
            float indent = TreePadding
                + Mathf.Max(0, depth - 1) * TreeIndent
                + 20f;
            float rightPadding = TreePadding;
            GameObject gridObject = new GameObject(
                "PatternGrid",
                typeof(RectTransform),
                typeof(GridLayoutGroup),
                typeof(ContentSizeFitter),
                typeof(LayoutElement),
                typeof(NoteSaveResponsiveGrid));
            RectTransform gridRect = gridObject.GetComponent<RectTransform>();
            gridRect.SetParent(content, false);
            GridLayoutGroup grid = gridObject.GetComponent<GridLayoutGroup>();
            grid.cellSize = new Vector2(
                PatternCardWidth,
                PatternCardHeight);
            grid.spacing = new Vector2(
                PatternGridSpacing,
                PatternGridSpacing);
            grid.padding = new RectOffset(
                Mathf.RoundToInt(indent),
                Mathf.RoundToInt(rightPadding),
                2,
                2);
            grid.startCorner = GridLayoutGroup.Corner.UpperLeft;
            grid.startAxis = GridLayoutGroup.Axis.Horizontal;
            grid.childAlignment = TextAnchor.UpperLeft;
            grid.constraint = GridLayoutGroup.Constraint.Flexible;
            LayoutElement layout = gridObject.GetComponent<LayoutElement>();
            layout.flexibleWidth = 1f;
            NoteSaveResponsiveGrid responsive =
                gridObject.GetComponent<NoteSaveResponsiveGrid>();
            responsive.Configure(
                paths.Count,
                PatternCardHeight,
                PatternGridSpacing);

            foreach (string path in paths)
            {
                AddPatternCard(
                    gridRect,
                    path,
                    PatternCardWidth,
                    PatternCardHeight,
                    depth);
            }
            CreatePatternGuideOverlay(
                content,
                gridRect,
                depth);
        }

        private static void AddPatternCard(
            RectTransform parent,
            string path,
            float cardWidth,
            float cardHeight,
            int depth)
        {
            GameObject card = new GameObject(
                "PatternCard_" + Path.GetFileNameWithoutExtension(path),
                typeof(RectTransform),
                typeof(CanvasRenderer),
                typeof(Image),
                typeof(LayoutElement));
            RectTransform cardRect = card.GetComponent<RectTransform>();
            cardRect.SetParent(parent, false);
            LayoutElement cardElement = card.GetComponent<LayoutElement>();
            cardElement.preferredWidth = cardWidth;
            cardElement.minWidth = cardWidth;
            cardElement.preferredHeight = cardHeight;
            cardElement.minHeight = cardHeight;
            Image cardImage = card.GetComponent<Image>();
            cardImage.color = Color.clear;
            cardImage.raycastTarget = true;

            GameObject trackObject = new GameObject(
                "TrackPreview",
                typeof(RectTransform),
                typeof(CanvasRenderer),
                typeof(RectMask2D));
            RectTransform trackRect = trackObject.GetComponent<RectTransform>();
            trackRect.SetParent(cardRect, false);
            trackRect.anchorMin = new Vector2(0f, 0f);
            trackRect.anchorMax = new Vector2(1f, 1f);
            trackRect.offsetMin = new Vector2(4f, 28f);
            trackRect.offsetMax = new Vector2(-4f, -4f);

            NotePatternFile pattern = null;
            try
            {
                pattern = NoteSaveFeature.ReadPattern(path);
            }
            catch
            {
            }
            AddNativeTrackPreview(
                trackRect,
                pattern,
                path,
                cardWidth - 8f,
                cardHeight - 30f);

            GameObject nameObject = new GameObject(
                "PatternName",
                typeof(RectTransform),
                typeof(CanvasRenderer),
                typeof(TextMeshProUGUI));
            RectTransform nameRect = nameObject.GetComponent<RectTransform>();
            nameRect.SetParent(cardRect, false);
            nameRect.anchorMin = new Vector2(0f, 0f);
            nameRect.anchorMax = new Vector2(1f, 0f);
            nameRect.pivot = new Vector2(0.5f, 0f);
            nameRect.offsetMin = new Vector2(4f, 3f);
            nameRect.offsetMax = new Vector2(-34f, 27f);
            TextMeshProUGUI nameText = nameObject.GetComponent<TextMeshProUGUI>();
            nameText.font =
                NoteSaveNativeUI.Panel == null
                    || NoteSaveNativeUI.Panel.inspectorPanel == null
                    ? null
                    : NoteSaveNativeUI.Panel.inspectorPanel.title.font;
            nameText.fontSize = 13f;
            nameText.alignment = TextAlignmentOptions.Center;
            nameText.textWrappingMode = TextWrappingModes.NoWrap;
            nameText.overflowMode = TextOverflowModes.Truncate;
            nameText.color = new Color(1f, 1f, 1f, 0.72f);
            nameText.raycastTarget = false;
            nameText.text = Path.GetFileNameWithoutExtension(path);

            string relative = NoteSaveFeature.ToRelativePath(path);
            NoteSaveTreeEntry entry =
                card.AddComponent<NoteSaveTreeEntry>();
            entry.Configure(
                false,
                path,
                relative,
                delegate
                {
                    NoteSaveFeature.SelectPattern(relative);
                    RefreshSelection();
                },
                delegate
                {
                    NoteSaveFeature.SelectPattern(relative);
                    NoteSaveFeature.BeginRenamePattern(path);
                },
                delegate(PointerEventData eventData)
                {
                    NoteSaveFeature.SelectPattern(relative);
                    RefreshSelection();
                    ShowPatternContextMenu(eventData.position, path, relative);
                });
            entry.SetCardVisual(nameText, cardImage);
            entry.SetSelected(
                NoteSaveFeature.SelectedPatternPath == relative);
            AddPinnedMarker(
                cardRect,
                path,
                new Vector2(0f, 1f),
                new Vector2(5f, -5f),
                new Vector2(11f, 11f));

            if (scnEditor.instance != null)
            {
                InspectorPanel inspector = scnEditor.instance.levelEventsPanel
                    ?? scnEditor.instance.settingsPanel;
                Sprite deleteSprite = inspector != null
                    && inspector.deleteEventButton != null
                    && inspector.deleteEventButton.image != null
                    ? inspector.deleteEventButton.image.sprite
                    : null;
                Sprite previewSprite = inspector == null
                    ? null
                    : inspector.visibleIcon;
                AddCardActionButton(
                    cardRect,
                    deleteSprite ?? Resources.Load<Sprite>("delete_event2"),
                    new Vector2(1f, 1f),
                    new Vector2(-4f, -4f),
                    delegate { NoteSaveFeature.BeginDelete(path, false); });
                AddCardActionButton(
                    cardRect,
                    previewSprite ?? Resources.Load<Sprite>("eye"),
                    new Vector2(1f, 0f),
                    new Vector2(-4f, 4f),
                    delegate { NoteSaveFeature.OpenPreview(relative); });
            }
        }

        private static void AddCardActionButton(
            RectTransform card,
            Sprite sprite,
            Vector2 anchor,
            Vector2 position,
            Action action)
        {
            GameObject buttonObject = new GameObject(
                action.Method.Name.Contains("Delete")
                    ? "DeleteButton"
                    : "PreviewButton",
                typeof(RectTransform),
                typeof(CanvasRenderer),
                typeof(Image),
                typeof(Button));
            buttonObject.transform.SetParent(card, false);
            RectTransform rect = buttonObject.GetComponent<RectTransform>();
            rect.anchorMin = anchor;
            rect.anchorMax = anchor;
            rect.pivot = anchor;
            rect.anchoredPosition = position;
            rect.sizeDelta = new Vector2(32f, 32f);
            Image image = buttonObject.GetComponent<Image>();
            image.sprite = sprite;
            image.color = Color.white;
            image.preserveAspect = true;
            image.raycastTarget = true;
            if (sprite == null)
            {
                GameObject fallbackObject = new GameObject(
                    "FallbackGlyph",
                    typeof(RectTransform),
                    typeof(CanvasRenderer),
                    typeof(TextMeshProUGUI));
                RectTransform fallbackRect =
                    fallbackObject.GetComponent<RectTransform>();
                fallbackRect.SetParent(rect, false);
                fallbackRect.anchorMin = Vector2.zero;
                fallbackRect.anchorMax = Vector2.one;
                fallbackRect.offsetMin = Vector2.zero;
                fallbackRect.offsetMax = Vector2.zero;
                TextMeshProUGUI glyph =
                    fallbackObject.GetComponent<TextMeshProUGUI>();
                glyph.font =
                    NoteSaveNativeUI.Panel == null
                        || NoteSaveNativeUI.Panel.inspectorPanel == null
                        ? null
                        : NoteSaveNativeUI.Panel.inspectorPanel.title.font;
                glyph.fontSize = 24f;
                glyph.alignment = TextAlignmentOptions.Center;
                glyph.color = Color.white;
                glyph.raycastTarget = false;
                glyph.text = anchor.y > 0.5f ? "×" : "◉";
            }

            Button button = buttonObject.GetComponent<Button>();
            button.targetGraphic = image;
            ColorBlock colors = button.colors;
            colors.normalColor = Color.white;
            colors.highlightedColor = new Color(0.82f, 0.82f, 0.82f, 1f);
            colors.pressedColor = new Color(0.65f, 0.65f, 0.65f, 1f);
            colors.selectedColor = Color.white;
            button.colors = colors;
            if (buttonObject.GetComponent<CanvasGroup>() == null)
            {
                buttonObject.AddComponent<CanvasGroup>();
            }
            button.onClick.RemoveAllListeners();
            button.onClick.AddListener(delegate { action(); });
            buttonObject.SetActive(true);
        }

        private static void AddNativeTrackPreview(
            RectTransform container,
            NotePatternFile pattern,
            string patternPath,
            float width,
            float height)
        {
            if (pattern == null || pattern.floors.Count == 0)
            {
                return;
            }

            RenderTexture rendered = GetRenderedPatternThumbnail(
                patternPath,
                pattern);
            if (rendered != null)
            {
                GameObject imageObject = new GameObject(
                    "NativeTrackPreview",
                    typeof(RectTransform),
                    typeof(CanvasRenderer),
                    typeof(RawImage));
                RectTransform imageRect =
                    imageObject.GetComponent<RectTransform>();
                imageRect.SetParent(container, false);
                imageRect.anchorMin = Vector2.zero;
                imageRect.anchorMax = Vector2.one;
                imageRect.offsetMin = Vector2.zero;
                imageRect.offsetMax = Vector2.zero;
                RawImage image = imageObject.GetComponent<RawImage>();
                image.texture = rendered;
                image.color = Color.white;
                image.raycastTarget = false;
                return;
            }

            AddFallbackTrackPreview(
                container,
                pattern,
                width,
                height);
        }

        private static RenderTexture GetRenderedPatternThumbnail(
            string patternPath,
            NotePatternFile pattern)
        {
            long ticks = !string.IsNullOrEmpty(patternPath)
                && File.Exists(patternPath)
                ? File.GetLastWriteTimeUtc(patternPath).Ticks
                : 0;
            ThumbnailCacheEntry cached;
            if (!string.IsNullOrEmpty(patternPath)
                && thumbnailCache.TryGetValue(patternPath, out cached)
                && cached.FileTicks == ticks
                && cached.Texture != null)
            {
                return cached.Texture;
            }

            RenderTexture texture = RenderActualPatternThumbnail(
                pattern);
            if (texture == null || string.IsNullOrEmpty(patternPath))
            {
                return texture;
            }
            if (thumbnailCache.Count >= 128)
            {
                string firstKey = thumbnailCache.Keys.FirstOrDefault();
                if (firstKey != null)
                {
                    ThumbnailCacheEntry removed =
                        thumbnailCache[firstKey];
                    thumbnailCache.Remove(firstKey);
                    if (removed.Texture != null)
                    {
                        UnityEngine.Object.Destroy(removed.Texture);
                    }
                }
            }
            thumbnailCache[patternPath] = new ThumbnailCacheEntry
            {
                FileTicks = ticks,
                Texture = texture
            };
            return texture;
        }

        private static RenderTexture RenderActualPatternThumbnail(
            NotePatternFile pattern)
        {
            if (pattern == null
                || pattern.floors.Count == 0
                || ADOBase.lm == null
                || ADOBase.lm.spriteFloor == null
                && ADOBase.lm.meshFloor == null
                || scrController.instance == null)
            {
                return null;
            }

            GameObject root = null;
            RenderTexture renderTexture = null;
            RenderTexture previousActive = RenderTexture.active;
            try
            {
                root = new GameObject("AngleThumbnailRig");
                Vector3 renderOrigin =
                    new Vector3(100000f, 100000f, 0f);
                root.transform.position = renderOrigin;
                const int floorLayer = 31;
                float tileSize = scrController.instance.tileSize;
                List<Vector3> points = new List<Vector3>();
                List<double> exitAngles = new List<double>();
                Vector3 cursor = Vector3.zero;
                double previousEntry = Math.PI * 1.5;
                points.Add(cursor);
                foreach (NoteFloorSave saved in pattern.floors)
                {
                    double exitAngle = saved.floatDirection == 999f
                        ? previousEntry
                        : (-saved.floatDirection + 90f)
                            * Mathf.Deg2Rad;
                    cursor += scrMisc.getVectorFromAngle(
                        exitAngle,
                        tileSize);
                    points.Add(cursor);
                    exitAngles.Add(exitAngle);
                    previousEntry =
                        (exitAngle + Math.PI) % (Math.PI * 2.0);
                }

                List<scrFloor> floors = new List<scrFloor>();
                GameObject floorPrefab =
                    ADOBase.lm.meshFloor != null
                        ? ADOBase.lm.meshFloor
                        : ADOBase.lm.spriteFloor;
                for (int index = 0; index < points.Count; index++)
                {
                    GameObject floorObject = UnityEngine.Object.Instantiate(
                        floorPrefab,
                        root.transform,
                        false);
                    floorObject.name = "ThumbnailFloor_" + index;
                    floorObject.SetActive(true);
                    scrFloor floor = floorObject.GetComponent<scrFloor>();
                    floor.seqID = index;
                    floor.transform.localPosition = points[index];
                    floor.startPos = points[index];
                    floor.entryangle = index == 0
                        ? Math.PI * 1.5
                        : (exitAngles[index - 1] + Math.PI)
                            % (Math.PI * 2.0);
                    floor.floatDirection = index == 0
                        ? -999f
                        : pattern.floors[index - 1].floatDirection;
                    floor.stringDirection = index == 0
                        ? 'S'
                        : pattern.floors[index - 1]
                            .stringDirection.FirstOrDefault();
                    floor.isCCW = false;
                    floor.speed = 1f;
                    floor.floorRenderer.renderer.enabled = true;
                    floor.floorRenderer.renderer.sortingLayerName =
                        "Floor";
                    floor.floorRenderer.renderer.sortingOrder =
                        1000 + index;
                    floor.SetTileColor(index == 0
                        ? Color.white
                        : GetPatternFloorColor(
                            pattern.floors[index - 1],
                            Color.white));
                    SetLayerRecursively(floorObject, floorLayer);
                    floors.Add(floor);
                }

                for (int index = 0;
                    index < pattern.floors.Count;
                    index++)
                {
                    scrFloor previous = floors[index];
                    scrFloor next = floors[index + 1];
                    previous.nextfloor = next;
                    next.prevfloor = previous;
                    previous.exitangle = exitAngles[index];
                    if (pattern.floors[index].floatDirection == 999f)
                    {
                        previous.midSpin = true;
                    }
                }
                scrFloor last = floors[floors.Count - 1];
                last.exitangle = last.entryangle + Math.PI;
                if (floors.Count > 0)
                {
                    floors[0].gameObject.SetActive(false);
                }
                for (int index = 1; index < floors.Count; index++)
                {
                    floors[index].UpdateAngle();
                    FloorMeshRenderer meshRenderer =
                        floors[index].floorRenderer
                            as FloorMeshRenderer;
                    if (meshRenderer != null
                        && meshRenderer.floorMesh != null)
                    {
                        meshRenderer.floorMesh.UpdateMesh();
                        meshRenderer.floorMesh.GenerateCollider();
                    }
                    if (floors[index].floorRenderer != null
                        && floors[index].floorRenderer.renderer != null)
                    {
                        floors[index].floorRenderer.renderer.enabled =
                            true;
                    }
                    SetLayerRecursively(
                        floors[index].gameObject,
                        floorLayer);
                }
                last.isportal = false;
                if (last.iconsprite != null)
                {
                    last.iconsprite.enabled = false;
                }

                AddThumbnailEventIcons(root.transform, floors, pattern);
                bool hasVisibleBounds = false;
                Bounds visibleBounds = default(Bounds);
                for (int index = 1; index < floors.Count; index++)
                {
                    Renderer renderer =
                        floors[index].floorRenderer == null
                            ? null
                            : floors[index].floorRenderer.renderer;
                    if (renderer == null || !renderer.enabled)
                    {
                        continue;
                    }
                    if (!hasVisibleBounds)
                    {
                        visibleBounds = renderer.bounds;
                        hasVisibleBounds = true;
                    }
                    else
                    {
                        visibleBounds.Encapsulate(renderer.bounds);
                    }
                }
                foreach (SpriteRenderer icon in
                    root.GetComponentsInChildren<SpriteRenderer>(true))
                {
                    if (icon == null
                        || icon.name != "ThumbnailEventIcon"
                        || !icon.enabled)
                    {
                        continue;
                    }
                    if (!hasVisibleBounds)
                    {
                        visibleBounds = icon.bounds;
                        hasVisibleBounds = true;
                    }
                    else
                    {
                        visibleBounds.Encapsulate(icon.bounds);
                    }
                }

                Vector3 center;
                float orthographicSize;
                if (hasVisibleBounds)
                {
                    center = visibleBounds.center - renderOrigin;
                    orthographicSize = Mathf.Max(
                        visibleBounds.extents.x,
                        visibleBounds.extents.y) + tileSize * 0.2f;
                }
                else
                {
                    float minX = points.Min(point => point.x);
                    float maxX = points.Max(point => point.x);
                    float minY = points.Min(point => point.y);
                    float maxY = points.Max(point => point.y);
                    center = new Vector3(
                        (minX + maxX) * 0.5f,
                        (minY + maxY) * 0.5f,
                        0f);
                    orthographicSize =
                        Mathf.Max(maxX - minX, maxY - minY) * 0.5f
                        + tileSize * 0.8f;
                }

                GameObject cameraObject = new GameObject(
                    "AngleThumbnailCamera",
                    typeof(Camera));
                cameraObject.transform.SetParent(root.transform, false);
                Camera camera = cameraObject.GetComponent<Camera>();
                camera.transform.position = new Vector3(
                    renderOrigin.x + center.x,
                    renderOrigin.y + center.y,
                    -20f);
                camera.orthographic = true;
                camera.orthographicSize =
                    Mathf.Max(tileSize, orthographicSize);
                camera.aspect = 1f;
                camera.clearFlags = CameraClearFlags.SolidColor;
                camera.backgroundColor = Color.clear;
                camera.cullingMask = 1 << floorLayer;
                camera.allowHDR = false;
                camera.allowMSAA = true;

                const int size = 256;
                renderTexture = new RenderTexture(
                    size,
                    size,
                    16,
                    RenderTextureFormat.ARGB32);
                renderTexture.filterMode = FilterMode.Bilinear;
                camera.targetTexture = renderTexture;
                camera.Render();
                renderTexture.wrapMode = TextureWrapMode.Clamp;
                camera.targetTexture = null;
                return renderTexture;
            }
            catch (Exception exception)
            {
                Debug.LogException(exception);
                if (renderTexture != null)
                {
                    renderTexture.Release();
                    UnityEngine.Object.Destroy(renderTexture);
                    renderTexture = null;
                }
                return null;
            }
            finally
            {
                RenderTexture.active = previousActive;
                if (root != null)
                {
                    UnityEngine.Object.DestroyImmediate(root);
                }
            }
        }

        private static void AddThumbnailEventIcons(
            Transform parent,
            List<scrFloor> floors,
            NotePatternFile pattern)
        {
            if (GCS.levelEventIcons == null)
            {
                return;
            }
            float tileSize = scrController.instance.tileSize;
            for (int index = 0;
                index < pattern.floors.Count
                && index + 1 < floors.Count;
                index++)
            {
                List<LevelEvent> events =
                    NoteSaveFeature.DecodeEvents(
                        pattern.floors[index].levelEventData)
                    .Where(levelEvent =>
                        levelEvent != null
                        && levelEvent.active
                        && levelEvent.visible)
                    .ToList();
                for (int eventIndex = 0;
                    eventIndex < events.Count;
                    eventIndex++)
                {
                    Sprite sprite;
                    Color tint;
                    if (!TryGetThumbnailEventIcon(
                        events[eventIndex],
                        floors[index + 1],
                        out sprite,
                        out tint))
                    {
                        continue;
                    }
                    GameObject iconObject = new GameObject(
                        "ThumbnailEventIcon",
                        typeof(SpriteRenderer));
                    iconObject.transform.SetParent(parent, false);
                    iconObject.layer = 31;
                    iconObject.transform.position =
                        floors[index + 1].transform.position
                        + new Vector3(
                            (eventIndex
                                - (events.Count - 1) * 0.5f)
                                * tileSize * 0.32f,
                            0f,
                            0f);
                    SpriteRenderer icon =
                        iconObject.GetComponent<SpriteRenderer>();
                    icon.sprite = sprite;
                    icon.color = tint;
                    icon.sortingLayerName = "Floor";
                    icon.sortingOrder = 3000 + index;
                    float spriteSize = Mathf.Max(
                        sprite.bounds.size.x,
                        sprite.bounds.size.y);
                    float scale = spriteSize <= 0.0001f
                        ? 1f
                        : tileSize * 0.62f / spriteSize;
                    iconObject.transform.localScale =
                        Vector3.one * scale;
                }
            }
        }

        private static bool TryGetThumbnailEventIcon(
            LevelEvent levelEvent,
            scrFloor floor,
            out Sprite sprite,
            out Color tint)
        {
            sprite = null;
            tint = Color.white;
            Color genericTint = scnEditor.instance == null
                ? new Color(0.72f, 0.3f, 1f, 1f)
                : scnEditor.instance.vfxIconColor;
            switch (levelEvent.eventType)
            {
                case LevelEventType.Twirl:
                    float moved = Mathf.Abs(Mathf.DeltaAngle(
                        (float)(floor.entryangle * Mathf.Rad2Deg),
                        (float)(floor.exitangle * Mathf.Rad2Deg)));
                    sprite = moved < 180f
                        ? ADOBase.gc.sprIconSwirlRed
                        : ADOBase.gc.sprIconSwirlBlue;
                    return sprite != null;
                case LevelEventType.SetSpeed:
                    float multiplier =
                        levelEvent.GetFloat("bpmMultiplier");
                    if (multiplier > 1.05f)
                    {
                        sprite = multiplier >= 4f
                            ? ADOBase.gc.sprIconDoubleRabbit
                            : ADOBase.gc.sprIconRabbit;
                    }
                    else if (multiplier > 0f
                        && multiplier < 0.95f)
                    {
                        sprite = multiplier <= 0.25f
                            ? ADOBase.gc.sprIconDoubleSnail
                            : ADOBase.gc.sprIconSnail;
                    }
                    else
                    {
                        sprite = ADOBase.gc.sprIconSameSpeed;
                    }
                    return sprite != null;
                case LevelEventType.Hold:
                    sprite = ADOBase.gc.sprIconHoldArrowShort;
                    return sprite != null;
                default:
                    tint = genericTint;
                    return GCS.levelEventIcons.TryGetValue(
                            levelEvent.eventType,
                            out sprite)
                        && sprite != null;
            }
        }

        private static void SetLayerRecursively(
            GameObject target,
            int layer)
        {
            if (target == null)
            {
                return;
            }
            target.layer = layer;
            foreach (Transform child in target.transform)
            {
                SetLayerRecursively(child.gameObject, layer);
            }
        }

        private static Color GetPatternFloorColor(
            NoteFloorSave floor,
            Color fallback)
        {
            if (floor == null || string.IsNullOrEmpty(floor.trackColor))
            {
                return fallback;
            }
            string value = floor.trackColor[0] == '#'
                ? floor.trackColor
                : "#" + floor.trackColor;
            Color color;
            return ColorUtility.TryParseHtmlString(value, out color)
                ? color
                : fallback;
        }

        private static void AddFallbackTrackPreview(
            RectTransform container,
            NotePatternFile pattern,
            float width,
            float height)
        {
            List<Vector2> points = new List<Vector2>();
            Vector2 cursor = Vector2.zero;
            points.Add(cursor);
            foreach (NoteFloorSave floor in pattern.floors)
            {
                float radians = floor.floatDirection * Mathf.Deg2Rad;
                cursor += new Vector2(Mathf.Cos(radians), Mathf.Sin(radians));
                points.Add(cursor);
            }

            float minX = points.Min(point => point.x);
            float maxX = points.Max(point => point.x);
            float minY = points.Min(point => point.y);
            float maxY = points.Max(point => point.y);
            float spanX = Mathf.Max(0.001f, maxX - minX);
            float spanY = Mathf.Max(0.001f, maxY - minY);
            float scale = Mathf.Min(
                Mathf.Max(1f, width - 12f) / spanX,
                Mathf.Max(1f, height - 12f) / spanY);
            Vector2 center = new Vector2(
                (minX + maxX) * 0.5f,
                (minY + maxY) * 0.5f);

            Texture tileTexture = ADOBase.gc == null
                ? null
                : ADOBase.gc.tex_floorTileDefault;
            if (tileTexture == null)
            {
                tileTexture = Texture2D.whiteTexture;
            }
            float textureAspect = tileTexture.height
                / (float)Mathf.Max(1, tileTexture.width);
            for (int index = 1; index < points.Count; index++)
            {
                Vector2 start = points[index - 1];
                Vector2 end = points[index];
                Vector2 delta = end - start;
                float length = delta.magnitude;
                if (length < 0.0001f)
                {
                    continue;
                }

                GameObject tileObject = new GameObject(
                    "TrackTile",
                    typeof(RectTransform),
                    typeof(CanvasRenderer),
                    typeof(RawImage));
                RectTransform tileRect =
                    tileObject.GetComponent<RectTransform>();
                tileRect.SetParent(container, false);
                tileRect.anchorMin = new Vector2(0.5f, 0.5f);
                tileRect.anchorMax = new Vector2(0.5f, 0.5f);
                tileRect.pivot = new Vector2(0.5f, 0.5f);
                tileRect.anchoredPosition =
                    ((start + end) * 0.5f - center) * scale;
                float tileLength = Mathf.Max(6f, length * scale * 0.92f);
                float tileHeight = Mathf.Clamp(
                    tileLength * textureAspect,
                    4f,
                    Mathf.Max(8f, Mathf.Min(28f, scale * 0.8f)));
                tileRect.sizeDelta = new Vector2(tileLength, tileHeight);
                tileRect.localEulerAngles =
                    new Vector3(0f, 0f, Mathf.Atan2(delta.y, delta.x) * Mathf.Rad2Deg);

                RawImage tile = tileObject.GetComponent<RawImage>();
                tile.texture = tileTexture;
                tile.color = GetPatternFloorColor(
                    pattern.floors[index - 1],
                    Color.white);
                tile.raycastTarget = false;
            }
        }

        private static void RefreshSelection()
        {
            NoteSaveTreeEntry[] entries =
                UnityEngine.Object.FindObjectsByType<NoteSaveTreeEntry>(
                    FindObjectsSortMode.None);
            foreach (NoteSaveTreeEntry entry in entries)
            {
                bool selected = entry.IsFolder
                    ? NoteSaveFeature.CurrentFolder == entry.RelativePath
                    : NoteSaveFeature.SelectedPatternPath == entry.RelativePath;
                if (entry.IsFolder && entry.Button != null)
                {
                    ApplyTreeRowStyle(entry.Button, selected);
                }
                else
                {
                    entry.SetSelected(selected);
                }
            }
        }

        private static PropertyControl_Export AddRow(
            RectTransform content,
            string text,
            float height)
        {
            GameObject propertyObject = UnityEngine.Object.Instantiate(
                ADOBase.gc.prefab_property,
                content,
                false);
            propertyObject.name = "AngleTreeRow_" + Guid.NewGuid().ToString("N");
            LayoutElement layout = propertyObject.AddComponent<LayoutElement>();
            layout.preferredHeight = height;
            layout.minHeight = height;

            ADOFAI.Property property = propertyObject.GetComponent<ADOFAI.Property>();
            property.key = propertyObject.name;
            property.label.text = "";
            if (property.helpButton != null)
            {
                property.helpButton.transform.parent.gameObject.SetActive(false);
            }

            GameObject controlObject = UnityEngine.Object.Instantiate(
                ADOBase.gc.prefab_controlExport,
                property.controlContainer,
                false);
            PropertyControl_Export control =
                controlObject.GetComponent<PropertyControl_Export>();
            control.propertiesPanel = NoteSaveNativeUI.Panel;
            control.propertyTransform = property.GetComponent<RectTransform>();
            control.buttonText.text = text;
            control.exportButton.onClick.RemoveAllListeners();
            property.control = control;
            NoteSaveNativeUI.Panel.properties[property.key] = property;
            return control;
        }

        private static Button AddFolderTreeRow(
            RectTransform content,
            string text,
            int depth,
            bool expanded,
            Action toggle)
        {
            EnsureTreeSprites();
            GameObject rowObject = new GameObject(
                "FolderTreeRow_" + text,
                typeof(RectTransform),
                typeof(CanvasRenderer),
                typeof(Image),
                typeof(Button),
                typeof(LayoutElement));
            RectTransform rowRect = rowObject.GetComponent<RectTransform>();
            rowRect.SetParent(content, false);
            LayoutElement layout = rowObject.GetComponent<LayoutElement>();
            layout.preferredHeight = TreeRowHeight;
            layout.minHeight = TreeRowHeight;

            Image background = rowObject.GetComponent<Image>();
            background.color = Color.white;
            background.raycastTarget = true;
            Button rowButton = rowObject.GetComponent<Button>();
            rowButton.targetGraphic = background;
            rowButton.navigation = new Navigation { mode = Navigation.Mode.None };

            float indent = TreePadding + depth * TreeIndent;
            for (int level = 0; level < depth; level++)
            {
                AddTreeGuide(
                    rowRect,
                    TreePadding + level * TreeIndent,
                    TreeRowHeight);
            }
            AddTreeGuide(rowRect, indent, TreeRowHeight);

            GameObject chevronObject = new GameObject(
                "Chevron",
                typeof(RectTransform),
                typeof(CanvasRenderer),
                typeof(Image),
                typeof(Button));
            RectTransform chevronRect =
                chevronObject.GetComponent<RectTransform>();
            chevronRect.SetParent(rowRect, false);
            chevronRect.anchorMin = new Vector2(0f, 0.5f);
            chevronRect.anchorMax = new Vector2(0f, 0.5f);
            chevronRect.pivot = new Vector2(0.5f, 0.5f);
            chevronRect.anchoredPosition = new Vector2(indent + 7f, 0f);
            chevronRect.sizeDelta = new Vector2(12f, 12f);
            Image chevronImage = chevronObject.GetComponent<Image>();
            chevronImage.sprite = chevronRightSprite;
            chevronImage.color = new Color(1f, 1f, 1f, 0.85f);
            chevronImage.preserveAspect = true;
            chevronImage.raycastTarget = true;
            chevronRect.localEulerAngles = expanded
                ? new Vector3(0f, 0f, -90f)
                : Vector3.zero;
            Button chevronButton = chevronObject.GetComponent<Button>();
            chevronButton.targetGraphic = chevronImage;
            chevronButton.navigation =
                new Navigation { mode = Navigation.Mode.None };
            chevronButton.onClick.AddListener(delegate { toggle(); });

            GameObject textObject = new GameObject(
                "Name",
                typeof(RectTransform),
                typeof(CanvasRenderer),
                typeof(TextMeshProUGUI));
            RectTransform textRect = textObject.GetComponent<RectTransform>();
            textRect.SetParent(rowRect, false);
            textRect.anchorMin = new Vector2(0f, 0f);
            textRect.anchorMax = new Vector2(1f, 1f);
            textRect.offsetMin = new Vector2(indent + 20f, 0f);
            textRect.offsetMax = new Vector2(-TreePadding, 0f);
            TextMeshProUGUI label = textObject.GetComponent<TextMeshProUGUI>();
            label.font =
                NoteSaveNativeUI.Panel == null
                    || NoteSaveNativeUI.Panel.inspectorPanel == null
                    ? null
                    : NoteSaveNativeUI.Panel.inspectorPanel.title.font;
            label.fontSize = 16f;
            label.alignment = TextAlignmentOptions.MidlineLeft;
            label.color = new Color(1f, 1f, 1f, 0.88f);
            label.raycastTarget = false;
            label.text = text;
            return rowButton;
        }

        private static void AddTreeGuide(
            RectTransform row,
            float x,
            float height)
        {
            GameObject guideObject = new GameObject(
                "TreeGuide",
                typeof(RectTransform),
                typeof(CanvasRenderer),
                typeof(Image));
            RectTransform guideRect = guideObject.GetComponent<RectTransform>();
            guideRect.SetParent(row, false);
            guideRect.anchorMin = new Vector2(0f, 0.5f);
            guideRect.anchorMax = new Vector2(0f, 0.5f);
            guideRect.pivot = new Vector2(0.5f, 0.5f);
            guideRect.anchoredPosition = new Vector2(x, 0f);
            guideRect.sizeDelta = new Vector2(1f, height);
            Image guide = guideObject.GetComponent<Image>();
            guide.color = new Color(1f, 1f, 1f, 0.17f);
            guide.raycastTarget = false;
        }

        private static void CreatePatternGuideOverlay(
            RectTransform content,
            RectTransform target,
            int depth)
        {
            GameObject layerObject = new GameObject(
                "PatternTreeGuides",
                typeof(RectTransform),
                typeof(LayoutElement),
                typeof(NoteSavePatternGuideOverlay));
            LayoutElement layout = layerObject.GetComponent<LayoutElement>();
            layout.ignoreLayout = true;
            RectTransform layer = layerObject.GetComponent<RectTransform>();
            layer.SetParent(content, false);
            NoteSavePatternGuideOverlay overlay =
                layerObject.GetComponent<NoteSavePatternGuideOverlay>();
            overlay.Configure(target);
            layer.SetAsFirstSibling();

            for (int level = 0; level < Mathf.Max(1, depth); level++)
            {
                GameObject guideObject = new GameObject(
                    "PatternTreeGuide",
                    typeof(RectTransform),
                    typeof(CanvasRenderer),
                    typeof(Image));
                RectTransform guideRect =
                    guideObject.GetComponent<RectTransform>();
                guideRect.SetParent(layer, false);
                guideRect.anchorMin = new Vector2(0f, 0f);
                guideRect.anchorMax = new Vector2(0f, 1f);
                guideRect.pivot = new Vector2(0.5f, 0.5f);
                guideRect.anchoredPosition = new Vector2(
                    TreePadding + level * TreeIndent,
                    0f);
                guideRect.sizeDelta = new Vector2(1f, 0f);
                Image guide = guideObject.GetComponent<Image>();
                guide.color = new Color(1f, 1f, 1f, 0.15f);
                guide.raycastTarget = false;
            }
        }

        private static void EnsureTreeSprites()
        {
            if (chevronRightSprite == null)
            {
                chevronRightSprite = CreateChevronSprite();
            }
        }

        private static Sprite CreateChevronSprite()
        {
            const int size = 64;
            Texture2D texture = new Texture2D(
                size,
                size,
                TextureFormat.RGBA32,
                false);
            for (int y = 0; y < size; y++)
            {
                for (int x = 0; x < size; x++)
                {
                    int hits = 0;
                    const int samples = 4;
                    for (int sy = 0; sy < samples; sy++)
                    {
                        for (int sx = 0; sx < samples; sx++)
                        {
                            float px = x + (sx + 0.5f) / samples;
                            float py = y + (sy + 0.5f) / samples;
                            if (PointInTriangle(
                                px,
                                py,
                                new Vector2(13f, 11f),
                                new Vector2(13f, 53f),
                                new Vector2(53f, 32f)))
                            {
                                hits++;
                            }
                        }
                    }
                    float alpha = hits / (float)(samples * samples);
                    texture.SetPixel(
                        x,
                        y,
                        new Color(1f, 1f, 1f, alpha));
                }
            }
            texture.Apply();
            return Sprite.Create(
                texture,
                new Rect(0f, 0f, size, size),
                new Vector2(0.5f, 0.5f),
                size);
        }

        private static bool PointInTriangle(
            float px,
            float py,
            Vector2 a,
            Vector2 b,
            Vector2 c)
        {
            float d1 = (px - b.x) * (a.y - b.y)
                - (a.x - b.x) * (py - b.y);
            float d2 = (px - c.x) * (b.y - c.y)
                - (b.x - c.x) * (py - c.y);
            float d3 = (px - a.x) * (c.y - a.y)
                - (c.x - a.x) * (py - a.y);
            bool hasNegative = d1 < 0f || d2 < 0f || d3 < 0f;
            bool hasPositive = d1 > 0f || d2 > 0f || d3 > 0f;
            return !(hasNegative && hasPositive);
        }


        private static void AddExpansionButton(
            PropertyControl_Export row,
            string symbol,
            int depth,
            Action action)
        {
            GameObject buttonObject = UnityEngine.Object.Instantiate(
                ADOBase.gc.prefab_controlExport,
                row.exportButton.transform,
                false);
            RectTransform rect = buttonObject.GetComponent<RectTransform>();
            rect.anchorMin = new Vector2(0f, 0.5f);
            rect.anchorMax = new Vector2(0f, 0.5f);
            rect.pivot = new Vector2(0f, 0.5f);
            rect.anchoredPosition = new Vector2(6f + depth * 18f, 0f);
            rect.sizeDelta = new Vector2(30f, 30f);
            PropertyControl_Export control =
                buttonObject.GetComponent<PropertyControl_Export>();
            control.buttonText.text = symbol;
            control.buttonText.alignment = TextAlignmentOptions.Center;
            control.buttonText.color = Color.black;
            ColorBlock colors = control.exportButton.colors;
            colors.normalColor = Color.clear;
            colors.highlightedColor = new Color(0f, 0f, 0f, 0.08f);
            colors.pressedColor = new Color(0f, 0f, 0f, 0.16f);
            colors.selectedColor = Color.clear;
            control.exportButton.colors = colors;
            control.exportButton.onClick.RemoveAllListeners();
            control.exportButton.onClick.AddListener(delegate { action(); });
        }

        private static void ApplyTreeRowStyle(Button button, bool selected)
        {
            ColorBlock colors = button.colors;
            colors.normalColor = selected
                ? new Color(0.36f, 0.48f, 0.65f, 0.28f)
                : Color.clear;
            colors.highlightedColor = new Color(0.42f, 0.52f, 0.66f, 0.2f);
            colors.pressedColor = new Color(0.4f, 0.52f, 0.7f, 0.32f);
            colors.selectedColor = new Color(0.36f, 0.48f, 0.65f, 0.28f);
            colors.disabledColor = Color.clear;
            button.colors = colors;
            TMP_Text text = button.GetComponentInChildren<TMP_Text>(true);
            if (text != null)
            {
                text.color = new Color(
                    1f,
                    1f,
                    1f,
                    selected ? 1f : 0.82f);
            }
        }

        internal static void ShowBlankContextMenu(Vector2 screenPosition)
        {
            ShowContextMenu(
                screenPosition,
                new[]
                {
                    new ContextItem(
                        AngleMod.L("新建文件夹", "New Folder"),
                        delegate
                        {
                            NoteSaveFeature.BeginCreateFolder();
                        })
                });
        }

        private static void ShowFolderContextMenu(
            Vector2 screenPosition,
            string path,
            string relative)
        {
            List<ContextItem> items = new List<ContextItem>();
            items.Add(new ContextItem(
                IsPinned(path)
                    ? AngleMod.L("取消置顶", "Unpin")
                    : AngleMod.L("置顶", "Pin to top"),
                delegate { TogglePin(path); }));
            items.Add(new ContextItem(
                AngleMod.L("新建文件夹", "New Folder"),
                delegate
                {
                    NoteSaveFeature.SelectFolder(relative);
                    NoteSaveFeature.BeginCreateFolder();
                }));
            items.Add(new ContextItem(
                AngleMod.L("重命名", "Rename"),
                delegate { NoteSaveFeature.BeginRenameFolder(path); }));
            if (relative != "Default")
            {
                items.Add(new ContextItem(
                    AngleMod.L("删除", "Delete"),
                    delegate { NoteSaveFeature.BeginDelete(path, true); }));
            }
            ShowContextMenu(screenPosition, items);
        }

        private static void ShowPatternContextMenu(
            Vector2 screenPosition,
            string path,
            string relative)
        {
            ShowContextMenu(
                screenPosition,
                new[]
                {
                    new ContextItem(
                        IsPinned(path)
                            ? AngleMod.L("取消置顶", "Unpin")
                            : AngleMod.L("置顶", "Pin to top"),
                        delegate { TogglePin(path); }),
                    new ContextItem(
                        AngleMod.L("预览键型", "Preview Pattern"),
                        delegate { NoteSaveFeature.OpenPreview(relative); }),
                    new ContextItem(
                        AngleMod.L("重命名", "Rename"),
                        delegate
                        {
                            NoteSaveFeature.SelectPattern(relative);
                            NoteSaveFeature.BeginRenamePattern(path);
                        }),
                    new ContextItem(
                        AngleMod.L("删除", "Delete"),
                        delegate { NoteSaveFeature.BeginDelete(path, false); })
                });
        }

        private static void ShowContextMenu(
            Vector2 screenPosition,
            IEnumerable<ContextItem> sourceItems)
        {
            CloseContextMenu();
            List<ContextItem> items = sourceItems.ToList();
            scnEditor editor = scnEditor.instance;
            if (editor == null
                || editor.levelEditorCanvas == null
                || items.Count == 0)
            {
                return;
            }

            GameObject menuObject = new GameObject(
                "AngleNativeContextMenu",
                typeof(RectTransform),
                typeof(VerticalLayoutGroup));
            menuObject.name = "AngleNativeContextMenu";
            RectTransform menuRect = menuObject.GetComponent<RectTransform>();
            menuRect.SetParent(editor.levelEditorCanvas.transform, false);
            float height = items.Count * ContextRowHeight
                + Mathf.Max(0, items.Count - 1) * ContextRowSpacing
                + 4f;
            menuRect.anchorMin = new Vector2(0f, 1f);
            menuRect.anchorMax = new Vector2(0f, 1f);
            menuRect.pivot = new Vector2(0f, 1f);
            menuRect.sizeDelta = new Vector2(208f, height);

            VerticalLayoutGroup menuLayout =
                menuObject.GetComponent<VerticalLayoutGroup>();
            menuLayout.spacing = ContextRowSpacing;
            menuLayout.padding = new RectOffset(0, 0, 2, 2);
            menuLayout.childControlWidth = true;
            menuLayout.childControlHeight = true;
            menuLayout.childForceExpandWidth = true;
            menuLayout.childForceExpandHeight = false;

            RectTransform canvasRect = editor.levelEditorCanvas.GetComponent<RectTransform>();
            Camera eventCamera = editor.levelEditorCanvas.renderMode
                == RenderMode.ScreenSpaceOverlay
                ? null
                : editor.levelEditorCanvas.worldCamera;
            RectTransformUtility.ScreenPointToLocalPointInRectangle(
                canvasRect,
                screenPosition,
                eventCamera,
                out Vector2 localPoint);
            Rect canvasBounds = canvasRect.rect;
            Vector2 anchoredPosition = new Vector2(
                localPoint.x - canvasBounds.xMin + 8f,
                localPoint.y - canvasBounds.yMax - 8f);
            float maxX = canvasBounds.width - menuRect.sizeDelta.x - 4f;
            float minY = -(canvasBounds.height - menuRect.sizeDelta.y - 4f);
            anchoredPosition.x = Mathf.Clamp(anchoredPosition.x, 4f, maxX);
            anchoredPosition.y = Mathf.Clamp(anchoredPosition.y, minY, -4f);
            menuRect.anchoredPosition = anchoredPosition;

            foreach (ContextItem item in items)
            {
                AddContextRow(menuRect, item);
            }
            LayoutRebuilder.ForceRebuildLayoutImmediate(menuRect);

            NoteSaveContextDismiss dismiss =
                menuObject.AddComponent<NoteSaveContextDismiss>();
            dismiss.Configure(menuObject, menuRect, Time.frameCount);
            menuObject.transform.SetAsLastSibling();
            contextMenu = menuObject;
        }

        private static void AddContextRow(
            RectTransform menuRect,
            ContextItem item)
        {
            GameObject rowObject = UnityEngine.Object.Instantiate(
                ADOBase.gc.prefab_controlExport,
                menuRect,
                false);
            LayoutElement layout = rowObject.AddComponent<LayoutElement>();
            layout.preferredHeight = ContextRowHeight;
            layout.minHeight = ContextRowHeight;
            PropertyControl_Export row =
                rowObject.GetComponent<PropertyControl_Export>();
            row.buttonText.text = item.Text;
            row.buttonText.alignment = TextAlignmentOptions.Center;
            row.buttonText.rectTransform.offsetMin = Vector2.zero;
            row.buttonText.rectTransform.offsetMax = Vector2.zero;
            row.exportButton.onClick.RemoveAllListeners();
            row.exportButton.onClick.AddListener(delegate
            {
                CloseContextMenu();
                item.Action();
            });
        }

        internal static void CloseContextMenu()
        {
            if (contextMenu != null)
            {
                UnityEngine.Object.Destroy(contextMenu);
                contextMenu = null;
            }
        }

        internal static void ClearUiSelection()
        {
            if (EventSystem.current != null)
            {
                EventSystem.current.SetSelectedGameObject(null);
            }
        }

        internal static void CloseContextMenu(GameObject owner)
        {
            if (contextMenu != null && contextMenu == owner)
            {
                UnityEngine.Object.Destroy(contextMenu);
                contextMenu = null;
            }
        }

        internal static void BeginDrag(NoteSaveTreeEntry entry)
        {
            if (entry == null || entry.IsDefaultFolder)
            {
                return;
            }
            ResetDrag();
            draggedEntry = entry;
            CanvasGroup group = entry.GetComponent<CanvasGroup>();
            if (group == null)
            {
                group = entry.gameObject.AddComponent<CanvasGroup>();
            }
            group.alpha = 0.55f;
            CreateDragGhost(entry);
        }

        internal static void UpdateDrag(PointerEventData eventData)
        {
            if (draggedEntry == null)
            {
                return;
            }
            UpdateDragGhost(
                eventData.position,
                eventData.pressEventCamera);
            NoteSaveTreeEntry target = FindFolderAt(
                eventData.position,
                eventData.pressEventCamera);
            if (target == draggedEntry
                || !CanDropInto(draggedEntry, target))
            {
                target = null;
            }
            if (highlightedFolder == target)
            {
                return;
            }
            ClearHighlight();
            highlightedFolder = target;
            if (highlightedFolder != null)
            {
                ApplyTreeRowStyle(highlightedFolder.Button, true);
            }
        }

        internal static void EndDrag(PointerEventData eventData)
        {
            if (draggedEntry == null)
            {
                return;
            }
            NoteSaveTreeEntry source = draggedEntry;
            NoteSaveTreeEntry target = FindFolderAt(
                eventData.position,
                eventData.pressEventCamera);
            ResetDrag();
            if (source != null
                && target != null
                && target.IsFolder
                && CanDropInto(source, target))
            {
                NoteSaveFeature.MoveEntryToFolder(
                    source.FullPath,
                    target.FullPath);
            }
        }

        private static NoteSaveTreeEntry FindFolderAt(
            Vector2 screenPosition,
            Camera eventCamera)
        {
            NoteSaveTreeEntry[] entries =
                UnityEngine.Object.FindObjectsByType<NoteSaveTreeEntry>(
                    FindObjectsSortMode.None);
            for (int index = entries.Length - 1; index >= 0; index--)
            {
                NoteSaveTreeEntry entry = entries[index];
                if (entry == null
                    || !entry.IsFolder
                    || entry.transform is not RectTransform rect)
                {
                    continue;
                }
                if (RectTransformUtility.RectangleContainsScreenPoint(
                    rect,
                    screenPosition,
                    eventCamera))
                {
                    return entry;
                }
            }
            return null;
        }

        private static bool CanDropInto(
            NoteSaveTreeEntry source,
            NoteSaveTreeEntry target)
        {
            if (source == null || target == null || !target.IsFolder)
            {
                return false;
            }
            string sourcePath = Path.GetFullPath(source.FullPath)
                .TrimEnd(Path.DirectorySeparatorChar)
                + Path.DirectorySeparatorChar;
            string targetPath = Path.GetFullPath(target.FullPath);
            if (!source.IsFolder)
            {
                return !string.Equals(
                    Path.GetDirectoryName(source.FullPath),
                    target.FullPath,
                    StringComparison.OrdinalIgnoreCase);
            }
            if (string.Equals(
                Path.GetDirectoryName(source.FullPath),
                target.FullPath,
                StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }
            return !targetPath.StartsWith(
                sourcePath,
                StringComparison.OrdinalIgnoreCase)
                && !string.Equals(
                    source.FullPath,
                    target.FullPath,
                    StringComparison.OrdinalIgnoreCase);
        }

        private static void ClearHighlight()
        {
            if (highlightedFolder != null)
            {
                ApplyTreeRowStyle(
                    highlightedFolder.Button,
                    NoteSaveFeature.CurrentFolder == highlightedFolder.RelativePath);
                highlightedFolder = null;
            }
        }

        private static void CreateDragGhost(NoteSaveTreeEntry entry)
        {
            scnEditor editor = scnEditor.instance;
            if (editor == null || editor.levelEditorCanvas == null)
            {
                return;
            }
            if (dragGhostSprite == null)
            {
                dragGhostSprite = CreateDragGhostSprite();
            }
            dragGhost = new GameObject(
                "AnglePatternDragGhost",
                typeof(RectTransform),
                typeof(CanvasRenderer),
                typeof(Image),
                typeof(CanvasGroup));
            dragGhost.transform.SetParent(
                editor.levelEditorCanvas.transform,
                false);
            dragGhostRect = dragGhost.GetComponent<RectTransform>();
            dragGhostRect.anchorMin = new Vector2(0f, 1f);
            dragGhostRect.anchorMax = new Vector2(0f, 1f);
            dragGhostRect.pivot = new Vector2(0f, 1f);
            dragGhostRect.sizeDelta = entry.IsFolder
                ? new Vector2(140f, 28f)
                : new Vector2(176f, 42f);
            Image background = dragGhost.GetComponent<Image>();
            background.sprite = dragGhostSprite;
            background.type = Image.Type.Sliced;
            background.color = Color.white;
            background.raycastTarget = false;
            background.enabled = !entry.IsFolder;
            CanvasGroup group = dragGhost.GetComponent<CanvasGroup>();
            group.alpha = 0.68f;
            group.blocksRaycasts = false;
            group.interactable = false;

            GameObject textObject = new GameObject(
                "Name",
                typeof(RectTransform),
                typeof(CanvasRenderer),
                typeof(TextMeshProUGUI));
            RectTransform textRect = textObject.GetComponent<RectTransform>();
            textRect.SetParent(dragGhostRect, false);
            textRect.anchorMin = Vector2.zero;
            textRect.anchorMax = Vector2.one;
            textRect.offsetMin = entry.IsFolder
                ? Vector2.zero
                : new Vector2(12f, 0f);
            textRect.offsetMax = entry.IsFolder
                ? Vector2.zero
                : new Vector2(-12f, 0f);
            TextMeshProUGUI text = textObject.GetComponent<TextMeshProUGUI>();
            text.font =
                NoteSaveNativeUI.Panel == null
                    || NoteSaveNativeUI.Panel.inspectorPanel == null
                    ? null
                    : NoteSaveNativeUI.Panel.inspectorPanel.title.font;
            text.fontSize = 15f;
            text.alignment = entry.IsFolder
                ? TextAlignmentOptions.Center
                : TextAlignmentOptions.MidlineLeft;
            text.color = Color.white;
            text.raycastTarget = false;
            text.overflowMode = TextOverflowModes.Ellipsis;
            text.text = entry.IsFolder
                ? Path.GetFileName(entry.FullPath)
                : Path.GetFileNameWithoutExtension(entry.FullPath);
            dragGhost.transform.SetAsLastSibling();
        }

        private static void UpdateDragGhost(
            Vector2 screenPosition,
            Camera eventCamera)
        {
            scnEditor editor = scnEditor.instance;
            if (dragGhostRect == null
                || editor == null
                || editor.levelEditorCanvas == null)
            {
                return;
            }
            RectTransform canvasRect =
                editor.levelEditorCanvas.GetComponent<RectTransform>();
            Camera camera = eventCamera;
            if (editor.levelEditorCanvas.renderMode == RenderMode.ScreenSpaceOverlay)
            {
                camera = null;
            }
            RectTransformUtility.ScreenPointToLocalPointInRectangle(
                canvasRect,
                screenPosition,
                camera,
                out Vector2 local);
            Rect bounds = canvasRect.rect;
            dragGhostRect.anchoredPosition = new Vector2(
                local.x - bounds.xMin + 16f,
                local.y - bounds.yMax - 16f);
            dragGhost.transform.SetAsLastSibling();
        }

        private static Sprite CreateDragGhostSprite()
        {
            const int size = 64;
            Texture2D texture = new Texture2D(
                size,
                size,
                TextureFormat.RGBA32,
                false);
            for (int y = 0; y < size; y++)
            {
                for (int x = 0; x < size; x++)
                {
                    bool edge = x < 2
                        || y < 2
                        || x >= size - 2
                        || y >= size - 2;
                    bool corner = (x < 10 || x >= size - 10)
                        && (y < 10 || y >= size - 10)
                        && Vector2.Distance(
                            new Vector2(x, y),
                            new Vector2(
                                x < 10 ? 10 : size - 1 - 10,
                                y < 10 ? 10 : size - 1 - 10))
                            > 10f;
                    texture.SetPixel(
                        x,
                        y,
                        corner
                            ? Color.clear
                            : edge
                                ? new Color(0.9f, 0.93f, 0.97f, 0.9f)
                                : new Color(0.025f, 0.035f, 0.05f, 0.92f));
                }
            }
            texture.Apply();
            return Sprite.Create(
                texture,
                new Rect(0f, 0f, size, size),
                new Vector2(0.5f, 0.5f),
                100f,
                0,
                SpriteMeshType.FullRect,
                new Vector4(10f, 10f, 10f, 10f));
        }

        private static void ResetDrag()
        {
            ClearHighlight();
            if (draggedEntry != null)
            {
                CanvasGroup group = draggedEntry.GetComponent<CanvasGroup>();
                if (group != null)
                {
                    group.alpha = 1f;
                }
                draggedEntry = null;
            }
            if (dragGhost != null)
            {
                UnityEngine.Object.Destroy(dragGhost);
                dragGhost = null;
                dragGhostRect = null;
            }
        }

        private sealed class ContextItem
        {
            internal readonly string Text;
            internal readonly Action Action;

            internal ContextItem(string text, Action action)
            {
                Text = text;
                Action = action;
            }
        }
    }

    internal sealed class NoteSaveResponsiveGrid : MonoBehaviour
    {
        private GridLayoutGroup grid;
        private LayoutElement layout;
        private int childCount;
        private float cellHeight;
        private float spacing;
        private int lastColumns = -1;

        internal void Configure(
            int count,
            float targetCellHeight,
            float targetSpacing)
        {
            grid = GetComponent<GridLayoutGroup>();
            layout = GetComponent<LayoutElement>();
            childCount = count;
            cellHeight = targetCellHeight;
            spacing = targetSpacing;
            Refresh();
        }

        private void LateUpdate()
        {
            Refresh();
        }

        private void Refresh()
        {
            if (grid == null || layout == null)
            {
                return;
            }
            RectTransform rect = transform as RectTransform;
            float width = rect?.rect.width ?? 0f;
            RectTransform parent =
                rect == null ? null : rect.parent as RectTransform;
            float viewportWidth = NoteSaveNativeUI.Panel != null
                && NoteSaveNativeUI.Panel.viewport != null
                ? NoteSaveNativeUI.Panel.viewport.rect.width
                : 0f;
            if (viewportWidth > 1f)
            {
                width = viewportWidth;
            }
            else if (parent != null && parent.rect.width > 1f)
            {
                width = parent.rect.width;
            }
            if (width <= 1f)
            {
                return;
            }
            RectOffset padding = grid.padding;
            float usable = Mathf.Max(
                grid.cellSize.x,
                width - padding.left - padding.right);
            int columns = Mathf.Max(
                1,
                Mathf.FloorToInt(
                    (usable + spacing + 0.01f)
                    / (grid.cellSize.x + spacing)));
            columns = Mathf.Min(columns, Mathf.Max(1, childCount));
            grid.constraint = GridLayoutGroup.Constraint.FixedColumnCount;
            grid.constraintCount = columns;
            int rows = Mathf.Max(
                1,
                Mathf.CeilToInt(childCount / (float)columns));
            float height = padding.top
                + padding.bottom
                + rows * cellHeight
                + Mathf.Max(0, rows - 1) * spacing;
            layout.preferredHeight = height;
            layout.minHeight = height;
            layout.preferredWidth = width;
            layout.minWidth = width;
            layout.flexibleWidth = 1f;
            if (columns != lastColumns)
            {
                lastColumns = columns;
                LayoutRebuilder.MarkLayoutForRebuild(rect);
                if (parent != null)
                {
                    LayoutRebuilder.MarkLayoutForRebuild(parent);
                }
            }
        }
    }

    internal sealed class NoteSavePatternGuideOverlay : MonoBehaviour
    {
        private RectTransform target;

        internal void Configure(RectTransform targetRect)
        {
            target = targetRect;
        }

        private void LateUpdate()
        {
            if (target == null)
            {
                return;
            }
            RectTransform rect = transform as RectTransform;
            if (rect == null)
            {
                return;
            }
            rect.anchorMin = target.anchorMin;
            rect.anchorMax = target.anchorMax;
            rect.pivot = target.pivot;
            rect.anchoredPosition = target.anchoredPosition;
            rect.sizeDelta = target.sizeDelta;
            rect.localScale = target.localScale;
        }
    }

    internal sealed class NoteSaveTreeEntry :
        MonoBehaviour,
        IPointerClickHandler,
        IBeginDragHandler,
        IDragHandler,
        IEndDragHandler
    {
        internal bool IsFolder { get; private set; }
        internal string FullPath { get; private set; }
        internal string RelativePath { get; private set; }
        internal bool IsDefaultFolder =>
            IsFolder
            && string.Equals(RelativePath, "Default", StringComparison.OrdinalIgnoreCase);
        internal Button Button =>
            GetComponent<Button>();
        private TMP_Text cardLabel;
        private Image cardBackground;

        private Action primaryAction;
        private Action renameAction;
        private Action<PointerEventData> contextAction;

        internal void Configure(
            bool isFolder,
            string fullPath,
            string relativePath,
            Action primary,
            Action rename,
            Action<PointerEventData> context)
        {
            IsFolder = isFolder;
            FullPath = fullPath;
            RelativePath = relativePath;
            primaryAction = primary;
            renameAction = rename;
            contextAction = context;
        }

        internal void SetCardVisual(TMP_Text label, Image background)
        {
            cardLabel = label;
            cardBackground = background;
        }

        internal void SetSelected(bool selected)
        {
            if (cardLabel != null)
            {
                cardLabel.color = new Color(
                    1f,
                    1f,
                    1f,
                    selected ? 1f : 0.72f);
                cardLabel.fontStyle = selected
                    ? FontStyles.Bold
                    : FontStyles.Normal;
            }
            if (cardBackground != null)
            {
                cardBackground.color = selected
                    ? new Color(1f, 1f, 1f, 0.055f)
                    : Color.clear;
            }
        }

        public void OnPointerClick(PointerEventData eventData)
        {
            NoteSaveBrowser.ClearUiSelection();
            if (eventData.button == PointerEventData.InputButton.Right)
            {
                contextAction?.Invoke(eventData);
                return;
            }
            if (eventData.button != PointerEventData.InputButton.Left)
            {
                return;
            }
            if (eventData.clickCount >= 2 && renameAction != null)
            {
                renameAction();
            }
            else
            {
                primaryAction?.Invoke();
            }
        }

        public void OnBeginDrag(PointerEventData eventData)
        {
            if (eventData.button == PointerEventData.InputButton.Left)
            {
                NoteSaveBrowser.BeginDrag(this);
            }
        }

        public void OnDrag(PointerEventData eventData)
        {
            if (eventData.button == PointerEventData.InputButton.Left)
            {
                NoteSaveBrowser.UpdateDrag(eventData);
            }
        }

        public void OnEndDrag(PointerEventData eventData)
        {
            if (eventData.button == PointerEventData.InputButton.Left)
            {
                NoteSaveBrowser.EndDrag(eventData);
            }
        }
    }

    internal sealed class NoteSaveBlankArea : MonoBehaviour, IPointerClickHandler
    {
        public void OnPointerClick(PointerEventData eventData)
        {
            if (eventData.button == PointerEventData.InputButton.Right)
            {
                NoteSaveBrowser.ShowBlankContextMenu(eventData.position);
            }
            else if (eventData.button == PointerEventData.InputButton.Left)
            {
                NoteSaveBrowser.CloseContextMenu();
            }
        }
    }

    internal sealed class NoteSaveContextDismiss : MonoBehaviour
    {
        private RectTransform rect;
        private GameObject owner;
        private int openedFrame;

        internal void Configure(
            GameObject menuOwner,
            RectTransform target,
            int frame)
        {
            owner = menuOwner;
            rect = target;
            openedFrame = frame;
        }

        private void Update()
        {
            if (Time.frameCount <= openedFrame)
            {
                return;
            }
            if (Input.GetKeyDown(KeyCode.Escape))
            {
                NoteSaveBrowser.CloseContextMenu(owner);
                return;
            }
            if ((Input.GetMouseButtonDown(0) || Input.GetMouseButtonDown(1))
                && !RectTransformUtility.RectangleContainsScreenPoint(
                    rect,
                    Input.mousePosition,
                    null))
            {
                NoteSaveBrowser.CloseContextMenu(owner);
            }
        }
    }

    internal sealed class OverwriteTooltipTrigger :
        MonoBehaviour,
        IPointerEnterHandler,
        IPointerExitHandler
    {
        private RectTransform anchor;
        private float enteredAt;
        private bool hovering;
        private bool shown;

        internal void Configure(RectTransform target)
        {
            anchor = target;
        }

        public void OnPointerEnter(PointerEventData eventData)
        {
            hovering = true;
            shown = false;
            enteredAt = Time.unscaledTime;
        }

        public void OnPointerExit(PointerEventData eventData)
        {
            hovering = false;
            shown = false;
            NoteSaveBrowser.HideOverwriteTooltip();
        }

        private void Update()
        {
            if (hovering
                && !shown
                && Time.unscaledTime - enteredAt >= 0.7f)
            {
                shown = true;
                NoteSaveBrowser.ShowOverwriteTooltip(anchor);
            }
        }

        private void OnDisable()
        {
            hovering = false;
            shown = false;
            NoteSaveBrowser.HideOverwriteTooltip();
        }
    }
}
