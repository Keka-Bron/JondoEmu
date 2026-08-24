using System;
using MelonLoader;
using HarmonyLib;
using Il2CppThrift.Transport;
using Il2CppZaap_CSharp_Client;
using System.IO;
using System.Text;
using System.Linq;
using System.Collections.Generic;
using System.Text.Json;
using Il2CppCore.DataCenter;
using Il2CppCore.DataCenter.Metadata.World;
using Il2CppCore.DataCenter.Metadata.Item;
using Il2CppCore.UILogic.Admin;
using Il2CppCore.UILogic.Components.Filters;

[assembly: MelonInfo(typeof(JondoFix.JondoFixMod), "JondoFix", "1.3.4", "Jondo")]
[assembly: MelonGame("Ankama", "Dofus")]

namespace JondoFix
{
    public class JondoFixMod : MelonMod
    {
        public static bool UseLocalRedirect { get; private set; } = false;
        /// <summary>
        /// Si el cliente puede ENSEÑAR las cosas de administrador: el id junto al nombre del
        /// objeto y el catálogo sin filtrar.
        ///
        /// NO es una comprobación de permisos y no se puede usar como tal. Sale de una variable
        /// de entorno que pone el lanzador, y cualquiera que arranque el Dofus.exe a mano puede
        /// ponérsela. Quien decide de verdad es el servidor, que mira el rol en la base cada vez
        /// que llega un comando.
        /// </summary>
        public static bool IsJondoAdministrator { get; private set; } = false;

        /// <summary>
        /// El rol de administrador, el mismo <c>Roles.Administrador</c> del servidor.
        ///
        /// Se repite aquí porque JondoFix no puede referenciar Jondo.Unity.Contract: es un mod del
        /// cliente y se compila contra los ensamblados de Unity. Si la escala vuelve a moverse
        /// —ya pasó una vez, del 4 al 5— hay que tocar este número a mano, y no lo va a avisar el
        /// compilador. Por eso está aquí arriba y con nombre, y no suelto dentro de un if.
        /// </summary>
        private const int RolAdministrador = 5;
        public static Il2CppSystem.Net.Security.RemoteCertificateValidationCallback BypassedCallback { get; private set; }
        public static Il2CppMono.Security.Interface.MonoRemoteCertificateValidationCallback BypassedMonoCallback { get; private set; }
        private static bool hasDumped = false;
        private static bool itemMappingsLoadedFromClient = false;
        public static readonly Dictionary<int, int> ItemNameIdToGid = new Dictionary<int, int>();

        /// <summary>
        /// Emulator data root as seen from INSIDE the game process.
        ///
        /// AppContext.BaseDirectory is useless here: it points at the client folder, not the
        /// emulator's. We derive it by going one level up from the game directory
        /// (...\DofusClient\..\Jondo Unity Emulator) and fall back to the legacy path.
        /// </summary>
        private static string _emulatorRoot;
        public static string EmulatorRoot
        {
            get
            {
                if (_emulatorRoot != null) return _emulatorRoot;
                try
                {
                    string configured = Environment.GetEnvironmentVariable("JONDO_EMULATOR_ROOT");
                    if (!string.IsNullOrWhiteSpace(configured) && Directory.Exists(configured))
                    {
                        _emulatorRoot = Path.GetFullPath(configured);
                        return _emulatorRoot;
                    }

                    string gameDir = AppDomain.CurrentDomain.BaseDirectory;
                    string parent = Path.GetFullPath(Path.Combine(gameDir, ".."));
                    // El nombre de la carpeta del repositorio. Si alguien la tiene con otro
                    // nombre, JONDO_EMULATOR_ROOT ya lo resuelve, que es para lo que está.
                    foreach (string folder in new[] { "Jondo Unity Emulator", "JondoEmu" })
                    {
                        string candidate = Path.Combine(parent, folder);
                        if (Directory.Exists(candidate))
                        {
                            _emulatorRoot = candidate;
                            return _emulatorRoot;
                        }
                    }
                }
                catch { }
                _emulatorRoot = @"C:\Jondo";
                return _emulatorRoot;
            }
        }

        /// <summary>Resolves a data file: new root first, legacy path as fallback.</summary>
        private static string DataFile(string relative)
        {
            string preferred = Path.Combine(EmulatorRoot, relative);
            if (File.Exists(preferred) || Directory.Exists(Path.GetDirectoryName(preferred) ?? ""))
            {
                return preferred;
            }
            return Path.Combine(@"C:\Jondo", relative);
        }

        private static void LoadItemNames()
        {
            try
            {
                string path = DataFile(@"dofus3_data\items.json");
                if (!File.Exists(path))
                {
                    MelonLogger.Msg($"[JondoFix] Optional legacy items.json not found at {path}; client metadata will be used.");
                    return;
                }

                string content = File.ReadAllText(path);
                using (var doc = JsonDocument.Parse(content))
                {
                    var root = doc.RootElement;
                    if (root.TryGetProperty("references", out var references))
                    {
                        if (references.TryGetProperty("RefIds", out var refIds))
                        {
                            foreach (var item in refIds.EnumerateArray())
                            {
                                if (item.TryGetProperty("type", out var type))
                                {
                                    if (type.TryGetProperty("class", out var cls))
                                    {
                                        string clsName = cls.GetString();
                                        if (clsName == "ItemData" || clsName == "WeaponData")
                                        {
                                            if (item.TryGetProperty("data", out var data))
                                            {
                                                if (data.TryGetProperty("id", out var idField) && data.TryGetProperty("nameId", out var nameIdField))
                                                {
                                                    int id = idField.GetInt32();
                                                    int nameId = nameIdField.GetInt32();
                                                    ItemNameIdToGid[nameId] = id;
                                                }
                                            }
                                        }
                                    }
                                }
                            }
                        }
                    }
                }
                MelonLogger.Msg($"[JondoFix] Loaded {ItemNameIdToGid.Count} item name mappings successfully.");
            }
            catch (Exception ex)
            {
                MelonLogger.Error($"[JondoFix] Error loading item names: {ex.Message}");
            }
        }

        /// <summary>
        /// Builds the localization-key to item-id map from the catalogue bundled with the running
        /// client. It therefore covers the exact Dofus version in use, including internal items
        /// that may be absent from an old emulator-side JSON export.
        /// </summary>
        private static void LoadItemNamesFromClientData()
        {
            if (itemMappingsLoadedFromClient) return;

            var root = Il2CppCore.DataCenter.DataCenterModule.itemsDataRoot;
            var items = root?.GetObjects();
            if (items == null || items.Count == 0) return;

            int added = 0;
            int madeSaleable = 0;
            for (int i = 0; i < items.Count; i++)
            {
                ItemData item = items[i];
                if (item == null || item.nameId > int.MaxValue) continue;

                ItemNameIdToGid[(int)item.nameId] = item.id;
                added++;

                // Destapar los objetos que Ankama esconde SÓLO al administrador. Leer el
                // catálogo lo hace todo el mundo, porque de ahí sale la tabla de nombres, pero
                // tocar la marca cambia lo que ve el jugador en su enciclopedia: sin esta
                // condición, cualquiera se encontraría los objetos internos mezclados con los
                // suyos.
                if (!JondoFixMod.IsJondoAdministrator) continue;
                try
                {
                    if (!item.isSaleable)
                    {
                        item.isSaleable = true;
                        madeSaleable++;
                    }
                }
                catch { }
            }

            // Las listas que el cliente ya hubiera cacheado antes de tocar las marcas.
            if (JondoFixMod.IsJondoAdministrator)
            {
                try { AbstractItemFilter.s_queriedLists?.Clear(); } catch { }
                try { AbstractItemFilter.s_typesToReturn?.Clear(); } catch { }
            }
            itemMappingsLoadedFromClient = true;
            MelonLogger.Msg($"[JondoFix] Loaded {added} item mappings; exposed {madeSaleable} hidden items.");
        }

        public override void OnInitializeMelon()
        {
            LoadItemNames();
            UseLocalRedirect = IsEmulatorActive();
            IsJondoAdministrator = UseLocalRedirect &&
                int.TryParse(Environment.GetEnvironmentVariable("JONDO_ACCOUNT_ROLE"), out int role) &&
                role >= RolAdministrador;
            LoggerInstance.Msg("====================================================");
            LoggerInstance.Msg("  JONDO REDIRECTOR & FIX");
            LoggerInstance.Msg($"  Version: 1.3.4");
            LoggerInstance.Msg($"  Local Emulator Active? {UseLocalRedirect}");
            LoggerInstance.Msg($"  Jondo Administrator? {IsJondoAdministrator}");
            if (UseLocalRedirect)
            {
                LoggerInstance.Msg("  [+] DNS and Socket redirection is ACTIVE");
            }
            else
            {
                LoggerInstance.Msg("  [-] Redirector is INACTIVE (Official servers bypass)");
            }
            LoggerInstance.Msg("====================================================");

            LoggerInstance.Msg($"[JondoFix Env] ZAAP_PORT = {Environment.GetEnvironmentVariable("ZAAP_PORT")}");
            LoggerInstance.Msg($"[JondoFix Env] ZAAP_HASH = {Environment.GetEnvironmentVariable("ZAAP_HASH")}");
            LoggerInstance.Msg($"[JondoFix Env] ZAAP_GAME = {Environment.GetEnvironmentVariable("ZAAP_GAME")}");
            LoggerInstance.Msg($"[JondoFix Env] ZAAP_RELEASE = {Environment.GetEnvironmentVariable("ZAAP_RELEASE")}");
            LoggerInstance.Msg($"[JondoFix Env] ZAAP_INSTANCE_ID = {Environment.GetEnvironmentVariable("ZAAP_INSTANCE_ID")}");
            LoggerInstance.Msg($"[JondoFix Env] ZAAP_CAN_AUTH = {Environment.GetEnvironmentVariable("ZAAP_CAN_AUTH")}");
            
            if (UseLocalRedirect)
            {
                try
                {
                    // Initialize IL2CPP SSL/TLS Validation Callbacks
                    try
                    {
                        var myCsharpDelegate = new Func<Il2CppSystem.Object, Il2CppSystem.Security.Cryptography.X509Certificates.X509Certificate, Il2CppSystem.Security.Cryptography.X509Certificates.X509Chain, Il2CppSystem.Net.Security.SslPolicyErrors, bool>(
                            (sender, certificate, chain, sslPolicyErrors) => {
                                MelonLogger.Msg("[JondoFix] IL2CPP SSL validation callback hit! Returning true.");
                                return true;
                            }
                        );
                        BypassedCallback = Il2CppInterop.Runtime.DelegateSupport.ConvertDelegate<Il2CppSystem.Net.Security.RemoteCertificateValidationCallback>(myCsharpDelegate);
                        LoggerInstance.Msg("  [+] IL2CPP SSL/TLS Validation Callback registered successfully!");
                    }
                    catch (Exception ex)
                    {
                        LoggerInstance.Error($"  [-] Failed to register IL2CPP SSL/TLS Validation Callback: {ex.Message}");
                    }

                    try
                    {
                        var myMonoDelegate = new Func<string, Il2CppSystem.Security.Cryptography.X509Certificates.X509Certificate, Il2CppSystem.Security.Cryptography.X509Certificates.X509Chain, Il2CppMono.Security.Interface.MonoSslPolicyErrors, bool>(
                            (targetHost, certificate, chain, sslPolicyErrors) => {
                                MelonLogger.Msg("[JondoFix] IL2CPP Mono SSL validation callback hit! Returning true.");
                                return true;
                            }
                        );
                        BypassedMonoCallback = Il2CppInterop.Runtime.DelegateSupport.ConvertDelegate<Il2CppMono.Security.Interface.MonoRemoteCertificateValidationCallback>(myMonoDelegate);
                        LoggerInstance.Msg("  [+] IL2CPP Mono SSL/TLS Validation Callback registered successfully!");
                    }
                    catch (Exception ex)
                    {
                        LoggerInstance.Error($"  [-] Failed to register IL2CPP Mono SSL/TLS Validation Callback: {ex.Message}");
                    }
                }
                catch (Exception ex)
                {
                    LoggerInstance.Error($"[JondoFix] Failed to register manual patches for SpinProtocol: {ex.Message}");
                }
            }
        }

        public static void BypassSslStreamInstance(Il2CppSystem.Net.Security.SslStream stream)
        {
            if (stream == null) return;
            try
            {
                // 1. Force the standard validationCallback
                stream.validationCallback = BypassedCallback;
                
                // 2. Force the settings object fields using direct type property setters
                var settings = stream.settings;
                if (settings == null)
                {
                    settings = new Il2CppMono.Security.Interface.MonoTlsSettings();
                    stream.settings = settings;
                }

                if (settings != null)
                {
                    settings.UseServicePointManagerCallback = new Il2CppSystem.Nullable<bool>(true);
                    if (BypassedMonoCallback != null)
                    {
                        settings.RemoteCertificateValidationCallback = BypassedMonoCallback;
                    }
                    MelonLogger.Msg("[JondoFix] Set settings.UseServicePointManagerCallback to true and RemoteCertificateValidationCallback successfully!");
                }
            }
            catch (Exception ex)
            {
                MelonLogger.Error($"[JondoFix] Error in BypassSslStreamInstance: {ex.Message}");
            }
        }

        private static void SslStreamCtorPostfix(Il2CppSystem.Net.Security.SslStream __instance)
        {
            try
            {
                MelonLogger.Msg("[JondoFix] SslStream ctor hit via dynamic patch! Injecting bypass.");
                BypassSslStreamInstance(__instance);
            }
            catch (Exception ex)
            {
                MelonLogger.Error($"[JondoFix] Error in SslStreamCtorPostfix: {ex.Message}");
            }
        }

        public override void OnLateInitializeMelon()
        {
            if (!UseLocalRedirect) return;

            LoggerInstance.Msg("[JondoFix] Late initialization starting...");
            try
            {
                var harmony = new HarmonyLib.Harmony("com.jondo.fix.late");
                
                Type eudType = null;
                try
                {
                    eudType = System.AppDomain.CurrentDomain.GetAssemblies()
                        .SelectMany(a => { try { return a.GetTypes(); } catch { return new Type[0]; } })
                        .FirstOrDefault(t => t.Name == "eud" || t.Name == "CartographyManager");
                }
                catch { }

                if (eudType != null)
                {
                    var bcnnMethod = eudType.GetMethods().FirstOrDefault(m => m.Name == "bcnn");
                    if (bcnnMethod != null)
                    {
                        var prefix = typeof(EudBcnnPatch).GetMethod("Prefix", System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static);
                        var finalizer = typeof(EudBcnnPatch).GetMethod("Finalizer", System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static);
                        harmony.Patch(bcnnMethod, prefix: new HarmonyMethod(prefix), finalizer: new HarmonyMethod(finalizer));
                        LoggerInstance.Msg("[JondoFix] Successfully applied dynamic prefix and finalizer patches to CartographyManager.bcnn!");
                    }

                    var bckuMethod = eudType.GetMethod("bcku", new Type[] { });
                    if (bckuMethod != null)
                    {
                        var prefix = typeof(EudBckuPatch).GetMethod("Prefix", System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static);
                        var finalizer = typeof(EudBckuPatch).GetMethod("Finalizer", System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static);
                        harmony.Patch(bckuMethod, prefix: new HarmonyMethod(prefix), finalizer: new HarmonyMethod(finalizer));
                        LoggerInstance.Msg("[JondoFix] Successfully applied dynamic prefix and finalizer patches to CartographyManager.bcku!");
                    }

                    var bckpMethod = eudType.GetMethods().FirstOrDefault(m => m.Name == "bckp");
                    if (bckpMethod != null)
                    {
                        var prefix = typeof(EudBckpPatch).GetMethod("Prefix", System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static);
                        harmony.Patch(bckpMethod, prefix: new HarmonyMethod(prefix));
                        LoggerInstance.Msg("[JondoFix] Successfully applied dynamic prefix patch to CartographyManager.bckp!");
                    }

                    var bcohMethod = eudType.GetMethods().FirstOrDefault(m => m.Name == "bcoh");
                    if (bcohMethod != null)
                    {
                        var prefix = typeof(EudBcohPatch).GetMethod("Prefix", System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static);
                        harmony.Patch(bcohMethod, prefix: new HarmonyMethod(prefix));
                        LoggerInstance.Msg("[JondoFix] Successfully applied dynamic prefix patch to CartographyManager.bcoh!");
                    }
                }

                // Dynamically patch SslStream constructors to bypass SSL validations
                try
                {
                    LoggerInstance.Msg("[JondoFix] Dynamically patching SslStream constructors...");
                    var sslStreamType = typeof(Il2CppSystem.Net.Security.SslStream);
                    var ctors = sslStreamType.GetConstructors(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance);
                    int patchedCount = 0;
                    foreach (var ctor in ctors)
                    {
                        var parameters = ctor.GetParameters();
                        // Skip the IntPtr pointer constructor
                        if (parameters.Length == 1 && parameters[0].ParameterType == typeof(IntPtr))
                            continue;

                        var postfixMethod = typeof(JondoFixMod).GetMethod(nameof(SslStreamCtorPostfix), System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static);
                        if (postfixMethod != null)
                        {
                            harmony.Patch(ctor, postfix: new HarmonyMethod(postfixMethod));
                            patchedCount++;
                        }
                    }
                    LoggerInstance.Msg($"[JondoFix] Successfully dynamically patched {patchedCount} SslStream constructors!");
                }
                catch (Exception ex)
                {
                    LoggerInstance.Error($"[JondoFix] Failed to dynamically patch SslStream constructors: {ex.Message}");
                }

                // Dynamically patch SpinProtocol.CheckAuthentication
                try
                {
                    LoggerInstance.Msg("[JondoFix] Dynamically patching SpinProtocol.CheckAuthentication...");
                    var spinProtocolType = typeof(Il2CppAnkama.SpinConnection.SpinProtocol);
                    var checkAuthMethods = spinProtocolType.GetMethods(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Static)
                        .Where(m => m.Name == "CheckAuthentication").ToList();
                    
                    int patchedCount = 0;
                    foreach (var method in checkAuthMethods)
                    {
                        var prefixMethod = typeof(SpinProtocolCheckAuthenticationPatch).GetMethod(nameof(SpinProtocolCheckAuthenticationPatch.Prefix), System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static);
                        if (prefixMethod != null)
                        {
                            harmony.Patch(method, prefix: new HarmonyMethod(prefixMethod));
                            patchedCount++;
                        }
                    }
                    LoggerInstance.Msg($"[JondoFix] Successfully dynamically patched {patchedCount} CheckAuthentication overloads!");
                }
                catch (Exception ex)
                {
                    LoggerInstance.Error($"[JondoFix] Failed to dynamically patch SpinProtocol.CheckAuthentication: {ex.Message}");
                }

            }
            catch (Exception ex)
            {
                LoggerInstance.Error($"[JondoFix] Error applying late Harmony patches: {ex}");
            }

            // Global ServicePointManager bypass
            try
            {
                System.Net.ServicePointManager.ServerCertificateValidationCallback = (sender, certificate, chain, sslPolicyErrors) => true;
                LoggerInstance.Msg("[JondoFix] Global managed ServicePointManager callback set to always return true!");
            }
            catch (Exception ex)
            {
                LoggerInstance.Error($"[JondoFix] Failed to set global managed ServicePointManager: {ex.Message}");
            }
        }

        public override void OnUpdate()
        {
            if (UseLocalRedirect && !itemMappingsLoadedFromClient)
            {
                try
                {
                    LoadItemNamesFromClientData();
                }
                catch (Exception)
                {
                    // Data roots are unavailable during the first loading frames. Retry later.
                }
            }

            if (UseLocalRedirect && !hasDumped)
            {
                try
                {
                    if (Il2CppCore.DataCenter.DataCenterModule.mapsCoordinatesDataRoot != null && 
                        Il2CppCore.DataCenter.DataCenterModule.mapScrollActionsDataRoot != null && 
                        Il2CppCore.DataCenter.DataCenterModule.mapsInformationDataRoot != null)
                    {
                        var coords = Il2CppCore.DataCenter.DataCenterModule.mapsCoordinatesDataRoot.GetObjects();
                        var scrolls = Il2CppCore.DataCenter.DataCenterModule.mapScrollActionsDataRoot.GetObjects();
                        var infos = Il2CppCore.DataCenter.DataCenterModule.mapsInformationDataRoot.GetObjects();

                        if (coords != null && coords.Count > 0 && 
                            scrolls != null && scrolls.Count > 0 && 
                            infos != null && infos.Count > 0)
                        {
                            hasDumped = true;
                            LoggerInstance.Msg("[JondoFix] Metadata loaded in memory. Checking if dump is needed...");
                            
                            bool forceDump = false;
                            bool filesExist = File.Exists(DataFile("map_dump_coordinates.csv")) && 
                                              File.Exists(DataFile("map_dump_scrolls.csv")) && 
                                              File.Exists(DataFile("map_dump_infos.csv"));

                            if (!filesExist || forceDump)
                            {
                                LoggerInstance.Msg("[JondoFix] CSV files not found. Starting metadata dump...");
                                DumpMetadata(coords, scrolls, infos);
                            }
                            else
                            {
                                LoggerInstance.Msg("[JondoFix] Map metadata files already exist on disk. Skipping dump.");
                            }
                        }
                    }
                }
                catch (Exception)
                {
                    // Ignore exceptions during early initialization frames when roots are not yet ready
                }
            }
        }

        private static void DumpMetadata(
            Il2CppSystem.Collections.Generic.List<Il2CppCore.DataCenter.Metadata.World.MapsCoordinateData> coords,
            Il2CppSystem.Collections.Generic.List<Il2CppCore.DataCenter.Metadata.World.MapScrollActionData> scrolls,
            Il2CppSystem.Collections.Generic.List<Il2CppCore.DataCenter.Metadata.World.MapInformationData> infos)
        {
            try
            {
                MelonLogger.Msg($"[JondoFix] Coords count: {coords.Count}");
                MelonLogger.Msg($"[JondoFix] Scrolls count: {scrolls.Count}");
                MelonLogger.Msg($"[JondoFix] Infos count: {infos.Count}");

                // Create C:\Jondo directory if it doesn't exist
                Directory.CreateDirectory(EmulatorRoot);

                // 1. Dump Coordinates
                using (var writer = new StreamWriter(DataFile("map_dump_coordinates.csv")))
                {
                    writer.WriteLine("compressedCoords,x,y,mapIds");
                    for (int i = 0; i < coords.Count; i++)
                    {
                        var item = coords[i];
                        var sb = new System.Text.StringBuilder();
                        if (item.mapIds != null)
                        {
                            for (int j = 0; j < item.mapIds.Count; j++)
                            {
                                if (j > 0) sb.Append(";");
                                sb.Append(item.mapIds[j]);
                            }
                        }
                        writer.WriteLine($"{item.compressedCoords},{item.x},{item.y},{sb.ToString()}");
                    }
                }
                MelonLogger.Msg("[JondoFix] Wrote map_dump_coordinates.csv successfully.");

                // 2. Dump Scrolls
                using (var writer = new StreamWriter(DataFile("map_dump_scrolls.csv")))
                {
                    writer.WriteLine("mapId,rightMapId,bottomMapId,leftMapId,topMapId");
                    for (int i = 0; i < scrolls.Count; i++)
                    {
                        var item = scrolls[i];
                        writer.WriteLine($"{item.id},{item.rightMapId},{item.bottomMapId},{item.leftMapId},{item.topMapId}");
                    }
                }
                MelonLogger.Msg("[JondoFix] Wrote map_dump_scrolls.csv successfully.");

                // 3. Dump Infos
                using (var writer = new StreamWriter(DataFile("map_dump_infos.csv")))
                {
                    writer.WriteLine("mapId,posX,posY,subAreaId,outdoor,name");
                    for (int i = 0; i < infos.Count; i++)
                    {
                        var item = infos[i];
                        string cleanName = item.name != null ? item.name.Replace(",", " ").Replace("\n", " ").Replace("\r", " ") : "";
                        writer.WriteLine($"{item.id},{item.posX},{item.posY},{item.subAreaId},{item.outdoor},{cleanName}");
                    }
                }
                MelonLogger.Msg("[JondoFix] Wrote map_dump_infos.csv successfully.");
                MelonLogger.Msg("[JondoFix] ALL METADATA DUMPED SUCCESSFULLY!");
            }
            catch (Exception ex)
            {
                MelonLogger.Msg($"[JondoFix] Error during metadata dump: {ex}");
            }
        }

        private static bool IsEmulatorActive()
        {
            try
            {
                using (var tcp = new System.Net.Sockets.TcpClient())
                {
                    var ar = tcp.BeginConnect("127.0.0.1", 8888, null, null);
                    if (ar.AsyncWaitHandle.WaitOne(100)) // 100ms timeout
                    {
                        tcp.EndConnect(ar);
                        return true;
                    }
                }
            }
            catch {}
            return false;
        }
    }

    // --- MANAGED HTTP / URI PATCHES ---

    [HarmonyPatch(typeof(System.Uri), MethodType.Constructor, typeof(string))]
    public class UriPatch
    {
        public static void Prefix(ref string uriString)
        {
            if (JondoFixMod.UseLocalRedirect && uriString != null)
            {
                if (uriString.Contains("haapi.ankama.com") || uriString.Contains("haapi.ankama.corp"))
                {
                    uriString = uriString.Replace("https://haapi.ankama.com", "http://127.0.0.1:8888")
                                         .Replace("https://haapi.ankama.corp", "http://127.0.0.1:8888");
                    MelonLogger.Msg($"[JondoFix] Redirected HAAPI URI to: {uriString}");
                }
            }
        }
    }

    [HarmonyPatch(typeof(System.Net.Http.HttpClient), "SendAsync", new[] { typeof(System.Net.Http.HttpRequestMessage), typeof(System.Threading.CancellationToken) })]
    public class HttpClientSendAsyncPatch
    {
        public static void Prefix(System.Net.Http.HttpRequestMessage request)
        {
            if (JondoFixMod.UseLocalRedirect && request?.RequestUri != null)
            {
                var uri = request.RequestUri;
                if (uri.Host.Contains("haapi.ankama.corp") || uri.Host.Contains("haapi.ankama"))
                {
                    var newUri = new Uri("http://127.0.0.1:8888" + uri.PathAndQuery);
                    MelonLogger.Msg($"[JondoFix HAAPI REDIRECT] {uri} -> {newUri}");
                    request.RequestUri = newUri;
                    request.Headers.Remove("Host");
                }
            }
        }
    }

    // --- IL2CPP NATIVE SOCKET PATCHES ---

    [HarmonyPatch(typeof(Il2CppSystem.Net.Sockets.Socket), nameof(Il2CppSystem.Net.Sockets.Socket.Connect), typeof(Il2CppSystem.Net.IPAddress), typeof(int))]
    public class SocketConnectIPPatch
    {
        public static void Prefix(ref Il2CppSystem.Net.IPAddress address, ref int port)
        {
            if (JondoFixMod.UseLocalRedirect && address != null)
            {
                string ipStr = address.ToString();
                MelonLogger.Msg($"[JondoFix] Socket connecting to IP: {ipStr}:{port}");
                if (port == 5555 || port == 443)
                {
                    if (ipStr != "127.0.0.1" && ipStr != "::1")
                    {
                        MelonLogger.Msg($"[JondoFix] Redirecting IP Game Server to Localhost:5555!");
                        address = Il2CppSystem.Net.IPAddress.Parse("127.0.0.1");
                        port = 5555;
                    }
                }
            }
        }
    }

    [HarmonyPatch(typeof(Il2CppSystem.Net.Sockets.Socket), nameof(Il2CppSystem.Net.Sockets.Socket.Connect), typeof(Il2CppSystem.Net.EndPoint))]
    public class SocketConnectEPPatch
    {
        public static void Prefix(ref Il2CppSystem.Net.EndPoint remoteEP)
        {
            if (JondoFixMod.UseLocalRedirect && remoteEP != null)
            {
                string epStr = remoteEP.ToString();
                MelonLogger.Msg($"[JondoFix] Socket connecting to EndPoint: {epStr}");
                if (epStr.Contains("ankama") || epStr.Contains("34.247.205") || epStr.Contains("54.75.207") || epStr.Contains(":5555") || epStr.Contains(":443"))
                {
                    MelonLogger.Msg($"[JondoFix] Redirecting Socket EndPoint to Localhost:5555!");
                    remoteEP = new Il2CppSystem.Net.IPEndPoint(Il2CppSystem.Net.IPAddress.Parse("127.0.0.1"), 5555);
                }
            }
        }
    }

    [HarmonyPatch(typeof(Il2CppSystem.Net.Sockets.Socket), nameof(Il2CppSystem.Net.Sockets.Socket.ConnectAsync), typeof(Il2CppSystem.Net.Sockets.SocketAsyncEventArgs))]
    public class SocketConnectAsyncEventArgsPatch
    {
        public static void Prefix(Il2CppSystem.Net.Sockets.SocketAsyncEventArgs e)
        {
            if (JondoFixMod.UseLocalRedirect && e != null && e.RemoteEndPoint != null)
            {
                string epStr = e.RemoteEndPoint.ToString();
                MelonLogger.Msg($"[JondoFix] Socket.ConnectAsync(SocketAsyncEventArgs) to: {epStr}");
                if (epStr.Contains("ankama") || epStr.Contains("34.247.205") || epStr.Contains("54.75.207") || epStr.Contains(":5555") || epStr.Contains(":443"))
                {
                    MelonLogger.Msg($"[JondoFix] Redirecting SocketAsyncEventArgs to Localhost:5555!");
                    e.RemoteEndPoint = new Il2CppSystem.Net.IPEndPoint(Il2CppSystem.Net.IPAddress.Parse("127.0.0.1"), 5555);
                }
            }
        }
    }

    // --- IL2CPP TCPCLIENT PATCHES (USED BY SPIN NETWORK LAYER) ---

    [HarmonyPatch(typeof(Il2CppSystem.Net.Sockets.TcpClient), nameof(Il2CppSystem.Net.Sockets.TcpClient.Connect), typeof(string), typeof(int))]
    public class TcpClientConnectStringPatch
    {
        public static void Prefix(ref string hostname, ref int port)
        {
            if (JondoFixMod.UseLocalRedirect)
            {
                MelonLogger.Msg($"[JondoFix] TcpClient connecting to: {hostname}:{port}");
                if (hostname != null && (hostname.Contains("ankama") || port == 5555 || port == 443))
                {
                    MelonLogger.Msg($"[JondoFix] Redirecting TcpClient to Localhost:5555!");
                    hostname = "127.0.0.1";
                    port = 5555;
                }
            }
        }
    }

    [HarmonyPatch(typeof(Il2CppSystem.Net.Sockets.TcpClient), nameof(Il2CppSystem.Net.Sockets.TcpClient.Connect), typeof(Il2CppSystem.Net.IPEndPoint))]
    public class TcpClientConnectEPPatch
    {
        public static void Prefix(ref Il2CppSystem.Net.IPEndPoint remoteEP)
        {
            if (JondoFixMod.UseLocalRedirect && remoteEP != null)
            {
                string epStr = remoteEP.ToString();
                MelonLogger.Msg($"[JondoFix] TcpClient connecting to EndPoint: {epStr}");
                if (epStr.Contains("ankama") || epStr.Contains("34.247.205") || epStr.Contains("54.75.207") || remoteEP.Port == 5555 || remoteEP.Port == 443)
                {
                    MelonLogger.Msg($"[JondoFix] Redirecting TcpClient EndPoint to Localhost:5555!");
                    remoteEP = new Il2CppSystem.Net.IPEndPoint(Il2CppSystem.Net.IPAddress.Parse("127.0.0.1"), 5555);
                }
            }
        }
    }

    [HarmonyPatch(typeof(Il2CppSystem.Net.Sockets.TcpClient), nameof(Il2CppSystem.Net.Sockets.TcpClient.ConnectAsync), typeof(string), typeof(int))]
    public class TcpClientConnectAsyncStringPatch
    {
        public static void Prefix(ref string host, ref int port)
        {
            if (JondoFixMod.UseLocalRedirect)
            {
                MelonLogger.Msg($"[JondoFix] TcpClient.ConnectAsync to: {host}:{port}");
                if (host != null && (host.Contains("ankama") || port == 5555 || port == 443))
                {
                    MelonLogger.Msg($"[JondoFix] Redirecting TcpClient.ConnectAsync to Localhost:5555!");
                    host = "127.0.0.1";
                    port = 5555;
                }
            }
        }
    }

    [HarmonyPatch(typeof(Il2CppSystem.Net.Sockets.TcpClient), nameof(Il2CppSystem.Net.Sockets.TcpClient.BeginConnect), typeof(string), typeof(int), typeof(Il2CppSystem.AsyncCallback), typeof(Il2CppSystem.Object))]
    public class TcpClientBeginConnectPatch
    {
        public static void Prefix(ref string host, ref int port)
        {
            if (JondoFixMod.UseLocalRedirect)
            {
                MelonLogger.Msg($"[JondoFix] TcpClient.BeginConnect to: {host}:{port}");
                if (host != null && (host.Contains("ankama") || port == 5555 || port == 443))
                {
                    MelonLogger.Msg($"[JondoFix] Redirecting TcpClient.BeginConnect to Localhost:5555!");
                    host = "127.0.0.1";
                    port = 5555;
                }
            }
        }
    }

    // --- OTHER HELPERS ---

    [HarmonyPatch(typeof(UnityEngine.Networking.UnityWebRequest), "Get", typeof(string))]
    public class UnityWebRequestGetPatch
    {
        public static void Prefix(ref string uri)
        {
            if (JondoFixMod.UseLocalRedirect)
            {
                MelonLogger.Msg($"[JondoFix] UnityWebRequest.Get: {uri}");
                if (uri != null && uri.Contains("dofus3.json"))
                {
                    MelonLogger.Msg($"[JondoFix] Intercepting config download!");
                    uri = "http://127.0.0.1:8888/config/dofus3.json";
                }
            }
        }
    }

    [HarmonyPatch(typeof(UnityEngine.Networking.UnityWebRequest), "Post", typeof(string), typeof(string), typeof(string))]
    public class UnityWebRequestPostPatch
    {
        public static void Prefix(string uri)
        {
            if (JondoFixMod.UseLocalRedirect)
            {
                MelonLogger.Msg($"[JondoFix] UnityWebRequest.Post: {uri}");
            }
        }
    }

    [HarmonyPatch(typeof(UnityEngine.Debug), nameof(UnityEngine.Debug.LogError), typeof(Il2CppSystem.Object))]
    public class LogErrorPatch
    {
        public static void Prefix(Il2CppSystem.Object message)
        {
            if (JondoFixMod.UseLocalRedirect)
            {
                MelonLogger.Msg($"[DofusError] {message}");
            }
        }
    }

    [HarmonyPatch(typeof(UnityEngine.Debug), nameof(UnityEngine.Debug.LogException), typeof(Il2CppSystem.Exception))]
    public class LogExceptionPatch
    {
        public static void Prefix(Il2CppSystem.Exception exception)
        {
            if (JondoFixMod.UseLocalRedirect && exception != null)
            {
                MelonLogger.Msg("[DofusException] ----------------------------------------------------");
                MelonLogger.Msg($"[DofusException] Message: {exception.Message}");
                if (!string.IsNullOrEmpty(exception.StackTrace))
                {
                    MelonLogger.Msg($"[DofusException] StackTrace:\n{exception.StackTrace}");
                }
                if (exception.InnerException != null)
                {
                    MelonLogger.Msg($"[DofusException] InnerException Message: {exception.InnerException.Message}");
                }
                MelonLogger.Msg("[DofusException] ----------------------------------------------------");
            }
        }
    }

    [HarmonyPatch(typeof(ZaapClient), nameof(ZaapClient.Connect), new Type[] { typeof(ZaapClient.ParametersSources) })]
    public class ZaapClientConnectSourcePatch
    {
        public static void Prefix(ZaapClient.ParametersSources source)
        {
            if (JondoFixMod.UseLocalRedirect)
            {
                MelonLogger.Msg($"[JondoFix] ZaapClient.Connect(source: {source})");
            }
        }
    }

    [HarmonyPatch(typeof(ZaapClient), nameof(ZaapClient.Connect), new Type[] { typeof(ZaapClientParameters) })]
    public class ZaapClientConnectParamsPatch
    {
        public static void Prefix(ZaapClientParameters parameters)
        {
            if (JondoFixMod.UseLocalRedirect)
            {
                if (parameters != null)
                {
                    MelonLogger.Msg($"[JondoFix] ZaapClient.Connect(parameters: port={parameters.port}, name={parameters.name}, release={parameters.release}, instanceId={parameters.instanceId}, hash={parameters.hash})");
                }
                else
                {
                    MelonLogger.Msg("[JondoFix] ZaapClient.Connect(parameters: null)");
                }
            }
        }
    }

    [HarmonyPatch(typeof(ZaapClient), nameof(ZaapClient.Connect), new Type[] { typeof(int), typeof(string), typeof(string), typeof(int), typeof(string) })]
    public class ZaapClientConnectExplicitPatch
    {
        public static void Prefix(int port, string name, string release, int instanceId, string hash)
        {
            if (JondoFixMod.UseLocalRedirect)
            {
                MelonLogger.Msg($"[JondoFix] ZaapClient.Connect(explicit: port={port}, name={name}, release={release}, instanceId={instanceId}, hash={hash})");
            }
        }
    }

    [HarmonyPatch(typeof(TNamedPipeClientTransport), MethodType.Constructor, new Type[] { typeof(string) })]
    public class TNamedPipeClientTransportPatch1
    {
        public static void Prefix(ref string pipe)
        {
            if (JondoFixMod.UseLocalRedirect)
            {
                MelonLogger.Msg($"[JondoFix] TNamedPipeClientTransport .ctor(pipe: {pipe})");
            }
        }
    }

    [HarmonyPatch(typeof(TNamedPipeClientTransport), MethodType.Constructor, new Type[] { typeof(string), typeof(string) })]
    public class TNamedPipeClientTransportPatch2
    {
        public static void Prefix(string server, ref string pipe)
        {
            if (JondoFixMod.UseLocalRedirect)
            {
                MelonLogger.Msg($"[JondoFix] TNamedPipeClientTransport .ctor(server: {server}, pipe: {pipe})");
            }
        }
    }

    // --- CARTOGRAPHY PRISM REFERENCE NULL PATCHES ---

    public class EudBcnnPatch
    {
        public static bool Prefix(Il2Cpp.ku a, bool b)
        {
            if (a == null || a.Pointer == IntPtr.Zero)
            {
                MelonLogger.Msg("[JondoFix] eud (CartographyManager).bcnn called with null or native-null ku (Quest)! Skipping to prevent NullReferenceException crash.");
                return false; // Return false to skip the original method!
            }
            return true; // Return true to run the original method
        }

        public static Exception Finalizer(Exception __exception)
        {
            if (__exception != null)
            {
                MelonLogger.Msg($"[JondoFix] Suppressed exception in eud (CartographyManager).bcnn: {__exception.Message}");
                return null; // Suppress the exception!
            }
            return null;
        }
    }

    public class EudBckuPatch
    {
        public static bool Prefix(Il2CppInterop.Runtime.InteropTypes.Il2CppObjectBase __instance)
        {
            if (__instance == null || __instance.Pointer == IntPtr.Zero) return true;
            return true;
        }

        public static Exception Finalizer(Exception __exception)
        {
            if (__exception != null)
            {
                MelonLogger.Msg($"[JondoFix] Suppressed exception in CartographyManager.bcku: {__exception.Message}");
                return null;
            }
            return null;
        }
    }

    public class EudBckpPatch
    {
        public static bool Prefix(Il2CppSystem.Collections.Generic.List<int> a)
        {
            if (a == null) return true;
            try
            {
                var subAreasRoot = Il2CppCore.DataCenter.DataCenterModule.subAreasDataRoot;
                if (subAreasRoot == null)
                {
                    MelonLogger.Msg("[JondoFix] eud (CartographyManager).bckp: subAreasDataRoot is null. Skipping filtering.");
                    return true;
                }

                for (int i = a.Count - 1; i >= 0; i--)
                {
                    int subAreaId = a[i];
                    var subArea = subAreasRoot.GetSubAreaById(subAreaId);
                    if (subArea == null || subArea.Pointer == IntPtr.Zero)
                    {
                        MelonLogger.Msg($"[JondoFix] eud (CartographyManager).bckp: Removed invalid/null subarea ID {subAreaId} at index {i} to prevent async crash.");
                        a.RemoveAt(i);
                    }
                }
            }
            catch (Exception ex)
            {
                MelonLogger.Msg($"[JondoFix] Error filtering list in eud (CartographyManager).bckp: {ex.Message}");
            }
            return true;
        }
    }

    public class SpinProtocolCheckAuthenticationPatch
    {
        public static bool Prefix(out Il2CppAnkama.SpinConnection.SpinProtocol.ConnectionErrors optConnError, ref bool __result)
        {
            MelonLogger.Msg("[JondoFix] SpinProtocol.CheckAuthentication Prefix hit! Forcing success.");
            optConnError = Il2CppAnkama.SpinConnection.SpinProtocol.ConnectionErrors.NoneOrOtherOrUnknown;
            __result = true;
            return false; // Skip original validation method
        }
    }


    [HarmonyPatch(typeof(Il2CppSystem.Net.Security.SslStream), "SetAndVerifyValidationCallback")]
    public class SslStreamSetAndVerifyValidationCallbackPatch
    { 
        public static void Prefix(Il2CppSystem.Net.Security.SslStream __instance, ref Il2CppSystem.Net.Security.RemoteCertificateValidationCallback callback)
        {
            try
            {
                MelonLogger.Msg("[JondoFix] SslStream.SetAndVerifyValidationCallback Prefix hit! Forcing bypassed callbacks.");
                callback = JondoFixMod.BypassedCallback;
                JondoFixMod.BypassSslStreamInstance(__instance);
            }
            catch (Exception ex)
            {
                MelonLogger.Error($"[JondoFix] Error in SetAndVerifyValidationCallback Prefix: {ex.Message}");
            }
        }
    }

    [HarmonyPatch(typeof(Il2CppSystem.Net.Security.SslStream), nameof(Il2CppSystem.Net.Security.SslStream.AuthenticateAsClient), new Type[] { typeof(string), typeof(Il2CppSystem.Security.Cryptography.X509Certificates.X509CertificateCollection), typeof(Il2CppSystem.Security.Authentication.SslProtocols), typeof(bool) })]
    public class SslStreamAuthenticateAsClientPatch
    {
        public static void Prefix(Il2CppSystem.Net.Security.SslStream __instance)
        {
            try
            {
                MelonLogger.Msg("[JondoFix] SslStream.AuthenticateAsClient Prefix hit! Bypassing stream.");
                JondoFixMod.BypassSslStreamInstance(__instance);
            }
            catch (Exception ex)
            {
                MelonLogger.Error($"[JondoFix] Failed in AuthenticateAsClient Prefix: {ex.Message}");
            }
        }
    }

    [HarmonyPatch(typeof(Il2CppSystem.Net.Security.SslStream), nameof(Il2CppSystem.Net.Security.SslStream.BeginAuthenticateAsClient), new Type[] { typeof(string), typeof(Il2CppSystem.Security.Cryptography.X509Certificates.X509CertificateCollection), typeof(Il2CppSystem.Security.Authentication.SslProtocols), typeof(bool), typeof(Il2CppSystem.AsyncCallback), typeof(Il2CppSystem.Object) })]
    public class SslStreamBeginAuthenticateAsClientPatch
    {
        public static void Prefix(Il2CppSystem.Net.Security.SslStream __instance)
        {
            try
            {
                MelonLogger.Msg("[JondoFix] SslStream.BeginAuthenticateAsClient Prefix hit! Bypassing stream.");
                JondoFixMod.BypassSslStreamInstance(__instance);
            }
            catch (Exception ex)
            {
                MelonLogger.Error($"[JondoFix] Failed in BeginAuthenticateAsClient Prefix: {ex.Message}");
            }
        }
    }

    [HarmonyPatch(typeof(Il2CppSystem.Net.Security.SslStream), nameof(Il2CppSystem.Net.Security.SslStream.AuthenticateAsClientAsync), new Type[] { typeof(string), typeof(Il2CppSystem.Security.Cryptography.X509Certificates.X509CertificateCollection), typeof(Il2CppSystem.Security.Authentication.SslProtocols), typeof(bool) })]
    public class SslStreamAuthenticateAsClientAsyncPatch
    {
        public static void Prefix(Il2CppSystem.Net.Security.SslStream __instance)
        {
            try
            {
                MelonLogger.Msg("[JondoFix] SslStream.AuthenticateAsClientAsync Prefix hit! Bypassing stream.");
                JondoFixMod.BypassSslStreamInstance(__instance);
            }
            catch (Exception ex)
            {
                MelonLogger.Error($"[JondoFix] Failed in AuthenticateAsClientAsync Prefix: {ex.Message}");
            }
        }
    }

    public class EudBcohPatch
    {
        public static bool Prefix(Il2CppInterop.Runtime.InteropTypes.Il2CppObjectBase __instance, Il2CppSystem.Object a)
        {
            MelonLogger.Msg("[JondoFix] CartographyManager.bcoh called. Skipping execution to prevent NullReferenceException crash.");
            return false; // Skip the original method completely!
        }
    }

    // Dynamic constructor patching replaced static SslStreamCtorPatches

    [HarmonyPatch(typeof(Il2CppSystem.Net.ServicePointManager), "get_ServerCertificateValidationCallback")]
    public class ServicePointManagerGetServerCertificateValidationCallbackPatch
    {
        public static bool Prefix(ref Il2CppSystem.Net.Security.RemoteCertificateValidationCallback __result)
        {
            __result = JondoFixMod.BypassedCallback;
            return false; // Skip original getter
        }
    }

    /// <summary>
    /// ItemData.name is the value consumed by encyclopedia rows. Patching the generic localization
    /// accessor was insufficient because this client caches many item names through another path.
    /// </summary>
    [HarmonyPatch(typeof(ItemData), "get_name")]
    public class AdminItemNameIdPatch
    {
        private static bool hasLoggedNamePatch = false;

        public static void Postfix(ItemData __instance, ref string __result)
        {
            try
            {
                if (JondoFixMod.IsJondoAdministrator && __instance != null && !string.IsNullOrEmpty(__result))
                {
                    string suffix = $" [{__instance.id}]";
                    if (!__result.EndsWith(suffix, StringComparison.Ordinal))
                    {
                        __result += suffix;
                        if (!hasLoggedNamePatch)
                        {
                            hasLoggedNamePatch = true;
                            MelonLogger.Msg($"[JondoFix] Admin item id display active: {__result}");
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                MelonLogger.Error($"[JondoFix] Error in ItemData.name Postfix: {ex.Message}");
            }
        }
    }

    /// <summary>
    /// The official administration selector deliberately drops internal/hidden entries. On the
    /// local emulator administrators need the complete client catalogue (combat pets included),
    /// because its selected id is precisely what commands such as .item consume.
    /// </summary>
    [HarmonyPatch(typeof(AdminSelectItemUI), nameof(AdminSelectItemUI.ShouldSkipItem))]
    public class AdminSelectItemShowEverythingPatch
    {
        public static bool Prefix(ref bool __result)
        {
            if (!JondoFixMod.IsJondoAdministrator) return true;

            __result = false;
            return false;
        }
    }
}

