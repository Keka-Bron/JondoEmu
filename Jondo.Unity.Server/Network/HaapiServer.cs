using System;
using System.Net;
using System.IO;
using System.Threading.Tasks;
using Jondo.Unity.Launcher;

namespace Jondo.Unity.Server.Network
{
    public static class HaapiServer
    {
        private static HttpListener? _listener;
        private static Task? _listenTask;
        private static bool _isRunning;

        public static void Start(int port)
        {
            if (_isRunning) return;
            _isRunning = true;

            _listener = new HttpListener();
            if (ServerBinding.Public)
            {
                _listener.Prefixes.Add($"http://+:{port}/");
            }
            else
            {
                _listener.Prefixes.Add($"http://localhost:{port}/");
                _listener.Prefixes.Add($"http://127.0.0.1:{port}/");
            }
            _listener.Start();

            Console.WriteLine($"[+] HAAPI HTTP Server listening on port {port}");

            _listenTask = Task.Run(async () =>
            {
                while (_isRunning && _listener != null)
                {
                    try
                    {
                        var ctx = await _listener.GetContextAsync();

                        // Con techo. Antes se despachaba a pelo con «_ = ...», sin cola ni límite,
                        // así que N peticiones simultáneas sumaban sus cuerpos en memoria y sus
                        // PBKDF2 en hilos. El semáforo no rechaza a nadie: hace esperar.
                        _ = AtenderConTechoAsync(ctx);
                    }
                    catch (Exception ex)
                    {
                        if (!_isRunning) break;
                        Console.WriteLine($"[HAAPI Error] {ex.Message}");
                    }
                }
            });
        }

        public static void Stop()
        {
            if (!_isRunning) return;
            _isRunning = false;
            _listener?.Stop();
            _listener?.Close();
            _listener = null;
        }

        /// <summary>
        /// El latido del lanzador: «¿estás ahí?» y «¿quién tiene cliente abierto?».
        ///
        /// La ventana del lanzador lo pregunta cada dos segundos, y cada vuelta escribía cuatro
        /// líneas —la petición y el cuerpo, por dos rutas—. Eso son ciento veinte líneas por
        /// minuto que no cuentan nada, y ahora que el registro sólo se ve en el servidor entierran
        /// lo que sí importa: quién entra, qué mapa se carga, qué pelea empieza. Se atienden
        /// exactamente igual; simplemente no se anotan.
        /// </summary>
        private static bool EsLatido(string path)
            => path == Contract.Prefijo + "estado" || path == Contract.Prefijo + "activos";

        /// <summary>Lo más grande que se acepta como cuerpo. El JSON mayor de estas rutas no llega a 1 KB.</summary>
        private const int TopeDelCuerpo = 64 * 1024;

        /// <summary>Cuántas peticiones se atienden a la vez.</summary>
        /// <remarks>
        /// Cada una puede costar un PBKDF2 —210.000 vueltas, unos 400 ms medidos— así que sin techo
        /// un puñado de peticiones simultáneas se lleva el hilo de todos. Ocho es holgado para un
        /// canal de mando que usa un lanzador.
        /// </remarks>
        private static readonly SemaphoreSlim _aLaVez = new SemaphoreSlim(8, 8);

        private static async Task AtenderConTechoAsync(HttpListenerContext ctx)
        {
            await _aLaVez.WaitAsync();
            try { await HandleHaapiRequestAsync(ctx); }
            finally { _aLaVez.Release(); }
        }

        /// <summary>
        /// El cuerpo, sin pasar de <see cref="TopeDelCuerpo"/>. Cadena vacía si se pasa.
        /// </summary>
        /// <remarks>
        /// No basta con mirar Content-Length: una petición con Transfer-Encoding: chunked no lo
        /// trae, y entonces el tope de arriba no ve nada que comparar. Aquí se cuenta lo leído de
        /// verdad y se corta.
        /// </remarks>
        private static async Task<string> LeerAcotadoAsync(HttpListenerRequest req)
        {
            var buffer = new byte[8192];
            using var acumulado = new MemoryStream();

            while (acumulado.Length <= TopeDelCuerpo)
            {
                int leidos = await req.InputStream.ReadAsync(buffer, 0, buffer.Length);
                if (leidos == 0) break;
                acumulado.Write(buffer, 0, leidos);
            }

            if (acumulado.Length > TopeDelCuerpo) return "";
            return (req.ContentEncoding ?? System.Text.Encoding.UTF8).GetString(acumulado.ToArray());
        }

        private static async Task HandleHaapiRequestAsync(HttpListenerContext ctx)
        {
            var req = ctx.Request;
            var resp = ctx.Response;
            string path = req.Url?.AbsolutePath ?? "/";
            string clientIp = req.RemoteEndPoint.Address.ToString();
            bool latido = EsLatido(path);
            if (path != "/" && !latido)
            {
                Console.WriteLine($"[HAAPI] {req.HttpMethod} {path} from {clientIp}");
            }

            // The launcher is no longer a web page: it is the native window in the UI folder. All
            // that is left here are the icons, which a stray request still asks for.
            if (path == "/favicon.ico" || path == "/icon.ico")
            {
                await ServeLauncherStaticFileAsync(path, resp);
                return;
            }

            // El cuerpo, con tope y ANTES de decidir si la ruta pide token. Ese orden no se puede
            // cambiar —hay rutas que no piden ninguno, como /api/estado— así que el tope es la única
            // defensa: sin él, ReadToEndAsync se traga lo que le manden. HttpListener no acota el
            // cuerpo por su cuenta (el MaxRequestBytes de http.sys es para la línea de petición y
            // las cabeceras, no para la entidad), y ReadToEndAsync acumula en un StringBuilder y
            // luego hace ToString(), o sea que el pico de memoria es del orden de cuatro veces lo
            // enviado. 64 KB sobra para el JSON más grande de estas rutas.
            string body = "";
            if (req.HasEntityBody)
            {
                if (req.ContentLength64 > TopeDelCuerpo)
                {
                    Console.WriteLine($"[HAAPI] Cuerpo de {req.ContentLength64} bytes desde {clientIp}: " +
                                      $"pasa del tope de {TopeDelCuerpo}. 413.");
                    resp.StatusCode = 413;
                    resp.Close();
                    return;
                }

                body = await LeerAcotadoAsync(req);
                if (body.Length == 0 && req.ContentLength64 != 0)
                {
                    resp.StatusCode = 413;
                    resp.Close();
                    return;
                }

                if (body.Length > 0 && body.Length < 1000 && !latido)
                    Console.WriteLine($"[HAAPI]  body: {Censura.Cuerpo(body)}");
            }

            // Las rutas de mando del lanzador. Estuvieron aquí, se borraron al pasar a la ventana
            // nativa —"peso muerto con una puerta abierta encima"— y vuelven ahora que el lanzador
            // es otro proceso y no puede llamar a nadie por memoria.
            //
            // What closes the door is ConRol, inside ControlApi: a token the database recognises plus
            // the administrator role, checked server-side on every request. It is NOT the secret --
            // the header is passed along and ControlApi never looks at it, see ControlApi.Autorizada
            // for why. This comment used to say "without the secret this answers 403", and that was
            // simply untrue.
            var deControl = ControlApi.Responder(path, req.HttpMethod, body, req.Headers[Contract.Cabecera], clientIp);
            if (deControl != null)
            {
                byte[] cuerpo = System.Text.Encoding.UTF8.GetBytes(deControl.Value.Json);
                resp.StatusCode = deControl.Value.Codigo;
                resp.ContentType = "application/json; charset=utf-8";
                resp.ContentLength64 = cuerpo.Length;
                await resp.OutputStream.WriteAsync(cuerpo, 0, cuerpo.Length);
                resp.Close();
                return;
            }

            try
            {
                long accountId = ResolveRequestAccount(req, body);
                string json = RouteHaapi(path, req.HttpMethod, body, accountId);
                byte[] buf = System.Text.Encoding.UTF8.GetBytes(json);
                resp.StatusCode = 200;
                resp.ContentType = "application/json; charset=utf-8";
                resp.ContentLength64 = buf.Length;
                
                resp.AddHeader("Access-Control-Allow-Origin", "*");
                resp.AddHeader("Access-Control-Allow-Headers", "Content-Type, Authorization, apikey");
                await resp.OutputStream.WriteAsync(buf, 0, buf.Length);
            }
            catch (NotImplementedException nie)
            {
                Console.WriteLine($"[HAAPI]  !! Unhandled endpoint: {nie.Message}");
                resp.StatusCode = 404;
                byte[] buf = System.Text.Encoding.UTF8.GetBytes($"{{\"error\":\"Not implemented: {nie.Message}\"}}");
                resp.ContentLength64 = buf.Length;
                await resp.OutputStream.WriteAsync(buf, 0, buf.Length);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[HAAPI]  !! Error: {ex.Message}");
                resp.StatusCode = 500;
            }
            finally
            {
                resp.OutputStream.Close();
            }
        }

        private static async Task ServeLauncherStaticFileAsync(string path, HttpListenerResponse resp)
        {
            string assetsDir = Path.Combine(Paths.Root, "launcher_assets");
            string filename = Path.GetFileName(path);
            string filePath = Path.Combine(assetsDir, filename);
            if (!File.Exists(filePath) && (filename == "favicon.ico" || filename == "icon.ico"))
            {
                filePath = Path.Combine(assetsDir, "favicon.ico");
            }

            if (File.Exists(filePath))
            {
                byte[] fileBytes = await File.ReadAllBytesAsync(filePath);
                resp.StatusCode = 200;
                string ext = Path.GetExtension(filePath).ToLowerInvariant();
                resp.ContentType = ext switch
                {
                    ".html" => "text/html; charset=utf-8",
                    ".png" => "image/png",
                    ".jpg" or ".jpeg" => "image/jpeg",
                    ".css" => "text/css",
                    ".js" => "application/javascript",
                    _ => "application/octet-stream"
                };
                resp.ContentLength64 = fileBytes.Length;
                resp.AddHeader("Access-Control-Allow-Origin", "*");
                await resp.OutputStream.WriteAsync(fileBytes, 0, fileBytes.Length);
            }
            else
            {
                resp.StatusCode = 404;
            }
            resp.OutputStream.Close();
        }

        private static string RouteHaapi(string path, string method, string body, long accountId)
        {
            if (method == "OPTIONS") return "{}";

            if (method == "GET" && path == "/config/dofus3.json")
                return Dofus3ConfigResponse();

            if (method == "POST" && (path == "/json/Ankama/v5/Api/Connect" || path == "/json/Ankama/v5/Account/ApiKey" || path == "/json/Ankama/v5/Account/CreateApiKey"))
                return TokenResponse(accountId);

            if (method == "GET" && path.StartsWith("/json/Ankama/v5/Account/GetAccount"))
                return AccountResponse(accountId);

            if (method == "GET" && path == "/json/Ankama/v5/Game/ServerList")
                return GameServerListResponse(accountId);

            if (method == "POST" && path == "/json/Ankama/v5/Api/GameToken")
                return GameTokenResponse(accountId);

            if (method == "POST" && path == "/json/Ankama/v5/Game/SelectServer")
                return SelectServerResponse(accountId);

            // Return a tolerant empty JSON response for any other unhandled endpoint
            // (e.g. telemetries like SendEvent) to prevent client-side promise rejection crashes.
            return "{}";
        }

        private static string Dofus3ConfigResponse() => @"{
            ""gameAppId"": 1,
            ""connectionHosts"": [
                ""JMBouftou:127.0.0.1:5555""
            ],
            ""buildType"": ""release"",
            ""chatAppId"": 99,
            ""chatServerHost"": ""127.0.0.1"",
            ""chatServerPort"": 6337,
            ""versionFileUrl"": """",
            ""haapiAnkamaUrl"": ""http://127.0.0.1:8888/json/Ankama/v5/"",
            ""haapiDofusUrl"": ""http://127.0.0.1:8888/json/Dofus/v3/"",
            ""shopiDofusUrl"": ""https://shop-api.ankama.com"",
            ""webShopDofusUrl"": ""https://store.ankama.com/"",
            ""gamesActivityDescriptorUrl"": ""https://launcher.cdn.ankama.com/configs/useractivities.json"",
            ""avatarUrlFormat"": ""https://avatar.ankama.lan/users/{0}.png"",
            ""dofusWebsiteUrl"": ""https://www.dofus.com"",
            ""local"": {
                ""build_override"": ""3.6.4"",
                ""cdn_override"": ""https://dofus2.cdn.ankama.com"",
                ""client_override"": ""es""
            },
            ""login"": {
                ""ports"": [5555],
                ""hosts"": [""127.0.0.1""]
            }
        }";

        private static string TokenResponse(long accountId)
        {
            string token = Guid.NewGuid().ToString("N");
            ClientLaunchRegistry.RegisterToken(accountId, token);
            return System.Text.Json.JsonSerializer.Serialize(new
            {
                token,
                key = token,
                expiration = "2035-01-01T00:00:00Z"
            });
        }

        private static string AccountResponse(long accountId)
        {
            var account = DatabaseManager.GetAccountById(accountId);
            long accId = account?.Id ?? 0;
            string login = account?.Login ?? "unknown";
            string nick = account?.Nickname ?? "Jondo";

            return $@"{{
                ""id"": {accId},
                ""login"": ""{login}"",
                ""nickname"": ""{nick}"",
                ""tag"": ""2026"",
                ""security"": [],
                ""added_date"": ""2026-06-22T00:00:00Z"",
                ""locked"": false,
                ""parental_control"": false,
                ""avatar"": ""0"",
                ""fb_id"": null,
                ""anonymous"": false,
                ""steam_id"": null,
                ""google_id"": null,
                ""apple_id"": null,
                ""email"": ""{login}@emulator.com"",
                ""lang"": ""es"",
                ""country"": ""ES"",
                ""pioneer"": false
            }}";
        }

        /// <summary>
        /// Server list over HTTP. It comes from the same table as the protocol one, so that both
        /// say the same thing: this one used to advertise a server the other did not even have.
        /// The status here is what decides how each server is drawn on the selection screen; only
        /// the open one is reported as online.
        /// </summary>
        private static string GameServerListResponse(long accountId)
        {
            var characters = DatabaseManager.GetCharactersByAccountId(accountId);

            var servers = new List<object>();
            foreach (var server in DatabaseManager.GetServers())
            {
                int charactersOnServer = 0;
                foreach (var c in characters)
                {
                    if (c.ServerId == server.Id) charactersOnServer++;
                }

                servers.Add(new
                {
                    id = server.Id,
                    name = server.Name,
                    type = server.Type,
                    status = server.Status,
                    completion = 1,
                    is_mono = false,
                    characters = charactersOnServer,
                    date = DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ssZ")
                });
            }

            return System.Text.Json.JsonSerializer.Serialize(new { servers });
        }

        private static string GameTokenResponse(long accountId)
        {
            if (accountId <= 0) throw new InvalidOperationException("No account token was supplied to HAAPI.");
            string token = Guid.NewGuid().ToString("N");
            DatabaseManager.SetGameToken(accountId, token);
            ClientLaunchRegistry.RegisterToken(accountId, token);

            return System.Text.Json.JsonSerializer.Serialize(new
            {
                token = token,
                server = new { host = "127.0.0.1", port = 5555 }
            });
        }

        /// <summary>
        /// Elegir servidor: devuelve el token con el que el cliente se conectara al de juego.
        /// </summary>
        /// <remarks>
        /// EL TOKEN SE REGISTRA, que es lo que aqui faltaba. Es el mismo fallo que tenia el kqr de
        /// la vuelta atras: se acunaba un identificador, se le mandaba al cliente y no se guardaba
        /// en ningun sitio, asi que cuando el cliente lo presentaba no lo reconocia nadie y se le
        /// cerraba la conexion.
        ///
        /// Aquella se descubrio porque el jugador la pisaba; esta no la pisa NADIE con este
        /// cliente -en las 6.803 lineas del registro y sus veinte arranques, esta ruta y la de
        /// GameToken tienen cero visitas; las unicas /json/ que pide son Cms/PollInGame/Get,
        /// Cms/Items/GetFeeds y Game/SendEvent-. O sea que es la misma puerta rota esperando a que
        /// alguien la abra, no un fallo que se este viendo. Se arregla igual.
        ///
        /// Con la cuenta a cero no se registra nada: seria dejar un token valido sin dueno.
        /// </remarks>
        private static string SelectServerResponse(long accountId)
        {
            string token = Guid.NewGuid().ToString("N");

            if (accountId > 0)
            {
                DatabaseManager.SetGameToken(accountId, token);
                ClientLaunchRegistry.RegisterToken(accountId, token);
            }
            else
            {
                Console.WriteLine("[HAAPI] SelectServer sin cuenta identificada: el token que se " +
                                  "devuelve no queda registrado y la conexion sera rechazada.");
            }

            return System.Text.Json.JsonSerializer.Serialize(new
            {
                token,
                server = new { host = "127.0.0.1", port = 5555 }
            });
        }

        private static long ResolveRequestAccount(HttpListenerRequest request, string body)
        {
            var candidates = new List<string?>
            {
                request.Headers["Authorization"],
                request.Headers["apikey"],
                request.QueryString["token"],
                request.QueryString["apikey"]
            };

            foreach (string? raw in candidates)
            {
                string candidate = raw?.Trim() ?? "";
                if (candidate.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
                    candidate = candidate.Substring(7).Trim();
                long accountId = ClientLaunchRegistry.ResolveToken(candidate);
                if (accountId > 0) return accountId;
            }

            // Some HAAPI calls carry their token in a small JSON body rather than a header.
            if (!string.IsNullOrWhiteSpace(body))
            {
                try
                {
                    using var document = System.Text.Json.JsonDocument.Parse(body);
                    foreach (var property in document.RootElement.EnumerateObject())
                    {
                        if (property.Value.ValueKind != System.Text.Json.JsonValueKind.String) continue;
                        if (!property.Name.Contains("token", StringComparison.OrdinalIgnoreCase) &&
                            !property.Name.Contains("key", StringComparison.OrdinalIgnoreCase)) continue;
                        long accountId = ClientLaunchRegistry.ResolveToken(property.Value.GetString());
                        if (accountId > 0) return accountId;
                    }
                }
                catch { }
            }
            return 0;
        }
    }
}
