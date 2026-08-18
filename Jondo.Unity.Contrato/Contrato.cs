using System;
using System.IO;
using System.Security.Cryptography;
using System.Threading;

namespace Jondo.Unity.Launcher
{
    /// <summary>
    /// El contrato entre el servidor y el lanzador. Es lo ÚNICO que los dos conocen.
    ///
    /// El lanzador se reparte a los jugadores, así que no puede llevar dentro el servidor: ni la
    /// base de datos, ni los mapas, ni los manejadores de protocolo, ni el catálogo de efectos.
    /// Son dos ejecutables de verdad y esta biblioteca es la única pieza que viaja en los dos.
    ///
    /// Por eso aquí no hay lógica: nombres de rutas, el sitio del secreto y los códigos con los que
    /// el servidor dice qué ha pasado. Todo lo que se le añada a este fichero acaba en el
    /// ordenador de todos los jugadores, así que conviene que sea poco.
    /// </summary>
    public static class Contrato
    {
        /// <summary>La versión que se publica en el estado.</summary>
        public const string Version = "3.6.10.10";

        /// <summary>La dirección de origen cuando la petición sale de esta misma máquina.</summary>
        public const string LocalIp = "127.0.0.1";

        /// <summary>
        /// El puerto por el que se manda.
        ///
        /// Es el del HAAPI a propósito: es el que el mod del cliente sondea para decidir si redirige
        /// al emulador, así que «este puerto contesta» es exactamente la señal de vida que el
        /// lanzador necesita antes de arrancar un cliente.
        /// </summary>
        public const int Puerto = 8888;

        /// <summary>El prefijo de todas las rutas de mando.</summary>
        public const string Prefijo = "/api/";

        /// <summary>La cabecera por la que viaja el secreto.</summary>
        public const string Cabecera = "X-Jondo-Control";

        // ─── Los códigos ────────────────────────────────────────────────────────────────────
        //
        // El servidor dice QUÉ ha pasado; el lanzador decide CÓMO contárselo a la persona y en qué
        // idioma. Si el servidor mandara la frase hecha tendría que saber el idioma del usuario, y
        // para eso tendría que leer un fichero de preferencias del escritorio de alguien.

        public const string MotivoSesionCaducada = "sesion-caducada";
        public const string MotivoCuentaYaAbierta = "cuenta-ya-abierta";
        public const string MotivoTopeDeClientes = "tope-de-clientes";

        // ─── El secreto ─────────────────────────────────────────────────────────────────────
        //
        // El canal está en localhost, pero en localhost está cualquier cosa que corra en la
        // máquina, y por aquí se crean cuentas y se arrancan clientes. Quien borró estas rutas la
        // primera vez habló de «una puerta abierta encima», y tenía razón.
        //
        // El servidor se inventa un secreto en cada arranque y lo deja escrito en el perfil del
        // usuario; el lanzador lo lee de ahí. Nadie lo teclea y no sale de la máquina.

        /// <summary>Dónde vive el secreto: en el perfil, junto a las preferencias del lanzador.</summary>
        public static string FicheroDelSecreto => Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "Jondo", "control.secreto");

        /// <summary>Reparte un secreto nuevo y lo deja escrito. Lo llama el servidor al arrancar.</summary>
        public static string NuevoSecreto()
        {
            string secreto = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(FicheroDelSecreto)!);
                File.WriteAllText(FicheroDelSecreto, secreto);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[Control] No se ha podido escribir el secreto: {ex.Message}");
            }
            return secreto;
        }

        /// <summary>El secreto que dejó escrito el servidor, o cadena vacía si no hay ninguno.</summary>
        public static string LeerSecreto()
        {
            try
            {
                return File.Exists(FicheroDelSecreto) ? File.ReadAllText(FicheroDelSecreto).Trim() : "";
            }
            catch { return ""; }
        }

        /// <summary>Compara dos secretos sin que el tiempo de la comparación diga nada.</summary>
        public static bool MismoSecreto(string? uno, string? otro)
            => !string.IsNullOrEmpty(uno) && !string.IsNullOrEmpty(otro) &&
               CryptographicOperations.FixedTimeEquals(
                   System.Text.Encoding.UTF8.GetBytes(uno),
                   System.Text.Encoding.UTF8.GetBytes(otro));

        // ─── Uno de cada, y sólo uno ────────────────────────────────────────────────────────
        //
        // No había ningún guardia de instancia: dos servidores se peleaban por el 8888 y por la
        // tubería con nombre "15881", y el segundo se moría escribiendo el error en una consola que
        // en un WinExe no existe. Doble clic que no hacía nada y nadie sabía por qué.

        private static Mutex? _candado;

        /// <summary>Coge el sitio de este programa. Falso si ya lo tenía otro.</summary>
        public static bool CogerElSitio(string nombre)
        {
            try
            {
                _candado = new Mutex(initiallyOwned: true, @"Local\" + nombre, out bool nuestro);
                if (!nuestro)
                {
                    _candado.Dispose();
                    _candado = null;
                }
                return nuestro;
            }
            catch
            {
                // Si el candado no se puede coger, mejor dejar arrancar que impedirlo: el fallo de
                // los puertos avisa después, y ahora avisa bien.
                return true;
            }
        }

        public static void SoltarElSitio()
        {
            try { _candado?.ReleaseMutex(); } catch { }
            _candado?.Dispose();
            _candado = null;
        }
    }
}
