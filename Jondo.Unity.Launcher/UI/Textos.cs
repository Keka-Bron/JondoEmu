namespace Jondo.Unity.Launcher.UI
{
    /// <summary>
    /// Los rótulos que ha traído la interfaz nueva, en los tres idiomas.
    /// </summary>
    /// <remarks>
    /// Van aparte de <see cref="LauncherTexts"/> a propósito: aquel catálogo lo comparten el
    /// lanzador y el servidor, y meterle textos que sólo usa una pantalla del lanzador lo
    /// engordaría para nadie. El día que alguno haga falta en los dos sitios, se muda.
    ///
    /// Sin diccionarios ni ficheros: son doce cadenas por idioma y un <c>switch</c> se lee de un
    /// vistazo y no puede quedarse a medias en un idioma sin que el compilador lo diga.
    /// </remarks>
    internal static class Textos
    {
        public static string Jugar(Language i) => i switch
        {
            Language.En => "PLAY",
            Language.Fr => "JOUER",
            _ => "JUGAR",
        };

        public static string Cuentas(Language i) => i switch
        {
            Language.En => "ACCOUNTS",
            Language.Fr => "COMPTES",
            _ => "CUENTAS",
        };

        public static string Ajustes(Language i) => i switch
        {
            Language.En => "SETTINGS",
            Language.Fr => "OPTIONS",
            _ => "AJUSTES",
        };

        public static string Idioma(Language i) => i switch
        {
            Language.En => "Language",
            Language.Fr => "Langue",
            _ => "Idioma",
        };

        public static string Musica(Language i) => i switch
        {
            Language.En => "Music",
            Language.Fr => "Musique",
            _ => "Música",
        };

        public static string Cliente(Language i) => i switch
        {
            Language.En => "Dofus client",
            Language.Fr => "Client Dofus",
            _ => "Cliente de Dofus",
        };

        public static string CuentasGuardadas(Language i) => i switch
        {
            Language.En => "Saved accounts",
            Language.Fr => "Comptes enregistrés",
            _ => "Cuentas guardadas",
        };

        /// <summary>Que el idioma manda también sobre el juego, que es lo que no se adivina.</summary>
        public static string PieIdioma(Language i) => i switch
        {
            Language.En => "The game starts in this language too.",
            Language.Fr => "Le jeu démarre aussi dans cette langue.",
            _ => "El juego arranca también en este idioma.",
        };

        public static string PieCliente(Language i) => i switch
        {
            Language.En => "Only needed if the game is not next to the launcher.",
            Language.Fr => "Utile seulement si le jeu n'est pas à côté du lanceur.",
            _ => "Sólo hace falta si el juego no está junto al lanzador.",
        };

        public static string PieCuentasGuardadas(Language i) => i switch
        {
            Language.En => "Removes the accounts ticked on the Play screen. Accounts already in game are kept.",
            Language.Fr => "Retire les comptes cochés dans l'écran Jouer. Ceux déjà en jeu sont conservés.",
            _ => "Quita las cuentas marcadas en la pantalla de jugar. Las que ya están en el juego se quedan.",
        };

        /// <summary>Lo que se enseña cuando todavía no hay ninguna cuenta guardada.</summary>
        public static string EquipoVacio(Language i) => i switch
        {
            Language.En => "No accounts yet. Add one and it will stay here for next time.",
            Language.Fr => "Aucun compte pour l'instant. Ajoutes-en un et il restera ici.",
            _ => "Todavía no hay ninguna cuenta. Añade una y se quedará aquí para la próxima vez.",
        };

        /// <summary>Cuántas de las marcadas están ya jugando, dicho de forma que se entienda.</summary>
        public static string Resumen(Language i, int marcadas, int enJuego) => i switch
        {
            Language.En => $"{marcadas} ticked · {enJuego} already in game",
            Language.Fr => $"{marcadas} cochés · {enJuego} déjà en jeu",
            _ => $"{marcadas} marcada(s) · {enJuego} ya en el juego",
        };

        public static string Nivel(Language i) => i switch
        {
            Language.En => "Level",
            Language.Fr => "Niveau",
            _ => "Nivel",
        };

        /// <summary>El botón de la música dice lo que HACE, no cómo está.</summary>
        public static string ApagarMusica(Language i) => i switch
        {
            Language.En => "Turn music off",
            Language.Fr => "Couper la musique",
            _ => "Apagar la música",
        };

        public static string EncenderMusica(Language i) => i switch
        {
            Language.En => "Turn music on",
            Language.Fr => "Activer la musique",
            _ => "Encender la música",
        };
    }
}
