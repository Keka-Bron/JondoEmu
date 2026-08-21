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
    public static class ControlClient
    {
        /// <summary>Cuánto se espera a una respuesta antes de dar al servidor por no disponible.</summary>
        private static readonly TimeSpan Paciencia = TimeSpan.FromSeconds(4);

        private static readonly HttpClient Cliente = new HttpClient { Timeout = Paciencia };

        private static string _secreto = "";

        /// <summary>
        /// La sesión con la que se habla, o cadena vacía si todavía no ha entrado nadie.
        ///
        /// Va en cada petición: desde que el servidor comprueba roles, es lo que dice QUIÉN pide
        /// las cosas. Antes esto lo hacía un secreto de la máquina, que no sirve en cuanto el
        /// lanzador está en el ordenador de otro.
        /// </summary>
        public static string Token { get; set; } = "";

        /// <summary>
        /// A qué servidor se le habla.
        ///
        /// Sale de las preferencias, no de un literal: por defecto esta misma máquina —jugar en
        /// local— y si no, la dirección que se haya puesto en el desplegable, que puede ser la de
        /// otro ordenador por Hamachi o la de una VPS.
        /// </summary>
        public static string Base
            => UI.LauncherPreferences.ControlBaseUrl;

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
            // Un intento y UN reintento de transporte. Pasaba esto: el HttpClient guarda las
            // conexiones para reutilizarlas, http.sys las cierra por su cuenta al rato de
            // inactividad, y la petición que llega justo después coje la conexión muerta —falla al
            // instante, con el servidor perfectamente en pie— y el lanzador le decía a la persona
            // «el servidor no responde» cuando sí respondía. Con el reintento, esa primera
            // tentative se descarta y la segunda sale por una conexión nueva y llega.
            //
            // Y ConnectionClose en cada petición: en localhost abrir la conexión cuesta nada y así
            // no hay nunca una guardada que pueda estar muerta. Es el mismo diagnóstico que el del
            // reintento, atajado de raíz.
            for (int intento = 0; ; intento++)
            {
                try
                {
                    string json = ConElToken(cuerpo);
                    using var peticion = new HttpRequestMessage(HttpMethod.Post, Base + Contract.Prefijo + verbo)
                    {
                        Content = new StringContent(json, Encoding.UTF8, "application/json"),
                        Headers = { ConnectionClose = true },
                    };
                    if (_secreto.Length > 0) peticion.Headers.Add(Contract.Cabecera, _secreto);

                    using var respuesta = Cliente.Send(peticion);
                    string texto = respuesta.Content.ReadAsStringAsync().GetAwaiter().GetResult();
                    return new Respuesta(true, (int)respuesta.StatusCode, texto);
                }
                catch
                {
                    // Sólo el fallo de transporte se reintenta, y una sola vez: los 4 segundos de
                    // paciencia son por petición, y no se doblan para nadie.
                    if (intento == 0) continue;

                    // No hay servidor al otro lado, o no ha contestado a tiempo. No es un error a
                    // gritos: es la situación normal mientras el servidor arranca.
                    return new Respuesta(false, 0, "");
                }
            }
        }

        /// <summary>
        /// El cuerpo de la petición, con el token de la sesión metido dentro.
        ///
        /// Se pone aquí y no en cada llamada para que no se pueda olvidar en ninguna: si falta, el
        /// servidor contesta 401 y el lanzador se queda tonto sin decir por qué. Si quien llama ya
        /// trae su propio token —el caso de arrancar un cliente de una cuenta concreta del
        /// equipo— manda el suyo, que no tiene por qué ser el de la sesión de la ventana.
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
        /// ¿Hay alguien al otro lado? Es la comprobación previa a arrancar un cliente.
        ///
        /// Importa más de lo que parece: el mod del cliente decide UNA sola vez, al arrancar, si
        /// redirige al emulador, y lo decide sondeando este mismo puerto con 100 ms de paciencia.
        /// Si no contesta, el cliente no da ningún error: se va a los servidores de Ankama. Así que
        /// antes de lanzar hay que saber que el 8888 está contestando de verdad.
        ///
        /// Y como esta es justamente la comprobación que no puede darse por vencida a la primera:
        /// se le pregunta DOS veces antes de decir que no hay nadie. Una sola contestación basta
        /// para el sí; para el no hacen falta dos silencios seguidos, que es lo que cuesta decirle
        /// a alguien que su servidor —que está abierto en la otra ventana— no responde.
        /// </summary>
        public static bool ServidorVivo()
        {
            var estado = Pedir("estado");
            if (estado.Bien) return true;

            // Some launcher builds still see the server as "offline" when the body is empty or the
            // first HTTP attempt races the listener startup. Accept a valid JSON answer even if the
            // transport had to retry once: the whole point of this probe is to answer "is the HAAPI
            // alive?", not to reject a perfectly valid status payload.
            if (estado.Llego && estado.Codigo == 200 && estado.Json.Length > 0)
            {
                try
                {
                    using var doc = JsonDocument.Parse(estado.Json);
                    if (doc.RootElement.TryGetProperty("enLinea", out var enLinea) &&
                        enLinea.ValueKind == JsonValueKind.True)
                    {
                        return true;
                    }
                }
                catch { }
            }

            var segunda = Pedir("estado");
            if (segunda.Bien) return true;
            if (segunda.Llego && segunda.Codigo == 200 && segunda.Json.Length > 0)
            {
                try
                {
                    using var doc = JsonDocument.Parse(segunda.Json);
                    return doc.RootElement.TryGetProperty("enLinea", out var enLinea) &&
                           enLinea.ValueKind == JsonValueKind.True;
                }
                catch { }
            }
            return false;
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
