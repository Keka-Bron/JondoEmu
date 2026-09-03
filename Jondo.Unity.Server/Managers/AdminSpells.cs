using Jondo.Unity.Launcher;

namespace Jondo.Unity.Server.Managers
{
    /// <summary>
    /// Los hechizos que sólo tiene quien administra el servidor.
    /// </summary>
    /// <remarks>
    /// No son inventados: están en los datos del propio cliente, con su nombre, su icono y su
    /// descripción, y son los que Ankama reserva a su equipo. Aquí sólo se decide a quién se le
    /// declaran, que es lo que el cliente no puede saber por su cuenta.
    ///
    /// La comprobación es SIEMPRE contra la base y por cuenta, nunca contra nada que mande el
    /// cliente, igual que la de los comandos del chat.
    /// </remarks>
    public static class AdminSpells
    {
        /// <summary>Doom de Masas: mata todo lo que coge la zona.</summary>
        /// <remarks>
        /// Sacado del catálogo del cliente: nombre «Doom de Masas», adminName «Doom de masse», un
        /// solo grado, 1 PA, alcance 0, y dos efectos — el 141 «Mata al objetivo» sobre la zona y
        /// el 120, que devuelve el PA gastado. O sea que se puede encadenar sin quedarse sin
        /// puntos, que es justo lo que hace falta para saltarse una pelea de prueba.
        /// </remarks>
        public const int DoomDeMasas = 3450;

        /// <summary>Su único grado.</summary>
        public const int GradoDeDoom = 1;

        /// <summary>El rol a partir del cual se declara.</summary>
        public const int HaceFalta = Roles.Administrador;

        /// <summary>¿A esta cuenta se le declaran los hechizos de administración?</summary>
        public static bool Para(long accountId)
        {
            if (accountId <= 0) return false;
            return Roles.AlMenos(DatabaseManager.GetAccountRole(accountId), HaceFalta);
        }
    }
}
