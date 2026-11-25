using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using Jondo.Unity.Launcher;

namespace Jondo.Unity.Server.Managers
{
    /// <summary>
    /// Fights a reply to an NPC starts, with who is in front, who fights beside the player and on
    /// which arena (content/fights/npc_fights.json).
    /// </summary>
    /// <remarks>
    /// The tutorial's last test: the celestial chafers of the combat room are NPCs, and «Empezar el
    /// combate contra los tres monstruos» (effect 911 beside it) puts the player on 241441797
    /// against a chafer explorer and two pikemen, with Master Darmas (monster 8018) on his side --
    /// fighter -1, 12 AP, 80 % resistances, in the jxg of the tutorial capture.
    /// </remarks>
    public static class NpcFights
    {
        /// <summary>The file, relative to the content root.</summary>
        public const string AuthoredFile = "fights/npc_fights.json";

        /// <summary>One fight a reply starts.</summary>
        public sealed class Fight
        {
            public int NpcId { get; init; }
            public long MapId { get; init; }
            public long Reply { get; init; }
            public long ArenaMapId { get; init; }
            public IReadOnlyList<(int Monster, int Grade)> Monsters { get; init; } = Array.Empty<(int, int)>();
            public IReadOnlyList<(int Monster, int Grade)> Allies { get; init; } = Array.Empty<(int, int)>();

            /// <summary>
            /// The cell of the fight's board the character starts on («playerCell»), or 0 for the
            /// board's placement: «Le jour des assassins» is won by putting the magypus to sleep in
            /// the first round, at range 1, and a drawn placement left him rows away from it.
            /// </summary>
            public int PlayerCell { get; init; }

            /// <summary>Each ally's start cell, in the order of <see cref="Allies"/> («allyCells»), 0 for none.</summary>
            public IReadOnlyList<int> AllyCells { get; init; } = Array.Empty<int>();

            /// <summary>Each monster's start cell, in the order of <see cref="Monsters"/> (its «cell»), 0 for none.</summary>
            public IReadOnlyList<int> MonsterCells { get; init; } = Array.Empty<int>();

            /// <summary>Where the row starts its fighters, or null when the board's placement does it all.</summary>
            public StartCells? Start => PlayerCell > 0 || AllyCells.Any(c => c > 0) || MonsterCells.Any(c => c > 0)
                ? new StartCells(PlayerCell, AllyCells, MonsterCells)
                : null;

            /// <summary>Whether the fight is lost when one of the allies falls («alliesMustLive»).</summary>
            public bool AlliesMustLive { get; init; }

            /// <summary>Where a win leaves the character, if not where it fought («winMap», «winCell»).</summary>
            public long WinMapId { get; init; }

            /// <summary>The cell of <see cref="WinMapId"/>.</summary>
            public int WinCellId { get; init; }

            /// <summary>Where a scripted defeat leaves the character («loseMap», «loseCell»).</summary>
            public long LoseMapId { get; init; }

            /// <summary>The cell of <see cref="LoseMapId"/>.</summary>
            public int LoseCellId { get; init; }
            /// <summary>
            /// A mark the win notes among the character's used elements («marks»), which a reply's
            /// «afterElement» asks for: the heaps of filings of «Las primeras impresiones son
            /// engañosas» give their handful only once their moguis are beaten. Negative, so it
            /// never meets an element of a map.
            /// </summary>
            public int Marks { get; init; }

            /// <summary>
            /// Whether the monsters come at the grade of the character's level instead of the one written
            /// («byLevel»): the recruiting captains of Bonta and Brakmar duel whoever comes, and their
            /// monsters (6851, 6852) have one grade every 20 levels, from 20 to 200.
            /// </summary>
            public bool ByLevel { get; init; }

            /// <summary>
            /// The monster the character fights as, or 0 («incarnates»: { monster, grade,
            /// invulnerable }): «Barnabé en el espacio» makes him the Entrenador (3985) -- one life
            /// point, three AP, no MP, the gladiators' three calls, Esponja and Bronca --, «un
            /// épouvantail qui a 1PV 0PM 3PA mais vous êtes invulnérable» (Dofus pour les noobs).
            /// </summary>
            public int Incarnates { get; init; }

            /// <summary>The grade of <see cref="Incarnates"/>, 1-based.</summary>
            public int IncarnationGrade { get; init; } = 1;

            /// <summary>Whether the incarnation can take no blow (the client's state 269).</summary>
            public bool IncarnationInvulnerable { get; init; }

            /// <summary>
            /// Whether the character plays the allies by hand («alliesPlayed»): Barnabé,
            /// «contrôlable depuis la màj 2.70» (Fighter.IsPlayedAlly).
            /// </summary>
            public bool AlliesPlayed { get; init; }

            /// <summary>
            /// Whether losing costs nothing and leaves the character where it fought («defeatFree»):
            /// the prophetic fire of «El receptáculo de los dofus», «Si vous perdez le combat, vous ne
            /// perdez pas d'énergie et vous restez sur la map vous permettant de relancer le combat
            /// instantanément» (Dofus pour les noobs).
            /// </summary>
            public bool DefeatFree { get; init; }

            /// <summary>
            /// Whom the row is for («criterion», in the client's language), or empty for anybody.
            /// Rows of the same reply are tried in order and the first that holds is the fight:
            /// Skalo's recruits face the first prisoner of the formation's year the character has
            /// captured -- «El prisionero al que hay que enfrentarse será seleccionado entre los que
            /// ya has capturado».
            /// </summary>
            public string Criterion { get; init; } = "";

            /// <summary>
            /// Whether the fight is played alone («solo»): true, nobody comes in beside the character,
            /// his heroes neither -- «Le combat se lance et s'effectue seul» (Dofus pour les noobs,
            /// «Mise à l'épreuve») --; false, a group may; absent, its board decides
            /// (<see cref="Handlers.FightHandler.PlayedAlone"/>). Keka-Bron/JondoEmu#209.
            /// </summary>
            public bool? Solo { get; init; }

            /// <summary>
            /// Monsters that come in later, each wave so many rounds after the one before
            /// («waves»: [{ "turns", "monsters" }]): Dark Vlad's chafers.
            /// </summary>
            public IReadOnlyList<Jondo.Unity.World.Fights.FightInstance.TimedWave> Waves { get; init; }
                = Array.Empty<Jondo.Unity.World.Fights.FightInstance.TimedWave>();

            /// <summary>
            /// What the quest adds to the fight («grants», «passives», «states», «winState»): see
            /// <see cref="Jondo.Unity.World.Fights.FightInstance.QuestRules"/>.
            /// </summary>
            public Jondo.Unity.World.Fights.FightInstance.QuestRules Rules { get; init; }
                = Jondo.Unity.World.Fights.FightInstance.QuestRules.None;

            /// <summary>
            /// The element of <see cref="MapId"/> whose use starts the fight («element»), for a row
            /// with no NPC: the old chest of the Xelorium [6,6], opened, ambushes whoever opened it.
            /// </summary>
            public int ElementId { get; init; }

            /// <summary>
            /// The interactive type the element is declared with while its fight is to be fought
            /// («type»), the client's name on hover; zero for the quest default.
            /// </summary>
            public int ElementType { get; init; }

            /// <summary>
            /// The fight that starts as soon as this one is won and the character is back on the
            /// map («then», a row of its own without npc, reply or element): the Xelorium chest's
            /// second ambush, right after the first one's waves.
            /// </summary>
            public Fight? Then { get; init; }

            /// <summary>The last fight of the chain this one starts (itself without <see cref="Then"/>).</summary>
            public Fight Final => Then?.Final ?? this;

            /// <summary>
            /// Whether the whole chain is behind the character: its last fight leaves a mark
            /// (<see cref="Marks"/>) and <paramref name="marked"/> has it. An element's chain is fought once.
            /// </summary>
            public bool Done(Func<int, bool> marked) => Final.Marks != 0 && marked(Final.Marks);
        }

        /// <summary>
        /// The cells a row starts its fighters on, which win over the board's placement: the
        /// character's, each ally's and each monster's, in the order the row lists them; 0 leaves
        /// that one to the board.
        /// </summary>
        public sealed record StartCells(int Player, IReadOnlyList<int> Allies, IReadOnlyList<int> Monsters);

        private static List<Fight>? _fights;
        private static readonly object _lock = new();

        /// <summary>The fight this reply to this NPC starts here, or null.</summary>
        public static Fight? For(int npcId, long mapId, long reply)
        {
            Load();
            foreach (var fight in _fights!)
            {
                if (fight.ElementId != 0) continue;
                if (fight.NpcId != npcId || fight.Reply != reply || (fight.MapId != 0 && fight.MapId != mapId)) continue;
                if (fight.Criterion.Length > 0 && !PlayerCriteria.Met(fight.Criterion, mapId)) continue;
                return fight;
            }
            return null;
        }

        /// <summary>
        /// The fight this element of this map starts for the session's character, or null: its row's
        /// criterion holds and its chain is not done yet (<see cref="Fight.Done"/>).
        /// </summary>
        public static Fight? ForElement(long mapId, int elementId, Func<int, bool> marked)
        {
            Load();
            foreach (var fight in _fights!)
            {
                if (fight.ElementId != elementId || fight.MapId != mapId) continue;
                if (fight.Done(marked)) continue;
                if (fight.Criterion.Length > 0 && !PlayerCriteria.Met(fight.Criterion, mapId)) continue;
                return fight;
            }
            return null;
        }

        /// <summary>The element rows of a map, to declare their elements usable.</summary>
        public static IEnumerable<Fight> OnMap(long mapId)
        {
            Load();
            foreach (var fight in _fights!)
                if (fight.ElementId != 0 && fight.MapId == mapId) yield return fight;
        }

        /// <summary>For the tests: read the file again.</summary>
        internal static void Forget()
        {
            lock (_lock) _fights = null;
        }

        private static void Load()
        {
            lock (_lock)
            {
                if (_fights != null) return;
                var fights = new List<Fight>();
                try
                {
                    string path = Paths.ContentFile(AuthoredFile);
                    if (File.Exists(path))
                    {
                        using var doc = JsonDocument.Parse(File.ReadAllText(path));
                        foreach (var row in doc.RootElement.GetProperty("fights").EnumerateArray())
                        {
                            fights.Add(Parse(row));
                        }
                    }
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"[Content] {AuthoredFile} is unreadable: {ex.Message}");
                }
                _fights = fights;
            }
        }

        /// <summary>One row of the file, and the fight it chains to («then»), read the same way.</summary>
        internal static Fight Parse(JsonElement row)
        {
            bool incarnates = row.TryGetProperty("incarnates", out var incarnation)
                              && incarnation.ValueKind == JsonValueKind.Object;
            return new Fight
            {
                NpcId = (int)Long(row, "npc"),
                MapId = Long(row, "map"),
                Reply = Long(row, "reply"),
                ArenaMapId = Long(row, "arena"),
                Monsters = Members(row, "monsters"),
                Allies = Members(row, "allies"),
                PlayerCell = Math.Max(0, (int)Long(row, "playerCell")),
                AllyCells = Cells(row),
                MonsterCells = MemberCells(row, "monsters"),
                AlliesMustLive = row.TryGetProperty("alliesMustLive", out var must) && must.ValueKind == JsonValueKind.True,
                WinMapId = Long(row, "winMap"),
                WinCellId = (int)Long(row, "winCell"),
                LoseMapId = Long(row, "loseMap"),
                LoseCellId = (int)Long(row, "loseCell"),
                Marks = (int)Long(row, "marks"),
                ByLevel = row.TryGetProperty("byLevel", out var byLevel) && byLevel.ValueKind == JsonValueKind.True,
                Incarnates = incarnates ? (int)Long(incarnation, "monster") : 0,
                IncarnationGrade = incarnates ? Math.Max(1, (int)Long(incarnation, "grade")) : 1,
                IncarnationInvulnerable = incarnates && incarnation.TryGetProperty("invulnerable", out var invulnerable)
                                          && invulnerable.ValueKind == JsonValueKind.True,
                AlliesPlayed = row.TryGetProperty("alliesPlayed", out var played) && played.ValueKind == JsonValueKind.True,
                DefeatFree = row.TryGetProperty("defeatFree", out var free) && free.ValueKind == JsonValueKind.True,
                Criterion = row.TryGetProperty("criterion", out var criterion) && criterion.ValueKind == JsonValueKind.String
                            ? criterion.GetString() ?? "" : "",
                Solo = row.TryGetProperty("solo", out var solo) && solo.ValueKind is JsonValueKind.True or JsonValueKind.False
                       ? solo.GetBoolean() : null,
                Waves = Waves(row),
                Rules = RulesOf(row),
                ElementId = (int)Long(row, "element"),
                ElementType = (int)Long(row, "type"),
                Then = row.TryGetProperty("then", out var then) && then.ValueKind == JsonValueKind.Object ? Parse(then) : null,
            };
        }

        private static List<Jondo.Unity.World.Fights.FightInstance.TimedWave> Waves(JsonElement row)
        {
            var waves = new List<Jondo.Unity.World.Fights.FightInstance.TimedWave>();
            if (!row.TryGetProperty("waves", out var list) || list.ValueKind != JsonValueKind.Array) return waves;
            foreach (var wave in list.EnumerateArray())
            {
                var members = Members(wave, "monsters");
                if (members.Count > 0)
                    waves.Add(new Jondo.Unity.World.Fights.FightInstance.TimedWave(Math.Max(1, (int)Long(wave, "turns")), members));
            }
            return waves;
        }

        /// <summary>
        /// A row's quest rules: «grants» [spell...], «passives» [{ monster, spell }], «states»
        /// [{ monster, state }], «winState», «sacrifice» { ally, message }, «winMessage» and
        /// «allyProof» [monster...].
        /// </summary>
        internal static Jondo.Unity.World.Fights.FightInstance.QuestRules RulesOf(JsonElement row)
        {
            var grants = new List<int>();
            if (row.TryGetProperty("grants", out var list) && list.ValueKind == JsonValueKind.Array)
                foreach (var spell in list.EnumerateArray())
                    if (spell.ValueKind == JsonValueKind.Number && spell.GetInt32() > 0) grants.Add(spell.GetInt32());
            var allyProof = new List<int>();
            if (row.TryGetProperty("allyProof", out var proof) && proof.ValueKind == JsonValueKind.Array)
                foreach (var monster in proof.EnumerateArray())
                    if (monster.ValueKind == JsonValueKind.Number && monster.GetInt32() > 0) allyProof.Add(monster.GetInt32());
            bool sacrifice = row.TryGetProperty("sacrifice", out var sacrificed) && sacrificed.ValueKind == JsonValueKind.Object;
            var rules = new Jondo.Unity.World.Fights.FightInstance.QuestRules(
                grants, Pairs(row, "passives", "spell"), Pairs(row, "states", "state"), (int)Long(row, "winState"))
            {
                SacrificeAlly = sacrifice ? (int)Long(sacrificed, "ally") : 0,
                SacrificeMessage = sacrifice ? (int)Long(sacrificed, "message") : 0,
                SacrificeTakes = sacrifice ? (int)Long(sacrificed, "takes") : 0,
                WinMessage = (int)Long(row, "winMessage"),
                AllyProof = allyProof,
            };
            return rules.IsEmpty ? Jondo.Unity.World.Fights.FightInstance.QuestRules.None : rules;
        }

        private static List<(int, int)> Pairs(JsonElement row, string name, string what)
        {
            var found = new List<(int, int)>();
            if (!row.TryGetProperty(name, out var list) || list.ValueKind != JsonValueKind.Array) return found;
            foreach (var pair in list.EnumerateArray())
            {
                int monster = (int)Long(pair, "monster"), value = (int)Long(pair, what);
                if (monster != 0 && value != 0) found.Add((monster, value));
            }
            return found;
        }

        private static List<(int, int)> Members(JsonElement row, string name)
        {
            var found = new List<(int, int)>();
            if (!row.TryGetProperty(name, out var list) || list.ValueKind != JsonValueKind.Array) return found;
            foreach (var member in list.EnumerateArray())
            {
                int monster = (int)Long(member, "monster");
                if (monster != 0) found.Add((monster, (int)Long(member, "grade")));
            }
            return found;
        }

        /// <summary>The «allyCells» of a row: one per ally, in order; 0 or a missing one leaves it to the board.</summary>
        private static List<int> Cells(JsonElement row)
        {
            var found = new List<int>();
            if (!row.TryGetProperty("allyCells", out var list) || list.ValueKind != JsonValueKind.Array) return found;
            foreach (var cell in list.EnumerateArray())
                found.Add(cell.ValueKind == JsonValueKind.Number && cell.TryGetInt32(out int n) && n > 0 ? n : 0);
            return found;
        }

        /// <summary>Each member's «cell», 0 when it has none, in the order <see cref="Members"/> reads them.</summary>
        private static List<int> MemberCells(JsonElement row, string name)
        {
            var found = new List<int>();
            if (!row.TryGetProperty(name, out var list) || list.ValueKind != JsonValueKind.Array) return found;
            foreach (var member in list.EnumerateArray())
                if (Long(member, "monster") != 0) found.Add(Math.Max(0, (int)Long(member, "cell")));
            return found;
        }

        private static long Long(JsonElement row, string name)
            => row.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt64(out long n) ? n : 0;
    }
}
