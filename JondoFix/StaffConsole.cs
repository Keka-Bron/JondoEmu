using System;
using Il2CppQFSW.QC;
using MelonLoader;
using UnityEngine.InputSystem;

namespace JondoFix
{
    /// <summary>
    /// The client's own staff console (Quantum Console, QFSW.QC), opened with F11 for an
    /// administrator, and the client's admin menu (menuadmin.xml, in the context menu of a
    /// character): this installs the menu's file and toggles the console. The commands both run are
    /// the server's (StaffConsoleHandler); see docs/admin-menu.md.
    /// </summary>
    internal static class StaffConsole
    {
        /// <summary>
        /// The admin menu reads its entries from &lt;persistentDataPath&gt;/Admin/menuadmin.xml (the client
        /// ships none): writes ours, embedded in this mod, so every client with the mod has the menu.
        /// One per language -- the client has no way of its own to translate a menu it was not given
        /// (its overwrite files go by rank, not by language) --, the one the client was started in.
        /// </summary>
        private static void InstallAdminMenuFile(MelonLogger.Instance log)
        {
            try
            {
                string language = ClientLanguage();
                using var stream = typeof(StaffConsole).Assembly.GetManifestResourceStream($"menuadmin.{language}.xml");
                if (stream == null) { log.Warning($"[JondoFix] Admin menu: no embedded menuadmin.{language}.xml."); return; }
                using var reader = new System.IO.StreamReader(stream);
                string xml = reader.ReadToEnd();
                string dir = System.IO.Path.Combine(UnityEngine.Application.persistentDataPath, "Admin");
                System.IO.Directory.CreateDirectory(dir);
                string path = System.IO.Path.Combine(dir, "menuadmin.xml");
                if (!System.IO.File.Exists(path) || System.IO.File.ReadAllText(path) != xml)
                    System.IO.File.WriteAllText(path, xml, new System.Text.UTF8Encoding(false));
                log.Msg($"[JondoFix] Admin menu file ({language}): {path}");
            }
            catch (Exception ex) { log.Warning($"[JondoFix] Admin menu file: {ex.Message}"); }
        }

        /// <summary>
        /// The language the client was started in (--langCode, as the launcher starts it): French or
        /// Spanish when it is one of them, English for every other language.
        /// </summary>
        private static string ClientLanguage()
        {
            string[] args = Environment.GetCommandLineArgs();
            int at = Array.IndexOf(args, "--langCode");
            string code = at >= 0 && at + 1 < args.Length ? args[at + 1].ToLowerInvariant() : "";
            return code.StartsWith("fr") ? "fr" : code.StartsWith("es") ? "es" : "en";
        }

        /// <summary>
        /// The one change the admin menu needs in the client. "#item" opens the catalogue window
        /// (AdminSelectItemUI) AND the player's own inventory, which is centered on the same spot
        /// and hides it: what looked like "#item only opens the inventory". The inventory is
        /// forced shut (openInventory false), and the catalogue shows alone.
        /// </summary>
        public static void HookAdminMenu(MelonLogger.Instance log)
        {
            InstallAdminMenuFile(log);
            try
            {
                var harmony = new HarmonyLib.Harmony("jondo.staffconsole.adminmenu");
                foreach (var m in HarmonyLib.AccessTools.GetDeclaredMethods(typeof(Il2CppCore.UILogic.Admin.AdminSelectItemUI)))
                    if (m.Name == "OpenAdminItemSelection")
                        harmony.Patch(m, prefix: new HarmonyLib.HarmonyMethod(typeof(StaffConsole).GetMethod(nameof(NoInventoryBehindTheSelector))));
            }
            catch (Exception ex) { log.Warning($"[JondoFix] Admin menu hooks: {ex.Message}"); }
        }

        /// <summary>Prefix of AdminSelectItemUI.OpenAdminItemSelection(callback, command, openInventory).</summary>
        public static void NoInventoryBehindTheSelector(ref bool __2) => __2 = false;

        public static void Tick(MelonLogger.Instance log)
        {
            var keyboard = Keyboard.current;
            if (keyboard == null || !keyboard.f11Key.wasPressedThisFrame) return;
            try
            {
                var console = QuantumConsole.Instance;
                if (console == null)
                {
                    // Instance only sees an enabled console: the client's sits disabled in its scene
                    // until something turns it on.
                    var all = UnityEngine.Resources.FindObjectsOfTypeAll(Il2CppInterop.Runtime.Il2CppType.Of<QuantumConsole>());
                    for (int i = 0; i < all.Length && console == null; i++)
                    {
                        var found = all[i].TryCast<QuantumConsole>();
                        if (found != null && found.gameObject.scene.IsValid()) console = found;
                    }
                    if (console == null) return;
                    for (var t = console.transform; t != null; t = t.parent) t.gameObject.SetActive(true);
                    console.enabled = true;
                    console.Activate(true);
                    return;
                }
                console.Toggle();
            }
            catch (Exception ex)
            {
                log.Warning($"[JondoFix] Staff console: {ex.GetType().Name}: {ex.Message}");
            }
        }
    }
}
