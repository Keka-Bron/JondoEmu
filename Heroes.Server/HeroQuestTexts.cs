using System.Globalization;

namespace Heroes.Server;

internal static class HeroQuestTexts
{
    internal static string Get(string language, string key, params object[] args)
    {
        var texts = language.Trim().ToLowerInvariant() switch { "en" => English, "fr" => French, _ => Spanish };
        return string.Format(CultureInfo.InvariantCulture, texts[key], args);
    }

    private static readonly Dictionary<string, string> Spanish = new()
    {
        ["hero.quest.behind"] = "{0} no ha seguido tu misión: {1}. El detalle está en el botón de misiones de la ventana de héroes.",
        ["hero.quest.why.elsewhere"] = "no estaba en tu mapa",
        ["hero.quest.why.infight"] = "estaba en combate",
        ["hero.quest.why.notinthefight"] = "no ganó ese combate contigo",
        ["hero.quest.why.missingitems"] = "le faltan {0} de los objetos pedidos",
        ["hero.quest.why.notshared"] = "este paso no se comparte (el PNJ pide objetos o kamas, o te lleva a otro sitio)",
        ["hero.quest.why.notstarted"] = "no tiene esta misión",
        ["hero.quest.why.behind"] = "no estaba en el mismo paso",
        ["hero.quest.why.requirements"] = "no cumple las condiciones de la misión",
        ["hero.quest.teamshort"] = "Nadie avanza en la misión: a tu equipo le faltan {0} de los objetos pedidos para todos. Desactiva el reparto de misiones de un héroe para avanzar sin él.",
    };

    private static readonly Dictionary<string, string> English = new()
    {
        ["hero.quest.behind"] = "{0} did not follow your quest: {1}. The details are on the quests button of the heroes window.",
        ["hero.quest.why.elsewhere"] = "not on your map",
        ["hero.quest.why.infight"] = "in a fight",
        ["hero.quest.why.notinthefight"] = "not among the winners of that fight",
        ["hero.quest.why.missingitems"] = "{0} of the items asked for are missing",
        ["hero.quest.why.notshared"] = "this step is not shared (the NPC takes items or kamas, or moves you)",
        ["hero.quest.why.notstarted"] = "the quest is not started",
        ["hero.quest.why.behind"] = "not on the same step",
        ["hero.quest.why.requirements"] = "the quest's conditions are not met",
        ["hero.quest.teamshort"] = "Nobody moves on in the quest: your team lacks {0} of the items asked for everyone. Turn a hero's quest sharing off to go on without him.",
    };

    private static readonly Dictionary<string, string> French = new()
    {
        ["hero.quest.behind"] = "{0} n'a pas suivi ta quête : {1}. Le détail est sur le bouton des quêtes de la fenêtre des héros.",
        ["hero.quest.why.elsewhere"] = "il n'était pas sur ta carte",
        ["hero.quest.why.infight"] = "il était en combat",
        ["hero.quest.why.notinthefight"] = "il n'a pas gagné ce combat avec toi",
        ["hero.quest.why.missingitems"] = "il lui manque {0} des objets demandés",
        ["hero.quest.why.notshared"] = "cette étape ne se partage pas (le PNJ prend des objets ou des kamas, ou te déplace)",
        ["hero.quest.why.notstarted"] = "il n'a pas cette quête",
        ["hero.quest.why.behind"] = "il n'était pas à la même étape",
        ["hero.quest.why.requirements"] = "il ne remplit pas les conditions de la quête",
        ["hero.quest.teamshort"] = "Personne n'avance dans la quête : il manque à ton équipe {0} des objets demandés pour tout le monde. Coupe le partage des quêtes d'un héros pour avancer sans lui.",
    };

}
