using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Text.Json;
using Jondo.Unity.Launcher;
using Jondo.Unity.World.Client;

namespace Jondo.Unity.World.Quests
{
    /// <summary>One thing a quest step wants done.</summary>
    public sealed class QuestObjective
    {
        public int Id { get; init; }
        public int StepId { get; init; }
        public int TypeId { get; init; }

        /// <summary>The map it wants you on, when it says. Zero when it does not.</summary>
        public long MapId { get; init; }

        /// <summary>Where on the world map the client draws its flag, when it has one.</summary>
        public (int X, int Y)? Coords { get; init; }

        /// <summary>parameter0..4 with the trailing zeroes cut. What they mean depends on the type.</summary>
        public IReadOnlyList<int> Parameters { get; init; } = Array.Empty<int>();

        /// <summary>
        /// The NPC this objective sends you to, or zero.
        /// </summary>
        /// <remarks>
        /// Five of the eighteen types name an NPC, and they all put its id in parameter0. That is
        /// measured, not assumed: 7,806 of the 7,844 objectives of those types carry a number that
        /// is a real NpcTemplates id. The other thirteen types name a monster, an item or a map in
        /// the same slot, which is why this is gated on the type rather than read blind.
        /// </remarks>
        public int NpcId => QuestCatalogue.NamesAnNpc(TypeId) && Parameters.Count > 0 ? Parameters[0] : 0;

        /// <summary>
        /// The monster this objective wants beaten, or zero.
        /// </summary>
        /// <remarks>
        /// Same measured layout as the NPC one, on the three types that name a monster: parameter0
        /// is the monster and parameter1 is how many. 776 of the 788 type 6 objectives, 143 of 143
        /// type 14 and 88 of 88 type 16 carry a real Monsters id in parameter0, and parameter1
        /// never leaves the range 1 to 25 — which is what says round the other way is wrong.
        /// </remarks>
        public int MonsterId => QuestCatalogue.NamesAMonster(TypeId) && Parameters.Count > 0
            ? Parameters[0]
            : 0;

        /// <summary>The map it has to happen on, for the one type that says. Zero otherwise.</summary>
        public long OnMap => TypeId == QuestCatalogue.BeatOnMap && Parameters.Count > 2
            ? Parameters[2]
            : 0;

        /// <summary>
        /// Whether the count has to be reached inside a single fight.
        /// </summary>
        /// <remarks>
        /// Type 6 and type 16 say "en un solo combate" in the client's own words; type 14 does not,
        /// and is a tally that carries across fights. Treating them the same either makes a
        /// three-Jalató objective impossible in a two-Jalató fight, or lets a five-kill one be
        /// finished by five separate fights when Ankama wanted one.
        /// </remarks>
        public bool InOneFight => TypeId is QuestCatalogue.BeatInOneFight or QuestCatalogue.BeatOnMap;

        /// <summary>How many it takes. One for everything that is not a count.</summary>
        public int Needed => QuestCatalogue.NamesAMonster(TypeId) && Parameters.Count > 1
            ? Math.Max(1, Parameters[1])
            : 1;

        /// <summary>
        /// The item this objective wants carried, or zero.
        /// </summary>
        /// <remarks>
        /// Two layouts, because the two families put it in different slots and reading it blind
        /// would fetch an NPC id:
        ///
        ///   2 and 3   [npc, item, count]   "Montrer à X : n Y", "Ramener à X : n Y"
        ///   17        [item, count]        "Fabriquer n Y et fermer l'interface"
        ///
        /// Measured on the catalogue: parameter0 of types 2 and 3 is a real NpcTemplates id 320 of
        /// 323 and 2,143 of 2,146 times, which is what says the item cannot be there. world.db has
        /// no table to check the item against — ItemTemplates is keyed differently — so this is
        /// read by position and the server is left to find nothing if the id is wrong, rather than
        /// pretending the number was verified.
        /// </remarks>
        public int ItemId
        {
            get
            {
                if (TypeId is QuestCatalogue.ShowItems or QuestCatalogue.BringItems)
                    return Parameters.Count > 1 ? Parameters[1] : 0;

                if (TypeId == QuestCatalogue.CraftItem)
                    return Parameters.Count > 0 ? Parameters[0] : 0;

                return 0;
            }
        }

        /// <summary>How many of <see cref="ItemId"/>. Zero when the objective wants no item.</summary>
        public int ItemCount
        {
            get
            {
                if (TypeId is QuestCatalogue.ShowItems or QuestCatalogue.BringItems)
                    return Parameters.Count > 2 ? Math.Max(1, Parameters[2]) : 1;

                if (TypeId == QuestCatalogue.CraftItem)
                    return Parameters.Count > 1 ? Math.Max(1, Parameters[1]) : 1;

                return 0;
            }
        }

        /// <summary>Whether finishing this one takes the items away rather than just showing them.</summary>
        public bool ConsumesItems => TypeId == QuestCatalogue.BringItems;

        /// <summary>
        /// The map this objective is finished by walking onto, or zero.
        /// </summary>
        /// <remarks>
        /// From the objective's own map field and NOT from parameter0, which for this type is a
        /// text id: the 874 of them all resolve to a place NAME — "Laboratoire Wabbit", "Souterrain
        /// de la Bibliothèque" — and none of them is a map id. 765 of the 874 carry the map itself,
        /// so that is what is used; the other 109 name a map this server cannot identify and are
        /// left open rather than closed on a guess.
        /// </remarks>
        public long DiscoverMapId => TypeId == QuestCatalogue.DiscoverMap ? MapId : 0;

        /// <summary>The subarea this objective is finished by walking into, or zero.</summary>
        /// <remarks>Only five in the game, and parameter0 is a real SubAreaId in all five.</remarks>
        public int DiscoverAreaId => TypeId == QuestCatalogue.DiscoverArea && Parameters.Count > 0
            ? Parameters[0]
            : 0;
    }

    /// <summary>What a step hands over when it is finished.</summary>
    public sealed class QuestReward
    {
        public int Id { get; init; }
        public double ExperienceRatio { get; init; }
        public double KamasRatio { get; init; }

        /// <summary>Item id and how many of it.</summary>
        public IReadOnlyList<(int Item, int Count)> Items { get; init; } = Array.Empty<(int, int)>();

        public IReadOnlyList<int> Spells { get; init; } = Array.Empty<int>();
        public IReadOnlyList<int> Emotes { get; init; } = Array.Empty<int>();
        public IReadOnlyList<int> Jobs { get; init; } = Array.Empty<int>();
        public IReadOnlyList<int> Titles { get; init; } = Array.Empty<int>();

        public bool Empty => Items.Count == 0 && Spells.Count == 0 && Emotes.Count == 0
                             && Jobs.Count == 0 && Titles.Count == 0;
    }

    /// <summary>One step of a quest: what to do, and the line that hands it over.</summary>
    public sealed class QuestStep
    {
        public int Id { get; init; }
        public int QuestId { get; init; }
        public string Name { get; init; } = "";
        public string Description { get; init; } = "";

        /// <summary>
        /// The NPC line that gives this step out. Zero when the step is not handed over by talking.
        /// </summary>
        /// <remarks>
        /// This is the join the whole screen is built on, and it is the same number the protocol
        /// carries: in the capture of a quest being accepted, the server walks the conversation
        /// down to line 50071 and then starts quest 2432, whose only step declares dialogId 50071.
        /// 1,260 of the 2,225 steps have one, and every one of those resolves to real text.
        /// </remarks>
        public long DialogId { get; init; }

        public int OptimalLevel { get; init; }

        public IReadOnlyList<QuestObjective> Objectives { get; init; } = Array.Empty<QuestObjective>();
        public IReadOnlyList<QuestReward> Rewards { get; init; } = Array.Empty<QuestReward>();

        public override string ToString() => Name.Length > 0 ? Name : $"step {Id}";
    }

    /// <summary>Who hands a quest out, and where they stand.</summary>
    public readonly struct QuestGiver
    {
        public QuestGiver(int npcId, long mapId)
        {
            NpcId = npcId;
            MapId = mapId;
        }

        public int NpcId { get; }
        public long MapId { get; }
    }

    /// <summary>A whole quest.</summary>
    public sealed class Quest
    {
        public int Id { get; init; }
        public string Name { get; init; } = "";
        public int CategoryId { get; init; }
        public string Category { get; init; } = "";
        public int LevelMin { get; init; }
        public int LevelMax { get; init; }

        public bool Dungeon { get; init; }
        public bool Party { get; init; }
        public bool Event { get; init; }
        public bool Repeatable { get; init; }

        /// <summary>What has to be true before an NPC will offer it. See <see cref="Requires"/>.</summary>
        public string Criterion { get; init; } = "";

        public IReadOnlyList<QuestGiver> Givers { get; init; } = Array.Empty<QuestGiver>();
        public IReadOnlyList<QuestStep> Steps { get; init; } = Array.Empty<QuestStep>();

        /// <summary>The quests that have to be finished first, read out of the criterion.</summary>
        public IReadOnlyList<int> Requires { get; init; } = Array.Empty<int>();

        /// <summary>How many of its steps are handed over by an NPC saying something.</summary>
        public int SpokenSteps
        {
            get
            {
                int n = 0;
                foreach (var step in Steps) if (step.DialogId > 0) n++;
                return n;
            }
        }

        public override string ToString() => Name.Length > 0 ? Name : $"quest {Id}";
    }

    /// <summary>
    /// The quest catalogue: 1,976 quests, 2,225 steps, 15,547 objectives.
    /// </summary>
    /// <remarks>
    /// Read out of <c>datos/quests_3.6.10.10.json</c>, which
    /// <c>tools/extract_quests.py</c> flattens from six Unity dumps the repository does not carry.
    /// Nothing here talks to <c>world.db</c> except through the text lookup.
    ///
    /// Everything is a translation key on disk and a string here, resolved through
    /// <see cref="ClientText"/>, so the screen follows the language the rest of the editor is in.
    /// That is not a nicety: a quest called <em>El desratizador desratizado</em> in Spanish is
    /// <em>Le dératiseur dératisé</em> in French, and a step written against one set of names has
    /// to be readable from the other.
    ///
    /// <b>Read only, and shared.</b> Nothing writes quests through this class; the server reads the
    /// same instance to play them and the editor reads it to show them, which is the point of it
    /// living here rather than in either one.
    /// </remarks>
    public sealed class QuestCatalogue
    {
        /// <summary>The five objective types whose parameter0 is an npcId.</summary>
        private static readonly HashSet<int> NpcTypes = new HashSet<int> { 1, 2, 3, 9, 12 };

        /// <summary>
        /// "#1", with nothing to go on but the text. 5,670 objectives, the biggest kind by far.
        /// </summary>
        /// <remarks>
        /// This is the one the client used to be trusted about, and it should not be. Across the
        /// 401 captures there are exactly 16 <c>idw</c> frames — the client saying an objective is
        /// done — every one of them of this type and every one of them from the guided tutorial.
        /// Outside the tutorial the client never reports one, because on Ankama's server it was the
        /// SERVER that decided: the poster was clicked, the stele was examined, the item appeared.
        ///
        /// Which stele, on which map, is not in the catalogue: the objective's map field is zero
        /// and parameter0 is only the text that describes it. That binding is content, and it lives
        /// in <c>content/quests/objectives.json</c>.
        /// </remarks>
        public const int FreeText = 0;

        /// <summary>"Montrer à #1 : #3 #2" — carry the items and show them. 323 objectives.</summary>
        public const int ShowItems = 2;

        /// <summary>"Ramener à #1 : x#3 #2" — hand the items over. 2,146 objectives.</summary>
        public const int BringItems = 3;

        /// <summary>"Découvrir la carte : #1". 874 objectives, 765 naming the map.</summary>
        public const int DiscoverMap = 4;

        /// <summary>"Découvrir la zone #1". Five in the whole game.</summary>
        public const int DiscoverArea = 5;

        /// <summary>"Fabriquer #2 #1 et fermer l'interface". 109 objectives.</summary>
        public const int CraftItem = 17;

        /// <summary>"Beat #2 x #1 in one fight". 788 objectives.</summary>
        public const int BeatInOneFight = 6;

        /// <summary>"Beat #2 x #1", carried across fights. 143 objectives.</summary>
        public const int BeatAnywhere = 14;

        /// <summary>"Beat #2 x #1 on map #3 in one fight". 88 objectives.</summary>
        public const int BeatOnMap = 16;

        /// <summary>The three objective types whose parameter0 is a monster id.</summary>
        private static readonly HashSet<int> MonsterTypes
            = new HashSet<int> { BeatInOneFight, BeatAnywhere, BeatOnMap };

        private readonly Dictionary<int, Quest> _quests = new Dictionary<int, Quest>();
        private readonly Dictionary<int, QuestStep> _steps = new Dictionary<int, QuestStep>();
        private readonly Dictionary<int, QuestObjective> _objectives = new Dictionary<int, QuestObjective>();

        /// <summary>Objective type to the sentence the client draws it with, "#1" and all.</summary>
        private readonly Dictionary<int, string> _typeNames = new Dictionary<int, string>();

        private List<Quest>? _all;

        public QuestCatalogue(ClientText? text = null, Action<string>? report = null)
        {
            Read(text, report);
        }

        /// <summary>True when the catalogue was on disk and parsed.</summary>
        public bool Ready => _quests.Count > 0;

        public int QuestCount => _quests.Count;
        public int StepCount => _steps.Count;
        public int ObjectiveCount => _objectives.Count;

        /// <summary>How many steps are handed over by a line of dialogue. The join, counted.</summary>
        public int SpokenSteps { get; private set; }

        /// <summary>How many quests will not be offered until another one is finished.</summary>
        public int GatedQuests { get; private set; }

        /// <summary>Whether an objective of this type puts an npcId in parameter0.</summary>
        public static bool NamesAnNpc(int typeId) => NpcTypes.Contains(typeId);

        /// <summary>Whether an objective of this type puts a monster id in parameter0.</summary>
        public static bool NamesAMonster(int typeId) => MonsterTypes.Contains(typeId);

        /// <summary>Every quest, by name, for the picker.</summary>
        public List<Quest> All()
        {
            if (_all != null) return _all;

            _all = new List<Quest>(_quests.Values);
            _all.Sort((a, b) => string.Compare(a.Name, b.Name, StringComparison.CurrentCultureIgnoreCase));
            return _all;
        }

        public Quest? Of(int questId) => _quests.TryGetValue(questId, out var quest) ? quest : null;

        public QuestStep? Step(int stepId) => _steps.TryGetValue(stepId, out var step) ? step : null;

        /// <summary>The sentence the client draws an objective of this type with.</summary>
        public string TypeName(int typeId)
            => _typeNames.TryGetValue(typeId, out string? said) ? said : "";

        /// <summary>
        /// What an objective asks for, with its parameters filled into the client's own sentence.
        /// </summary>
        /// <remarks>
        /// The client's templates read <c>Ve a ver a #1</c> and <c>Entrégale a #1: #3 x #2</c>, and
        /// the numbering is one-based over the parameters. Names for the things the parameters
        /// point at are not looked up here — that needs the NPC and item catalogues, which the
        /// screen has and this class deliberately does not — so a caller passes in whatever it can
        /// resolve and the rest falls back to the id.
        /// </remarks>
        public string Describe(QuestObjective objective, Func<int, int, string>? name = null)
        {
            string template = TypeName(objective.TypeId);
            if (template.Length == 0)
            {
                return $"type {objective.TypeId}";
            }

            var text = new StringBuilder(template);
            for (int slot = objective.Parameters.Count; slot >= 1; slot--)
            {
                string token = "#" + slot;
                int index = text.ToString().IndexOf(token, StringComparison.Ordinal);
                if (index < 0) continue;

                int value = objective.Parameters[slot - 1];
                string said = name?.Invoke(slot, value) ?? "";
                text.Remove(index, token.Length);
                text.Insert(index, said.Length > 0 ? said : value.ToString());
            }

            return text.ToString();
        }

        // ─── Reading ──────────────────────────────────────────────────────────────

        private void Read(ClientText? text, Action<string>? report)
        {
            string path = Paths.QuestsJson;
            if (!File.Exists(path))
            {
                report?.Invoke($"{Path.GetFileName(path)} is not there; the quest section will be " +
                               "empty. Run tools/extract_quests.py to build it.");
                return;
            }

            try
            {
                using var doc = JsonDocument.Parse(File.ReadAllText(path));
                var root = doc.RootElement;

                string Say(long key) => text?.Of(key) ?? "";

                if (root.TryGetProperty("objectiveTypes", out var types))
                {
                    foreach (var entry in types.EnumerateObject())
                    {
                        if (int.TryParse(entry.Name, out int id) && entry.Value.TryGetInt64(out long key))
                        {
                            _typeNames[id] = Say(key);
                        }
                    }
                }

                var categories = new Dictionary<int, string>();
                if (root.TryGetProperty("categories", out var cats))
                {
                    foreach (var entry in cats.EnumerateObject())
                    {
                        if (int.TryParse(entry.Name, out int id))
                        {
                            categories[id] = Say(Long(entry.Value, "name"));
                        }
                    }
                }

                var rewards = ReadRewards(root);
                var objectivesByStep = ReadObjectives(root);
                var stepsByQuest = ReadSteps(root, Say, rewards, objectivesByStep);

                ReadQuests(root, Say, categories, stepsByQuest);
            }
            catch (Exception ex)
            {
                report?.Invoke($"{Path.GetFileName(path)} is unreadable: {ex.Message}");
            }
        }

        private static Dictionary<int, QuestReward> ReadRewards(JsonElement root)
        {
            var rewards = new Dictionary<int, QuestReward>();
            if (!root.TryGetProperty("rewards", out var table)) return rewards;

            foreach (var entry in table.EnumerateObject())
            {
                if (!int.TryParse(entry.Name, out int id)) continue;
                var row = entry.Value;

                var items = new List<(int, int)>();
                if (row.TryGetProperty("items", out var list) && list.ValueKind == JsonValueKind.Array)
                {
                    foreach (var pair in list.EnumerateArray())
                    {
                        var numbers = Numbers(pair);
                        if (numbers.Count >= 2) items.Add((numbers[0], numbers[1]));
                        else if (numbers.Count == 1) items.Add((numbers[0], 1));
                    }
                }

                rewards[id] = new QuestReward
                {
                    Id = id,
                    ExperienceRatio = Double(row, "experienceRatio"),
                    KamasRatio = Double(row, "kamasRatio"),
                    Items = items,
                    Spells = Numbers(row, "spells"),
                    Emotes = Numbers(row, "emotes"),
                    Jobs = Numbers(row, "jobs"),
                    Titles = Numbers(row, "titles"),
                };
            }

            return rewards;
        }

        private Dictionary<int, List<QuestObjective>> ReadObjectives(JsonElement root)
        {
            var byStep = new Dictionary<int, List<QuestObjective>>();
            if (!root.TryGetProperty("objectives", out var table)) return byStep;

            foreach (var entry in table.EnumerateObject())
            {
                if (!int.TryParse(entry.Name, out int id)) continue;
                var row = entry.Value;

                var coordinates = Numbers(row, "coords");
                var objective = new QuestObjective
                {
                    Id = id,
                    StepId = (int)Long(row, "step"),
                    TypeId = (int)Long(row, "type"),
                    MapId = Long(row, "map"),
                    Coords = coordinates.Count == 2 ? (coordinates[0], coordinates[1]) : null,
                    Parameters = Numbers(row, "params"),
                };

                _objectives[id] = objective;

                if (!byStep.TryGetValue(objective.StepId, out var list))
                {
                    byStep[objective.StepId] = list = new List<QuestObjective>();
                }

                list.Add(objective);
            }

            return byStep;
        }

        private Dictionary<int, List<QuestStep>> ReadSteps(
            JsonElement root, Func<long, string> say,
            IReadOnlyDictionary<int, QuestReward> rewards,
            IReadOnlyDictionary<int, List<QuestObjective>> objectivesByStep)
        {
            var byQuest = new Dictionary<int, List<QuestStep>>();
            if (!root.TryGetProperty("steps", out var table)) return byQuest;

            foreach (var entry in table.EnumerateObject())
            {
                if (!int.TryParse(entry.Name, out int id)) continue;
                var row = entry.Value;

                var mine = new List<QuestReward>();
                foreach (int rewardId in Numbers(row, "rewards"))
                {
                    if (rewards.TryGetValue(rewardId, out var reward)) mine.Add(reward);
                }

                // The order the client declares them in, not the order they came out of a
                // dictionary: a step's objectives are a list to be worked through.
                var objectives = objectivesByStep.TryGetValue(id, out var found)
                    ? new List<QuestObjective>(found)
                    : new List<QuestObjective>();
                var declared = Numbers(row, "objectives");
                if (declared.Count > 0)
                {
                    objectives.Sort((a, b) =>
                    {
                        int left = declared.IndexOf(a.Id), right = declared.IndexOf(b.Id);
                        if (left < 0) left = int.MaxValue;
                        if (right < 0) right = int.MaxValue;
                        return left != right ? left.CompareTo(right) : a.Id.CompareTo(b.Id);
                    });
                }

                long dialog = Long(row, "dialog");
                if (dialog > 0) SpokenSteps++;

                var step = new QuestStep
                {
                    Id = id,
                    QuestId = (int)Long(row, "quest"),
                    Name = say(Long(row, "name")),
                    Description = say(Long(row, "description")),
                    DialogId = dialog,
                    OptimalLevel = (int)Long(row, "level"),
                    Objectives = objectives,
                    Rewards = mine,
                };

                _steps[id] = step;

                if (!byQuest.TryGetValue(step.QuestId, out var list))
                {
                    byQuest[step.QuestId] = list = new List<QuestStep>();
                }

                list.Add(step);
            }

            return byQuest;
        }

        private void ReadQuests(JsonElement root, Func<long, string> say,
                                IReadOnlyDictionary<int, string> categories,
                                IReadOnlyDictionary<int, List<QuestStep>> stepsByQuest)
        {
            if (!root.TryGetProperty("quests", out var table)) return;

            foreach (var entry in table.EnumerateObject())
            {
                if (!int.TryParse(entry.Name, out int id)) continue;
                var row = entry.Value;

                var givers = new List<QuestGiver>();
                if (row.TryGetProperty("givers", out var list) && list.ValueKind == JsonValueKind.Array)
                {
                    foreach (var pair in list.EnumerateArray())
                    {
                        var numbers = Longs(pair);
                        if (numbers.Count >= 2) givers.Add(new QuestGiver((int)numbers[0], numbers[1]));
                    }
                }

                // In the order the quest declares, which is the order they are played.
                var steps = new List<QuestStep>();
                foreach (int stepId in Numbers(row, "steps"))
                {
                    if (_steps.TryGetValue(stepId, out var step)) steps.Add(step);
                }

                if (steps.Count == 0 && stepsByQuest.TryGetValue(id, out var loose))
                {
                    steps.AddRange(loose);
                }

                string criterion = row.TryGetProperty("criterion", out var c) && c.ValueKind == JsonValueKind.String
                    ? c.GetString() ?? ""
                    : "";

                var requires = FinishedQuests(criterion);
                if (requires.Count > 0) GatedQuests++;

                int categoryId = (int)Long(row, "category");
                _quests[id] = new Quest
                {
                    Id = id,
                    Name = say(Long(row, "name")),
                    CategoryId = categoryId,
                    Category = categories.TryGetValue(categoryId, out string? said) ? said : "",
                    LevelMin = (int)Long(row, "levelMin"),
                    LevelMax = (int)Long(row, "levelMax"),
                    Dungeon = Long(row, "dungeon") != 0,
                    Party = Long(row, "party") != 0,
                    Event = Long(row, "event") != 0,
                    Repeatable = Long(row, "repeatLimit") > 1 || Long(row, "repeatType") > 1,
                    Criterion = criterion,
                    Givers = givers,
                    Steps = steps,
                    Requires = requires,
                };
            }
        }

        /// <summary>
        /// The quests a criterion says must already be finished.
        /// </summary>
        /// <remarks>
        /// Ankama writes the whole start condition as one string — <c>Ps=1&amp;Pa=1&amp;PL&gt;29&amp;Qf=55</c>
        /// — with a two-letter operator, a comparison and a value, joined by <c>&amp;</c> and
        /// <c>|</c>. There are 29 operators in use across the 1,976 quests and this reads exactly
        /// one of them: <c>Qf</c>, "quest finished". That is the one that chains a questline
        /// together, and 990 quests have at least one.
        ///
        /// Only equality is taken. <c>Qf!=55</c> means the opposite and reading it as a
        /// prerequisite would draw an arrow pointing the wrong way, which is worse than no arrow.
        /// </remarks>
        public static List<int> FinishedQuests(string criterion)
        {
            var found = new List<int>();
            if (criterion.Length == 0) return found;

            int at = 0;
            while (at < criterion.Length)
            {
                int mark = criterion.IndexOf("Qf", at, StringComparison.Ordinal);
                if (mark < 0) break;

                at = mark + 2;

                // Only "Qf=", never "Qf!=" or "Qf<".
                if (at >= criterion.Length || criterion[at] != '=') continue;
                at++;

                int start = at;
                while (at < criterion.Length && char.IsDigit(criterion[at])) at++;
                if (at > start && int.TryParse(criterion.Substring(start, at - start), out int quest))
                {
                    if (!found.Contains(quest)) found.Add(quest);
                }
            }

            return found;
        }

        // ─── Reading the odds and ends out of JSON ────────────────────────────────

        private static long Long(JsonElement row, string name)
            => row.TryGetProperty(name, out var value) && value.TryGetInt64(out long number) ? number : 0;

        private static double Double(JsonElement row, string name)
            => row.TryGetProperty(name, out var value) && value.TryGetDouble(out double number) ? number : 0;

        private static List<int> Numbers(JsonElement row, string name)
            => row.TryGetProperty(name, out var value) ? Numbers(value) : new List<int>();

        private static List<int> Numbers(JsonElement value)
        {
            var numbers = new List<int>();
            if (value.ValueKind != JsonValueKind.Array) return numbers;

            foreach (var item in value.EnumerateArray())
            {
                if (item.TryGetInt32(out int number)) numbers.Add(number);
            }

            return numbers;
        }

        private static List<long> Longs(JsonElement value)
        {
            var numbers = new List<long>();
            if (value.ValueKind != JsonValueKind.Array) return numbers;

            foreach (var item in value.EnumerateArray())
            {
                if (item.TryGetInt64(out long number)) numbers.Add(number);
            }

            return numbers;
        }
    }
}
