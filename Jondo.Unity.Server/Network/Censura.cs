using System;
using System.Text.RegularExpressions;

namespace Jondo.Unity.Server.Network
{
    /// <summary>
    /// Tapa los secretos antes de que lleguen al registro.
    ///
    /// El registro no es un sitio privado: va a la consola, a «logs/emulator_console.log» y al
    /// buffer que sirve «/api/registro» a cualquiera con rol de administrador. Y por ahí pasaban
    /// en claro las contraseñas de entrar y crear cuenta, y los identificadores de sesión del
    /// Thrift, que valen para suplantar a alguien sin saber su clave.
    ///
    /// Se hace por NOMBRE DE CAMPO y no por ruta a propósito. Una lista de rutas que no se anotan
    /// hay que acordarse de ampliarla, y al añadir la siguiente ruta con contraseña nadie se
    /// acuerda; así, un campo que se llame «clave» queda tapado venga de donde venga.
    /// </summary>
    public static class Censura
    {
        /// <summary>Los nombres cuyo valor no se escribe nunca.</summary>
        private static readonly string[] Secretos =
        {
            "clave", "password", "contrasena", "contraseña", "pass",
            "token", "secreto", "secret", "ticket", "hash", "gameSession", "sessionId"
        };

        private static readonly Regex EnJson = new Regex(
            @"(""(?:" + string.Join("|", Secretos) + @")""\s*:\s*)""[^""]*""",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);

        /// <summary>
        /// Un cuerpo JSON con los valores secretos sustituidos. El nombre del campo se deja para
        /// que se siga viendo la forma del mensaje, que es para lo que sirve el registro.
        /// </summary>
        public static string Cuerpo(string? json)
        {
            if (string.IsNullOrEmpty(json)) return json ?? "";
            return EnJson.Replace(json, "$1\"***\"");
        }

        /// <summary>
        /// Un valor suelto —el que llega como argumento de Thrift, sin json alrededor—. Se dejan
        /// los cuatro primeros caracteres porque hacen falta para seguir una sesión por el
        /// registro, y con cuatro no se adivina el resto.
        /// </summary>
        public static string Valor(string? secreto)
        {
            if (string.IsNullOrEmpty(secreto)) return "(vacío)";
            return secreto.Length <= 4 ? "***" : secreto.Substring(0, 4) + "***";
        }

        /// <summary>
        /// ¿Lleva este texto algo que no debería escribirse? Lo usa la guardia de regresión para
        /// mirar el registro de verdad, no sólo el código.
        /// </summary>
        public static bool Delata(string? texto)
        {
            if (string.IsNullOrEmpty(texto)) return false;
            return EnJson.IsMatch(texto);
        }
    }
}
