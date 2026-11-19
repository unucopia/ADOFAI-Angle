using System;
using System.Collections.Generic;
using ADOFAI;
using HarmonyLib;
using UnityEngine;
using UnityEngine.UI;

namespace Angle
{
    internal static class NoteSaveNativeUI
    {
        internal const string SettingsKey = "AngleNoteSave";
        internal static readonly LevelEventType EventType = (LevelEventType)901;
        internal static PropertiesPanel Panel;
        internal static InspectorTab Tab;
        internal static bool TabSelected;

        internal static void Initialize(scnEditor editor)
        {
            if (editor == null
                || editor.settingsPanel == null
                || Panel != null)
            {
                return;
            }

            if (GCS.settingsInfo == null)
            {
                GCS.settingsInfo = new Dictionary<string, LevelEventInfo>();
            }
            GCS.settingsInfo.Remove(SettingsKey);
            if (GCS.levelEventTypeString == null)
            {
                GCS.levelEventTypeString = new Dictionary<LevelEventType, string>();
            }
            if (GCS.levelEventIcons == null)
            {
                GCS.levelEventIcons = new Dictionary<LevelEventType, Sprite>();
                foreach (object value in Enum.GetValues(typeof(LevelEventType)))
                {
                    LevelEventType type = (LevelEventType)value;
                    Sprite sprite = Resources.Load<Sprite>(
                        "LevelEditor/LevelEvents/" + type.ToString());
                    if (sprite != null)
                    {
                        GCS.levelEventIcons[type] = sprite;
                    }
                }
            }

            LevelEventInfo eventInfo = CreateEventInfo();
            GCS.levelEventTypeString[EventType] = SettingsKey;
            GCS.levelEventIcons[EventType] =
                NoteSaveFeature.TabIcon ?? RDConstants.data.sprIconDecoration;

            InspectorPanel inspector = editor.settingsPanel;
            GameObject panelObject = UnityEngine.Object.Instantiate(
                RDConstants.data.prefab_propertiesPanel,
                inspector.panels,
                false);
            panelObject.name = SettingsKey;
            Panel = panelObject.GetComponent<PropertiesPanel>();
            Panel.levelEventType = EventType;
            Panel.Init(inspector, eventInfo);
            panelObject.SetActive(false);

            GameObject tabObject = UnityEngine.Object.Instantiate(
                RDConstants.data.prefab_tab,
                inspector.tabs,
                false);
            tabObject.name = SettingsKey;
            Tab = tabObject.GetComponent<InspectorTab>();
            Tab.Init(EventType, inspector);
            Tab.icon.sprite =
                NoteSaveFeature.TabIcon ?? RDConstants.data.sprIconDecoration;
            RectTransform tabRect = Tab.GetComponent<RectTransform>();
            int childCount = inspector.tabs.childCount;
            float tabY = 8f - 68f * Mathf.Max(0, childCount - 1);
            if (childCount >= 2)
            {
                RectTransform previous =
                    inspector.tabs.GetChild(childCount - 2) as RectTransform;
                float gap = 68f;
                if (childCount >= 3)
                {
                    RectTransform previousPrevious =
                        inspector.tabs.GetChild(childCount - 3) as RectTransform;
                    if (previous != null && previousPrevious != null)
                    {
                        float measuredGap = Mathf.Abs(
                            previous.anchoredPosition.y
                            - previousPrevious.anchoredPosition.y);
                        if (measuredGap > 1f)
                        {
                            gap = measuredGap;
                        }
                    }
                }
                if (previous != null)
                {
                    gap = Mathf.Max(gap, previous.rect.height);
                    tabY = previous.anchoredPosition.y - gap;
                }
            }
            tabRect.AnchorPosY(tabY);
            Tab.SetSelected(false);
        }

        private static LevelEventInfo CreateEventInfo()
        {
            LevelEventInfo info = new LevelEventInfo
            {
                categories = new List<LevelEventCategory>(),
                executionTime = LevelEventExecutionTime.Special,
                name = SettingsKey,
                propertiesInfo = new Dictionary<string, ADOFAI.PropertyInfo>(),
                type = EventType
            };
            return info;
        }

        internal static bool ShowNativePanel(InspectorPanel inspector, LevelEventType type)
        {
            if (type != EventType || Panel == null)
            {
                return false;
            }

            foreach (PropertiesPanel candidate in inspector.panelsList)
            {
                if (candidate != null)
                {
                    candidate.gameObject.SetActive(false);
                }
            }
            Panel.gameObject.SetActive(true);

            inspector.title.text = AngleMod.L("键型剪贴板", "Pattern Clipboard");
            inspector.selectedEventType = EventType;
            inspector.selectedEvent = null;
            TabSelected = true;
            NoteSaveFeature.NativePanelOpen = true;
            NoteSaveFeature.OnNativePanelShown();

            if (inspector.tabs != null)
            {
                for (int index = 0; index < inspector.tabs.childCount; index++)
                {
                    InspectorTab tab = inspector.tabs.GetChild(index).GetComponent<InspectorTab>();
                    if (tab != null)
                    {
                        tab.SetSelected(tab == Tab);
                    }
                }
            }
            return true;
        }

        internal static void CloseNativePanel()
        {
            NoteSaveFeature.NativePanelOpen = false;
            TabSelected = false;
            NoteSaveBrowser.Close();
            if (Panel != null)
            {
                Panel.gameObject.SetActive(false);
            }
        }
    }

    [HarmonyPatch(typeof(scnEditor), "Start")]
    internal static class NoteSaveNativeStartPatch
    {
        private static void Postfix(scnEditor __instance)
        {
            NoteSaveNativeUI.Initialize(__instance);
            AngleMod.ResetCameraSession(__instance);
        }
    }

    [HarmonyPatch(typeof(InspectorPanel), "ShowPanel")]
    internal static class NoteSaveNativeShowPanelPatch
    {
        private static bool Prefix(InspectorPanel __instance, LevelEventType eventType)
        {
            if (scnEditor.instance != null
                && __instance != scnEditor.instance.settingsPanel)
            {
                return true;
            }
            if (eventType != NoteSaveNativeUI.EventType
                && NoteSaveNativeUI.Panel != null
                && NoteSaveNativeUI.Tab != null
                && NoteSaveNativeUI.Tab.gameObject.activeSelf
                && NoteSaveNativeUI.TabSelected
                && __instance.selectedEventType == NoteSaveNativeUI.EventType)
            {
                NoteSaveNativeUI.Panel.gameObject.SetActive(true);
                NoteSaveFeature.NativePanelOpen = true;
                NoteSaveFeature.RequestNativeRebuild();
                return false;
            }
            return !NoteSaveNativeUI.ShowNativePanel(__instance, eventType);
        }

        private static void Postfix(InspectorPanel __instance, LevelEventType eventType)
        {
            if (scnEditor.instance != null
                && __instance != scnEditor.instance.settingsPanel)
            {
                return;
            }
            if (eventType != NoteSaveNativeUI.EventType
                && __instance.selectedEventType != NoteSaveNativeUI.EventType)
            {
                NoteSaveNativeUI.CloseNativePanel();
            }
        }
    }

    [HarmonyPatch(typeof(InspectorPanel), "HideAllInspectorTabs")]
    internal static class NoteSaveNativeHidePatch
    {
        private static void Postfix(InspectorPanel __instance)
        {
            if (scnEditor.instance == null
                || __instance != scnEditor.instance.settingsPanel)
            {
                return;
            }
            NoteSaveNativeUI.CloseNativePanel();
        }
    }

    [HarmonyPatch(typeof(EditorConstants), "IsSetting")]
    internal static class NoteSaveTypeIsSettingPatch
    {
        private static void Postfix(LevelEventType type, ref bool __result)
        {
            if (type == NoteSaveNativeUI.EventType)
            {
                __result = true;
            }
        }
    }

    [HarmonyPatch(typeof(InspectorTab), "SetSelected")]
    internal static class NoteSaveTabSetSelectedPatch
    {
        private static bool Prefix(InspectorTab __instance, bool selected)
        {
            if (__instance == null
                || __instance.levelEventType != NoteSaveNativeUI.EventType)
            {
                return true;
            }

            NoteSaveNativeUI.TabSelected = selected;
            if (!selected)
            {
                NoteSaveFeature.NativePanelOpen = false;
            }

            if (__instance.cycleButtons != null)
            {
                __instance.cycleButtons.gameObject.SetActive(false);
            }

            RectTransform rect = __instance.GetComponent<RectTransform>();
            if (rect != null)
            {
                rect.sizeDelta = new Vector2(0f, rect.sizeDelta.y);
                rect.anchoredPosition = new Vector2(
                    selected ? 0f : 3f,
                    rect.anchoredPosition.y);
            }

            if (__instance.button != null)
            {
                ColorBlock colors = __instance.button.colors;
                colors.normalColor =
                    Color.white.WithAlpha(selected ? 0.7f : 0.45f);
                __instance.button.colors = colors;
            }
            if (__instance.icon != null)
            {
                __instance.icon.color =
                    Color.white.WithAlpha(selected ? 1f : 0.6f);
            }
            return false;
        }
    }
}
