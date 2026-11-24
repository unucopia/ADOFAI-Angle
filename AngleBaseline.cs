using System;
using System.Reflection;
using ADOFAI;
using ADOFAI.Editor;
using ADOFAI.Editor.Actions;
using DG.Tweening;
using HarmonyLib;
using TMPro;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;
using UnityModManagerNet;

namespace Angle;

public static class AngleMod
{
	private enum HotkeyCaptureTarget
	{
		None,
		RelativePlacement,
		CameraFollow
	}

	private const float ArrowHoldDelay = 0.45f;

	private const float ArrowRepeatInterval = 0.025f;

	internal static UnityModManager.ModEntry modEntry;

	internal static AngleSettings settings;

	internal static bool RhythmPreservationEnabled => settings?.preserveRhythm != false;

	internal static bool keyboardPresetScope;

	internal static bool capturingHotkey;

	internal static bool showAllAngles;

	internal static string captureMessage = "";

	private static Harmony harmony;

	private static GameObject hostObject;

	private static readonly AccessTools.FieldRef<scnEditor, AudioSource> OttoAudioSrc = AccessTools.FieldRefAccess<scnEditor, AudioSource>("ottoAudioSrc");

	private static readonly FieldInfo InterfaceAudioSrc = AccessTools.Field(typeof(scnEditor), "interfaceAudioSrc");

	private static readonly System.Reflection.PropertyInfo HighBpmProperty = AccessTools.Property(typeof(scnEditor), "highBPM");

	private static readonly MethodInfo UpdateFloorDirectionButtonsMethod = AccessTools.Method(typeof(scnEditor), "UpdateFloorDirectionButtons", (Type[])null, (Type[])null);

	private static bool angleLabelsVisible;

	private static float nextAngleLabelRefresh;

	private static HotkeyCaptureTarget hotkeyCaptureTarget;

	private static Tween cameraRotationTween;

	private static float activeCameraRotationDirection;

	private static bool cameraDefaultCaptured;

	private static float cameraDefaultAngle;

	private static bool cameraNeedsFreshSelection;

	private static bool hasCameraTarget;

	private static float lastCameraTargetAngle;

	private static bool wasInPlayMode;

	private static bool buttonCanvasDefaultCaptured;

	private static Quaternion buttonCanvasDefaultRotation;

	private static KeyCode heldSelectionArrow = KeyCode.None;

	private static float nextSelectionRepeatTime;

	public static bool Load(UnityModManager.ModEntry loadedEntry)
	{
		//IL_00cd: Unknown result type (might be due to invalid IL or missing references)
		//IL_00d7: Expected O, but got Unknown
		modEntry = loadedEntry;
		settings = UnityModManager.ModSettings.Load<AngleSettings>(modEntry) ?? new AngleSettings();
		settings.Validate();
			NoteSaveFeature.Initialize(modEntry);
		if (!settings.cameraFollowInitialized)
		{
			settings.cameraFollowEnabled = settings.relativeModeEnabled;
			settings.cameraFollowInitialized = true;
			((UnityModManager.ModSettings)settings).Save(modEntry);
		}
		modEntry.OnUnload = OnUnload;
		modEntry.OnGUI = DrawSettingsGUI;
		modEntry.OnSaveGUI = SaveSettingsGUI;
		hostObject = new GameObject("AngleOverlay");
		UnityEngine.Object.DontDestroyOnLoad(hostObject);
		hostObject.AddComponent<AngleOverlay>();
		harmony = new Harmony("Angle");
		harmony.PatchAll(Assembly.GetExecutingAssembly());
		modEntry.Logger.Log("Angle loaded.");
		return true;
	}

	private static bool OnUnload(UnityModManager.ModEntry entry)
	{
		SaveSettingsGUI(entry);
		scnEditor instance = scnEditor.instance;
		showAllAngles = false;
		RestoreFloorNumberText(instance);
		HideAngleLabels(instance);
		RestoreEditorCameraImmediate(instance);
		RestoreDirectionButtonRotation(instance);
		TrackLayerFeature.Destroy();
		if (harmony != null)
		{
			harmony.UnpatchAll("Angle");
			harmony = null;
		}
		if (hostObject != null)
		{
			UnityEngine.Object.Destroy(hostObject);
			hostObject = null;
		}
		keyboardPresetScope = false;
		capturingHotkey = false;
		hotkeyCaptureTarget = HotkeyCaptureTarget.None;
		ResetHeldSelectionArrow();
		EventClipboardFeature.Reset();
			NoteSaveFeature.Shutdown();
		return true;
	}

	internal static void UpdateOverlay()
	{
		if (settings == null)
		{
			return;
		}
		if (capturingHotkey)
		{
			UpdateHotkeyCapture();
			return;
		}
		scnEditor instance = scnEditor.instance;
		if ((UnityEngine.Object)(object)instance == null || IsTextInputFocused())
		{
			return;
		}
		SyncDirectionButtonRotation(instance);
		SyncEditorLabelRotation(instance);
		UpdateHoldArrowSelection(instance);
		if (HoldingControl() && !HoldingAlt() && !HoldingShift() && Input.GetKeyDown(KeyCode.A))
		{
			ToggleAllAngles(instance);
		}
		else if (MatchesRelativeHotkey())
		{
			settings.relativeModeEnabled = !settings.relativeModeEnabled;
			if (settings.relativeModeEnabled)
			{
				ResumeCameraFollowAfterRelativeOn();
			}
			else
			{
				SuspendCameraFollowForRelativeOff();
			}
			((UnityModManager.ModSettings)settings).Save(modEntry);
			ShowFeatureNotification(instance, settings.relativeModeEnabled ? L("相对角度放置已开启", "Relative placement on") : L("相对角度放置已关闭", "Relative placement off"), settings.relativeModeEnabled);
			RefreshFloorDirectionButtons(instance);
			MaintainAngleLabels(instance);
		}
		else if (MatchesCameraHotkey())
		{
			SetCameraFollowEnabled(!settings.cameraFollowEnabled, showNotice: true);
		}
		else
		{
			UpdateCameraFollow(instance);
			MaintainAngleLabels(instance);
		}
	}

	internal static bool TryApplyRelativeAngle(scnEditor editor, ref float requestedAngle, ref char requestedCharacter, bool fullSpin)
	{
		if (!keyboardPresetScope || settings == null || !settings.relativeModeEnabled || (UnityEngine.Object)(object)editor == null || editor.levelData == null || editor.levelData.isOldLevel || fullSpin)
		{
			return false;
		}
		if (!FloorHelper.TryGetPathInDegrees(requestedCharacter, out var angle) || Mathf.Approximately(angle, 999f))
		{
			return false;
		}
		if (!TryGetRelativeBase(editor, out var referenceAngle, out var rotationSign))
		{
			return false;
		}
		requestedAngle = referenceAngle + rotationSign * NormalizeSignedAngle(angle);
		requestedCharacter = '?';
		return true;
	}

	internal static void OnEditorFloorSelected(scnEditor editor, scrFloor floor)
	{
		if (!((UnityEngine.Object)(object)editor == null) && !((UnityEngine.Object)(object)floor == null) && !editor.playMode && settings != null && settings.relativeModeEnabled && settings.cameraFollowEnabled)
		{
			EnsureCameraDefault(editor);
			cameraNeedsFreshSelection = false;
			ApplyCameraFollow(editor, floor);
		}
	}

	private static void UpdateCameraFollow(scnEditor editor)
	{
		if ((UnityEngine.Object)(object)editor == null || editor.camera == null)
		{
			return;
		}
		EnsureCameraDefault(editor);
		if (editor.playMode)
		{
			wasInPlayMode = true;
			return;
		}
		bool flag = wasInPlayMode;
		wasInPlayMode = false;
		if (settings.relativeModeEnabled && settings.cameraFollowEnabled && !cameraNeedsFreshSelection && editor.selectedFloors != null && editor.selectedFloors.Count == 1 && !((UnityEngine.Object)(object)editor.selectedFloors[0] == null))
		{
			scrFloor floor = editor.selectedFloors[0];
			if (TryGetCameraTargetAngle(editor, floor, out var targetAngle) && (flag || !hasCameraTarget || Mathf.Abs(Mathf.DeltaAngle(lastCameraTargetAngle, targetAngle)) > 0.01f))
			{
				ApplyCameraFollow(editor, floor);
			}
		}
	}

	private static void ApplyCameraFollow(scnEditor editor, scrFloor floor)
	{
		if (TryGetCameraTargetAngle(editor, floor, out var targetAngle))
		{
			hasCameraTarget = true;
			lastCameraTargetAngle = targetAngle;
			RotateEditorCamera(editor, targetAngle);
		}
	}

	private static bool TryGetCameraTargetAngle(scnEditor editor, scrFloor floor, out float targetAngle)
	{
		targetAngle = 0f;
		if ((UnityEngine.Object)(object)editor == null || (UnityEngine.Object)(object)floor == null)
		{
			return false;
		}
		float num = (float)floor.entryangle * 57.29578f;
		float num2 = Mathf.Abs(450f - num) % 360f;
		targetAngle = NormalizeSignedAngle(num2 - 180f);
		return true;
	}

	private static void SetCameraFollowEnabled(bool enabled, bool showNotice)
	{
		if ((enabled && !settings.relativeModeEnabled) || settings.cameraFollowEnabled == enabled)
		{
			return;
		}
		settings.cameraFollowEnabled = enabled;
		((UnityModManager.ModSettings)settings).Save(modEntry);
		hasCameraTarget = false;
		cameraNeedsFreshSelection = false;
		RefreshFloorDirectionButtons(scnEditor.instance);
		scnEditor instance = scnEditor.instance;
		if (enabled)
		{
			if ((UnityEngine.Object)(object)instance != null && !instance.playMode && instance.selectedFloors != null && instance.selectedFloors.Count == 1 && (UnityEngine.Object)(object)instance.selectedFloors[0] != null)
			{
				ApplyCameraFollow(instance, instance.selectedFloors[0]);
			}
		}
		else if ((UnityEngine.Object)(object)instance != null && instance.camera != null)
		{
			EnsureCameraDefault(instance);
			RotateEditorCamera(instance, cameraDefaultAngle);
		}
		if (showNotice)
		{
			ShowFeatureNotification(scnEditor.instance, enabled ? L("视角跟随已开启", "Camera rotation on") : L("视角跟随已关闭", "Camera rotation off"), enabled);
		}
	}

	private static void SuspendCameraFollowForRelativeOff()
	{
		hasCameraTarget = false;
		cameraNeedsFreshSelection = true;
		scnEditor instance = scnEditor.instance;
		if ((UnityEngine.Object)(object)instance != null && instance.camera != null)
		{
			EnsureCameraDefault(instance);
			RotateEditorCamera(instance, cameraDefaultAngle);
		}
	}

	private static void ResumeCameraFollowAfterRelativeOn()
	{
		hasCameraTarget = false;
		cameraNeedsFreshSelection = false;
		if (settings != null && settings.cameraFollowEnabled)
		{
			scnEditor instance = scnEditor.instance;
			if (!((UnityEngine.Object)(object)instance == null) && !instance.playMode && instance.selectedFloors != null && instance.selectedFloors.Count == 1 && !((UnityEngine.Object)(object)instance.selectedFloors[0] == null))
			{
				ApplyCameraFollow(instance, instance.selectedFloors[0]);
			}
		}
	}

	private static void ShowFeatureNotification(scnEditor editor, string text, bool enabled)
	{
		if (!((UnityEngine.Object)(object)editor == null))
		{
			editor.ShowNotification(text);
			AudioSource audioSource = OttoAudioSrc.Invoke(editor);
			AudioClip[] array = ((ADOBase.gc == null) ? null : ADOBase.gc.soundEffects);
			bool flag = HighBpmProperty != null && (bool)HighBpmProperty.GetValue(editor, null);
			int num = ((!enabled) ? 7 : (flag ? 6 : 5));
			if (audioSource != null && array != null && num < array.Length && array[num] != null)
			{
				audioSource.clip = array[num];
				audioSource.Play();
			}
		}
	}

	internal static void NotifyCannotPreserveTiming(scnEditor editor)
	{
		editor.ShowNotification(L("当前角度无法保拍", "Cannot preserve timing at this angle"));
		AudioClip[] effects = ADOBase.gc == null ? null : ADOBase.gc.soundEffects;
		const int NoFailDisabledSound = 11;
		if (effects != null && effects.Length > NoFailDisabledSound && effects[NoFailDisabledSound] != null)
		{
			(InterfaceAudioSrc?.GetValue(editor) as AudioSource)?.PlayOneShot(effects[NoFailDisabledSound]);
		}
	}

	private static bool EnsureCameraDefault(scnEditor editor)
	{
		if (cameraDefaultCaptured)
		{
			return true;
		}
		if ((UnityEngine.Object)(object)editor == null || editor.camera == null)
		{
			return false;
		}
		cameraDefaultAngle = NormalizeSignedAngle(editor.camera.transform.localEulerAngles.z);
		cameraDefaultCaptured = true;
		return true;
	}

	private static void RotateEditorCamera(scnEditor editor, float targetAngle)
	{
		if ((UnityEngine.Object)(object)editor == null || editor.camera == null)
		{
			return;
		}
		Transform cameraTransform = editor.camera.transform;
		float num = NormalizeSignedAngle(cameraTransform.localEulerAngles.z);
		float num2 = NormalizeSignedAngle(targetAngle);
		float num3 = Mathf.DeltaAngle(num, num2);
		float num4 = ((Mathf.Abs(num3) > 0.01f) ? Mathf.Sign(num3) : 0f);
		bool flag = cameraRotationTween != null && cameraRotationTween.IsActive() && Mathf.Abs(activeCameraRotationDirection) > 0.5f && Mathf.Abs(num4) > 0.5f && Mathf.Approximately(activeCameraRotationDirection, num4);
		cameraRotationTween?.Kill();
		cameraRotationTween = null;
		activeCameraRotationDirection = num4;
		if (Mathf.Abs(num3) <= 0.01f)
		{
			Vector3 localEulerAngles = cameraTransform.localEulerAngles;
			localEulerAngles.z = num2;
			cameraTransform.localEulerAngles = localEulerAngles;
			activeCameraRotationDirection = 0f;
			return;
		}
		float num5 = Mathf.Clamp(0.15f + Mathf.Abs(num3) / 180f * 0.35f, 0.15f, 0.6f);
		if (flag)
		{
			num5 = Mathf.Clamp(num5 * 0.75f, 0.08f, 0.45f);
		}
		Vector3 localEulerAngles2 = cameraTransform.localEulerAngles;
		localEulerAngles2.z = num + num3;
		Vector3 finalEuler = cameraTransform.localEulerAngles;
		finalEuler.z = num2;
		cameraRotationTween = cameraTransform.DOLocalRotate(localEulerAngles2, num5).SetEase(Ease.OutQuad).SetUpdate(isIndependentUpdate: true)
			.OnComplete(delegate
			{
				if (cameraTransform != null)
				{
					cameraTransform.localEulerAngles = finalEuler;
				}
				activeCameraRotationDirection = 0f;
				cameraRotationTween = null;
			});
	}

	private static void RestoreEditorCameraImmediate(scnEditor editor)
	{
		cameraRotationTween?.Kill();
		cameraRotationTween = null;
		if ((UnityEngine.Object)(object)editor != null && editor.camera != null && cameraDefaultCaptured)
		{
			Vector3 localEulerAngles = editor.camera.transform.localEulerAngles;
			localEulerAngles.z = cameraDefaultAngle;
			editor.camera.transform.localEulerAngles = localEulerAngles;
		}
		cameraNeedsFreshSelection = false;
		hasCameraTarget = false;
		activeCameraRotationDirection = 0f;
	}

	private static void SyncDirectionButtonRotation(scnEditor editor)
	{
		if ((UnityEngine.Object)(object)editor == null || editor.playMode || editor.floorButtonCanvas == null || editor.camera == null)
		{
			return;
		}
		Transform transform = editor.floorButtonCanvas.transform;
		if (!buttonCanvasDefaultCaptured)
		{
			buttonCanvasDefaultRotation = transform.rotation;
			buttonCanvasDefaultCaptured = true;
		}
		float z = 0f;
		if (settings.cameraFollowEnabled)
		{
			z = editor.camera.transform.localEulerAngles.z;
		}
		transform.rotation = Quaternion.Euler(0f, 0f, z);
		if (editor.floorDirectionButtons != null)
		{
			FloorDirectionButton[] floorDirectionButtons = editor.floorDirectionButtons;
			for (int i = 0; i < floorDirectionButtons.Length; i++)
			{
				ResetDirectionButtonLabelRotation(floorDirectionButtons[i]);
			}
		}
	}

	private static void SyncEditorLabelRotation(scnEditor editor)
	{
		if ((UnityEngine.Object)(object)editor == null || editor.playMode || editor.camera == null || editor.floors == null || (!editor.showFloorNums && !showAllAngles))
		{
			return;
		}
		float z = editor.camera.transform.localEulerAngles.z;
		foreach (scrFloor floor in editor.floors)
		{
			if (!((UnityEngine.Object)(object)floor == null) && ((Behaviour)(object)floor).enabled && !floor.isFake && !(floor.editorNumText == null) && !(floor.editorNumText.letterText == null))
			{
				Vector3 eulerAngles = floor.editorNumText.letterText.transform.eulerAngles;
				eulerAngles.z = z;
				floor.editorNumText.letterText.transform.eulerAngles = eulerAngles;
			}
		}
	}

	private static void UpdateHoldArrowSelection(scnEditor editor)
	{
		if ((UnityEngine.Object)(object)editor == null || editor.playMode || IsTextInputFocused() || HoldingControl() || HoldingShift() || HoldingAlt() || Input.GetKey(KeyCode.BackQuote))
		{
			ResetHeldSelectionArrow();
			return;
		}
		KeyCode keyCode = (Input.GetKey(KeyCode.RightArrow) ? KeyCode.RightArrow : (Input.GetKey(KeyCode.LeftArrow) ? KeyCode.LeftArrow : KeyCode.None));
		if (keyCode == KeyCode.None)
		{
			ResetHeldSelectionArrow();
			return;
		}
		float unscaledTime = Time.unscaledTime;
		if (heldSelectionArrow != keyCode)
		{
			heldSelectionArrow = keyCode;
			nextSelectionRepeatTime = unscaledTime + 0.45f;
		}
		else if (!(unscaledTime < nextSelectionRepeatTime) && editor.selectedFloors != null && editor.selectedFloors.Count != 0)
		{
			scrFloor floor = ((keyCode == KeyCode.RightArrow) ? editor.selectedFloors[editor.selectedFloors.Count - 1] : editor.selectedFloors[0]);
			scrFloor scrFloor = ((keyCode == KeyCode.RightArrow) ? editor.NextFloor(floor) : editor.PreviousFloor(floor));
			if ((UnityEngine.Object)(object)scrFloor != null)
			{
				editor.SelectFloor(scrFloor);
			}
			nextSelectionRepeatTime = unscaledTime + 0.025f;
		}
	}

	private static void ResetHeldSelectionArrow()
	{
		heldSelectionArrow = KeyCode.None;
		nextSelectionRepeatTime = 0f;
	}

	internal static void ResetDirectionButtonLabelRotation(FloorDirectionButton directionButton)
	{
		if (!((UnityEngine.Object)(object)directionButton == null))
		{
			ResetButtonLabelLocalRotation(directionButton.text);
			ResetButtonLabelLocalRotation(directionButton.textShifted);
			if (directionButton.deleteIcon != null)
			{
				ResetButtonLabelLocalRotation(directionButton.deleteIcon.transform);
			}
			ApplyDirectionButtonText(directionButton);
		}
	}

	private static void ApplyDirectionButtonText(FloorDirectionButton directionButton)
	{
		if ((UnityEngine.Object)(object)directionButton == null)
		{
			return;
		}
		string label = directionButton.btnType.ToString().Replace("BackQuote", "");
		float directionButtonMappingRotation = GetDirectionButtonMappingRotation();
		if (!Mathf.Approximately(directionButtonMappingRotation, 0f) && TryGetDirectionButtonAngle(directionButton.btnType, out var angle))
		{
			int num = Mathf.RoundToInt(NormalizeDisplayAngle(angle - directionButtonMappingRotation) / 15f) % 24;
			if (num < 0)
			{
				num += 24;
			}
			TryGetDirectionButtonLabel((float)num * 15f, out label);
		}
		if (directionButton.text != null)
		{
			directionButton.text.text = label;
		}
		if (directionButton.textShifted != null)
		{
			directionButton.textShifted.text = label;
		}
	}

	private static float GetDirectionButtonMappingRotation()
	{
		if (settings == null || settings.cameraFollowEnabled || !settings.relativeModeEnabled)
		{
			return 0f;
		}
		scnEditor instance = scnEditor.instance;
		if ((UnityEngine.Object)(object)instance == null || instance.selectedFloors == null || instance.selectedFloors.Count != 1 || (UnityEngine.Object)(object)instance.selectedFloors[0] == null)
		{
			return 0f;
		}
		if (!TryGetCameraTargetAngle(instance, instance.selectedFloors[0], out var targetAngle))
		{
			return 0f;
		}
		return targetAngle;
	}

	private static bool TryGetDirectionButtonLabel(float angle, out string label)
	{
		switch (Mathf.RoundToInt(NormalizeDisplayAngle(angle) / 15f) % 24)
		{
		case 0:
			label = "D";
			return true;
		case 1:
			label = "J";
			return true;
		case 2:
			label = "J";
			return true;
		case 3:
			label = "E";
			return true;
		case 4:
			label = "Y";
			return true;
		case 5:
			label = "Y";
			return true;
		case 6:
			label = "W";
			return true;
		case 7:
			label = "T";
			return true;
		case 8:
			label = "T";
			return true;
		case 9:
			label = "Q";
			return true;
		case 10:
			label = "H";
			return true;
		case 11:
			label = "H";
			return true;
		case 12:
			label = "A";
			return true;
		case 13:
			label = "N";
			return true;
		case 14:
			label = "N";
			return true;
		case 15:
			label = "Z";
			return true;
		case 16:
			label = "V";
			return true;
		case 17:
			label = "V";
			return true;
		case 18:
			label = "S";
			return true;
		case 19:
			label = "B";
			return true;
		case 20:
			label = "B";
			return true;
		case 21:
			label = "C";
			return true;
		case 22:
			label = "M";
			return true;
		case 23:
			label = "M";
			return true;
		default:
			label = "";
			return false;
		}
	}

	private static void ResetButtonLabelLocalRotation(TMP_Text text)
	{
		if (!(text == null))
		{
			SetButtonLabelWorldAngle(text.transform);
		}
	}

	private static void ResetButtonLabelLocalRotation(Transform transform)
	{
		if (!(transform == null))
		{
			SetButtonLabelWorldAngle(transform);
		}
	}

	private static void SetButtonLabelWorldAngle(Transform transform)
	{
		scnEditor instance = scnEditor.instance;
		float z = 0f;
		if (settings != null && settings.cameraFollowEnabled && (UnityEngine.Object)(object)instance != null && instance.floorButtonCanvas != null)
		{
			z = instance.floorButtonCanvas.transform.eulerAngles.z;
		}
		Vector3 eulerAngles = transform.eulerAngles;
		eulerAngles.z = z;
		transform.eulerAngles = eulerAngles;
	}

	private static void RestoreDirectionButtonRotation(scnEditor editor)
	{
		if ((UnityEngine.Object)(object)editor != null && editor.floorButtonCanvas != null && buttonCanvasDefaultCaptured)
		{
			editor.floorButtonCanvas.transform.rotation = buttonCanvasDefaultRotation;
		}
		buttonCanvasDefaultCaptured = false;
	}

	internal static void PreparePreviewMode(scnEditor editor)
	{
		cameraRotationTween?.Kill();
		cameraRotationTween = null;
		activeCameraRotationDirection = 0f;
		hasCameraTarget = false;
		cameraNeedsFreshSelection = false;
		cameraDefaultCaptured = true;
		cameraDefaultAngle = 0f;
		wasInPlayMode = false;
		if (editor != null && editor.camera != null)
		{
			Vector3 angles = editor.camera.transform.localEulerAngles;
			angles.z = 0f;
			editor.camera.transform.localEulerAngles = angles;
		}
		EditorViewRotationControl.SyncFromCamera(editor, force: true);
		RestoreDirectionButtonRotation(editor);
		if (editor != null && editor.floorDirectionButtons != null)
		{
			foreach (FloorDirectionButton button in editor.floorDirectionButtons)
			{
				ResetDirectionButtonLabelRotation(button);
			}
		}
	}

	internal static void ResetCameraSession(scnEditor editor)
	{
		cameraRotationTween?.Kill();
		cameraRotationTween = null;
		activeCameraRotationDirection = 0f;
		hasCameraTarget = false;
		cameraNeedsFreshSelection = false;
		cameraDefaultCaptured = true;
		cameraDefaultAngle = 0f;
		wasInPlayMode = false;
		if (editor != null && editor.camera != null)
		{
			Vector3 angles = editor.camera.transform.localEulerAngles;
			angles.z = 0f;
			editor.camera.transform.localEulerAngles = angles;
		}
		EditorViewRotationControl.SyncFromCamera(editor, force: true);
	}

	internal static void RestoreCameraSession(scnEditor editor)
	{
		cameraRotationTween?.Kill();
		cameraRotationTween = null;
		activeCameraRotationDirection = 0f;
		hasCameraTarget = false;
		cameraNeedsFreshSelection = false;
		wasInPlayMode = false;
		if (editor != null && editor.camera != null)
		{
			cameraDefaultCaptured = true;
			cameraDefaultAngle = NormalizeSignedAngle(
				editor.camera.transform.localEulerAngles.z);
		}
		EditorViewRotationControl.SyncFromCamera(editor, force: true);
	}

	internal static void SetManualCameraAngle(scnEditor editor, float angle)
	{
		if (editor == null || editor.camera == null)
		{
			return;
		}
		angle = Mathf.Clamp(angle, -180f, 180f);
		cameraDefaultCaptured = true;
		cameraDefaultAngle = angle;
		hasCameraTarget = false;
		cameraNeedsFreshSelection = false;
		RotateEditorCamera(editor, angle);
	}

	internal static void ApplyRelativeDeleteButton(scnEditor editor)
	{
		if (settings == null || !settings.relativeModeEnabled || !settings.cameraFollowEnabled || (UnityEngine.Object)(object)editor == null || editor.selectedFloors == null || editor.selectedFloors.Count != 1 || (UnityEngine.Object)(object)editor.selectedFloors[0] == null || editor.floorDirectionButtons == null)
		{
			return;
		}
		scrFloor scrFloor = editor.selectedFloors[0];
		if (scrFloor.seqID == 0 || scrFloor.midSpin || (UnityEngine.Object)(object)scrFloor.nextfloor == null || Mathf.Approximately(scrFloor.nextfloor.floatDirection, 999f))
		{
			return;
		}
		float current = 180f;
		FloorDirectionButton[] floorDirectionButtons = editor.floorDirectionButtons;
		foreach (FloorDirectionButton floorDirectionButton in floorDirectionButtons)
		{
			if (!((UnityEngine.Object)(object)floorDirectionButton == null))
			{
				if (!TryGetDirectionButtonAngle(floorDirectionButton.btnType, out var angle))
				{
					floorDirectionButton.delete = false;
				}
				else
				{
					floorDirectionButton.delete = Mathf.Abs(Mathf.DeltaAngle(current, angle)) < 0.5f;
				}
				((Component)(object)floorDirectionButton).gameObject.SetActive(!floorDirectionButton.delete || scrFloor.seqID != 0);
				floorDirectionButton.Init();
			}
		}
	}

	private static bool TryGetDirectionButtonAngle(FloorDirectionButtonType buttonType, out float angle)
	{
		switch (buttonType)
		{
		case FloorDirectionButtonType.D:
			angle = 0f;
			return true;
		case FloorDirectionButtonType.BackQuoteJ:
			angle = 15f;
			return true;
		case FloorDirectionButtonType.J:
			angle = 30f;
			return true;
		case FloorDirectionButtonType.E:
			angle = 45f;
			return true;
		case FloorDirectionButtonType.Y:
			angle = 60f;
			return true;
		case FloorDirectionButtonType.BackQuoteY:
			angle = 75f;
			return true;
		case FloorDirectionButtonType.W:
			angle = 90f;
			return true;
		case FloorDirectionButtonType.BackQuoteT:
			angle = 105f;
			return true;
		case FloorDirectionButtonType.T:
			angle = 120f;
			return true;
		case FloorDirectionButtonType.Q:
			angle = 135f;
			return true;
		case FloorDirectionButtonType.H:
			angle = 150f;
			return true;
		case FloorDirectionButtonType.BackQuoteH:
			angle = 165f;
			return true;
		case FloorDirectionButtonType.A:
			angle = 180f;
			return true;
		case FloorDirectionButtonType.BackQuoteN:
			angle = 195f;
			return true;
		case FloorDirectionButtonType.N:
			angle = 210f;
			return true;
		case FloorDirectionButtonType.Z:
			angle = 225f;
			return true;
		case FloorDirectionButtonType.V:
			angle = 240f;
			return true;
		case FloorDirectionButtonType.BackQuoteV:
			angle = 255f;
			return true;
		case FloorDirectionButtonType.S:
			angle = 270f;
			return true;
		case FloorDirectionButtonType.BackQuoteB:
			angle = 285f;
			return true;
		case FloorDirectionButtonType.B:
			angle = 300f;
			return true;
		case FloorDirectionButtonType.C:
			angle = 315f;
			return true;
		case FloorDirectionButtonType.M:
			angle = 330f;
			return true;
		case FloorDirectionButtonType.BackQuoteM:
			angle = 345f;
			return true;
		default:
			angle = 0f;
			return false;
		}
	}

	private static void RefreshFloorDirectionButtons(scnEditor editor)
	{
		if (!((UnityEngine.Object)(object)editor == null) && editor.SelectionIsSingle() && !(UpdateFloorDirectionButtonsMethod == null))
		{
			UpdateFloorDirectionButtonsMethod.Invoke(editor, new object[1] { true });
		}
	}

	internal static void PrepareForFloorNumbers(scnEditor editor)
	{
		showAllAngles = false;
		angleLabelsVisible = false;
		RestoreFloorNumberText(editor);
	}

	internal static void UpdateAngleLabels(scnEditor editor)
	{
		if ((UnityEngine.Object)(object)editor == null || editor.floors == null)
		{
			return;
		}
		bool visible = showAllAngles && !editor.playMode;
		foreach (scrFloor floor in editor.floors)
		{
			ApplyAngleLabel(floor, visible);
		}
		angleLabelsVisible = visible;
	}

	internal static void UpdateAngleLabelForFloor(scrFloor floor)
	{
		scnEditor instance = scnEditor.instance;
		if (showAllAngles && !((UnityEngine.Object)(object)instance == null) && !instance.playMode)
		{
			ApplyAngleLabel(floor, visible: true);
		}
	}

	private static void ApplyAngleLabel(scrFloor floor, bool visible)
	{
		if (!((UnityEngine.Object)(object)floor == null) && !(floor.editorNumText == null))
		{
			bool flag = visible && ((Behaviour)(object)floor).enabled && !floor.isFake;
			floor.editorNumText.gameObject.SetActive(flag);
			if (flag && !(floor.editorNumText.letterText == null))
			{
				floor.editorNumText.letterText.text = (TryGetDisplayAngle(scnEditor.instance, floor, out var displayAngle) ? (displayAngle.ToString("0.##") + "°") : "--");
			}
		}
	}

	private static void ToggleAllAngles(scnEditor editor)
	{
		showAllAngles = !showAllAngles;
		if (showAllAngles)
		{
			editor.showFloorNums = false;
		}
		editor.RemakePath();
		if (showAllAngles)
		{
			UpdateAngleLabels(editor);
		}
		else
		{
			RestoreFloorNumberText(editor);
			HideAngleLabels(editor);
		}
		nextAngleLabelRefresh = Time.unscaledTime + 0.2f;
	}

	private static void MaintainAngleLabels(scnEditor editor)
	{
		if (!(Time.unscaledTime < nextAngleLabelRefresh))
		{
			nextAngleLabelRefresh = Time.unscaledTime + 0.2f;
			if (showAllAngles)
			{
				UpdateAngleLabels(editor);
			}
			else if (angleLabelsVisible)
			{
				RestoreFloorNumberText(editor);
				HideAngleLabels(editor);
			}
		}
	}

	private static void RestoreFloorNumberText(scnEditor editor)
	{
		if ((UnityEngine.Object)(object)editor == null || editor.floors == null)
		{
			return;
		}
		foreach (scrFloor floor in editor.floors)
		{
			if ((UnityEngine.Object)(object)floor != null && floor.editorNumText != null && floor.editorNumText.letterText != null)
			{
				floor.editorNumText.letterText.text = floor.seqID.ToString();
			}
		}
	}

	private static void HideAngleLabels(scnEditor editor)
	{
		if ((UnityEngine.Object)(object)editor == null || editor.floors == null || editor.showFloorNums)
		{
			angleLabelsVisible = false;
			return;
		}
		foreach (scrFloor floor in editor.floors)
		{
			if ((UnityEngine.Object)(object)floor != null && floor.editorNumText != null)
			{
				floor.editorNumText.gameObject.SetActive(value: false);
			}
		}
		angleLabelsVisible = false;
	}

	private static bool TryGetDisplayAngle(scnEditor editor, scrFloor floor, out float displayAngle)
	{
		displayAngle = 0f;
		if (editor == null || editor.floors == null || floor == null || floor.seqID < 0
			|| floor.seqID >= editor.floors.Count || floor.nextfloor == null)
		{
			return false;
		}
		if (floor.midSpin)
		{
			displayAngle = 999f;
			return true;
		}
		double radians = scrMisc.GetAngleMoved(floor.entryangle, floor.exitangle, !floor.isCCW);
		if (double.IsNaN(radians) || double.IsInfinity(radians))
		{
			return false;
		}
		const double fullTurn = 2d * (float)Math.PI;
		const double turnaroundTolerance = 0.000001;
		displayAngle = radians <= turnaroundTolerance || radians >= fullTurn - turnaroundTolerance
			? 360f : (float)(radians * 360 / fullTurn);
		return true;
	}

	private static bool TryGetRelativeBase(scnEditor editor, out float referenceAngle, out float rotationSign)
	{
		referenceAngle = 0f;
		rotationSign = 1f;
		if ((UnityEngine.Object)(object)editor == null || editor.selectedFloors == null || editor.selectedFloors.Count != 1 || (UnityEngine.Object)(object)editor.selectedFloors[0] == null || editor.floors == null || editor.floors.Count == 0)
		{
			return false;
		}
		scrFloor scrFloor = editor.selectedFloors[0];
		rotationSign = (scrFloor.isCCW ? (-1f) : 1f);
		return TryGetIncomingDirection(editor, scrFloor.seqID, out referenceAngle);
	}

	private static bool TryGetIncomingDirection(scnEditor editor, int floorSeqID, out float direction)
	{
		direction = 0f;
		if ((UnityEngine.Object)(object)editor == null || editor.floors == null)
		{
			return false;
		}
		if (floorSeqID <= 0)
		{
			return true;
		}
		for (int num = Mathf.Min(floorSeqID, editor.floors.Count - 1); num >= 1; num--)
		{
			scrFloor scrFloor = editor.floors[num];
			if ((UnityEngine.Object)(object)scrFloor != null && IsUsableReferenceAngle(scrFloor.floatDirection))
			{
				direction = scrFloor.floatDirection;
				return true;
			}
			if ((UnityEngine.Object)(object)scrFloor != null)
			{
				Mathf.Approximately(scrFloor.floatDirection, 999f);
			}
		}
		return true;
	}

	private static bool IsDisplayableAngle(float value)
	{
		if (!float.IsNaN(value) && !float.IsInfinity(value))
		{
			return value > -998f;
		}
		return false;
	}

	private static bool IsUsableReferenceAngle(float value)
	{
		if (IsDisplayableAngle(value))
		{
			return value < 998f;
		}
		return false;
	}

	private static float NormalizeSignedAngle(float value)
	{
		value %= 360f;
		if (value > 180f)
		{
			value -= 360f;
		}
		else if (value <= -180f)
		{
			value += 360f;
		}
		return value;
	}

	private static float NormalizeDisplayAngle(float value)
	{
		value %= 360f;
		return value < 0f ? value + 360f : value;
	}

	private static bool MatchesRelativeHotkey()
	{
		return MatchesHotkey(settings.hotkeyKeyCode, settings.hotkeyControl, settings.hotkeyShift, settings.hotkeyAlt);
	}

	private static bool MatchesCameraHotkey()
	{
		return MatchesHotkey(settings.cameraHotkeyKeyCode, settings.cameraHotkeyControl, settings.cameraHotkeyShift, settings.cameraHotkeyAlt);
	}

	private static bool MatchesHotkey(int keyCodeValue, bool control, bool shift, bool alt)
	{
		if (Input.GetKey(KeyCode.Tab) || keyCodeValue == 0 || !Input.GetKeyDown((KeyCode)keyCodeValue))
		{
			return false;
		}
		if (HoldingControl() == control && HoldingShift() == shift)
		{
			return HoldingAlt() == alt;
		}
		return false;
	}

	private static bool IsSameHotkey(KeyCode keyCode, bool control, bool shift, bool alt, int otherKeyCode, bool otherControl, bool otherShift, bool otherAlt)
	{
		if (keyCode == (KeyCode)otherKeyCode && control == otherControl && shift == otherShift)
		{
			return alt == otherAlt;
		}
		return false;
	}

	private static void UpdateHotkeyCapture()
	{
		if (Input.GetKeyDown(KeyCode.Escape))
		{
			capturingHotkey = false;
			hotkeyCaptureTarget = HotkeyCaptureTarget.None;
			captureMessage = L("已取消", "CANCELLED");
			return;
		}
		bool flag = false;
		foreach (KeyCode value in Enum.GetValues(typeof(KeyCode)))
		{
			if (!Input.GetKeyDown(value))
			{
				continue;
			}
			if (!IsAssignableKey(value))
			{
				flag = true;
				continue;
			}
			bool flag2 = HoldingControl();
			bool flag3 = HoldingShift();
			bool flag4 = HoldingAlt();
			if (IsReservedDisplayShortcut(value, flag2, flag3, flag4))
			{
				captureMessage = L("该快捷键已被保留", "RESERVED SHORTCUT");
				return;
			}
			if (hotkeyCaptureTarget == HotkeyCaptureTarget.RelativePlacement)
			{
				if (IsSameHotkey(value, flag2, flag3, flag4, settings.cameraHotkeyKeyCode, settings.cameraHotkeyControl, settings.cameraHotkeyShift, settings.cameraHotkeyAlt))
				{
					captureMessage = L("该快捷键已被使用", "SHORTCUT ALREADY IN USE");
					return;
				}
				settings.hotkeyKeyCode = (int)value;
				settings.hotkeyControl = flag2;
				settings.hotkeyShift = flag3;
				settings.hotkeyAlt = flag4;
			}
			else
			{
				if (hotkeyCaptureTarget != HotkeyCaptureTarget.CameraFollow)
				{
					return;
				}
				if (IsSameHotkey(value, flag2, flag3, flag4, settings.hotkeyKeyCode, settings.hotkeyControl, settings.hotkeyShift, settings.hotkeyAlt))
				{
					captureMessage = L("该快捷键已被使用", "SHORTCUT ALREADY IN USE");
					return;
				}
				settings.cameraHotkeyKeyCode = (int)value;
				settings.cameraHotkeyControl = flag2;
				settings.cameraHotkeyShift = flag3;
				settings.cameraHotkeyAlt = flag4;
			}
			((UnityModManager.ModSettings)settings).Save(modEntry);
			capturingHotkey = false;
			hotkeyCaptureTarget = HotkeyCaptureTarget.None;
			captureMessage = "";
			return;
		}
		if (flag)
		{
			captureMessage = L("此键不可设置", "UNSUPPORTED KEY");
		}
	}

	internal static bool IsAssignableKey(KeyCode keyCode)
	{
		string text = keyCode.ToString();
		if (keyCode != KeyCode.None && !IsModifierOrLockKey(keyCode) && !IsMouseKey(keyCode))
		{
			return !text.StartsWith("Joystick", StringComparison.Ordinal);
		}
		return false;
	}

	internal static bool IsReservedDisplayShortcut(KeyCode keyCode, bool control, bool shift, bool alt)
	{
		if (keyCode == KeyCode.Tab && !control && !alt)
		{
			return true;
		}
		if (control && !shift && !alt)
		{
			if (keyCode != KeyCode.A)
			{
				return keyCode == KeyCode.F;
			}
			return true;
		}
		return false;
	}

	private static bool IsModifierOrLockKey(KeyCode keyCode)
	{
		if (keyCode != KeyCode.LeftShift && keyCode != KeyCode.RightShift && keyCode != KeyCode.LeftControl && keyCode != KeyCode.RightControl && keyCode != KeyCode.LeftAlt && keyCode != KeyCode.RightAlt && keyCode != KeyCode.LeftMeta && keyCode != KeyCode.RightMeta && keyCode != KeyCode.AltGr && keyCode != KeyCode.CapsLock && keyCode != KeyCode.Numlock)
		{
			return keyCode == KeyCode.ScrollLock;
		}
		return true;
	}

	private static bool IsMouseKey(KeyCode keyCode)
	{
		if (keyCode >= KeyCode.Mouse0)
		{
			return keyCode <= KeyCode.Mouse6;
		}
		return false;
	}

	private static bool HoldingControl()
	{
		if (!Input.GetKey(KeyCode.LeftControl))
		{
			return Input.GetKey(KeyCode.RightControl);
		}
		return true;
	}

	private static bool HoldingShift()
	{
		if (!Input.GetKey(KeyCode.LeftShift))
		{
			return Input.GetKey(KeyCode.RightShift);
		}
		return true;
	}

	private static bool HoldingAlt()
	{
		if (!Input.GetKey(KeyCode.LeftAlt))
		{
			return Input.GetKey(KeyCode.RightAlt);
		}
		return true;
	}

	private static bool IsTextInputFocused()
	{
		EventSystem current = EventSystem.current;
		GameObject gameObject = ((current == null) ? null : current.currentSelectedGameObject);
		if (gameObject == null)
		{
			return false;
		}
		if (!(gameObject.GetComponentInParent<InputField>() != null))
		{
			return gameObject.GetComponentInParent<TMP_InputField>() != null;
		}
		return true;
	}

	internal static string GetRelativeHotkeyDisplay()
	{
		return GetHotkeyDisplay(settings.hotkeyKeyCode, settings.hotkeyControl, settings.hotkeyShift, settings.hotkeyAlt);
	}

	internal static string GetCameraHotkeyDisplay()
	{
		return GetHotkeyDisplay(settings.cameraHotkeyKeyCode, settings.cameraHotkeyControl, settings.cameraHotkeyShift, settings.cameraHotkeyAlt);
	}

	private static string GetHotkeyDisplay(int keyCodeValue, bool control, bool shift, bool alt)
	{
		if (settings == null)
		{
			return "unset";
		}
		string text = "";
		if (control)
		{
			text += "Ctrl+";
		}
		if (alt)
		{
			text += "Alt+";
		}
		if (shift)
		{
			text += "Shift+";
		}
		KeyCode keyCode = (KeyCode)keyCodeValue;
		string text2 = keyCode.ToString();
		if (text2.StartsWith("Alpha", StringComparison.Ordinal))
		{
			text2 = text2.Substring(5);
		}
		else if (text2.StartsWith("Keypad", StringComparison.Ordinal))
		{
			text2 = "Num " + text2.Substring(6);
		}
		else if (keyCode == KeyCode.BackQuote)
		{
			text2 = "`";
		}
		return text + text2;
	}

	private static void GetCurrentFloorInfo(out string floorText, out string angleText)
	{
		floorText = "--";
		angleText = "--";
		scnEditor instance = scnEditor.instance;
		if (!((UnityEngine.Object)(object)instance == null) && instance.selectedFloors != null && instance.selectedFloors.Count == 1 && !((UnityEngine.Object)(object)instance.selectedFloors[0] == null))
		{
			scrFloor scrFloor = instance.selectedFloors[0];
			floorText = scrFloor.seqID.ToString();
			angleText = (TryGetDisplayAngle(instance, scrFloor, out var displayAngle) ? (displayAngle.ToString("0.##") + "°") : "--");
		}
	}

	internal static string L(string chinese, string english)
	{
		return settings != null && settings.language == 1 ? english : chinese;
	}

	private static void DrawSettingsGUI(UnityModManager.ModEntry entry)
	{
		GUILayout.BeginHorizontal();
		GUILayout.Label(L("语言", "Language"));
		if (GUILayout.Button("中文", GUILayout.ExpandWidth(false)))
		{
			settings.language = 0;
			((UnityModManager.ModSettings)settings).Save(entry);
			NoteSaveFeature.OnLanguageChanged();
		}
		if (GUILayout.Button("English", GUILayout.ExpandWidth(false)))
		{
			settings.language = 1;
			((UnityModManager.ModSettings)settings).Save(entry);
			NoteSaveFeature.OnLanguageChanged();
		}
		GUILayout.EndHorizontal();
		bool flag = GUILayout.Toggle(settings.relativeModeEnabled, L("相对角度放置", "Relative Placement"));
		if (flag != settings.relativeModeEnabled)
		{
			settings.relativeModeEnabled = flag;
			if (!flag)
			{
				SuspendCameraFollowForRelativeOff();
				if (hotkeyCaptureTarget == HotkeyCaptureTarget.CameraFollow)
				{
					capturingHotkey = false;
					hotkeyCaptureTarget = HotkeyCaptureTarget.None;
					captureMessage = "";
				}
			}
			else
			{
				ResumeCameraFollowAfterRelativeOn();
			}
			((UnityModManager.ModSettings)settings).Save(entry);
			RefreshFloorDirectionButtons(scnEditor.instance);
		}
		DrawShortcutRow(GetRelativeHotkeyDisplay(), HotkeyCaptureTarget.RelativePlacement, delegate
		{
			settings.ResetHotkey();
		});
		bool enabled = GUI.enabled;
		GUI.enabled = settings.relativeModeEnabled && enabled;
		bool flag2 = GUILayout.Toggle(settings.cameraFollowEnabled, L("视角跟随", "Camera Rotation"));
		if (flag2 != settings.cameraFollowEnabled)
		{
			SetCameraFollowEnabled(flag2, showNotice: false);
		}
		DrawShortcutRow(GetCameraHotkeyDisplay(), HotkeyCaptureTarget.CameraFollow, delegate
		{
			settings.ResetCameraHotkey();
		});
		GUI.enabled = enabled;
		GUILayout.Label(L("整段转向：多选轨道后，按住 Tab+目标方向。", "Redirect a section: select tracks, then hold Tab+direction."));
		bool preserveRhythm = GUILayout.Toggle(settings.preserveRhythm, L("保拍", "Preserve Rhythm"));
		bool rhythmSettingsChanged = preserveRhythm != settings.preserveRhythm;
		settings.preserveRhythm = preserveRhythm;
		if (preserveRhythm)
		{
			GUILayout.BeginHorizontal();
			GUILayout.Space(18f);
			GUILayout.BeginVertical();
			bool optimizeLayout = GUILayout.Toggle(settings.autoLayoutOptimization,
				L("自动排版优化 (测试)", "Automatic layout optimization (Beta)"));
			bool allowSpeedEvents = GUILayout.Toggle(settings.allowRhythmSpeedEvents,
				L("允许变速保拍（仅当轨道与旋转无法保拍时）", "Allow BPM compensation when angles and twirls cannot preserve timing"));
			GUILayout.EndVertical();
			GUILayout.EndHorizontal();
			rhythmSettingsChanged |= optimizeLayout != settings.autoLayoutOptimization
				|| allowSpeedEvents != settings.allowRhythmSpeedEvents;
			settings.autoLayoutOptimization = optimizeLayout;
			settings.allowRhythmSpeedEvents = allowSpeedEvents;
		}
		if (rhythmSettingsChanged)
		{
			SaveSettingsGUI(entry);
		}
		if (!string.IsNullOrEmpty(captureMessage))
		{
			GUILayout.Label(captureMessage);
		}
		GetCurrentFloorInfo(out var floorText, out var angleText);
		GUILayout.Label(L("音符：", "Floor: ") + floorText);
		GUILayout.Label(L("角度：", "Angle: ") + angleText);
	}

	private static void DrawShortcutRow(string shortcut, HotkeyCaptureTarget captureTarget, Action resetAction)
	{
		GUILayout.BeginHorizontal();
		GUILayout.Label(L("快捷键：", "Shortcut: ") + shortcut, GUILayout.ExpandWidth(false));
		if (GUILayout.Button((capturingHotkey && hotkeyCaptureTarget == captureTarget) ? L("请按键...", "Press key...") : L("设置", "Set"), GUILayout.ExpandWidth(false)))
		{
			capturingHotkey = true;
			hotkeyCaptureTarget = captureTarget;
			captureMessage = "";
		}
		if (GUILayout.Button(L("恢复默认", "Reset"), GUILayout.ExpandWidth(false)))
		{
			resetAction();
			((UnityModManager.ModSettings)settings).Save(modEntry);
			capturingHotkey = false;
			hotkeyCaptureTarget = HotkeyCaptureTarget.None;
			captureMessage = "";
		}
		GUILayout.EndHorizontal();
	}

	private static void SaveSettingsGUI(UnityModManager.ModEntry entry)
	{
		if (settings != null && entry != null)
		{
			((UnityModManager.ModSettings)settings).Save(entry);
		}
	}
}
internal sealed class AngleOverlay : MonoBehaviour
{
	private void Update()
	{
		AngleMod.UpdateOverlay();
		NoteSaveFeature.Update();
		TrackLayerFeature.Update();
	}

	private void OnGUI()
	{
		NoteSaveFeature.DrawOverlay();
	}
}
public sealed class AngleSettings : UnityModManager.ModSettings
{
	public int language;

	public bool preserveRhythm = true;

	public bool autoLayoutOptimization = true;

	public bool allowRhythmSpeedEvents;

	public bool relativeModeEnabled;

	public int hotkeyKeyCode = 114;

	public bool hotkeyControl;

	public bool hotkeyShift;

	public bool hotkeyAlt = true;

	public bool cameraFollowEnabled;

	public bool cameraFollowInitialized;

	public int cameraHotkeyKeyCode = 99;

	public bool cameraHotkeyControl;

	public bool cameraHotkeyShift;

	public bool cameraHotkeyAlt = true;

	public void Validate()
	{
		language = ((language == 1) ? 1 : 0);
		ValidateHotkey(hotkeyKeyCode, hotkeyControl, hotkeyShift, hotkeyAlt, cameraHotkeyKeyCode, cameraHotkeyControl, cameraHotkeyShift, cameraHotkeyAlt, ResetHotkey);
		ValidateHotkey(cameraHotkeyKeyCode, cameraHotkeyControl, cameraHotkeyShift, cameraHotkeyAlt, hotkeyKeyCode, hotkeyControl, hotkeyShift, hotkeyAlt, ResetCameraHotkey);
	}

	private static void ValidateHotkey(int keyCodeValue, bool control, bool shift, bool alt, int otherKeyCode, bool otherControl, bool otherShift, bool otherAlt, Action reset)
	{
		bool flag = keyCodeValue == otherKeyCode && control == otherControl && shift == otherShift && alt == otherAlt;
		if (!Enum.IsDefined(typeof(KeyCode), keyCodeValue) || !AngleMod.IsAssignableKey((KeyCode)keyCodeValue) || AngleMod.IsReservedDisplayShortcut((KeyCode)keyCodeValue, control, shift, alt) || flag)
		{
			reset();
		}
	}

	public void ResetHotkey()
	{
		hotkeyKeyCode = 114;
		hotkeyControl = false;
		hotkeyShift = false;
		hotkeyAlt = true;
	}

	public void ResetCameraHotkey()
	{
		cameraHotkeyKeyCode = 99;
		cameraHotkeyControl = false;
		cameraHotkeyShift = false;
		cameraHotkeyAlt = true;
	}

	public override void Save(UnityModManager.ModEntry modEntry)
	{
		UnityModManager.ModSettings.Save<AngleSettings>(this, modEntry);
	}
}
[HarmonyPatch(typeof(EditorKeybindManager), "ExecutePressedActions")]
internal static class KeyboardPresetScopePatch
{
	private static bool Prefix(EditorKeybindManager __instance, ref bool __result)
	{
		AngleMod.keyboardPresetScope = true;
		if (PatternPasteFeature.TryHandleDirectionShortcut(__instance))
		{
			__result = true;
			return false;
		}
		return true;
	}

	private static Exception Finalizer(Exception __exception)
	{
		AngleMod.keyboardPresetScope = false;
		return __exception;
	}
}
[HarmonyPatch(typeof(scnEditor), "CreateFloorWithCharOrAngle")]
internal static class RelativeAnglePlacementPatch
{
	private static void Prefix(scnEditor __instance, ref float angle, ref char chara, bool fullSpin)
	{
		AngleMod.TryApplyRelativeAngle(__instance, ref angle, ref chara, fullSpin);
	}
}
[HarmonyPatch(typeof(ToggleFloorNumsEditorAction), "Execute")]
internal static class ToggleFloorNumbersPatch
{
	private static void Prefix(scnEditor editor)
	{
		AngleMod.PrepareForFloorNumbers(editor);
	}
}
[HarmonyPatch(typeof(scnEditor), "DrawFloorNums")]
internal static class AngleLabelRefreshPatch
{
	private static void Postfix(scnEditor __instance)
	{
		if (AngleMod.showAllAngles)
		{
			AngleMod.UpdateAngleLabels(__instance);
		}
	}
}
[HarmonyPatch(typeof(scrFloor), "OnBecameVisible")]
internal static class AngleLabelVisibilityPatch
{
	private static void Postfix(scrFloor __instance)
	{
		AngleMod.UpdateAngleLabelForFloor(__instance);
	}
}
[HarmonyPatch(typeof(scnEditor), "SelectFloor")]
internal static class CameraFollowSelectionPatch
{
	private static void Postfix(scnEditor __instance, scrFloor floorToSelect)
	{
		AngleMod.OnEditorFloorSelected(__instance, floorToSelect);
	}
}
[HarmonyPatch(typeof(scnEditor), "DragCamera")]
internal static class RotatedCameraDragPatch
{
	private static readonly AccessTools.FieldRef<scnEditor, Vector3> CameraPositionAtDragStart = AccessTools.FieldRefAccess<scnEditor, Vector3>("cameraPositionAtDragStart");

	private static void Prefix(scnEditor __instance, ref Vector3 delta)
	{
		if (!((UnityEngine.Object)(object)__instance == null) && !(__instance.camera == null))
		{
			Vector3 vector = CameraPositionAtDragStart.Invoke(__instance);
			Vector3 vector2 = vector - delta;
			vector2.z = 0f;
			Vector3 vector3 = Quaternion.Euler(0f, 0f, __instance.camera.transform.localEulerAngles.z) * vector2;
			vector3.z = 0f;
			delta = vector - vector3;
		}
	}
}
[HarmonyPatch(typeof(FloorDirectionButton), "Init")]
internal static class DirectionButtonLabelRotationPatch
{
	private static void Postfix(FloorDirectionButton __instance)
	{
		AngleMod.ResetDirectionButtonLabelRotation(__instance);
	}
}
[HarmonyPatch(typeof(scnEditor), "UpdateFloorDirectionButtons")]
internal static class RelativeDeleteButtonPatch
{
	private static void Postfix(scnEditor __instance)
	{
		AngleMod.ApplyRelativeDeleteButton(__instance);
	}
}
