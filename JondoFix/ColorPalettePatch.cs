using System;
using System.Collections.Generic;
using HarmonyLib;
using MelonLoader;
using UnityEngine;
using UnityEngine.UIElements;
using Il2CppInterop.Runtime;
using Il2CppCore.UILogic.Inventory.Uis.Cosmetic.AppearanceTabs;
using Il2CppCore.UILogic.Inventory.Uis.Cosmetic.AppearanceItems;
using Il2CppCore.UILogic.Inventory.Uis.PlayerInventory;
using Il2CppCore.UILogic.Components.Figma;

namespace JondoFix
{
    /// <summary>
    /// Patches for AppearanceColorsTab to ensure color palettes can be created, confirmed,
    /// or modified directly via the Validate (btnValidate) / Cancel (btnCancel) buttons
    /// without requiring the outfit save button. Also supports modifying existing palettes (via color adjustments or right-click).
    /// </summary>
    public static class ColorPalettePatch
    {
        public static AppearanceColorsTab ActiveTab { get; private set; }

        private static DateTime _lastSaved = DateTime.MinValue;
        private static int _editingSlotId = -1;
        private static bool _buttonsWired = false;

        public static void OnCharacterChanged()
        {
            try
            {
                _editingSlotId = -1;
                if (ActiveTab != null)
                {
                    HideValidationButtons(ActiveTab);
                    MelonCoroutines.Start(RefreshColorsAfterSwitch());
                }
            }
            catch (Exception ex)
            {
                MelonLogger.Warning($"[ColorPalette] Error handling character change: {ex.Message}");
            }
        }

        private static System.Collections.IEnumerator RefreshColorsAfterSwitch()
        {
            for (int i = 0; i < 25; i++)
            {
                yield return null;
            }
            try
            {
                if (ActiveTab != null && ActiveTab.m_uiBinding?.rightValidationParent?.panel != null)
                {
                    ActiveTab.UpdateContent();
                    HideValidationButtons(ActiveTab);
                    MelonLogger.Msg("[ColorPalette] Refreshed color palette slots after hero switch.");
                }
            }
            catch (Exception ex)
            {
                MelonLogger.Warning($"[ColorPalette] Error in RefreshColorsAfterSwitch: {ex.Message}");
            }
        }

        private static int ExtractRgb(int original, Color c)
        {
            if (original > 0) return original;
            int r = Math.Clamp((int)Math.Round(c.r * 255f), 0, 255);
            int g = Math.Clamp((int)Math.Round(c.g * 255f), 0, 255);
            int b = Math.Clamp((int)Math.Round(c.b * 255f), 0, 255);
            return (r << 16) | (g << 8) | b;
        }

        public static void ShowValidationButtons(AppearanceColorsTab tab, string customTitle = null)
        {
            try
            {
                if (tab == null) return;
                var ui = tab.m_appearanceUI;
                var binding = tab.m_uiBinding;
                if (binding == null) return;

                if (ui != null)
                {
                    ui.editMode = true;
                    ui.tempEditedPart = AppearanceType.Color;
                    ui.lastSlotType = AppearanceType.Color;
                }

                if (binding.rightValidationParent != null)
                {
                    binding.rightValidationParent.style.display = new StyleEnum<DisplayStyle>(DisplayStyle.Flex);
                    binding.rightValidationParent.style.visibility = new StyleEnum<Visibility>(Visibility.Visible);
                }
                if (binding.validateParent != null)
                {
                    binding.validateParent.style.display = new StyleEnum<DisplayStyle>(DisplayStyle.Flex);
                    binding.validateParent.style.visibility = new StyleEnum<Visibility>(Visibility.Visible);
                }
                if (binding.btnsParent != null)
                {
                    binding.btnsParent.style.display = new StyleEnum<DisplayStyle>(DisplayStyle.Flex);
                    binding.btnsParent.style.visibility = new StyleEnum<Visibility>(Visibility.Visible);
                }
                if (binding.btnValidate != null)
                {
                    binding.btnValidate.style.display = new StyleEnum<DisplayStyle>(DisplayStyle.Flex);
                    binding.btnValidate.style.visibility = new StyleEnum<Visibility>(Visibility.Visible);
                    binding.btnValidate.SetEnabled(true);
                }
                if (binding.btnCancel != null)
                {
                    binding.btnCancel.style.display = new StyleEnum<DisplayStyle>(DisplayStyle.Flex);
                    binding.btnCancel.style.visibility = new StyleEnum<Visibility>(Visibility.Visible);
                    binding.btnCancel.SetEnabled(true);
                }
                if (binding.validateInfo != null)
                {
                    binding.validateInfo.style.display = new StyleEnum<DisplayStyle>(DisplayStyle.Flex);
                    binding.validateInfo.style.visibility = new StyleEnum<Visibility>(Visibility.Visible);
                    if (!string.IsNullOrEmpty(customTitle))
                    {
                        binding.validateInfo.text = customTitle;
                    }
                    else if (ui != null && ui.currentSlotId >= 0)
                    {
                        bool isExisting = tab.displayedColor != null &&
                                          ui.currentSlotId < tab.displayedColor.Count &&
                                          tab.displayedColor[ui.currentSlotId]?.data != null &&
                                          tab.displayedColor[ui.currentSlotId]?.data.originalColor1 != -1;
                        binding.validateInfo.text = isExisting ? $"Edit palette #{ui.currentSlotId + 1}" : "Create new palette";
                    }
                }
            }
            catch (Exception ex)
            {
                MelonLogger.Warning($"[ColorPalette] Error showing validation buttons: {ex.Message}");
            }
        }

        public static void HideValidationButtons(AppearanceColorsTab tab)
        {
            try
            {
                if (tab == null) return;
                var binding = tab.m_uiBinding;
                if (binding != null && binding.rightValidationParent != null)
                {
                    binding.rightValidationParent.style.display = new StyleEnum<DisplayStyle>(DisplayStyle.None);
                }
                if (tab.m_appearanceUI != null)
                {
                    tab.m_appearanceUI.editMode = false;
                }
                _editingSlotId = -1;
            }
            catch (Exception ex)
            {
                MelonLogger.Warning($"[ColorPalette] Error hiding validation buttons: {ex.Message}");
            }
        }

        public static void SaveCurrentPalette(AppearanceColorsTab tab)
        {
            try
            {
                if (tab == null || tab.m_appearanceUI == null) return;
                if ((DateTime.UtcNow - _lastSaved).TotalMilliseconds < 500) return;

                int slotIdx = tab.m_appearanceUI.currentSlotId;
                if (slotIdx < 0) return;

                var cs = tab.currentColorsChoice;
                if (cs == null) return;

                var cols = new List<int>
                {
                    ExtractRgb(cs.originalColor1, cs.color1),
                    ExtractRgb(cs.originalColor2, cs.color2),
                    ExtractRgb(cs.originalColor3, cs.color3),
                    ExtractRgb(cs.originalColor4, cs.color4),
                    ExtractRgb(cs.originalColor5, cs.color5),
                    ExtractRgb(cs.originalColor6, cs.color6)
                };

                _lastSaved = DateTime.UtcNow;
                MelonLogger.Msg($"[ColorPalette] Confirming and saving palette #{slotIdx + 1}: [{string.Join(", ", cols)}]");

                var body = new Dictionary<string, object>
                {
                    ["slot"] = slotIdx,
                    ["colors"] = cols
                };

                SavePaletteToServer(body, slotIdx);

                // Update local slot visual data
                if (tab.displayedColor != null && slotIdx < tab.displayedColor.Count)
                {
                    var slot = tab.displayedColor[slotIdx];
                    if (slot != null)
                    {
                        slot.data = cs;
                        slot.SetDraftState(false);
                        slot.SetActiveState(true);
                        slot.SetSelectedState(true);
                    }
                }

                HideValidationButtons(tab);
            }
            catch (Exception ex)
            {
                MelonLogger.Warning($"[ColorPalette] Error in SaveCurrentPalette: {ex.Message}");
            }
        }

        private static void SavePaletteToServer(Dictionary<string, object> body, int slotIdx)
        {
            string token = (Environment.GetEnvironmentVariable("JONDO_CONTROL_TOKEN") ??
                            Environment.GetEnvironmentVariable("JONDO_PLAYER_TOKEN") ?? "").Trim();
            if (token.Length > 0) body["token"] = token;

            System.Threading.Tasks.Task.Run(async () =>
            {
                try
                {
                    var (code, text) = await AdminItemsUi.PostAsync("palette/save", body);
                    if (code != 200)
                    {
                        MelonLogger.Warning($"[ColorPalette] API palette/save returned {code}: {text}");
                    }
                    else
                    {
                        MelonLogger.Msg($"[ColorPalette] Palette #{slotIdx + 1} saved successfully on server.");
                    }
                }
                catch (Exception ex)
                {
                    MelonLogger.Warning($"[ColorPalette] Error saving palette to server: {ex.Message}");
                }
            });
        }

        public static void CancelCurrentEdit(AppearanceColorsTab tab)
        {
            try
            {
                if (tab == null) return;
                int slotIdx = _editingSlotId >= 0 ? _editingSlotId : (tab.m_appearanceUI != null ? tab.m_appearanceUI.currentSlotId : -1);
                if (slotIdx >= 0 && tab.displayedColor != null && slotIdx < tab.displayedColor.Count)
                {
                    var slot = tab.displayedColor[slotIdx];
                    if (slot != null && slot.data != null && slot.data.originalColor1 != -1)
                    {
                        tab.SelectColor(slotIdx, slot.data);
                    }
                }
                HideValidationButtons(tab);
            }
            catch (Exception ex)
            {
                MelonLogger.Warning($"[ColorPalette] Error in CancelCurrentEdit: {ex.Message}");
            }
        }

        [HarmonyPatch(typeof(AppearanceColorsTab), nameof(AppearanceColorsTab.UpdateContent))]
        public static class UpdateContentPatch
        {
            public static void Postfix(AppearanceColorsTab __instance)
            {
                try
                {
                    if (__instance == null) return;
                    ActiveTab = __instance;

                    var binding = __instance.m_uiBinding;
                    if (binding != null && !_buttonsWired)
                    {
                        // Wire btnValidate
                        if (binding.btnValidate != null)
                        {
                            binding.btnValidate.RegisterCallback<ClickEvent>(DelegateSupport.ConvertDelegate<EventCallback<ClickEvent>>(
                                new Action<ClickEvent>(evt =>
                                {
                                    try { evt.StopPropagation(); } catch { }
                                    SaveCurrentPalette(__instance);
                                })), TrickleDown.NoTrickleDown);
                        }

                        // Wire btnCancel
                        if (binding.btnCancel != null)
                        {
                            binding.btnCancel.RegisterCallback<ClickEvent>(DelegateSupport.ConvertDelegate<EventCallback<ClickEvent>>(
                                new Action<ClickEvent>(evt =>
                                {
                                    try { evt.StopPropagation(); } catch { }
                                    CancelCurrentEdit(__instance);
                                })), TrickleDown.NoTrickleDown);
                        }

                        _buttonsWired = true;
                    }

                    // Hook Right Click on slots to enter Edit Mode
                    if (__instance.displayedColor != null)
                    {
                        for (int i = 0; i < __instance.displayedColor.Count; i++)
                        {
                            int slotIdx = i;
                            var slot = __instance.displayedColor[i];
                            if (slot != null)
                            {
                                slot.RegisterCallback<MouseDownEvent>(DelegateSupport.ConvertDelegate<EventCallback<MouseDownEvent>>(
                                    new Action<MouseDownEvent>(evt =>
                                    {
                                        if (evt.button == 1) // Right click
                                        {
                                            try { evt.StopPropagation(); } catch { }
                                            _editingSlotId = slotIdx;
                                            if (__instance.m_appearanceUI != null)
                                            {
                                                __instance.m_appearanceUI.currentSlotId = slotIdx;
                                            }
                                            if (slot.data != null && slot.data.originalColor1 != -1)
                                            {
                                                __instance.SelectColor(slotIdx, slot.data);
                                            }
                                            ShowValidationButtons(__instance, $"Edit palette #{slotIdx + 1}");
                                            MelonLogger.Msg($"[ColorPalette] Edit mode activated by right-click on slot {slotIdx + 1}.");
                                        }
                                    })), TrickleDown.NoTrickleDown);
                            }
                        }
                    }
                }
                catch (Exception ex)
                {
                    MelonLogger.Warning($"[ColorPalette] Error in UpdateContent Postfix: {ex.Message}");
                }
            }
        }

        [HarmonyPatch(typeof(AppearanceColorsTab), nameof(AppearanceColorsTab.ClickColorSlot))]
        public static class ClickColorSlotPatch
        {
            public static void Postfix(AppearanceColorsTab __instance, int index)
            {
                try
                {
                    if (__instance == null) return;
                    ActiveTab = __instance;

                    if (__instance.displayedColor == null || index < 0 || index >= __instance.displayedColor.Count)
                        return;

                    var clickedSlot = __instance.displayedColor[index];
                    if (clickedSlot == null) return;

                    // CASE 1: ALREADY CONFIGURED palette (slot.data != null and not -1)
                    if (clickedSlot.data != null && clickedSlot.data.originalColor1 != -1)
                    {
                        _editingSlotId = -1;
                        for (int i = 0; i < __instance.displayedColor.Count; i++)
                        {
                            var s = __instance.displayedColor[i];
                            if (s != null)
                            {
                                s.SetSelectedState(i == index);
                                s.SetActiveState(i == index);
                            }
                        }

                        // Normal click just wears/selects the palette; buttons remain hidden until modified or right-clicked
                        HideValidationButtons(__instance);
                        MelonLogger.Msg($"[ColorPalette] Selected palette #{index + 1}.");
                    }
                    // CASE 2: NEW / UNCONFIGURED palette (slot.data == null)
                    else
                    {
                        _editingSlotId = index;
                        ShowValidationButtons(__instance, "Create new palette");
                        MelonLogger.Msg($"[ColorPalette] Opened new slot #{index + 1} to create palette.");
                    }
                }
                catch (Exception ex)
                {
                    MelonLogger.Warning($"[ColorPalette] Error in ClickColorSlot patch: {ex.Message}");
                }
            }
        }

        [HarmonyPatch(typeof(AppearanceColorsTab), nameof(AppearanceColorsTab.OnUpdateColors))]
        public static class OnUpdateColorsPatch
        {
            public static void Postfix(AppearanceColorsTab __instance)
            {
                try
                {
                    if (__instance == null) return;
                    ActiveTab = __instance;
                    if (_editingSlotId < 0 && __instance.m_appearanceUI != null)
                    {
                        _editingSlotId = __instance.m_appearanceUI.currentSlotId;
                    }
                    ShowValidationButtons(__instance);
                }
                catch (Exception ex)
                {
                    MelonLogger.Warning($"[ColorPalette] Error in OnUpdateColors patch: {ex.Message}");
                }
            }
        }

        [HarmonyPatch(typeof(AppearanceUI), nameof(AppearanceUI.ValidateEdition))]
        public static class ValidateEditionPatch
        {
            public static void Postfix(AppearanceUI __instance)
            {
                try
                {
                    if (__instance == null || ActiveTab == null) return;
                    if (__instance.lastSlotType == AppearanceType.Color || __instance.tempEditedPart == AppearanceType.Color)
                    {
                        SaveCurrentPalette(ActiveTab);
                    }
                }
                catch (Exception ex)
                {
                    MelonLogger.Warning($"[ColorPalette] Error in ValidateEdition Postfix: {ex.Message}");
                }
            }
        }

        [HarmonyPatch(typeof(AppearanceUI), "CancelEdition")]
        public static class CancelEditionPatch
        {
            public static void Postfix(AppearanceUI __instance)
            {
                try
                {
                    if (ActiveTab != null)
                    {
                        CancelCurrentEdit(ActiveTab);
                    }
                }
                catch (Exception ex)
                {
                    MelonLogger.Warning($"[ColorPalette] Error in CancelEdition Postfix: {ex.Message}");
                }
            }
        }

        [HarmonyPatch(typeof(AppearanceColorsTab), "UpdateRightValidateButton")]
        public static class UpdateRightValidateButtonPatch
        {
            public static void Postfix(AppearanceColorsTab __instance)
            {
                try
                {
                    if (__instance == null) return;
                    var binding = __instance.m_uiBinding;
                    if (binding != null && binding.btnValidate != null)
                    {
                        binding.btnValidate.SetEnabled(true);
                    }
                }
                catch { }
            }
        }

        [HarmonyPatch(typeof(CosmeticUi), "OnSaveCurrentOutfit")]
        public static class OnSaveCurrentOutfitPatch
        {
            public static void Prefix(CosmeticUi __instance)
            {
                try
                {
                    if (ActiveTab != null && ActiveTab.m_appearanceUI != null && ActiveTab.m_appearanceUI.editMode)
                    {
                        SaveCurrentPalette(ActiveTab);
                    }
                }
                catch (Exception ex)
                {
                    MelonLogger.Warning($"[ColorPalette] Error in OnSaveCurrentOutfit Prefix: {ex.Message}");
                }
            }
        }
    }
}
