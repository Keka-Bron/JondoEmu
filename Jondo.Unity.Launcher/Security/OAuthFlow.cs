using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace Jondo.Unity.Launcher.Security
{
    /// <summary>
    /// Entrar por la web, con el navegador de por medio y sin que la contraseña pase por aquí.
    /// </summary>
    /// <remarks>
    /// <b>Por qué.</b> Hoy el lanzador pide usuario y contraseña en su propia ventana. Mientras eso
    /// sea así, la contraseña pasa por nuestro proceso y respondemos de ella: de que no se quede en
    /// memoria, de que no acabe en un registro, de que nadie ponga un lanzador falso con la misma
    /// cara. Con el navegador de por medio, la contraseña se escribe en la web y aquí sólo llega un
    /// código de un solo uso.
    ///
    /// <b>El flujo, que es el estándar para aplicaciones de escritorio</b> (RFC 8252, código de
    /// autorización con PKCE y redirección a loopback):
    ///
    /// <code>
    ///   1. se abre un servidor HTTP en 127.0.0.1, en un puerto libre que elige el sistema
    ///   2. se genera un verificador al azar y su reto = BASE64URL(SHA-256(verificador))
    ///   3. se abre el navegador en  .../authorize?...&amp;code_challenge=RETO&amp;state=ESTADO
    ///   4. la persona entra en la web; la web redirige a  http://127.0.0.1:PUERTO/?code=...&amp;state=...
    ///   5. se comprueba que el estado es el que se mandó y se cambia el código por los vales,
    ///      enviando el VERIFICADOR: sin él, un código robado no vale para nada
    /// </code>
    ///
    /// Nada de secreto de cliente: en algo que se reparte a los jugadores no hay secreto que valga,
    /// porque va dentro del ejecutable. Eso es justo lo que PKCE viene a sustituir.
    ///
    /// <b>Estado.</b> Está escrito y probado contra su propio bucle, pero <b>todavía no hay web</b>
    /// contra la que hablar: mientras <see cref="UI.LauncherPreferences.WebSite"/> esté vacío, el
    /// lanzador entra por el camino de siempre. El día que exista el sitio, se rellena esa
    /// preferencia y esto entra en funcionamiento sin tocar nada más.
    /// </remarks>
    internal static class OAuthFlow
    {
        /// <summary>Dónde vive la web y con qué identidad se presenta el lanzador.</summary>
        public sealed record Endpoints(string Authorize, string Token, string ClientId, string Scope)
        {
            /// <summary>Los de un sitio en <paramref name="site"/> con las rutas de siempre.</summary>
            public static Endpoints For(string site)
            {
                string raiz = site.TrimEnd('/');
                return new Endpoints($"{raiz}/oauth/authorize", $"{raiz}/oauth/token",
                                     "jondo-launcher", "game offline_access");
            }
        }

        /// <summary>Lo que devuelve la web cuando todo ha ido bien.</summary>
        public sealed class Session
        {
            public string AccessToken { get; init; } = "";
            public string RefreshToken { get; init; } = "";
            public DateTimeOffset ExpiresAt { get; init; }

            /// <summary>
            /// Si conviene renovar ya.
            /// </summary>
            /// <remarks>
            /// Con un minuto de margen: renovar justo al vencer deja la petición siguiente a merced
            /// de que el reloj de las dos máquinas coincida, y no coinciden.
            /// </remarks>
            public bool NeedsRefresh => DateTimeOffset.UtcNow >= ExpiresAt.AddMinutes(-1);
        }

        /// <summary>Lo que falla, con el motivo dicho de forma que se pueda enseñar.</summary>
        public sealed class OAuthException : Exception
        {
            public OAuthException(string message) : base(message) { }
        }

        private static readonly HttpClient _http = new HttpClient
        {
            // Los mismos cinco segundos que usa el cliente de Bubble para conectar. Una web que no
            // contesta en cinco segundos no va a contestar, y dejar la ventana colgada mientras
            // tanto es peor que decirlo.
            Timeout = TimeSpan.FromSeconds(20),
        };

        /// <summary>Abre el navegador y espera a que la web devuelva el código.</summary>
        public static async Task<Session> SignInAsync(Endpoints endpoints, CancellationToken ct = default)
        {
            string verificador = Base64Url(RandomNumberGenerator.GetBytes(32));
            string reto = Base64Url(SHA256.HashData(Encoding.ASCII.GetBytes(verificador)));
            string estado = Base64Url(RandomNumberGenerator.GetBytes(32));

            int puerto = PuertoLibre();
            string redireccion = $"http://127.0.0.1:{puerto}/";

            using var escucha = new HttpListener();
            escucha.Prefixes.Add(redireccion);
            escucha.Start();

            try
            {
                var url = new StringBuilder(endpoints.Authorize)
                    .Append("?response_type=code")
                    .Append("&client_id=").Append(Uri.EscapeDataString(endpoints.ClientId))
                    .Append("&redirect_uri=").Append(Uri.EscapeDataString(redireccion))
                    .Append("&scope=").Append(Uri.EscapeDataString(endpoints.Scope))
                    .Append("&state=").Append(estado)
                    .Append("&code_challenge=").Append(reto)
                    .Append("&code_challenge_method=S256")
                    .ToString();

                AbrirNavegador(url);

                string codigo = await EsperarElCodigo(escucha, estado, ct).ConfigureAwait(false);
                return await CambiarElCodigo(endpoints, codigo, verificador, redireccion, ct)
                    .ConfigureAwait(false);
            }
            finally
            {
                try { escucha.Stop(); } catch { }
            }
        }

        /// <summary>Renueva con el vale de renovación, sin volver a molestar a nadie.</summary>
        public static async Task<Session> RefreshAsync(Endpoints endpoints, string refreshToken,
                                                       CancellationToken ct = default)
        {
            if (string.IsNullOrWhiteSpace(refreshToken))
                throw new OAuthException("No hay vale de renovación que usar.");

            return await Pedir(endpoints, new Dictionary<string, string>
            {
                ["grant_type"] = "refresh_token",
                ["refresh_token"] = refreshToken,
                ["client_id"] = endpoints.ClientId,
            }, ct).ConfigureAwait(false);
        }

        // ─── Las piezas ─────────────────────────────────────────────────────────

        /// <summary>
        /// Un puerto que esté libre ahora mismo.
        /// </summary>
        /// <remarks>
        /// Se pide el 0 y el sistema da uno suyo. Fijar un puerto haría que dos lanzadores abiertos
        /// a la vez se pelearan por él, y en esta casa eso pasa: ocho clientes multicuenta.
        /// </remarks>
        private static int PuertoLibre()
        {
            var socket = new TcpListener(IPAddress.Loopback, 0);
            socket.Start();
            int puerto = ((IPEndPoint)socket.LocalEndpoint).Port;
            socket.Stop();
            return puerto;
        }

        private static async Task<string> EsperarElCodigo(HttpListener escucha, string estado,
                                                          CancellationToken ct)
        {
            // Cinco minutos: lo que tarda alguien en entrar en la web, escribir la contraseña y
            // pasar por el segundo factor si lo hay. Sin tope, un lanzador con la pestaña cerrada
            // se queda esperando para siempre.
            using var plazo = CancellationTokenSource.CreateLinkedTokenSource(ct);
            plazo.CancelAfter(TimeSpan.FromMinutes(5));

            using (plazo.Token.Register(() => { try { escucha.Abort(); } catch { } }))
            {
                while (true)
                {
                    HttpListenerContext contexto;
                    try
                    {
                        contexto = await escucha.GetContextAsync().ConfigureAwait(false);
                    }
                    catch (Exception) when (plazo.IsCancellationRequested)
                    {
                        throw new OAuthException("Se ha agotado el tiempo esperando a la web.");
                    }

                    var consulta = contexto.Request.QueryString;

                    // El navegador pide también el icono; eso no es la respuesta.
                    if (consulta["code"] == null && consulta["error"] == null)
                    {
                        Responder(contexto, 404, "");
                        continue;
                    }

                    string? error = consulta["error"];
                    string? codigo = consulta["code"];
                    string? devuelto = consulta["state"];

                    if (error != null)
                    {
                        Responder(contexto, 400, Pagina("No se ha podido entrar", error));
                        throw new OAuthException($"La web ha rechazado la entrada: {error}");
                    }

                    // El estado es lo que ata esta respuesta a esta petición. Sin compararlo, otra
                    // página abierta en el mismo navegador podría colar aquí su propio código.
                    if (!FixedTimeEquals(devuelto, estado))
                    {
                        Responder(contexto, 400, Pagina("Respuesta inesperada",
                            "El identificador de la petición no coincide."));
                        throw new OAuthException("La respuesta no corresponde a esta petición.");
                    }

                    Responder(contexto, 200, Pagina("Ya está",
                        "Puedes cerrar esta pestaña y volver al lanzador."));
                    return codigo!;
                }
            }
        }

        private static async Task<Session> CambiarElCodigo(Endpoints endpoints, string codigo,
                                                           string verificador, string redireccion,
                                                           CancellationToken ct)
            => await Pedir(endpoints, new Dictionary<string, string>
            {
                ["grant_type"] = "authorization_code",
                ["code"] = codigo,
                ["redirect_uri"] = redireccion,
                ["client_id"] = endpoints.ClientId,
                ["code_verifier"] = verificador,
            }, ct).ConfigureAwait(false);

        private static async Task<Session> Pedir(Endpoints endpoints, Dictionary<string, string> campos,
                                                 CancellationToken ct)
        {
            using var contenido = new FormUrlEncodedContent(campos);
            using var respuesta = await _http.PostAsync(endpoints.Token, contenido, ct).ConfigureAwait(false);
            string cuerpo = await respuesta.Content.ReadAsStringAsync(ct).ConfigureAwait(false);

            if (!respuesta.IsSuccessStatusCode)
                throw new OAuthException($"La web ha contestado {(int)respuesta.StatusCode} al pedir los vales.");

            try
            {
                using var json = JsonDocument.Parse(cuerpo);
                var raiz = json.RootElement;

                string acceso = Texto(raiz, "access_token");
                if (acceso.Length == 0) throw new OAuthException("La web no ha devuelto ningún vale de acceso.");

                int segundos = raiz.TryGetProperty("expires_in", out var e) && e.TryGetInt32(out int v) ? v : 3600;

                return new Session
                {
                    AccessToken = acceso,
                    RefreshToken = Texto(raiz, "refresh_token"),
                    ExpiresAt = DateTimeOffset.UtcNow.AddSeconds(segundos),
                };
            }
            catch (JsonException)
            {
                throw new OAuthException("La web ha contestado algo que no se entiende.");
            }
        }

        private static string Texto(JsonElement raiz, string nombre)
            => raiz.TryGetProperty(nombre, out var v) && v.ValueKind == JsonValueKind.String
                ? v.GetString() ?? ""
                : "";

        private static void Responder(HttpListenerContext contexto, int codigo, string html)
        {
            try
            {
                byte[] bytes = Encoding.UTF8.GetBytes(html);
                contexto.Response.StatusCode = codigo;
                contexto.Response.ContentType = "text/html; charset=utf-8";
                contexto.Response.ContentLength64 = bytes.Length;
                contexto.Response.OutputStream.Write(bytes, 0, bytes.Length);
                contexto.Response.Close();
            }
            catch { }
        }

        private static string Pagina(string titulo, string detalle) =>
            "<!doctype html><meta charset=\"utf-8\">" +
            "<title>Jondo</title>" +
            "<body style=\"background:#0d0603;color:#fff3d6;font:16px/1.6 system-ui,sans-serif;" +
            "display:flex;flex-direction:column;align-items:center;justify-content:center;height:100vh;margin:0\">" +
            $"<h1 style=\"color:#e6b800;font-size:24px;margin:0 0 8px\">{WebUtility.HtmlEncode(titulo)}</h1>" +
            $"<p style=\"margin:0;color:#b89865\">{WebUtility.HtmlEncode(detalle)}</p>";

        private static void AbrirNavegador(string url)
        {
            try
            {
                Process.Start(new ProcessStartInfo { FileName = url, UseShellExecute = true });
            }
            catch (Exception ex)
            {
                throw new OAuthException($"No se ha podido abrir el navegador: {ex.Message}");
            }
        }

        /// <summary>Comparación sin fugas por tiempo, que es como se comparan los secretos.</summary>
        private static bool FixedTimeEquals(string? a, string? b)
        {
            if (a == null || b == null) return false;
            return CryptographicOperations.FixedTimeEquals(
                Encoding.ASCII.GetBytes(a), Encoding.ASCII.GetBytes(b));
        }

        private static string Base64Url(byte[] bytes) => Convert.ToBase64String(bytes)
            .TrimEnd('=').Replace('+', '-').Replace('/', '_');
    }
}
