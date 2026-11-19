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
using HarmonyLib;
using Newtonsoft.Json;
using TMPro;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;

namespace Angle
{
    internal static class NoteSavePreview
    {
        private static readonly System.Reflection.PropertyInfo UnsavedChangesProperty =
            typeof(scnEditor).GetProperty(
                "unsavedChanges",
                System.Reflection.BindingFlags.Instance
                | System.Reflection.BindingFlags.Public
                | System.Reflection.BindingFlags.NonPublic);
        private static readonly MethodInfo ChangeStateMethod =
            typeof(scrController).GetMethod(
                "ChangeState",
                BindingFlags.Instance | BindingFlags.NonPublic);
        private static readonly MethodInfo ShowEventPickerMethod =
            typeof(scnEditor).GetMethod(
                "ShowEventPicker",
                BindingFlags.Instance | BindingFlags.NonPublic);

        private sealed class UiState
        {
            internal GameObject Object;
            internal bool Active;
        }

        private sealed class GraphicState
        {
            internal Graphic Graphic;
            internal bool Enabled;
        }

        private sealed class EndpointVisualState
        {
            internal bool FirstActive;
            internal bool LastActive;
            internal Vector3 FirstScale;
            internal Vector3 LastScale;
            internal bool FirstHoldActive;
            internal bool LastHoldActive;
        }

        private static bool active;
        private static string patternPath;
        private static NotePatternFile originalPattern;
        private static LevelData originalData;
        private static EndpointVisualState originalEndpointVisuals;
        private static int endpointRestoreFramesRemaining;
        private static scrFloor previewPlaybackStartFloor;
        private static int originalFirstFloorSeqID = -1;
        private static bool playbackCameraSyncPending;
        private static bool previewEventBarRequested;
        private static bool previewEndPauseInjected;
        private static bool originalUnsaved;
        private static bool originalAuto;
        private static bool originalNoFail;
        private static List<scnEditor.LevelState> originalUndoStates;
        private static List<scnEditor.LevelState> originalRedoStates;
        private static List<int> originalSelectedFloors;
        private static Vector3 originalCameraPosition;
        private static Quaternion originalCameraRotation;
        private static float originalCameraSize;
        private static LevelEventType originalSettingsPanelType;
        private static readonly List<UiState> hiddenUi = new List<UiState>();
        private static readonly List<GraphicState> hiddenGraphics =
            new List<GraphicState>();
        private static GameObject exitButton;
        private static bool unsavedPopupShowing;

        internal static bool IsActive => active;

        internal static void Open(string relativePatternPath)
        {
            if (active || string.IsNullOrEmpty(relativePatternPath))
            {
                return;
            }

            scnEditor editor = scnEditor.instance;
            string path = NoteSaveFeature.ToAbsolutePath(relativePatternPath);
            if (editor == null
                || editor.playMode
                || string.IsNullOrEmpty(path)
                || !File.Exists(path))
            {
                return;
            }

            NotePatternFile pattern = NoteSaveFeature.ReadPattern(path);
            if (pattern == null || pattern.floors.Count == 0)
            {
                editor.ShowNotification(
                    AngleMod.L("键型中没有轨道", "This pattern has no tracks"));
                return;
            }
            NormalizePatternEventData(pattern);

            patternPath = path;
            originalPattern = pattern;
            unsavedPopupShowing = false;
            originalData = editor.levelData.Copy();
            originalEndpointVisuals = CaptureEndpointVisualState(editor);
            originalFirstFloorSeqID = ADOBase.controller == null
                || ADOBase.controller.firstFloor == null
                ? -1
                : ADOBase.controller.firstFloor.seqID;
            previewPlaybackStartFloor = null;
            playbackCameraSyncPending = false;
            previewEventBarRequested = false;
            previewEndPauseInjected = false;
            originalUnsaved = GetUnsavedChanges(editor);
            originalAuto = RDC.auto;
            originalNoFail = ADOBase.controller != null
                && ADOBase.controller.noFail;
            originalUndoStates =
                new List<scnEditor.LevelState>(editor.undoStates);
            originalRedoStates =
                new List<scnEditor.LevelState>(editor.redoStates);
            originalSelectedFloors = editor.selectedFloors
                .Select(floor => floor.seqID)
                .ToList();
            originalSettingsPanelType = editor.settingsPanel.selectedEventType;
            if (editor.camera != null)
            {
                originalCameraPosition = editor.camera.transform.position;
                originalCameraRotation = editor.camera.transform.rotation;
                originalCameraSize = editor.camUserSizeMultiplier;
            }

            LevelData previewData = editor.levelData.Copy();
            previewData.pathData = "";
            previewData.isOldLevel = false;
            previewData.angleData = pattern.floors
                .Select(floor => floor.floatDirection)
                .ToList();
            previewData.levelEvents =
                CreatePreviewEvents(pattern);
            previewData.decorations =
                CreatePreviewDecorations(pattern);
            previewData.backgroundSettings["bgImage"] = "";
            previewData.miscSettings["bgVideo"] = "";

            NoteSaveBrowser.Close();
            NoteSaveFeature.NativePanelOpen = false;
            AngleMod.PreparePreviewMode(editor);
            CaptureAndHideUi(editor);
            try
            {
                editor.customLevel.levelData = previewData;
                editor.RemakePath();
                HideEndpointFloorVisuals(editor);
                CenterPreviewCamera(editor);
                editor.undoStates.Clear();
                editor.redoStates.Clear();
                SetUnsavedChanges(editor, false);
                editor.DeselectFloors(skipSaving: true);
                originalPattern = BuildPatternFromEditor(editor);
                active = true;
                CreateExitButton(editor);
                MaintainUi(editor);
            }
            catch (Exception exception)
            {
                active = false;
                if (editor.playMode)
                {
                    editor.SwitchToEditMode();
                }
                RDC.auto = originalAuto;
                if (ADOBase.controller != null)
                {
                    ADOBase.controller.noFail = originalNoFail;
                }
                editor.customLevel.levelData = originalData;
                editor.RemakePath();
                RestoreEndpointVisualState(editor);
                endpointRestoreFramesRemaining = 3;
                RestoreOriginalFirstFloor(editor);
                RestoreUi();
                editor.ShowNotification(
                    AngleMod.L("无法打开预览：", "Could not open preview: ")
                    + exception.Message);
                NoteSaveFeature.RestoreClipboardPanel();
            }
        }

        internal static void Update()
        {
            scnEditor editor = scnEditor.instance;
            if (!active)
            {
                if (endpointRestoreFramesRemaining > 0)
                {
                    if (editor != null)
                    {
                        RestoreEndpointVisualState(editor);
                    }
                    endpointRestoreFramesRemaining--;
                    if (endpointRestoreFramesRemaining == 0)
                    {
                        originalEndpointVisuals = null;
                    }
                }
                return;
            }
            if (editor == null)
            {
                return;
            }
            if (!editor.playMode)
            {
                RemovePreviewEndPause(editor);
                ShowPreviewEventBar(editor);
            }
            else
            {
                previewEventBarRequested = false;
                PreparePlaybackFrame(editor);
                SynchronizePlaybackCamera();
            }
            MaintainUi(editor);
            if (editor.camera != null)
            {
                Vector3 angles = editor.camera.transform.localEulerAngles;
                angles.z = Mathf.Repeat(angles.z, 360f);
                editor.camera.transform.localEulerAngles = angles;
            }
        }

        internal static void SaveCurrentPattern()
        {
            scnEditor editor = scnEditor.instance;
            if (!active
                || editor == null
                || string.IsNullOrEmpty(patternPath))
            {
                return;
            }

            NotePatternFile pattern = BuildPatternFromEditor(editor);

            try
            {
                File.WriteAllText(
                    patternPath,
                    JsonConvert.SerializeObject(pattern, Formatting.Indented),
                    new UTF8Encoding(false));
                originalPattern = pattern;
                editor.ShowNotification(
                    AngleMod.L("键型已保存", "Pattern saved"));
            }
            catch (Exception exception)
            {
                editor.ShowNotification(
                    AngleMod.L("保存失败：", "Save failed: ")
                    + exception.Message);
            }
        }

        internal static void RequestClose()
        {
            if (!active)
            {
                return;
            }
            if (HasUnsavedChanges())
            {
                ShowUnsavedPopup();
            }
            else
            {
                Close();
            }
        }

        private static bool HasUnsavedChanges()
        {
            scnEditor editor = scnEditor.instance;
            if (!active
                || editor == null
                || originalPattern == null
                || editor.levelData == null
                || editor.levelData.angleData == null)
            {
                return false;
            }
            NotePatternFile current = BuildPatternFromEditor(editor);
            return !string.Equals(
                JsonConvert.SerializeObject(current),
                JsonConvert.SerializeObject(originalPattern),
                StringComparison.Ordinal);
        }

        private static NotePatternFile BuildPatternFromEditor(
            scnEditor editor)
        {
            NotePatternFile pattern = new NotePatternFile
            {
                referenceAngle = originalPattern.referenceAngle,
                referenceIsCCW = originalPattern.referenceIsCCW
            };
            for (int index = 0;
                index < editor.levelData.angleData.Count;
                index++)
            {
                int floorID = index + 1;
                char stringDirection =
                    floorID < editor.floors.Count
                        ? editor.floors[floorID].stringDirection
                        : '?';
                List<LevelEvent> floorEvents = editor.events
                    .Where(levelEvent =>
                        levelEvent != null
                        && levelEvent.floor == floorID
                        && !IsPreviewEndPause(levelEvent))
                    .ToList();
                List<LevelEvent> floorDecorations = editor.decorations
                    .Where(decoration =>
                        decoration != null
                        && decoration.floor == floorID)
                    .ToList();
                string trackColor = index < originalPattern.floors.Count
                    ? originalPattern.floors[index].trackColor
                    : "";
                if (string.IsNullOrEmpty(trackColor)
                    && floorID < editor.floors.Count
                    && editor.floors[floorID].floorRenderer != null)
                {
                    trackColor = ColorUtility.ToHtmlStringRGBA(
                        editor.floors[floorID]
                            .floorRenderer.deselectedColor);
                }
                pattern.floors.Add(new NoteFloorSave
                {
                    stringDirection = stringDirection.ToString(),
                    floatDirection =
                        editor.levelData.angleData[index],
                    levelEventData =
                        NoteSaveFeature.EncodeEvents(floorEvents),
                    attachedDecorations =
                        NoteSaveFeature.EncodeEvents(floorDecorations),
                    trackColor = trackColor
                });
            }
            return pattern;
        }

        private static bool IsPreviewEndPause(LevelEvent levelEvent)
        {
            return levelEvent != null
                && levelEvent.eventType == LevelEventType.Pause
                && levelEvent.GetBool("anglePreviewEndPause");
        }

        private static void ShowUnsavedPopup()
        {
            scnEditor editor = scnEditor.instance;
            if (editor == null || editor.unsavedChangesPopupContainer == null)
            {
                Close();
                return;
            }
            editor.ShowPopup(
                show: true,
                scnEditor.PopupType.UnsavedChanges);
            unsavedPopupShowing = true;
        }

        internal static bool HandleNativeUnsavedSave()
        {
            if (!active)
            {
                return false;
            }
            SaveCurrentPattern();
            CloseNativeUnsavedPopup();
            Close(save: false);
            return true;
        }

        internal static bool HandleNativeUnsavedDiscard()
        {
            if (!active)
            {
                return false;
            }
            CloseNativeUnsavedPopup();
            Close(save: false);
            return true;
        }

        internal static void CloseNativeUnsavedPopup()
        {
            scnEditor editor = scnEditor.instance;
            unsavedPopupShowing = false;
            if (editor != null)
            {
                editor.ShowPopup(
                    show: false,
                    scnEditor.PopupType.UnsavedChanges);
            }
        }

        internal static void Close(bool save = false)
        {
            if (!active)
            {
                return;
            }

            scnEditor editor = scnEditor.instance;
            string closingRelative = NoteSaveFeature.ToRelativePath(patternPath);
            if (save)
            {
                SaveCurrentPattern();
            }
            active = false;
            unsavedPopupShowing = false;

            if (editor != null)
            {
                if (editor.playMode)
                {
                    editor.SwitchToEditMode();
                }
                editor.customLevel.levelData = originalData;
                editor.RemakePath();
                RestoreEndpointVisualState(editor);
                endpointRestoreFramesRemaining = 3;
                RestoreOriginalFirstFloor(editor);
                editor.undoStates = originalUndoStates;
                editor.redoStates = originalRedoStates;
                SetUnsavedChanges(editor, originalUnsaved);
                editor.DeselectFloors(skipSaving: true);
                RestoreSelection(editor);
                if (editor.camera != null)
                {
                    editor.camera.transform.position = originalCameraPosition;
                    editor.camera.transform.rotation = originalCameraRotation;
                    editor.camUserSizeMultiplier = originalCameraSize;
                    AngleMod.RestoreCameraSession(editor);
                }
            }
            RDC.auto = originalAuto;
            if (ADOBase.controller != null)
            {
                ADOBase.controller.noFail = originalNoFail;
            }

            DestroyExitButton();
            RestoreUi();
            patternPath = "";
            originalPattern = null;
            originalData = null;
            previewPlaybackStartFloor = null;
            originalUndoStates = null;
            originalRedoStates = null;
            originalSelectedFloors = null;
            NoteSaveFeature.SelectPattern(closingRelative);
            NoteSaveFeature.RequestNativeRebuild();
            if (editor != null)
            {
                editor.settingsPanel.ShowInspector(true, forceAction: true);
                editor.settingsPanel.ShowPanel(NoteSaveNativeUI.EventType);
            }
        }

        internal static void Shutdown()
        {
            if (active)
            {
                Close(save: false);
            }
            else
            {
                DestroyExitButton();
                RestoreUi();
                endpointRestoreFramesRemaining = 0;
                originalEndpointVisuals = null;
                previewPlaybackStartFloor = null;
                originalFirstFloorSeqID = -1;
            }
        }

        internal static void PreparePlayback(scnEditor editor)
        {
            if (!active || editor == null)
            {
                return;
            }
            if (previewPlaybackStartFloor != null
                && ADOBase.controller != null)
            {
                ADOBase.controller.firstFloor =
                    previewPlaybackStartFloor;
            }
            RDC.auto = true;
            if (ADOBase.controller != null)
            {
                ADOBase.controller.noFail = true;
            }
            ADOBase.playerManager.SetAllPlayerResponsive(true);
        }

        private static EventsArray<LevelEvent> CreatePreviewEvents(
            NotePatternFile pattern)
        {
            EventsArray<LevelEvent> result =
                new EventsArray<LevelEvent>();
            for (int index = 0; index < pattern.floors.Count; index++)
            {
                foreach (LevelEvent levelEvent in DecodeMappedEvents(
                    pattern.floors[index].levelEventData,
                    index + 1))
                {
                    result.Add(levelEvent);
                }
            }
            return result;
        }

        private static DecorationsArray<LevelEvent> CreatePreviewDecorations(
            NotePatternFile pattern)
        {
            DecorationsArray<LevelEvent> result =
                new DecorationsArray<LevelEvent>();
            for (int index = 0; index < pattern.floors.Count; index++)
            {
                foreach (LevelEvent decoration in DecodeMappedEvents(
                    pattern.floors[index].attachedDecorations,
                    index + 1))
                {
                    result.Add(decoration);
                }
            }
            return result;
        }

        private static void NormalizePatternEventData(
            NotePatternFile pattern)
        {
            for (int index = 0; index < pattern.floors.Count; index++)
            {
                NoteFloorSave floor = pattern.floors[index];
                floor.levelEventData = NoteSaveFeature.EncodeEvents(
                    DecodeMappedEvents(
                        floor.levelEventData,
                        index + 1));
                floor.attachedDecorations = NoteSaveFeature.EncodeEvents(
                    DecodeMappedEvents(
                        floor.attachedDecorations,
                        index + 1));
            }
        }

        private static List<LevelEvent> DecodeMappedEvents(
            string json,
            int floor)
        {
            List<LevelEvent> events =
                NoteSaveFeature.DecodeEvents(json);
            foreach (LevelEvent levelEvent in events)
            {
                levelEvent.floor = floor;
                levelEvent.locked = false;
            }
            return events;
        }

        private static void ShowPreviewEventBar(scnEditor editor)
        {
            if (!active
                || editor == null
                || editor.playMode
                || editor.levelEventsBar == null)
            {
                return;
            }
            editor.levelEventsBar.gameObject.SetActive(true);
            if (!previewEventBarRequested)
            {
                Transform canvas = editor.levelEditorCanvas == null
                    ? null
                    : editor.levelEditorCanvas.transform;
                ActivateTransformPath(
                    editor.levelEventsBar.transform,
                    canvas);
                EnablePreviewGraphics(
                    editor.levelEventsBar.gameObject);
                if (editor.levelEventsBarButtons != null)
                {
                    editor.levelEventsBarButtons.gameObject
                        .SetActive(true);
                    EnablePreviewGraphics(
                        editor.levelEventsBarButtons.gameObject);
                }
                if (editor.levelEventsBarCategories != null)
                {
                    editor.levelEventsBarCategories.gameObject
                        .SetActive(true);
                    EnablePreviewGraphics(
                        editor.levelEventsBarCategories.gameObject);
                }
                if (editor.levelEventsPanel != null
                    && editor.levelEventsPanel.tabs != null)
                {
                    ActivateTransformPath(
                        editor.levelEventsPanel.tabs,
                        canvas);
                    EnablePreviewGraphics(
                        editor.levelEventsPanel.tabs.gameObject);
                }
                ShowEventPickerMethod?.Invoke(
                    editor,
                    new object[] { true });
                previewEventBarRequested = true;
            }
        }

        private static void ActivateTransformPath(
            Transform target,
            Transform stopAt)
        {
            Transform current = target;
            while (current != null && current != stopAt)
            {
                if (!current.gameObject.activeSelf)
                {
                    current.gameObject.SetActive(true);
                }
                current = current.parent;
            }
        }

        private static void EnablePreviewGraphics(GameObject root)
        {
            if (root == null)
            {
                return;
            }
            foreach (Graphic graphic in
                root.GetComponentsInChildren<Graphic>(true))
            {
                graphic.enabled = true;
            }
        }

        internal static void ForcePreviewEventPickerOpen(ref bool show)
        {
            if (!active
                || scnEditor.instance == null
                || scnEditor.instance.playMode)
            {
                return;
            }
            show = true;
        }

        private static void SynchronizePlaybackCamera()
        {
            if (!playbackCameraSyncPending
                || previewPlaybackStartFloor == null
                || ADOBase.controller == null
                || !ADOBase.conductor.hasSongStarted
                || ADOBase.controller.currentState != States.Checkpoint
                && ADOBase.controller.currentState != States.PlayerControl)
            {
                return;
            }
            foreach (scrPlayer player in ADOBase.playerManager)
            {
                scrPlanet planet =
                    player.planetarySystem.chosenPlanet;
                if (planet != null && scrCamera.instance != null)
                {
                    scrCamera.instance.ViewObjectInstant(
                        planet.transform);
                }
            }
            playbackCameraSyncPending = false;
        }

        private static void PreparePlaybackFrame(scnEditor editor)
        {
            if (previewPlaybackStartFloor == null
                || ADOBase.controller == null)
            {
                return;
            }
            RDC.auto = true;
            ADOBase.controller.noFail = true;
            editor.inStrictlyEditingMode = false;
            ADOBase.playerManager.SetAllPlayerResponsive(true);
            if (!ADOBase.conductor.hasSongStarted
                || ADOBase.controller.currentState != States.Start
                && ADOBase.controller.currentState != States.Checkpoint
                || ADOBase.conductor.songposition_minusi
                    < previewPlaybackStartFloor.entryTimeAfterExtraBeats)
            {
                return;
            }
            ADOBase.controller.currentSeqID =
                previewPlaybackStartFloor.seqID;
            ChangeStateMethod?.Invoke(
                ADOBase.controller,
                new object[] { States.PlayerControl });
        }

        private static void EnsurePreviewEndPause(scnEditor editor)
        {
            if (editor == null
                || editor.levelData == null
                || editor.levelData.angleData == null
                || editor.levelData.levelEvents == null)
            {
                return;
            }
            int desiredFloor =
                editor.levelData.angleData.Count - 1;
            bool pauseAtDesiredFloor = editor.levelData.levelEvents.Any(
                levelEvent =>
                    levelEvent != null
                    && levelEvent.eventType == LevelEventType.Pause
                    && levelEvent.GetBool("anglePreviewEndPause")
                    && levelEvent.floor == desiredFloor);
            int markerCount = editor.levelData.levelEvents.Count(
                levelEvent =>
                    levelEvent != null
                    && levelEvent.eventType == LevelEventType.Pause
                    && levelEvent.GetBool("anglePreviewEndPause"));
            if (markerCount == 1 && pauseAtDesiredFloor)
            {
                return;
            }

            editor.levelData.levelEvents.RemoveAll(
                levelEvent =>
                    levelEvent != null
                    && levelEvent.eventType == LevelEventType.Pause
                    && levelEvent.GetBool("anglePreviewEndPause"));
            if (desiredFloor > 0)
            {
                LevelEvent endPause = new LevelEvent(
                    desiredFloor,
                    LevelEventType.Pause);
                endPause["anglePreviewEndPause"] = true;
                endPause["duration"] = 999999f;
                endPause["countdownTicks"] = 0;
                endPause["angleCorrectionDir"] =
                    AngleCorrectionDirection.None;
                endPause.visible = false;
                endPause.locked = true;
                editor.levelData.levelEvents.Add(endPause);
                previewEndPauseInjected = true;
            }
            editor.ApplyEventsToFloors();
        }

        private static void RemovePreviewEndPause(scnEditor editor)
        {
            if (!previewEndPauseInjected
                || editor == null
                || editor.levelData == null
                || editor.levelData.levelEvents == null)
            {
                return;
            }
            int removed = editor.levelData.levelEvents.RemoveAll(
                levelEvent =>
                    levelEvent != null
                    && levelEvent.eventType == LevelEventType.Pause
                    && levelEvent.GetBool("anglePreviewEndPause"));
            previewEndPauseInjected = false;
            if (removed > 0)
            {
                editor.ApplyEventsToFloors();
            }
        }

        internal static bool PreparePlaybackStart(scnEditor editor)
        {
            if (!active || editor == null)
            {
                return true;
            }
            if (editor.selectedFloors == null
                || editor.selectedFloors.Count == 0)
            {
                editor.ShowNotification(AngleMod.L(
                    "请先选择一条轨道作为预览起点",
                    "Select a floor as the preview start"));
                return false;
            }
            if (editor.selectedFloors.Count > 1)
            {
                scrFloor start = editor.selectedFloors
                    .Where(floor => floor != null)
                    .OrderBy(floor => floor.seqID)
                    .FirstOrDefault();
                if (start == null)
                {
                    editor.ShowNotification(AngleMod.L(
                        "请先选择一条轨道作为预览起点",
                        "Select a floor as the preview start"));
                    return false;
                }
                editor.DeselectFloors(skipSaving: true);
                editor.SelectFloor(start);
            }
            scrFloor playbackStart = editor.selectedFloors
                .FirstOrDefault();
            if (playbackStart == null)
            {
                editor.ShowNotification(AngleMod.L(
                    "请先选择一条轨道作为预览起点",
                    "Select a floor as the preview start"));
                return false;
            }
            EnsurePreviewEndPause(editor);
            previewPlaybackStartFloor = playbackStart;
            if (ADOBase.controller != null)
            {
                ADOBase.controller.firstFloor = playbackStart;
                ADOBase.controller.noFail = true;
            }
            RDC.auto = true;
            ADOBase.playerManager.SetAllPlayerResponsive(true);
            playbackCameraSyncPending = true;
            return true;
        }

        private static void RestoreOriginalFirstFloor(scnEditor editor)
        {
            if (ADOBase.controller == null)
            {
                return;
            }
            ADOBase.controller.firstFloor =
                editor != null
                && editor.floors != null
                && originalFirstFloorSeqID >= 0
                && originalFirstFloorSeqID < editor.floors.Count
                    ? editor.floors[originalFirstFloorSeqID]
                    : null;
            previewPlaybackStartFloor = null;
            playbackCameraSyncPending = false;
            originalFirstFloorSeqID = -1;
        }

        internal static bool HandleEscape(scnEditor editor)
        {
            bool escapePressed = Input.GetKeyDown(KeyCode.Escape);
            bool popupVisible = editor != null
                && editor.popupPanel != null
                && editor.popupPanel.activeSelf;
            if (unsavedPopupShowing && !popupVisible)
            {
                unsavedPopupShowing = false;
            }
            if (unsavedPopupShowing && popupVisible)
            {
                if (escapePressed)
                {
                    CloseNativeUnsavedPopup();
                }
                return true;
            }
            if (!active
                || editor == null
                || editor.playMode
                || !escapePressed)
            {
                return false;
            }
            if (editor.selectedFloors != null
                && editor.selectedFloors.Count > 0)
            {
                editor.DeselectFloors(skipSaving: true);
                return true;
            }
            RequestClose();
            return true;
        }

        private static void CaptureAndHideUi(scnEditor editor)
        {
            hiddenUi.Clear();
            Track(editor.fileActionsPanel);
            Track(editor.shortcutsPanel);
            Track(editor.levelStringPanel);
            Track(editor.inspectorTabs);
            Track(editor.settingsPanel == null
                ? null
                : editor.settingsPanel.rect.gameObject);
            Track(editor.controlsTip == null
                ? null
                : editor.controlsTip.gameObject);
            Track(editor.filenameText == null
                ? null
                : editor.filenameText.gameObject);
            Track(editor.buttonFileActionDropdown == null
                ? null
                : editor.buttonFileActionDropdown.gameObject);
            Track(editor.buttonNew == null ? null : editor.buttonNew.gameObject);
            Track(editor.buttonOpen == null ? null : editor.buttonOpen.gameObject);
            Track(editor.buttonOpenRecent == null
                ? null
                : editor.buttonOpenRecent.gameObject);
            Track(editor.buttonOpenURL == null
                ? null
                : editor.buttonOpenURL.gameObject);
            Track(editor.buttonSave == null ? null : editor.buttonSave.gameObject);
            Track(editor.buttonSaveAs == null
                ? null
                : editor.buttonSaveAs.gameObject);
            Track(editor.buttonPreferences == null
                ? null
                : editor.buttonPreferences.gameObject);
            Track(editor.buttonExit == null ? null : editor.buttonExit.gameObject);
            Track(editor.buttonSettings == null
                ? null
                : editor.buttonSettings.gameObject);
            Track(editor.buttonHelp == null ? null : editor.buttonHelp.gameObject);
            Track(editor.buttonDiscord == null
                ? null
                : editor.buttonDiscord.gameObject);
            Track(editor.playPause == null ? null : editor.playPause.gameObject);
            Track(editor.rewind == null ? null : editor.rewind.gameObject);
            Track(editor.buttonAuto == null ? null : editor.buttonAuto.gameObject);
            Track(editor.buttonNextPage == null
                ? null
                : editor.buttonNextPage.gameObject);
            Track(editor.buttonPrevPage == null
                ? null
                : editor.buttonPrevPage.gameObject);
            Track(editor.buttonNoFail == null
                ? null
                : editor.buttonNoFail.gameObject);
            Track(editor.fileIcon == null ? null : editor.fileIcon.gameObject);
            Track(editor.fileArrow == null ? null : editor.fileArrow.gameObject);
            Track(editor.lockIcon == null ? null : editor.lockIcon.gameObject);
            Track(editor.lockBackground == null
                ? null
                : editor.lockBackground.gameObject);
            Track(editor.editorDifficultySelector == null
                ? null
                : editor.editorDifficultySelector.gameObject);
            Track(editor.speedIndicator == null
                ? null
                : editor.speedIndicator.gameObject);
            Track(editor.selectingFloorIDRectTransform == null
                ? null
                : editor.selectingFloorIDRectTransform.gameObject);
            Track(editor.findFloorPanel);
            KeepTrackedEventUiAncestorsActive(editor);
            foreach (UiState state in hiddenUi)
            {
                state.Object.SetActive(false);
            }
            CaptureGraphics(editor);
        }

        private static void KeepTrackedEventUiAncestorsActive(
            scnEditor editor)
        {
            if (editor == null)
            {
                return;
            }
            Transform eventsBar = editor.levelEventsBar == null
                ? null
                : editor.levelEventsBar.transform;
            Transform eventsPanel =
                editor.levelEventsPanel == null
                || editor.levelEventsPanel.rect == null
                    ? null
                    : editor.levelEventsPanel.rect.transform;
            Transform eventTabs =
                editor.levelEventsPanel == null
                || editor.levelEventsPanel.tabs == null
                    ? null
                    : editor.levelEventsPanel.tabs.transform;
            hiddenUi.RemoveAll(state =>
                state.Object == null
                || eventsBar != null
                && eventsBar.IsChildOf(state.Object.transform)
                || eventsPanel != null
                && eventsPanel.IsChildOf(state.Object.transform)
                || eventTabs != null
                && eventTabs.IsChildOf(state.Object.transform));
        }

        private static bool GetUnsavedChanges(scnEditor editor)
        {
            try
            {
                return UnsavedChangesProperty != null
                    && editor != null
                    && (bool)UnsavedChangesProperty.GetValue(editor, null);
            }
            catch
            {
                return false;
            }
        }

        private static void SetUnsavedChanges(scnEditor editor, bool value)
        {
            try
            {
                if (UnsavedChangesProperty != null && editor != null)
                {
                    UnsavedChangesProperty.SetValue(editor, value, null);
                }
            }
            catch
            {
            }
        }

        private static void Track(GameObject target)
        {
            if (target == null
                || hiddenUi.Any(state => state.Object == target))
            {
                return;
            }
            hiddenUi.Add(new UiState
            {
                Object = target,
                Active = target.activeSelf
            });
        }

        private static void MaintainUi(scnEditor editor)
        {
            foreach (UiState state in hiddenUi)
            {
                if (state.Object != null && state.Object.activeSelf)
                {
                    state.Object.SetActive(false);
                }
            }
            foreach (GraphicState state in hiddenGraphics)
            {
                if (state.Graphic != null && state.Graphic.enabled)
                {
                    state.Graphic.enabled = false;
                }
            }
            HideEndpointFloorVisuals(editor);
        }

        private static EndpointVisualState CaptureEndpointVisualState(
            scnEditor editor)
        {
            if (editor == null || editor.floors == null
                || editor.floors.Count == 0)
            {
                return null;
            }
            scrFloor first = editor.floors[0];
            scrFloor last = editor.floors[editor.floors.Count - 1];
            return new EndpointVisualState
            {
                FirstActive = first != null && first.gameObject.activeSelf,
                LastActive = last != null && last.gameObject.activeSelf,
                FirstScale = first == null
                    ? Vector3.one
                    : first.transform.localScale,
                LastScale = last == null
                    ? Vector3.one
                    : last.transform.localScale,
                FirstHoldActive = first == null
                    || first.holdGO == null
                    || first.holdGO.activeSelf,
                LastHoldActive = last == null
                    || last.holdGO == null
                    || last.holdGO.activeSelf
            };
        }

        private static void RestoreEndpointVisualState(scnEditor editor)
        {
            if (editor == null
                || editor.floors == null
                || editor.floors.Count == 0
                || originalEndpointVisuals == null)
            {
                return;
            }
            scrFloor first = editor.floors[0];
            scrFloor last = editor.floors[editor.floors.Count - 1];
            RestoreFloorObject(
                first,
                originalEndpointVisuals.FirstActive,
                originalEndpointVisuals.FirstScale,
                originalEndpointVisuals.FirstHoldActive);
            RestoreFloorObject(
                last,
                originalEndpointVisuals.LastActive,
                originalEndpointVisuals.LastScale,
                originalEndpointVisuals.LastHoldActive);
            if (last != null)
            {
                last.UpdateIconSprite();
                if (last.iconsprite != null)
                {
                    last.iconsprite.enabled = true;
                }
                foreach (scrPortalParticles particle in
                    last.GetComponentsInChildren<scrPortalParticles>(
                        true))
                {
                    particle.gameObject.SetActive(true);
                }
            }
        }

        private static void RestoreFloorObject(
            scrFloor floor,
            bool active,
            Vector3 localScale,
            bool holdActive)
        {
            if (floor == null)
            {
                return;
            }
            floor.gameObject.SetActive(active);
            floor.transform.localScale = localScale;
            if (floor.holdGO != null)
            {
                floor.holdGO.SetActive(holdActive);
            }
        }

        private static void HideEndpointFloorVisuals(scnEditor editor)
        {
            if (editor == null
                || editor.floors == null
                || editor.floors.Count == 0)
            {
                return;
            }
            HideFloorVisuals(editor.floors[0]);
            if (editor.floors.Count > 1)
            {
                HideEndpointPortalVisuals(
                    editor.floors[editor.floors.Count - 1]);
            }
        }

        private static void CenterPreviewCamera(scnEditor editor)
        {
            if (editor == null
                || editor.camera == null
                || editor.floors == null
                || editor.floors.Count <= 1)
            {
                return;
            }
            FloorMesh.UpdateAllRequired();
            bool hasBounds = false;
            Bounds bounds = default(Bounds);
            for (int index = 1;
                index < editor.floors.Count;
                index++)
            {
                scrFloor floor = editor.floors[index];
                if (floor == null
                    || floor.floorRenderer == null
                    || floor.floorRenderer.renderer == null
                    || !floor.floorRenderer.renderer.enabled)
                {
                    continue;
                }
                if (!hasBounds)
                {
                    bounds = floor.floorRenderer.renderer.bounds;
                    hasBounds = true;
                }
                else
                {
                    bounds.Encapsulate(
                        floor.floorRenderer.renderer.bounds);
                }
            }
            if (!hasBounds)
            {
                return;
            }
            Vector3 position = editor.camera.transform.position;
            position.x = bounds.center.x;
            position.y = bounds.center.y;
            editor.camera.transform.position = position;
        }

        private static void HideEndpointPortalVisuals(
            scrFloor floor)
        {
            if (floor == null)
            {
                return;
            }
            floor.gameObject.SetActive(true);
            Vector3 scale = floor.transform.localScale;
            if (scale.sqrMagnitude < 0.0001f)
            {
                floor.transform.localScale = Vector3.one;
            }
            if (floor.iconsprite != null)
            {
                floor.iconsprite.enabled = false;
            }
            floor.SetIconSprite(null);
            floor.SetIconOutlineSprite(null);
            foreach (scrPortalParticles particle in
                floor.GetComponentsInChildren<scrPortalParticles>(true))
            {
                particle.gameObject.SetActive(false);
            }
        }

        private static void HideFloorVisuals(scrFloor floor)
        {
            if (floor == null)
            {
                return;
            }
            if (floor.holdGO != null)
            {
                floor.holdGO.SetActive(false);
            }
            floor.gameObject.SetActive(true);
            floor.transform.localScale = Vector3.zero;
        }

        private static void RestoreUi()
        {
            foreach (GraphicState state in hiddenGraphics)
            {
                if (state.Graphic != null)
                {
                    state.Graphic.enabled = state.Enabled;
                }
            }
            hiddenGraphics.Clear();
            foreach (UiState state in hiddenUi)
            {
                if (state.Object != null)
                {
                    state.Object.SetActive(state.Active);
                }
            }
            hiddenUi.Clear();
        }

        private static void CaptureGraphics(scnEditor editor)
        {
            hiddenGraphics.Clear();
            if (editor.levelEditorCanvas == null)
            {
                return;
            }
            Canvas[] keptCanvases =
            {
                editor.floorButtonCanvas,
                editor.floorButtonPrimaryCanvas,
                editor.floorButtonExtraCanvas,
                editor.floorButtonExtraBackQuoteCanvas,
                editor.floorButtonLeftRightCanvas
            };
            foreach (Graphic graphic in
                editor.levelEditorCanvas.GetComponentsInChildren<Graphic>(true))
            {
                if (graphic == null
                    || IsInsideAnyCanvas(graphic.transform, keptCanvases)
                    || IsInsidePreviewEventUi(editor, graphic.transform)
                    || graphic.GetComponentInParent<LevelEventButton>(
                        true) != null
                    || graphic.GetComponentInParent<CategoryTab>(
                        true) != null
                    || editor.buttonToggleAngleInput != null
                    && (graphic.transform
                            == editor.buttonToggleAngleInput.transform
                        || graphic.transform.IsChildOf(
                            editor.buttonToggleAngleInput.transform))
                    || editor.notificationText != null
                    && (graphic.transform == editor.notificationText.transform
                        || graphic.transform.IsChildOf(
                            editor.notificationText.transform))
                    || EditorViewRotationControl.RootTransform != null
                    && graphic.transform.IsChildOf(
                        EditorViewRotationControl.RootTransform)
                    || editor.popupPanel != null
                    && (graphic.transform == editor.popupPanel.transform
                        || graphic.transform.IsChildOf(
                            editor.popupPanel.transform))
                    || editor.popupWindow != null
                    && (graphic.transform == editor.popupWindow.transform
                        || graphic.transform.IsChildOf(
                            editor.popupWindow.transform)))
                {
                    continue;
                }
                hiddenGraphics.Add(new GraphicState
                {
                    Graphic = graphic,
                    Enabled = graphic.enabled
                });
                graphic.enabled = false;
            }
        }

        private static bool IsInsidePreviewEventUi(
            scnEditor editor,
            Transform transform)
        {
            if (editor == null || transform == null)
            {
                return false;
            }
            Transform eventsBar = editor.levelEventsBar == null
                ? null
                : editor.levelEventsBar.transform;
            Transform eventsPanel =
                editor.levelEventsPanel == null
                || editor.levelEventsPanel.rect == null
                    ? null
                    : editor.levelEventsPanel.rect.transform;
            Transform eventTabs =
                editor.levelEventsPanel == null
                || editor.levelEventsPanel.tabs == null
                    ? null
                    : editor.levelEventsPanel.tabs.transform;
            return eventsBar != null
                && (transform == eventsBar
                    || transform.IsChildOf(eventsBar))
                || eventsPanel != null
                && (transform == eventsPanel
                    || transform.IsChildOf(eventsPanel))
                || eventTabs != null
                && (transform == eventTabs
                    || transform.IsChildOf(eventTabs));
        }

        private static bool IsInsideAnyCanvas(
            Transform transform,
            IEnumerable<Canvas> canvases)
        {
            foreach (Canvas canvas in canvases)
            {
                if (canvas != null
                    && (transform == canvas.transform
                        || transform.IsChildOf(canvas.transform)))
                {
                    return true;
                }
            }
            return false;
        }

        private static void RestoreSelection(scnEditor editor)
        {
            if (originalSelectedFloors == null
                || originalSelectedFloors.Count == 0
                || editor.floors == null)
            {
                return;
            }

            List<int> valid = originalSelectedFloors
                .Where(index => index >= 0 && index < editor.floors.Count)
                .ToList();
            if (valid.Count == 1)
            {
                editor.SelectFloor(editor.floors[valid[0]]);
            }
            else if (valid.Count > 1)
            {
                editor.MultiSelectFloors(
                    editor.floors[valid[0]],
                    editor.floors[valid[valid.Count - 1]]);
            }
            if (!string.IsNullOrEmpty(NoteSaveFeature.SelectedPatternPath))
            {
                editor.settingsPanel.ShowPanel(originalSettingsPanelType);
            }
        }

        private static void CreateExitButton(scnEditor editor)
        {
            if (exitButton != null || editor.levelEditorCanvas == null)
            {
                return;
            }
            exitButton = UnityEngine.Object.Instantiate(
                ADOBase.gc.prefab_controlExport,
                editor.levelEditorCanvas.transform,
                false);
            exitButton.name = "AnglePreviewExit";
            RectTransform rect = exitButton.GetComponent<RectTransform>();
            rect.anchorMin = new Vector2(1f, 1f);
            rect.anchorMax = new Vector2(1f, 1f);
            rect.pivot = new Vector2(1f, 1f);
            rect.anchoredPosition = new Vector2(-24f, -24f);
            rect.sizeDelta = new Vector2(150f, 46f);

            PropertyControl_Export control =
                exitButton.GetComponent<PropertyControl_Export>();
            control.buttonText.text =
                AngleMod.L("退出预览", "Exit Preview");
            control.exportButton.onClick.RemoveAllListeners();
            control.exportButton.onClick.AddListener(RequestClose);
            exitButton.transform.SetAsLastSibling();
        }

        private static void DestroyExitButton()
        {
            if (exitButton != null)
            {
                UnityEngine.Object.Destroy(exitButton);
                exitButton = null;
            }
        }
    }

    [HarmonyLib.HarmonyPatch(typeof(scnEditor), "SaveLevel")]
    internal static class NoteSavePreviewSavePatch
    {
        private static bool Prefix()
        {
            if (!NoteSavePreview.IsActive)
            {
                return true;
            }
            NoteSavePreview.SaveCurrentPattern();
            return false;
        }
    }

    [HarmonyLib.HarmonyPatch(typeof(scnEditor), "SaveLevelAs")]
    internal static class NoteSavePreviewSaveAsPatch
    {
        private static bool Prefix()
        {
            if (!NoteSavePreview.IsActive)
            {
                return true;
            }
            NoteSavePreview.SaveCurrentPattern();
            return false;
        }
    }

    [HarmonyLib.HarmonyPatch(typeof(OpenLevelEditorAction), "Execute")]
    internal static class NoteSavePreviewOpenPatch
    {
        private static bool Prefix()
        {
            return !NoteSavePreview.IsActive;
        }
    }

    [HarmonyLib.HarmonyPatch(typeof(NewLevelEditorAction), "Execute")]
    internal static class NoteSavePreviewNewPatch
    {
        private static bool Prefix()
        {
            return !NoteSavePreview.IsActive;
        }
    }

    [HarmonyLib.HarmonyPatch(typeof(OpenRecentEditorAction), "Execute")]
    internal static class NoteSavePreviewRecentPatch
    {
        private static bool Prefix()
        {
            return !NoteSavePreview.IsActive;
        }
    }

    [HarmonyLib.HarmonyPatch(typeof(OpenUrlEditorAction), "Execute")]
    internal static class NoteSavePreviewUrlPatch
    {
        private static bool Prefix()
        {
            return !NoteSavePreview.IsActive;
        }
    }

    [HarmonyLib.HarmonyPatch(typeof(scnEditor), "Play")]
    internal static class NoteSavePreviewPlayPatch
    {
        private static bool Prefix(scnEditor __instance)
        {
            return NoteSavePreview.PreparePlaybackStart(__instance);
        }

        private static void Postfix(scnEditor __instance)
        {
            NoteSavePreview.PreparePlayback(__instance);
        }
    }

    [HarmonyLib.HarmonyPatch(typeof(scnEditor), "ShowEventPicker")]
    internal static class NoteSavePreviewEventPickerPatch
    {
        private static void Prefix(ref bool show)
        {
            NoteSavePreview.ForcePreviewEventPickerOpen(ref show);
        }
    }

    [HarmonyLib.HarmonyPatch(typeof(scnEditor), "HandleKeyboardActions")]
    internal static class NoteSavePreviewEscapePatch
    {
        private static bool Prefix(scnEditor __instance)
        {
            return !NoteSavePreview.HandleEscape(__instance);
        }
    }

    [HarmonyLib.HarmonyPatch(typeof(scnEditor), "SaveAndQuit")]
    internal static class NoteSavePreviewNativeSavePatch
    {
        private static bool Prefix()
        {
            return !NoteSavePreview.HandleNativeUnsavedSave();
        }
    }

    [HarmonyLib.HarmonyPatch(typeof(scnEditor), "DoQuitAction")]
    internal static class NoteSavePreviewNativeDiscardPatch
    {
        private static bool Prefix()
        {
            return !NoteSavePreview.HandleNativeUnsavedDiscard();
        }
    }
}
