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
        public string AddAccountButton { get; init; } = "";
        public string TeamAccountsFormat { get; init; } = "";
        public string TeamTitle { get; init; } = "";
        public string SelectAll { get; init; } = "";
        public string DeselectAll { get; init; } = "";
        public string LaunchSelected { get; init; } = "";
        public string RemoveSelected { get; init; } = "";
        public string MaxAccountsError { get; init; } = "";
        public string SelectAccountError { get; init; } = "";
        public string BackToTeam { get; init; } = "";
        public string TeamSummaryFormat { get; init; } = "";
        public string InGame { get; init; } = "";
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

        // Los avisos de lanzar un cliente. Estaban escritos en francés dentro de LauncherService y
        // de ClientLaunchRegistry, así que salían en francés con el lanzador puesto en español o en
        // inglés. Aquí es donde vive el idioma.
        public string SessionExpiredError { get; init; } = "";
        public string ClientStartFailed { get; init; } = "";
        public string AccountAlreadyRunning { get; init; } = "";
        public string MaxClientsError { get; init; } = "";
        public string ClientNotFound { get; init; } = "";
        public string AccountCreated { get; init; } = "";
        public string ServidorSinResponder { get; init; } = "";
        public string ControlRechazado { get; init; } = "";

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
                AddAccountButton = "+ AÑADIR OTRA CUENTA",
                TeamAccountsFormat = "★ Equipo: {0} cuenta(s) ★",
                TeamTitle = "EQUIPO ({0}/8)",
                SelectAll = "SELECCIONAR TODO",
                DeselectAll = "DESELECCIONAR TODO",
                LaunchSelected = "LANZAR SELECCIÓN ({0})",
                RemoveSelected = "Eliminar selección",
                MaxAccountsError = "El equipo ya contiene el máximo de 8 cuentas.",
                SelectAccountError = "Selecciona al menos una cuenta.",
                BackToTeam = "← VOLVER AL EQUIPO",
                TeamSummaryFormat = "{0} seleccionado(s) · {1} activo(s)",
                InGame = "EN JUEGO",
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
                GenericError = "Error.",
                SessionExpiredError = "La sesión de esta cuenta ha caducado. Vuelve a conectarla antes de jugar.",
                ClientStartFailed = "No se ha podido arrancar el cliente de Dofus.",
                AccountAlreadyRunning = "Esta cuenta ya tiene un cliente abierto.",
                MaxClientsError = "Ya hay 8 clientes abiertos, que es el máximo.",
                ClientNotFound = "No se encuentra Dofus.exe. Elige dónde está con el botón de la ruta del cliente.",
                AccountCreated = "Cuenta creada.",
                ServidorSinResponder = "El servidor no responde. Espera a que termine de arrancar o vuelve a abrirlo.",
                ControlRechazado = "El servidor ha rearrancado. Cierra el lanzador y vuelve a abrirlo."
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
                AddAccountButton = "+ ADD ANOTHER ACCOUNT",
                TeamAccountsFormat = "★ Team: {0} account(s) ★",
                TeamTitle = "TEAM ({0}/8)",
                SelectAll = "SELECT ALL",
                DeselectAll = "DESELECT ALL",
                LaunchSelected = "LAUNCH SELECTED ({0})",
                RemoveSelected = "Remove selected",
                MaxAccountsError = "The team already contains the maximum of 8 accounts.",
                SelectAccountError = "Select at least one account.",
                BackToTeam = "← BACK TO TEAM",
                TeamSummaryFormat = "{0} selected · {1} active",
                InGame = "IN GAME",
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
                GenericError = "Error.",
                SessionExpiredError = "This account's session has expired. Log it in again before playing.",
                ClientStartFailed = "The Dofus client could not be started.",
                AccountAlreadyRunning = "This account already has a client running.",
                MaxClientsError = "There are already 8 clients running, which is the maximum.",
                ClientNotFound = "Dofus.exe was not found. Point at it with the client path button.",
                AccountCreated = "Account created.",
                ServidorSinResponder = "The server is not responding. Wait for it to finish starting, or open it again.",
                ControlRechazado = "The server has restarted. Close the launcher and open it again."
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
                AddAccountButton = "+ AJOUTER UN AUTRE COMPTE",
                TeamAccountsFormat = "★ Équipe : {0} compte(s) ★",
                TeamTitle = "ÉQUIPE ({0}/8)",
                SelectAll = "TOUT SÉLECTIONNER",
                DeselectAll = "TOUT DÉSÉLECTIONNER",
                LaunchSelected = "LANCER LA SÉLECTION ({0})",
                RemoveSelected = "Supprimer la sélection",
                MaxAccountsError = "L’équipe contient déjà le maximum de 8 comptes.",
                SelectAccountError = "Sélectionnez au moins un compte.",
                BackToTeam = "← RETOUR À L’ÉQUIPE",
                TeamSummaryFormat = "{0} sélectionné(s) · {1} actif(s)",
                InGame = "EN JEU",
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
                GenericError = "Erreur.",
                SessionExpiredError = "La session de ce compte a expiré. Reconnecte-le avant de jouer.",
                ClientStartFailed = "Le client Dofus n'a pas pu démarrer.",
                AccountAlreadyRunning = "Ce compte possède déjà un client actif.",
                MaxClientsError = "Il y a déjà 8 clients actifs, ce qui est le maximum.",
                ClientNotFound = "Dofus.exe est introuvable. Indique son emplacement avec le bouton du chemin du client.",
                AccountCreated = "Compte créé.",
                ServidorSinResponder = "Le serveur ne répond pas. Attends la fin du démarrage ou relance-le.",
                ControlRechazado = "Le serveur a redémarré. Ferme le lanceur et rouvre-le."
            }
        };

        public static LauncherTexts Get(Language language) => Catalog[language];

        /// <summary>Los textos en el idioma que el lanzador tenga puesto ahora mismo.</summary>
        public static LauncherTexts Current => Get(LauncherPreferences.Language);

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
