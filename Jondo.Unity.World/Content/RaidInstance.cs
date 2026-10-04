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

        /// <summary>Who launched it. He can close it early, and he does not have to be inside.</summary>
        public long CaptainId { get; }

        public DateTimeOffset StartedUtc { get; }
        public DateTimeOffset EndsUtc { get; private set; }

        /// <summary>Why it finished, once it has.</summary>
        public enum Ending { Running, TimeUp, Captain, Beaten }

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

        // ─── Los contadores ─────────────────────────────────────────────────────

        /// <summary>
        /// The namespace the raid's variables live in. Every RV criterion of the client's data
        /// carries a 7 in front of the name -- <c>RV!7,n1_worldlight,0</c> -- so the number is
        /// part of what the content asks for, and a variable of another namespace is not ours.
        /// </summary>
        public const int Namespace = 7;

        /// <summary>The score, which is what the chest and the rewards read. Its name is the data's.</summary>
        public const string ScoreVariable = "Raid_Score";

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
                        return Criterion.Compare(condition.Operator, Get(condition.Args[1]), condition.Value);

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
    /// in five zones for the Sanctuary, with 209 monster groups already standing on them. What is
    /// NOT measured, and is written here from the game's own published pages, is the price, how
    /// long a raid runs and how many may go in; no capture carries a raid.
    /// </summary>
    public sealed class RaidKind
    {
        public int Id { get; init; }
        public string Name { get; init; } = "";

        /// <summary>The area of the client's data: 103 the Gigalodón, 102 the Sanctuary.</summary>
        public int Area { get; init; }

        /// <summary>Its floors or zones, in order, by subarea. Measured in the client's data.</summary>
        public IReadOnlyList<int> Floors { get; init; } = Array.Empty<int>();

        /// <summary>Guild kamas it costs. From the game's pages, not from a capture.</summary>
        public int Price { get; init; }

        public TimeSpan RunsFor { get; init; }
        public int MinPlayers { get; init; }
        public int MaxPlayers { get; init; }

        /// <summary>Whether its floors carry a light to manage. Only the Gigalodón's do.</summary>
        public bool HasLight { get; init; }

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
        /// <summary>La Sima del Gigalodón: seis plantas, una hora, de ocho a doce.</summary>
        public const int Gigalodon = 1;

        /// <summary>El Santuario de los Jardines Eternos: cinco zonas, dos horas, de ocho a dieciséis.</summary>
        public const int EternalGardens = 2;

        private static readonly Dictionary<int, RaidKind> Catalogue = new()
        {
            [Gigalodon] = new RaidKind
            {
                Id = Gigalodon,
                Name = "Sima del Gigalodón",
                Area = 103,
                // -1 Puesto avanzado de los exploradores, -2 Meseta de la Morreina,
                // -3 Acantilado sumergido, -4 Madriguera de Cangrancio, -5 Osario abisal,
                // -6 Fosombrío de Willorca. Los seis, con sus 73 mapas, están en world.db.
                Floors = new[] { 1131, 1132, 1133, 1134, 1135, 1136 },
                Price = 360,
                RunsFor = TimeSpan.FromHours(1),
                MinPlayers = 8,
                MaxPlayers = 12,
                HasLight = true,
                Podium = new[] { 184, 185, 186 },
            },
            [EternalGardens] = new RaidKind
            {
                Id = EternalGardens,
                Name = "Santuario de los Jardines Eternos",
                Area = 102,
                // Obra monocromática, Enclave de los protectores, Reserva de Belladona,
                // Patio de Efedra y el Castillo del santuario. 51 mapas, todos en world.db.
                Floors = new[] { 1126, 1127, 1128, 1129, 1130 },
                Price = 480,
                RunsFor = TimeSpan.FromHours(2),
                MinPlayers = 8,
                MaxPlayers = 16,
                HasLight = false,
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
