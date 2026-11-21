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
    /// Patches for AppearanceColorsTab so that a colour palette can be created, confirmed or
    /// modified with the tab's own Validate (btnValidate) and Cancel (btnCancel) buttons, without
    /// the outfit save button. A right click on a palette opens it for editing. A palette is kept
    /// by the server's control API (palette/save); the tab shows it as saved only once the server
    /// has taken it, and the server then sends the colours tab's collection again.
    /// </summary>
    public static class ColorPalettePatch
    {
        public static AppearanceColorsTab ActiveTab { get; private set; }

        private static DateTime _lastSaved = DateTime.MinValue;
        private static int _editingSlotId = -1;

        // The buttons and slots already hooked, by their native object: the tab's binding is
        // rebuilt when the window is opened again, and its new buttons need their own callbacks,
        // while UpdateContent runs many times on the same ones and must not pile them up.
        private static IntPtr _wiredValidate = IntPtr.Zero;
        private static readonly HashSet<IntPtr> _hookedSlots = new HashSet<IntPtr>();

        /// <summary>
        /// The RGB value of a palette colour: the original one when it has one (black, 0,
        /// included), otherwise the colour shown, -1 being the client's "none".
        /// </summary>
        private static int ExtractRgb(int original, Color c)
        {
            if (original >= 0) return original;
            int r = Math.Clamp((int)Math.Round(c.r * 255f), 0, 255);
            int g = Math.Clamp((int)Math.Round(c.g * 255f), 0, 255);
            int b = Math.Clamp((int)Math.Round(c.b * 255f), 0, 255);
            return (r << 16) | (g << 8) | b;
        }

        private static void Show(VisualElement element)
        {
            if (element == null) return;
            element.style.display = new StyleEnum<DisplayStyle>(DisplayStyle.Flex);
            element.style.visibility = new StyleEnum<Visibility>(Visibility.Visible);
        }

        /// <summary>The validation buttons, with the client's own texts.</summary>
        public static void ShowValidationButtons(AppearanceColorsTab tab)
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

                Show(binding.rightValidationParent);
                Show(binding.validateParent);
                Show(binding.btnsParent);
                Show(binding.validateInfo);
                if (binding.btnValidate != null)
                {
                    Show(binding.btnValidate);
                    binding.btnValidate.SetEnabled(true);
                }
                if (binding.btnCancel != null)
                {
                    Show(binding.btnCancel);
                    binding.btnCancel.SetEnabled(true);
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
                MelonLogger.Msg($"[ColorPalette] Saving palette #{slotIdx + 1}: [{string.Join(", ", cols)}]");

                var body = new Dictionary<string, object>
                {
                    ["slot"] = slotIdx,
                    ["colors"] = cols
                };
                // Shown as saved only once the server has kept it, in the tab open by then.
                SavePaletteToServer(body, slotIdx, () =>
                {
                    var tab = ActiveTab;
                    if (tab == null) return;
                    if (tab.displayedColor != null && slotIdx < tab.displayedColor.Count && tab.displayedColor[slotIdx] is { } slot)
                    {
                        slot.data = cs;
                        slot.SetDraftState(false);
                        slot.SetActiveState(true);
                        slot.SetSelectedState(true);
                    }
                    HideValidationButtons(tab);
                });
            }
            catch (Exception ex)
            {
                MelonLogger.Warning($"[ColorPalette] Error in SaveCurrentPalette: {ex.Message}");
            }
        }

        /// <summary>
        /// Posts the palette off the main thread; what the answer changes in the tab goes back to
        /// it through the mod's queue, and only when the server kept the palette.
        /// </summary>
        private static void SavePaletteToServer(Dictionary<string, object> body, int slotIdx, Action onSaved)
        {
            string token = (Environment.GetEnvironmentVariable("JONDO_CONTROL_TOKEN") ??
                            Environment.GetEnvironmentVariable("JONDO_PLAYER_TOKEN") ?? "").Trim();
            if (token.Length > 0) body["token"] = token;

            System.Threading.Tasks.Task.Run(async () =>
            {
                var (code, text) = await AdminItemsUi.PostAsync("palette/save", body);
                AdminItemsUi._toUnity.Enqueue(() =>
                {
                    if (code != 200)
                    {
                        MelonLogger.Warning($"[ColorPalette] palette/save answered {code}: {text}");
                        return;
                    }
                    MelonLogger.Msg($"[ColorPalette] Palette #{slotIdx + 1} saved on the server.");
                    onSaved();
                });
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

        /// <summary>The validate and cancel buttons of a binding not wired yet; they act on the tab open at click time.</summary>
        private static void WireButtons(AppearanceColorsTab tab)
        {
            var binding = tab.m_uiBinding;
            if (binding == null || binding.btnValidate == null || binding.btnValidate.Pointer == _wiredValidate) return;

            binding.btnValidate.RegisterCallback<ClickEvent>(DelegateSupport.ConvertDelegate<EventCallback<ClickEvent>>(
                new Action<ClickEvent>(evt =>
                {
                    try { evt.StopPropagation(); } catch { }
                    SaveCurrentPalette(ActiveTab);
                })), TrickleDown.NoTrickleDown);

            if (binding.btnCancel != null)
            {
                binding.btnCancel.RegisterCallback<ClickEvent>(DelegateSupport.ConvertDelegate<EventCallback<ClickEvent>>(
                    new Action<ClickEvent>(evt =>
                    {
                        try { evt.StopPropagation(); } catch { }
                        CancelCurrentEdit(ActiveTab);
                    })), TrickleDown.NoTrickleDown);
            }

            _wiredValidate = binding.btnValidate.Pointer;
            _hookedSlots.Clear();
        }

        /// <summary>A right click on a palette opens it for editing; each slot element is hooked once.</summary>
        private static void HookRightClicks(AppearanceColorsTab tab)
        {
            if (tab.displayedColor == null) return;
            for (int i = 0; i < tab.displayedColor.Count; i++)
            {
                var slot = tab.displayedColor[i];
                if (slot == null || !_hookedSlots.Add(slot.Pointer)) continue;
                IntPtr element = slot.Pointer;
                slot.RegisterCallback<MouseDownEvent>(DelegateSupport.ConvertDelegate<EventCallback<MouseDownEvent>>(
                    new Action<MouseDownEvent>(evt =>
                    {
                        if (evt.button != 1) return;
                        try { evt.StopPropagation(); } catch { }
                        EditSlot(element);
                    })), TrickleDown.NoTrickleDown);
            }
        }

        /// <summary>The slot element's current place in the open tab, looked up at click time.</summary>
        private static void EditSlot(IntPtr element)
        {
            var tab = ActiveTab;
            if (tab?.displayedColor == null) return;
            for (int index = 0; index < tab.displayedColor.Count; index++)
            {
                var slot = tab.displayedColor[index];
                if (slot == null || slot.Pointer != element) continue;
                _editingSlotId = index;
                if (tab.m_appearanceUI != null) tab.m_appearanceUI.currentSlotId = index;
                if (slot.data != null && slot.data.originalColor1 != -1) tab.SelectColor(index, slot.data);
                ShowValidationButtons(tab);
                MelonLogger.Msg($"[ColorPalette] Editing palette #{index + 1}.");
                return;
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
                    WireButtons(__instance);
                    HookRightClicks(__instance);
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

                    // A saved palette: a plain click wears it; the buttons wait for a change or a right click.
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
                        HideValidationButtons(__instance);
                    }
                    // An empty slot: a new palette to fill.
                    else
                    {
                        _editingSlotId = index;
                        ShowValidationButtons(__instance);
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
