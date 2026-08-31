using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;

namespace Jondo.Unity.Launcher.Security
{
    /// <summary>
    /// Cifra en reposo lo que el lanzador guarda en disco.
    /// </summary>
    /// <remarks>
    /// <b>Que habia antes.</b> Las cuentas guardadas -- con su credencial de sesion dentro -- iban a
    /// <c>%APPDATA%\Jondo\lanzador.cfg</c> pasadas por Base64. Base64 no cifra nada: es una forma de
    /// escribir, no un secreto. Cualquiera con acceso al fichero, o cualquier cosa que se cuele en
    /// el perfil, se llevaba las credenciales de las ocho cuentas en claro.
    ///
    /// <b>Que hay ahora.</b> En Windows, DPAPI con ambito de usuario: la clave la guarda el sistema,
    /// atada a la cuenta de Windows, y el fichero copiado a otra maquina o abierto por otro usuario
    /// no se descifra. Es lo mismo que hace un navegador con las contrasenas guardadas.
    ///
    /// Fuera de Windows hay un respaldo con AES-GCM y una clave en un fichero aparte con permisos
    /// de solo-el-dueno. <b>Es mas debil y conviene decirlo</b>: quien pueda leer el fichero de
    /// clave puede descifrar. Se pone porque el lanzador ya no esta atado a Windows y es mejor que
    /// dejar el respaldo en claro, no porque sea equivalente a DPAPI.
    ///
    /// Si algo no se puede descifrar -- fichero de otra maquina, perfil recreado, clave perdida --
    /// se descarta y se empieza de cero, que es lo que hace tambien el cliente de Bubble con su
    /// sesion. Intentar rescatarlo acaba en un estado a medias que nadie sabe interpretar.
    /// </remarks>
    internal static class SecretStore
    {
        /// <summary>Marca de que el contenido esta cifrado y con que.</summary>
        private const string DpapiPrefix = "dpapi:";
        private const string AesPrefix = "aesgcm:";

        private static bool OnWindows => RuntimeInformation.IsOSPlatform(OSPlatform.Windows);

        /// <summary>El texto cifrado y en Base64, listo para escribirlo en el fichero.</summary>
        public static string Protect(string plain)
        {
            if (string.IsNullOrEmpty(plain)) return "";
            byte[] bytes = Encoding.UTF8.GetBytes(plain);

            try
            {
                if (OnWindows)
                {
                    byte[] cifrado = ProtectedData.Protect(bytes, null, DataProtectionScope.CurrentUser);
                    return DpapiPrefix + Convert.ToBase64String(cifrado);
                }
                return AesPrefix + Convert.ToBase64String(CifrarConAes(bytes));
            }
            catch (Exception ex)
            {
                // Sin cifrado NO se guarda. Antes esto acababa en Base64 y parecia que habia algo
                // protegido; devolver vacio hace que la sesion no se recuerde, que es peor de usar
                // y mucho mejor de defender.
                Program.LogDebug($"[Lanzador] No se ha podido cifrar lo que se iba a guardar: {ex.Message}");
                return "";
            }
        }

        /// <summary>Lo de vuelta, o cadena vacia si no se puede descifrar.</summary>
        public static string Unprotect(string stored)
        {
            if (string.IsNullOrWhiteSpace(stored)) return "";

            try
            {
                if (stored.StartsWith(DpapiPrefix, StringComparison.Ordinal))
                {
                    byte[] cifrado = Convert.FromBase64String(stored.Substring(DpapiPrefix.Length));
                    return Encoding.UTF8.GetString(
                        ProtectedData.Unprotect(cifrado, null, DataProtectionScope.CurrentUser));
                }

                if (stored.StartsWith(AesPrefix, StringComparison.Ordinal))
                {
                    byte[] cifrado = Convert.FromBase64String(stored.Substring(AesPrefix.Length));
                    return Encoding.UTF8.GetString(DescifrarConAes(cifrado));
                }
            }
            catch (Exception ex)
            {
                Program.LogDebug($"[Lanzador] Se descarta una sesion guardada que no se descifra: {ex.Message}");
                return "";
            }

            // Sin prefijo es lo de la version anterior: Base64 a secas. Se lee UNA vez para no
            // echar del lanzador a quien ya lo tenia, y al guardarse vuelve cifrado.
            try
            {
                return Encoding.UTF8.GetString(Convert.FromBase64String(stored));
            }
            catch
            {
                return "";
            }
        }

        /// <summary>Si lo guardado viene de la version que no cifraba.</summary>
        public static bool LooksUnprotected(string stored)
            => !string.IsNullOrWhiteSpace(stored)
               && !stored.StartsWith(DpapiPrefix, StringComparison.Ordinal)
               && !stored.StartsWith(AesPrefix, StringComparison.Ordinal);

        // ─── El respaldo de fuera de Windows ────────────────────────────────────

        private static string KeyPath => System.IO.Path.Combine(
            System.IO.Path.GetDirectoryName(UI.LauncherPreferences.Path) ?? ".", "clave.bin");

        private static byte[] LlaveDeRespaldo()
        {
            if (File.Exists(KeyPath))
            {
                byte[] guardada = File.ReadAllBytes(KeyPath);
                if (guardada.Length == 32) return guardada;
            }

            byte[] nueva = RandomNumberGenerator.GetBytes(32);
            string? carpeta = System.IO.Path.GetDirectoryName(KeyPath);
            if (!string.IsNullOrEmpty(carpeta)) Directory.CreateDirectory(carpeta);
            File.WriteAllBytes(KeyPath, nueva);

            try
            {
                // Sólo el dueño. Sin esto la clave queda legible para cualquier cuenta de la
                // máquina y el cifrado no defiende de nada.
                File.SetUnixFileMode(KeyPath, UnixFileMode.UserRead | UnixFileMode.UserWrite);
            }
            catch
            {
                // En sistemas sin permisos POSIX no hay nada que ajustar.
            }

            return nueva;
        }

        private static byte[] CifrarConAes(byte[] plain)
        {
            byte[] nonce = RandomNumberGenerator.GetBytes(12);
            byte[] tag = new byte[16];
            byte[] cifrado = new byte[plain.Length];

            using (var aes = new AesGcm(LlaveDeRespaldo(), 16))
            {
                aes.Encrypt(nonce, plain, cifrado, tag);
            }

            var salida = new byte[nonce.Length + tag.Length + cifrado.Length];
            Buffer.BlockCopy(nonce, 0, salida, 0, nonce.Length);
            Buffer.BlockCopy(tag, 0, salida, nonce.Length, tag.Length);
            Buffer.BlockCopy(cifrado, 0, salida, nonce.Length + tag.Length, cifrado.Length);
            return salida;
        }

        private static byte[] DescifrarConAes(byte[] blob)
        {
            if (blob.Length < 28) throw new CryptographicException("El bloque cifrado está incompleto.");

            var nonce = new byte[12];
            var tag = new byte[16];
            var cifrado = new byte[blob.Length - 28];
            Buffer.BlockCopy(blob, 0, nonce, 0, 12);
            Buffer.BlockCopy(blob, 12, tag, 0, 16);
            Buffer.BlockCopy(blob, 28, cifrado, 0, cifrado.Length);

            var plano = new byte[cifrado.Length];
            using var aes = new AesGcm(LlaveDeRespaldo(), 16);
            aes.Decrypt(nonce, cifrado, tag, plano);
            return plano;
        }
    }
}
