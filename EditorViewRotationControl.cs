using System;
using System.Globalization;
using ADOFAI;
using ADOFAI.LevelEditor.Controls;
using TMPro;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;

namespace Angle
{
    internal static class EditorViewRotationControl
    {
        private const float MaximumAngle = 180f;

        private static scnEditor owner;
        private static GameObject root;
        private static Slider slider;
        private static TMP_InputField valueInput;
        private static Sprite nudgeButtonSprite;
        private static Sprite sliderHandleSprite;
        private static bool updating;
        private static bool sliderDragging;
        private static float currentValue;

        internal static Transform RootTransform => root == null ? null : root.transform;

        internal static void Update()
        {
            scnEditor editor = scnEditor.instance;
            if (editor == null || editor.levelEditorCanvas == null)
            {
                Destroy();
                return;
            }
            Ensure(editor);
            if (root == null)
            {
                return;
            }

            bool visible = !editor.playMode;
            if (root.activeSelf != visible)
            {
                root.SetActive(visible);
            }
            if (visible
                && !sliderDragging
                && editor.camera != null
                && !(valueInput != null && valueInput.isFocused))
            {
                SyncFromCamera(editor, force: false);
            }
        }

        internal static void SyncFromCamera(scnEditor editor, bool force)
        {
            if (editor == null || editor.camera == null)
            {
                return;
            }
            Ensure(editor);
            if (slider == null)
            {
                return;
            }

            float actual = editor.camera.transform.localEulerAngles.z;
            float target = ClosestEquivalent(actual, currentValue);
            if (!force && Mathf.Abs(target - currentValue) < 0.05f)
            {
                return;
            }
            currentValue = Mathf.Clamp(target, -MaximumAngle, MaximumAngle);
            SetSliderValue(currentValue);
        }

        internal static void ResetValue(float value)
        {
            currentValue = Mathf.Clamp(value, -MaximumAngle, MaximumAngle);
            SetSliderValue(currentValue);
        }

        internal static void Destroy()
        {
            if (root != null)
            {
                UnityEngine.Object.Destroy(root);
            }
            owner = null;
            root = null;
            slider = null;
            valueInput = null;
            sliderDragging = false;
        }

        private static void Ensure(scnEditor editor)
        {
            if (root != null && owner == editor)
            {
                return;
            }
            Destroy();
            owner = editor;
            Build(editor);
        }

        private static void Build(scnEditor editor)
        {
            float canvasScale = Mathf.Max(
                0.0001f,
                editor.levelEditorCanvas.scaleFactor);
            float sliderHeight = Screen.height * 0.5f / canvasScale;
            root = new GameObject(
                "AngleViewRotationControl",
                typeof(RectTransform));
            RectTransform rootRect = root.GetComponent<RectTransform>();
            rootRect.SetParent(editor.levelEditorCanvas.transform, false);
            rootRect.anchorMin = new Vector2(1f, 0.5f);
            rootRect.anchorMax = new Vector2(1f, 0.5f);
            rootRect.pivot = new Vector2(1f, 0.5f);
            rootRect.anchoredPosition = Vector2.zero;
            rootRect.sizeDelta = new Vector2(110f, sliderHeight + 58f);

            BuildSlider(editor, rootRect, sliderHeight);
            BuildValueInput(editor, rootRect);
            BuildNudgeButton(rootRect, "-", -1f, sliderHeight);
            BuildNudgeButton(rootRect, "+", 1f, sliderHeight);
            root.transform.SetAsLastSibling();
        }

        private static void BuildSlider(
            scnEditor editor,
            RectTransform rootRect,
            float height)
        {
            if (sliderHandleSprite == null)
            {
                sliderHandleSprite = CreateCircleSprite();
            }

            float bodyHeight = Mathf.Max(24f, height);
            Sprite sliderTrackSprite = CreateCapsuleTrackSprite(bodyHeight);
            GameObject sliderObject = new GameObject(
                "ViewRotationSlider",
                typeof(RectTransform),
                typeof(CanvasRenderer),
                typeof(Image),
                typeof(Slider));
            RectTransform sliderRect = sliderObject.GetComponent<RectTransform>();
            sliderRect.SetParent(rootRect, false);
            sliderRect.anchorMin = new Vector2(0.5f, 0.5f);
            sliderRect.anchorMax = new Vector2(0.5f, 0.5f);
            sliderRect.pivot = new Vector2(0.5f, 0.5f);
            sliderRect.anchoredPosition = new Vector2(38f, 0f);
            sliderRect.sizeDelta = new Vector2(18f, bodyHeight);

            Image track = sliderObject.GetComponent<Image>();
            track.sprite = sliderTrackSprite;
            track.type = Image.Type.Simple;
            track.color = Color.white;
            track.raycastTarget = true;

            GameObject handleObject = new GameObject(
                "Handle",
                typeof(RectTransform),
                typeof(CanvasRenderer),
                typeof(Image));
            RectTransform handleRect = handleObject.GetComponent<RectTransform>();
            handleRect.SetParent(sliderRect, false);
            handleRect.anchorMin = new Vector2(0.5f, 0.5f);
            handleRect.anchorMax = new Vector2(0.5f, 0.5f);
            handleRect.pivot = new Vector2(0.5f, 0.5f);
            handleRect.sizeDelta = new Vector2(22f, 22f);
            Image handleImage = handleObject.GetComponent<Image>();
            handleImage.sprite = sliderHandleSprite;
            handleImage.color = Color.white;
            handleImage.preserveAspect = true;
            handleImage.raycastTarget = true;
            ViewRotationHandleShape shape =
                handleObject.AddComponent<ViewRotationHandleShape>();
            shape.Configure(handleRect, 22f);

            slider = sliderObject.GetComponent<Slider>();
            slider.direction = Slider.Direction.TopToBottom;
            slider.minValue = -MaximumAngle;
            slider.maxValue = MaximumAngle;
            slider.wholeNumbers = true;
            slider.value = 0f;
            slider.targetGraphic = track;
            GameObject handleAreaObject = new GameObject(
                "Handle Slide Area",
                typeof(RectTransform));
            RectTransform handleArea =
                handleAreaObject.GetComponent<RectTransform>();
            handleArea.SetParent(sliderRect, false);
            handleArea.anchorMin = Vector2.zero;
            handleArea.anchorMax = Vector2.one;
            handleArea.offsetMin = new Vector2(0f, 11f);
            handleArea.offsetMax = new Vector2(0f, -11f);
            handleRect.SetParent(handleArea, false);
            slider.handleRect = handleRect;
            slider.value = 0f;
            slider.navigation = new Navigation { mode = Navigation.Mode.None };
            slider.onValueChanged.RemoveAllListeners();
            slider.onValueChanged.AddListener(delegate(float value)
            {
                if (updating)
                {
                    return;
                }
                currentValue = Mathf.Clamp(
                    Mathf.Round(value),
                    -MaximumAngle,
                    MaximumAngle);
                UpdateInputText();
                AngleMod.SetManualCameraAngle(
                    scnEditor.instance,
                    currentValue);
            });

            ViewRotationSliderEvents events =
                sliderObject.GetComponent<ViewRotationSliderEvents>();
            if (events == null)
            {
                events = sliderObject.AddComponent<ViewRotationSliderEvents>();
            }
            events.OnDragStateChanged = delegate(bool dragging)
            {
                sliderDragging = dragging;
                if (!dragging)
                {
                    SyncFromCamera(scnEditor.instance, force: true);
                }
            };
        }

        private static void BuildValueInput(
            scnEditor editor,
            RectTransform rootRect)
        {
            GameObject inputPrefab = UnityEngine.Object.Instantiate(
                ADOBase.gc.prefab_controlText,
                rootRect,
                false);
            PropertyControl_Text control =
                inputPrefab.GetComponent<PropertyControl_Text>();
            valueInput = control == null ? null : control.inputField;
            if (valueInput == null)
            {
                UnityEngine.Object.Destroy(inputPrefab);
                return;
            }

            Transform inputTransform = valueInput.transform;
            if (inputTransform.gameObject != inputPrefab)
            {
                inputTransform.SetParent(rootRect, false);
                UnityEngine.Object.Destroy(inputPrefab);
            }
            RectTransform inputRect = inputTransform as RectTransform;
            if (inputRect != null)
            {
                inputRect.anchorMin = new Vector2(0.5f, 0.5f);
                inputRect.anchorMax = new Vector2(0.5f, 0.5f);
                inputRect.pivot = new Vector2(1f, 0.5f);
                inputRect.anchoredPosition = new Vector2(22f, 0f);
                inputRect.sizeDelta = new Vector2(58f, 24f);
            }
            valueInput.contentType = TMP_InputField.ContentType.Standard;
            valueInput.lineType = TMP_InputField.LineType.SingleLine;
            valueInput.characterLimit = 6;
            valueInput.onValueChanged.RemoveAllListeners();
            valueInput.onEndEdit.RemoveAllListeners();
            valueInput.onSubmit.RemoveAllListeners();
            valueInput.onEndEdit.AddListener(delegate { CommitInput(); });
            valueInput.onSubmit.AddListener(delegate { CommitInput(); });
            foreach (Image image in inputPrefab.GetComponentsInChildren<Image>(true))
            {
                image.color = new Color(1f, 1f, 1f, 0f);
                image.raycastTarget = true;
            }
            if (valueInput.textComponent != null)
            {
                if (valueInput.textViewport != null)
                {
                    valueInput.textViewport.offsetMin = Vector2.zero;
                    valueInput.textViewport.offsetMax = Vector2.zero;
                }
                valueInput.textComponent.fontSize = 16f;
                valueInput.textComponent.alignment = TextAlignmentOptions.Right;
                valueInput.textComponent.color = Color.white;
                valueInput.textComponent.margin = Vector4.zero;
                RectTransform textRect =
                    valueInput.textComponent.rectTransform;
                textRect.anchorMin = Vector2.zero;
                textRect.anchorMax = Vector2.one;
                textRect.offsetMin = Vector2.zero;
                textRect.offsetMax = new Vector2(-13f, 0f);
            }
            CreateDegreeLabel(inputRect);
            UpdateInputText();
        }

        private static void BuildNudgeButton(
            RectTransform rootRect,
            string text,
            float direction,
            float sliderHeight)
        {
            GameObject buttonObject = new GameObject(
                "ViewRotationNudge",
                typeof(RectTransform),
                typeof(CanvasRenderer),
                typeof(Image),
                typeof(Button));
            buttonObject.transform.SetParent(rootRect, false);
            RectTransform rect = buttonObject.GetComponent<RectTransform>();
            rect.anchorMin = new Vector2(0.5f, 0.5f);
            rect.anchorMax = new Vector2(0.5f, 0.5f);
            rect.pivot = new Vector2(0.5f, 0.5f);
            rect.anchoredPosition = new Vector2(
                38f,
                direction < 0f
                    ? sliderHeight * 0.5f + 16f
                    : -sliderHeight * 0.5f - 16f);
            rect.sizeDelta = new Vector2(20f, 20f);
            if (nudgeButtonSprite == null)
            {
                nudgeButtonSprite = CreateCircleSprite();
            }
            Image image = buttonObject.GetComponent<Image>();
            image.sprite = nudgeButtonSprite;
            image.color = Color.white;
            image.raycastTarget = true;
            Button button = buttonObject.GetComponent<Button>();
            button.targetGraphic = image;
            ColorBlock colors = button.colors;
            colors.normalColor = Color.white;
            colors.highlightedColor = new Color(0.82f, 0.82f, 0.82f, 1f);
            colors.pressedColor = new Color(0.65f, 0.65f, 0.65f, 1f);
            button.colors = colors;
            button.onClick.RemoveAllListeners();
            button.onClick.AddListener(delegate
            {
                Nudge(direction);
            });

            GameObject textObject = new GameObject(
                "Glyph",
                typeof(RectTransform),
                typeof(CanvasRenderer),
                typeof(TextMeshProUGUI));
            RectTransform textRect = textObject.GetComponent<RectTransform>();
            textRect.SetParent(rect, false);
            textRect.anchorMin = Vector2.zero;
            textRect.anchorMax = Vector2.one;
            textRect.offsetMin = Vector2.zero;
            textRect.offsetMax = Vector2.zero;
            TextMeshProUGUI glyph = textObject.GetComponent<TextMeshProUGUI>();
            glyph.font =
                NoteSaveNativeUI.Panel == null
                    || NoteSaveNativeUI.Panel.inspectorPanel == null
                    ? null
                    : NoteSaveNativeUI.Panel.inspectorPanel.title.font;
            glyph.fontSize = 17f;
            glyph.alignment = TextAlignmentOptions.Center;
            glyph.color = Color.white;
            glyph.raycastTarget = false;
            glyph.text = text;
        }

        private static void Nudge(float direction)
        {
            float value = Mathf.Clamp(
                currentValue + direction,
                -MaximumAngle,
                MaximumAngle);
            currentValue = value;
            SetSliderValue(value);
            AngleMod.SetManualCameraAngle(scnEditor.instance, value);
        }

        private static void CommitInput()
        {
            if (valueInput == null)
            {
                return;
            }
            string raw = valueInput.text.Replace("°", "").Trim();
            if (!float.TryParse(
                raw,
                NumberStyles.Float,
                CultureInfo.InvariantCulture,
                out float value))
            {
                UpdateInputText();
                return;
            }
            value = Mathf.Clamp(value, -MaximumAngle, MaximumAngle);
            currentValue = value;
            SetSliderValue(value);
            AngleMod.SetManualCameraAngle(scnEditor.instance, value);
        }

        private static void SetSliderValue(float value)
        {
            if (slider == null)
            {
                return;
            }
            updating = true;
            slider.SetValueWithoutNotify(value);
            updating = false;
            UpdateInputText();
        }

        private static void UpdateInputText()
        {
            if (valueInput != null)
            {
                float rounded = Mathf.Round(currentValue);
                valueInput.SetTextWithoutNotify(
                    rounded.ToString("0", CultureInfo.InvariantCulture));
            }
        }

        private static void CreateDegreeLabel(RectTransform inputRect)
        {
            if (inputRect == null)
            {
                return;
            }
            GameObject labelObject = new GameObject(
                "DegreeSymbol",
                typeof(RectTransform),
                typeof(CanvasRenderer),
                typeof(TextMeshProUGUI));
            RectTransform labelRect = labelObject.GetComponent<RectTransform>();
            labelRect.SetParent(inputRect, false);
            labelRect.anchorMin = new Vector2(1f, 0.5f);
            labelRect.anchorMax = new Vector2(1f, 0.5f);
            labelRect.pivot = new Vector2(1f, 0.5f);
            labelRect.anchoredPosition = Vector2.zero;
            labelRect.sizeDelta = new Vector2(8f, 22f);
            TextMeshProUGUI label =
                labelObject.GetComponent<TextMeshProUGUI>();
            label.font =
                NoteSaveNativeUI.Panel == null
                    || NoteSaveNativeUI.Panel.inspectorPanel == null
                    ? null
                    : NoteSaveNativeUI.Panel.inspectorPanel.title.font;
            label.fontSize = 16f;
            label.alignment = TextAlignmentOptions.Left;
            label.color = Color.white;
            label.raycastTarget = false;
            label.margin = Vector4.zero;
            label.text = "°";
        }

        private static Sprite CreateCapsuleTrackSprite(float uiHeight)
        {
            const float pixelsPerUnit = 4f;
            int width = Mathf.RoundToInt(18f * pixelsPerUnit);
            int height = Mathf.Max(
                width,
                Mathf.RoundToInt(uiHeight * pixelsPerUnit));
            float radius = width * 0.5f;
            Vector2 center = new Vector2(width * 0.5f, height * 0.5f);
            Texture2D texture = new Texture2D(
                width,
                height,
                TextureFormat.RGBA32,
                false);
            texture.name = "AngleViewRotationCapsule";
            for (int y = 0; y < height; y++)
            {
                for (int x = 0; x < width; x++)
                {
                    float px = Mathf.Abs(x + 0.5f - center.x);
                    float py = Mathf.Abs(y + 0.5f - center.y)
                        - Mathf.Max(0f, height * 0.5f - radius);
                    float signedDistance = py > 0f
                        ? Mathf.Sqrt(px * px + py * py) - radius
                        : px - radius;
                    if (signedDistance > 0f)
                    {
                        texture.SetPixel(x, y, Color.clear);
                        continue;
                    }

                    bool border = signedDistance > -3f;
                    texture.SetPixel(
                        x,
                        y,
                        border
                            ? new Color(1f, 1f, 1f, 0.82f)
                            : new Color(0.035f, 0.045f, 0.06f, 0.92f));
                }
            }
            texture.Apply();
            return Sprite.Create(
                texture,
                new Rect(0f, 0f, width, height),
                new Vector2(0.5f, 0.5f),
                pixelsPerUnit,
                0,
                SpriteMeshType.FullRect);
        }

        private static Sprite CreateCircleSprite()
        {
            const int size = 64;
            Texture2D texture = new Texture2D(
                size,
                size,
                TextureFormat.RGBA32,
                false);
            texture.name = "AngleViewRotationCircle";
            float radius = size * 0.5f - 1f;
            Vector2 center = new Vector2(size * 0.5f, size * 0.5f);
            for (int y = 0; y < size; y++)
            {
                for (int x = 0; x < size; x++)
                {
                    float distance = Vector2.Distance(
                        new Vector2(x + 0.5f, y + 0.5f),
                        center);
                    if (distance > radius)
                    {
                        texture.SetPixel(x, y, Color.clear);
                    }
                    else if (distance > radius - 3f)
                    {
                        texture.SetPixel(x, y, Color.white);
                    }
                    else
                    {
                        texture.SetPixel(
                            x,
                            y,
                            new Color(0.025f, 0.035f, 0.05f, 0.94f));
                    }
                }
            }
            texture.Apply();
            return Sprite.Create(
                texture,
                new Rect(0f, 0f, size, size),
                new Vector2(0.5f, 0.5f),
                100f);
        }

        private static float ClosestEquivalent(float actual, float reference)
        {
            float best = actual;
            float bestDistance = Mathf.Abs(actual - reference);
            for (int turn = -1; turn <= 1; turn++)
            {
                float candidate = actual + turn * 360f;
                float distance = Mathf.Abs(candidate - reference);
                if (distance < bestDistance)
                {
                    best = candidate;
                    bestDistance = distance;
                }
            }
            return best;
        }
    }

    internal sealed class ViewRotationHandleShape : MonoBehaviour
    {
        private RectTransform rect;
        private float size;

        internal void Configure(RectTransform target, float diameter)
        {
            rect = target;
            size = diameter;
        }

        private void LateUpdate()
        {
            if (rect != null)
            {
                rect.sizeDelta = new Vector2(size, size);
            }
        }
    }

    internal sealed class ViewRotationSliderEvents :
        MonoBehaviour,
        IPointerDownHandler,
        IPointerUpHandler
    {
        internal Action<bool> OnDragStateChanged;

        public void OnPointerDown(PointerEventData eventData)
        {
            OnDragStateChanged?.Invoke(true);
        }

        public void OnPointerUp(PointerEventData eventData)
        {
            OnDragStateChanged?.Invoke(false);
        }
    }
}
