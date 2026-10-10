using System;
using System.Collections.Generic;
using System.Linq;

namespace Jondo.Unity.World.Content
{
    /// <summary>
    /// A guild raid in progress: who is inside, until when, and the counters its own content
    /// reads.
    ///
    /// The counters are the whole design, and they are not ours: the client's data asks for them
    /// by name. Every monster of the Gigalodón carries
    /// <c>(PB=1131&amp;RV!7,n1_worldlight,0)|…</c> in its aggressiveImmunityCriterion, the raid
    /// chest changes its look by <c>RV&lt;7,Raid_Score,5000</c> and the boss rewards are gated on
    /// <c>RV&gt;7,Raid_Score,9999</c>. So a raid is an instance with named numbers in it, and the
    /// content that is already in the database comes alive when they are answered.
    /// </summary>
    public sealed class RaidInstance
    {
        public RaidInstance(long id, int raidId, long guildId, long captainId, DateTimeOffset startedUtc,
                            TimeSpan runsFor)
        {
            Id = id;
            RaidId = raidId;
            GuildId = guildId;
            CaptainId = captainId;
            StartedUtc = startedUtc;
            EndsUtc = startedUtc + runsFor;
        }

        public long Id { get; }
        public int RaidId { get; }
        public long GuildId { get; }

        /// <summary>
        /// The uuid of the raid on the guild's board this instance plays, which is also what the
        /// client calls the running raid by. Empty for an instance set up by hand.
        /// </summary>
        public string Uuid { get; init; } = "";

        /// <summary>
        /// Who leads it. He can close it early, and he does not have to be inside; he can hand it
        /// on while it runs.
        /// </summary>
        public long CaptainId { get; set; }

        public DateTimeOffset StartedUtc { get; private set; }
        public DateTimeOffset EndsUtc { get; private set; }

        /// <summary>Why it finished, once it has.</summary>
        public enum Ending { Running, TimeUp, Captain, Beaten, OutOfHealth }

        /// <summary>
        /// The raid's health, for a raid that has it (the Santuario): what its panel draws as hearts.
        /// Null for one that has none.
        /// </summary>
        public int? Health { get; private set; }

        /// <summary>Gives the raid its health, full.</summary>
        public void SetHealth(int health) => Health = health;

        /// <summary>Takes health off the raid (or gives it back, never above its maximum); returns what is left.</summary>
        public int ChangeHealth(int by, int most)
        {
            if (Health == null) return int.MaxValue;
            Health = Math.Clamp(Health.Value + by, 0, most);
            return Health.Value;
        }

        public Ending Over { get; private set; } = Ending.Running;

        public bool Running => Over == Ending.Running;

        public TimeSpan Left(DateTimeOffset now) => EndsUtc > now ? EndsUtc - now : TimeSpan.Zero;

        /// <summary>The characters inside, in the order they entered.</summary>
        private readonly List<long> _members = new List<long>();
        public IReadOnlyList<long> Members => _members;

        public bool Add(long characterId)
        {
            if (_members.Contains(characterId)) return false;
            _members.Add(characterId);
            return true;
        }

        public bool Remove(long characterId) => _members.Remove(characterId);
        public bool Has(long characterId) => _members.Contains(characterId);

        /// <summary>The fights already counted for its goals: each fight ends once per player.</summary>
        private readonly HashSet<long> _countedFights = new HashSet<long>();

        /// <summary>True the first time a fight is offered, false after.</summary>
        public bool CountFight(long fightId) => _countedFights.Add(fightId);

        // ─── The floors: which are open, and their light ─────────────────────────

        /// <summary>How long a band of light lasts before it fades to the one below (the guides: 2 minutes).</summary>
        public static readonly TimeSpan LightStep = TimeSpan.FromMinutes(2);

        /// <summary>Each lit floor's light: the band it was set to and when. It fades from there.</summary>
        private readonly Dictionary<int, (int Level, DateTimeOffset Since)> _lights = new Dictionary<int, (int, DateTimeOffset)>();

        /// <summary>The floors opened beyond the first, by the goals that open them.</summary>
        private readonly HashSet<int> _openFloors = new HashSet<int>();

        /// <summary>Whether a floor can be gone into: the first always, the others once opened.</summary>
        public bool IsOpen(int floor) => floor <= 1 || _openFloors.Contains(floor);

        /// <summary>Opens a floor. True the first time.</summary>
        public bool Open(int floor) => _openFloors.Add(floor);

        /// <summary>
        /// A floor's light at a moment: the band it was set to, less one for every
        /// <see cref="LightStep"/> since, never below darkness. A floor whose light was only ever
        /// written as a variable keeps it as written.
        /// </summary>
        public int LightAt(int floor, DateTimeOffset now)
        {
            if (!_lights.TryGetValue(floor, out var light)) return (int)Math.Max(0, Get(LightVariable(floor)));
            long faded = now <= light.Since ? 0 : (long)((now - light.Since).Ticks / LightStep.Ticks);
            return (int)Math.Max(0, light.Level - faded);
        }

        /// <summary>Sets a floor's light from now: it starts fading again at this band.</summary>
        public void SetLight(int floor, int level, DateTimeOffset now)
        {
            _lights[floor] = (level, now);
            Set(LightVariable(floor), level);
        }

        /// <summary>A variable's value now: a floor's light is its faded band.</summary>
        private long ValueNow(string name)
        {
            for (int floor = 1; floor <= 9; floor++)
                if (name == LightVariable(floor)) return LightAt(floor, DateTimeOffset.UtcNow);
            return Get(name);
        }

        /// <summary>The key fragments the raid has found, one to four.</summary>
        private readonly HashSet<int> _fragments = new HashSet<int>();
        public IReadOnlyCollection<int> Fragments => _fragments;

        /// <summary>Records a fragment found. True the first time.</summary>
        public bool FoundFragment(int fragment) => _fragments.Add(fragment);

        /// <summary>Who has left it of his own accord: they do not come back in.</summary>
        private readonly HashSet<long> _left = new HashSet<long>();
        public bool HasLeft(long characterId) => _left.Contains(characterId);

        /// <summary>Takes someone out for good: he left the raid.</summary>
        public void Leave(long characterId)
        {
            _members.Remove(characterId);
            _left.Add(characterId);
        }

        // ─── The goals ──────────────────────────────────────────────────────────

        /// <summary>
        /// How far each of the raid's goals has gone, by the goal's id in the client's data. A goal
        /// is met when it reaches the value its data asks for; one not started is not here.
        /// </summary>
        private readonly Dictionary<int, int> _goals = new Dictionary<int, int>();
        public IReadOnlyDictionary<int, int> Goals => _goals;

        public int GoalAt(int goalId) => _goals.TryGetValue(goalId, out int value) ? value : 0;

        public void SetGoal(int goalId, int value) => _goals[goalId] = value;

        /// <summary>
        /// Starts it over, as the captain may in a raid whose data allows it: the same team, the
        /// counters and goals at nothing, and the whole time again from now.
        /// </summary>
        public void Restart(DateTimeOffset now, TimeSpan runsFor)
        {
            if (!Running) return;
            _variables.Clear();
            _sequences.Clear();
            _goals.Clear();
            _lights.Clear();
            _openFloors.Clear();
            _fragments.Clear();
            StartedUtc = now;
            EndsUtc = now + runsFor;
        }

        // ─── The counters ───────────────────────────────────────────────────────

        /// <summary>
        /// The namespace the raid's variables live in. Every RV criterion of the client's data
        /// carries a 7 in front of the name -- <c>RV!7,n1_worldlight,0</c> -- so the number is
        /// part of what the content asks for, and a variable of another namespace is not ours.
        /// </summary>
        public const int Namespace = 7;

        /// <summary>The score, which is what the chest and the rewards read. Its name is the data's.</summary>
        public const string ScoreVariable = "Raid_Score";

        /// <summary>
        /// The Gigalodón's depths salt, a pool the whole raid shares: the monsters' salt goes into
        /// it and the luminomachines burn it (the guides). No content asks for it by name, so the
        /// name is ours.
        /// </summary>
        public const string SaltVariable = "Raid_Salt";

        /// <summary>
        /// The light of a floor, from 0 to 4. One per floor, named n1..n5 in the data -- the
        /// sixth floor has none, and neither does its criterion. At 0 the monsters of that floor
        /// turn aggressive, which is exactly what their criterion says: they are immune to
        /// aggression while the light is NOT 0.
        /// </summary>
        public static string LightVariable(int floor) => "n" + floor + "_worldlight";

        private readonly Dictionary<string, long> _variables = new Dictionary<string, long>(StringComparer.Ordinal);

        public long Get(string name) => _variables.TryGetValue(name ?? "", out long value) ? value : 0;

        public void Set(string name, long value)
        {
            if (string.IsNullOrEmpty(name)) return;
            _variables[name] = value;
        }

        public long Add(string name, long howMuch)
        {
            long value = Get(name) + howMuch;
            Set(name, value);
            return value;
        }

        public IReadOnlyDictionary<string, long> Variables => _variables;

        /// <summary>Ordered values the raid remembers by name: the order a boss showed its forms.</summary>
        private readonly Dictionary<string, List<int>> _sequences = new Dictionary<string, List<int>>(StringComparer.Ordinal);

        public IReadOnlyList<int> SequenceOf(string name)
            => _sequences.TryGetValue(name ?? "", out var values) ? values : (IReadOnlyList<int>)Array.Empty<int>();

        public void SetSequence(string name, IEnumerable<int> values)
        {
            if (string.IsNullOrEmpty(name)) return;
            _sequences[name] = values.ToList();
        }

        public long Score => Get(ScoreVariable);

        /// <summary>Closes the raid. The first ending sticks: a raid does not finish twice.</summary>
        public void Finish(Ending how, DateTimeOffset now)
        {
            if (!Running || how == Ending.Running) return;
            Over = how;
            if (EndsUtc > now) EndsUtc = now;
        }

        /// <summary>
        /// Answers the conditions a raid knows: its own variables, and which subarea the one
        /// asking is standing in. Everything else comes back Unknown, which is what keeps this
        /// from quietly deciding things it has not been taught.
        /// </summary>
        public Criterion.Resolver ResolverFor(int subArea)
            => condition =>
            {
                switch (condition.Code)
                {
                    // PB: the subarea. The raid's own floors are 1131..1136 and 1126..1130.
                    case "PB":
                        return Criterion.Compare(condition.Operator, subArea, condition.Value);

                    // RV: namespace, name, value. A variable of another namespace is not ours.
                    case "RV":
                        if (condition.Args.Count < 3) return Answer.Unknown;
                        if (!int.TryParse(condition.Args[0], out int ns) || ns != Namespace) return Answer.Unknown;
                        return Criterion.Compare(condition.Operator, ValueNow(condition.Args[1]), condition.Value);

                    default:
                        return Answer.Unknown;
                }
            };
    }

    /// <summary>
    /// The two raids, as far as the client's own data and the game's own pages describe them.
    ///
    /// The areas, the floors and their maps are MEASURED: they are in the client's data and every
    /// one of their maps is already in world.db -- 73 maps in six floors for the Gigalodón and 51
    /// in five zones for the Sanctuary, with 209 monster groups already standing on them. The price,
    /// how long a raid runs and how many may go in are the client's data too, and the server reads
    /// them from there (GuildRaidCatalogue); the ids are the client's, which every raid screen uses.
    /// </summary>
    public sealed class RaidKind
    {
        public int Id { get; init; }
        public string Name { get; init; } = "";

        /// <summary>The area of the client's data: 103 the Gigalodón, 102 the Sanctuary.</summary>
        public int Area { get; init; }

        /// <summary>Its floors or zones, in order, by subarea. Measured in the client's data.</summary>
        public IReadOnlyList<int> Floors { get; init; } = Array.Empty<int>();

        /// <summary>Whether its floors carry a light to manage. Only the Gigalodón's do.</summary>
        public bool HasLight { get; init; }

        /// <summary>
        /// Whether its chest stands where the raid comes in: the Gigalodón's, in the outpost (the
        /// guides: treasures are "deposited in the outpost chest", the Gigalodón comes out "from the
        /// chest"). Otherwise it waits at the far end.
        /// </summary>
        public bool ChestAtEntry { get; init; }

        /// <summary>
        /// The raid's health, for one that has it: the Santuario's 20. NOT in the client's data -- its
        /// playerHealth is 0 for both raids -- but in the guides: an enigma's mistake costs one, a
        /// fight lost one per character in it, an enigma solved gives one back, and at none the
        /// raid is over. Zero for a raid without health.
        /// </summary>
        public int Health { get; init; }

        /// <summary>
        /// The three ornaments of this raid's weekly podium, first to third.
        /// </summary>
        /// <remarks>
        /// Measured: six ornaments in the client's catalogue, named "Sima del Gigalodón - #1" to
        /// "#3" and "Santuario de los Jardines Eternos - #1" to "#3", and the game's own page says
        /// "los mejores podrán representar con orgullo a su gremio en la clasificación global".
        /// </remarks>
        public IReadOnlyList<int> Podium { get; init; } = Array.Empty<int>();

        /// <summary>Which floor a subarea is, counting from one; zero when it is not of this raid.</summary>
        public int FloorOf(int subArea)
        {
            for (int i = 0; i < Floors.Count; i++)
            {
                if (Floors[i] == subArea) return i + 1;
            }
            return 0;
        }
    }

    public static class Raids
    {
        /// <summary>The Santuario de los Jardines Eternos, id 1 in the client's data: five zones.</summary>
        public const int EternalGardens = 1;

        /// <summary>The Sima del Gigalodón, id 2 in the client's data: six floors.</summary>
        public const int Gigalodon = 2;

        private static readonly Dictionary<int, RaidKind> Catalogue = new()
        {
            [Gigalodon] = new RaidKind
            {
                Id = Gigalodon,
                Name = "Sima del Gigalodón",
                Area = 103,
                // -1 Puesto avanzado de los exploradores, -2 Meseta de la Morreina,
                // -3 Acantilado sumergido, -4 Madriguera de Cangrancio, -5 Osario abisal,
                // -6 Fosombrío de Willorca. All six, with their 73 maps, are in world.db.
                Floors = new[] { 1131, 1132, 1133, 1134, 1135, 1136 },
                HasLight = true,
                ChestAtEntry = true,
                Podium = new[] { 184, 185, 186 },
            },
            [EternalGardens] = new RaidKind
            {
                Id = EternalGardens,
                Name = "Santuario de los Jardines Eternos",
                Area = 102,
                // Obra monocromática, Enclave de los protectores, Reserva de Belladona,
                // Patio de Efedra and the Castillo del santuario. 51 maps, all in world.db.
                Floors = new[] { 1126, 1127, 1128, 1129, 1130 },
                HasLight = false,
                Health = 20,
                Podium = new[] { 181, 182, 183 },
            },
        };

        public static IReadOnlyList<RaidKind> All => Catalogue.Values.ToList();

        public static RaidKind Of(int id) => Catalogue.TryGetValue(id, out var raid) ? raid : null;

        /// <summary>The raid a subarea belongs to, or null when it is not a raid's.</summary>
        public static RaidKind BySubArea(int subArea)
        {
            foreach (var raid in Catalogue.Values)
            {
                if (raid.FloorOf(subArea) > 0) return raid;
            }
            return null;
        }
    }
}
