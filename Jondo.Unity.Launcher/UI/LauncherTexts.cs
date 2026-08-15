using System;
using System.Collections.Generic;
using System.IO;

namespace Jondo.Unity.Launcher.UI
{
    /// <summary>Languages offered by the selector in the top bar.</summary>
    internal enum Language
    {
        Es,
        En,
        Fr
    }

    /// <summary>
    /// Interface texts in the three languages of the web launcher (the i18n object of
    /// index.html), plus the chosen language, which is remembered across sessions the way
    /// localStorage used to.
    /// </summary>
    internal sealed class LauncherTexts
    {
        public string LoginTab { get; init; } = "";
        public string RegisterTab { get; init; } = "";
        public string UsernameLabel { get; init; } = "";
        public string PasswordLabel { get; init; } = "";
        public string NewUsernameLabel { get; init; } = "";
        public string NewPasswordLabel { get; init; } = "";
        public string NicknameLabel { get; init; } = "";
        public string ConnectButton { get; init; } = "";
        public string CreateButton { get; init; } = "";
        public string Welcome { get; init; } = "";
        public string Subscription { get; init; } = "";
        public string PlayButton { get; init; } = "";
        public string LogOutButton { get; init; } = "";
        public string StatusChecking { get; init; } = "";
        public string StatusOnline { get; init; } = "";
        public string StatusOffline { get; init; } = "";
        public string AccountCreatedMessage { get; init; } = "";
        public string DialogAccept { get; init; } = "Aceptar";
        public string ClientStartedMessage { get; init; } = "";
        public string ConsoleTitle { get; init; } = "";
        public string ClearButton { get; init; } = "";
        public string MusicOn { get; init; } = "";
        public string MusicOff { get; init; } = "";
        public string UsernamePlaceholder { get; init; } = "";
        public string NewUsernamePlaceholder { get; init; } = "";
        public string NicknamePlaceholder { get; init; } = "";
        public string AutoScroll { get; init; } = "";
        public string GenericError { get; init; } = "";

        private static readonly Dictionary<Language, LauncherTexts> Catalog = new()
        {
            [Language.Es] = new LauncherTexts
            {
                LoginTab = "INICIAR SESIÓN",
                RegisterTab = "CREAR CUENTA",
                UsernameLabel = "Usuario",
                PasswordLabel = "Contraseña",
                NewUsernameLabel = "Nuevo Usuario",
                NewPasswordLabel = "Contraseña",
                NicknameLabel = "Apodo",
                ConnectButton = "ENTRAR Y CONECTAR",
                CreateButton = "CREAR CUENTA",
                Welcome = "¡BIENVENIDO,",
                Subscription = "★ Abono Indefinido ★",
                PlayButton = "JUGAR",
                LogOutButton = "Cerrar Sesión",
                StatusChecking = "COMPROBANDO ESTADO...",
                StatusOnline = "SERVIDOR DE JUEGO: EN LÍNEA",
                StatusOffline = "SERVIDOR DE JUEGO: FUERA DE LÍNEA",
                AccountCreatedMessage = "¡Cuenta creada exitosamente! Ya puedes iniciar sesión.",
                DialogAccept = "ACEPTAR",
                ClientStartedMessage = "¡Cliente iniciado exitosamente!",
                ConsoleTitle = "REGISTRO DE EVENTOS DEL SERVIDOR",
                ClearButton = "LIMPIAR",
                MusicOn = "MÚSICA: ON",
                MusicOff = "MÚSICA: OFF",
                UsernamePlaceholder = "nombre de usuario",
                NewUsernamePlaceholder = "3-32 caracteres",
                NicknamePlaceholder = "Apodo",
                AutoScroll = "Auto-Scroll",
                GenericError = "Error."
            },
            [Language.En] = new LauncherTexts
            {
                LoginTab = "LOG IN",
                RegisterTab = "REGISTER",
                UsernameLabel = "Username",
                PasswordLabel = "Password",
                NewUsernameLabel = "New Username",
                NewPasswordLabel = "Password",
                NicknameLabel = "Nickname",
                ConnectButton = "LOG IN & CONNECT",
                CreateButton = "CREATE ACCOUNT",
                Welcome = "WELCOME,",
                Subscription = "★ Unlimited Subscription ★",
                PlayButton = "PLAY",
                LogOutButton = "Log Out",
                StatusChecking = "CHECKING STATUS...",
                StatusOnline = "GAME SERVER: ONLINE",
                StatusOffline = "GAME SERVER: OFFLINE",
                AccountCreatedMessage = "Account created successfully! You can now log in.",
                DialogAccept = "ACCEPT",
                ClientStartedMessage = "Game client started successfully!",
                ConsoleTitle = "SERVER EVENT LOGS",
                ClearButton = "CLEAR",
                MusicOn = "MUSIC: ON",
                MusicOff = "MUSIC: OFF",
                UsernamePlaceholder = "username",
                NewUsernamePlaceholder = "3-32 characters",
                NicknamePlaceholder = "Nickname",
                AutoScroll = "Auto-Scroll",
                GenericError = "Error."
            },
            [Language.Fr] = new LauncherTexts
            {
                LoginTab = "CONNEXION",
                RegisterTab = "S'INSCRIRE",
                UsernameLabel = "Nom d'utilisateur",
                PasswordLabel = "Mot de passe",
                NewUsernameLabel = "Nouveau compte",
                NewPasswordLabel = "Mot de passe",
                NicknameLabel = "Pseudo",
                ConnectButton = "SE CONNECTER",
                CreateButton = "CRÉER UN COMPTE",
                Welcome = "BIENVENUE,",
                Subscription = "★ Abonnement Illimité ★",
                PlayButton = "JOUER",
                LogOutButton = "Déconnexion",
                StatusChecking = "VÉRIFICATION DU STATUT...",
                StatusOnline = "SERVEUR DE JEU: EN LIGNE",
                StatusOffline = "SERVEUR DE JEU: HORS LIGNE",
                AccountCreatedMessage = "Compte créé avec succès! Vous pouvez maintenant vous connecter.",
                DialogAccept = "ACCEPTER",
                ClientStartedMessage = "Client de jeu démarré avec succès!",
                ConsoleTitle = "JOURNAL DU SERVEUR",
                ClearButton = "EFFACER",
                MusicOn = "MUSIQUE: ON",
                MusicOff = "MUSIQUE: OFF",
                UsernamePlaceholder = "nom d'utilisateur",
                NewUsernamePlaceholder = "3-32 caractères",
                NicknamePlaceholder = "Pseudo",
                AutoScroll = "Auto-Scroll",
                GenericError = "Erreur."
            }
        };

        public static LauncherTexts Get(Language language) => Catalog[language];

        /// <summary>Two-letter language code, used to draw the flag.</summary>
        public static string Code(Language language) => language switch
        {
            Language.En => "en",
            Language.Fr => "fr",
            _ => "es"
        };

        // Las preferencias las guarda LauncherPreferences. Esto se queda como atajo porque lo
        // llaman desde varios sitios, pero el fichero lo lleva él: escribir aquí con WriteAllText,
        // como se hacía antes, borraba de paso la ruta del cliente que guarda la otra opción.

        /// <summary>Recovers the language chosen last time; Spanish by default.</summary>
        public static Language LoadLanguage() => LauncherPreferences.Language;

        /// <summary>Stores the chosen language for the next time the launcher is opened.</summary>
        public static void SaveLanguage(Language language) => LauncherPreferences.Language = language;
    }
}
