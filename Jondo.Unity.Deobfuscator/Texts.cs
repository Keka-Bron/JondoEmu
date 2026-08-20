using Jondo.Unity.Launcher.UI;

namespace Jondo.Unity.Deobfuscator;

/// <summary>
/// Lo que dice la ventana, en los tres idiomas del emulador.
///
/// Mismo mecanismo que <see cref="LauncherTexts"/> —un catálogo por idioma con propiedades de sólo
/// lectura— y por los mismos motivos: lo comprueba el compilador, no hace falta ningún fichero al
/// lado del ejecutable, y una cadena que falte sale vacía en vez de tirar la ventana.
///
/// Va en este proyecto y no en el contrato a propósito. Son noventa cadenas que sólo usa esta
/// herramienta, y el contrato lo cargan también el servidor y el lanzador, que se reparten a la
/// gente: no tienen por qué llevar dentro la explicación de qué es un ensamblado de Cpp2IL.
///
/// El idioma de partida es el español, que es en el que se piensa este proyecto; el inglés y el
/// francés son traducción. Cuando haya duda, manda el español.
/// </summary>
public sealed class Texts
{
    // ─── La ventana ─────────────────────────────────────────────────────────────────────
    public string Title { get; init; } = "";
    public string Subtitle { get; init; } = "";
    public string Next { get; init; } = "";
    public string Back { get; init; } = "";
    public string Cancel { get; init; } = "";
    public string Close { get; init; } = "";
    public string Choose { get; init; } = "";
    public string Skip { get; init; } = "";
    public string Working { get; init; } = "";
    public string Done { get; init; } = "";
    public string Failed { get; init; } = "";
    public string StepFormat { get; init; } = "";

    // ─── Paso 1: de qué va esto ─────────────────────────────────────────────────────────
    public string WelcomeStep { get; init; } = "";
    public string WelcomeTitle { get; init; } = "";
    public string WelcomeBody { get; init; } = "";
    public string WelcomeStart { get; init; } = "";

    // ─── Paso 2: el cliente nuevo ───────────────────────────────────────────────────────
    public string ClientStep { get; init; } = "";
    public string ClientTitle { get; init; } = "";
    public string ClientBody { get; init; } = "";
    public string ClientLabel { get; init; } = "";
    public string ClientNeeded { get; init; } = "";
    public string ClientOk { get; init; } = "";
    public string ClientNoFolder { get; init; } = "";
    public string ClientNoProtocol { get; init; } = "";
    public string ClientNoDump { get; init; } = "";

    // ─── Paso 3: el protocolo viejo ─────────────────────────────────────────────────────
    public string OldStep { get; init; } = "";
    public string OldTitle { get; init; } = "";
    public string OldBody { get; init; } = "";
    public string OldLabel { get; init; } = "";
    public string OldOk { get; init; } = "";
    public string OldMissing { get; init; } = "";
    public string OldSkip { get; init; } = "";

    // ─── Paso 4: leer el código ─────────────────────────────────────────────────────────
    public string IndexStep { get; init; } = "";
    public string IndexTitle { get; init; } = "";
    public string IndexBody { get; init; } = "";
    public string IndexRun { get; init; } = "";
    public string IndexReuse { get; init; } = "";
    public string IndexDone { get; init; } = "";

    // ─── Paso 5: emparejar ──────────────────────────────────────────────────────────────
    public string MatchStep { get; init; } = "";
    public string MatchTitle { get; init; } = "";
    public string MatchBody { get; init; } = "";
    public string MatchRun { get; init; } = "";
    public string MatchDone { get; init; } = "";

    // ─── Paso 6: el modelo ──────────────────────────────────────────────────────────────
    public string ModelStep { get; init; } = "";
    public string ModelTitle { get; init; } = "";
    public string ModelBody { get; init; } = "";
    public string ModelProvider { get; init; } = "";
    public string ModelUrl { get; init; } = "";
    public string ModelName { get; init; } = "";
    public string ModelKey { get; init; } = "";
    public string ModelKeyHint { get; init; } = "";
    public string ModelAtOnce { get; init; } = "";
    public string ModelTest { get; init; } = "";
    public string ModelTesting { get; init; } = "";
    public string ModelTestOkFormat { get; init; } = "";
    public string ModelTestFailFormat { get; init; } = "";
    public string ModelLocalNote { get; init; } = "";
    public string ModelPickFromList { get; init; } = "";

    // ─── Paso 7: preguntar ──────────────────────────────────────────────────────────────
    public string AskStep { get; init; } = "";
    public string AskTitle { get; init; } = "";
    public string AskBody { get; init; } = "";
    public string AskQueueFormat { get; init; } = "";
    public string AskRun { get; init; } = "";
    public string AskStop { get; init; } = "";
    public string AskDoneFormat { get; init; } = "";
    public string AskNothing { get; init; } = "";

    // ─── Paso 8: revisar ────────────────────────────────────────────────────────────────
    public string ReviewStep { get; init; } = "";
    public string ReviewTitle { get; init; } = "";
    public string ReviewBody { get; init; } = "";
    public string ReviewSearch { get; init; } = "";
    public string ReviewAccept { get; init; } = "";
    public string ReviewReject { get; init; } = "";
    public string ReviewEdit { get; init; } = "";
    public string ReviewAll { get; init; } = "";
    public string ReviewPending { get; init; } = "";
    public string ReviewMeasured { get; init; } = "";
    public string ReviewEmulator { get; init; } = "";
    public string ReviewUnresolved { get; init; } = "";
    public string ReviewEmpty { get; init; } = "";
    public string ReviewEvidence { get; init; } = "";
    public string ReviewNoEvidence { get; init; } = "";

    // ─── Paso 9: exportar ───────────────────────────────────────────────────────────────
    public string ExportStep { get; init; } = "";
    public string ExportTitle { get; init; } = "";
    public string ExportBody { get; init; } = "";
    public string ExportRun { get; init; } = "";
    public string ExportDoneFormat { get; init; } = "";
    public string ExportOpen { get; init; } = "";

    // ─── La pantalla única: dos protocolos y un botón ───────────────────────────────────
    public string MapOld { get; init; } = "";
    public string MapNew { get; init; } = "";
    public string MapRun { get; init; } = "";
    public string MapReady { get; init; } = "";
    public string MapMissing { get; init; } = "";
    public string MapAsk { get; init; } = "";
    public string MapAskFormat { get; init; } = "";
    public string MapNoKey { get; init; } = "";
    public string MapByStructure { get; init; } = "";
    public string MapByModel { get; init; } = "";
    public string MapDoubtFormat { get; init; } = "";
    public string MapGone { get; init; } = "";

    // ─── Las confianzas, tal como las declara el modelo ─────────────────────────────────
    public string ConfidenceMeasured { get; init; } = "";
    public string ConfidenceSure { get; init; } = "";
    public string ConfidenceLikely { get; init; } = "";
    public string ConfidenceMaybe { get; init; } = "";
    public string ConfidenceNone { get; init; } = "";

    public static Texts Get(Language language) => Catalog.GetValueOrDefault(language) ?? Catalog[Language.Es];

    private static readonly Dictionary<Language, Texts> Catalog = new()
    {
        [Language.Es] = new Texts
        {
            Title = "JONDO",
            Subtitle = "DESOFUSCADOR",
            Next = "SIGUIENTE",
            Back = "ATRÁS",
            Cancel = "CANCELAR",
            Close = "CERRAR",
            Choose = "ELEGIR...",
            Skip = "SALTAR ESTE PASO",
            Working = "trabajando...",
            Done = "listo",
            Failed = "no ha salido",
            StepFormat = "paso {0} de {1}",

            WelcomeStep = "Empezar",
            WelcomeTitle = "Ponerle nombre a un protocolo que no los tiene",
            WelcomeBody =
                "Ankama rota los nombres del protocolo en cada parche: el mensaje que hoy se llama " +
                "«jsd» mañana se llamará otra cosa. Son más de dos mil, y el emulador usa " +
                "doscientos cincuenta.\n\n" +
                "Esta herramienta te lleva paso a paso: lee el cliente nuevo, lo compara con la " +
                "versión que ya conocías, busca en el código del juego las pistas que se le " +
                "escaparon al ofuscador y, para lo que quede en duda, le pregunta a un modelo de " +
                "lenguaje. Tú decides lo dudoso; nada entra sin que alguien lo mire.\n\n" +
                "Puedes cerrar en cualquier momento: cada paso se guarda al terminarlo.",
            WelcomeStart = "EMPEZAR",

            ClientStep = "Cliente nuevo",
            ClientTitle = "¿Dónde está el cliente que quieres desofuscar?",
            ClientBody =
                "La carpeta donde está «Dofus.exe». De ahí salen las dos cosas que hacen falta: el " +
                "protocolo con sus números de campo y el código del juego que lo usa.",
            ClientLabel = "Carpeta del cliente",
            ClientNeeded =
                "Hace falta que el cliente tenga MelonLoader instalado y arrancado una vez: es " +
                "quien deja el volcado de Cpp2IL que aquí se lee. Si nunca lo has arrancado con " +
                "MelonLoader puesto, hazlo antes y vuelve.",
            ClientOk = "Cliente {0} · {1} mensajes en el protocolo",
            ClientNoFolder = "Esa carpeta no existe.",
            ClientNoProtocol = "Ahí no está «Dofus.exe». ¿Es la carpeta del cliente?",
            ClientNoDump =
                "Falta el volcado de Cpp2IL. Arranca el cliente una vez con MelonLoader puesto y " +
                "vuelve a intentarlo:\n{0}",

            OldStep = "Versión conocida",
            OldTitle = "¿Con qué versión la comparo?",
            OldBody =
                "Una versión anterior de la que ya se sepa algo. Emparejando las dos por su forma " +
                "—los números de campo no se barajan— se arrastra lo que ya se sabía a los nombres " +
                "nuevos. Vale la carpeta de otro cliente o directamente su ensamblado del protocolo.\n\n" +
                "Puedes saltarte este paso: sin él se pierde el arrastre, pero lo demás funciona.",
            OldLabel = "Cliente viejo o su Ankama.Dofus.Protocol.Game.dll",
            OldOk = "Versión vieja: {0} mensajes",
            OldMissing = "Ahí no hay un ensamblado de protocolo que se pueda leer.",
            OldSkip = "No tengo una versión anterior",

            IndexStep = "Leer el código",
            IndexTitle = "Buscar en el juego lo que se le escapó al ofuscador",
            IndexBody =
                "Ankama rota los nombres del protocolo, pero no los del cliente entero: lo que " +
                "Unity necesita por nombre se queda tal cual. Hay clases que conservan métodos " +
                "llamados «WaitProcessMapComplementaryInfo», y lo que esas clases toquen dice de " +
                "qué va.\n\n" +
                "Esto recorre los métodos del cliente y anota, mensaje a mensaje, quién lo usa. " +
                "Tarda menos de un minuto y no hace falta repetirlo mientras no cambie el cliente.",
            IndexRun = "LEER EL CÓDIGO",
            IndexReuse = "Ya está leído, de otra vez. Puedes seguir o volver a leerlo.",
            IndexDone = "{0} mensajes con alguna pista en el código",

            MatchStep = "Emparejar",
            MatchTitle = "Quién es quién entre las dos versiones",
            MatchBody =
                "Se comparan las dos versiones por su forma y por su vecindad: qué campos tiene " +
                "cada mensaje y de quién es campo. Lo que salga con una sola pareja posible se da " +
                "por bueno; lo demás se queda en duda y lo verás luego.",
            MatchRun = "EMPAREJAR",
            MatchDone = "{0} emparejados · {1} ambiguos · {2} sin pareja",

            ModelStep = "El modelo",
            ModelTitle = "¿A qué modelo le pregunto?",
            ModelBody =
                "Para los mensajes que ni la forma ni el código resuelven, se le pasa a un modelo " +
                "de lenguaje un expediente con todo lo que se sabe y se le pide un nombre, con su " +
                "confianza y en qué se basa.\n\n" +
                "Puede ser un modelo de pago o uno que corra en tu propia máquina. Si no quieres " +
                "usar ninguno, sáltate este paso: lo demás ya ha hecho su trabajo.",
            ModelProvider = "Proveedor",
            ModelUrl = "Dirección",
            ModelName = "Modelo",
            ModelKey = "Clave de la API",
            ModelKeyHint = "Se guarda cifrada contra tu cuenta de Windows, no en el repositorio.",
            ModelAtOnce = "Preguntas a la vez",
            ModelTest = "PROBAR",
            ModelTesting = "probando la conexión...",
            ModelTestOkFormat = "Conecta. {0} modelos disponibles.",
            ModelTestFailFormat = "No conecta: {0}",
            ModelLocalNote = "En tu máquina: no cuesta dinero y no sale nada de aquí.",
            ModelPickFromList = "Elige uno de la lista o escríbelo",

            AskStep = "Preguntar",
            AskTitle = "Los expedientes, delante del modelo",
            AskBody =
                "Sólo se pregunta por los mensajes que tienen algo que contar. Pagar por uno del " +
                "que no se sabe nada es pagar por un «no lo sé» que ya sabíamos.\n\n" +
                "Lo que conteste se guarda: si paras a mitad y vuelves, no se paga dos veces.",
            AskQueueFormat = "{0} mensajes por preguntar",
            AskRun = "PREGUNTAR",
            AskStop = "PARAR",
            AskDoneFormat = "{0} contestadas · {1} con nombre",
            AskNothing = "No queda ninguno que merezca la pena preguntar.",

            ReviewStep = "Revisar",
            ReviewTitle = "Lo dudoso lo decides tú",
            ReviewBody =
                "Cada propuesta viene con en qué se basa. Acepta lo que te convenza y rechaza lo " +
                "que no: sólo lo aceptado sale en la exportación.",
            ReviewSearch = "buscar...",
            ReviewAccept = "ACEPTAR",
            ReviewReject = "RECHAZAR",
            ReviewEdit = "Corregir el nombre",
            ReviewAll = "Todos",
            ReviewPending = "Por revisar",
            ReviewMeasured = "Medidos",
            ReviewEmulator = "Los del emulador",
            ReviewUnresolved = "Sin resolver",
            ReviewEmpty = "No hay ninguno que cumpla eso.",
            ReviewEvidence = "En qué se basa",
            ReviewNoEvidence = "Nada. Ni el código ni las capturas dicen nada de este mensaje.",

            ExportStep = "Exportar",
            ExportTitle = "Llevarse el resultado",
            ExportBody =
                "Se escriben dos cosas: la tabla de nombres, para leerla, y una clase de C# con " +
                "una constante por opcode, para que el emulador deje de tener «jsd» escrito a " +
                "pelo por todas partes. El día del parche siguiente eso es lo que hay que " +
                "regenerar, y nada más.",
            ExportRun = "EXPORTAR",
            ExportDoneFormat = "Escrito en {0}",
            ExportOpen = "ABRIR LA CARPETA",

            MapOld = "El protocolo que ya conoces (carpeta del cliente viejo, o su .dll)",
            MapNew = "El protocolo nuevo (carpeta del cliente nuevo, o su .dll)",
            MapRun = "HACER EL MAPEO",
            MapReady = "Dale las dos versiones y pulsa el botón.",
            MapMissing = "No encuentro el protocolo en una de las dos rutas.",
            MapAsk = "PREGUNTAR A LA IA",
            MapAskFormat = "PREGUNTAR A LA IA POR LOS {0} DUDOSOS",
            MapNoKey = "Para resolver las dudas hace falta un modelo. Configúralo aquí.",
            MapByStructure = "estructura",
            MapByModel = "el modelo eligió",
            MapDoubtFormat = "duda entre {0}",
            MapGone = "retirado",

            ConfidenceMeasured = "medida",
            ConfidenceSure = "segura",
            ConfidenceLikely = "probable",
            ConfidenceMaybe = "posible",
            ConfidenceNone = "ninguna",
        },

        [Language.En] = new Texts
        {
            Title = "JONDO",
            Subtitle = "DEOBFUSCATOR",
            Next = "NEXT",
            Back = "BACK",
            Cancel = "CANCEL",
            Close = "CLOSE",
            Choose = "BROWSE...",
            Skip = "SKIP THIS STEP",
            Working = "working...",
            Done = "done",
            Failed = "it did not work",
            StepFormat = "step {0} of {1}",

            WelcomeStep = "Start",
            WelcomeTitle = "Putting names to a protocol that has none",
            WelcomeBody =
                "Ankama rotates the protocol names on every patch: the message called «jsd» today " +
                "will be called something else tomorrow. There are over two thousand of them, and " +
                "the emulator uses two hundred and fifty.\n\n" +
                "This tool walks you through it: it reads the new client, compares it against the " +
                "version you already knew, digs through the game code for the clues the obfuscator " +
                "missed and, for whatever is left in doubt, asks a language model. You decide the " +
                "doubtful ones; nothing goes in without someone looking at it.\n\n" +
                "You can close at any time: every step is saved when you finish it.",
            WelcomeStart = "START",

            ClientStep = "New client",
            ClientTitle = "Where is the client you want to deobfuscate?",
            ClientBody =
                "The folder holding «Dofus.exe». Both things needed come from there: the protocol " +
                "with its field numbers, and the game code that uses it.",
            ClientLabel = "Client folder",
            ClientNeeded =
                "The client needs MelonLoader installed and started at least once: it is what " +
                "leaves behind the Cpp2IL dump this reads. If you have never run it with " +
                "MelonLoader in place, do that first and come back.",
            ClientOk = "Client {0} · {1} messages in the protocol",
            ClientNoFolder = "That folder does not exist.",
            ClientNoProtocol = "No «Dofus.exe» in there. Is that the client folder?",
            ClientNoDump =
                "The Cpp2IL dump is missing. Start the client once with MelonLoader in place and " +
                "try again:\n{0}",

            OldStep = "Known version",
            OldTitle = "Which version should I compare it against?",
            OldBody =
                "An earlier version something is already known about. Matching the two by shape " +
                "—field numbers are not shuffled— drags what was already known onto the new names. " +
                "Another client folder will do, or its protocol assembly directly.\n\n" +
                "You can skip this step: without it the dragging is lost, but the rest still works.",
            OldLabel = "Old client, or its Ankama.Dofus.Protocol.Game.dll",
            OldOk = "Old version: {0} messages",
            OldMissing = "There is no readable protocol assembly there.",
            OldSkip = "I have no earlier version",

            IndexStep = "Read the code",
            IndexTitle = "Digging out what the obfuscator missed",
            IndexBody =
                "Ankama rotates the protocol names, but not the whole client: whatever Unity needs " +
                "by name is left alone. There are classes that still hold methods called " +
                "«WaitProcessMapComplementaryInfo», and whatever those classes touch tells you what " +
                "it is about.\n\n" +
                "This walks every method in the client and notes, message by message, who uses it. " +
                "It takes under a minute and does not need repeating unless the client changes.",
            IndexRun = "READ THE CODE",
            IndexReuse = "Already read, from a previous run. Carry on, or read it again.",
            IndexDone = "{0} messages with some clue in the code",

            MatchStep = "Match",
            MatchTitle = "Who is who across the two versions",
            MatchBody =
                "The two versions are compared by shape and by neighbourhood: which fields each " +
                "message has, and whose field it is. Anything with a single possible partner is " +
                "taken as settled; the rest stays in doubt and you will see it later.",
            MatchRun = "MATCH",
            MatchDone = "{0} matched · {1} ambiguous · {2} unmatched",

            ModelStep = "The model",
            ModelTitle = "Which model should I ask?",
            ModelBody =
                "For the messages that neither shape nor code settles, a language model is handed a " +
                "dossier with everything known and asked for a name, with its confidence and what " +
                "it is based on.\n\n" +
                "It can be a paid model or one running on your own machine. If you would rather not " +
                "use one, skip this step: everything else has already done its work.",
            ModelProvider = "Provider",
            ModelUrl = "Address",
            ModelName = "Model",
            ModelKey = "API key",
            ModelKeyHint = "Stored encrypted against your Windows account, never in the repository.",
            ModelAtOnce = "Questions at a time",
            ModelTest = "TEST",
            ModelTesting = "testing the connection...",
            ModelTestOkFormat = "Connected. {0} models available.",
            ModelTestFailFormat = "Cannot connect: {0}",
            ModelLocalNote = "On your machine: costs nothing and nothing leaves here.",
            ModelPickFromList = "Pick one from the list, or type it",

            AskStep = "Ask",
            AskTitle = "The dossiers, in front of the model",
            AskBody =
                "Only messages with something to say get asked about. Paying for one nothing is " +
                "known about is paying for an «I don't know» we already had.\n\n" +
                "Whatever comes back is kept: stop halfway and come back, and it is not paid twice.",
            AskQueueFormat = "{0} messages left to ask about",
            AskRun = "ASK",
            AskStop = "STOP",
            AskDoneFormat = "{0} answered · {1} with a name",
            AskNothing = "There is nothing left worth asking about.",

            ReviewStep = "Review",
            ReviewTitle = "The doubtful ones are yours to decide",
            ReviewBody =
                "Every proposal comes with what it is based on. Accept what convinces you and " +
                "reject what does not: only accepted names make it into the export.",
            ReviewSearch = "search...",
            ReviewAccept = "ACCEPT",
            ReviewReject = "REJECT",
            ReviewEdit = "Fix the name",
            ReviewAll = "All",
            ReviewPending = "To review",
            ReviewMeasured = "Measured",
            ReviewEmulator = "Emulator's",
            ReviewUnresolved = "Unresolved",
            ReviewEmpty = "Nothing matches that.",
            ReviewEvidence = "What it is based on",
            ReviewNoEvidence = "Nothing. Neither the code nor the captures say anything about this message.",

            ExportStep = "Export",
            ExportTitle = "Taking the result away",
            ExportBody =
                "Two things get written: the table of names, to read, and a C# class with one " +
                "constant per opcode, so the emulator stops having «jsd» hardcoded all over the " +
                "place. On the day of the next patch that is what needs regenerating, and nothing " +
                "else.",
            ExportRun = "EXPORT",
            ExportDoneFormat = "Written to {0}",
            ExportOpen = "OPEN THE FOLDER",

            MapOld = "The protocol you already know (old client folder, or its .dll)",
            MapNew = "The new protocol (new client folder, or its .dll)",
            MapRun = "BUILD THE MAPPING",
            MapReady = "Give it both versions and press the button.",
            MapMissing = "I cannot find the protocol in one of the two paths.",
            MapAsk = "ASK THE AI",
            MapAskFormat = "ASK THE AI ABOUT THE {0} DOUBTFUL ONES",
            MapNoKey = "Resolving the doubts needs a model. Set one up here.",
            MapByStructure = "structure",
            MapByModel = "the model chose",
            MapDoubtFormat = "one of {0}",
            MapGone = "gone",

            ConfidenceMeasured = "measured",
            ConfidenceSure = "certain",
            ConfidenceLikely = "likely",
            ConfidenceMaybe = "possible",
            ConfidenceNone = "none",
        },

        [Language.Fr] = new Texts
        {
            Title = "JONDO",
            Subtitle = "DÉSOBFUSCATEUR",
            Next = "SUIVANT",
            Back = "RETOUR",
            Cancel = "ANNULER",
            Close = "FERMER",
            Choose = "PARCOURIR...",
            Skip = "PASSER CETTE ÉTAPE",
            Working = "en cours...",
            Done = "terminé",
            Failed = "ça n'a pas marché",
            StepFormat = "étape {0} sur {1}",

            WelcomeStep = "Commencer",
            WelcomeTitle = "Donner un nom à un protocole qui n'en a pas",
            WelcomeBody =
                "Ankama fait tourner les noms du protocole à chaque patch : le message appelé " +
                "« jsd » aujourd'hui s'appellera autrement demain. Il y en a plus de deux mille, et " +
                "l'émulateur en utilise deux cent cinquante.\n\n" +
                "Cet outil vous guide pas à pas : il lit le nouveau client, le compare à la version " +
                "que vous connaissiez déjà, cherche dans le code du jeu les indices qui ont échappé " +
                "à l'obfuscateur et, pour ce qui reste douteux, interroge un modèle de langage. " +
                "C'est vous qui tranchez les cas douteux ; rien n'entre sans que quelqu'un l'ait vu.\n\n" +
                "Vous pouvez fermer à tout moment : chaque étape est enregistrée en la terminant.",
            WelcomeStart = "COMMENCER",

            ClientStep = "Nouveau client",
            ClientTitle = "Où se trouve le client à désobfusquer ?",
            ClientBody =
                "Le dossier qui contient « Dofus.exe ». C'est de là que viennent les deux choses " +
                "nécessaires : le protocole avec ses numéros de champ, et le code du jeu qui s'en sert.",
            ClientLabel = "Dossier du client",
            ClientNeeded =
                "Il faut que le client ait MelonLoader installé et lancé au moins une fois : c'est " +
                "lui qui laisse le dump de Cpp2IL que l'on lit ici. Si vous ne l'avez jamais lancé " +
                "avec MelonLoader en place, faites-le d'abord et revenez.",
            ClientOk = "Client {0} · {1} messages dans le protocole",
            ClientNoFolder = "Ce dossier n'existe pas.",
            ClientNoProtocol = "Pas de « Dofus.exe » là-dedans. C'est bien le dossier du client ?",
            ClientNoDump =
                "Le dump de Cpp2IL manque. Lancez le client une fois avec MelonLoader en place et " +
                "réessayez :\n{0}",

            OldStep = "Version connue",
            OldTitle = "Avec quelle version dois-je comparer ?",
            OldBody =
                "Une version antérieure dont on sait déjà quelque chose. En appariant les deux par " +
                "leur forme —les numéros de champ ne sont pas mélangés— on reporte ce que l'on " +
                "savait déjà sur les nouveaux noms. Le dossier d'un autre client fait l'affaire, ou " +
                "directement son assembly de protocole.\n\n" +
                "Vous pouvez passer cette étape : sans elle on perd le report, mais le reste marche.",
            OldLabel = "Ancien client, ou son Ankama.Dofus.Protocol.Game.dll",
            OldOk = "Ancienne version : {0} messages",
            OldMissing = "Il n'y a pas là d'assembly de protocole lisible.",
            OldSkip = "Je n'ai pas de version antérieure",

            IndexStep = "Lire le code",
            IndexTitle = "Chercher dans le jeu ce qui a échappé à l'obfuscateur",
            IndexBody =
                "Ankama fait tourner les noms du protocole, mais pas ceux du client entier : ce dont " +
                "Unity a besoin par son nom reste tel quel. Certaines classes gardent des méthodes " +
                "appelées « WaitProcessMapComplementaryInfo », et ce que ces classes touchent dit de " +
                "quoi il s'agit.\n\n" +
                "Ceci parcourt les méthodes du client et note, message par message, qui l'utilise. " +
                "Cela prend moins d'une minute et il est inutile de recommencer tant que le client " +
                "ne change pas.",
            IndexRun = "LIRE LE CODE",
            IndexReuse = "Déjà lu, lors d'une autre fois. Continuez, ou relisez-le.",
            IndexDone = "{0} messages avec un indice dans le code",

            MatchStep = "Apparier",
            MatchTitle = "Qui est qui entre les deux versions",
            MatchBody =
                "Les deux versions sont comparées par leur forme et par leur voisinage : quels " +
                "champs a chaque message, et de qui il est le champ. Ce qui ne sort qu'avec un seul " +
                "partenaire possible est retenu ; le reste reste douteux et vous le verrez ensuite.",
            MatchRun = "APPARIER",
            MatchDone = "{0} appariés · {1} ambigus · {2} sans partenaire",

            ModelStep = "Le modèle",
            ModelTitle = "À quel modèle dois-je demander ?",
            ModelBody =
                "Pour les messages que ni la forme ni le code ne résolvent, on présente à un modèle " +
                "de langage un dossier avec tout ce que l'on sait et on lui demande un nom, avec sa " +
                "confiance et ce sur quoi il se fonde.\n\n" +
                "Ce peut être un modèle payant ou un modèle qui tourne sur votre machine. Si vous " +
                "n'en voulez aucun, passez cette étape : le reste a déjà fait son travail.",
            ModelProvider = "Fournisseur",
            ModelUrl = "Adresse",
            ModelName = "Modèle",
            ModelKey = "Clé d'API",
            ModelKeyHint = "Enregistrée chiffrée contre votre compte Windows, jamais dans le dépôt.",
            ModelAtOnce = "Questions à la fois",
            ModelTest = "TESTER",
            ModelTesting = "test de la connexion...",
            ModelTestOkFormat = "Connecté. {0} modèles disponibles.",
            ModelTestFailFormat = "Connexion impossible : {0}",
            ModelLocalNote = "Sur votre machine : ça ne coûte rien et rien ne sort d'ici.",
            ModelPickFromList = "Choisissez-en un dans la liste, ou écrivez-le",

            AskStep = "Demander",
            AskTitle = "Les dossiers, devant le modèle",
            AskBody =
                "On ne demande que pour les messages qui ont quelque chose à dire. Payer pour un " +
                "message dont on ne sait rien, c'est payer un « je ne sais pas » que l'on avait déjà.\n\n" +
                "Ce qu'il répond est conservé : si vous arrêtez en cours de route et revenez, ce " +
                "n'est pas payé deux fois.",
            AskQueueFormat = "{0} messages à demander",
            AskRun = "DEMANDER",
            AskStop = "ARRÊTER",
            AskDoneFormat = "{0} répondus · {1} avec un nom",
            AskNothing = "Il ne reste rien qui vaille la peine d'être demandé.",

            ReviewStep = "Relire",
            ReviewTitle = "Les cas douteux, c'est vous qui tranchez",
            ReviewBody =
                "Chaque proposition vient avec ce sur quoi elle se fonde. Acceptez ce qui vous " +
                "convainc et rejetez le reste : seul l'accepté part à l'export.",
            ReviewSearch = "chercher...",
            ReviewAccept = "ACCEPTER",
            ReviewReject = "REJETER",
            ReviewEdit = "Corriger le nom",
            ReviewAll = "Tous",
            ReviewPending = "À relire",
            ReviewMeasured = "Mesurés",
            ReviewEmulator = "Ceux de l'émulateur",
            ReviewUnresolved = "Non résolus",
            ReviewEmpty = "Rien ne correspond.",
            ReviewEvidence = "Ce sur quoi ça se fonde",
            ReviewNoEvidence = "Rien. Ni le code ni les captures ne disent quoi que ce soit de ce message.",

            ExportStep = "Exporter",
            ExportTitle = "Emporter le résultat",
            ExportBody =
                "Deux choses sont écrites : la table des noms, à lire, et une classe C# avec une " +
                "constante par opcode, pour que l'émulateur cesse d'avoir « jsd » écrit en dur " +
                "partout. Le jour du patch suivant, c'est cela qu'il faut régénérer, et rien d'autre.",
            ExportRun = "EXPORTER",
            ExportDoneFormat = "Écrit dans {0}",
            ExportOpen = "OUVRIR LE DOSSIER",

            MapOld = "Le protocole que vous connaissez déjà (dossier de l'ancien client, ou son .dll)",
            MapNew = "Le nouveau protocole (dossier du nouveau client, ou son .dll)",
            MapRun = "CONSTRUIRE LE MAPPAGE",
            MapReady = "Donnez-lui les deux versions et appuyez sur le bouton.",
            MapMissing = "Je ne trouve pas le protocole dans l'un des deux chemins.",
            MapAsk = "DEMANDER À L'IA",
            MapAskFormat = "DEMANDER À L'IA POUR LES {0} DOUTEUX",
            MapNoKey = "Résoudre les doutes demande un modèle. Configurez-le ici.",
            MapByStructure = "structure",
            MapByModel = "le modèle a choisi",
            MapDoubtFormat = "un parmi {0}",
            MapGone = "retiré",

            ConfidenceMeasured = "mesurée",
            ConfidenceSure = "certaine",
            ConfidenceLikely = "probable",
            ConfidenceMaybe = "possible",
            ConfidenceNone = "aucune",
        },
    };
}
