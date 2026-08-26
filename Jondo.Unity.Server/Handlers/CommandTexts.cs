using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text.RegularExpressions;
using Jondo.Unity.Server.Network;

namespace Jondo.Unity.Server.Handlers
{
    /// <summary>
    /// Player-facing command replies. The server console stays in its diagnostic language; only
    /// text sent to the game client is selected from this catalogue.
    /// </summary>
    internal static class CommandTexts
    {
        private static readonly IReadOnlyDictionary<string, string> Es = new Dictionary<string, string>
        {
            ["usage.kamas"] = "Uso: .kamas <cantidad> — por ejemplo .kamas 10000 (con menos delante, resta)",
            ["usage.level"] = "Uso: .level <nivel> — por ejemplo .level 200",
            ["usage.teleport"] = "Uso: .teleport [x,y] — por ejemplo .teleport [-1,0]",
            ["usage.relative"] = "Uso: .relative — pasa al siguiente MapId de las mismas coordenadas",
            ["usage.shop"] = "Uso: .shop — sin nada detrás",
            ["usage.size"] = "Uso: .size <n> — 100 es el tamaño normal, por ejemplo .size 200",
            ["usage.item"] = "Uso: .item <id> [cantidad] — por ejemplo .item 10784 1",
            ["usage.itemset"] = "Uso: .itemset <id de panoplia> — por ejemplo .itemset 1",
            ["usage.packets"] = "Uso: .packets [cuántos] — lo que el cliente manda y no atendemos",
            ["command.unknown"] = "El comando {0} no existe. Los que hay: {1}.",
            ["command.denied"] = "No tienes permiso para usar {0}.",
            ["command.failed"] = "El comando {0} ha fallado: {1}",
            ["kamas.result"] = "Kamas: {0} ({1}{2}).",
            ["level.requested"] = " (se pidió {0})",
            ["level.omega"] = " Por encima de 200 no se reparten más puntos de característica.",
            ["level.result"] = "Nivel {0}{1}, antes {2}. Experiencia {3}. Puntos libres {4} de {5}. {6}{7}",
            ["spells.table_missing"] = "Hechizos sin tocar: la tabla de hechizos no está cargada.",
            ["spells.breed_missing"] = "Hechizos sin tocar: la raza {0} no tiene ninguno en los datos.",
            ["spells.result"] = "Hechizos: {0} (+{1}, -{2}, {3} cambian de grado).",
            ["teleport.in_fight"] = "No se puede teleportar en combate.",
            ["teleport.no_map"] = "No hay ningún mapa en [{0},{1}].",
            ["teleport.load_failed"] = "El mapa {0} de [{1},{2}] no se puede cargar.",
            ["teleport.multiple"] = " Había {0} mapas en esas coordenadas; se ha elegido el de la subzona con más casillas andables ({1}).",
            ["teleport.result"] = "En [{0},{1}]: mapa {2}, {3}, casilla {4}.{5}",
            ["map.subarea"] = "subzona {0}",
            ["relative.current_missing"] = "El mapa actual {0} no está en MapPositions.",
            ["relative.none"] = "No hay otro MapId en [{0},{1}].",
            ["relative.load_failed"] = "El mapa relativo {0} no se puede cargar.",
            ["relative.wrapped"] = " Vuelta al primer MapId.",
            ["relative.result"] = "Relative [{0},{1}]: {2} -> {3} ({4}/{5}), {6}, casilla {7}.{8}",
            ["shop.no_npcs"] = "No hay ningún NPC colocado en la base: no sé a qué mapa llevarte.",
            ["shop.map_missing"] = "El mapa de los vendedores ({0}) no está en los datos del mundo.",
            ["shop.unknown_place"] = "sitio desconocido",
            ["shop.result"] = "A la tienda: mapa {0} ({1}), {2} NPC, casilla {3}.",
            ["size.character_missing"] = "No encuentro tu personaje en la base para redibujarlo.",
            ["size.clamped"] = " (se pidió {0}; el tope va de {1} a {2})",
            ["size.result"] = "Tamaño {0} %{1}. El normal es {2}.",
            ["item.quantity"] = "La cantidad debe ser mayor que cero.",
            ["item.template_missing"] = "No existe la plantilla de objeto {0}.",
            ["item.added"] = "Objeto {0} x{1} añadido al inventario.",
            ["itemset.missing"] = "No existe la panoplia {0}.",
            ["itemset.templates_missing"] = " Plantillas ausentes: {0}.",
            ["itemset.added"] = "Panoplia {0}: {1}/{2} objetos añadidos.{3}",
            ["packets.none"] = "No hay ningún paquete sin atender apuntado.",
            ["packets.summary"] = "{0} forma(s) de {1} opcode(s): {2} sin atender, {3} silenciada(s), {4} ilegible(s)",
            ["packets.silenced"] = "silenciado",
            ["packets.undecodable"] = "ilegible",
            ["packets.unhandled"] = "sin atender",
        };

        private static readonly IReadOnlyDictionary<string, string> En = new Dictionary<string, string>
        {
            ["usage.kamas"] = "Usage: .kamas <amount> — for example .kamas 10000 (a negative amount removes kamas)",
            ["usage.level"] = "Usage: .level <level> — for example .level 200",
            ["usage.teleport"] = "Usage: .teleport [x,y] — for example .teleport [-1,0]",
            ["usage.relative"] = "Usage: .relative — move to the next MapId at the same coordinates",
            ["usage.shop"] = "Usage: .shop — with no argument",
            ["usage.size"] = "Usage: .size <n> — 100 is the normal size, for example .size 200",
            ["usage.item"] = "Usage: .item <id> [quantity] — for example .item 10784 1",
            ["usage.itemset"] = "Usage: .itemset <set id> — for example .itemset 1",
            ["usage.packets"] = "Usage: .packets [count] — client messages the server does not handle",
            ["command.unknown"] = "Command {0} does not exist. Available commands: {1}.",
            ["command.denied"] = "You do not have permission to use {0}.",
            ["command.failed"] = "Command {0} failed: {1}",
            ["kamas.result"] = "Kamas: {0} ({1}{2}).",
            ["level.requested"] = " (requested {0})",
            ["level.omega"] = " Levels above 200 do not grant characteristic points.",
            ["level.result"] = "Level {0}{1}, previously {2}. Experience {3}. Free points {4} of {5}. {6}{7}",
            ["spells.table_missing"] = "Spells unchanged: the spell table is not loaded.",
            ["spells.breed_missing"] = "Spells unchanged: breed {0} has no spells in the data.",
            ["spells.result"] = "Spells: {0} (+{1}, -{2}, {3} changed grade).",
            ["teleport.in_fight"] = "You cannot teleport during a fight.",
            ["teleport.no_map"] = "There is no map at [{0},{1}].",
            ["teleport.load_failed"] = "Map {0} at [{1},{2}] cannot be loaded.",
            ["teleport.multiple"] = " There were {0} maps at those coordinates; the sub-area with the most walkable cells was selected ({1}).",
            ["teleport.result"] = "At [{0},{1}]: map {2}, {3}, cell {4}.{5}",
            ["map.subarea"] = "sub-area {0}",
            ["relative.current_missing"] = "Current map {0} is not in MapPositions.",
            ["relative.none"] = "There is no other MapId at [{0},{1}].",
            ["relative.load_failed"] = "Relative map {0} cannot be loaded.",
            ["relative.wrapped"] = " Back to the first MapId.",
            ["relative.result"] = "Relative [{0},{1}]: {2} -> {3} ({4}/{5}), {6}, cell {7}.{8}",
            ["shop.no_npcs"] = "There are no NPC spawns in the database, so there is no shop map to use.",
            ["shop.map_missing"] = "The vendor map ({0}) is missing from the world data.",
            ["shop.unknown_place"] = "unknown location",
            ["shop.result"] = "Shop: map {0} ({1}), {2} NPC, cell {3}.",
            ["size.character_missing"] = "Your character could not be found in the database for its look refresh.",
            ["size.clamped"] = " (requested {0}; limits are {1} to {2})",
            ["size.result"] = "Size {0} %{1}. Normal size is {2}.",
            ["item.quantity"] = "Quantity must be greater than zero.",
            ["item.template_missing"] = "Item template {0} does not exist.",
            ["item.added"] = "Item {0} x{1} added to the inventory.",
            ["itemset.missing"] = "Item set {0} does not exist.",
            ["itemset.templates_missing"] = " Missing templates: {0}.",
            ["itemset.added"] = "Item set {0}: {1}/{2} items added.{3}",
            ["packets.none"] = "No unhandled packets have been recorded.",
            ["packets.summary"] = "{0} shape(s) from {1} opcode(s): {2} unhandled, {3} silenced, {4} undecodable",
            ["packets.silenced"] = "silenced",
            ["packets.undecodable"] = "undecodable",
            ["packets.unhandled"] = "unhandled",
        };

        private static readonly IReadOnlyDictionary<string, string> Fr = new Dictionary<string, string>
        {
            ["usage.kamas"] = "Utilisation : .kamas <quantité> — exemple : .kamas 10000 (une valeur négative en retire)",
            ["usage.level"] = "Utilisation : .level <niveau> — exemple : .level 200",
            ["usage.teleport"] = "Utilisation : .teleport [x,y] — exemple : .teleport [-1,0]",
            ["usage.relative"] = "Utilisation : .relative — passe au MapId suivant aux mêmes coordonnées",
            ["usage.shop"] = "Utilisation : .shop — sans argument",
            ["usage.size"] = "Utilisation : .size <n> — 100 est la taille normale, exemple : .size 200",
            ["usage.item"] = "Utilisation : .item <id> [quantité] — exemple : .item 10784 1",
            ["usage.itemset"] = "Utilisation : .itemset <id de panoplie> — exemple : .itemset 1",
            ["usage.packets"] = "Utilisation : .packets [nombre] — messages du client non traités",
            ["command.unknown"] = "La commande {0} n'existe pas. Commandes disponibles : {1}.",
            ["command.denied"] = "Tu n'as pas la permission d'utiliser {0}.",
            ["command.failed"] = "La commande {0} a échoué : {1}",
            ["kamas.result"] = "Kamas : {0} ({1}{2}).",
            ["level.requested"] = " (niveau demandé : {0})",
            ["level.omega"] = " Les niveaux supérieurs à 200 ne donnent pas de points de caractéristique.",
            ["level.result"] = "Niveau {0}{1}, auparavant {2}. Expérience {3}. Points disponibles : {4}/{5}. {6}{7}",
            ["spells.table_missing"] = "Sorts inchangés : la table des sorts n'est pas chargée.",
            ["spells.breed_missing"] = "Sorts inchangés : la classe {0} n'en possède aucun dans les données.",
            ["spells.result"] = "Sorts : {0} (+{1}, -{2}, {3} changent de rang).",
            ["teleport.in_fight"] = "Téléportation impossible pendant un combat.",
            ["teleport.no_map"] = "Aucune carte trouvée en [{0},{1}].",
            ["teleport.load_failed"] = "La carte {0} en [{1},{2}] ne peut pas être chargée.",
            ["teleport.multiple"] = " {0} cartes existent à ces coordonnées ; celle de la sous-zone avec le plus de cellules praticables a été choisie ({1}).",
            ["teleport.result"] = "En [{0},{1}] : carte {2}, {3}, cellule {4}.{5}",
            ["map.subarea"] = "sous-zone {0}",
            ["relative.current_missing"] = "La carte actuelle {0} est absente de MapPositions.",
            ["relative.none"] = "Aucun autre MapId en [{0},{1}].",
            ["relative.load_failed"] = "La carte relative {0} ne peut pas être chargée.",
            ["relative.wrapped"] = " Retour au premier MapId.",
            ["relative.result"] = "Relative [{0},{1}] : {2} -> {3} ({4}/{5}), {6}, cellule {7}.{8}",
            ["shop.no_npcs"] = "Aucun PNJ vendeur n'est enregistré dans la base.",
            ["shop.map_missing"] = "La carte des vendeurs ({0}) est absente des données du monde.",
            ["shop.unknown_place"] = "lieu inconnu",
            ["shop.result"] = "Boutique : carte {0} ({1}), {2} PNJ, cellule {3}.",
            ["size.character_missing"] = "Ton personnage est introuvable dans la base pour actualiser son apparence.",
            ["size.clamped"] = " (taille demandée : {0} ; limites : {1} à {2})",
            ["size.result"] = "Taille : {0} %{1}. Taille normale : {2}.",
            ["item.quantity"] = "La quantité doit être supérieure à zéro.",
            ["item.template_missing"] = "Le modèle d'objet {0} n'existe pas.",
            ["item.added"] = "Objet {0} x{1} ajouté à l'inventaire.",
            ["itemset.missing"] = "La panoplie {0} n'existe pas.",
            ["itemset.templates_missing"] = " Modèles absents : {0}.",
            ["itemset.added"] = "Panoplie {0} : {1}/{2} objets ajoutés.{3}",
            ["packets.none"] = "Aucun paquet non traité n'a été enregistré.",
            ["packets.summary"] = "{0} forme(s) pour {1} opcode(s) : {2} non traitée(s), {3} ignorée(s), {4} illisible(s)",
            ["packets.silenced"] = "ignoré",
            ["packets.undecodable"] = "illisible",
            ["packets.unhandled"] = "non traité",
        };

        static CommandTexts()
        {
            AssertSameKeys(En, "en");
            AssertSameKeys(Fr, "fr");
        }

        public static string Get(string key, params object[] values)
            => Get(SessionContext.State.Language, key, values);

        internal static void AssertCatalogs() { }

        internal static string Get(string language, string key, params object[] values)
        {
            var catalog = language.Trim().ToLowerInvariant() switch
            {
                "en" => En,
                "fr" => Fr,
                _ => Es,
            };
            if (!catalog.TryGetValue(key, out string? format)) format = Es[key];
            return values.Length == 0
                ? format
                : string.Format(CultureInfo.InvariantCulture, format, values);
        }

        private static void AssertSameKeys(IReadOnlyDictionary<string, string> catalog, string language)
        {
            foreach (string key in Es.Keys)
            {
                if (!catalog.ContainsKey(key))
                    throw new InvalidOperationException($"Command text '{key}' is missing for {language}.");
                if (!Placeholders(Es[key]).SetEquals(Placeholders(catalog[key])))
                    throw new InvalidOperationException(
                        $"Command text '{key}' does not use the same placeholders in {language}.");
            }
            if (catalog.Count != Es.Count)
                throw new InvalidOperationException($"Command text catalogue {language} has unexpected keys.");
        }

        private static HashSet<int> Placeholders(string format)
        {
            var result = new HashSet<int>();
            foreach (Match match in Regex.Matches(format, @"\{(?<index>\d+)(?:[^}]*)\}"))
            {
                result.Add(int.Parse(match.Groups["index"].Value, CultureInfo.InvariantCulture));
            }
            return result;
        }
    }
}
