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
    /// Logging in through the website, with the browser in between and without the password going through here.
    /// </summary>
    /// <remarks>
    /// <b>Why.</b> Today the launcher asks for username and password in its own window. While that
    /// is so, the password goes through our process and we answer for it: that it does not stay in
    /// memory, that it does not end up in a log, that nobody puts up a fake launcher with the same
    /// face. With the browser in between, the password is typed on the website and here only a
    /// single-use code arrives.
    ///
    /// <b>The flow, which is the standard for desktop applications</b> (RFC 8252, authorisation code
    /// with PKCE and loopback redirect):
    ///
    /// <code>
    ///   1. an HTTP server is opened on 127.0.0.1, on a free port the system chooses
    ///   2. a random verifier is generated and its challenge = BASE64URL(SHA-256(verifier))
    ///   3. the browser is opened at  .../authorize?...&amp;code_challenge=CHALLENGE&amp;state=STATE
    ///   4. the person logs in on the website; the website redirects to  http://127.0.0.1:PORT/?code=...&amp;state=...
    ///   5. it is checked that the state is the one sent and the code is exchanged for the tokens,
    ///      sending the VERIFIER: without it, a stolen code is worth nothing
    /// </code>
    ///
    /// No client secret: in something handed out to the players there is no secret that holds,
    /// because it goes inside the executable. That is exactly what PKCE is here to replace.
    ///
    /// <b>Status.</b> It is written and tested against its own loop, but <b>there is no website yet</b>
    /// to talk to: while <see cref="UI.LauncherPreferences.WebSite"/> is empty, the
    /// launcher logs in the usual way. The day the site exists, that
    /// preference is filled in and this comes into operation without touching anything else.
    /// </remarks>
    internal static class OAuthFlow
    {
        /// <summary>Where the website lives and with what identity the launcher presents itself.</summary>
        public sealed record Endpoints(string Authorize, string Token, string ClientId, string Scope)
        {
            /// <summary>Those of a site at <paramref name="site"/> with the usual paths.</summary>
            public static Endpoints For(string site)
            {
                string raiz = site.TrimEnd('/');
                return new Endpoints($"{raiz}/oauth/authorize", $"{raiz}/oauth/token",
                                     "jondo-launcher", "game offline_access");
            }
        }

        /// <summary>What the website returns when everything has gone well.</summary>
        public sealed class Session
        {
            public string AccessToken { get; init; } = "";
            public string RefreshToken { get; init; } = "";
            public DateTimeOffset ExpiresAt { get; init; }

            /// <summary>
            /// Whether it is time to renew.
            /// </summary>
            /// <remarks>
            /// With a minute's margin: renewing right on expiry leaves the next request at the mercy
            /// of the two machines' clocks matching, and they do not.
            /// </remarks>
            public bool NeedsRefresh => DateTimeOffset.UtcNow >= ExpiresAt.AddMinutes(-1);
        }

        /// <summary>What fails, with the reason said in a way that can be shown.</summary>
        public sealed class OAuthException : Exception
        {
            public OAuthException(string message) : base(message) { }
        }

        private static readonly HttpClient _http = new HttpClient
        {
            // The same five seconds Bubble's client uses to connect. A website that does not
            // answer in five seconds is not going to answer, and leaving the window hanging meanwhile
            // is worse than saying so.
            Timeout = TimeSpan.FromSeconds(20),
        };

        /// <summary>Opens the browser and waits for the website to return the code.</summary>
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

        /// <summary>Renews with the refresh token, without bothering anyone again.</summary>
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

        // ─── The pieces ─────────────────────────────────────────────────────────

        /// <summary>
        /// A port that is free right now.
        /// </summary>
        /// <remarks>
        /// 0 is asked for and the system gives one of its own. Fixing a port would make two launchers open
        /// at once fight over it, and in this house that happens: eight multi-account clients.
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
            // Five minutes: what it takes someone to go to the website, type the password and
            // go through the second factor if there is one. Without a cap, a launcher whose tab was closed
            // stays waiting forever.
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

                    // The browser also asks for the icon; that is not the answer.
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

                    // The state is what ties this answer to this request. Without comparing it, another
                    // page open in the same browser could slip its own code in here.
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

        /// <summary>Comparison without timing leaks, which is how secrets are compared.</summary>
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
