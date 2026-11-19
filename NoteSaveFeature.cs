using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using ADOFAI;
using ADOFAI.Editor;
using ADOFAI.Editor.Actions;
using ADOFAI.LevelEditor.Controls;
using GDMiniJSON;
using HarmonyLib;
using Newtonsoft.Json;
using TMPro;
using UnityEngine;
using UnityEngine.UI;
using UnityModManagerNet;

namespace Angle
{
    [Serializable]
    internal sealed class NotePatternFile
    {
        public int version = 3;
        public float? referenceAngle;
        public bool? referenceIsCCW;
        public List<NoteFloorSave> floors = new List<NoteFloorSave>();
    }

    [Serializable]
    internal sealed class NoteFloorSave
    {
        public string stringDirection = "R";
        public float floatDirection;
        public string levelEventData = "[]";
        public string attachedDecorations = "[]";
        public string trackColor = "";
    }

    internal static class NoteSaveFeature
    {
        private enum NameAction
        {
            None,
            NewFolder,
            RenameFolder,
            RenamePattern
        }

        internal const string PatternExtension = ".anglepattern";
        private const scnEditor.PopupType NativeInputPopupType =
            (scnEditor.PopupType)902;
        private static UnityModManager.ModEntry modEntry;
        private static string noteSaveRoot;
        private static string currentRelativeFolder = "Default";
        private static string selectedPatternPath = "";
        private static string statusText = "";
        private static NameAction nameAction;
        private static string nameInput = "";
        private static GameObject nativeInputPopup;
        private static TMP_InputField nativeInputField;
        private static TMP_Text nativeInputPrompt;
        private static Button nativeInputOkButton;
        private static Button nativeInputCancelButton;
        private static Action<string> nativeInputAccepted;
        private static Action nativeInputCancelled;
        private static bool nativeInputButtonsSwapped;
        private static bool restoreClipboardAfterPopup;
        internal static bool NativePanelOpen;
        internal static Sprite TabIcon;
        private static bool nativeDirty = true;
        private static bool overwritePasteEnabled;
        private static readonly HashSet<string> expandedFolders =
            new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        internal static string RootPath => noteSaveRoot;
        internal static string CurrentFolder => CurrentRelativeFolder();
        internal static string SelectedPatternPath => selectedPatternPath;
        internal static string StatusText => statusText;
        internal static bool OverwritePasteEnabled =>
            overwritePasteEnabled;

        internal static void Initialize(UnityModManager.ModEntry entry)
        {
            PatternPasteFeature.Reset();
            modEntry = entry;
            noteSaveRoot = Path.Combine(entry.Path, "notesave");
            Directory.CreateDirectory(noteSaveRoot);
            LoadTabIcon();
            NoteSaveBrowser.ResetResponsiveSizing();
            currentRelativeFolder = FindOrCreateRootFolder();
            expandedFolders.Clear();
            expandedFolders.Add(currentRelativeFolder);
            selectedPatternPath = "";
            statusText = "";
        }

        internal static void Shutdown()
        {
            PatternPasteFeature.Reset();
            NativePanelOpen = false;
            nameAction = NameAction.None;
            nativeInputAccepted = null;
            nativeInputCancelled = null;
            restoreClipboardAfterPopup = false;
            NoteSaveBrowser.Close();
            EditorViewRotationControl.Destroy();
            NoteSavePreview.Shutdown();
        }

        internal static void Update()
        {
            scnEditor editor = scnEditor.instance;
            if (NativePanelOpen && editor != null && editor.settingsPanel != null)
            {
                editor.settingsPanel.title.text =
                    AngleMod.L("键型剪贴板", "Pattern Clipboard");
                NoteSaveBrowser.UpdateResponsiveLayout();
                if (NoteSaveNativeUI.Panel != null
                    && NoteSaveNativeUI.Panel.content != null
                    && NoteSaveNativeUI.Panel.content.childCount == 0)
                {
                    nativeDirty = true;
                }
                if (nativeDirty)
                {
                    RebuildNativeContent();
                }
            }
            NoteSavePreview.Update();
            EditorViewRotationControl.Update();
            if (restoreClipboardAfterPopup
                && editor != null
                && editor.popupPanel != null
                && !editor.popupPanel.activeSelf)
            {
                restoreClipboardAfterPopup = false;
                RestoreClipboardPanel();
            }
        }

        internal static void DrawOverlay()
        {
        }

        internal static void OnFloorsCopied(scnEditor editor, bool cut)
        {
            ClearPatternSelection();
            PatternPasteFeature.RememberCopiedSource(editor);
            if (cut
                || editor == null
                || editor.clipboardContent != scnEditor.ClipboardContent.Floors
                || editor.clipboard == null
                || editor.clipboard.Count <= 1)
            {
                return;
            }

            try
            {
                EnsureStorageRoot();
                NotePatternFile pattern = PatternFromClipboard(
                    editor.clipboard,
                    editor.selectedFloors,
                    editor);
                string folder = CurrentFolderAbsolute();
                string date = DateTime.Now.ToString("yyyyMMdd");
                int number = 1;
                string fullPath;
                do
                {
                    fullPath = Path.Combine(
                        folder,
                        date + "_" + number.ToString("000") + PatternExtension);
                    number++;
                }
                while (File.Exists(fullPath) || Directory.Exists(fullPath));
                File.WriteAllText(fullPath, JsonConvert.SerializeObject(pattern, Formatting.Indented), new UTF8Encoding(false));
                PatternPasteFeature.AssociateSavedCopy(fullPath, File.GetLastWriteTimeUtc(fullPath));
                selectedPatternPath = RelativePath(fullPath);
                statusText = AngleMod.L("已保存当前多选键型", "Copied selection saved");
                RequestNativeRebuild();
            }
            catch (Exception exception)
            {
                statusText = AngleMod.L("保存失败：", "Save failed: ") + exception.Message;
                if (modEntry != null)
                {
                    modEntry.Logger.Error("Failed to save note pattern: " + exception);
                }
            }
        }

        internal static void PrepareSavedPatternForPaste(scnEditor editor)
        {
            if (editor == null || string.IsNullOrEmpty(selectedPatternPath))
            {
                return;
            }

            string fullPath = AbsoluteFromRelative(selectedPatternPath);
            if (!File.Exists(fullPath))
            {
                selectedPatternPath = "";
                return;
            }

            if (PatternPasteFeature.IsLiveSavedCopy(editor, fullPath, File.GetLastWriteTimeUtc(fullPath)))
            {
                return;
            }
            NotePatternFile pattern = LoadPattern(fullPath);
            if (pattern == null || pattern.floors.Count == 0)
            {
                return;
            }

            editor.clipboard.Clear();
            foreach (NoteFloorSave savedFloor in pattern.floors)
            {
                char stringDirection = string.IsNullOrEmpty(savedFloor.stringDirection)
                    ? 'R'
                    : savedFloor.stringDirection[0];
                editor.clipboard.Add(new scnEditor.FloorData(
                    stringDirection,
                    savedFloor.floatDirection,
                    DecodeEvents(savedFloor.levelEventData),
                    DecodeEvents(savedFloor.attachedDecorations)));
            }
            editor.clipboardContent = scnEditor.ClipboardContent.Floors;
            PatternPasteFeature.RememberSource(editor, pattern);
        }

        internal static void SetOverwritePasteEnabled(bool enabled)
        {
            overwritePasteEnabled = enabled;
            statusText = enabled
                ? AngleMod.L("覆盖粘贴已开启", "Overwrite paste enabled")
                : AngleMod.L("覆盖粘贴已关闭", "Overwrite paste disabled");
        }

        internal static bool TryOverwritePaste(scnEditor editor)
        {
            if (!overwritePasteEnabled
                || editor == null
                || string.IsNullOrEmpty(selectedPatternPath))
            {
                return false;
            }
            if (editor.selectedFloors == null
                || editor.selectedFloors.Count != 1)
            {
                editor.ShowNotification(AngleMod.L(
                    "覆盖粘贴需要选择一条起始轨道",
                    "Select one starting floor for overwrite paste"));
                return true;
            }

            string fullPath = AbsoluteFromRelative(selectedPatternPath);
            if (!File.Exists(fullPath))
            {
                editor.ShowNotification(AngleMod.L(
                    "找不到选中的键型文件",
                    "The selected pattern file was not found"));
                return true;
            }
            NotePatternFile pattern = LoadPattern(fullPath);
            if (pattern == null || pattern.floors.Count == 0)
            {
                return true;
            }
            scrFloor selected = editor.selectedFloors[0];
            int startIndex = selected.seqID;
            int targetCount = pattern.floors.Count;
            PatternRhythm rhythm = PatternPasteFeature.IsLiveSavedCopy(editor, fullPath,
                File.GetLastWriteTimeUtc(fullPath)) ? PatternPasteFeature.GetCopiedRhythm(editor) : null;
            LevelData before = rhythm == null ? null : editor.levelData.Copy();
            PatternPasteTransform transform =
                PatternPasteFeature.TransformForPattern(editor, pattern);
            bool compensateSpin = PatternPasteFeature.NeedsSpinCompensation(
                editor, pattern.referenceIsCCW, transform, targetCount);
            PatternTwirlCleanup twirlCleanup = rhythm != null || compensateSpin
                ? PatternTwirlCleanup.ForPaste(editor, startIndex, targetCount, overwrite: true) : null;
            float[] angles = pattern.floors
                .Select(floor => transform.Apply(floor.floatDirection)).ToArray();
            string legacyPath = null;
            bool keepLegacy = editor.levelData.isOldLevel
                && PatternPasteFeature.TryLegacyCharacters(angles, out legacyPath);
            bool reshaped = false;
            int[] midspins = Array.Empty<int>();
            using (new SaveStateScope(editor))
            {
                if (editor.levelData.isOldLevel && !keepLegacy)
                {
                    PatternPasteFeature.PromoteLegacyLevel(editor);
                }
                List<char> legacyTracks = keepLegacy
                    ? editor.levelData.pathData.ToList() : null;
                int existingCount = keepLegacy
                    ? legacyTracks.Count : editor.levelData.angleData.Count;
                for (int index = 0; index < targetCount; index++)
                {
                    int targetIndex = startIndex + index;
                    int targetFloor = targetIndex + 1;
                    bool isNewTrack = targetIndex >= existingCount;
                    NoteFloorSave source = pattern.floors[index];
                    if (keepLegacy)
                    {
                        if (isNewTrack)
                        {
                            legacyTracks.Add(legacyPath[index]);
                        }
                        else
                        {
                            legacyTracks[targetIndex] = legacyPath[index];
                        }
                    }
                    else if (isNewTrack)
                    {
                        editor.levelData.angleData.Add(
                            angles[index]);
                    }
                    else
                    {
                        editor.levelData.angleData[targetIndex] =
                            angles[index];
                    }
                    if (!isNewTrack)
                    {
                        editor.levelData.levelEvents.RemoveAll(
                            levelEvent =>
                                levelEvent.floor == targetFloor
                                && IsOverwriteEventType(
                                    levelEvent.eventType, rhythm != null));
                    }

                    List<LevelEvent> copiedEvents = DecodeEvents(source.levelEventData);
                    twirlCleanup?.RememberCopied(targetFloor, copiedEvents);
                    List<LevelEvent> sourceEvents = PatternPasteFeature.DirectionEvents(
                        copiedEvents, index, targetCount, compensateSpin);
                    foreach (LevelEvent levelEvent in sourceEvents)
                    {
                        if (!isNewTrack
                            && !IsOverwriteEventType(
                                levelEvent.eventType, rhythm != null))
                        {
                            continue;
                        }
                        levelEvent.floor = targetFloor;
                        editor.levelData.levelEvents.Add(levelEvent);
                    }

                    if (isNewTrack)
                    {
                        foreach (LevelEvent decoration in
                            DecodeEvents(source.attachedDecorations))
                        {
                            decoration.floor = targetFloor;
                            editor.levelData.decorations.Add(decoration);
                        }
                    }
                }
                if (keepLegacy)
                {
                    editor.levelData.pathData = new string(legacyTracks.ToArray());
                }
                editor.RemakePath();
                if (rhythm != null && !rhythm.Preserve(editor, PatternRhythm.ThroughExit,
                    PatternPasteFeature.ForcedDirection, out reshaped, out midspins))
                {
                    PatternRhythm.Restore(editor, before, startIndex, startIndex, refreshDecorations: true);
                    return true;
                }
                twirlCleanup?.RemoveRedundant(editor, midspins);
                if (editor.floors.Count > startIndex + 1)
                {
                    editor.SelectFloor(editor.floors[
                        Mathf.Min(
                            PatternRhythmLayout.MapFloor(startIndex + 1, midspins),
                            editor.floors.Count - 1)]);
                }
            }
            if (reshaped)
            {
                editor.ShowNotification(PatternPasteFeature.ReshapedNotice(midspins.Length > 0));
            }
            statusText = AngleMod.L(
                "覆盖粘贴完成",
                "Overwrite paste complete");
            RequestNativeRebuild();
            return true;
        }

        private static bool IsOverwriteEventType(
            LevelEventType eventType, bool preserveRhythm)
        {
            return eventType == LevelEventType.Twirl
                || eventType == LevelEventType.Pause
                || eventType == LevelEventType.Checkpoint
                || eventType == LevelEventType.SetSpeed
                || eventType == LevelEventType.AutoPlayTiles
                || eventType == LevelEventType.Hold
                || (preserveRhythm && (eventType == LevelEventType.Multitap
                    || eventType == LevelEventType.MultiPlanet || eventType == LevelEventType.FreeRoam));
        }

        internal static void OnNativePanelShown()
        {
            nativeDirty = true;
        }

        internal static void OnLanguageChanged()
        {
            nativeDirty = true;
        }

        internal static void RequestNativeRebuild()
        {
            nativeDirty = true;
        }

        internal static void RestoreClipboardPanel()
        {
            scnEditor editor = scnEditor.instance;
            if (editor == null
                || editor.playMode
                || editor.settingsPanel == null)
            {
                return;
            }
            editor.settingsPanel.ShowInspector(true, forceAction: true);
            editor.settingsPanel.ShowPanel(NoteSaveNativeUI.EventType);
        }

        internal static bool IsFolderExpanded(string relative)
        {
            return expandedFolders.Contains(relative);
        }

        internal static void ToggleFolder(string relative)
        {
            if (expandedFolders.Contains(relative))
            {
                expandedFolders.Remove(relative);
            }
            else
            {
                expandedFolders.Add(relative);
            }
        }

        internal static void SetFolderExpanded(string relative, bool expanded)
        {
            if (expanded)
            {
                expandedFolders.Add(relative);
            }
            else
            {
                expandedFolders.Remove(relative);
            }
        }

        private static void ReplaceExpandedFolderPrefix(
            string oldRelative,
            string newRelative)
        {
            string oldPrefix = oldRelative + "/";
            List<string> affected = expandedFolders
                .Where(relative =>
                    string.Equals(
                        relative,
                        oldRelative,
                        StringComparison.OrdinalIgnoreCase)
                    || relative.StartsWith(
                        oldPrefix,
                        StringComparison.OrdinalIgnoreCase))
                .ToList();
            foreach (string relative in affected)
            {
                expandedFolders.Remove(relative);
                expandedFolders.Add(
                    string.Equals(
                        relative,
                        oldRelative,
                        StringComparison.OrdinalIgnoreCase)
                        ? newRelative
                        : newRelative + relative.Substring(oldRelative.Length));
            }
        }

        internal static void SelectFolder(string relative)
        {
            currentRelativeFolder = relative;
            selectedPatternPath = "";
        }

        internal static void SelectPattern(string relative)
        {
            if (string.IsNullOrEmpty(relative))
            {
                return;
            }
            selectedPatternPath = relative;
            currentRelativeFolder =
                Path.GetDirectoryName(relative)?.Replace('\\', '/') ?? "Default";
            statusText = AngleMod.L("已选中：", "Selected: ")
                + Path.GetFileNameWithoutExtension(relative);
        }

        internal static void ClearPatternSelection()
        {
            selectedPatternPath = "";
        }

        internal static string ToRelativePath(string fullPath)
        {
            return RelativePath(fullPath);
        }

        internal static string ToAbsolutePath(string relative)
        {
            return AbsoluteFromRelative(relative);
        }

        internal static NotePatternFile ReadPattern(string path)
        {
            return LoadPattern(path);
        }

        internal static void BeginCreateFolder()
        {
            BeginName(NameAction.NewFolder, "");
        }

        internal static void BeginRenameFolder(string path)
        {
            currentRelativeFolder = RelativePath(path);
            BeginName(
                NameAction.RenameFolder,
                Path.GetFileName(path));
        }

        internal static void BeginRenamePattern(string path)
        {
            selectedPatternPath = RelativePath(path);
            BeginName(
                NameAction.RenamePattern,
                Path.GetFileNameWithoutExtension(path));
        }

        internal static void OpenPreview(string relative)
        {
            SelectPattern(relative);
            NoteSavePreview.Open(relative);
        }

        private static void RebuildNativeContent()
        {
            nativeDirty = false;
            if (NoteSaveNativeUI.Panel == null || NoteSaveNativeUI.Panel.content == null)
            {
                return;
            }

            RectTransform content = NoteSaveNativeUI.Panel.content;
            for (int index = content.childCount - 1; index >= 0; index--)
            {
                GameObject child = content.GetChild(index).gameObject;
                child.transform.SetParent(null, false);
                UnityEngine.Object.Destroy(child);
            }
            NoteSaveNativeUI.Panel.properties.Clear();
            NoteSaveNativeUI.Panel.propertySelectables.Clear();
            NoteSaveBrowser.Rebuild(content);
        }

        internal static Texture2D CreatePatternThumbnail(
            NotePatternFile pattern,
            int size)
        {
            Texture2D texture = new Texture2D(size, size, TextureFormat.RGBA32, false);
            Color clear = new Color(0.015f, 0.02f, 0.03f, 1f);
            Color line = new Color(0.95f, 0.97f, 1f, 1f);
            Color[] pixels = Enumerable.Repeat(clear, size * size).ToArray();
            if (pattern == null || pattern.floors.Count == 0)
            {
                texture.SetPixels(pixels);
                texture.Apply();
                return texture;
            }

            List<Vector2> points = new List<Vector2>();
            Vector2 cursor = Vector2.zero;
            points.Add(cursor);
            foreach (NoteFloorSave floor in pattern.floors)
            {
                float radians = floor.floatDirection * Mathf.Deg2Rad;
                cursor += new Vector2(Mathf.Cos(radians), -Mathf.Sin(radians));
                points.Add(cursor);
            }

            float minX = points.Min(point => point.x);
            float maxX = points.Max(point => point.x);
            float minY = points.Min(point => point.y);
            float maxY = points.Max(point => point.y);
            float span = Mathf.Max(maxX - minX, maxY - minY);
            if (span < 0.001f)
            {
                span = 1f;
            }
            float scale = (size - 16f) / span;
            Vector2 center = new Vector2((minX + maxX) * 0.5f, (minY + maxY) * 0.5f);
            for (int index = 0; index < points.Count; index++)
            {
                points[index] = (points[index] - center) * scale
                    + new Vector2(size * 0.5f, size * 0.5f);
            }
            for (int index = 1; index < points.Count; index++)
            {
                DrawThumbnailLine(pixels, size, points[index - 1], points[index], line, 3);
            }
            texture.SetPixels(pixels);
            texture.Apply();
            return texture;
        }

        private static void DrawThumbnailLine(
            Color[] pixels,
            int size,
            Vector2 from,
            Vector2 to,
            Color color,
            int thickness)
        {
            int x0 = Mathf.RoundToInt(from.x);
            int y0 = Mathf.RoundToInt(from.y);
            int x1 = Mathf.RoundToInt(to.x);
            int y1 = Mathf.RoundToInt(to.y);
            int dx = Math.Abs(x1 - x0);
            int sx = x0 < x1 ? 1 : -1;
            int dy = -Math.Abs(y1 - y0);
            int sy = y0 < y1 ? 1 : -1;
            int error = dx + dy;
            while (true)
            {
                for (int oy = -thickness; oy <= thickness; oy++)
                {
                    for (int ox = -thickness; ox <= thickness; ox++)
                    {
                        if (ox * ox + oy * oy > thickness * thickness)
                        {
                            continue;
                        }
                        int px = x0 + ox;
                        int py = y0 + oy;
                        if (px >= 0 && py >= 0 && px < size && py < size)
                        {
                            pixels[py * size + px] = color;
                        }
                    }
                }
                if (x0 == x1 && y0 == y1)
                {
                    break;
                }
                int twiceError = 2 * error;
                if (twiceError >= dy)
                {
                    error += dy;
                    x0 += sx;
                }
                if (twiceError <= dx)
                {
                    error += dx;
                    y0 += sy;
                }
            }
        }

#if false
        private static void AddNativeLabel(string text)
        {
            GameObject propertyObject = UnityEngine.Object.Instantiate(
                ADOBase.gc.prefab_property,
                NoteSaveNativeUI.Panel.content,
                false);
            propertyObject.name = "AngleNoteSaveLabel_" + Guid.NewGuid().ToString("N");
            ADOFAI.Property property = propertyObject.GetComponent<ADOFAI.Property>();
            property.key = propertyObject.name;
            property.label.text = text;
            if (property.controlContainer != null)
            {
                property.controlContainer.gameObject.SetActive(false);
            }
        }

        private static void DrawPreview()
        {
            if (previewPattern == null)
            {
                previewOpen = false;
                return;
            }

            Event current = Event.current;
            float scale = UiScale();
            Rect closeRect = new Rect(
                Screen.width - 58f * scale,
                18f * scale,
                40f * scale,
                40f * scale);
            if (GUI.Button(closeRect, "×", dangerStyle))
            {
                previewOpen = false;
                previewPattern = null;
                return;
            }

            Rect saveRect = new Rect(18f * scale, 18f * scale, 82f * scale, 34f * scale);
            if (GUI.Button(saveRect, AngleMod.L("保存", "Save"), buttonStyle))
            {
                SavePreview();
            }
            Rect addRect = new Rect(18f * scale, 58f * scale, 82f * scale, 34f * scale);
            if (GUI.Button(addRect, AngleMod.L("新增", "Add"), buttonStyle))
            {
                float direction = 0f;
                if (previewSelectedFloor >= 0
                    && previewSelectedFloor < previewPattern.floors.Count)
                {
                    direction = previewPattern.floors[previewSelectedFloor].floatDirection;
                }
                previewPattern.floors.Add(new NoteFloorSave
                {
                    floatDirection = direction
                });
                previewSelectedFloor = previewPattern.floors.Count - 1;
            }
            Rect deleteRect = new Rect(18f * scale, 98f * scale, 82f * scale, 34f * scale);
            if (GUI.Button(
                deleteRect,
                AngleMod.L("删除", "Delete"),
                dangerStyle)
                || previewSelectedFloor > 0
                && Input.GetKeyDown(KeyCode.Delete))
            {
                if (previewSelectedFloor > 0
                    && previewSelectedFloor < previewPattern.floors.Count)
                {
                    previewPattern.floors.RemoveAt(previewSelectedFloor);
                    previewSelectedFloor =
                        Mathf.Min(previewSelectedFloor, previewPattern.floors.Count - 1);
                }
            }

            Rect sliderRect = new Rect(
                Screen.width - 36f * scale,
                90f * scale,
                22f * scale,
                Screen.height - 180f * scale);
            previewRotation = GUI.VerticalSlider(sliderRect, previewRotation, 0f, 360f);

            bool pointerOverSlider = sliderRect.Contains(current.mousePosition);
            if (!pointerOverSlider && current.type == EventType.ScrollWheel)
            {
                previewZoom = Mathf.Clamp(
                    previewZoom - current.delta.y * 0.08f,
                    0.25f,
                    4f);
                current.Use();
                Repaint();
            }

            if (current.type == EventType.MouseDown
                && current.button == 2)
            {
                previewDragging = true;
                current.Use();
            }
            else if (current.type == EventType.MouseDrag && previewDragging)
            {
                previewCenter += current.delta;
                current.Use();
                Repaint();
            }
            else if (current.type == EventType.MouseUp && current.button == 2)
            {
                previewDragging = false;
                current.Use();
            }

            DrawPreviewPattern(current);
        }

        private static void DrawPreviewPattern(Event current)
        {
            float size = Mathf.Min(Screen.width, Screen.height) * 0.55f * previewZoom;
            Rect bounds = new Rect(
                previewCenter.x - size * 0.5f,
                previewCenter.y - size * 0.5f,
                size,
                size);

            Matrix4x4 oldMatrix = GUI.matrix;
            GUIUtility.RotateAroundPivot(previewRotation, previewCenter);
            DrawPattern(bounds, previewPattern, 0f);

            if (current.type == EventType.MouseDown
                && current.button == 0
                && previewPattern.floors.Count > 1)
            {
                List<Vector2> points = BuildPatternPoints(bounds, previewPattern, 0f);
                for (int index = 1; index < points.Count; index++)
                {
                    if (Vector2.Distance(points[index], current.mousePosition) < 15f)
                    {
                        previewSelectedFloor = index;
                        break;
                    }
                }
            }
            else if (current.type == EventType.MouseDrag
                && current.button == 0
                && previewSelectedFloor > 0
                && previewSelectedFloor < previewPattern.floors.Count)
            {
                List<Vector2> points = BuildPatternPoints(bounds, previewPattern, 0f);
                Vector2 delta = current.mousePosition - points[previewSelectedFloor - 1];
                if (delta.sqrMagnitude > 0.01f)
                {
                    float angle = Mathf.Atan2(-delta.y, delta.x) * Mathf.Rad2Deg;
                    previewPattern.floors[previewSelectedFloor].floatDirection =
                        NormalizeAngle(angle);
                }
                current.Use();
                Repaint();
            }
            GUI.matrix = oldMatrix;
        }

        private static void OpenPreview(string path)
        {
            try
            {
                previewPattern = LoadPattern(path);
                if (previewPattern == null || previewPattern.floors.Count == 0)
                {
                    return;
                }
                previewPath = path;
                previewCenter = new Vector2(Screen.width * 0.5f, Screen.height * 0.5f);
                previewZoom = 1f;
                previewRotation = 0f;
                previewSelectedFloor = -1;
                previewOpen = true;
            }
            catch (Exception exception)
            {
                statusText = AngleMod.L("预览失败：", "Preview failed: ") + exception.Message;
            }
        }

        private static void SavePreview()
        {
            if (string.IsNullOrEmpty(previewPath) || previewPattern == null)
            {
                return;
            }
            File.WriteAllText(
                previewPath,
                JsonConvert.SerializeObject(previewPattern, Formatting.Indented),
                new UTF8Encoding(false));
            statusText = AngleMod.L("预览修改已保存", "Preview changes saved");
        }

        private static void DrawPattern(Rect rect, NotePatternFile pattern, float rotation)
        {
            if (pattern == null || pattern.floors.Count == 0)
            {
                return;
            }
            List<Vector2> points = BuildPatternPoints(rect, pattern, rotation);
            Color old = GUI.color;
            GUI.color = new Color(1f, 1f, 1f, 0.9f);
            for (int index = 1; index < points.Count; index++)
            {
                DrawLine(points[index - 1], points[index], Mathf.Max(2f, rect.width / 28f));
            }
            GUI.color = new Color(1f, 0.58f, 0.1f, 1f);
            foreach (Vector2 point in points)
            {
                GUI.DrawTexture(new Rect(point.x - 2f, point.y - 2f, 4f, 4f), Texture2D.whiteTexture);
            }
            GUI.color = old;
        }

        private static List<Vector2> BuildPatternPoints(
            Rect rect,
            NotePatternFile pattern,
            float rotation)
        {
            List<Vector2> raw = new List<Vector2>();
            Vector2 cursor = Vector2.zero;
            raw.Add(cursor);
            foreach (NoteFloorSave floor in pattern.floors)
            {
                float radians = floor.floatDirection * Mathf.Deg2Rad;
                cursor += new Vector2(Mathf.Cos(radians), -Mathf.Sin(radians));
                raw.Add(cursor);
            }

            float angleRadians = rotation * Mathf.Deg2Rad;
            for (int index = 0; index < raw.Count; index++)
            {
                raw[index] = Rotate(raw[index], angleRadians);
            }

            float minX = raw.Min(point => point.x);
            float maxX = raw.Max(point => point.x);
            float minY = raw.Min(point => point.y);
            float maxY = raw.Max(point => point.y);
            float span = Mathf.Max(maxX - minX, maxY - minY);
            if (span < 0.001f)
            {
                span = 1f;
            }
            float scale = Mathf.Min(rect.width, rect.height) * 0.82f / span;
            Vector2 center = new Vector2((minX + maxX) * 0.5f, (minY + maxY) * 0.5f);
            Vector2 rectCenter = rect.center;
            for (int index = 0; index < raw.Count; index++)
            {
                raw[index] = (raw[index] - center) * scale + rectCenter;
            }
            return raw;
        }

        private static Vector2 Rotate(Vector2 value, float radians)
        {
            float sin = Mathf.Sin(radians);
            float cos = Mathf.Cos(radians);
            return new Vector2(value.x * cos - value.y * sin, value.x * sin + value.y * cos);
        }

        private static void DrawLine(Vector2 from, Vector2 to, float width)
        {
            Vector2 delta = to - from;
            if (delta.sqrMagnitude < 0.01f)
            {
                return;
            }

            Matrix4x4 old = GUI.matrix;
            float angle = Mathf.Atan2(delta.y, delta.x) * Mathf.Rad2Deg;
            Vector2 center = (from + to) * 0.5f;
            GUIUtility.RotateAroundPivot(angle, center);
            GUI.DrawTexture(
                new Rect(center.x - delta.magnitude * 0.5f, center.y - width * 0.5f, delta.magnitude, width),
                Texture2D.whiteTexture);
            GUI.matrix = old;
        }

        private static float NormalizeAngle(float angle)
        {
            angle %= 360f;
            if (angle < 0f)
            {
                angle += 360f;
            }
            return angle;
        }
#endif

        private static void DeleteConfirmed(string path, bool directory)
        {
            try
            {
                string currentAbsolute = CurrentFolderAbsolute();
                if (directory)
                {
                    if (IsSafePath(path) && Directory.Exists(path))
                    {
                        Directory.Delete(path, true);
                    }
                }
                else if (IsSafePath(path) && File.Exists(path))
                {
                    File.Delete(path);
                }
                if (selectedPatternPath == RelativePath(path))
                {
                    selectedPatternPath = "";
                }
                if (directory
                    && selectedPatternPath.StartsWith(
                        RelativePath(path) + "/",
                        StringComparison.OrdinalIgnoreCase))
                {
                    selectedPatternPath = "";
                }
                if (directory
                    && (currentAbsolute == path
                        || currentAbsolute.StartsWith(
                            path + Path.DirectorySeparatorChar,
                            StringComparison.OrdinalIgnoreCase)))
                {
                    currentRelativeFolder = FindOrCreateRootFolder();
                    selectedPatternPath = "";
                }
                statusText = AngleMod.L("已删除", "Deleted");
            }
            catch (Exception exception)
            {
                statusText = AngleMod.L("删除失败：", "Delete failed: ") + exception.Message;
            }
            RequestNativeRebuild();
            RestoreClipboardPanel();
        }

        private static void BeginName(NameAction action, string initial)
        {
            nameAction = action;
            nameInput = initial;
            string prompt = action == NameAction.NewFolder
                ? AngleMod.L("新建文件夹", "New Folder")
                : action == NameAction.RenameFolder
                    ? AngleMod.L("重命名文件夹", "Rename Folder")
                    : AngleMod.L("重命名键型", "Rename Pattern");
            string hint = action == NameAction.RenameFolder
                || action == NameAction.RenamePattern
                    ? AngleMod.L("请输入重命名", "Enter a new name")
                    : AngleMod.L("请输入名称", "Enter a name");
            ShowNativeTextInput(
                prompt,
                hint,
                initial,
                delegate(string value)
                {
                    nameInput = value;
                    CommitName();
                },
                delegate
                {
                    nameAction = NameAction.None;
                    nameInput = "";
                    RestoreClipboardPanel();
                });
        }

        private static void ShowNativeTextInput(
            string title,
            string hint,
            string initial,
            Action<string> accepted,
            Action cancelled)
        {
            scnEditor editor = scnEditor.instance;
            if (editor == null)
            {
                return;
            }

            EnsureNativeInputPopup(editor);
            if (nativeInputPopup == null
                || nativeInputField == null
                || nativeInputOkButton == null
                || nativeInputCancelButton == null)
            {
                statusText = AngleMod.L(
                    "无法打开名称输入窗口",
                    "Could not open the name dialog");
                return;
            }

            nativeInputAccepted = accepted;
            nativeInputCancelled = cancelled;
            nativeInputPopup.transform.SetParent(null, false);
            nativeInputPopup.SetActive(true);
            editor.ShowPopup(true, NativeInputPopupType);
            nativeInputPopup.transform.SetParent(
                editor.popupWindow.transform,
                false);
            nativeInputPopup.SetActive(true);
            nativeInputPopup.transform.SetAsLastSibling();
            if (nativeInputPrompt != null)
            {
                nativeInputPrompt.text = title;
            }
            TMP_Text placeholder =
                nativeInputField.placeholder as TMP_Text;
            if (placeholder != null)
            {
                placeholder.text = hint;
            }
            nativeInputField.SetTextWithoutNotify(initial ?? "");
            nativeInputField.caretPosition = (initial ?? "").Length;
            SetButtonText(
                nativeInputOkButton,
                AngleMod.L("确定", "OK"));
            SetButtonText(
                nativeInputCancelButton,
                AngleMod.L("取消", "Cancel"));
            if (!nativeInputButtonsSwapped)
            {
                SwapNativeInputButtons();
            }
            nativeInputField.Select();
            nativeInputField.ActivateInputField();
        }

        private static void EnsureNativeInputPopup(scnEditor editor)
        {
            if (nativeInputPopup != null
                || editor == null
                || editor.urlPopupContainer == null
                || editor.popupWindow == null)
            {
                return;
            }

            nativeInputPopup = UnityEngine.Object.Instantiate(
                editor.urlPopupContainer,
                editor.popupWindow.transform,
                false);
            nativeInputPopup.name = "AngleNativeInputPopup";
            nativeInputPopup.SetActive(false);

            foreach (scrTextChanger changer in
                nativeInputPopup.GetComponentsInChildren<scrTextChanger>(true))
            {
                UnityEngine.Object.Destroy(changer);
            }

            nativeInputField = FindChildComponent<TMP_InputField>(
                nativeInputPopup.transform,
                "inputField");
            if (nativeInputField == null)
            {
                nativeInputField =
                    nativeInputPopup.GetComponentInChildren<TMP_InputField>(true);
            }

            nativeInputPrompt = FindPromptText(
                nativeInputPopup.transform,
                nativeInputField == null ? null : nativeInputField.transform);
            nativeInputOkButton = FindChildComponent<Button>(
                nativeInputPopup.transform,
                "buttonDownload");
            nativeInputCancelButton = FindChildComponent<Button>(
                nativeInputPopup.transform,
                "buttonCancel");
            nativeInputButtonsSwapped = false;

            if (nativeInputField != null)
            {
                nativeInputField.contentType =
                    TMP_InputField.ContentType.Standard;
                nativeInputField.lineType =
                    TMP_InputField.LineType.SingleLine;
                nativeInputField.characterLimit = 96;
                nativeInputField.onValueChanged.RemoveAllListeners();
                nativeInputField.onEndEdit.RemoveAllListeners();
                nativeInputField.onSubmit.RemoveAllListeners();
                nativeInputField.onSubmit.AddListener(
                    delegate { AcceptNativeTextInput(); });
                nativeInputField.onEndEdit.AddListener(delegate
                {
                    if (nativeInputField != null
                        && nativeInputField.wasCanceled)
                    {
                        CancelNativeTextInput();
                    }
                });
            }

            if (nativeInputOkButton != null)
            {
                nativeInputOkButton.onClick.RemoveAllListeners();
                nativeInputOkButton.onClick.AddListener(
                    AcceptNativeTextInput);
            }
            if (nativeInputCancelButton != null)
            {
                nativeInputCancelButton.onClick.RemoveAllListeners();
                nativeInputCancelButton.onClick.AddListener(
                    CancelNativeTextInput);
            }
        }

        private static T FindChildComponent<T>(
            Transform root,
            string childName)
            where T : Component
        {
            Transform child = FindDescendant(root, childName);
            return child == null ? null : child.GetComponent<T>();
        }

        private static Transform FindDescendant(Transform root, string name)
        {
            if (root == null)
            {
                return null;
            }
            if (string.Equals(root.name, name, StringComparison.OrdinalIgnoreCase))
            {
                return root;
            }
            for (int index = 0; index < root.childCount; index++)
            {
                Transform result = FindDescendant(root.GetChild(index), name);
                if (result != null)
                {
                    return result;
                }
            }
            return null;
        }

        private static TMP_Text FindPromptText(
            Transform root,
            Transform inputTransform)
        {
            TMP_Text fallback = null;
            foreach (TMP_Text text in root.GetComponentsInChildren<TMP_Text>(true))
            {
                if (inputTransform != null
                    && text.transform.IsChildOf(inputTransform))
                {
                    continue;
                }
                if (string.Equals(
                    text.name,
                    "popupText",
                    StringComparison.OrdinalIgnoreCase))
                {
                    return text;
                }
                if (fallback == null
                    && text.GetComponentInParent<Button>() == null)
                {
                    fallback = text;
                }
            }
            return fallback;
        }

        private static void SetButtonText(Button button, string text)
        {
            if (button == null)
            {
                return;
            }
            TMP_Text label = button.GetComponentInChildren<TMP_Text>(true);
            if (label != null)
            {
                label.text = text;
            }
        }

        private static void SwapNativeInputButtons()
        {
            if (nativeInputOkButton == null
                || nativeInputCancelButton == null)
            {
                return;
            }
            Transform ok = nativeInputOkButton.transform;
            Transform cancel = nativeInputCancelButton.transform;
            if (ok.parent != null && ok.parent == cancel.parent)
            {
                int okIndex = ok.GetSiblingIndex();
                int cancelIndex = cancel.GetSiblingIndex();
                ok.SetSiblingIndex(cancelIndex);
                cancel.SetSiblingIndex(okIndex);
            }
            RectTransform okRect = ok as RectTransform;
            RectTransform cancelRect = cancel as RectTransform;
            if (okRect != null && cancelRect != null)
            {
                Vector2 okPosition = okRect.anchoredPosition;
                Vector2 cancelPosition = cancelRect.anchoredPosition;
                Vector2 okAnchorMin = okRect.anchorMin;
                Vector2 okAnchorMax = okRect.anchorMax;
                Vector2 okPivot = okRect.pivot;

                okRect.anchorMin = cancelRect.anchorMin;
                okRect.anchorMax = cancelRect.anchorMax;
                okRect.pivot = cancelRect.pivot;
                okRect.anchoredPosition = cancelPosition;

                cancelRect.anchorMin = okAnchorMin;
                cancelRect.anchorMax = okAnchorMax;
                cancelRect.pivot = okPivot;
                cancelRect.anchoredPosition = okPosition;
            }
            nativeInputButtonsSwapped = true;
        }

        private static void AcceptNativeTextInput()
        {
            if (nativeInputPopup == null || !nativeInputPopup.activeSelf)
            {
                return;
            }
            string value = nativeInputField == null
                ? ""
                : nativeInputField.text;
            Action<string> accepted = nativeInputAccepted;
            nativeInputAccepted = null;
            nativeInputCancelled = null;
            CloseNativeInputPopup();
            accepted?.Invoke(value);
        }

        private static void CancelNativeTextInput()
        {
            if (nativeInputPopup == null || !nativeInputPopup.activeSelf)
            {
                return;
            }
            Action cancelled = nativeInputCancelled;
            nativeInputAccepted = null;
            nativeInputCancelled = null;
            CloseNativeInputPopup();
            cancelled?.Invoke();
        }

        private static void CloseNativeInputPopup()
        {
            scnEditor editor = scnEditor.instance;
            if (nativeInputPopup != null)
            {
                nativeInputPopup.SetActive(false);
            }
            if (editor != null)
            {
                editor.ShowPopup(false, NativeInputPopupType);
            }
        }

        private static void CommitName()
        {
            string safeName = SanitizeName(nameInput);
            if (string.IsNullOrEmpty(safeName))
            {
                nameAction = NameAction.None;
                return;
            }

            try
            {
                if (nameAction == NameAction.NewFolder)
                {
                    string created = UniqueDirectory(Path.Combine(CurrentFolderAbsolute(), safeName));
                    Directory.CreateDirectory(created);
                    expandedFolders.Add(CurrentRelativeFolder());
                }
                else if (nameAction == NameAction.RenameFolder)
                {
                    string source = CurrentFolderAbsolute();
                    string oldRelative = RelativePath(source);
                    string destination = UniqueDirectory(
                        Path.Combine(Path.GetDirectoryName(source), safeName));
                    Directory.Move(source, destination);
                    string newRelative = RelativePath(destination);
                    ReplaceExpandedFolderPrefix(oldRelative, newRelative);
                    currentRelativeFolder = newRelative;
                    if (selectedPatternPath.StartsWith(
                        oldRelative + "/",
                        StringComparison.OrdinalIgnoreCase))
                    {
                        selectedPatternPath =
                            newRelative + selectedPatternPath.Substring(oldRelative.Length);
                    }
                }
                else if (nameAction == NameAction.RenamePattern && !string.IsNullOrEmpty(selectedPatternPath))
                {
                    string source = AbsoluteFromRelative(selectedPatternPath);
                    string destination = UniquePath(Path.Combine(Path.GetDirectoryName(source), safeName + PatternExtension));
                    File.Move(source, destination);
                    selectedPatternPath = RelativePath(destination);
                }
                statusText = AngleMod.L("操作完成", "Done");
            }
            catch (Exception exception)
            {
                statusText = AngleMod.L("操作失败：", "Operation failed: ") + exception.Message;
            }
            nameAction = NameAction.None;
            nameInput = "";
            RequestNativeRebuild();
            RestoreClipboardPanel();
        }

        internal static void MoveEntryToFolder(
            string sourcePath,
            string destinationFolder)
        {
            if (!IsSafePath(sourcePath)
                || !IsSafePath(destinationFolder)
                || !Directory.Exists(destinationFolder)
                || !File.Exists(sourcePath)
                && !Directory.Exists(sourcePath))
            {
                return;
            }

            try
            {
                string sourceFull = Path.GetFullPath(sourcePath)
                    .TrimEnd(Path.DirectorySeparatorChar);
                string destinationFull = Path.GetFullPath(destinationFolder)
                    .TrimEnd(Path.DirectorySeparatorChar);
                string oldRelative = RelativePath(sourceFull);

                if (Directory.Exists(sourceFull))
                {
                    if (destinationFull.StartsWith(
                        sourceFull + Path.DirectorySeparatorChar,
                        StringComparison.OrdinalIgnoreCase)
                        || string.Equals(
                            sourceFull,
                            destinationFull,
                            StringComparison.OrdinalIgnoreCase))
                    {
                        return;
                    }
                    string destination = UniqueDirectory(
                        Path.Combine(destinationFull, Path.GetFileName(sourceFull)));
                    Directory.Move(sourceFull, destination);
                    string newRelative = RelativePath(destination);
                    ReplaceExpandedFolderPrefix(oldRelative, newRelative);
                    if (currentRelativeFolder.Equals(
                            oldRelative,
                            StringComparison.OrdinalIgnoreCase)
                        || currentRelativeFolder.StartsWith(
                            oldRelative + "/",
                            StringComparison.OrdinalIgnoreCase))
                    {
                        currentRelativeFolder =
                            newRelative + currentRelativeFolder.Substring(oldRelative.Length);
                    }
                    if (selectedPatternPath.StartsWith(
                        oldRelative + "/",
                        StringComparison.OrdinalIgnoreCase))
                    {
                        selectedPatternPath =
                            newRelative + selectedPatternPath.Substring(oldRelative.Length);
                    }
                }
                else
                {
                    string destination = UniquePath(
                        Path.Combine(destinationFull, Path.GetFileName(sourceFull)));
                    File.Move(sourceFull, destination);
                    selectedPatternPath = RelativePath(destination);
                }
                statusText = AngleMod.L("移动完成", "Moved");
            }
            catch (Exception exception)
            {
                statusText = AngleMod.L("移动失败：", "Move failed: ") + exception.Message;
            }
            RequestNativeRebuild();
        }

        private static void BeginDelete()
        {
            if (!string.IsNullOrEmpty(selectedPatternPath))
            {
                BeginDelete(AbsoluteFromRelative(selectedPatternPath), false);
            }
            else if (CurrentRelativeFolder() != "Default")
            {
                BeginDelete(CurrentFolderAbsolute(), true);
            }
        }

        internal static void BeginDelete(string path, bool directory)
        {
            scnEditor editor = scnEditor.instance;
            if (editor == null || string.IsNullOrEmpty(path))
            {
                return;
            }

            string itemName = Path.GetFileNameWithoutExtension(path);
            string message = directory
                ? AngleMod.L(
                    "确定删除文件夹“{0}”吗？\n此操作无法撤销。",
                    "Delete the folder \"{0}\"?\nThis cannot be undone.")
                : AngleMod.L(
                    "确定删除键型“{0}”吗？\n此操作无法撤销。",
                    "Delete the pattern \"{0}\"?\nThis cannot be undone.");
            editor.ConfirmPopup(
                string.Format(message, itemName),
                delegate { DeleteConfirmed(path, directory); });
            restoreClipboardAfterPopup = true;
        }

        private static NotePatternFile PatternFromClipboard(
            IEnumerable<object> clipboard,
            IList<scrFloor> selectedFloors,
            scnEditor editor)
        {
            NotePatternFile result = new NotePatternFile();
            PatternPasteFeature.SetCopyReference(result, editor);
            List<scnEditor.FloorData> clipboardFloors =
                clipboard.OfType<scnEditor.FloorData>().ToList();
            int count = selectedFloors == null
                ? clipboardFloors.Count
                : selectedFloors.Count;
            for (int index = 0; index < count; index++)
            {
                scrFloor sourceFloor =
                    selectedFloors != null
                    && index < selectedFloors.Count
                        ? selectedFloors[index]
                        : null;
                scnEditor.FloorData data =
                    index < clipboardFloors.Count
                        ? clipboardFloors[index]
                        : default(scnEditor.FloorData);
                bool hasClipboardData =
                    index < clipboardFloors.Count;
                if (!hasClipboardData && sourceFloor == null)
                {
                    continue;
                }
                char stringDirection = hasClipboardData
                    ? data.stringDirection
                    : sourceFloor != null
                        ? sourceFloor.stringDirection
                        : '?';
                float floatDirection = hasClipboardData
                    ? data.floatDirection
                    : sourceFloor != null
                        ? sourceFloor.floatDirection
                        : 0f;
                List<LevelEvent> sourceEvents = hasClipboardData
                    ? data.levelEventData
                    : editor == null || sourceFloor == null
                        ? new List<LevelEvent>()
                        : editor.events
                            .Where(levelEvent =>
                                levelEvent.floor == sourceFloor.seqID)
                            .ToList();
                List<LevelEvent> sourceDecorations =
                    hasClipboardData
                        ? data.attachedDecorations
                        : editor == null || sourceFloor == null
                            ? new List<LevelEvent>()
                            : editor.decorations
                                .Where(decoration =>
                                    decoration.floor
                                        == sourceFloor.seqID)
                                .ToList();
                Color trackColor = sourceFloor != null
                    && sourceFloor.floorRenderer != null
                        ? sourceFloor.floorRenderer.deselectedColor
                        : Color.white;
                result.floors.Add(new NoteFloorSave
                {
                    stringDirection = stringDirection.ToString(),
                    floatDirection = floatDirection,
                    levelEventData = EncodeEvents(sourceEvents),
                    attachedDecorations =
                        EncodeEvents(sourceDecorations),
                    trackColor = ColorUtility.ToHtmlStringRGBA(trackColor)
                });
            }
            return result;
        }

        internal static string EncodeEvents(List<LevelEvent> events)
        {
            if (events == null)
            {
                return "[]";
            }
            return Json.Serialize(events.Select(levelEvent => levelEvent.Encode()).ToList());
        }

        internal static List<LevelEvent> DecodeEvents(string json)
        {
            List<LevelEvent> result = new List<LevelEvent>();
            if (string.IsNullOrEmpty(json))
            {
                return result;
            }
            List<object> values = Json.Deserialize(json) as List<object>;
            if (values == null)
            {
                return result;
            }
            foreach (object value in values)
            {
                Dictionary<string, object> dictionary = value as Dictionary<string, object>;
                if (dictionary != null && dictionary.Count > 0)
                {
                    result.Add(new LevelEvent(dictionary));
                }
            }
            return result;
        }

        private static NotePatternFile LoadPattern(string path)
        {
            NotePatternFile pattern =
                JsonConvert.DeserializeObject<NotePatternFile>(File.ReadAllText(path));
            PatternPasteFeature.ResolveLegacyPatternAngles(pattern);
            return pattern;
        }

        private static void LoadTabIcon()
        {
            try
            {
                string iconPath = Path.Combine(modEntry.Path, "AngleClipboardIcon.png");
                if (!File.Exists(iconPath))
                {
                    return;
                }
                Texture2D texture = new Texture2D(2, 2, TextureFormat.RGBA32, false);
                Type imageConversionType = Type.GetType(
                    "UnityEngine.ImageConversion, UnityEngine.ImageConversionModule");
                MethodInfo loadImage = imageConversionType?.GetMethod(
                    "LoadImage",
                    new[] { typeof(Texture2D), typeof(byte[]) });
                bool loaded = loadImage != null
                    && (bool)loadImage.Invoke(
                        null,
                        new object[] { texture, File.ReadAllBytes(iconPath) });
                if (!loaded)
                {
                    return;
                }
                texture.wrapMode = TextureWrapMode.Clamp;
                TabIcon = Sprite.Create(
                    texture,
                    new Rect(0f, 0f, texture.width, texture.height),
                    new Vector2(0.5f, 0.5f),
                    100f);
            }
            catch (Exception exception)
            {
                if (modEntry != null)
                {
                    modEntry.Logger.Error("Failed to load clipboard tab icon: " + exception);
                }
            }
        }

#if false
        private static void EnsureStyles()
        {
            if (smallStyle == null)
            {
                smallStyle = new GUIStyle(GUI.skin.label)
                {
                    fontSize = 11,
                    clipping = TextClipping.Clip,
                    normal = { textColor = new Color(0.82f, 0.86f, 0.92f, 1f) }
                };
            }
            if (buttonStyle == null)
            {
                buttonStyle = CreateOutlineButtonStyle(
                    new Color(0.045f, 0.055f, 0.075f, 0.96f),
                    new Color(0.95f, 0.97f, 1f, 1f),
                    new Color(0.075f, 0.09f, 0.12f, 1f));
            }
            if (dangerStyle == null)
            {
                dangerStyle = CreateOutlineButtonStyle(
                    new Color(0.34f, 0.045f, 0.045f, 0.98f),
                    new Color(1f, 0.35f, 0.3f, 1f),
                    new Color(0.55f, 0.07f, 0.06f, 1f));
            }
        }

        private static GUIStyle CreateOutlineButtonStyle(
            Color fill,
            Color border,
            Color hoverFill)
        {
            GUIStyle style = new GUIStyle(GUI.skin.button)
            {
                border = new RectOffset(10, 10, 10, 10),
                padding = new RectOffset(12, 12, 6, 6),
                alignment = TextAnchor.MiddleCenter,
                clipping = TextClipping.Clip,
                normal =
                {
                    background = CreateRoundedTexture(64, fill, border, 2, 10),
                    textColor = Color.white
                },
                hover =
                {
                    background = CreateRoundedTexture(64, hoverFill, border, 2, 10),
                    textColor = Color.white
                },
                active =
                {
                    background = CreateRoundedTexture(64, fill * 0.8f, border, 2, 10),
                    textColor = Color.white
                }
            };
            return style;
        }

        private static Texture2D CreateRoundedTexture(
            int size,
            Color fill,
            Color border,
            int borderWidth,
            int radius)
        {
            Texture2D texture = new Texture2D(size, size, TextureFormat.RGBA32, false);
            Color clear = new Color(0f, 0f, 0f, 0f);
            for (int y = 0; y < size; y++)
            {
                for (int x = 0; x < size; x++)
                {
                    float edgeX = Mathf.Min(x, size - 1 - x);
                    float edgeY = Mathf.Min(y, size - 1 - y);
                    bool corner = edgeX < radius && edgeY < radius
                        && Vector2.Distance(
                            new Vector2(x, y),
                            new Vector2(
                                x < radius ? radius : size - 1 - radius,
                                y < radius ? radius : size - 1 - radius))
                            > radius;
                    if (corner)
                    {
                        texture.SetPixel(x, y, clear);
                    }
                    else
                    {
                        bool isBorder =
                            x < borderWidth
                            || y < borderWidth
                            || x >= size - borderWidth
                            || y >= size - borderWidth;
                        texture.SetPixel(x, y, isBorder ? border : fill);
                    }
                }
            }
            texture.Apply();
            return texture;
        }

        private static float UiScale()
        {
            return Mathf.Clamp(Screen.width / 1920f, 0.75f, 1.4f);
        }
#endif

        private static string CurrentRelativeFolder()
        {
            return currentRelativeFolder.Replace('\\', '/');
        }

        private static string CurrentFolderAbsolute()
        {
            string folder = Path.Combine(noteSaveRoot, currentRelativeFolder);
            Directory.CreateDirectory(folder);
            return folder;
        }

        private static string FindOrCreateRootFolder()
        {
            if (noteSaveRoot == null)
            {
                throw new InvalidOperationException("NoteSave is not initialized.");
            }
            string defaultFolder = Path.Combine(noteSaveRoot, "Default");
            if (Directory.Exists(defaultFolder))
            {
                return "Default";
            }
            string firstFolder = Directory.GetDirectories(noteSaveRoot)
                .OrderBy(path => Path.GetFileName(path), StringComparer.OrdinalIgnoreCase)
                .FirstOrDefault();
            if (firstFolder != null)
            {
                return Path.GetFileName(firstFolder);
            }
            Directory.CreateDirectory(defaultFolder);
            return "Default";
        }

        private static void EnsureStorageRoot()
        {
            if (noteSaveRoot == null)
            {
                throw new InvalidOperationException("NoteSave is not initialized.");
            }
            Directory.CreateDirectory(noteSaveRoot);
            if (string.IsNullOrWhiteSpace(currentRelativeFolder)
                || !Directory.Exists(Path.Combine(
                    noteSaveRoot,
                    currentRelativeFolder)))
            {
                currentRelativeFolder = FindOrCreateRootFolder();
            }
        }

        private static string RelativePath(string fullPath)
        {
            string root = Path.GetFullPath(noteSaveRoot)
                .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
                + Path.DirectorySeparatorChar;
            string full = Path.GetFullPath(fullPath);
            if (!full.StartsWith(root, StringComparison.OrdinalIgnoreCase))
            {
                return "";
            }
            return full.Substring(root.Length).Replace('\\', '/');
        }

        private static string AbsoluteFromRelative(string relative)
        {
            string full = Path.GetFullPath(Path.Combine(noteSaveRoot, relative));
            return IsSafePath(full) ? full : "";
        }

        private static bool IsSafePath(string path)
        {
            string root = Path.GetFullPath(noteSaveRoot)
                .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
                + Path.DirectorySeparatorChar;
            string full = Path.GetFullPath(path);
            return full.StartsWith(root, StringComparison.OrdinalIgnoreCase);
        }

        private static string UniqueDirectory(string path)
        {
            if (!Directory.Exists(path) && !File.Exists(path))
            {
                return path;
            }
            for (int index = 2; ; index++)
            {
                string candidate = path + "_" + index;
                if (!Directory.Exists(candidate) && !File.Exists(candidate))
                {
                    return candidate;
                }
            }
        }

        private static string UniquePath(string path)
        {
            if (!File.Exists(path) && !Directory.Exists(path))
            {
                return path;
            }
            string directory = Path.GetDirectoryName(path);
            string name = Path.GetFileNameWithoutExtension(path);
            string extension = Path.GetExtension(path);
            for (int index = 2; ; index++)
            {
                string candidate = Path.Combine(directory, name + "_" + index + extension);
                if (!File.Exists(candidate) && !Directory.Exists(candidate))
                {
                    return candidate;
                }
            }
        }

        private static string SanitizeName(string value)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                return "";
            }
            char[] invalid = Path.GetInvalidFileNameChars();
            StringBuilder builder = new StringBuilder(value.Trim());
            foreach (char character in invalid)
            {
                builder.Replace(character, '_');
            }
            string result = builder.ToString().Trim('.');
            return result == "." || result == ".." ? "" : result;
        }

        private static void Repaint()
        {
            if (Event.current != null)
            {
                Event.current.Use();
            }
        }
    }

    [HarmonyPatch(typeof(scnEditor), "MultiCopyFloors")]
    internal static class NoteSaveCopyPatch
    {
        private static void Postfix(scnEditor __instance, bool cut)
        {
            NoteSaveFeature.OnFloorsCopied(__instance, cut);
        }
    }

    [HarmonyPatch(typeof(PasteFloorEditorAction), "Execute")]
    internal static class NoteSavePastePatch
    {
        private static bool Prefix(scnEditor editor)
        {
            if (NoteSaveFeature.OverwritePasteEnabled)
            {
                return !NoteSaveFeature.TryOverwritePaste(editor);
            }
            NoteSaveFeature.PrepareSavedPatternForPaste(editor);
            return true;
        }
    }
}
