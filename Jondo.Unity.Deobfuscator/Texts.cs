using Jondo.Unity.Launcher.UI;

namespace Jondo.Unity.Deobfuscator;

/// <summary>The mapping window and provider dialog's labels, in the emulator's three languages.</summary>
/// <remarks>
/// These labels belong to this tool, so they stay outside the shared contract.
/// Spanish is the default; English and French are translations.
/// </remarks>
public sealed class Texts
{
    // ─── Common actions ─────────────────────────────────────────────────────────────────
    public string Cancel { get; init; } = "";
    public string Close { get; init; } = "";
    public string Choose { get; init; } = "";
    public string Failed { get; init; } = "";

    // ─── Model settings ─────────────────────────────────────────────────────────────────
    public string ModelStep { get; init; } = "";
    public string ModelTitle { get; init; } = "";
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
    public string ModelCatalogue { get; init; } = "";

    // ─── Mapping and export ─────────────────────────────────────────────────────────────
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
    public string ExportRun { get; init; } = "";
    public string ExportDoneFormat { get; init; } = "";
    public string ExportOpen { get; init; } = "";
    public string ExportOpenFailed { get; init; } = "";

    public static Texts Get(Language language) => Catalog.GetValueOrDefault(language) ?? Catalog[Language.Es];

    private static readonly Dictionary<Language, Texts> Catalog = new()
    {
        [Language.Es] = new Texts
        {
            Cancel = "CANCELAR",
            Close = "CERRAR",
            Choose = "ELEGIR...",
            Failed = "no ha salido",

            ModelStep = "El modelo",
            ModelTitle = "¿A qué modelo le pregunto?",
            ModelProvider = "Proveedor",
            ModelUrl = "Dirección",
            ModelName = "Modelo",
            ModelKey = "Clave de la API",
            ModelKeyHint = "Se guarda cifrada en tu perfil de usuario, no en el repositorio.",
            ModelAtOnce = "Preguntas a la vez",
            ModelTest = "PROBAR",
            ModelTesting = "probando la conexión...",
            ModelTestOkFormat = "Conecta. {0} modelos disponibles.",
            ModelTestFailFormat = "No conecta: {0}",
            ModelLocalNote = "En tu máquina: no cuesta dinero y no sale nada de aquí.",
            ModelPickFromList = "Elige uno de la lista o escríbelo",
            ModelCatalogue = "Modelos disponibles",

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
            ExportRun = "EXPORTAR",
            ExportDoneFormat = "Escrito en {0}",
            ExportOpen = "ABRIR LA CARPETA",
            ExportOpenFailed = "Los archivos están guardados, pero no se ha podido abrir la carpeta.",
        },

        [Language.En] = new Texts
        {
            Cancel = "CANCEL",
            Close = "CLOSE",
            Choose = "BROWSE...",
            Failed = "it did not work",

            ModelStep = "The model",
            ModelTitle = "Which model should I ask?",
            ModelProvider = "Provider",
            ModelUrl = "Address",
            ModelName = "Model",
            ModelKey = "API key",
            ModelKeyHint = "Stored encrypted in your user profile, never in the repository.",
            ModelAtOnce = "Questions at a time",
            ModelTest = "TEST",
            ModelTesting = "testing the connection...",
            ModelTestOkFormat = "Connected. {0} models available.",
            ModelTestFailFormat = "Cannot connect: {0}",
            ModelLocalNote = "On your machine: costs nothing and nothing leaves here.",
            ModelPickFromList = "Pick one from the list, or type it",
            ModelCatalogue = "Available models",

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
            ExportRun = "EXPORT",
            ExportDoneFormat = "Written to {0}",
            ExportOpen = "OPEN THE FOLDER",
            ExportOpenFailed = "The files are saved, but the folder could not be opened.",
        },

        [Language.Fr] = new Texts
        {
            Cancel = "ANNULER",
            Close = "FERMER",
            Choose = "PARCOURIR...",
            Failed = "ça n'a pas marché",

            ModelStep = "Le modèle",
            ModelTitle = "À quel modèle dois-je demander ?",
            ModelProvider = "Fournisseur",
            ModelUrl = "Adresse",
            ModelName = "Modèle",
            ModelKey = "Clé d'API",
            ModelKeyHint = "Enregistrée chiffrée dans votre profil utilisateur, jamais dans le dépôt.",
            ModelAtOnce = "Questions à la fois",
            ModelTest = "TESTER",
            ModelTesting = "test de la connexion...",
            ModelTestOkFormat = "Connecté. {0} modèles disponibles.",
            ModelTestFailFormat = "Connexion impossible : {0}",
            ModelLocalNote = "Sur votre machine : ça ne coûte rien et rien ne sort d'ici.",
            ModelPickFromList = "Choisissez-en un dans la liste, ou écrivez-le",
            ModelCatalogue = "Modèles disponibles",

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
            ExportRun = "EXPORTER",
            ExportDoneFormat = "Écrit dans {0}",
            ExportOpen = "OUVRIR LE DOSSIER",
            ExportOpenFailed = "Les fichiers sont enregistrés, mais le dossier n'a pas pu être ouvert.",
        },
    };
}
