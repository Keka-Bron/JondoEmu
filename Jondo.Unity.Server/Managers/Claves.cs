using System;
using System.Security.Cryptography;
using System.Text;

namespace Jondo.Unity.Server.Managers
{
    /// <summary>
    /// Passwords, hashed.
    ///
    /// Until now they were stored as typed, and the comparison was done by the SQL itself («AND
    /// Password = $pass»). That means anybody with the auth.db file in front of him -- a backup, the zip
    /// passed to a friend, a dump by mistake -- had everybody's passwords, and since people reuse
    /// passwords, not only the ones for here.
    ///
    /// The format is a single line and carries inside everything needed to check it:
    ///
    ///     pbkdf2$&lt;rounds&gt;$&lt;salt in base64&gt;$&lt;hash in base64&gt;
    ///
    /// Storing the rounds inside is what allows raising them later without breaking what is already
    /// stored: each password is checked with its own, and the next time somebody signs in it is written
    /// again with today's.
    ///
    /// What was already written in clear IS STILL VALID: it is recognised because it does not start with
    /// «pbkdf2$», it is compared as before and, if it matches, it is rewritten hashed at that moment.
    /// That way the old database converts itself as each one signs in, without locking anybody out and
    /// without having to ask anybody to change theirs.
    /// </summary>
    public static class Claves
    {
        private const string Marca = "pbkdf2$";
        private const int Vueltas = 210_000;   // what OWASP recommends for PBKDF2-SHA256
        private const int BytesDeSal = 16;
        private const int BytesDeResumen = 32;

        /// <summary>Is this already hashed, or one of the old ones?</summary>
        public static bool EstaCifrada(string? guardado)
            => !string.IsNullOrEmpty(guardado) && guardado.StartsWith(Marca, StringComparison.Ordinal);

        /// <summary>What has to go into the Password column.</summary>
        public static string Cifrar(string clave)
        {
            byte[] sal = RandomNumberGenerator.GetBytes(BytesDeSal);
            byte[] resumen = Rdkf2(clave, sal, Vueltas);
            return $"{Marca}{Vueltas}${Convert.ToBase64String(sal)}${Convert.ToBase64String(resumen)}";
        }

        /// <summary>
        /// Is this the password? Also returns whether it has to be rewritten, either because it was in clear
        /// or because it was hashed with fewer rounds than are used today.
        /// </summary>
        public static bool Comprueba(string clave, string? guardado, out bool hayQueReescribir)
        {
            hayQueReescribir = false;
            if (string.IsNullOrEmpty(guardado)) return false;

            if (!EstaCifrada(guardado))
            {
                // One of the old ones. It is compared in constant time all the same, which costs the same.
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
        /// A comparison that takes the same time whether it matches or fails. With «==» the password can be
        /// found letter by letter by measuring how long it takes to answer.
        /// </summary>
        private static bool IgualesSinDelatar(byte[] a, byte[] b)
            => a.Length == b.Length && CryptographicOperations.FixedTimeEquals(a, b);
    }
}
