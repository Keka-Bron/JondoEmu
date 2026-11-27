using System.Globalization;

namespace Cazacabezas.Server;

internal static class HuntTexts
{
    private static readonly Dictionary<string, string> Es = new()
    {
        ["hunt.expired"] = "Tu orden de caza contra {0} ha caducado.",
        ["hunt.escaped"] = "{0} ha desaparecido del mundo: la caza se cierra por ahora.",
        ["hunt.closed"] = "Se acabó el tiempo: {0} se te escapa por ahora. La caza volverá a abrirse más tarde.",
        ["hunt.respite"] = "Los cazacabezas te dan un respiro: ya puedes ocultar tus alas.",
        ["hunt.open"] = "¡{0} ha aparecido! La caza está abierta durante {1} minutos.",
        ["hunt.hunted"] = "¡Un cazacabezas ha puesto precio a tu cabeza! Durante {0} minutos no podrás ocultar tus alas.",
        ["hunt.seen"] = "Han visto a {0} en {1}.",
        ["hunt.seen.fighting"] = "Han visto a {0} combatiendo en {1}.",
        ["hunt.held"] = "Un cazacabezas te sigue la pista: mientras dure la caza no puedes {0}.",
        ["hunt.held.fight"] = "combatir contra nadie más que tu cazador",
        ["hunt.held.koliseo"] = "entrar en el Koliseo",
        ["hunt.held.merkasako"] = "entrar en un Merkasako",
        ["hunt.held.dungeon"] = "entrar en una mazmorra",
        ["hunt.held.house"] = "entrar en una casa",
        ["hunt.taken"] = "Un cazacabezas va tras {0}: mientras dure la caza, solo su cazador puede combatir con él.",
        ["hunt.away"] = "{0} está fuera del mapa del mundo: en el Koliseo, una instancia o una casa.",
        ["hunt.away.last"] = "{0} está fuera del mapa del mundo (en el Koliseo, una instancia o una casa). Lo vieron por última vez en {1}.",
        ["hunt.killed"] = "¡Has cazado a {0}! Cobras {1} chapeuvepés.",
        ["hunt.fallen"] = "El cazacabezas {0} se ha cobrado tu cabeza.",
        ["hunt.survived"] = "Ganas {0} chapeuvepé y {1} puntos de honor por sobrevivir a la caza.",
        ["hunt.failed"] = "{0} ha sobrevivido a tu caza. Tendrás que esperar una hora para otra orden.",
    };

    private static readonly Dictionary<string, string> En = new()
    {
        ["hunt.expired"] = "Your hunt order on {0} has run out.",
        ["hunt.escaped"] = "{0} has left the world: the hunt closes for now.",
        ["hunt.closed"] = "Time is up: {0} gets away for now. The hunt will open again later.",
        ["hunt.respite"] = "The head hunters give you a respite: you may fold your wings again.",
        ["hunt.open"] = "{0} has turned up! The hunt is open for {1} minutes.",
        ["hunt.hunted"] = "A head hunter has put a price on your head! For {0} minutes you cannot fold your wings.",
        ["hunt.seen"] = "{0} was seen at {1}.",
        ["hunt.seen.fighting"] = "{0} was seen fighting at {1}.",
        ["hunt.held"] = "A head hunter is on your trail: while the hunt lasts you cannot {0}.",
        ["hunt.held.fight"] = "fight anybody but your hunter",
        ["hunt.held.koliseo"] = "go into the Kolizeum",
        ["hunt.held.merkasako"] = "go into a havenbag",
        ["hunt.held.dungeon"] = "go into a dungeon",
        ["hunt.held.house"] = "go into a house",
        ["hunt.taken"] = "A head hunter is after {0}: while the hunt lasts, only the hunter may fight them.",
        ["hunt.away"] = "{0} is away from the world map: in the Kolizeum, an instance or a house.",
        ["hunt.away.last"] = "{0} is away from the world map (in the Kolizeum, an instance or a house). Last seen at {1}.",
        ["hunt.killed"] = "You hunted {0} down! You collect {1} chapeuvepés.",
        ["hunt.fallen"] = "The head hunter {0} has taken your head.",
        ["hunt.survived"] = "You earn {0} chapeuvepé and {1} honour points for surviving the hunt.",
        ["hunt.failed"] = "{0} survived your hunt. You will have to wait an hour for another order.",
    };

    private static readonly Dictionary<string, string> Fr = new()
    {
        ["hunt.expired"] = "Ton ordre de traque contre {0} a expiré.",
        ["hunt.escaped"] = "{0} a quitté le monde : la traque se ferme pour l'instant.",
        ["hunt.closed"] = "Le temps est écoulé : {0} t'échappe pour l'instant. La traque rouvrira plus tard.",
        ["hunt.respite"] = "Les chasseurs de têtes t'accordent un répit : tu peux à nouveau replier tes ailes.",
        ["hunt.open"] = "{0} est apparu ! La traque est ouverte pendant {1} minutes.",
        ["hunt.hunted"] = "Un chasseur de têtes a mis ta tête à prix ! Pendant {0} minutes tu ne pourras pas replier tes ailes.",
        ["hunt.seen"] = "{0} a été vu en {1}.",
        ["hunt.seen.fighting"] = "{0} a été vu en plein combat en {1}.",
        ["hunt.held"] = "Un chasseur de têtes est sur ta piste : tant que dure la chasse, tu ne peux pas {0}.",
        ["hunt.held.fight"] = "combattre qui que ce soit d'autre que ton chasseur",
        ["hunt.held.koliseo"] = "entrer au Kolizéum",
        ["hunt.held.merkasako"] = "entrer dans un havre-sac",
        ["hunt.held.dungeon"] = "entrer dans un donjon",
        ["hunt.held.house"] = "entrer dans une maison",
        ["hunt.taken"] = "Un chasseur de têtes traque {0} : tant que dure la chasse, seul son chasseur peut le combattre.",
        ["hunt.away"] = "{0} n'est plus sur la carte du monde : au Kolizéum, dans une instance ou une maison.",
        ["hunt.away.last"] = "{0} n'est plus sur la carte du monde (au Kolizéum, dans une instance ou une maison). Vu pour la dernière fois en {1}.",
        ["hunt.killed"] = "Tu as eu {0} ! Tu touches {1} pévétons.",
        ["hunt.fallen"] = "Le chasseur de têtes {0} a eu ta tête.",
        ["hunt.survived"] = "Tu gagnes {0} pévéton et {1} points d'honneur pour avoir survécu à la traque.",
        ["hunt.failed"] = "{0} a survécu à ta traque. Tu devras attendre une heure pour un autre ordre.",
    };

    internal static string Get(string language, string key, params object[] values)
    {
        var catalog = language.Trim().ToLowerInvariant() switch
        {
            "en" => En,
            "fr" => Fr,
            _ => Es,
        };
        return values.Length == 0 ? catalog[key] : string.Format(CultureInfo.InvariantCulture, catalog[key], values);
    }
}
