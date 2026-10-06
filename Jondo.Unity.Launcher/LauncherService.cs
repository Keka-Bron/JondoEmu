using System;
using System.Collections.Generic;
using System.IO;
using Jondo.Unity.Launcher.Network;

namespace Jondo.Unity.Launcher
{
    /// <summary>
    /// Launcher logic, independent of the user interface.
    ///
    /// It used to live inside <see cref="HaapiServer"/> and could only be reached over HTTP from
    /// the web interface. The native desktop window now calls these methods directly, so the HTTP
    /// route in front of them has been removed: the only thing still speaking HTTP to that server
    /// is the Dofus client, and it does not use any of this.
    /// </summary>
    public static class LauncherService
    {
        /// <summary>Emulator version published in the service status.</summary>
        public const string Version = "3.6.10.10";

        /// <summary>Address used as the origin when the request comes from this very machine.</summary>
        public const string LocalIp = "127.0.0.1";

        // ─── Result types ───────────────────────────────────────────────────────

        /// <summary>Generic result of a launcher operation.</summary>
        public class Result
        {
            public bool Success { get; set; }
            public string Message { get; set; } = "";
        }

        /// <summary>Result of a successful login.</summary>
        public sealed class SignInResult : Result
        {
            public string Token { get; set; } = "";
            public string Nickname { get; set; } = "";
            public long AccountId { get; set; }
        }

        /// <summary>Status of the emulation services.</summary>
        public sealed class ServicesStatus
        {
            public bool Online { get; set; }
            public bool DatabaseOk { get; set; }
            public bool ServicesListening { get; set; }
            public string Version { get; set; } = LauncherService.Version;
        }

        // ─── Operations ─────────────────────────────────────────────────────────

        /// <summary>
        /// Validates an account's credentials and generates a launcher token. There is
        /// deliberately no process-wide "active account".
        /// </summary>
        public static SignInResult SignIn(string username, string password, string clientIp)
        {
            var respuesta = ControlClient.Pedir("entrar", new
            {
                usuario = username,
                clave = password,
                ip = clientIp.Length > 0 ? clientIp : LocalIp,
            });

            var cuerpo = respuesta.Cuerpo();
            if (cuerpo == null)
            {
                return new SignInResult { Success = false, Message = MensajeDeSilencio(respuesta) };
            }

            if (!cuerpo.Value.GetProperty("bien").GetBoolean())
            {
                return new SignInResult
                {
                    Success = false,
                    Message = cuerpo.Value.TryGetProperty("motivo", out var m) ? (m.GetString() ?? "") : "",
                };
            }

            // The session stays set for everything that comes after: it is what says who asks
            // for things, and what the server takes the role from.
            Network.ControlClient.Token = cuerpo.Value.GetProperty("token").GetString() ?? "";
            EsAdministrador = cuerpo.Value.TryGetProperty("rol", out var rolDicho) && rolDicho.GetInt32() >= Roles.Administrador;

            return new SignInResult
            {
                Success = true,
                Token = cuerpo.Value.GetProperty("token").GetString() ?? "",
                Nickname = cuerpo.Value.GetProperty("apodo").GetString() ?? "",
                AccountId = cuerpo.Value.GetProperty("cuenta").GetInt64(),
            };
        }

        /// <summary>
        /// Logs in with a voucher from the website instead of with username and password.
        /// </summary>
        /// <remarks>
        /// It is the other half of <see cref="Security.OAuthFlow"/>: the website says whoever logged in
        /// can be trusted and gives a voucher, and the game server is the one that translates that voucher into an account and
        /// returns the usual session credential. From there on everything works the same,
        /// which was the idea: the team, the play button and the rest do not notice which way one
        /// came in.
        ///
        /// <b>This does not work yet, and it is worth saying plainly.</b> The verb <c>entrar-con-vale</c>
        /// is NOT written on the server, and it cannot be: one needs to know who signs the
        /// vouchers and with what key, and that is decided by the website the day it exists. While
        /// <see cref="UI.LauncherPreferences.WebSite"/> stays empty, the launcher never calls here.
        /// What is done and tested is everything on this side: the loopback server, the
        /// PKCE, the state check and the code exchange.
        /// </remarks>
        public static SignInResult SignInWithToken(string accessToken)
        {
            var respuesta = ControlClient.Pedir("entrar-con-vale", new { vale = accessToken });

            var cuerpo = respuesta.Cuerpo();
            if (cuerpo == null)
            {
                return new SignInResult { Success = false, Message = MensajeDeSilencio(respuesta) };
            }

            if (!cuerpo.Value.GetProperty("bien").GetBoolean())
            {
                return new SignInResult
                {
                    Success = false,
                    Message = cuerpo.Value.TryGetProperty("motivo", out var m) ? (m.GetString() ?? "") : "",
                };
            }

            Network.ControlClient.Token = cuerpo.Value.GetProperty("token").GetString() ?? "";
            EsAdministrador = cuerpo.Value.TryGetProperty("rol", out var rolDicho)
                              && rolDicho.GetInt32() >= Roles.Administrador;

            return new SignInResult
            {
                Success = true,
                Token = cuerpo.Value.GetProperty("token").GetString() ?? "",
                Nickname = cuerpo.Value.GetProperty("apodo").GetString() ?? "",
                AccountId = cuerpo.Value.GetProperty("cuenta").GetInt64(),
            };
        }

        /// <summary>
        /// The main screen's working area, without the taskbar.
        /// </summary>
        /// <remarks>
        /// The launcher's window is asked, which is what knows which screen it is on. If
        /// there is no window yet —or no screens to ask, which happens when starting without a
        /// graphical session— 1920 by 1080 is returned, which is the same fallback the
        /// Windows Forms version had.
        /// </remarks>
        private static (int Width, int Height) PantallaDeTrabajo()
        {
            try
            {
                var vida = Avalonia.Application.Current?.ApplicationLifetime
                    as Avalonia.Controls.ApplicationLifetimes.IClassicDesktopStyleApplicationLifetime;
                var ventana = vida?.MainWindow;

                var pantalla = ventana?.Screens?.ScreenFromWindow(ventana)
                               ?? ventana?.Screens?.Primary;
                if (pantalla != null)
                {
                    return (pantalla.WorkingArea.Width, pantalla.WorkingArea.Height);
                }
            }
            catch
            {
                // Asking about the screen cannot stop the game from starting.
            }

            return (1920, 1080);
        }

        /// <summary>
        /// What the user is told when the server has not answered.
        ///
        /// THREE faults are told apart, because each one is fixed differently and naming the wrong
        /// one sends the user round in circles:
        ///
        ///   401  the session is no longer valid: one has to log in again
        ///   403  the secret does not match: it is fixed by restarting the launcher
        ///   none nobody is listening: one has to wait for the server to start
        ///
        /// The 401 came out as «el servidor no responde», which was false and misleading too: the
        /// server answered wonderfully, and what it answered was that the session was dead.
        /// </summary>
        private static string MensajeDeSilencio(Network.ControlClient.Respuesta respuesta)
            => respuesta.Llego && respuesta.Codigo == 401
                ? UI.LauncherPreferences.Textos.SessionExpiredError
                : respuesta.Llego && respuesta.Codigo == 403
                    ? UI.LauncherPreferences.Textos.ControlRechazado
                    : UI.LauncherPreferences.Textos.ServidorSinResponder;

        /// <summary>Creates a new account with its nickname. The server writes it, not the launcher.</summary>
        public static Result RegisterAccount(string username, string password, string nickname, string clientIp)
        {
            var respuesta = ControlClient.Pedir("crear-cuenta", new
            {
                usuario = username,
                clave = password,
                apodo = nickname,
                ip = clientIp.Length > 0 ? clientIp : LocalIp,
            });

            var cuerpo = respuesta.Cuerpo();
            if (cuerpo == null) return new Result { Success = false, Message = MensajeDeSilencio(respuesta) };

            bool bien = cuerpo.Value.GetProperty("bien").GetBoolean();
            return new Result
            {
                Success = bien,
                Message = bien
                    ? UI.LauncherPreferences.Textos.AccountCreated
                    : (cuerpo.Value.TryGetProperty("motivo", out var m) ? (m.GetString() ?? "") : ""),
            };
        }

        /// <summary>
        /// Accepts again a session the launcher had stored from the previous time.
        ///
        /// The server only accepts it if the token is still in the base under that account's name:
        /// the launcher cannot claim to be whoever it fancies.
        /// </summary>
        public static bool RememberSession(long accountId, string token)
        {
            Network.ControlClient.Token = token ?? "";
            var cuerpo = ControlClient.Pedir("recordar-token",
                new { cuenta = accountId, token }).Cuerpo();

            // If the server has NOT answered, nothing is known: this runs in the window's
            // constructor and the server may still be loading maps. It is given the benefit of
            // the doubt and it will be seen on pressing play. The session is only considered dead when the
            // server answers that it is not valid, which is when one really has to log in again.
            if (cuerpo == null) return true;

            return cuerpo.Value.GetProperty("bien").GetBoolean();
        }

        /// <summary>A character of the account, as the server tells it.</summary>
        public sealed class Character
        {
            public long Id { get; init; }
            public string Name { get; init; } = "";
            public int Level { get; init; }
            public int Breed { get; init; }
            public int Sex { get; init; }

            /// <summary>The look string, which is what the launcher draws.</summary>
            public string Look { get; init; } = "";
        }

        /// <summary>
        /// The characters of a stored account.
        /// </summary>
        /// <remarks>
        /// The only thing the launcher cannot take from the client's assets: the look string
        /// is in the database and here there is no database. The portrait is drawn here, with
        /// the client's own bones.
        ///
        /// The server returns those of the TOKEN's account, not those of the number it is sent, so
        /// this is no use for looking at someone else's team.
        /// </remarks>
        public static List<Character> CharactersOf(string token)
        {
            var salida = new List<Character>();

            var cuerpo = ControlClient.Pedir("personajes", new { token }).Cuerpo();
            if (cuerpo == null || !cuerpo.Value.TryGetProperty("personajes", out var lista)) return salida;

            foreach (var uno in lista.EnumerateArray())
            {
                salida.Add(new Character
                {
                    Id = uno.TryGetProperty("id", out var id) ? id.GetInt64() : 0,
                    Name = uno.TryGetProperty("nombre", out var n) ? (n.GetString() ?? "") : "",
                    Level = uno.TryGetProperty("nivel", out var l) ? l.GetInt32() : 1,
                    Breed = uno.TryGetProperty("raza", out var r) ? r.GetInt32() : 0,
                    Sex = uno.TryGetProperty("sexo", out var x) ? x.GetInt32() : 0,
                    Look = uno.TryGetProperty("aspecto", out var a) ? (a.GetString() ?? "") : "",
                });
            }

            return salida;
        }

        /// <summary>Whether whoever logged in is an administrator. COSMETIC: the server decides.</summary>
        public static bool EsAdministrador { get; private set; }

        /// <summary>Asks the server to shut down. It only works if the account is an administrator.</summary>
        public static bool StopServer() => ControlClient.Pedir("apagar").Bien;

        /// <summary>
        /// The code the server sends, said in the launcher's language.
        ///
        /// This is the border: the server says WHAT happened and here it is decided HOW to tell it. Before,
        /// the server built the sentence, and for that it had to read the user's language preferences
        /// from %APPDATA%; a server should not know that a desktop exists.
        /// </summary>
        private static string EnCristiano(string codigo) => codigo switch
        {
            Contract.MotivoSesionCaducada => UI.LauncherPreferences.Textos.SessionExpiredError,
            Contract.MotivoCuentaYaAbierta => UI.LauncherPreferences.Textos.AccountAlreadyRunning,
            Contract.MotivoTopeDeClientes => UI.LauncherPreferences.Textos.MaxClientsError,
            _ => codigo.Length > 0 ? codigo : UI.LauncherPreferences.Textos.GenericError,
        };

        /// <summary>
        /// Starts the Dofus client executable, pointing it at the local emulator.
        /// The token identifies the account that has just logged in; if it is not recognized we
        /// reject the launch instead of silently using another account.
        /// </summary>
        public static Result LaunchClient(string token)
        {
            try
            {
                string clientPath = ResolveClient();
                if (clientPath.Length == 0)
                {
                    return new Result
                    {
                        Success = false,
                        Message = UI.LauncherPreferences.Textos.ClientNotFound
                    };
                }

                // BEFORE anything, that the server is really answering.
                //
                // It is not paranoia: the client mod decides only once, on initialising, whether
                // to redirect to the emulator, and it decides by probing 8888 with 100 ms of patience
                // (JondoFix/Class1.cs:471). If at that instant nobody is there, the client gives NO
                // error: it connects to Ankama's servers. With a single process this could not
                // happen because the services were up before the window existed; now it can,
                // so it is checked.
                if (!ControlClient.ServidorVivo())
                {
                    return new Result
                    {
                        Success = false,
                        Message = UI.LauncherPreferences.Textos.ServidorSinResponder
                    };
                }

                string language = UI.LauncherTexts.Code(UI.LauncherPreferences.Language);

                // The start is handed out by the server: it sets the instanceId and the hash, and it is the one that will
                // check them when the client presents itself to the Zaap. Before, the launcher invented them
                // and recorded them in a dictionary in its own memory; that was the knot
                // that made separating the processes impossible.
                var respuesta = ControlClient.Pedir("lanzamiento", new { token = token ?? "", idioma = language });
                var cuerpo = respuesta.Cuerpo();
                if (cuerpo == null) return new Result { Success = false, Message = MensajeDeSilencio(respuesta) };

                if (!cuerpo.Value.GetProperty("bien").GetBoolean())
                {
                    string motivo = cuerpo.Value.TryGetProperty("motivo", out var m) ? (m.GetString() ?? "") : "";
                    return new Result { Success = false, Message = EnCristiano(motivo) };
                }

                long accountId = cuerpo.Value.GetProperty("cuenta").GetInt64();
                int instanceId = cuerpo.Value.GetProperty("instancia").GetInt32();
                string hash = cuerpo.Value.GetProperty("hash").GetString() ?? "";
                int accountRole = cuerpo.Value.TryGetProperty("rol", out var roleValue)
                    ? roleValue.GetInt32()
                    : Roles.Jugador;

                // Unity is told the size up front. Maximizing afterwards is not enough on its own:
                // the client rebuilds its window when it moves between screens (server choice,
                // character choice, world) and reapplies its own saved resolution, so it drops out
                // of the maximized state and part of the interface ends up off screen.
                //
                // The measure is now given by Avalonia and not System.Windows.Forms.Screen: it was the last thing
                // tying this file to the Windows desktop. The WORKING area is asked for, without
                // the taskbar, which is what WorkingArea did; if there is no screen to
                // ask —started without a graphical session— it falls back to 1920 by 1080, which is what
                // there was before as a fallback.
                var area = PantallaDeTrabajo();

                // The optional HD/4K scenery packs: a flag only for a pack the player turned on
                // AND that is installed and verified for the client's current version.
                string packFlags = Packs.TexturePackService.ForClient(clientPath)
                    .LaunchFlags(UI.LauncherPreferences.PackHd, UI.LauncherPreferences.Pack4k);
                if ((UI.LauncherPreferences.PackHd || UI.LauncherPreferences.Pack4k) && packFlags.Length == 0)
                {
                    Console.WriteLine("[Launcher] A texture pack is turned on but not installed and verified " +
                                      "for this client version; starting without it.");
                }

                string arguments = ClientArguments(area.Width, area.Height, instanceId, hash, language, packFlags);

                // The client's mod, as the emulator ships it.
                InstallMod(clientPath);

                var startInfo = new System.Diagnostics.ProcessStartInfo
                {
                    FileName = clientPath,
                    Arguments = arguments,
                    WorkingDirectory = Path.GetDirectoryName(clientPath) ?? "",
                    UseShellExecute = false
                };

                startInfo.Environment["ZAAP_PORT"] = "15881";
                startInfo.Environment["ZAAP_HASH"] = hash;
                startInfo.Environment["ZAAP_GAME"] = "dofus";
                startInfo.Environment["ZAAP_RELEASE"] = "dofus3";
                startInfo.Environment["ZAAP_INSTANCE_ID"] = instanceId.ToString(System.Globalization.CultureInfo.InvariantCulture);
                startInfo.Environment["ZAAP_CAN_AUTH"] = "true";
                // JondoFix uses this only to decide whether item ids may be shown in the client.
                // It is cosmetic: every administration command is still authorized by the server.
                startInfo.Environment["JONDO_ACCOUNT_ROLE"] = accountRole.ToString(System.Globalization.CultureInfo.InvariantCulture);
                // And, to an administrator only, the token the mod's give-item panel presents to
                // the control API. It is the account's own session, in its own process, and the
                // server still checks token and role on every request.
                if (Roles.AlMenos(accountRole, Roles.Administrador))
                    startInfo.Environment["JONDO_CONTROL_TOKEN"] = token ?? "";

                System.Diagnostics.Process? client;
                try
                {
                    client = System.Diagnostics.Process.Start(startInfo);
                }
                catch
                {
                    _arrancadosAqui.TryRemove(accountId, out _);
                    Devolver(accountId);
                    throw;
                }

                if (client == null)
                {
                    _arrancadosAqui.TryRemove(accountId, out _);
                    Devolver(accountId);
                    return new Result { Success = false, Message = UI.LauncherPreferences.Textos.ClientStartFailed };
                }

                // When the client closes the server has to be told, which is what keeps
                // count of who is playing. And even if this notice gets lost —because the
                // launcher is closed first— the server has two nets underneath: the game session's leaving
                // and the expiry of launches that never got to connect.
                // Recorded before anything, which is what keeps the second click from asking
                // the server for anything.
                _arrancadosAqui[accountId] = 0;

                client.EnableRaisingEvents = true;
                client.Exited += (s, e) =>
                {
                    _arrancadosAqui.TryRemove(accountId, out _);
                    Devolver(accountId);
                };
                MaximizeWhenReady(client);
                Console.WriteLine($"[Launcher] Client {instanceId} launched for account {accountId} (PID {client.Id}).");
                return new Result { Success = true };
            }
            catch (Exception ex)
            {
                return new Result { Success = false, Message = $"Error starting the client: {ex.Message}" };
            }
        }

        /// <summary>The Dofus client's command line.</summary>
        /// <param name="packFlags">From <see cref="Packs.TexturePackService.LaunchFlags"/>; may be empty.</param>
        internal static string ClientArguments(int width, int height, int instanceId, string hash, string language, string packFlags)
        {
            // MelonLoader opens its own black console and its splash screen in front of the
            // game. It is told not to on the command line as well as in Loader.cfg: the command
            // rules over the file, so it does not matter if someone rewrites it.
            string arguments =
                $"-force-d3d11 -screen-fullscreen 0 -screen-width {width} -screen-height {height} " +
                "--melonloader.hideconsole --melonloader.disablestartscreen " +
                $"--port 15881 --gameName dofus --gameRelease dofus3 --instanceId {instanceId} --hash {hash} " +
                $"--canLogin true --langCode {language} " +
                "--autoConnectType 1 --connectionPort 5555";

            // Same place as in Ankama's zaap.yml: after the connection arguments, with no value.
            return packFlags.Length > 0 ? arguments + " " + packFlags : arguments;
        }

        /// <summary>
        /// The emulator's JondoFix into the client's Mods, when it differs from the one there.
        /// </summary>
        /// <remarks>
        /// What the mod changes in the client -- the Koliseo window with the JondoBots' card, among
        /// the rest -- comes with the emulator, not with a new client: whoever has a client with
        /// MelonLoader gets it at the next launch. A client without MelonLoader is left alone. A
        /// client already open holds the file; it is then updated at the next launch.
        /// </remarks>
        internal static void InstallMod(string clientPath)
        {
            try
            {
                string shipped = Path.Combine(Paths.Root, "JondoFix", "JondoFix.dll");
                string clientDir = Path.GetDirectoryName(clientPath) ?? "";
                if (!File.Exists(shipped) || !Directory.Exists(Path.Combine(clientDir, "MelonLoader"))) return;

                string mods = Path.Combine(clientDir, "Mods");
                string installed = Path.Combine(mods, "JondoFix.dll");
                if (File.Exists(installed) && SameContent(installed, shipped)) return;

                Directory.CreateDirectory(mods);
                File.Copy(shipped, installed, overwrite: true);
                Console.WriteLine($"[Launcher] JondoFix updated in {mods}.");
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
            {
                Console.WriteLine($"[Launcher] JondoFix could not be updated now (is a client open?): {ex.Message}");
            }
        }

        internal static bool SameContent(string a, string b)
        {
            var infoA = new FileInfo(a);
            var infoB = new FileInfo(b);
            if (infoA.Length != infoB.Length) return false;
            using var sha = System.Security.Cryptography.SHA256.Create();
            using var streamA = File.OpenRead(a);
            using var streamB = File.OpenRead(b);
            return sha.ComputeHash(streamA).AsSpan().SequenceEqual(sha.ComputeHash(streamB));
        }

        /// <summary>
        /// Where Dofus.exe is, or an empty string if it does not show up.
        ///
        /// What has been chosen by hand rules, because the client need not be next to the
        /// emulator: whoever has it on another disk points to it once and that is it. If nothing is
        /// chosen —or what was chosen no longer exists— it is looked for where it has always been looked for, alongside.
        /// </summary>
        public static string ResolveClient()
        {
            string elegido = UI.LauncherPreferences.ClientExecutable;
            if (elegido.Length > 0) return elegido;

            string alLado = Path.Combine(Paths.ClientDir, "Dofus.exe");
            return File.Exists(alLado) ? alLado : "";
        }

        [System.Runtime.InteropServices.DllImport("user32.dll")]
        private static extern bool ShowWindow(IntPtr window, int command);

        private const int ShowMaximized = 3;

        /// <summary>
        /// Maximizes the client once it has a window. It opens at whatever size Unity has saved,
        /// which is often smaller than the screen, and the game lays its interface out against
        /// the window: part of the bottom bar ends up off the visible area.
        ///
        /// It has to be done from here rather than through a launch argument because the window
        /// does not exist yet when the process starts.
        /// </summary>
        private static void MaximizeWhenReady(System.Diagnostics.Process client)
        {
            System.Threading.Tasks.Task.Run(async () =>
            {
                try
                {
                    var deadline = DateTime.UtcNow.AddSeconds(90);
                    while (DateTime.UtcNow < deadline)
                    {
                        if (client.HasExited) return;
                        client.Refresh();
                        if (client.MainWindowHandle != IntPtr.Zero)
                        {
                            // The window appears before Unity finishes sizing it; maximizing too
                            // early gets undone.
                            await System.Threading.Tasks.Task.Delay(2500);
                            client.Refresh();
                            if (!client.HasExited && client.MainWindowHandle != IntPtr.Zero)
                            {
                                ShowWindow(client.MainWindowHandle, ShowMaximized);
                                Console.WriteLine("[Launcher] Client window maximized.");
                            }
                            return;
                        }
                        await System.Threading.Tasks.Task.Delay(500);
                    }
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"[Launcher] Could not maximize the client window: {ex.Message}");
                }
            });
        }

        /// <summary>Tells the server that the client of that account is no longer there.</summary>
        private static void Devolver(long accountId)
        {
            try { ControlClient.Pedir("fin-de-lanzamiento", new { cuenta = accountId }); }
            catch { }
        }

        // ─── Who is playing ─────────────────────────────────────────────────────
        //
        // The window asks this many times while repainting —each row's "En juego"
        // dot, the team counter, whether a button can be pressed— and it cannot go over the wire
        // for each one. What the server said in the last status probe is kept, which is every
        // two seconds, and the reads go against that.

        private static volatile System.Collections.Generic.HashSet<long> _jugando = new();
        private static volatile int _tope = 8;

        /// <summary>How many clients the server admits at once.</summary>
        public static int MaximumClients => _tope;

        /// <summary>
        /// The clients THIS launcher has started that are still alive.
        /// </summary>
        /// <remarks>
        /// The probe arrives every two seconds and a double click does not wait two seconds. Pressing
        /// «Jugar» twice in a row ran the start twice: the second still saw the
        /// account free —because the probe had not come back yet—, launched another client, and the
        /// server rejected it with «cuenta-ya-abierta»; the launcher showed that as if the
        /// player had done something wrong.
        ///
        /// This knows it on the spot and without asking anyone: it is recorded on starting the process and
        /// removed when the process dies, which is the same event that already told the server.
        /// </remarks>
        private static readonly System.Collections.Concurrent.ConcurrentDictionary<long, byte> _arrancadosAqui
            = new System.Collections.Concurrent.ConcurrentDictionary<long, byte>();

        /// <summary>Whether that account has a client open: what the probe said, or what we already know.</summary>
        public static bool IsActive(long accountId)
            => _jugando.Contains(accountId) || _arrancadosAqui.ContainsKey(accountId);

        /// <summary>How many accounts are playing, without counting twice those that appear in both places.</summary>
        public static int ActiveCount
        {
            get
            {
                var todas = new System.Collections.Generic.HashSet<long>(_jugando);
                foreach (var cuenta in _arrancadosAqui.Keys) todas.Add(cuenta);
                return todas.Count;
            }
        }

        private static void RefrescarQuienJuega()
        {
            var cuerpo = ControlClient.Pedir("activos").Cuerpo();
            if (cuerpo == null)
            {
                // Without a server nobody is playing, and above all: not keeping the previous list, which
                // would make the window keep painting accounts "en juego" from a dead session.
                _jugando = new System.Collections.Generic.HashSet<long>();
                return;
            }

            var vistas = new System.Collections.Generic.HashSet<long>();
            if (cuerpo.Value.TryGetProperty("cuentas", out var lista))
            {
                foreach (var una in lista.EnumerateArray()) vistas.Add(una.GetInt64());
            }
            _jugando = vistas;
            if (cuerpo.Value.TryGetProperty("maximo", out var maximo)) _tope = maximo.GetInt32();
        }

        /// <summary>
        /// Whether the server is up, and in passing who is playing.
        ///
        /// It looked at two static flags —ZaapServer.IsRunning and GameServerProxy.IsRunning— which belong
        /// to the process that raised them. In the launcher they would always be false and the traffic light would say
        /// "fuera de línea" with the server perfectly alive.
        /// </summary>
        public static ServicesStatus GetStatus()
        {
            var cuerpo = ControlClient.Pedir("estado").Cuerpo();
            if (cuerpo == null)
            {
                _jugando = new System.Collections.Generic.HashSet<long>();
                return new ServicesStatus { Online = false, DatabaseOk = false, ServicesListening = false };
            }

            RefrescarQuienJuega();

            return new ServicesStatus
            {
                Online = cuerpo.Value.GetProperty("enLinea").GetBoolean(),
                DatabaseOk = cuerpo.Value.GetProperty("base_").GetBoolean(),
                ServicesListening = cuerpo.Value.GetProperty("servicios").GetBoolean(),
                Version = cuerpo.Value.TryGetProperty("version", out var v) ? (v.GetString() ?? Version) : Version,
            };
        }

    }
}
