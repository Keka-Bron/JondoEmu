using Jondo.Unity.Launcher;

namespace Jondo.Unity.Server.Managers
{
    /// <summary>
    /// The spells only whoever administers the server has.
    /// </summary>
    /// <remarks>
    /// They are not made up: they are in the client's own data, with their name, their icon and their
    /// description, and they are the ones Ankama reserves for its team. Here only who they are declared
    /// to is decided, which is what the client cannot know by itself.
    ///
    /// The check is ALWAYS against the database and by account, never against anything the client sends,
    /// the same as the chat commands'.
    /// </remarks>
    public static class AdminSpells
    {
        /// <summary>Doom de Masas: kills everything the area catches.</summary>
        /// <remarks>
        /// Taken from the client's catalogue: name «Doom de Masas», adminName «Doom de masse», a single
        /// grade, 1 AP, range 0, and two effects -- 141 «Mata al objetivo» (kills the target) over the
        /// area and 120, which gives back the AP spent. So it can be chained without running out of
        /// points, which is exactly what is needed to skip a test fight.
        /// </remarks>
        public const int DoomDeMasas = 3450;

        /// <summary>Its only grade.</summary>
        public const int GradoDeDoom = 1;

        /// <summary>The role from which it is declared.</summary>
        public const int HaceFalta = Roles.Administrador;

        /// <summary>Are the administration spells declared to this account?</summary>
        public static bool Para(long accountId)
        {
            if (accountId <= 0) return false;
            return Roles.AlMenos(DatabaseManager.GetAccountRole(accountId), HaceFalta);
        }
    }
}
