using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;

namespace Jondo.Unity.World.Quests
{
    /// <summary>
    /// How a free-text objective is finished. The text says which, and only the text does.
    /// </summary>
    /// <remarks>
    /// Type 0 is not one thing. Reading the 205 still unbound in the quests this server can hand
    /// out, the French sorts into four verbs and a long tail:
    ///
    ///   Click       "Examiner la stèle", "Utiliser la longue-vue de Matu Vuh", "Fouiller la tombe"
    ///   Talk        "Parler à un vieux de la vieille", "Parler au premier malade"
    ///   Enter       "Pénétrer dans l'antre du Milimilou", "Entrer dans la taverne d'Astrub"
    ///   Beat        "Vaincre le Milimilou"
    ///
    /// and 118 of the 205 that say none of those. Treating them all as "click something" would put
    /// a clickable stele where the quest wanted a conversation.
    /// </remarks>
    public enum QuestBindingKind
    {
        /// <summary>Click one of the elements. The default, and the commonest.</summary>
        Click,

        /// <summary>Talk to the NPC. The catalogue has a type for this; these are the ones it did not use.</summary>
        Talk,

        /// <summary>Arrive on the map. Safe to work out, because the objective carries the map itself.</summary>
        Enter,

        /// <summary>Beat the monster.</summary>
        Beat,

        /// <summary>
        /// Walk on the map: the first walk the character finishes there. The guided tutorial's
        /// 19534 «Desplázate» is done by the server after the client's first jrw and jqi, with no
        /// idw from the client.
        /// </summary>
        Walk,

        /// <summary>
        /// Every objective in <see cref="QuestBinding.AfterObjectives"/> done, of whatever quest:
        /// the tutorial's 19253 «Recolecta los recursos necesarios» closes as 1629's fifth resource
        /// is gathered (ieb 1629 9660, then idz 2502 3187).
        /// </summary>
        Objectives,

        /// <summary>
        /// Gather <see cref="QuestBinding.ItemId"/> on the map: the tutorial's «Recolecta trigo»
        /// closes on the wheat of the jobs room (iwo 541424, iua 289, then ieb 1629 9657).
        /// </summary>
        Gather,

        /// <summary>
        /// Every element of <see cref="QuestBinding.Elements"/> read: «Averigua más información
        /// sobre tu objetivo: los dofus», the Dofusarium's six statues, each a document.
        /// </summary>
        Read,

        /// <summary>
        /// One of <see cref="QuestBinding.Replies"/> chosen in a conversation: «Habla con un
        /// veterano» is done when Noken gets to wishing the character luck, not on «hello».
        /// </summary>
        Reply,
    }

    /// <summary>
    /// What a quest objective is finished by, when the catalogue does not say.
    /// </summary>
    /// <remarks>
    /// 5,670 of the game's 15,547 objectives are type 0, which the client calls "#1" and fills in
    /// with a line of text: "Examiner la stèle d'un vestige dans les pâturages", "Utiliser la
    /// longue-vue de Matu Vuh", "Inspecter la cave". The catalogue gives them no map, no element
    /// and no item — only that text. On Ankama's server something decided when they were done, and
    /// whatever that was never left it.
    ///
    /// It is not the client either. Across the 401 captures there are exactly 16 <c>idw</c> frames
    /// — the client claiming an objective is finished — and all 16 are type 0 and all 16 are from
    /// the guided tutorial. Outside it the client never claims one.
    ///
    /// So this is the join, and it is content: one row says "objective 9817 is finished by clicking
    /// element 504680 on map 153356296". Written by a person, checked by
    /// <c>tools/check_quest_bindings.py</c>, and kept apart from Ankama's catalogue so the two are
    /// never confused.
    /// </remarks>
    public sealed class QuestBinding
    {
        /// <summary>The objective this closes. The join to Ankama's catalogue.</summary>
        public int ObjectiveId { get; init; }

        /// <summary>The quest it belongs to, carried so a bad row can be spotted without a lookup.</summary>
        public int QuestId { get; init; }

        /// <summary>What finishes it. See <see cref="QuestBindingKind"/>.</summary>
        public QuestBindingKind Kind { get; init; } = QuestBindingKind.Click;

        /// <summary>The NPC to talk to, for <see cref="QuestBindingKind.Talk"/>.</summary>
        public int NpcId { get; init; }

        /// <summary>The map to arrive on, for <see cref="QuestBindingKind.Enter"/>.</summary>
        public long MapId { get; init; }

        /// <summary>The monster to beat, for <see cref="QuestBindingKind.Beat"/>.</summary>
        public int MonsterId { get; init; }

        /// <summary>
        /// The elements that finish it. Any one of them, not all.
        /// </summary>
        /// <remarks>
        /// A set and not one element because the objectives say so. "Examiner la stèle d'UN vestige
        /// dans les pâturages" — a vestige, not the vestige — and there are two steles in the
        /// pastures. Binding one of the two would send a player who clicked the other one walking
        /// to the far side of the subarea for no reason he could ever work out.
        /// </remarks>
        public IReadOnlyList<(long MapId, int ElementId)> Elements { get; init; }
            = Array.Empty<(long, int)>();

        /// <summary>
        /// The skill the element offers. <c>Utiliser</c> unless a row says otherwise.
        /// </summary>
        /// <remarks>
        /// An element with no skill is not clickable at all, so one has to be declared even for
        /// something the player just clicks. The <c>iwo</c> the client sends carries the skill
        /// INSTANCE and not the skill, so the captures cannot say which one Ankama used; 114 is the
        /// client's own generic "Utiliser", it exists in Skills, and it renders in every language.
        /// </remarks>
        public int SkillId { get; init; } = DefaultSkill;

        /// <summary>
        /// What the client is told the element IS. Minus one unless a row says otherwise.
        /// </summary>
        /// <remarks>
        /// Measured, not chosen. <c>datos/tipos_interactivos_3.6.10.10.json</c> is built from the
        /// captures — graphic to the type the server declared for it — and graphic 3518, which is
        /// the stele of Incarnam, appears 32 times and carries -1 every one of them, with no
        /// disagreement between captures. A zaap is 16 and an anomaly vestige is 359; a thing that
        /// exists only for a quest is -1, and that is what goes out.
        /// </remarks>
        public int TypeId { get; init; } = DefaultType;

        /// <summary>Items the click puts in the bag, if any: template and how many.</summary>
        public IReadOnlyList<(int Item, int Count)> Gives { get; init; }
            = Array.Empty<(int, int)>();

        /// <summary>What has to be carried for the element to respond.</summary>
        /// <remarks>
        /// It is checked before closing anything: without it, «pressing the tap with the lemonade» and
        /// «pressing the tap» are the same, and half the quest skips itself.
        /// </remarks>
        public IReadOnlyList<(int Item, int Count)> Requires { get; init; }
            = Array.Empty<(int, int)>();

        /// <summary>Whether what is required is spent on using it. By default not: carrying it is enough.</summary>
        public bool SpendsRequired { get; init; }

        /// <summary>The monster that comes out on pressing it, and how many. Zero when none comes out.</summary>
        /// <remarks>
        /// «Haz salir a la rata de su escondite» is not a decorative sentence: the next objective
        /// is to beat it, and without this there is nothing to fight against. The Rata Nsiosa is in zero
        /// groups of the world because it must not be placed -- it appears when it is made to appear.
        /// </remarks>
        public int SpawnsMonster { get; init; }
        public int SpawnsCount { get; init; } = 1;

        /// <summary>A monster of another kind that comes out with them («spawns»'s «with»), or zero.</summary>
        public int SpawnsWith { get; init; }

        /// <summary>
        /// How many fights against <see cref="MonsterId"/> a beat binding asks for: the three
        /// library rats of «Verdaderas ratas de biblioteca», one objective written as prose.
        /// </summary>
        public int Times { get; init; } = 1;

        /// <summary>The quest this element HANDS OUT, when an NPC does not give it.</summary>
        /// <remarks>
        /// There are quests that start by pressing something, not by talking: «Mort au rat» starts at the
        /// notice on the tavern's front. It also changes when the element is seen -- it is
        /// shown while the quest has NOT been taken, which is the opposite of all the others.
        /// </remarks>
        public int Starts { get; init; }

        /// <summary>
        /// A quest that starts once this objective is closed. The guided tutorial goes on that way:
        /// arriving at Ganymed's map closes 2545's 19536 (idz 3228) and starts 2502 (ief), with no
        /// NPC or element in between.
        /// </summary>
        public int ThenStarts { get; init; }

        /// <summary>The item gathered, for <see cref="QuestBindingKind.Gather"/>.</summary>
        public int ItemId { get; init; }

        /// <summary>
        /// The objectives whose being done closes it, for <see cref="QuestBindingKind.Objectives"/>;
        /// for any other kind, the objectives that have to be done before it can be: the three
        /// seekers of «La esperanza y la tragedia» come after Fécaline's book.
        /// </summary>
        public IReadOnlyList<int> AfterObjectives { get; init; } = Array.Empty<int>();

        /// <summary>The replies that close it, for <see cref="QuestBindingKind.Reply"/>.</summary>
        public IReadOnlyList<int> Replies { get; init; } = Array.Empty<int>();

        /// <summary>
        /// The row's map is not the one the objective carries, on purpose: «Entra en el antro de
        /// Minilubo Albino» carries the graves, where the gravedigger tells the way in, and the lair
        /// is another map. Without the word, a different map is taken for a copying mistake.
        /// </summary>
        public bool OverridesMap { get; init; }

        /// <summary>
        /// An NPC of the map who comes in once it is done: «Haz salir a la rata de su escondite»
        /// brings the Rata Nsiosa (2912) out, a jsn right after the ieb in «Matarratas»' capture.
        /// </summary>
        public int ShowsNpc { get; init; }

        /// <summary>
        /// The info line the client prints when it is done, or zero: the tavern's cellar says 591
        /// on the first look at the tap and 592 when the lemonade is poured (lqn before the ieb).
        /// </summary>
        public int Says { get; init; }

        /// <summary>
        /// The element only hands its items over, the objective stays: the blue pickaxe outside the
        /// quarry is picked up while «Libera a Rathrosk» is to do, and freeing him is his reply.
        /// </summary>
        public bool OnlyGives { get; init; }

        /// <summary>Why this row says what it says. Never read by the server; read by people.</summary>
        public string Why { get; init; } = "";

        /// <summary>The client's generic "Utiliser", skill 114, element action 3.</summary>
        public const int DefaultSkill = 114;

        /// <summary>What the captures declare for a quest-only element. See <see cref="TypeId"/>.</summary>
        public const int DefaultType = -1;
    }

    /// <summary>Every binding there is, indexed the two ways the server asks for them.</summary>
    public sealed class QuestBindingBook
    {
        private readonly Dictionary<int, QuestBinding> _byObjective = new Dictionary<int, QuestBinding>();

        private readonly Dictionary<(long MapId, int ElementId), List<QuestBinding>> _byElement
            = new Dictionary<(long, int), List<QuestBinding>>();

        private readonly Dictionary<long, List<QuestBinding>> _byMap
            = new Dictionary<long, List<QuestBinding>>();

        public int Count => _byObjective.Count;

        /// <summary>How many map-and-element pairs are spoken for.</summary>
        public int ElementCount => _byElement.Count;

        public QuestBinding? Of(int objectiveId)
            => _byObjective.TryGetValue(objectiveId, out var row) ? row : null;

        /// <summary>
        /// The bindings a click on that element could close. More than one is allowed on purpose:
        /// nothing says two quests cannot want the same stele examined.
        /// </summary>
        public IReadOnlyList<QuestBinding> At(long mapId, int elementId)
            => _byElement.TryGetValue((mapId, elementId), out var rows)
                ? rows
                : (IReadOnlyList<QuestBinding>)Array.Empty<QuestBinding>();

        /// <summary>Everything bound on a map, for deciding what to declare to a player.</summary>
        public IReadOnlyList<QuestBinding> OnMap(long mapId)
            => _byMap.TryGetValue(mapId, out var rows)
                ? rows
                : (IReadOnlyList<QuestBinding>)Array.Empty<QuestBinding>();

        public void Add(QuestBinding binding)
        {
            _byObjective[binding.ObjectiveId] = binding;

            // Only an element to click is the quest's own, drawn and pressed as such; the elements
            // of a «read» binding are the world's readables (the Dofusarium's statues), already there.
            // A beat binding's element brings its monsters out (the old stump of «Restauración rápida»).
            if (binding.Kind != QuestBindingKind.Click
                && !(binding.Kind == QuestBindingKind.Beat && binding.SpawnsMonster != 0)) return;

            foreach (var (mapId, elementId) in binding.Elements)
            {
                if (!_byElement.TryGetValue((mapId, elementId), out var here))
                {
                    _byElement[(mapId, elementId)] = here = new List<QuestBinding>();
                }

                here.Add(binding);

                if (!_byMap.TryGetValue(mapId, out var onMap))
                {
                    _byMap[mapId] = onMap = new List<QuestBinding>();
                }

                if (!onMap.Contains(binding)) onMap.Add(binding);
            }
        }
    }

    /// <summary>Reads <c>content/quests/objectives.json</c>.</summary>
    public static class QuestBindingContent
    {
        public const string AuthoredFile = "quests/objectives.json";

        /// <summary>
        /// Reads the file, or gives back an empty book. A missing file is not an error: without it
        /// the free-text objectives simply stay open, which is what happened before it existed.
        /// </summary>
        public static QuestBindingBook Load(string path, Action<string>? complain = null)
        {
            var book = new QuestBindingBook();
            if (!File.Exists(path)) return book;

            try
            {
                using var doc = JsonDocument.Parse(File.ReadAllText(path));
                if (!doc.RootElement.TryGetProperty("bindings", out var rows)
                    || rows.ValueKind != JsonValueKind.Array)
                {
                    return book;
                }

                foreach (var row in rows.EnumerateArray())
                {
                    var binding = Read(row);
                    if (binding != null) book.Add(binding);
                }
            }
            catch (Exception ex)
            {
                complain?.Invoke($"[Quests] Could not read {path}: {ex.Message}");
            }

            return book;
        }

        private static QuestBinding? Read(JsonElement row)
        {
            int objective = Int(row, "objective");
            if (objective == 0) return null;

            var elements = new List<(long, int)>();
            if (row.TryGetProperty("elements", out var list) && list.ValueKind == JsonValueKind.Array)
            {
                foreach (var one in list.EnumerateArray())
                {
                    long map = Long(one, "map");
                    int element = Int(one, "element");
                    if (map != 0 && element != 0) elements.Add((map, element));
                }
            }

            var gives = new List<(int, int)>();
            if (row.TryGetProperty("gives", out var items) && items.ValueKind == JsonValueKind.Array)
            {
                foreach (var one in items.EnumerateArray())
                {
                    int item = Int(one, "item");
                    if (item != 0) gives.Add((item, Math.Max(1, Int(one, "count"))));
                }
            }

            // What has to be carried for the element to respond. Without this one cannot
            // write «the tap only works if you carry the lemonade», which is half of the
            // quests that chain buying something with using it somewhere.
            var requires = new List<(int, int)>();
            if (row.TryGetProperty("requires", out var needed) && needed.ValueKind == JsonValueKind.Array)
            {
                foreach (var one in needed.EnumerateArray())
                {
                    int item = Int(one, "item");
                    if (item != 0) requires.Add((item, Math.Max(1, Int(one, "count"))));
                }
            }

            int spawnMonster = 0, spawnCount = 0, spawnWith = 0;
            if (row.TryGetProperty("spawns", out var spawn) && spawn.ValueKind == JsonValueKind.Object)
            {
                spawnMonster = Int(spawn, "monster");
                spawnCount = Math.Max(1, Int(spawn, "count"));
                spawnWith = Int(spawn, "with");
            }

            int skill = Int(row, "skill");
            bool hasType = row.TryGetProperty("type", out _);

            return new QuestBinding
            {
                ObjectiveId = objective,
                QuestId = Int(row, "quest"),
                Kind = Kind(row),
                NpcId = Int(row, "npc"),
                MapId = Long(row, "map"),
                MonsterId = Int(row, "monster"),
                Elements = elements,
                Gives = gives,
                Requires = requires,
                SpendsRequired = row.TryGetProperty("spends", out var spends)
                                 && spends.ValueKind == JsonValueKind.True,
                SpawnsMonster = spawnMonster,
                SpawnsCount = spawnCount,
                SpawnsWith = spawnWith,
                Times = Math.Max(1, Int(row, "times")),
                Starts = Int(row, "starts"),
                ThenStarts = Int(row, "thenStarts"),
                AfterObjectives = Ints(row, "afterObjectives"),
                Replies = Ints(row, "replies"),
                ShowsNpc = Int(row, "showsNpc"),
                Says = Int(row, "says"),
                OnlyGives = row.TryGetProperty("onlyGives", out var only) && only.ValueKind == JsonValueKind.True,
                OverridesMap = row.TryGetProperty("overridesMap", out var overrides)
                               && overrides.ValueKind == JsonValueKind.True,
                ItemId = Int(row, "item"),
                SkillId = skill == 0 ? QuestBinding.DefaultSkill : skill,

                // Not `Int(...) == 0 ? default : value`, because -1 IS the default and 0 is a
                // legitimate type: whether the row said anything has to be asked separately.
                TypeId = hasType ? Int(row, "type") : QuestBinding.DefaultType,
                Why = row.TryGetProperty("why", out var why) && why.ValueKind == JsonValueKind.String
                    ? why.GetString() ?? ""
                    : "",
            };
        }

        /// <summary>
        /// The kind a row declares. Click when it says nothing, because it is the commonest and
        /// because the field was added after the first rows were written.
        /// </summary>
        private static QuestBindingKind Kind(JsonElement row)
        {
            if (!row.TryGetProperty("kind", out var value) || value.ValueKind != JsonValueKind.String)
                return QuestBindingKind.Click;

            return (value.GetString() ?? "") switch
            {
                "talk" => QuestBindingKind.Talk,
                "enter" => QuestBindingKind.Enter,
                "beat" => QuestBindingKind.Beat,
                "walk" => QuestBindingKind.Walk,
                "objectives" => QuestBindingKind.Objectives,
                "gather" => QuestBindingKind.Gather,
                "read" => QuestBindingKind.Read,
                "reply" => QuestBindingKind.Reply,
                _ => QuestBindingKind.Click,
            };
        }

        private static IReadOnlyList<int> Ints(JsonElement row, string name)
        {
            if (!row.TryGetProperty(name, out var list) || list.ValueKind != JsonValueKind.Array) return Array.Empty<int>();
            var found = new List<int>();
            foreach (var item in list.EnumerateArray())
            {
                if (item.ValueKind == JsonValueKind.Number && item.TryGetInt32(out int number)) found.Add(number);
            }
            return found;
        }

        private static int Int(JsonElement row, string name)
            => row.TryGetProperty(name, out var value) && value.TryGetInt32(out int number) ? number : 0;

        private static long Long(JsonElement row, string name)
            => row.TryGetProperty(name, out var value) && value.TryGetInt64(out long number) ? number : 0;
    }
}
