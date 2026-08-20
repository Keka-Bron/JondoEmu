using System;
using System.Net.Http;
using System.Text;
using System.Text.Json;

namespace Jondo.Unity.Admin
{
    /// <summary>
    /// El cliente del canal de mando: por aquí habla el panel con el servidor en marcha.
    ///
    /// Es el mismo canal que usa el lanzador —el HAAPI del 8888, con sus rutas /api/— así que no
    /// hay nada nuevo abierto en el servidor: entrar con una cuenta de administrador da el token,
    /// y con el token se llama a lo demás. Si el servidor no está, todo falla en calma y la ventana
    /// lo enseña en su barra de estado.
    /// </summary>
    internal sealed class AdminClient
    {
        private readonly HttpClient _http = new() { Timeout = TimeSpan.FromSeconds(15) };

        public string Host { get; set; } = "127.0.0.1";
        public int Port { get; set; } = 8888;

        public string Token { get; private set; } = "";
        public string Nickname { get; private set; } = "";
        public long AccountId { get; private set; }
        public bool Connected => Token.Length > 0;

        private string Url(string ruta) => $"http://{Host}:{Port}/api/{ruta}";

        // ─── Las llamadas ─────────────────────────────────────────────────────────────────────

        /// <summary>Entra con usuario y clave. Devuelve null si fue bien, o el motivo si no.</summary>
        public string? SignIn(string usuario, string clave)
        {
            var respuesta = Post("entrar", new { usuario, clave, ip = "127.0.0.1" });
            if (respuesta == null) return "sin-respuesta";

            var raiz = respuesta.Value;
            bool bien = raiz.TryGetProperty("bien", out var b) && b.GetBoolean();
            if (!bien)
            {
                string motivo = raiz.TryGetProperty("motivo", out var m) ? m.GetString() ?? "" : "";
                return motivo.Length > 0 ? motivo : "credenciales";
            }

            Token = raiz.GetProperty("token").GetString() ?? "";
            Nickname = raiz.TryGetProperty("apodo", out var a) ? a.GetString() ?? "" : usuario;
            AccountId = raiz.TryGetProperty("cuenta", out var c) ? c.GetInt64() : 0;
            return null;
        }

        public void SignOut()
        {
            Token = "";
            Nickname = "";
            AccountId = 0;
        }

        /// <summary>El estado del servidor, o null si no contesta.</summary>
        public JsonElement? Status()
        {
            try
            {
                using var respuesta = _http.GetAsync(Url("estado")).Result;
                if (!respuesta.IsSuccessStatusCode) return null;
                return JsonDocument.Parse(respuesta.Content.ReadAsStringAsync().Result).RootElement.Clone();
            }
            catch { return null; }
        }

        /// <summary>Las sesiones vivas. Devuelve el elemento raíz, o null si algo falló.</summary>
        public JsonElement? Sessions()
        {
            var raiz = Post("admin/sesiones", new { token = Token });
            return raiz;
        }

        /// <summary>Expulsa a una cuenta. Devuelve (bien, motivo).</summary>
        public (bool Bien, string Motivo) Kick(long cuenta)
        {
            var raiz = Post("admin/expulsar", new { token = Token, cuenta });
            if (raiz == null) return (false, "sin-respuesta");
            bool bien = raiz.Value.TryGetProperty("bien", out var b) && b.GetBoolean();
            string motivo = raiz.Value.TryGetProperty("motivo", out var m) ? m.GetString() ?? "" : "";
            return (bien, motivo);
        }

        /// <summary>Ejecuta un comando sobre la sesión de una cuenta. Devuelve (bien, motivo).</summary>
        public (bool Bien, string Motivo) Command(long cuenta, string orden)
        {
            var raiz = Post("admin/comando", new { token = Token, cuenta, orden });
            if (raiz == null) return (false, "sin-respuesta");
            bool bien = raiz.Value.TryGetProperty("bien", out var b) && b.GetBoolean();
            string motivo = raiz.Value.TryGetProperty("motivo", out var m) ? m.GetString() ?? "" : "";
            return (bien, motivo);
        }

        /// <summary>Difunde una línea de chat a todos los que están en el mundo.</summary>
        public (bool Bien, int Cuantos) Broadcast(string texto)
        {
            var raiz = Post("admin/difundir", new { token = Token, texto });
            if (raiz == null) return (false, 0);
            bool bien = raiz.Value.TryGetProperty("bien", out var b) && b.GetBoolean();
            int cuantos = raiz.Value.TryGetProperty("cuantos", out var n) ? n.GetInt32() : 0;
            return (bien, cuantos);
        }

        /// <summary>El registro del servidor desde una línea dada, para la pestaña en vivo.</summary>
        public string ServerLog(long desde)
        {
            try
            {
                using var respuesta = _http.PostAsync(Url("registro"),
                        new StringContent(JsonSerializer.Serialize(new { token = Token, desde }),
                                          Encoding.UTF8, "application/json")).Result;
                if (!respuesta.IsSuccessStatusCode) return "";
                return respuesta.Content.ReadAsStringAsync().Result;
            }
            catch { return ""; }
        }

        // ─── Por donde salen todas ────────────────────────────────────────────────────────────

        /// <summary>POST con JSON; devuelve el elemento raíz de la respuesta, o null si falló.</summary>
        private JsonElement? Post(string ruta, object cuerpo)
        {
            try
            {
                using var respuesta = _http.PostAsync(Url(ruta),
                        new StringContent(JsonSerializer.Serialize(cuerpo), Encoding.UTF8, "application/json"))
                    .Result;
                if (!respuesta.IsSuccessStatusCode) return null;
                return JsonDocument.Parse(respuesta.Content.ReadAsStringAsync().Result).RootElement.Clone();
            }
            catch { return null; }
        }
    }
}
