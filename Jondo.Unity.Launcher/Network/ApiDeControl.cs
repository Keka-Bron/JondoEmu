using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using System.Text.Json;

namespace Jondo.Unity.Launcher.Network
{
    /// <summary>
    /// Por dónde le habla el lanzador al servidor.
    ///
    /// Esto existió y se borró. Lo dice el comentario que quedó en <see cref="HaapiServer"/>: las
    /// rutas /api/login, /api/register, /api/launch, /api/status y /api/logs vivían ahí y se
    /// quitaron al pasar de la interfaz web a la ventana nativa, porque la ventana llamaba a
    /// LauncherService directamente y aquello era peso muerto. En cuanto el lanzador y el servidor
    /// son dos procesos vuelve a hacer falta, así que vuelve.
    ///
    /// Va colgada del HAAPI, en el 8888, y no en un puerto nuevo, por tres razones:
    ///
    ///   * es el puerto que el mod del cliente sondea para decidir si redirige al emulador
    ///     (JondoFix/Class1.cs:471), así que «el 8888 contesta» es exactamente la señal de vida que
    ///     el lanzador necesita antes de arrancar un cliente;
    ///   * el HAAPI ya es un HttpListener atado a localhost y a 127.0.0.1, y a nada más;
    ///   * es donde estaba.
    ///
    /// LO QUE AQUÍ SE DECIDE ES DEL SERVIDOR. Este fichero no sabe nada de ventanas: recibe texto,
    /// llama a la base y al registro de lanzamientos, y devuelve texto. Los mensajes para el
    /// usuario NO se traducen aquí —viajan como código— porque el idioma es del lanzador.
    /// </summary>
    public static class ApiDeControl
    {
        /// <summary>Las rutas y la cabecera salen del contrato, que es lo que comparten los dos.</summary>
        public const string Prefijo = Contrato.Prefijo;

        // ─── El secreto ─────────────────────────────────────────────────────────────────────
        //
        // Uno por arranque: así un lanzador de una sesión anterior no se queda con llave de la de
        // ahora. Lo guarda el contrato, que es quien sabe dónde se escribe y quién lo lee.

        private static string _secreto = "";

        /// <summary>Reparte un secreto nuevo y lo deja escrito. Lo llama el servidor al arrancar.</summary>
        public static void NuevoSecreto() => _secreto = Contrato.NuevoSecreto();

        private static bool Autorizada(string? traido) => Contrato.MismoSecreto(traido, _secreto);

        // ─── Las respuestas ─────────────────────────────────────────────────────────────────

        /// <summary>Lo que sale por el cable: un código de estado y un cuerpo JSON.</summary>
        public readonly struct Respuesta
        {
            public Respuesta(int codigo, string json) { Codigo = codigo; Json = json; }
            public int Codigo { get; }
            public string Json { get; }
        }

        private static Respuesta Bien(object cuerpo)
            => new Respuesta(200, JsonSerializer.Serialize(cuerpo));

        private static Respuesta Mal(int codigo, string motivo)
            => new Respuesta(codigo, JsonSerializer.Serialize(new { error = motivo }));

        /// <summary>
        /// Contesta una petición de mando. Devuelve null si la ruta no es de aquí, para que el
        /// HAAPI siga con lo suyo.
        /// </summary>
        public static Respuesta? Responder(string ruta, string metodo, string cuerpo, string? secreto)
        {
            if (!ruta.StartsWith(Prefijo, StringComparison.Ordinal)) return null;

            if (!Autorizada(secreto))
            {
                Console.WriteLine($"[Control] Rechazada una petición a {ruta} sin el secreto bueno.");
                return Mal(403, "secreto");
            }

            try
            {
                switch (ruta)
                {
                    case Prefijo + "estado": return Estado();
                    case Prefijo + "activos": return Activos();
                    case Prefijo + "registro": return Registro(cuerpo);
                    case Prefijo + "entrar": return Entrar(cuerpo);
                    case Prefijo + "crear-cuenta": return CrearCuenta(cuerpo);
                    case Prefijo + "recordar-token": return RecordarToken(cuerpo);
                    case Prefijo + "lanzamiento": return Lanzamiento(cuerpo);
                    case Prefijo + "fin-de-lanzamiento": return FinDeLanzamiento(cuerpo);
                    case Prefijo + "apagar": return Apagar();
                    default: return Mal(404, "ruta");
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[Control] {ruta} ha reventado: {ex.Message}");
                return Mal(500, ex.Message);
            }
        }

        // ─── Cada verbo ─────────────────────────────────────────────────────────────────────

        /// <summary>
        /// Si esto contesta, el servidor está vivo. Aun así se dice lo que hay dentro, porque una
        /// base a medias con los puertos abiertos también es un problema.
        /// </summary>
        private static Respuesta Estado() => Bien(new
        {
            enLinea = ServiciosEnPie() && BaseEnPie(),
            base_ = BaseEnPie(),
            servicios = ServiciosEnPie(),
            version = Contrato.Version,
            proceso = Environment.ProcessId,
        });

        private static bool BaseEnPie() => File.Exists(Paths.AuthDb) && File.Exists(Paths.WorldDb);

        private static bool ServiciosEnPie() => ZaapServer.IsRunning && GameServerProxy.IsRunning;

        /// <summary>Quién tiene cliente abierto ahora mismo, y cuántos caben.</summary>
        private static Respuesta Activos()
        {
            var cuentas = new List<long>(ClientLaunchRegistry.ActiveAccounts);
            return Bien(new
            {
                maximo = ClientLaunchRegistry.MaximumClients,
                cuantos = cuentas.Count,
                cuentas,
            });
        }

        /// <summary>Las líneas de consola desde la que ya tenga el lanzador.</summary>
        private static Respuesta Registro(string cuerpo)
        {
            long desde = Numero(cuerpo, "desde");
            return new Respuesta(200, ConsoleLogBuffer.GetLogsJson(desde));
        }

        private static Respuesta Entrar(string cuerpo)
        {
            string usuario = Texto(cuerpo, "usuario");
            string clave = Texto(cuerpo, "clave");
            string ip = Texto(cuerpo, "ip");
            if (ip.Length == 0) ip = Contrato.LocalIp;

            if (!DatabaseManager.ValidateAccountCredentials(usuario, clave, ip, out var cuenta, out string fallo)
                || cuenta == null)
            {
                return Bien(new { bien = false, motivo = fallo });
            }

            string token = Guid.NewGuid().ToString("N");
            DatabaseManager.SetGameToken(cuenta.Id, token);
            ClientLaunchRegistry.RegisterToken(cuenta.Id, token);

            return Bien(new
            {
                bien = true,
                token,
                apodo = cuenta.Nickname ?? "",
                cuenta = cuenta.Id,
            });
        }

        private static Respuesta CrearCuenta(string cuerpo)
        {
            string usuario = Texto(cuerpo, "usuario");
            string clave = Texto(cuerpo, "clave");
            string apodo = Texto(cuerpo, "apodo");
            string ip = Texto(cuerpo, "ip");
            if (ip.Length == 0) ip = Contrato.LocalIp;

            bool bien = DatabaseManager.RegisterNewAccount(usuario, clave, apodo, ip, out string fallo);
            return Bien(new { bien, motivo = fallo });
        }

        /// <summary>
        /// El lanzador recuerda una sesión de la vez anterior y quiere que el servidor vuelva a
        /// dar por bueno ese token. Sólo se acepta si la base lo reconoce: el lanzador no puede
        /// inventarse la cuenta que quiera.
        /// </summary>
        private static Respuesta RecordarToken(string cuerpo)
        {
            long cuenta = Numero(cuerpo, "cuenta");
            string token = Texto(cuerpo, "token");
            if (cuenta <= 0 || token.Length == 0) return Bien(new { bien = false });

            long deLaBase = DatabaseManager.GetAccountIdByToken(token);
            if (deLaBase != cuenta) return Bien(new { bien = false });

            ClientLaunchRegistry.RegisterToken(cuenta, token);
            return Bien(new { bien = true });
        }

        /// <summary>
        /// Le da al lanzador el instanceId y el hash con los que arrancar un cliente.
        ///
        /// Aquí estaba el nudo: el hash se lo inventaba el lanzador y lo apuntaba en un diccionario
        /// en memoria que luego lee el Zaap. Con dos procesos, el lanzador apuntaba en su memoria y
        /// el Zaap miraba en la suya. Ahora lo reparte quien lo va a comprobar.
        /// </summary>
        private static Respuesta Lanzamiento(string cuerpo)
        {
            string token = Texto(cuerpo, "token");
            string idioma = Texto(cuerpo, "idioma");
            if (idioma.Length == 0) idioma = "es";

            long cuenta = ClientLaunchRegistry.ResolveToken(token);
            if (cuenta <= 0) return Bien(new { bien = false, motivo = MotivoSesionCaducada });

            try
            {
                var lanzamiento = ClientLaunchRegistry.Register(cuenta, token, Guid.NewGuid().ToString("N"), idioma);
                return Bien(new
                {
                    bien = true,
                    instancia = lanzamiento.InstanceId,
                    hash = lanzamiento.Hash,
                    cuenta,
                    idioma,
                });
            }
            catch (InvalidOperationException ex)
            {
                // Register rechaza por dos motivos y los dos son mensajes para el usuario. Viajan
                // como código; la frase la pone el lanzador en su idioma.
                return Bien(new { bien = false, motivo = ex.Message });
            }
        }

        /// <summary>El lanzador avisa de que el cliente de esa cuenta se ha cerrado.</summary>
        private static Respuesta FinDeLanzamiento(string cuerpo)
        {
            long cuenta = Numero(cuerpo, "cuenta");
            if (cuenta > 0) ClientLaunchRegistry.RemoveByAccount(cuenta);
            return Bien(new { bien = true });
        }

        /// <summary>
        /// Apaga el servidor, pero DESPUÉS de haber contestado.
        ///
        /// Llamando a RequestShutdown aquí mismo, el proceso se moría —y con él el HttpListener—
        /// antes de que la respuesta saliera por el cable: el lanzador se quedaba esperando y daba
        /// el apagado por fallido justo cuando había funcionado. Medido: la petición volvía con el
        /// cuerpo vacío.
        /// </summary>
        private static Respuesta Apagar()
        {
            Console.WriteLine("[Control] El lanzador pide apagar el servidor.");
            _ = System.Threading.Tasks.Task.Run(async () =>
            {
                await System.Threading.Tasks.Task.Delay(300);
                Program.RequestShutdown("orden del lanzador");
            });
            return Bien(new { bien = true });
        }

        /// <summary>Códigos de motivo. Son códigos, no frases: el idioma lo pone el lanzador.</summary>
        public const string MotivoSesionCaducada = "sesion-caducada";
        public const string MotivoCuentaYaAbierta = "cuenta-ya-abierta";
        public const string MotivoTopeDeClientes = "tope-de-clientes";

        // ─── Leer el cuerpo sin ceremonia ───────────────────────────────────────────────────

        private static string Texto(string json, string campo)
        {
            try
            {
                using var doc = JsonDocument.Parse(json.Length == 0 ? "{}" : json);
                return doc.RootElement.TryGetProperty(campo, out var v) && v.ValueKind == JsonValueKind.String
                    ? (v.GetString() ?? "")
                    : "";
            }
            catch { return ""; }
        }

        private static long Numero(string json, string campo)
        {
            try
            {
                using var doc = JsonDocument.Parse(json.Length == 0 ? "{}" : json);
                return doc.RootElement.TryGetProperty(campo, out var v) && v.ValueKind == JsonValueKind.Number
                    ? v.GetInt64()
                    : 0;
            }
            catch { return 0; }
        }
    }
}
