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

        /// <summary>A single line of the server event log.</summary>
        public sealed class LogEntry
        {
            public long Id { get; set; }
            public string Time { get; set; } = "";
            public string Message { get; set; } = "";
        }

        // ─── Operations ─────────────────────────────────────────────────────────

        /// <summary>
        /// Validates an account's credentials and generates a launcher token. There is
        /// deliberately no process-wide "active account".
        /// </summary>
        public static SignInResult SignIn(string username, string password, string clientIp)
        {
            var respuesta = ClienteDeControl.Pedir("entrar", new
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

            // La sesion queda puesta para todo lo que venga despues: es lo que dice quien pide
            // las cosas, y de lo que el servidor saca el rol.
            Network.ClienteDeControl.Token = cuerpo.Value.GetProperty("token").GetString() ?? "";
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
        /// Lo que se le dice al usuario cuando el servidor no ha contestado.
        ///
        /// Se distingue el silencio —no hay nadie escuchando— del rechazo por el secreto, porque
        /// son dos averías distintas y la segunda se arregla rearrancando el lanzador.
        /// </summary>
        private static string MensajeDeSilencio(Network.ClienteDeControl.Respuesta respuesta)
            => respuesta.Llego && respuesta.Codigo == 403
                ? UI.LauncherTexts.Current.ControlRechazado
                : UI.LauncherTexts.Current.ServidorSinResponder;

        /// <summary>Crea una cuenta nueva con su apodo. La escribe el servidor, no el lanzador.</summary>
        public static Result RegisterAccount(string username, string password, string nickname, string clientIp)
        {
            var respuesta = ClienteDeControl.Pedir("crear-cuenta", new
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
                    ? UI.LauncherTexts.Current.AccountCreated
                    : (cuerpo.Value.TryGetProperty("motivo", out var m) ? (m.GetString() ?? "") : ""),
            };
        }

        /// <summary>
        /// Vuelve a dar por buena una sesión que el lanzador tenía guardada de la vez anterior.
        ///
        /// El servidor sólo la acepta si el token sigue estando en la base a nombre de esa cuenta:
        /// el lanzador no puede decir que es quien le apetezca.
        /// </summary>
        public static bool RememberSession(long accountId, string token)
        {
            Network.ClienteDeControl.Token = token ?? "";
            var cuerpo = ClienteDeControl.Pedir("recordar-token",
                new { cuenta = accountId, token }).Cuerpo();
            return cuerpo != null && cuerpo.Value.GetProperty("bien").GetBoolean();
        }

        /// <summary>Si quien ha entrado es administrador. COSMÉTICO: quien decide es el servidor.</summary>
        public static bool EsAdministrador { get; private set; }

        /// <summary>Le pide al servidor que se apague. Sólo funciona si la cuenta es administrador.</summary>
        public static bool StopServer() => ClienteDeControl.Pedir("apagar").Bien;

        /// <summary>
        /// El código que manda el servidor, dicho en el idioma del lanzador.
        ///
        /// Ésta es la frontera: el servidor dice QUÉ ha pasado y aquí se decide CÓMO contarlo. Antes
        /// el servidor construía la frase, y para eso tenía que leer las preferencias de idioma del
        /// usuario desde %APPDATA%; un servidor no debería saber que existe un escritorio.
        /// </summary>
        private static string EnCristiano(string codigo) => codigo switch
        {
            Contrato.MotivoSesionCaducada => UI.LauncherTexts.Current.SessionExpiredError,
            Contrato.MotivoCuentaYaAbierta => UI.LauncherTexts.Current.AccountAlreadyRunning,
            Contrato.MotivoTopeDeClientes => UI.LauncherTexts.Current.MaxClientsError,
            _ => codigo.Length > 0 ? codigo : UI.LauncherTexts.Current.GenericError,
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
                        Message = UI.LauncherTexts.Current.ClientNotFound
                    };
                }

                // ANTES de nada, que el servidor esté contestando de verdad.
                //
                // No es paranoia: el mod del cliente decide una sola vez, al inicializarse, si
                // redirige al emulador, y lo decide sondeando el 8888 con 100 ms de paciencia
                // (JondoFix/Class1.cs:471). Si en ese instante no hay nadie, el cliente NO da
                // ningún error: se conecta a los servidores de Ankama. Con un solo proceso esto no
                // podía pasar porque los servicios estaban levantados antes de que existiera la
                // ventana; ahora sí puede, así que se comprueba.
                if (!ClienteDeControl.ServidorVivo())
                {
                    return new Result
                    {
                        Success = false,
                        Message = UI.LauncherTexts.Current.ServidorSinResponder
                    };
                }

                string language = UI.LauncherTexts.Code(UI.LauncherPreferences.Language);

                // El arranque lo reparte el servidor: él pone el instanceId y el hash, y él los va
                // a comprobar cuando el cliente se presente al Zaap. Antes se los inventaba el
                // lanzador y los apuntaba en un diccionario de su propia memoria; ese era el nudo
                // que hacía imposible separar los procesos.
                var respuesta = ClienteDeControl.Pedir("lanzamiento", new { token = token ?? "", idioma = language });
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

                // Unity is told the size up front. Maximizing afterwards is not enough on its own:
                // the client rebuilds its window when it moves between screens (server choice,
                // character choice, world) and reapplies its own saved resolution, so it drops out
                // of the maximized state and part of the interface ends up off screen.
                var area = System.Windows.Forms.Screen.PrimaryScreen?.WorkingArea
                           ?? new System.Drawing.Rectangle(0, 0, 1920, 1080);

                // MelonLoader abre su propia consola negra y su pantalla de arranque delante del
                // juego. Se le dice que no por línea de órdenes además de por Loader.cfg: la orden
                // manda sobre el fichero, así que da igual que alguien lo reescriba.
                string arguments =
                    $"-force-d3d11 -screen-fullscreen 0 -screen-width {area.Width} -screen-height {area.Height} " +
                    "--melonloader.hideconsole --melonloader.disablestartscreen " +
                    $"--port 15881 --gameName dofus --gameRelease dofus3 --instanceId {instanceId} --hash {hash} " +
                    $"--canLogin true --langCode {language} " +
                    "--autoConnectType 1 --connectionPort 5555";

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

                System.Diagnostics.Process? client;
                try
                {
                    client = System.Diagnostics.Process.Start(startInfo);
                }
                catch
                {
                    Devolver(accountId);
                    throw;
                }

                if (client == null)
                {
                    Devolver(accountId);
                    return new Result { Success = false, Message = UI.LauncherTexts.Current.ClientStartFailed };
                }

                // Cuando el cliente se cierre hay que decírselo al servidor, que es quien lleva la
                // cuenta de quién está jugando. Y aunque este aviso se pierda —porque se cierre el
                // lanzador antes— el servidor tiene dos redes debajo: la baja de la sesión de juego
                // y la caducidad de los lanzamientos que nunca llegaron a conectar.
                client.EnableRaisingEvents = true;
                client.Exited += (s, e) => Devolver(accountId);
                MaximizeWhenReady(client);
                Console.WriteLine($"[Launcher] Client {instanceId} launched for account {accountId} (PID {client.Id}).");
                return new Result { Success = true };
            }
            catch (Exception ex)
            {
                return new Result { Success = false, Message = $"Error starting the client: {ex.Message}" };
            }
        }

        /// <summary>
        /// Dónde está el Dofus.exe, o cadena vacía si no aparece.
        ///
        /// Manda lo que se haya elegido a mano, porque el cliente no tiene por qué estar junto al
        /// emulador: quien lo tenga en otro disco lo señala una vez y se acabó. Si no hay nada
        /// elegido —o lo elegido ya no existe— se busca donde se ha buscado siempre, al lado.
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

        /// <summary>Le dice al servidor que el cliente de esa cuenta ya no está.</summary>
        private static void Devolver(long accountId)
        {
            try { ClienteDeControl.Pedir("fin-de-lanzamiento", new { cuenta = accountId }); }
            catch { }
        }

        // ─── Quién está jugando ─────────────────────────────────────────────────
        //
        // La ventana lo pregunta muchas veces mientras se repinta —el punto de "En juego" de cada
        // fila, el contador del equipo, si un botón se puede pulsar— y no puede irse por el cable
        // en cada una. Se guarda lo que dijo el servidor en el último sondeo de estado, que es cada
        // dos segundos, y las lecturas van contra eso.

        private static volatile System.Collections.Generic.HashSet<long> _jugando = new();
        private static volatile int _tope = 8;

        /// <summary>Cuántos clientes admite el servidor a la vez.</summary>
        public static int MaximumClients => _tope;

        /// <summary>Si esa cuenta tiene un cliente abierto, según el último sondeo.</summary>
        public static bool IsActive(long accountId) => _jugando.Contains(accountId);

        /// <summary>Cuántas cuentas están jugando, según el último sondeo.</summary>
        public static int ActiveCount => _jugando.Count;

        private static void RefrescarQuienJuega()
        {
            var cuerpo = ClienteDeControl.Pedir("activos").Cuerpo();
            if (cuerpo == null)
            {
                // Sin servidor no hay nadie jugando, y sobre todo: no dejar la lista de antes, que
                // haría que la ventana siguiera pintando cuentas "en juego" de una sesión muerta.
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
        /// Si el servidor está en pie, y de paso quién está jugando.
        ///
        /// Miraba dos banderas estáticas —ZaapServer.IsRunning y GameServerProxy.IsRunning— que son
        /// del proceso que las levantó. En el lanzador valdrían false siempre y el semáforo diría
        /// "fuera de línea" con el servidor perfectamente vivo.
        /// </summary>
        public static ServicesStatus GetStatus()
        {
            var cuerpo = ClienteDeControl.Pedir("estado").Cuerpo();
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

        /// <summary>
        /// Returns the console lines newer than the given id, already deserialized, so that the
        /// native window does not have to talk over HTTP with its own process.
        /// </summary>
        public static IReadOnlyList<LogEntry> GetLogs(long sinceId)
        {
            var entries = new List<LogEntry>();
            try
            {
                // Por el canal, no leyendo logs/emulator_console.log. El fichero no reconstruye las
                // entradas ni sus números de secuencia —sólo existen en memoria— y la ventana los
                // usa para pedir nada más lo nuevo.
                var pedido = ClienteDeControl.Pedir("registro", new { desde = sinceId });
                if (!pedido.Bien) return entries;

                using var doc = System.Text.Json.JsonDocument.Parse(pedido.Json);
                if (!doc.RootElement.TryGetProperty("logs", out var list)) return entries;

                foreach (var element in list.EnumerateArray())
                {
                    // Each line is read on its own. One that cannot be read must not carry off the
                    // ones behind it: the window only moves its cursor as far as what it is given,
                    // so a line that always fails would be asked for again for ever and the console
                    // would sit there frozen. Better to lose the line and keep going.
                    long entryId = 0;
                    try
                    {
                        entryId = element.TryGetProperty("id", out var id) ? id.GetInt64() : 0;
                        entries.Add(new LogEntry
                        {
                            Id = entryId,
                            Time = element.TryGetProperty("time", out var time) ? time.GetString() ?? "" : "",
                            Message = element.TryGetProperty("msg", out var msg) ? msg.GetString() ?? "" : ""
                        });
                    }
                    catch
                    {
                        if (entryId > 0)
                        {
                            entries.Add(new LogEntry
                            {
                                Id = entryId,
                                Time = "",
                                Message = "[log] a line could not be read and was dropped."
                            });
                        }
                    }
                }
            }
            catch
            {
                // A failure while reading the buffer must not take the UI down: it is retried on the next cycle.
            }
            return entries;
        }

    }
}
