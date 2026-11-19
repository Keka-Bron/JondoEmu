using System;

namespace Jondo.Unity.Launcher.UI
{
    /// <summary>
    /// The installer's and the updater's texts, in the launcher's four languages. Apart from
    /// <see cref="Textos"/> for the same reason that one is apart from <see cref="LauncherTexts"/>:
    /// only these screens use them.
    /// </summary>
    internal static class SetupTexts
    {
        private static string T(Language i, string es, string en, string fr, string pt) => i switch
        {
            Language.En => en,
            Language.Fr => fr,
            Language.Pt => pt,
            _ => es,
        };

        // ─── The steps ─────────────────────────────────────────────────────────

        public static string StepRelease(Language i) => T(i, "Buscar la última versión", "Find the latest version", "Chercher la dernière version", "Buscar a versão mais recente");
        public static string StepDotNet(Language i) => T(i, "Descargar .NET 10", "Download .NET 10", "Télécharger .NET 10", "Baixar o .NET 10");
        public static string StepSystem(Language i) => T(i, "Preparar la carpeta y los accesos directos", "Prepare the folder and shortcuts", "Préparer le dossier et les raccourcis", "Preparar a pasta e os atalhos");
        public static string StepEmulator(Language i) => T(i, "Descargar el emulador", "Download the emulator", "Télécharger l'émulateur", "Baixar o emulador");
        public static string StepFiles(Language i) => T(i, "Instalar los archivos del emulador", "Install the emulator's files", "Installer les fichiers de l'émulateur", "Instalar os arquivos do emulador");
        public static string StepClient(Language i) => T(i, "Comprobar el cliente de Dofus", "Check the Dofus client", "Vérifier le client Dofus", "Verificar o cliente do Dofus");
        public static string StepClientDownload(Language i) => T(i, "Descargar el cliente de Dofus", "Download the Dofus client", "Télécharger le client Dofus", "Baixar o cliente do Dofus");
        public static string StepMelonLoader(Language i) => T(i, "Instalar MelonLoader", "Install MelonLoader", "Installer MelonLoader", "Instalar o MelonLoader");
        public static string StepMods(Language i) => T(i, "Instalar los mods de Jondo", "Install Jondo's mods", "Installer les mods de Jondo", "Instalar os mods do Jondo");
        public static string StepGeneration(Language i) => T(i, "Primera descompilación del cliente", "First decompilation of the client", "Première décompilation du client", "Primeira descompilação do cliente");
        public static string StepDatabase(Language i) => T(i, "Preparar la base de datos del mundo", "Prepare the world database", "Préparer la base de données du monde", "Preparar o banco de dados do mundo");
        public static string StepFinish(Language i) => T(i, "Terminar", "Finish", "Terminer", "Concluir");
        public static string StepServer(Language i) => T(i, "Arrancar el servidor", "Start the server", "Démarrer le serveur", "Iniciar o servidor");
        public static string ServerStarting(Language i)
            => T(i, "Cargando el mundo…", "Loading the world…", "Chargement du monde…", "Carregando o mundo…");
        public static string ServerOnline(Language i) => T(i, "En línea", "Online", "En ligne", "Online");
        public static string ServerAlreadyRunning(Language i)
            => T(i, "ya estaba en marcha", "already running", "déjà en marche", "já estava rodando");
        public static string ServerNoAnswer(Language i)
            => T(i, "El servidor no ha respondido en minuto y medio. Vuelve a intentarlo; el resto de la instalación ya está hecho.",
                    "The server did not answer within a minute and a half. Try again; the rest of the installation is done.",
                    "Le serveur n'a pas répondu en une minute et demie. Réessaie ; le reste de l'installation est fait.",
                    "O servidor não respondeu em um minuto e meio. Tente de novo; o resto da instalação já está feito.");
        public static string DoneOpening(Language i)
            => T(i, "Todo listo. Abriendo el launcher para que entres o crees tu cuenta…",
                    "All set. Opening the launcher so you can sign in or create your account…",
                    "Tout est prêt. Ouverture du lanceur pour te connecter ou créer ton compte…",
                    "Tudo pronto. Abrindo o launcher para você entrar ou criar a sua conta…");

        public static string StepCompare(Language i) => T(i, "Comparar con lo instalado", "Compare with what is installed", "Comparer avec l'installation", "Comparar com o que está instalado");
        public static string StepStopServer(Language i) => T(i, "Detener el servidor", "Stop the server", "Arrêter le serveur", "Parar o servidor");
        public static string StepDownloadUpdate(Language i) => T(i, "Descargar la actualización", "Download the update", "Télécharger la mise à jour", "Baixar a atualização");
        public static string StepApplyUpdate(Language i) => T(i, "Aplicar la actualización", "Apply the update", "Appliquer la mise à jour", "Aplicar a atualização");
        public static string StepVerify(Language i) => T(i, "Comprobar los archivos instalados", "Check the installed files", "Vérifier les fichiers installés", "Verificar os arquivos instalados");

        /// <summary>MelonLoader's first run, stage by stage, as its log goes.</summary>
        public static string GenerationStage(Language i, string stage) => stage switch
        {
            "starting" => T(i, "Arrancando el cliente con MelonLoader…", "Starting the client with MelonLoader…", "Démarrage du client avec MelonLoader…", "Iniciando o cliente com o MelonLoader…"),
            "remoteapi" => T(i, "Consultando la versión de Unity del juego…", "Looking up the game's Unity version…", "Recherche de la version Unity du jeu…", "Consultando a versão do Unity do jogo…"),
            "cpp2il-download" => T(i, "Descargando Cpp2IL…", "Downloading Cpp2IL…", "Téléchargement de Cpp2IL…", "Baixando o Cpp2IL…"),
            "cpp2il-extract" => T(i, "Descomprimiendo Cpp2IL…", "Unpacking Cpp2IL…", "Décompression de Cpp2IL…", "Descompactando o Cpp2IL…"),
            "plugin-download" => T(i, "Descargando el plugin de Cpp2IL…", "Downloading the Cpp2IL plugin…", "Téléchargement du plugin Cpp2IL…", "Baixando o plugin do Cpp2IL…"),
            "unity-download" => T(i, "Descargando las bibliotecas de Unity…", "Downloading Unity's libraries…", "Téléchargement des bibliothèques Unity…", "Baixando as bibliotecas do Unity…"),
            "unity-extract" => T(i, "Descomprimiendo las bibliotecas de Unity…", "Unpacking Unity's libraries…", "Décompression des bibliothèques Unity…", "Descompactando as bibliotecas do Unity…"),
            "checking" => T(i, "Comprobando GameAssembly.dll…", "Checking GameAssembly.dll…", "Vérification de GameAssembly.dll…", "Verificando o GameAssembly.dll…"),
            "cpp2il" => T(i, "Descompilando el juego con Cpp2IL (lo más largo)…", "Decompiling the game with Cpp2IL (the longest part)…", "Décompilation du jeu avec Cpp2IL (le plus long)…", "Descompilando o jogo com o Cpp2IL (a parte mais demorada)…"),
            "reading" => T(i, "Leyendo lo descompilado…", "Reading the decompiled code…", "Lecture du code décompilé…", "Lendo o código descompilado…"),
            "interop" => T(i, "Generando los ensamblados de interoperabilidad…", "Generating the interop assemblies…", "Génération des assemblages d'interopérabilité…", "Gerando os assemblies de interoperabilidade…"),
            "interop-done" => T(i, "Ensamblados generados", "Assemblies generated", "Assemblages générés", "Assemblies gerados"),
            "cleaning" => T(i, "Limpiando…", "Cleaning up…", "Nettoyage…", "Limpando…"),
            "generated" => T(i, "Descompilación terminada", "Decompilation finished", "Décompilation terminée", "Descompilação concluída"),
            "mods" => T(i, "Cargando los mods…", "Loading the mods…", "Chargement des mods…", "Carregando os mods…"),
            "done" => T(i, "Listo", "Ready", "Prêt", "Pronto"),
            _ => stage,
        };

        // ─── What a step can say ───────────────────────────────────────────────

        public static string AlreadyThere(Language i) => T(i, "ya estaba", "already there", "déjà là", "já estava lá");
        public static string Files(Language i) => T(i, "archivos", "files", "fichiers", "arquivos");
        public static string NoManifest(Language i, string tag, string platform)
            => T(i, $"La versión {tag} no trae archivos para {platform}.",
                    $"Release {tag} has no files for {platform}.",
                    $"La version {tag} n'a pas de fichiers pour {platform}.",
                    $"A versão {tag} não traz arquivos para {platform}.");
        public static string ElevationRefused(Language i)
            => T(i, "Windows no dio permiso para crear la carpeta. Vuelve a intentarlo y acepta el aviso, o elige otra carpeta.",
                    "Windows did not allow creating the folder. Try again and accept the prompt, or choose another folder.",
                    "Windows n'a pas autorisé la création du dossier. Réessaie et accepte l'invite, ou choisis un autre dossier.",
                    "O Windows não deu permissão para criar a pasta. Tente de novo e aceite o aviso, ou escolha outra pasta.");
        public static string NoClientLink(Language i)
            => T(i, "El README del repositorio no tiene enlace al cliente. Indica dónde está tu Dofus.exe.",
                    "The repository's README has no client link. Point to your Dofus.exe instead.",
                    "Le README du dépôt n'a pas de lien vers le client. Indique où est ton Dofus.exe.",
                    "O README do repositório não tem link para o cliente. Indique onde está o seu Dofus.exe.");
        public static string QuotaWait(Language i, DateTime until)
            => T(i, $"MEGA limita la descarga gratuita; se reanuda sola a las {until:HH:mm}.",
                    $"MEGA limits free downloads; it resumes by itself at {until:HH:mm}.",
                    $"MEGA limite les téléchargements gratuits ; reprise automatique à {until:HH:mm}.",
                    $"O MEGA limita o download gratuito; ele continua sozinho às {until:HH:mm}.");
        public static string WrongClientVersion(Language i, string version)
            => T(i, $"Ese cliente es la versión {version}; el emulador necesita la 3.6.10.11 o la 3.6.10.10.",
                    $"That client is version {version}; the emulator needs 3.6.10.11 or 3.6.10.10.",
                    $"Ce client est la version {version} ; l'émulateur a besoin de la 3.6.10.11 ou de la 3.6.10.10.",
                    $"Esse cliente é a versão {version}; o emulador precisa da 3.6.10.11 ou da 3.6.10.10.");
        public static string ClientNotWritable(Language i)
            => T(i, "No se puede escribir en la carpeta del cliente. Muévelo a una carpeta tuya o deja que el instalador lo descargue.",
                    "The client's folder is not writable. Move it to a folder of yours or let the installer download it.",
                    "Le dossier du client n'est pas modifiable. Déplace-le dans un de tes dossiers ou laisse l'installateur le télécharger.",
                    "Não é possível gravar na pasta do cliente. Mova-o para uma pasta sua ou deixe o instalador baixá-lo.");
        public static string ClientNotFound(Language i)
            => T(i, "No se encuentra Dofus.exe en esa ruta.", "Dofus.exe is not at that path.", "Dofus.exe est introuvable à cet emplacement.",
                    "O Dofus.exe não está nesse caminho.");
        public static string SpaceNeeded(Language i, string needed)
            => T(i, $"Hacen falta unos {needed} libres en ese disco.",
                    $"It needs about {needed} free on that drive.",
                    $"Il faut environ {needed} libres sur ce disque.",
                    $"São necessários cerca de {needed} livres nesse disco.");
        public static string Drive(Language i, string root, string total)
            => T(i, $"Disco {root} · {total}", $"Drive {root} · {total}", $"Disque {root} · {total}", $"Disco {root} · {total}");
        public static string DriveFree(Language i, string free) => T(i, $"{free} libres", $"{free} free", $"{free} libres", $"{free} livres");
        public static string NotEnoughSpace(Language i, string needed, string free)
            => T(i, $"Hacen falta {needed} libres en ese disco; hay {free}.",
                    $"{needed} free are needed on that drive; there are {free}.",
                    $"Il faut {needed} libres sur ce disque ; il y en a {free}.",
                    $"São necessários {needed} livres nesse disco; há {free}.");

        // ─── The installer's page ──────────────────────────────────────────────

        public static string InstallTitle(Language i) => T(i, "Instalar Jondo Emulator", "Install Jondo Emulator", "Installer Jondo Emulator", "Instalar o Jondo Emulator");
        public static string InstallIntro(Language i)
            => T(i, "Un clic y el instalador lo deja todo listo para jugar: el emulador, el cliente, MelonLoader y los mods.",
                    "One click and the installer gets everything ready to play: the emulator, the client, MelonLoader and the mods.",
                    "Un clic et l'installateur prépare tout pour jouer : l'émulateur, le client, MelonLoader et les mods.",
                    "Um clique e o instalador deixa tudo pronto para jogar: o emulador, o cliente, o MelonLoader e os mods.");
        public static string Folder(Language i) => T(i, "Carpeta de instalación", "Installation folder", "Dossier d'installation", "Pasta de instalação");
        public static string FolderNote(Language i)
            => T(i, "Windows pedirá permiso una sola vez para crearla.", "Windows will ask for permission once to create it.",
                    "Windows demandera l'autorisation une seule fois pour le créer.", "O Windows vai pedir permissão uma única vez para criá-la.");
        public static string ClientChoice(Language i) => T(i, "Cliente de Dofus 3.6", "Dofus 3.6 client", "Client Dofus 3.6", "Cliente do Dofus 3.6");
        public static string ClientDownload(Language i, string size)
            => T(i, $"Descargarlo ({size})", $"Download it ({size})", $"Le télécharger ({size})", $"Baixá-lo ({size})");
        public static string ClientExisting(Language i) => T(i, "Ya lo tengo", "I already have it", "Je l'ai déjà", "Já tenho");
        public static string ClientPick(Language i) => OperatingSystem.IsWindows()
            ? T(i, "Elegir Dofus.exe…", "Choose Dofus.exe…", "Choisir Dofus.exe…", "Escolher o Dofus.exe…")
            : T(i, "Elegir Dofus.x64, zaap-start.sh o Dofus.exe…", "Choose Dofus.x64, zaap-start.sh or Dofus.exe…",
                   "Choisir Dofus.x64, zaap-start.sh ou Dofus.exe…", "Escolher o Dofus.x64, zaap-start.sh ou Dofus.exe…");
        public static string Connection(Language i) => T(i, "Servidor", "Server", "Serveur", "Servidor");
        public static string ConnectionLocal(Language i) => T(i, "En este equipo", "On this computer", "Sur cet ordinateur", "Neste computador");
        public static string ConnectionRemote(Language i)
            => T(i, "En otra máquina (VPS, LAN…)", "On another machine (VPS, LAN…)", "Sur une autre machine (VPS, LAN…)", "Em outra máquina (VPS, LAN…)");
        public static string ConnectionAddress(Language i) => T(i, "IP o nombre del servidor", "Server IP or name", "IP ou nom du serveur", "IP ou nome do servidor");
        public static string Repository(Language i) => T(i, "Repositorio de actualizaciones", "Update repository", "Dépôt des mises à jour", "Repositório de atualizações");
        public static string RepositoryNote(Language i)
            => T(i, "El GitHub donde se buscan las versiones nuevas.",
                    "The GitHub repository new versions come from.",
                    "Le dépôt GitHub d'où viennent les nouvelles versions.",
                    "O GitHub onde as versões novas são buscadas.");
        /// <summary>The shape a repository is written in, shown in its empty field.</summary>
        public static string RepositoryShape(Language i) => T(i, "propietario/nombre", "owner/name", "propriétaire/nom", "dono/nome");
        public static string Shortcuts(Language i)
            => T(i, "Accesos directos en el escritorio y el menú Inicio", "Desktop and Start menu shortcuts",
                    "Raccourcis sur le bureau et dans le menu Démarrer", "Atalhos na área de trabalho e no menu Iniciar");
        public static string InstallButton(Language i) => T(i, "INSTALAR", "INSTALL", "INSTALLER", "INSTALAR");
        public static string Installing(Language i) => T(i, "Instalando", "Installing", "Installation", "Instalando");
        public static string Updating(Language i) => T(i, "Actualizando", "Updating", "Mise à jour", "Atualizando");
        public static string Total(Language i) => T(i, "Total", "Overall", "Total", "Total");
        public static string Remaining(Language i, string time) => T(i, $"quedan {time}", $"{time} left", $"encore {time}", $"faltam {time}");
        public static string Elapsed(Language i, string time) => T(i, $"duración: {time}", $"time taken: {time}", $"durée : {time}", $"duração: {time}");
        public static string Retry(Language i) => T(i, "REINTENTAR", "RETRY", "RÉESSAYER", "TENTAR DE NOVO");
        public static string Cancel(Language i) => T(i, "Cancelar", "Cancel", "Annuler", "Cancelar");
        public static string Back(Language i) => T(i, "Volver", "Back", "Retour", "Voltar");
        public static string Done(Language i) => T(i, "Todo listo", "All set", "Tout est prêt", "Tudo pronto");
        public static string DoneNote(Language i)
            => T(i, "El emulador está instalado. Ábrelo y crea tu cuenta para jugar.",
                    "The emulator is installed. Open it and create your account to play.",
                    "L'émulateur est installé. Ouvre-le et crée ton compte pour jouer.",
                    "O emulador está instalado. Abra-o e crie a sua conta para jogar.");
        public static string OpenLauncher(Language i) => T(i, "ABRIR EL LAUNCHER", "OPEN THE LAUNCHER", "OUVRIR LE LANCEUR", "ABRIR O LAUNCHER");
        public static string Failed(Language i) => T(i, "La instalación se ha detenido", "The installation stopped", "L'installation s'est arrêtée", "A instalação parou");
        public static string UpdateFailed(Language i) => T(i, "La actualización se ha detenido", "The update stopped", "La mise à jour s'est arrêtée", "A atualização parou");
        public static string FailedNote(Language i)
            => T(i, "Lo ya hecho se conserva: al reintentar se sigue donde se quedó.",
                    "What is done is kept: retrying carries on where it stopped.",
                    "Ce qui est fait est conservé : réessayer reprend là où ça s'est arrêté.",
                    "O que já foi feito é mantido: ao tentar de novo, continua de onde parou.");
        public static string GenerationWarning(Language i)
            => T(i, "La primera vez, MelonLoader descompila el cliente en segundo plano durante unos minutos, sin abrir ninguna ventana.",
                    "The first time, MelonLoader decompiles the client in the background for a few minutes, without opening any window.",
                    "La première fois, MelonLoader décompile le client en arrière-plan pendant quelques minutes, sans ouvrir de fenêtre.",
                    "Na primeira vez, o MelonLoader descompila o cliente em segundo plano por alguns minutos, sem abrir nenhuma janela.");

        // ─── Updates ───────────────────────────────────────────────────────────

        public static string UpdateButton(Language i, string version)
            => T(i, $"ACTUALIZAR A {version}", $"UPDATE TO {version}", $"METTRE À JOUR ({version})", $"ATUALIZAR PARA {version}");
        public static string UpdateRequired(Language i, string installed, string latest)
            => T(i, $"Hay una versión nueva ({latest}; tienes la {installed}). Actualiza para poder jugar.",
                    $"A new version is out ({latest}; you have {installed}). Update to play.",
                    $"Une nouvelle version est sortie ({latest} ; tu as la {installed}). Mets à jour pour jouer.",
                    $"Há uma versão nova ({latest}; você tem a {installed}). Atualize para poder jogar.");
        public static string UpdateSize(Language i, int files, string size, bool full)
            => full
                ? T(i, $"Actualización completa · {size}", $"Full update · {size}", $"Mise à jour complète · {size}", $"Atualização completa · {size}")
                : T(i, $"{files} archivo(s) · {size}", $"{files} file(s) · {size}", $"{files} fichier(s) · {size}", $"{files} arquivo(s) · {size}");
        public static string UpdateCheckFailed(Language i, string why)
            => T(i, $"No se pudo comprobar si hay actualizaciones: {why}", $"Could not check for updates: {why}",
                    $"Impossible de vérifier les mises à jour : {why}", $"Não foi possível verificar se há atualizações: {why}");
        public static string UpToDate(Language i, string version)
            => T(i, $"Al día ({version})", $"Up to date ({version})", $"À jour ({version})", $"Atualizado ({version})");
        public static string Updated(Language i, string version)
            => T(i, $"Actualizado a {version}.", $"Updated to {version}.", $"Mis à jour vers {version}.", $"Atualizado para {version}.");
        public static string ServerRunning(Language i)
            => T(i, "El servidor se detendrá para actualizar: los jugadores conectados saldrán del juego.",
                    "The server will stop to update: players in game will be disconnected.",
                    "Le serveur s'arrêtera pour la mise à jour : les joueurs en jeu seront déconnectés.",
                    "O servidor vai parar para atualizar: os jogadores conectados vão sair do jogo.");

        // ─── Settings ──────────────────────────────────────────────────────────

        public static string InstallationSection(Language i) => T(i, "Instalación", "Installation", "Installation", "Instalação");
        public static string InstalledVersion(Language i, string version, string folder)
            => T(i, $"Versión {version} en {folder}", $"Version {version} in {folder}", $"Version {version} dans {folder}", $"Versão {version} em {folder}");
        public static string CheckUpdates(Language i) => T(i, "Buscar actualizaciones", "Check for updates", "Chercher des mises à jour", "Buscar atualizações");
        public static string Repair(Language i) => T(i, "Reparar la instalación", "Repair the installation", "Réparer l'installation", "Reparar a instalação");
        public static string Repairing(Language i) => T(i, "Reparando", "Repairing", "Réparation", "Reparando");
        public static string Repaired(Language i) => T(i, "Instalación reparada", "Installation repaired", "Installation réparée", "Instalação reparada");
        public static string RepairedNote(Language i)
            => T(i, "Todo vuelve a estar en su sitio: los archivos, MelonLoader y los mods.",
                    "Everything is back in place: the files, MelonLoader and the mods.",
                    "Tout est de nouveau en place : les fichiers, MelonLoader et les mods.",
                    "Tudo voltou ao lugar: os arquivos, o MelonLoader e os mods.");
        public static string BackToLauncher(Language i) => T(i, "VOLVER AL LAUNCHER", "BACK TO THE LAUNCHER", "RETOUR AU LANCEUR", "VOLTAR AO LAUNCHER");
        public static string Uninstall(Language i) => T(i, "Desinstalar", "Uninstall", "Désinstaller", "Desinstalar");
        public static string Save(Language i) => T(i, "Guardar", "Save", "Enregistrer", "Salvar");
        public static string ConnectionSaved(Language i)
            => T(i, "Guardado. El launcher se reinicia para conectarse al servidor nuevo.",
                    "Saved. The launcher restarts to connect to the new server.",
                    "Enregistré. Le lanceur redémarre pour se connecter au nouveau serveur.",
                    "Salvo. O launcher reinicia para se conectar ao servidor novo.");
        public static string LocalServerStops(Language i, string host)
            => T(i, $"El servidor de este equipo se detendrá para conectarte a {host}: los jugadores conectados a él saldrán del juego.",
                    $"This computer's server will stop to connect you to {host}: players in game on it will be disconnected.",
                    $"Le serveur de cet ordinateur s'arrêtera pour te connecter à {host} : les joueurs en jeu dessus seront déconnectés.",
                    $"O servidor deste computador vai parar para conectar você a {host}: os jogadores conectados a ele vão sair do jogo.");
        public static string RelayFailed(Language i, string host, string why)
            => T(i, $"No se pudo preparar la conexión con {host} ({why}). Si hay un servidor abierto en este equipo, ciérralo o vuelve a «En este equipo» en Ajustes.",
                    $"The connection to {host} could not be set up ({why}). If a server is open on this computer, close it or go back to «On this computer» in Settings.",
                    $"La connexion à {host} n'a pas pu être préparée ({why}). Si un serveur est ouvert sur cet ordinateur, ferme-le ou reviens à « Sur cet ordinateur » dans les options.",
                    $"Não foi possível preparar a conexão com {host} ({why}). Se houver um servidor aberto neste computador, feche-o ou volte para «Neste computador» em Configurações.");
        public static string ConnectionUnchanged(Language i, bool local)
            => local
                ? T(i, "Esa dirección es este mismo equipo: el launcher ya se conecta a él.",
                       "That address is this very computer: the launcher already connects to it.",
                       "Cette adresse est cet ordinateur même : le lanceur s'y connecte déjà.",
                       "Esse endereço é este mesmo computador: o launcher já se conecta a ele.")
                : T(i, "El launcher ya se conecta a ese servidor.",
                       "The launcher already connects to that server.",
                       "Le lanceur se connecte déjà à ce serveur.",
                       "O launcher já se conecta a esse servidor.");
        public static string BadRepository(Language i)
            => T(i, "Escribe el repositorio como propietario/nombre o su dirección de GitHub.",
                    "Write the repository as owner/name or its GitHub address.",
                    "Écris le dépôt sous la forme propriétaire/nom ou son adresse GitHub.",
                    "Escreva o repositório como dono/nome ou o endereço dele no GitHub.");
        public static string BadAddress(Language i)
            => T(i, "Escribe la IP o el nombre del servidor.", "Write the server's IP or name.", "Écris l'IP ou le nom du serveur.",
                    "Escreva o IP ou o nome do servidor.");

        public static string UninstallTitle(Language i)
            => T(i, "Desinstalar Jondo Emulator", "Uninstall Jondo Emulator", "Désinstaller Jondo Emulator", "Desinstalar o Jondo Emulator");
        public static string UninstallQuestion(Language i, string folder)
            => T(i, $"Se borrará {folder} con el emulador, sus bases de datos (cuentas y personajes) y los accesos directos. ¿Seguir?",
                    $"{folder} will be deleted with the emulator, its databases (accounts and characters) and the shortcuts. Continue?",
                    $"{folder} sera supprimé avec l'émulateur, ses bases de données (comptes et personnages) et les raccourcis. Continuer ?",
                    $"{folder} será apagado com o emulador, os bancos de dados (contas e personagens) e os atalhos. Continuar?");
        public static string UninstallClientToo(Language i)
            => T(i, "El cliente descargado también se borra.", "The downloaded client is deleted too.", "Le client téléchargé est supprimé aussi.",
                    "O cliente baixado também é apagado.");

        // ─── The 2026 interface ────────────────────────────────────────────────

        public static string FolderDesc(Language i)
            => T(i, "Donde van el emulador, el servidor y sus datos.", "Where the emulator, the server and their data go.",
                    "Où vont l'émulateur, le serveur et leurs données.", "Onde ficam o emulador, o servidor e os dados deles.");
        public static string ClientDesc(Language i)
            => T(i, "El juego que abre el launcher.", "The game the launcher opens.", "Le jeu qu'ouvre le lanceur.", "O jogo que o launcher abre.");
        public static string ClientExistingDesc(Language i)
            => T(i, "Usar un Dofus 3.6 que ya está en este equipo.", "Use a Dofus 3.6 already on this computer.",
                    "Utiliser un Dofus 3.6 déjà présent ici.", "Usar um Dofus 3.6 que já está neste computador.");
        public static string ClientDownloadDesc(Language i)
            => T(i, "Del CDN oficial de Ankama, la versión que usa el emulador.", "From Ankama's official CDN, the version the emulator uses.",
                    "Depuis le CDN officiel d'Ankama, la version qu'utilise l'émulateur.", "Do CDN oficial da Ankama, a versão que o emulador usa.");
        public static string ClientDownloadReadmeDesc(Language i)
            => T(i, "Del enlace que da el README del repositorio.", "From the link in the repository's README.",
                    "Depuis le lien du README du dépôt.", "Do link que está no README do repositório.");
        public static string FromAnkama(Language i, string version, string platform)
            => T(i, $"Dofus {version} para {Platform(platform)} desde el CDN de Ankama", $"Dofus {version} for {Platform(platform)} from Ankama's CDN",
                    $"Dofus {version} pour {Platform(platform)} depuis le CDN d'Ankama", $"Dofus {version} para {Platform(platform)} do CDN da Ankama");
        public static string ReadingFileList(Language i, double fraction)
            => T(i, $"Leyendo la lista de archivos… {fraction:P0}", $"Reading the file list… {fraction:P0}",
                    $"Lecture de la liste des fichiers… {fraction:P0}", $"Lendo a lista de arquivos… {fraction:P0}");
        public static string CheckingWhatIsThere(Language i, string part)
            => T(i, $"Revisando lo que ya hay: {part}", $"Checking what is already there: {part}",
                    $"Vérification de ce qui est déjà là : {part}", $"Conferindo o que já existe: {part}");
        public static string VerifyingPart(Language i, string part)
            => T(i, $"Comprobando {part}", $"Verifying {part}", $"Vérification de {part}", $"Verificando {part}");
        public static string ConnectionRemoteShort(Language i) => T(i, "En otra máquina", "On another machine", "Sur une autre machine", "Em outra máquina");
        public static string ConnectionLocalDesc(Language i)
            => T(i, "El launcher arranca el servidor aquí.", "The launcher starts the server here.", "Le lanceur démarre le serveur ici.",
                    "O launcher inicia o servidor aqui.");
        public static string ConnectionRemoteDesc(Language i)
            => T(i, "Un VPS u otro PC donde ya esté en marcha.", "A VPS or another PC where it already runs.",
                    "Un VPS ou un autre PC où il tourne déjà.", "Um VPS ou outro PC onde ele já esteja rodando.");

        // ─── The client's platform, and the client from Ankama's CDN in the settings ─────────

        /// <summary>A platform as the window names it: the CDN's «windows» and «linux».</summary>
        public static string Platform(string platform) => platform == Setup.ClientCdn.Windows ? "Windows" : "Linux";

        /// <summary>The selector's label: the Windows client on Linux runs through Wine, and says so.</summary>
        public static string PlatformChoice(string platform)
            => platform == Setup.ClientCdn.Windows && !OperatingSystem.IsWindows() ? "Windows (Wine)" : Platform(platform);

        /// <summary>What the chosen platform means on this machine, under the selector.</summary>
        public static string PlatformNote(Language i, string platform)
        {
            if (platform == Setup.ClientCdn.Windows)
                return OperatingSystem.IsWindows() ? ""
                    : T(i, "Se abre con Wine, como en Windows: es el que carga los mods de Jondo.",
                           "Opens through Wine, as on Windows: the one that loads Jondo's mods.",
                           "S'ouvre avec Wine, comme sous Windows : c'est celui qui charge les mods de Jondo.",
                           "Abre com o Wine, como no Windows: é o que carrega os mods do Jondo.");
            return OperatingSystem.IsWindows()
                ? T(i, "Solo para llevarlo a un equipo con Linux: en Windows no se juega.",
                       "Only to take to a Linux computer: it does not play on Windows.",
                       "Seulement pour l'emporter sur un ordinateur sous Linux : il ne se joue pas sous Windows.",
                       "Só para levar a um computador com Linux: não roda no Windows.")
                : T(i, "El cliente nativo de Linux, sin Wine.", "The native Linux client, without Wine.",
                       "Le client Linux natif, sans Wine.", "O cliente nativo do Linux, sem Wine.");
        }

        /// <summary>The tooltip of the Linux choice where it cannot be played: the installer on Windows.</summary>
        public static string LinuxOnlyOnLinux(Language i)
            => T(i, "El cliente de Linux solo se juega desde Linux. Desde Ajustes puedes descargarlo para otro equipo.",
                    "The Linux client only plays on Linux. From the settings you can download it for another computer.",
                    "Le client Linux ne se joue que sous Linux. Depuis les paramètres, tu peux le télécharger pour un autre ordinateur.",
                    "O cliente do Linux só roda no Linux. Nos ajustes você pode baixá-lo para outro computador.");

        public static string ClientNotOnCdn(Language i, string version, string platform)
            => T(i, $"El CDN de Ankama ya no sirve Dofus {version} para {Platform(platform)}. Indica dónde está tu cliente.",
                    $"Ankama's CDN no longer serves Dofus {version} for {Platform(platform)}. Point to your client instead.",
                    $"Le CDN d'Ankama ne sert plus Dofus {version} pour {Platform(platform)}. Indique où est ton client.",
                    $"O CDN da Ankama não serve mais o Dofus {version} para {Platform(platform)}. Indique onde está o seu cliente.");

        public static string ClientCannotRunHere(Language i)
            => T(i, "Ese es el cliente de Linux y aquí solo se juega con el de Windows: elige Dofus.exe.",
                    "That is the Linux client, and only the Windows one plays here: choose Dofus.exe.",
                    "C'est le client Linux, et seul celui de Windows se joue ici : choisis Dofus.exe.",
                    "Esse é o cliente do Linux, e aqui só roda o do Windows: escolha o Dofus.exe.");

        public static string CdnTitle(Language i)
            => T(i, "Descargar del CDN de Ankama", "Download from Ankama's CDN", "Télécharger depuis le CDN d'Ankama", "Baixar do CDN da Ankama");

        /// <summary>What the CDN button will do with the platform chosen.</summary>
        public static string CdnNote(Language i, string platform, string version, bool verifies, string folder)
            => verifies
                ? T(i, $"Comprueba Dofus {version} para {Platform(platform)} y baja solo lo que falte o esté dañado.",
                       $"Checks Dofus {version} for {Platform(platform)} and downloads only what is missing or damaged.",
                       $"Vérifie Dofus {version} pour {Platform(platform)} et ne télécharge que ce qui manque ou est abîmé.",
                       $"Confere o Dofus {version} para {Platform(platform)} e baixa só o que falta ou está danificado.")
                : folder.Length > 0
                    ? T(i, $"Dofus {version} para {Platform(platform)}, en {folder}. Lo que ya esté se conserva.",
                           $"Dofus {version} for {Platform(platform)}, into {folder}. What is already there is kept.",
                           $"Dofus {version} pour {Platform(platform)}, dans {folder}. Ce qui est déjà là est conservé.",
                           $"Dofus {version} para {Platform(platform)}, em {folder}. O que já existe é mantido.")
                    : T(i, $"Dofus {version} para {Platform(platform)}, en la carpeta que elijas.",
                           $"Dofus {version} for {Platform(platform)}, into the folder you choose.",
                           $"Dofus {version} pour {Platform(platform)}, dans le dossier de ton choix.",
                           $"Dofus {version} para {Platform(platform)}, na pasta que você escolher.");

        public static string CdnDownload(Language i) => T(i, "Descargar", "Download", "Télécharger", "Baixar");
        public static string CdnVerify(Language i) => T(i, "Comprobar", "Verify", "Vérifier", "Verificar");

        public static string CdnWhere(Language i, string platform)
            => T(i, $"¿Dónde guardar el cliente de {Platform(platform)}?", $"Where should the {Platform(platform)} client go?",
                    $"Où enregistrer le client {Platform(platform)} ?", $"Onde salvar o cliente do {Platform(platform)}?");

        public static string CdnDownloading(Language i, string platform)
            => T(i, $"Descargando el cliente de {Platform(platform)}", $"Downloading the {Platform(platform)} client",
                    $"Téléchargement du client {Platform(platform)}", $"Baixando o cliente do {Platform(platform)}");

        public static string CdnVerifying(Language i, string platform)
            => T(i, $"Comprobando el cliente de {Platform(platform)}", $"Verifying the {Platform(platform)} client",
                    $"Vérification du client {Platform(platform)}", $"Verificando o cliente do {Platform(platform)}");

        public static string CdnReady(Language i) => T(i, "Cliente listo", "Client ready", "Client prêt", "Cliente pronto");

        public static string CdnReadyNote(Language i, string version, string platform, string folder, bool used)
            => used
                ? T(i, $"Dofus {version} para {Platform(platform)} está completo en {folder} y es el que abre el launcher.",
                       $"Dofus {version} for {Platform(platform)} is complete in {folder}, and the launcher opens it.",
                       $"Dofus {version} pour {Platform(platform)} est complet dans {folder}, et c'est lui qu'ouvre le lanceur.",
                       $"O Dofus {version} para {Platform(platform)} está completo em {folder} e é o que o launcher abre.")
                : T(i, $"Dofus {version} para {Platform(platform)} está completo en {folder}. Cópialo al equipo donde vayas a jugar.",
                       $"Dofus {version} for {Platform(platform)} is complete in {folder}. Copy it to the computer you will play on.",
                       $"Dofus {version} pour {Platform(platform)} est complet dans {folder}. Copie-le sur l'ordinateur où tu joueras.",
                       $"O Dofus {version} para {Platform(platform)} está completo em {folder}. Copie-o para o computador onde vai jogar.");

        public static string CdnBusy(Language i)
            => T(i, "Espera a que termine la descarga del pack.", "Wait for the pack's download to finish.",
                    "Attends la fin du téléchargement du pack.", "Espere terminar o download do pack.");

        public static string CdnModsMissing(Language i)
            => T(i, "Le faltan MelonLoader y los mods de Jondo: «Reparar» los instala.",
                    "It lacks MelonLoader and Jondo's mods: «Repair» installs them.",
                    "Il lui manque MelonLoader et les mods de Jondo : « Réparer » les installe.",
                    "Faltam o MelonLoader e os mods do Jondo: «Reparar» os instala.");

        public static string RepairShort(Language i) => T(i, "Reparar", "Repair", "Réparer", "Reparar");

        public static string StepOf(Language i, int n, int total)
            => T(i, $"PASO {n} DE {total}", $"STEP {n} OF {total}", $"ÉTAPE {n} SUR {total}", $"ETAPA {n} DE {total}");
        public static string StepsDone(Language i, int done, int total)
            => T(i, $"{done} de {total} pasos", $"{done} of {total} steps", $"{done} étapes sur {total}", $"{done} de {total} etapas");
        public static string NextStep(Language i, string title) => T(i, $"A continuación: {title}", $"Next: {title}", $"Ensuite : {title}", $"A seguir: {title}");
        public static string LastStep(Language i) => T(i, "Es el último paso.", "This is the last step.", "C'est la dernière étape.", "É a última etapa.");
        public static string StateRunning(Language i) => T(i, "EN CURSO", "RUNNING", "EN COURS", "EM ANDAMENTO");
        public static string StateDone(Language i) => T(i, "HECHO", "DONE", "TERMINÉ", "CONCLUÍDO");
        public static string StateSkipped(Language i) => T(i, "OMITIDO", "SKIPPED", "IGNORÉ", "IGNORADO");
        public static string StatePending(Language i) => T(i, "PENDIENTE", "PENDING", "EN ATTENTE", "PENDENTE");
        public static string StateFailed(Language i) => T(i, "DETENIDO", "STOPPED", "ARRÊTÉ", "PARADO");
        public static string SkippedDetail(Language i) => T(i, "No hacía falta.", "Not needed.", "Pas nécessaire.", "Não era necessário.");
        public static string PendingDetail(Language i)
            => T(i, "Empieza cuando termine el anterior.", "Starts when the previous one ends.", "Commence quand la précédente se termine.",
                    "Começa quando a anterior terminar.");
    }
}
