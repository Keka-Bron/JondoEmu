using System;
using System.Text.RegularExpressions;

namespace Jondo.Unity.Server.Network
{
    /// <summary>
    /// Covers up secrets before they reach the log.
    ///
    /// The log is not a private place: it goes to the console, to «logs/emulator_console.log» and to the
    /// buffer «/api/registro» serves to anyone with the administrator role. And through there went
    /// in the clear the passwords for logging in and creating an account, and the Thrift session
    /// identifiers, which are enough to impersonate someone without knowing his password.
    ///
    /// It is done by FIELD NAME and not by route on purpose. A list of routes that are not logged
    /// has to be remembered to be extended, and on adding the next route with a password nobody
    /// remembers; this way, a field called «clave» is covered wherever it comes from.
    /// </summary>
    public static class Censura
    {
        /// <summary>The names whose value is never written.</summary>
        private static readonly string[] Secretos =
        {
            "clave", "password", "contrasena", "contraseña", "pass",
            "token", "secreto", "secret", "ticket", "hash", "gameSession", "sessionId"
        };

        private static readonly Regex EnJson = new Regex(
            @"(""(?:" + string.Join("|", Secretos) + @")""\s*:\s*)""[^""]*""",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);

        /// <summary>
        /// A JSON body with the secret values replaced. The field name is left so that
        /// the message's shape can still be seen, which is what the log is for.
        /// </summary>
        public static string Cuerpo(string? json)
        {
            if (string.IsNullOrEmpty(json)) return json ?? "";
            return EnJson.Replace(json, "$1\"***\"");
        }

        /// <summary>
        /// A loose value —the one arriving as a Thrift argument, with no json around it—. The
        /// first four characters are left because they are needed to follow a session through the
        /// log, and with four the rest cannot be guessed.
        /// </summary>
        public static string Valor(string? secreto)
        {
            if (string.IsNullOrEmpty(secreto)) return "(vacío)";
            return secreto.Length <= 4 ? "***" : secreto.Substring(0, 4) + "***";
        }

        /// <summary>
        /// Does this text carry something that should not be written? The regression guard uses it to
        /// look at the real log, not only the code.
        /// </summary>
        public static bool Delata(string? texto)
        {
            if (string.IsNullOrEmpty(texto)) return false;
            return EnJson.IsMatch(texto);
        }
    }
}
