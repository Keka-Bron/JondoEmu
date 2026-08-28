using System;
using System.Security.Cryptography;
using System.Text;

namespace Jondo.Unity.Server.Managers
{
    /// <summary>
    /// Las contraseñas, cifradas.
    ///
    /// Hasta ahora se guardaban tal cual se escribían, y la comparación la hacía el propio SQL
    /// («AND Password = $pass»). Eso quiere decir que cualquiera con el fichero auth.db delante
    /// —una copia de seguridad, el zip que se pasa a un amigo, un volcado por error— tenía las
    /// claves de todo el mundo, y como la gente repite contraseña, no sólo las de aquí.
    ///
    /// El formato es de una sola línea y lleva dentro todo lo que hace falta para comprobarla:
    ///
    ///     pbkdf2$&lt;vueltas&gt;$&lt;sal en base64&gt;$&lt;resumen en base64&gt;
    ///
    /// Guardar las vueltas dentro es lo que permite subirlas más adelante sin romper lo ya
    /// guardado: cada clave se comprueba con las suyas, y la siguiente vez que alguien entra se
    /// vuelve a escribir con las de ahora.
    ///
    /// Lo que ya estaba escrito en claro SIGUE VALIENDO: se reconoce porque no empieza por
    /// «pbkdf2$», se compara como antes y, si acierta, se reescribe cifrada en ese momento. Así
    /// la base vieja se convierte sola según entra cada uno, sin dejar a nadie fuera y sin tener
    /// que pedirle a nadie que cambie la suya.
    /// </summary>
    public static class Claves
    {
        private const string Marca = "pbkdf2$";
        private const int Vueltas = 210_000;   // lo que recomienda OWASP para PBKDF2-SHA256
        private const int BytesDeSal = 16;
        private const int BytesDeResumen = 32;

        /// <summary>¿Está esto ya cifrado, o es de las de antes?</summary>
        public static bool EstaCifrada(string? guardado)
            => !string.IsNullOrEmpty(guardado) && guardado.StartsWith(Marca, StringComparison.Ordinal);

        /// <summary>Lo que hay que meter en la columna Password.</summary>
        public static string Cifrar(string clave)
        {
            byte[] sal = RandomNumberGenerator.GetBytes(BytesDeSal);
            byte[] resumen = Rdkf2(clave, sal, Vueltas);
            return $"{Marca}{Vueltas}${Convert.ToBase64String(sal)}${Convert.ToBase64String(resumen)}";
        }

        /// <summary>
        /// ¿Es ésta la contraseña? Devuelve además si hay que reescribirla, o porque estaba en
        /// claro o porque se cifró con menos vueltas de las que se usan hoy.
        /// </summary>
        public static bool Comprueba(string clave, string? guardado, out bool hayQueReescribir)
        {
            hayQueReescribir = false;
            if (string.IsNullOrEmpty(guardado)) return false;

            if (!EstaCifrada(guardado))
            {
                // De las de antes. Se compara en tiempo fijo igual, que cuesta lo mismo.
                bool acierta = IgualesSinDelatar(
                    Encoding.UTF8.GetBytes(clave), Encoding.UTF8.GetBytes(guardado));
                hayQueReescribir = acierta;
                return acierta;
            }

            // pbkdf2$vueltas$sal$resumen
            string[] partes = guardado.Split('$');
            if (partes.Length != 4) return false;
            if (!int.TryParse(partes[1], out int vueltas) || vueltas <= 0) return false;

            byte[] sal, esperado;
            try
            {
                sal = Convert.FromBase64String(partes[2]);
                esperado = Convert.FromBase64String(partes[3]);
            }
            catch (FormatException)
            {
                return false;
            }
            if (sal.Length == 0 || esperado.Length == 0) return false;

            byte[] mio = Rdkf2(clave, sal, vueltas, esperado.Length);
            if (!IgualesSinDelatar(mio, esperado)) return false;

            hayQueReescribir = vueltas < Vueltas;
            return true;
        }

        private static byte[] Rdkf2(string clave, byte[] sal, int vueltas, int largo = BytesDeResumen)
            => Rfc2898DeriveBytes.Pbkdf2(
                   Encoding.UTF8.GetBytes(clave), sal, vueltas, HashAlgorithmName.SHA256, largo);

        /// <summary>
        /// Comparación que tarda lo mismo acierte o falle. Con «==» se puede averiguar la clave
        /// letra a letra midiendo cuánto tarda en contestar.
        /// </summary>
        private static bool IgualesSinDelatar(byte[] a, byte[] b)
            => a.Length == b.Length && CryptographicOperations.FixedTimeEquals(a, b);
    }
}
