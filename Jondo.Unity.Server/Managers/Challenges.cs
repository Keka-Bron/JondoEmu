using Jondo.Unity.Launcher;
using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;

namespace Jondo.Unity.Server.Managers
{
    /// <summary>
    /// Fight challenges: what is chosen in placement and gives a bonus on winning.
    ///
    /// ─── Where they come from ───────────────────────────────────────────────────────────────
    ///
    /// From the client's table, 842 entries, with their names and descriptions already translated --
    /// tools/extraer_retos.py does it --. Each challenge carries two criteria in a very short language
    /// of its own: <c>activacion</c> says when it can be offered and <c>cumplimiento</c> what has to be
    /// done to achieve it.
    ///
    /// ─── The percentage is NOT in the client ────────────────────────────────────────────────
    ///
    /// The table carries no bonus: the percentage is set by the server and travels on the wire inside
    /// the ldd. So here there are only those of the FIFTEEN challenges seen going by, and it is their
    /// base value: the same challenge sometimes comes out with sixty more points -- in the anomaly they
    /// all carry them --, and that modifier has not been possible to reconstruct.
    ///
    /// That is why the emulator offers ONLY those fifteen. Offering the eight hundred with a made-up
    /// number would be worse: the player would see a challenge promising a bonus nobody has measured.
    ///
    /// ─── When a challenge can be offered ────────────────────────────────────────────────────
    ///
    /// The activation criterion explains why poutchs give no challenges: nearly all require
    /// <c>GL&gt;4,0</c>, that is a group level above four, and against a level 1 poutch it is not
    /// reached. There are some requiring a specific monster (<c>GM&gt;0,1185,1</c>); those are not
    /// offered here, because the real server IMPOSES them, it does not propose them, and that is
    /// another story.
    /// </summary>
    public static class Challenges
    {
        /// <summary>A challenge from the client's table.</summary>
        public sealed class Challenge
        {
            public int Id { get; init; }
            public string Name { get; init; } = "";
            public string Description { get; init; } = "";
            public int Category { get; init; }

            /// <summary>Which ones it cannot live with. It rules among the ones locked in, not among those offered.</summary>
            public IReadOnlyList<int> Incompatible { get; init; } = Array.Empty<int>();

            /// <summary>When it can be offered, in the table's short language.</summary>
            public string Activation { get; init; } = "";

            /// <summary>What has to be done to meet it. Nobody checks it yet.</summary>
            public string Completion { get; init; } = "";

            /// <summary>The bonus it promises, as a percentage. Zero if it has not been measured.</summary>
            public int Percent { get; set; }

            /// <summary>Does the percentage come from the wire, or did we set it?</summary>
            public bool PercentMeasured { get; set; }

            /// <summary>Group level above which it can be offered. Zero if not needed.</summary>
            public int MinGroupLevel { get; init; }

            /// <summary>Only valid inside a dungeon.</summary>
            public bool DungeonOnly { get; init; }

            /// <summary>Requires a specific monster to be in the group.</summary>
            public bool NeedsMonster => Monsters.Count > 0;

            /// <summary>Which monsters have to be in front for this challenge to exist.</summary>
            public IReadOnlyList<int> Monsters { get; init; } = Array.Empty<int>();

            /// <summary>
            /// Can it be proposed? Its percentage must have been seen, it must not depend on a dungeon or a
            /// monster -- those are IMPOSED by the content, not proposed -- and it must carry a level threshold,
            /// which is the only thing that can be read from the criterion here.
            /// </summary>
            public bool Offerable => Percent > 0 && MinGroupLevel > 0 && !DungeonOnly && !NeedsMonster;

            // ─── How one the place imposes is judged ─────────────────────────────
            //
            // A boss's challenges are the usual ones under another number: the Jalató Real's
            // "Prudente" (121) carries the same criterion, letter for letter, as the generic
            // Prudente (40). So the 773 need no watcher each: each is matched to the watched one
            // it is equivalent to, and that one judges it. OnlyOffer fills this in.

            /// <summary>
            /// The watched challenge this one is equivalent to: itself for a generic one, its twin
            /// for a boss's, and zero when nobody can judge it.
            /// </summary>
            public int Kind { get; set; }

            /// <summary>"CK#monster": that monster -- or one of those -- has to fall first.</summary>
            public IReadOnlyList<int> KillFirst { get; set; } = Array.Empty<int>();

            /// <summary>"Ck#monster": that monster has to fall last.</summary>
            public IReadOnlyList<int> KillLast { get; set; } = Array.Empty<int>();

            /// <summary>"ST&lt;N": the fight has to be won before round N. Zero when there is no limit.</summary>
            public int TurnLimit { get; set; }

            /// <summary>
            /// The ones that ask for nothing but having come in few: "Solo", which is winning with
            /// one character and whose criterion says only "Ma=1". What there is to check, the
            /// activation already checks.
            /// </summary>
            public bool PartySizeOnly { get; set; }

            /// <summary>The rule wired by hand, for the ones that only state it in their description.</summary>
            public BossRule Rule { get; set; }

            /// <summary>Whether anybody judges it. One nobody does is not imposed: it would always come out won.</summary>
            public bool Judged => Kind != 0 || KillFirst.Count > 0 || KillLast.Count > 0
                                  || TurnLimit > 0 || PartySizeOnly || Rule != BossRule.None;
        }

        /// <summary>
        /// The rules of the boss challenges whose criterion is "Ma=1", that is "a script handles
        /// it": what they ask is only in the description, read one by one. Here are the ones the
        /// fight can judge with what it already knows -- where each one ends, who hits whom and
        /// from where, who heals, who falls. Those that hang on a spell or a state of the boss's
        /// own are left out.
        /// </summary>
        public enum BossRule
        {
            None = 0,

            // Where the turn ends.
            EndInLineWithEnemy,
            EndDiagonalToEnemy,
            NeverInLineWithEnemy,
            NeverLineOrDiagonalEnemy,
            NeverInLineWithAlly,
            NeverLineEnemyOrAlly,
            NeverDiagonalEnemyOrAlly,
            EndOnStartCell,
            EndNearEnemy5,
            EndFarFromAllies3,
            EndFarFromAllies4,
            BeginOrEndInLineWithEnemy,

            // Who falls, and when.
            NoEnemyKilledBeforeRound6,
            NobodyKilledBeforeRound6,
            NoEnemySummonKilledByAlly,

            // Heals.
            NoHealEnemies,
            NoHealAllies,

            // Damage.
            NoRangedDamageToEnemies,
            NoMeleeDamageToEnemies,
            NoRangedDamageToBoss,
            NoPushDamageToEnemies,
            NoPushDamageToAllies,
            NoDamageToEnemySummons,
            NoDamageWhileEnemySummons,
            BossUntouchedUntilAlone,
        }

        /// <summary>Challenge → its rule. The numbers are those of the 3.6.10.10 client's table.</summary>
        private static readonly Dictionary<int, BossRule> _byHand = new()
        {
            [1037] = BossRule.EndInLineWithEnemy,          // Roblenlace
            [1056] = BossRule.EndDiagonalToEnemy,          // Untouched
            [1054] = BossRule.NeverInLineWithEnemy,        // Maternal emancipation
            [1059] = BossRule.NeverLineOrDiagonalEnemy,    // Mycology
            [1060] = BossRule.NeverInLineWithAlly,         // Un proyecto tentacular
            [985] = BossRule.NeverLineEnemyOrAlly,         // The forbidden line
            [1045] = BossRule.NeverDiagonalEnemyOrAlly,    // Diagonal of the void
            [1033] = BossRule.EndOnStartCell,              // Out of the ring
            [1023] = BossRule.EndNearEnemy5,               // Maestro Cuerbok
            [1061] = BossRule.EndFarFromAllies3,           // Mind my paws
            [1053] = BossRule.EndFarFromAllies4,           // Frozen autonomy
            [1074] = BossRule.BeginOrEndInLineWithEnemy,   // There are people around here

            [525] = BossRule.NoEnemyKilledBeforeRound6,    // Domakuroptimisation
            [528] = BossRule.NobodyKilledBeforeRound6,     // Dorigamisericordia
            [1100] = BossRule.NoEnemySummonKilledByAlly,   // Cascasaur protection
            [1101] = BossRule.NoEnemySummonKilledByAlly,
            [1102] = BossRule.NoEnemySummonKilledByAlly,

            [980] = BossRule.NoHealEnemies,                // Un milubo en el corral
            [1063] = BossRule.NoHealAllies,                // Allies sent to the future

            [485] = BossRule.NoRangedDamageToEnemies,      // Combate cercano
            [1050] = BossRule.NoRangedDamageToEnemies,     // Colmillo a colmillo
            [1066] = BossRule.NoRangedDamageToEnemies,     // Shadow play
            [1404] = BossRule.NoRangedDamageToEnemies,     // Crunchy and ringing
            [1073] = BossRule.NoMeleeDamageToEnemies,      // Failure is not an option
            [1007] = BossRule.NoRangedDamageToBoss,        // Within dart range
            [990] = BossRule.NoPushDamageToEnemies,        // Kwoknan? Kwokpujeee!
            [1008] = BossRule.NoPushDamageToAllies,        // Full steam ahead
            [1013] = BossRule.NoPushDamageToAllies,        // Do not give the pig too much honey
            [1071] = BossRule.NoPushDamageToAllies,        // Cuidado, suelo resbaladizo
            [982] = BossRule.NoDamageToEnemySummons,       // Dorado, mi fa sol
            [993] = BossRule.NoDamageToEnemySummons,       // No toques a mi blop, all four
            [994] = BossRule.NoDamageToEnemySummons,
            [995] = BossRule.NoDamageToEnemySummons,
            [996] = BossRule.NoDamageToEnemySummons,
            [998] = BossRule.NoDamageToEnemySummons,       // No desert, all four
            [999] = BossRule.NoDamageToEnemySummons,
            [1000] = BossRule.NoDamageToEnemySummons,
            [1001] = BossRule.NoDamageToEnemySummons,
            [1103] = BossRule.NoDamageToEnemySummons,      // Cascasaur protection, Grozilla's
            [1003] = BossRule.NoDamageWhileEnemySummons,   // Real cracks
            [1022] = BossRule.BossUntouchedUntilAlone,     // Ratcovery
        };

        /// <summary>"Matar a {0} en último lugar", said only in the description: its own boss, last.</summary>
        private static readonly int[] _killBossLast = { 1017, 1062, 2093 };

        /// <summary>"Los enemigos deben ser eliminados antes del inicio del turno 6."</summary>
        private static readonly int[] _beforeRound6 = { 526, 527 };

        /// <summary>"Huele a motín": ending in line with an ally, which is Del mismo linaje.</summary>
        private const int InLineWithAlly = 1080;
        private const int SameLineage = 964;

        /// <summary>Every challenge with a rule wired by hand, one way or another.</summary>
        internal static IEnumerable<int> HandWired
        {
            get
            {
                foreach (int id in _byHand.Keys) yield return id;
                foreach (int id in _killBossLast) yield return id;
                foreach (int id in _beforeRound6) yield return id;
                yield return InLineWithAlly;
            }
        }

        /// <summary>The criterion that says nothing: "a server script handles it".</summary>
        private const string Scripted = "Ma=1";

        private static readonly System.Text.RegularExpressions.Regex _killOrder =
            new System.Text.RegularExpressions.Regex(@"^C([Kk])#(\d+),1$");

        private static readonly System.Text.RegularExpressions.Regex _turnLimit =
            new System.Text.RegularExpressions.Regex(@"^ST<(\d+)$");

        /// <summary>"GN&lt;3,0": how many fighters, compared with what, and of which side.</summary>
        private static readonly System.Text.RegularExpressions.Regex _fighterCount =
            new System.Text.RegularExpressions.Regex(@"GN([<>=])(\d+),([01])");

        /// <summary>
        /// Finds who judges each boss challenge, by its completion criterion.
        ///
        /// What is left without a judge -- "Manos limpias", the ones about not taking AP or MP,
        /// "Místico" and the seventy-odd that are a boss's own mechanics -- is not imposed.
        /// </summary>
        private static void FindJudges(IReadOnlyDictionary<int, int> watched)
        {
            // Criterion → the generic challenge that judges it. "Ma=1" is left out: the Bárbaro
            // and half the catalogue share it, and it tells nobody apart.
            var byCriterion = new Dictionary<string, int>();
            var byName = new Dictionary<string, int>();
            foreach (int id in watched.Keys)
            {
                var generic = Get(id);
                if (generic == null || generic.NeedsMonster) continue;
                generic.Kind = id;
                if (generic.Completion == Scripted) byName[generic.Name] = id;
                else if (generic.Completion.Length > 0) byCriterion[generic.Completion] = id;
            }

            foreach (var challenge in _byId.Values)
            {
                if (!challenge.NeedsMonster) continue;

                var first = new List<int>();
                var last = new List<int>();
                bool killOrder = true;
                foreach (string part in challenge.Completion.Split('|'))
                {
                    var match = _killOrder.Match(part);
                    if (!match.Success) { killOrder = false; break; }
                    (match.Groups[1].Value == "K" ? first : last).Add(int.Parse(match.Groups[2].Value));
                }

                var limit = _turnLimit.Match(challenge.Completion);

                if (killOrder && (first.Count == 0 || last.Count == 0))
                {
                    challenge.KillFirst = first;
                    challenge.KillLast = last;
                }
                else if (limit.Success) challenge.TurnLimit = int.Parse(limit.Groups[1].Value);
                else if (byCriterion.TryGetValue(challenge.Completion, out int twin)) challenge.Kind = twin;
                else if (challenge.Completion == Scripted)
                {
                    if (_byHand.TryGetValue(challenge.Id, out var rule)) challenge.Rule = rule;
                    else if (Array.IndexOf(_killBossLast, challenge.Id) >= 0) challenge.KillLast = challenge.Monsters;
                    else if (Array.IndexOf(_beforeRound6, challenge.Id) >= 0) challenge.TurnLimit = 6;
                    else if (challenge.Id == InLineWithAlly && watched.ContainsKey(SameLineage)) challenge.Kind = SameLineage;
                    else if (byName.TryGetValue(challenge.Name, out int namesake)) challenge.Kind = namesake;
                    else challenge.PartySizeOnly = _fighterCount.IsMatch(challenge.Activation)
                                                   && challenge.Activation.Contains("GN<");
                }
            }

            int judged = 0, total = 0;
            foreach (var challenge in _byId.Values)
            {
                if (!challenge.NeedsMonster) continue;
                total++;
                if (challenge.Judged) judged++;
            }
            Console.WriteLine($"[Retos] De los {total} que impone un monstruo, {judged} tienen quien " +
                              $"los juzgue; los otros {total - judged} no se impondrán.");
        }

        /// <summary>
        /// Whether the fighters there are fit: what the activation's "GN" says -- "Dúo" asks for
        /// fewer than three on the players' side, "Pegajoso" for more than one. Summons do not
        /// count.
        /// </summary>
        public static bool FitsParty(Challenge challenge, int players, int monsters)
        {
            foreach (System.Text.RegularExpressions.Match match in _fighterCount.Matches(challenge.Activation))
            {
                int count = match.Groups[3].Value == "0" ? players : monsters;
                int wanted = int.Parse(match.Groups[2].Value);
                bool fits = match.Groups[1].Value switch
                {
                    "<" => count < wanted,
                    ">" => count > wanted,
                    _ => count == wanted,
                };
                if (!fits) return false;
            }
            return true;
        }

        private static readonly Dictionary<int, Challenge> _byId = new();
        private static readonly List<Challenge> _offerable = new();

        /// <summary>The challenges each monster carries: monster → its own.</summary>
        private static readonly Dictionary<int, List<Challenge>> _byMonster = new();

        public static int Count => _byId.Count;
        public static int OfferableCount => _offerable.Count;
        public static int WithMonsterCount => _byMonster.Count;

        /// <summary>
        /// Leaves in the offer ONLY what can be watched, and gives a percentage to whatever lacks one.
        ///
        /// What rules is the watching, not the percentage. A challenge nobody checks never breaks, so on
        /// winning it would come out met and pay the bonus: offering it would be giving away experience and
        /// loot in every fight.
        ///
        /// The other way round it can be done: a challenge that is watched, if its percentage has never come
        /// over the wire, is given one. It is not a measurement and is marked as such.
        /// </summary>
        public static void OnlyOffer(IReadOnlyDictionary<int, int> vigilados)
        {
            _offerable.Clear();
            FindJudges(vigilados);

            foreach (var reto in _byId.Values)
            {
                if (!vigilados.TryGetValue(reto.Id, out int puesto)) continue;
                if (reto.DungeonOnly || reto.NeedsMonster || reto.MinGroupLevel <= 0) continue;

                reto.PercentMeasured = reto.Percent > 0;
                if (reto.Percent <= 0) reto.Percent = puesto;
                if (reto.Percent > 0) _offerable.Add(reto);
            }

            int medidos = _offerable.FindAll(r => r.PercentMeasured).Count;
            Console.WriteLine($"[Retos] Se ofrecerán {_offerable.Count}: {medidos} con el porcentaje " +
                              $"medido y {_offerable.Count - medidos} con uno puesto por nosotros.");
        }

        public static Challenge? Get(int id) => _byId.TryGetValue(id, out var reto) ? reto : null;

        /// <summary>
        /// The watched challenge this one is equivalent to. A generic one is its own judge; a
        /// boss's is judged by the twin <see cref="FindJudges"/> found it, or by nobody.
        /// </summary>
        public static int KindOf(int id)
        {
            var reto = Get(id);
            if (reto == null) return id;
            if (reto.Kind != 0) return reto.Kind;
            return reto.NeedsMonster ? 0 : id;
        }

        public static void Initialize()
        {
            _byId.Clear();
            _offerable.Clear();
            _byMonster.Clear();

            string path = Paths.Resolve("retos_3.6.10.10.json");
            if (!File.Exists(path))
            {
                Console.WriteLine($"[Retos] Falta {Path.GetFileName(path)}; no se ofrecerá ninguno. " +
                                  "Genéralo con tools/extraer_retos.py.");
                return;
            }

            try
            {
                using var doc = JsonDocument.Parse(File.ReadAllText(path));
                if (!doc.RootElement.TryGetProperty("retos", out var retos)) return;

                foreach (var entry in retos.EnumerateObject())
                {
                    if (!int.TryParse(entry.Name, out int id)) continue;
                    var d = entry.Value;

                    var incompatible = new List<int>();
                    if (d.TryGetProperty("incompatibles", out var lista))
                    {
                        foreach (var uno in lista.EnumerateArray()) incompatible.Add(uno.GetInt32());
                    }

                    var reto = new Challenge
                    {
                        Id = id,
                        Name = Text(d, "nombre"),
                        Description = Text(d, "descripcion"),
                        Category = d.TryGetProperty("categoria", out var c) ? c.GetInt32() : 0,
                        Incompatible = incompatible,
                        Activation = Text(d, "activacion"),
                        Completion = Text(d, "cumplimiento"),
                        Percent = LowestSeen(d),
                        MinGroupLevel = d.TryGetProperty("nivel_umbral", out var u)
                                        && u.ValueKind == JsonValueKind.Number ? u.GetInt32() : 0,
                        DungeonOnly = d.TryGetProperty("solo_mazmorra", out var m)
                                      && m.ValueKind == JsonValueKind.True,
                        Monsters = Monsters(d),
                    };

                    _byId[id] = reto;
                    if (reto.Offerable) _offerable.Add(reto);
                    if (reto.NeedsMonster)
                    {
                        foreach (int bicho in reto.Monsters)
                        {
                            if (!_byMonster.TryGetValue(bicho, out var suyos))
                            {
                                suyos = new List<Challenge>();
                                _byMonster[bicho] = suyos;
                            }
                            suyos.Add(reto);
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[Retos] No se han podido leer: {ex.Message}");
                return;
            }

            Console.WriteLine($"[Retos] {_byId.Count} retos, {_offerable.Count} ofrecibles " +
                              "(los que tienen porcentaje medido).");
        }

        /// <summary>
        /// The challenges the content IMPOSES: the ones requiring a monster that is in front.
        ///
        /// These are not proposed, they are set, and they arrive with the bonus at zero. It is measured in
        /// the anomaly: the player chose one of the two normal ones, the server filled in the missing one,
        /// and after it sent three more kww -- 772 Duel, 773 Prudent and 774 Survivor -- that had never been
        /// offered and go without a percentage. All three require monster 5781, which was exactly that
        /// anomaly's.
        ///
        /// They are the ones with an achievement behind them, so they are taken away from a character who
        /// already has them done: an achievement is done once.
        /// </summary>
        public static IReadOnlyList<Challenge> Imposed(IEnumerable<int> monsters,
                                                       IReadOnlyCollection<int> alreadyDone,
                                                       int players, int monsterCount)
        {
            var salida = new List<Challenge>();
            var puestos = new HashSet<int>();

            foreach (int bicho in monsters)
            {
                if (!_byMonster.TryGetValue(bicho, out var suyos)) continue;
                foreach (var reto in suyos)
                {
                    if (alreadyDone.Contains(reto.Id)) continue;

                    // One nobody judges would come out won just by winning, and one that asks for
                    // few is not for a whole party.
                    if (!reto.Judged || !FitsParty(reto, players, monsterCount)) continue;
                    if (!puestos.Add(reto.Id)) continue;
                    salida.Add(reto);
                }
            }
            return salida;
        }

        private static List<int> Monsters(JsonElement d)
        {
            var salida = new List<int>();
            if (d.TryGetProperty("monstruos_requeridos", out var lista)
                && lista.ValueKind == JsonValueKind.Array)
            {
                foreach (var uno in lista.EnumerateArray())
                {
                    if (uno.ValueKind == JsonValueKind.Number) salida.Add(uno.GetInt32());
                }
            }
            return salida;
        }

        private static string Text(JsonElement d, string campo)
            => d.TryGetProperty(campo, out var v) && v.ValueKind == JsonValueKind.String
               ? (v.GetString() ?? "") : "";

        /// <summary>
        /// A challenge's base percentage: the LOWEST of those seen for it.
        ///
        /// The lowest is taken because the same challenge sometimes comes out with sixty points more -- 6 at
        /// 90 and at 150, 40 at 65 and at 125, 971 at 80 and at 140 --, so the high one carries inside a
        /// fight modifier that has not been possible to reconstruct.
        ///
        /// Two of the sixteen stay high by necessity: of 9 and of 969 there is only one reading, and both
        /// are from fights where the other challenges were raised too. Most likely their base is sixty less,
        /// but that would already be deducing, so what was measured goes.
        /// </summary>
        private static int LowestSeen(JsonElement d)
        {
            if (!d.TryGetProperty("porcentajes_vistos", out var vistos)) return 0;

            int menor = 0;
            foreach (var uno in vistos.EnumerateArray())
            {
                if (!uno.TryGetProperty("exp", out var e)) continue;
                int valor = e.GetInt32();
                if (valor > 0 && (menor == 0 || valor < menor)) menor = valor;
            }
            return menor;
        }

        /// <summary>
        /// Two candidates to propose, or none if there is nowhere to take them from.
        ///
        /// They are ALTERNATIVES to each other, so they do not need to be compatible with one another -- in
        /// the captures two that the table marks as incompatible were offered together --. What is respected
        /// is what is already LOCKED IN: against that the list does rule.
        /// </summary>
        public static IReadOnlyList<Challenge> Pair(int groupLevel, IReadOnlyCollection<int> alreadyFixed,
                                                    Random dado)
        {
            var pool = new List<Challenge>();
            foreach (var reto in _offerable)
            {
                if (groupLevel <= reto.MinGroupLevel) continue;
                if (alreadyFixed.Contains(reto.Id)) continue;
                if (ClashesWithFixed(reto, alreadyFixed)) continue;
                pool.Add(reto);
            }

            if (pool.Count == 0) return Array.Empty<Challenge>();
            if (pool.Count == 1) return new[] { pool[0] };

            int uno = dado.Next(pool.Count);
            int otro = dado.Next(pool.Count - 1);
            if (otro >= uno) otro++;
            return new[] { pool[uno], pool[otro] };
        }

        /// <summary>Does it clash with any of those already locked in? Incompatibility goes both ways.</summary>
        private static bool ClashesWithFixed(Challenge reto, IReadOnlyCollection<int> alreadyFixed)
        {
            foreach (int fijado in alreadyFixed)
            {
                if (reto.Incompatible.Contains(fijado)) return true;
                var otro = Get(fijado);
                if (otro != null && otro.Incompatible.Contains(reto.Id)) return true;
            }
            return false;
        }
    }
}
