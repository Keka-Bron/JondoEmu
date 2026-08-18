using System;
using System.Net.Http;
using System.Text;
using System.Text.Json;

namespace Jondo.Unity.Launcher.Network
{
    /// <summary>
    /// El lado del lanzador del canal de mando: le habla al servidor por HTTP.
    ///
    /// Todo va por aquí, incluso cuando el servidor es este mismo proceso. Un solo camino en vez de
    /// dos —«si está en local llamo al método, y si no, por el cable»— porque el camino que sólo se
    /// usa a veces es el que se rompe sin que nadie se entere.
    ///
    /// Las llamadas son SÍNCRONAS a propósito: quien llama es la ventana, que ya las hacía
    /// síncronas cuando eran métodos normales, y así no hay que reescribirla entera. Por eso el
    /// tiempo de espera es corto: con el servidor caído la ventana no se puede quedar pillada.
    /// </summary>
    public static class ClienteDeControl
    {
        /// <summary>Cuánto se espera a una respuesta antes de dar al servidor por no disponible.</summary>
        private static readonly TimeSpan Paciencia = TimeSpan.FromSeconds(4);

        private static readonly HttpClient Cliente = new HttpClient { Timeout = Paciencia };

        private static string _secreto = "";

        /// <summary>A qué servidor. Hoy siempre el de esta máquina: el lanzador arranca el cliente.</summary>
        public static string Base => $"http://127.0.0.1:{Program.haapiPort}";

        /// <summary>Lo que ha contestado el servidor, o el silencio si no había nadie.</summary>
        public readonly struct Respuesta
        {
            public Respuesta(bool llego, int codigo, string json)
            {
                Llego = llego; Codigo = codigo; Json = json;
            }

            /// <summary>Falso cuando no hubo respuesta: no hay servidor, o no contesta a tiempo.</summary>
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
        /// Manda una orden. Si se la rechazan por el secreto, lo vuelve a leer del fichero y lo
        /// intenta una vez más: el servidor reparte un secreto nuevo en cada arranque, así que un
        /// lanzador que llevara rato abierto se queda con el viejo en cuanto el servidor rearranca.
        /// </summary>
        public static Respuesta Pedir(string verbo, object? cuerpo = null)
        {
            if (_secreto.Length == 0) _secreto = ApiDeControl.LeerSecreto();

            var salida = Intentar(verbo, cuerpo);
            if (salida.Llego && salida.Codigo == 403)
            {
                string releido = ApiDeControl.LeerSecreto();
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
                string json = cuerpo == null ? "{}" : JsonSerializer.Serialize(cuerpo);
                using var peticion = new HttpRequestMessage(HttpMethod.Post, Base + ApiDeControl.Prefijo + verbo)
                {
                    Content = new StringContent(json, Encoding.UTF8, "application/json"),
                };
                if (_secreto.Length > 0) peticion.Headers.Add(ApiDeControl.Cabecera, _secreto);

                using var respuesta = Cliente.Send(peticion);
                string texto = respuesta.Content.ReadAsStringAsync().GetAwaiter().GetResult();
                return new Respuesta(true, (int)respuesta.StatusCode, texto);
            }
            catch
            {
                // No hay servidor al otro lado, o no ha contestado a tiempo. No es un error a
                // gritos: es la situación normal mientras el servidor arranca.
                return new Respuesta(false, 0, "");
            }
        }

        /// <summary>
        /// ¿Hay alguien al otro lado? Es la comprobación previa a arrancar un cliente.
        ///
        /// Importa más de lo que parece: el mod del cliente decide UNA sola vez, al arrancar, si
        /// redirige al emulador, y lo decide sondeando este mismo puerto con 100 ms de paciencia.
        /// Si no contesta, el cliente no da ningún error: se va a los servidores de Ankama. Así que
        /// antes de lanzar hay que saber que el 8888 está contestando de verdad.
        /// </summary>
        public static bool ServidorVivo()
        {
            var estado = Pedir("estado");
            return estado.Bien;
        }

        /// <summary>Espera a que el servidor conteste, hasta un tope. Devuelve si llegó a hacerlo.</summary>
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
