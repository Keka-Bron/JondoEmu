namespace Jondo.Unity.Launcher
{
    /// <summary>
    /// What each account can do.
    ///
    /// It lives in the contract because both sides need it: the server to decide, and the
    /// launcher to know whether to show that person the administration buttons or not. But let
    /// it be clear which of the two rules: **the check is always the server's**. What the
    /// launcher does with this is cosmetic —hiding a button that would be rejected anyway—
    /// because the launcher is on the player's computer and nothing can be trusted there.
    ///
    /// The number is stored in the Role column of the Accounts table and goes up: each level can do what the
    /// previous one can and something more, so the check is always «has at least this much».
    /// </summary>
    public static class Roles
    {
        /// <summary>A player. It is what one is on creating an account.</summary>
        public const int Jugador = 1;

        /// <summary>Moderator: watches the chat and moves around the world to help.</summary>
        public const int Moderador = 2;

        /// <summary>Game master padawan: first assistance and event tools.</summary>
        public const int GameMasterPadawan = 3;

        /// <summary>Game master: además toca personajes —nivel, kamas, aspecto— para arreglar cosas.</summary>
        public const int GameMaster = 4;

        /// <summary>Administrator: on top of that rules over the server itself.</summary>
        public const int Administrador = 5;

        /// <summary>The one a new account is given.</summary>
        public const int PorDefecto = Jugador;

        public static bool AlMenos(int rol, int hace_falta) => rol >= hace_falta;

        /// <summary>The role's name, for the logs and for the server window.</summary>
        public static string Nombre(int rol) => rol switch
        {
            Administrador => "administrador",
            GameMaster => "game master",
            GameMasterPadawan => "game master padawan",
            Moderador => "moderador",
            Jugador => "jugador",
            _ => $"desconocido ({rol})",
        };
    }
}
