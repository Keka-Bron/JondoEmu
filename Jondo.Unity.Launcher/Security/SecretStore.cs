using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;

namespace Jondo.Unity.Launcher.Security
{
    /// <summary>
    /// Encrypts at rest what the launcher stores on disk.
    /// </summary>
    /// <remarks>
    /// <b>What there was before.</b> The stored accounts -- with their session credential inside -- went to
    /// <c>%APPDATA%\Jondo\lanzador.cfg</c> run through Base64. Base64 encrypts nothing: it is a way of
    /// writing, not a secret. Anyone with access to the file, or anything that sneaks into
    /// the profile, took the credentials of the eight accounts in the clear.
    ///
    /// <b>What there is now.</b> On Windows, DPAPI with user scope: the key is kept by the system,
    /// tied to the Windows account, and the file copied to another machine or opened by another user
    /// does not decrypt. It is the same a browser does with saved passwords.
    ///
    /// Outside Windows there is a fallback with AES-GCM and a key in a separate file with owner-only
    /// permissions. <b>It is weaker and it is worth saying so</b>: whoever can read the key
    /// file can decrypt. It is there because the launcher is no longer tied to Windows and it is better than
    /// leaving the fallback in the clear, not because it is equivalent to DPAPI.
    ///
    /// If something cannot be decrypted -- a file from another machine, a recreated profile, a lost key --
    /// it is discarded and one starts from scratch, which is also what Bubble's client does with its
    /// session. Trying to rescue it ends in a half state nobody knows how to interpret.
    /// </remarks>
    internal static class SecretStore
    {
        /// <summary>Mark saying the content is encrypted and with what.</summary>
        private const string DpapiPrefix = "dpapi:";
        private const string AesPrefix = "aesgcm:";

        private static bool OnWindows => RuntimeInformation.IsOSPlatform(OSPlatform.Windows);

        /// <summary>The ciphertext in Base64, ready to write to the file.</summary>
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
                // Without encryption it is NOT stored. Before, this ended up in Base64 and it looked like something was
                // protected; returning empty makes the session not be remembered, which is worse to use
                // and much better to defend.
                Program.LogDebug($"[Lanzador] No se ha podido cifrar lo que se iba a guardar: {ex.Message}");
                return "";
            }
        }

        /// <summary>What comes back, or an empty string if it cannot be decrypted.</summary>
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

            // Without a prefix it is from the previous version: plain Base64. It is read ONCE so as not to
            // throw out of the launcher whoever already had it, and on saving it comes back encrypted.
            try
            {
                return Encoding.UTF8.GetString(Convert.FromBase64String(stored));
            }
            catch
            {
                return "";
            }
        }

        /// <summary>Whether what is stored comes from the version that did not encrypt.</summary>
        public static bool LooksUnprotected(string stored)
            => !string.IsNullOrWhiteSpace(stored)
               && !stored.StartsWith(DpapiPrefix, StringComparison.Ordinal)
               && !stored.StartsWith(AesPrefix, StringComparison.Ordinal);

        // ─── The fallback outside Windows ───────────────────────────────────────

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
                // Only the owner. Without this the key stays readable for any account on the
                // machine and the encryption defends against nothing.
                File.SetUnixFileMode(KeyPath, UnixFileMode.UserRead | UnixFileMode.UserWrite);
            }
            catch
            {
                // On systems without POSIX permissions there is nothing to adjust.
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
