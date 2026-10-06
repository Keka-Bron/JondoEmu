using System;
using System.Net.Http;
using System.Text;
using System.Text.Json;

namespace Jondo.Unity.Launcher.Network
{
    /// <summary>
    /// The launcher's side of the control channel: it talks to the server over HTTP.
    ///
    /// Everything goes through here, even when the server is this same process. A single path instead of
    /// two —«if it is local I call the method, and if not, over the wire»— because the path only
    /// used sometimes is the one that breaks without anyone noticing.
    ///
    /// The calls are SYNCHRONOUS on purpose: the caller is the window, which already made them
    /// synchronously when they were normal methods, and that way it does not have to be rewritten whole. That is why the
    /// timeout is short: with the server down the window cannot be left stuck.
    /// </summary>
    public static class ControlClient
    {
        // ─── The timeouts, each with its name and its reason ────────────────────
        //
        // There was a single one, of four seconds, for everything. Four seconds are plenty to
        // ask for the status and are TOO FEW to log in while the server is reading the maps:
        // that was the «el servidor no responde» that came out when logging in right after starting it,
        // with a server that was perfectly fine. A timeout without a name ends up used for everything and
        // being wrong for almost everything.

        /// <summary>Probing whether anyone is there. Short on purpose: it only asks and hangs up.</summary>
        public static readonly TimeSpan PlazoDeSondeo = TimeSpan.FromSeconds(2);

        /// <summary>A normal order —log in, create an account, launch—, which touches the database.</summary>
        public static readonly TimeSpan PlazoDeOrden = TimeSpan.FromSeconds(15);

        /// <summary>
        /// How long to wait for a freshly started server to answer for the first time.
        /// </summary>
        /// <remarks>
        /// Ninety seconds, which is what it takes from cold: it reads the base, the managers and the maps
        /// before opening a single port. It is not the timeout of a request but that of the loop that
        /// insists; each attempt inside uses <see cref="PlazoDeSondeo"/>.
        /// </remarks>
        public static readonly TimeSpan PlazoDeArranque = TimeSpan.FromSeconds(90);

        /// <summary>
        /// Each timeout, its client.
        /// </summary>
        /// <remarks>
        /// HttpClient fixes the timeout on being built and does not allow changing it once
        /// something has been sent, so there is one per timeout. They are two objects for the whole life of the process,
        /// not one per request, which is what has to be avoided with HttpClient.
        /// </remarks>
        private static readonly HttpClient ClienteDeSondeo = new HttpClient { Timeout = PlazoDeSondeo };
        private static readonly HttpClient ClienteDeOrden = new HttpClient { Timeout = PlazoDeOrden };

        /// <summary>The verbs that only ask, and that is why they go with the short timeout.</summary>
        private static readonly System.Collections.Generic.HashSet<string> SóloPreguntan =
            new(StringComparer.OrdinalIgnoreCase) { "estado", "recordar-token" };

        private static string _secreto = "";

        /// <summary>
        /// The session one talks with, or an empty string if nobody has logged in yet.
        ///
        /// It goes in every request: since the server checks roles, it is what says WHO asks
        /// for things. Before, this was done by a machine secret, which is no use as soon as the
        /// launcher is on someone else's computer.
        /// </summary>
        public static string Token { get; set; } = "";

        /// <summary>
        /// Which server one talks to.
        ///
        /// It comes from the preferences, not from a literal: by default this same machine —playing
        /// locally— and if not, the address put in the drop-down, which can be that of
        /// another computer over Hamachi or that of a VPS.
        /// </summary>
        public static string Base => $"http://{UI.LauncherPreferences.ServerHost}:{Contract.Puerto}";

        /// <summary>What the server answered, or silence if nobody was there.</summary>
        public readonly struct Respuesta
        {
            public Respuesta(bool llego, int codigo, string json)
            {
                Llego = llego; Codigo = codigo; Json = json;
            }

            /// <summary>False when there was no answer: there is no server, or it does not answer in time.</summary>
            public bool Llego { get; }
            public int Codigo { get; }
            public string Json { get; }

            public bool Bien => Llego && Codigo == 200;

            public JsonElement? Cuerpo()
            {
                if (!Bien || Json.Length == 0) return null;
                try { return JsonDocument.Parse(Json).RootElement.Clone(); }
                catch { return null; }
            }
        }

        /// <summary>
        /// Sends an order. If it is rejected because of the secret, it reads it again from the file and
        /// tries once more: the server hands out a new secret on each start, so a
        /// launcher that had been open for a while keeps the old one as soon as the server restarts.
        /// </summary>
        public static Respuesta Pedir(string verbo, object? cuerpo = null)
        {
            if (_secreto.Length == 0) _secreto = Contract.LeerSecreto();

            var salida = Intentar(verbo, cuerpo);
            if (salida.Llego && salida.Codigo == 403)
            {
                string releido = Contract.LeerSecreto();
                if (releido.Length > 0 && releido != _secreto)
                {
                    _secreto = releido;
                    salida = Intentar(verbo, cuerpo);
                }
            }
            return salida;
        }

        private static Respuesta Intentar(string verbo, object? cuerpo)
        {
            try
            {
                string json = ConElToken(cuerpo);
                using var peticion = new HttpRequestMessage(HttpMethod.Post, Base + Contract.Prefijo + verbo)
                {
                    Content = new StringContent(json, Encoding.UTF8, "application/json"),
                };
                if (_secreto.Length > 0) peticion.Headers.Add(Contract.Cabecera, _secreto);

                var cliente = SóloPreguntan.Contains(verbo) ? ClienteDeSondeo : ClienteDeOrden;
                using var respuesta = cliente.Send(peticion);
                string texto = respuesta.Content.ReadAsStringAsync().GetAwaiter().GetResult();
                return new Respuesta(true, (int)respuesta.StatusCode, texto);
            }
            catch
            {
                // There is no server on the other side, or it has not answered in time. It is not an error to
                // shout about: it is the normal situation while the server starts.
                return new Respuesta(false, 0, "");
            }
        }

        /// <summary>
        /// The request's body, with the session token put inside.
        ///
        /// It is put here and not in each call so that it cannot be forgotten in any: if it is missing, the
        /// server answers 401 and the launcher is left dumbfounded without saying why. If the caller already
        /// brings its own token —the case of starting a client of a specific account of the
        /// team— it sends its own, which need not be the window's session one.
        /// </summary>
        private static string ConElToken(object? cuerpo)
        {
            var campos = new System.Collections.Generic.Dictionary<string, object?>();
            if (cuerpo != null)
            {
                using var doc = JsonDocument.Parse(JsonSerializer.Serialize(cuerpo));
                foreach (var campo in doc.RootElement.EnumerateObject())
                {
                    campos[campo.Name] = campo.Value.ValueKind switch
                    {
                        JsonValueKind.String => campo.Value.GetString(),
                        JsonValueKind.Number => campo.Value.GetInt64(),
                        JsonValueKind.True => true,
                        JsonValueKind.False => false,
                        _ => campo.Value.ToString(),
                    };
                }
            }

            if (!campos.ContainsKey("token") || campos["token"] is not string suyo || suyo.Length == 0)
            {
                campos["token"] = Token;
            }
            return JsonSerializer.Serialize(campos);
        }

        /// <summary>
        /// Is anyone on the other side? It is the check before starting a client.
        ///
        /// It matters more than it seems: the client mod decides ONLY once, on starting, whether
        /// to redirect to the emulator, and it decides by probing this very port with 100 ms of patience.
        /// If it does not answer, the client gives no error: it goes to Ankama's servers. So
        /// before launching one has to know that 8888 is really answering.
        /// </summary>
        public static bool ServidorVivo()
        {
            var estado = Pedir("estado");
            return estado.Bien;
        }

        /// <summary>Waits for the server to answer, up to a cap. Returns whether it managed to.</summary>
        public static bool EsperarAlServidor(TimeSpan tope)
        {
            var hasta = DateTime.UtcNow + tope;
            while (DateTime.UtcNow < hasta)
            {
                if (ServidorVivo()) return true;
                System.Threading.Thread.Sleep(250);
            }
            return false;
        }
    }
}
