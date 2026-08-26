using System;
using System.Collections.Generic;
using System.Globalization;
using Jondo.Unity.World.Client;

namespace Jondo.Unity.Studio.Ui
{
    /// <summary>
    /// Every word the editor says, in the three languages the emulator speaks.
    /// </summary>
    /// <remarks>
    /// One catalogue rather than strings scattered through the pages, for the same reason the
    /// server has one: the day a fourth language is wanted it is a column here, and until then a
    /// missing translation is a visible key instead of a sentence in the wrong language.
    ///
    /// This covers the editor's own words. The <em>game's</em> words — the names of NPCs, monsters
    /// and items, and the lines of dialogue — are not here and never will be: they belong to Ankama
    /// and live in the client, five languages of them, and <see cref="ClientText"/> reads whichever
    /// one is picked. Changing the language here changes both, which is the point: an NPC is not
    /// called the same thing in Spanish and in French, and a dialogue tree built against one set of
    /// names has to be readable from the other.
    /// </remarks>
    public static class Words
    {
        private static GameLanguage _current = GameLanguage.Spanish;

        /// <summary>Which language the editor is speaking.</summary>
        public static GameLanguage Current => _current;

        /// <summary>Raised when the language changes, so the shell can rebuild.</summary>
        public static event Action? Changed;

        /// <summary>The three the launcher and the server already speak.</summary>
        public static readonly GameLanguage[] Offered =
        {
            GameLanguage.Spanish, GameLanguage.English, GameLanguage.French,
        };

        public static string TagOf(GameLanguage language) => ClientText.TagOf(language).ToUpperInvariant();

        public static void Use(GameLanguage language)
        {
            if (_current == language) return;
            _current = language;
            Changed?.Invoke();
        }

        /// <summary>One word or sentence, in the language in use.</summary>
        public static string T(string key)
        {
            if (!Catalogue.TryGetValue(key, out var row)) return key;

            return _current switch
            {
                GameLanguage.English => row.En,
                GameLanguage.French => row.Fr,
                _ => row.Es,
            };
        }

        /// <summary>The same, with numbers or names dropped into it.</summary>
        public static string T(string key, params object?[] parts)
        {
            string pattern = T(key);
            try
            {
                return string.Format(CultureInfo.CurrentCulture, pattern, parts);
            }
            catch (FormatException)
            {
                return pattern;
            }
        }

        private static readonly Dictionary<string, (string Es, string En, string Fr)> Catalogue
            = new Dictionary<string, (string, string, string)>(StringComparer.Ordinal)
        {
            // ─── The bar across the top ───────────────────────────────────────────
            ["nav.overview"] = ("Resumen", "Overview", "Résumé"),
            ["nav.traffic"] = ("Tráfico", "Traffic", "Trafic"),
            ["nav.packets"] = ("Paquetes", "Packets", "Paquets"),
            ["nav.placements"] = ("NPCs", "NPCs", "PNJ"),
            ["nav.dialogues"] = ("Diálogos", "Dialogues", "Dialogues"),
            ["nav.monsters"] = ("Monstruos", "Monsters", "Monstres"),
            ["nav.spells"] = ("Hechizos", "Spells", "Sorts"),
            ["nav.passages"] = ("Pasajes", "Passages", "Passages"),
            ["nav.cells"] = ("Casillas", "Map cells", "Cases"),

            // ─── Words that turn up on more than one screen ───────────────────────
            ["common.save"] = ("Guardar", "Save", "Enregistrer"),
            ["common.saved"] = ("guardado", "saved", "enregistré"),
            ["common.unsaved"] = ("sin guardar", "unsaved", "non enregistré"),
            ["common.remove"] = ("Quitar", "Remove", "Retirer"),
            ["common.undo"] = ("Deshacer", "Undo", "Annuler"),
            ["common.deselect"] = ("Soltar", "Deselect", "Désélectionner"),
            ["common.search"] = ("buscar", "search", "rechercher"),
            ["common.map"] = ("mapa", "map", "carte"),
            ["common.cell"] = ("casilla", "cell", "case"),
            ["common.facing"] = ("orientación", "facing", "orientation"),
            ["common.name"] = ("nombre", "name", "nom"),
            ["common.id"] = ("id", "id", "id"),
            ["common.measured"] = ("medido", "measured", "mesuré"),
            ["common.authored"] = ("puesto a mano", "placed by hand", "placé à la main"),
            ["common.removed"] = ("quitado", "taken away", "retiré"),
            ["common.none"] = ("nada", "nothing", "rien"),
            ["common.language"] = ("Idioma", "Language", "Langue"),
            ["common.couldNotSave"] = ("No se ha podido guardar: {0}",
                                       "Could not save: {0}",
                                       "Impossible d'enregistrer : {0}"),
            ["common.editView"] = ("Edición", "Editing", "Édition"),
            ["common.gameView"] = ("Como en el juego", "As in game", "Comme en jeu"),

            // ─── Overview ─────────────────────────────────────────────────────────
            ["overview.where"] = ("De dónde lee", "Where it reads from", "D'où il lit"),
            ["overview.what"] = ("Qué ha cargado", "What it loaded", "Ce qu'il a chargé"),
            ["overview.trouble"] = ("Qué no ha cargado", "What did not load", "Ce qui n'a pas chargé"),
            ["overview.writes"] = (
                "Todo lo que escribe va a content/, en texto y versionado. Nada abre world.db para escribir y nada habla con un servidor en marcha.",
                "Everything it writes goes to content/, in versioned text. Nothing opens world.db for writing and nothing talks to a running server.",
                "Tout ce qu'il écrit va dans content/, en texte versionné. Rien n'ouvre world.db en écriture et rien ne parle à un serveur en marche."),

            // ─── Traffic ──────────────────────────────────────────────────────────
            ["traffic.following"] = ("Siguiendo", "Following", "En direct"),
            ["traffic.paused"] = ("Parado", "Paused", "En pause"),
            ["traffic.toTail"] = ("Al final", "Back to the tail", "Revenir à la fin"),
            ["traffic.more"] = ("Más historia", "Read more history", "Plus d'historique"),
            ["traffic.client"] = ("cliente", "client", "client"),
            ["traffic.server"] = ("servidor", "server", "serveur"),
            ["traffic.strangers"] = ("sólo lo que no reconoce", "only what it does not know",
                                     "seulement ce qu'il ne connaît pas"),
            ["traffic.pickOne"] = ("Elige una trama.", "Pick a frame.", "Choisis une trame."),
            ["traffic.name"] = ("Bautizar este paquete", "Name this packet", "Nommer ce paquet"),
            ["traffic.frames"] = ("{0} de {1} tramas", "{0} of {1} frames", "{0} trames sur {1}"),
            ["traffic.noProtocol"] = ("sin fichero de protocolo: los campos van sin nombre",
                                      "no protocol file: fields have no names",
                                      "pas de fichier de protocole : les champs n'ont pas de nom"),

            // ─── Packets ──────────────────────────────────────────────────────────
            ["packets.scan"] = ("Barrer el registro", "Scan the traffic log", "Balayer le journal"),
            ["packets.scanning"] = ("Barriendo…", "Scanning…", "Balayage…"),
            ["packets.onlyUnknown"] = ("sólo los que no sé qué son", "only the ones nothing is known about",
                                       "seulement ceux dont on ne sait rien"),
            ["packets.kinds"] = ("{0} de {1} clases · {2} con nota · {3} opcodes",
                                 "{0} of {1} kinds · {2} with a note · {3} opcodes",
                                 "{0} sortes sur {1} · {2} annotées · {3} opcodes"),
            ["packets.notScanned"] = ("el registro de tráfico no se ha barrido todavía",
                                      "the traffic log has not been scanned yet",
                                      "le journal n'a pas encore été balayé"),
            ["packets.status"] = ("Por dónde va", "How far along", "Où ça en est"),
            ["packets.wholeOpcode"] = ("esta nota es del opcode, lleve lo que lleve",
                                       "this note is about the opcode, whatever it carries",
                                       "cette note concerne l'opcode, quel que soit son contenu"),
            ["packets.officialName"] = ("Nombre oficial", "Official name", "Nom officiel"),
            ["packets.officialHint"] = (
                "De los 513 que el cliente todavía trae dentro. Bautizar deja de ser inventar y pasa a ser elegir.",
                "One of the 513 the client still ships. Naming stops being invention and becomes a choice.",
                "L'un des 513 que le client contient encore. Nommer cesse d'être inventer."),
            ["packets.notes"] = ("Qué se sabe", "What is known", "Ce qu'on sait"),
            ["packets.sample"] = ("Una muestra, leída contra el protocolo declarado",
                                  "A sample, read against the declared protocol",
                                  "Un échantillon, lu contre le protocole déclaré"),
            ["packets.nothingCaptured"] = ("De éste no se ha capturado nada todavía.",
                                           "Nothing has been captured of this one yet.",
                                           "Rien n'a encore été capturé de celui-ci."),
            ["packets.pickOne"] = ("Elige uno.", "Pick one.", "Choisis-en un."),

            ["status.unknown"] = ("sin saber", "unknown", "inconnu"),
            ["status.named"] = ("bautizado", "named", "nommé"),
            ["status.documented"] = ("documentado", "documented", "documenté"),
            ["status.handled"] = ("atendido", "handled", "traité"),
            ["status.ignored"] = ("a propósito", "left alone", "laissé de côté"),

            // ─── NPC placements ───────────────────────────────────────────────────
            ["npc.pickToPlace"] = ("un NPC para colocar…", "an NPC to place…", "un PNJ à placer…"),
            ["npc.mapToDraw"] = ("mapa", "map", "carte"),
            ["npc.placements"] = ("{0} colocados", "{0} placements", "{0} placements"),
            ["npc.someOf"] = ("{0} de {1} colocados", "{0} of {1} placements", "{0} placements sur {1}"),
            ["npc.delta"] = ("{0} puestos o cambiados, {1} quitados",
                             "{0} added or changed, {1} removed",
                             "{0} ajoutés ou modifiés, {1} retirés"),
            ["npc.hintNoMap"] = ("Escribe un mapa, o elige una colocación, para pintarlo.",
                                 "Type a map, or pick a placement, to draw one.",
                                 "Tape une carte, ou choisis un placement, pour la dessiner."),
            ["npc.hintMove"] = ("Haz clic en una casilla para mover a {0} ahí.",
                                "Click a cell to move {0} there.",
                                "Clique sur une case pour y déplacer {0}."),
            ["npc.hintPlace"] = ("Haz clic en una casilla para poner a {0}.",
                                 "Click a cell to place {0}.",
                                 "Clique sur une case pour placer {0}."),
            ["npc.hintPick"] = ("Elige un NPC para colocar, o una colocación para moverla.",
                                "Pick an NPC to place, or a placement to move.",
                                "Choisis un PNJ à placer, ou un placement à déplacer."),
            ["npc.reFaced"] = ("medido, girado", "measured, re-faced", "mesuré, tourné"),
            ["npc.reset"] = ("Devolverlo a su sitio", "Put it back where it was",
                             "Le remettre à sa place"),
            ["npc.resetAll"] = ("Deshacerlo todo", "Undo everything", "Tout annuler"),
            ["npc.noPicture"] = ("sin dibujo", "no picture", "sans dessin"),
            ["npc.actionShop"] = ("tienda", "shop", "boutique"),
            ["npc.actionTalk"] = ("habla", "talks", "parle"),
            ["npc.what"] = ("Qué es", "What it is", "Ce que c'est"),
            ["npc.pickToSee"] = ("Elige una colocación para ver qué es.",
                                 "Pick a placement to see what it is.",
                                 "Choisis un placement pour voir ce que c'est."),
            ["npc.does"] = ("hace", "does", "fait"),
            ["npc.sells"] = ("vende", "sells", "vend"),
            ["npc.says"] = ("dice", "says", "dit"),
            ["npc.andMore"] = ("y {0} más", "and {0} more", "et {0} de plus"),
            ["npc.nothing"] = ("nada", "nothing", "rien"),
            ["npc.linesReplies"] = ("{0} frases, {1} respuestas", "{0} lines, {1} replies",
                                    "{0} phrases, {1} réponses"),

            // ─── The map picker ───────────────────────────────────────────────────
            ["maps.title"] = ("Elegir mapa", "Choose a map", "Choisir une carte"),
            ["maps.byCoordinates"] = ("por coordenadas", "by coordinates", "par coordonnées"),
            ["maps.here"] = ("{0} mapas en [{1}, {2}]", "{0} maps at [{1}, {2}]", "{0} cartes en [{1}, {2}]"),
            ["maps.outdoor"] = ("exterior", "outdoors", "extérieur"),
            ["maps.indoor"] = ("interior", "indoors", "intérieur"),
            ["maps.nothingHere"] = ("Aquí no hay ningún mapa.", "There is no map here.",
                                    "Il n'y a aucune carte ici."),

            // ─── Dialogues ────────────────────────────────────────────────────────
            ["dlg.save"] = ("Guardar los diálogos", "Save the dialogues", "Enregistrer les dialogues"),
            ["dlg.counts"] = ("{0} NPCs con algo que decir · {1} con árbol",
                              "{0} NPCs with something to say · {1} with a tree",
                              "{0} PNJ qui ont quelque chose à dire · {1} avec un arbre"),
            ["dlg.saysHeader"] = ("Lo que dice", "What it says", "Ce qu'il dit"),
            ["dlg.repliesHeader"] = ("Lo que se le puede contestar", "What can be said back",
                                     "Ce qu'on peut lui répondre"),
            ["dlg.inTree"] = ("en el árbol", "in the tree", "dans l'arbre"),
            ["dlg.notInTree"] = ("sin usar", "not used", "non utilisé"),
            ["dlg.opens"] = ("abre", "opens", "ouvre"),
            ["dlg.startHere"] = ("Abrir por aquí", "Start here", "Commencer ici"),
            ["dlg.addLine"] = ("Meter en el árbol", "Add to the tree", "Ajouter à l'arbre"),
            ["dlg.dropLine"] = ("Sacar del árbol", "Take out of the tree", "Retirer de l'arbre"),
            ["dlg.leadsTo"] = ("y luego…", "and then…", "et ensuite…"),
            ["dlg.ends"] = ("se acaba la conversación", "ends the conversation", "termine la conversation"),
            ["dlg.pickNpc"] = ("Elige un NPC.", "Pick an NPC.", "Choisis un PNJ."),
            ["dlg.pickLine"] = ("Elige una frase de arriba.", "Pick a line above.",
                                "Choisis une phrase ci-dessus."),
            ["dlg.lineNotInTree"] = (
                "Esta frase todavía no está en el árbol. Métela con «{0}» y podrás colgarle respuestas.",
                "This line is not in the tree yet. Add it with “{0}” and replies can hang off it.",
                "Cette phrase n'est pas encore dans l'arbre. Ajoute-la avec « {0} » pour pouvoir y accrocher des réponses."),
            ["dlg.columns"] = ("frases / respuestas", "lines / replies", "phrases / réponses"),
            ["dlg.everyReply"] = ("cualquier respuesta del juego", "any reply in the game",
                                  "n'importe quelle réponse du jeu"),
            ["dlg.everyLine"] = ("cualquier frase del juego", "any line in the game",
                                 "n'importe quelle phrase du jeu"),
            ["dlg.chainHint"] = (
                "Para encadenar: pon en «y luego…» la frase con la que el NPC contesta. Si aún no está en el árbol, entra sola.",
                "To chain: set \u201cand then\u2026\u201d to the line the NPC answers with. If it is not in the tree yet, it goes in.",
                "Pour enchaîner : mets dans « et ensuite… » la phrase par laquelle le PNJ répond. Si elle n'est pas dans l'arbre, elle y entre."),
            ["dlg.ownReplies"] = ("las suyas", "its own", "les siennes"),
            ["dlg.notSaved"] = ("No se ha guardado. {0}", "Not saved. {0}", "Non enregistré. {0}"),

            // ─── Monsters ─────────────────────────────────────────────────────────
            ["mob.save"] = ("Guardar los grupos", "Save the groups", "Enregistrer les groupes"),
            ["mob.pick"] = ("un monstruo para meter…", "a monster to add…", "un monstre à ajouter…"),
            ["mob.grade"] = ("grado {0}", "grade {0}", "grade {0}"),
            ["mob.noSpells"] = ("sin hechizos", "no spells", "sans sorts"),
            ["mob.takeAway"] = ("Quitar el grupo", "Take the group away", "Retirer le groupe"),
            ["mob.putBack"] = ("Devolverlo", "Put it back", "Le remettre"),
            ["mob.ankama"] = ("de Ankama", "Ankama's", "d'Ankama"),
            ["mob.newGroup"] = ("Grupo nuevo", "New group", "Nouveau groupe"),
            ["mob.members"] = ("El grupo abierto", "The group being edited", "Le groupe ouvert"),
            ["mob.removeFrom"] = ("Sacar del grupo", "Take out of the group", "Retirer du groupe"),
            ["mob.discard"] = ("Cerrar", "Close it", "Fermer"),
            ["mob.nothingOpen"] = ("Haz clic en un grupo de la lista para abrirlo.",
                                   "Click a group in the list to open it.",
                                   "Clique sur un groupe pour l'ouvrir."),
            ["mob.editingNew"] = ("grupo nuevo · {0} monstruo(s)", "new group · {0} monster(s)",
                                  "nouveau groupe · {0} monstre(s)"),
            ["mob.editingOne"] = ("abierto · {0} monstruo(s) · era {1}",
                                  "open · {0} monster(s) · was {1}",
                                  "ouvert · {0} monstre(s) · était {1}"),
            ["mob.hintOpen"] = ("Haz clic en un grupo para abrirlo, o en «Grupo nuevo» para empezar uno.",
                                "Click a group to open it, or New group to start one.",
                                "Clique sur un groupe pour l'ouvrir, ou Nouveau groupe."),
            ["mob.hintMove"] = ("Haz clic en una casilla para moverlo ahí. Tocar un grupo de Ankama hace una copia nuestra.",
                                "Click a cell to move it there. Touching one of Ankama's makes a copy of our own.",
                                "Clique sur une case pour le déplacer. Toucher un groupe d'Ankama en fait une copie."),
            ["mob.hintDrop"] = ("Mete monstruos y haz clic en una casilla para dejarlo.",
                                "Add monsters, then click a cell to drop it.",
                                "Ajoute des monstres, puis clique sur une case."),
            ["mob.hintNoMap"] = ("Escribe un mapa para ver sus grupos.",
                                 "Type a map to see its groups.",
                                 "Tape une carte pour voir ses groupes."),
            ["mob.here"] = ("mapa {0}: {1} de Ankama, {2} puestos a mano",
                            "map {0}: {1} of Ankama's, {2} placed by hand",
                            "carte {0} : {1} d'Ankama, {2} placés à la main"),
            ["mob.everywhere"] = ("{0} puestos y {1} quitados en total",
                                  "{0} placed and {1} taken away, everywhere",
                                  "{0} placés et {1} retirés en tout"),

            // ─── Spells ───────────────────────────────────────────────────────────
            ["spell.whose"] = ("los de un monstruo…", "a monster's spells…", "les sorts d'un monstre…"),
            ["spell.pickOne"] = ("Elige un hechizo.", "Pick a spell.", "Choisis un sort."),
            ["spell.effects"] = ("Lo que hace", "What it does", "Ce qu'il fait"),
            ["spell.grade"] = ("grado {0}", "grade {0}", "grade {0}"),
            ["spell.ap"] = ("PA", "AP", "PA"),
            ["spell.range"] = ("alcance", "range", "portée"),
            ["spell.selfOnly"] = ("sólo en su casilla", "own cell only", "sur sa case seulement"),
            ["spell.inLine"] = ("sólo en línea", "in a line only", "en ligne seulement"),
            ["spell.needsSight"] = ("necesita verlo", "needs line of sight", "nécessite la vue"),
            ["spell.perTurn"] = ("por turno", "per turn", "par tour"),
            ["spell.perTarget"] = ("por objetivo", "per target", "par cible"),
            ["spell.caster"] = ("quien lanza", "the caster", "le lanceur"),
            ["spell.reach"] = ("hasta donde llega", "where it reaches", "sa portée"),
            ["spell.wouldHit"] = ("a lo que daría", "what it would hit", "ce qu'il toucherait"),
            ["spell.moveCaster"] = ("Mover al lanzador", "Move the caster", "Déplacer le lanceur"),
            ["spell.clickToMove"] = ("Haz clic donde quieres poner al lanzador.",
                                     "Click where the caster should stand.",
                                     "Clique où le lanceur doit se tenir."),
            ["spell.sweep"] = ("Pasa el ratón por el mapa para apuntar. Clic para dejarlo fijo.",
                               "Sweep the map to aim. Click to hold it still.",
                               "Balaye la carte pour viser. Clique pour figer."),
            ["spell.aimedAt"] = ("casilla {0}, a {1} de distancia",
                                 "cell {0}, {1} away", "case {0}, à {1}"),
            ["spell.outOfReach"] = ("casilla {0}, a {1}: fuera de alcance",
                                    "cell {0}, {1} away: out of reach",
                                    "case {0}, à {1} : hors de portée"),
            ["spell.knows"] = ("{0} sabe {1}", "{0} knows {1}", "{0} connaît {1}"),
            ["spell.inTheGame"] = ("{0} hechizos en el juego · {1} encontrados",
                                   "{0} spells in the game · {1} found",
                                   "{0} sorts dans le jeu · {1} trouvés"),
            ["spell.noWords"] = ("(sin descripción)", "(no description)", "(sans description)"),
            ["spell.applied"] = ("se aplica", "applied", "appliqué"),
            ["spell.showDead"] = ("Los que no hacen nada", "The ones that do nothing",
                                  "Ceux qui ne font rien"),
            ["spell.showSpells"] = ("Los hechizos", "The spells", "Les sorts"),
            ["spell.deadList"] = ("{0} efectos sin código, en {1} niveles de hechizo",
                                  "{0} effects with no code, over {1} spell levels",
                                  "{0} effets sans code, sur {1} niveaux de sort"),
            ["spell.deadRow"] = ("{0} niveles · {1} hechizos", "{0} levels · {1} spells",
                                 "{0} niveaux · {1} sorts"),
            ["spell.asCharac"] = ("por característica", "as a characteristic", "par caractéristique"),
            ["spell.panelOnly"] = ("NO HACE NADA", "DOES NOTHING", "NE FAIT RIEN"),
            ["spell.coverage"] = ("efectos: {0} con código, {1} por característica, {2} que no hacen nada",
                                  "effects: {0} with code, {1} by characteristic, {2} that do nothing",
                                  "effets : {0} avec du code, {1} par caractéristique, {2} sans effet"),
            ["spell.deadEffects"] = ("{0} de sus efectos no hacen nada",
                                     "{0} of its effects do nothing",
                                     "{0} de ses effets ne font rien"),

            // ─── Passages ─────────────────────────────────────────────────────────
            ["tp.from"] = ("Desde este mapa", "From this map", "Depuis cette carte"),
            ["tp.to"] = ("Hasta este otro", "To this one", "Vers celle-ci"),
            ["tp.tie"] = ("Atarlos, ida y vuelta", "Join them, both ways",
                          "Les relier, aller et retour"),
            ["tp.oneWay"] = ("Sólo de ida", "One way only", "Aller simple"),
            ["tp.cut"] = ("Quitar el pasaje", "Take the passage away", "Retirer le passage"),
            ["tp.free"] = ("libre", "free", "libre"),
            ["tp.extracted"] = ("extraído", "extracted", "extrait"),
            ["tp.measuredType"] = ("tipo medido {0}", "measured type {0}", "type mesuré {0}"),
            ["tp.counts"] = ("{0} elementos en {1} mapas · {2} pasajes extraídos",
                             "{0} elements on {1} maps · {2} extracted passages",
                             "{0} éléments sur {1} cartes · {2} passages extraits"),
            ["tp.mine"] = ("{0} puestos a mano, {1} quitados",
                           "{0} placed by hand, {1} taken away",
                           "{0} placés à la main, {1} retirés"),
            ["tp.pickTwoMaps"] = ("Elige un mapa a cada lado.", "Pick a map on each side.",
                                  "Choisis une carte de chaque côté."),
            ["tp.pickTwoDoors"] = (
                "Elige un elemento en cada mapa. Sólo salen los que el mapa ya trae: un pasaje no se puede poner donde no hay nada que pulsar.",
                "Pick an element on each map. Only what the map already has is offered: a passage cannot go where there is nothing to click.",
                "Choisis un élément sur chaque carte. Seuls ceux que la carte possède déjà sont proposés."),
            ["tp.ready"] = ("Casilla {0} del mapa {1} ⇄ casilla {2} del mapa {3}",
                            "Cell {0} on map {1} ⇄ cell {2} on map {3}",
                            "Case {0} de la carte {1} ⇄ case {2} de la carte {3}"),
            ["tp.landsHere"] = ("se aterriza aquí", "lands here", "on arrive ici"),
            ["tp.lands"] = ("se aterriza en {0} y en {1}", "lands on {0} and on {1}",
                            "on arrive sur {0} et sur {1}"),
            ["tp.noElements"] = ("No se ha podido leer la lista de elementos interactivos.",
                                 "The interactive elements could not be read.",
                                 "Les éléments interactifs n'ont pas pu être lus."),
            ["tp.notSaved"] = ("No se ha guardado. {0}", "Not saved. {0}", "Non enregistré. {0}"),

            // ─── Cells ────────────────────────────────────────────────────────────
            ["cells.walkable"] = ("se pisa", "walkable", "praticable"),
            ["cells.notInFight"] = ("no en combate", "not in a fight", "pas en combat"),
            ["cells.seen"] = ("se ve a través", "seen through", "traversable à vue"),
            ["cells.solid"] = ("bloqueada", "solid", "bloquée"),
            ["cells.counts"] = ("{0} se pisan · {1} en combate · {2} tapan la vista",
                                "{0} walkable · {1} in a fight · {2} block sight",
                                "{0} praticables · {1} en combat · {2} bloquent la vue"),
            ["cells.inFight"] = ("se pisa en combate", "walkable in a fight", "praticable en combat"),
            ["cells.blocksSight"] = ("tapa la vista", "blocks sight", "bloque la vue"),
            ["cells.look"] = ("Sólo mirar", "Just looking", "Regarder"),
            ["cells.painting"] = ("Pintando", "Painting", "Peinture"),
            ["cells.changed"] = ("{0} cambiadas aquí", "{0} changed here", "{0} modifiées ici"),
            ["cells.undoMap"] = ("Deshacer este mapa", "Undo this map", "Annuler cette carte"),
            ["cells.undoAll"] = ("Deshacerlo todo", "Undo everything", "Tout annuler"),
            ["cells.around"] = ("Los mapas de al lado", "The maps next door", "Les cartes voisines"),
            ["cells.mine"] = ("cambiada a mano", "changed by hand", "modifiée à la main"),
            ["cells.trimmed"] = (
                "Ojo antes de dar una casilla por mal puesta: el fichero de pisables recorta el anillo exterior a propósito, para que no salgan monstruos ahí. Por eso un borde sale bloqueado y en combate no lo está.",
                "Before deciding a cell is wrong: the walkable file trims the outer ring on purpose, so monsters are not placed there. That is why a border reads as blocked while the fight file says it is fine.",
                "Avant de juger une case fausse : le fichier des cases praticables rogne l'anneau extérieur exprès, pour que les monstres n'y apparaissent pas."),
            ["cells.npcHere"] = ("un NPC", "an NPC", "un PNJ"),
            ["cells.mobHere"] = ("un grupo", "a group", "un groupe"),

            // ─── When something is not there ──────────────────────────────────────
            ["missing.world"] = ("No se ha podido abrir world.db, así que aquí no hay nada que enseñar.",
                                 "world.db could not be opened, so there is nothing to show here.",
                                 "world.db n'a pas pu être ouvert, il n'y a rien à montrer ici."),
            ["missing.lookedIn"] = ("Se ha mirado en {0}", "Looked in {0}", "Cherché dans {0}"),
        };
    }
}
