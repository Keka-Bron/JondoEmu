using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using Jondo.Unity.Launcher;

namespace Jondo.Unity.Server.Managers
{
    /// <summary>
    /// The temporal anomalies' places: the entry a travel to an anomaly lands on, the room where its
    /// guardian waits with his group, and the arena they fight on. The data and what of it is
    /// measured are content/anomalies/places.json.
    /// </summary>
    /// <remarks>
    /// Which place an anomaly opens on is drawn with each of its cycles (<see cref="Anomalies.MinutesLeft"/>
    /// counts them down): the captures took the same anomaly to the same place twice within one
    /// cycle, and five anomalies to five places with nothing of their zones in common.
    /// </remarks>
    public static class AnomalyPlaces
    {
        public const string AuthoredFile = "anomalies/places.json";

        public sealed record Group(int Cell, int Orientation, IReadOnlyList<int> Members);

        public sealed record Place(string Name, int Guardian, long Entry, int Arrival, int Exit,
                                   long Room, long Arena, Group? Group, bool Measured);

        private sealed record Document(IReadOnlyList<Place> Places, int VestigeStaging,
                                       int RightClock, int WrongClock, int WrongBy,
                                       IReadOnlyList<IReadOnlyList<int>> Families);

        private static Document? _document;
        private static readonly object _lock = new();

        public static IReadOnlyList<Place> All => Load().Places;

        /// <summary>The hpm an anomaly's zaap map plays at its end while the anomaly is open.</summary>
        public static int VestigeStaging => Load().VestigeStaging;

        /// <summary>The clocks' gfx: the one that tells the hour and the one that does not.</summary>
        public static int RightClock => Load().RightClock;
        public static int WrongClock => Load().WrongClock;

        /// <summary>How many hours the wrong clock is ahead.</summary>
        public static int WrongBy => Load().WrongBy;

        /// <summary>The place an anomaly opens on in its present cycle, or null when there is none.</summary>
        public static Place? Of(int subAreaId) => Of(subAreaId, DateTimeOffset.UtcNow);

        public static Place? Of(int subAreaId, DateTimeOffset now)
        {
            var places = All;
            if (places.Count == 0) return null;
            long cycle = Cycle(subAreaId, now);
            uint mixed = unchecked((uint)(subAreaId * 73856093) ^ (uint)(cycle * 19349663));
            return places[(int)(mixed % (uint)places.Count)];
        }

        /// <summary>Which of its cycles an anomaly is in: the count <see cref="Anomalies.MinutesLeft"/> goes down within.</summary>
        public static long Cycle(int subAreaId, DateTimeOffset now)
        {
            int duration = Anomalies.Duration;
            if (duration <= 0) return 0;
            long minutes = now.ToUnixTimeSeconds() / 60;
            return (minutes + Math.Abs(subAreaId) % duration) / duration;
        }

        public static Place? ByEntry(long mapId) => All.FirstOrDefault(p => p.Entry == mapId);
        public static Place? ByRoom(long mapId) => All.FirstOrDefault(p => p.Room == mapId);

        /// <summary>Whether a map is one of the places' entries or rooms.</summary>
        public static bool IsPlaceMap(long mapId) => All.Any(p => p.Entry == mapId || p.Room == mapId);

        /// <summary>The arena a room's fights are held on, or zero.</summary>
        public static long ArenaOf(long roomMapId) => ByRoom(roomMapId)?.Arena ?? 0;

        /// <summary>
        /// The most people a guardian's fight takes: «Elles peuvent être réalisées avec quatre
        /// personnages maximum» (Dofus pour les noobs, «Les Anomalies Temporelles»).
        /// </summary>
        public const int PeopleCap = 4;

        /// <summary>Julith, whose mechanics bring a fourth chronomorph: two of one family.</summary>
        public const int Julith = 5663;

        /// <summary>Whether a monster is a chronomorph: a brutoforme, a cublaforme or a kaxaforme.</summary>
        public static bool IsChronomorph(int monsterId) => Load().Families.Any(f => f.Contains(monsterId));

        /// <summary>
        /// A guardian's group: the guardian and his companions, the measured ones or else one of each
        /// family -- a brutoforme, a cublaforme and a kaxaforme -- drawn, and for Julith one more of
        /// any: «Julith étant accompagné de 4 chronomorphes à cause de sa mécanique, il y aura 2
        /// chronomorphes d'un même type dans le combat» (Dofus pour les noobs).
        /// </summary>
        public static List<int> GroupOf(Place place, Random dice)
        {
            var members = new List<int> { place.Guardian };
            if (place.Group != null && place.Group.Members.Count > 0)
            {
                members.AddRange(place.Group.Members);
                return members;
            }
            var families = Load().Families.Where(f => f.Count > 0).ToList();
            foreach (var family in families) members.Add(family[dice.Next(family.Count)]);
            if (place.Guardian == Julith && families.Count > 0)
            {
                var again = families[dice.Next(families.Count)];
                members.Add(again[dice.Next(again.Count)]);
            }
            return members;
        }

        /// <summary>
        /// The dice a place's group is drawn with: the same for the whole cycle of the anomaly that
        /// opens on it -- «Le chronomorphe est choisi aléatoirement quand l'anomalie s'ouvre et la
        /// composition du groupe reste la même pour toute la durée de l'anomalie» (Dofus pour les
        /// noobs) --, so that every fight with its guardian meets the same three.
        /// </summary>
        public static int GroupSeed(Place place, DateTimeOffset now)
        {
            var opening = Anomalies.All.Where(a => Of(a.SubAreaId, now) == place).OrderBy(a => a.SubAreaId).ToList();
            int subArea = opening.Count > 0 ? opening[0].SubAreaId : 0;
            long cycle = opening.Count > 0 ? Cycle(subArea, now)
                       : Anomalies.Duration > 0 ? now.ToUnixTimeSeconds() / 60 / Anomalies.Duration : 0;
            return unchecked((int)(place.Room * 2654435761L ^ cycle * 40503L ^ subArea * 97L));
        }

        private static Document Load()
        {
            if (_document != null) return _document;
            lock (_lock)
            {
                if (_document != null) return _document;
                var places = new List<Place>();
                int staging = 0, right = 0, wrong = 0, by = 0;
                var families = new List<IReadOnlyList<int>>();
                try
                {
                    using var doc = JsonDocument.Parse(File.ReadAllText(Paths.ContentFile(AuthoredFile)));
                    var root = doc.RootElement;
                    staging = Number(root, "vestigeStaging");
                    if (root.TryGetProperty("clocks", out var clocks))
                    {
                        right = Number(clocks, "right");
                        wrong = Number(clocks, "wrong");
                        by = Number(clocks, "wrongBy");
                    }
                    if (root.TryGetProperty("families", out var kinds))
                        foreach (var family in kinds.EnumerateObject())
                            families.Add(family.Value.EnumerateArray().Select(v => v.GetInt32()).ToList());
                    foreach (var row in root.GetProperty("places").EnumerateArray())
                    {
                        Group? group = null;
                        if (row.TryGetProperty("group", out var g))
                            group = new Group(Number(g, "cell"), Number(g, "orientation"),
                                g.TryGetProperty("members", out var m) ? m.EnumerateArray().Select(v => v.GetInt32()).ToList() : new List<int>());
                        places.Add(new Place(
                            row.TryGetProperty("name", out var name) ? name.GetString() ?? "" : "",
                            Number(row, "guardian"),
                            Long(row, "entry"),
                            Number(row, "arrival"),
                            Number(row, "exit"),
                            Long(row, "room"),
                            Long(row, "arena"),
                            group,
                            row.TryGetProperty("measured", out var measured) && measured.ValueKind == JsonValueKind.True));
                    }
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"[Anomalies] Could not read {AuthoredFile}: {ex.Message}");
                }
                _document = new Document(places, staging, right, wrong, by, families);
                return _document;
            }
        }

        private static int Number(JsonElement row, string name)
            => row.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number ? value.GetInt32() : 0;

        private static long Long(JsonElement row, string name)
            => row.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number ? value.GetInt64() : 0;
    }
}
