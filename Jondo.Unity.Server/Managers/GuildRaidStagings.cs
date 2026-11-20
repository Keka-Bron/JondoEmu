using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Sockets;
using System.Text.Json;
using System.Threading.Tasks;
using Jondo.Unity.Launcher;
using Jondo.Unity.Protocol;
using Jondo.Unity.Server.Network;
using Jondo.Unity.World.Content;

namespace Jondo.Unity.Server.Managers
{
    /// <summary>
    /// The guild raids' map stagings: what their maps hold hidden, shown to the raid's members.
    /// </summary>
    /// <remarks>
    /// A raid map carries pieces at alpha 0 in its data -- the Santuario's pedestal objects, its
    /// paper flowers and their green and red, the colour its Obra takes -- and staging sequences
    /// that show them, each by a uniqueId of that map ("pop_lanterne_p1", "#SD fleur_true").
    /// The client plays one when the server sends hpm { f1 progression %, f2 intensity %, f3 the
    /// uniqueId }, in a fight or out of it: its staging service (ebm) listens for it on the
    /// network with no condition, and every map load registers the map's sequences with it
    /// (MapRenderer.HandleMapMetadataLoaded) and gets it ready (LoadMap). Read in Ghidra.
    ///
    /// A map change resets them, so what a raid has shown on a map is kept here ("standing")
    /// and played again, at its end, to whoever of the raid loads that map. What only happens
    /// once -- the red of a wrong flower, the flash of a lost life -- is played, not kept.
    ///
    /// The sequences of each map are the client's (tools/extract_raid_stagings.py), and so are the
    /// names its elements carry for them: where each named piece stands, and which clickable
    /// element it is when it is one (<see cref="ElementsOf"/>).
    /// </remarks>
    public static class GuildRaidStagings
    {
        /// <summary>Progression of a staging played from its start, and of one shown at its end at once.</summary>
        public const int FromStart = 0;
        public const int AtEnd = 100;

        /// <summary>Full intensity.</summary>
        public const int FullIntensity = 100;

        /// <summary>"Perte pv": the red flash every Santuario map plays when the raid loses a life.</summary>
        public const string LifeLost = "Perte pv";

        private sealed record Document(IReadOnlyDictionary<long, IReadOnlyList<(int Id, string Name)>> Maps,
                                       IReadOnlyDictionary<long, IReadOnlyList<(string Name, int Cell, int Element)>> Elements,
                                       IReadOnlyDictionary<long, IReadOnlyList<(string Name, int Cell)>> Objects,
                                       IReadOnlyDictionary<long, (IReadOnlyList<int> Red, IReadOnlyList<int> Blue)> Placement);

        private static Document? _document;
        private static readonly object _lock = new();

        /// <summary>What each raid has standing on each map, in the order it was shown.</summary>
        private static readonly ConcurrentDictionary<long, ConcurrentDictionary<long, List<int>>> _standing = new();

        private static Document Loaded
        {
            get
            {
                if (_document != null) return _document;
                lock (_lock)
                {
                    _document ??= Load(Paths.RaidStagingsJson);
                    return _document;
                }
            }
        }

        private static IReadOnlyDictionary<long, IReadOnlyList<(int Id, string Name)>> Maps => Loaded.Maps;

        private static Document Load(string path)
        {
            var maps = new Dictionary<long, IReadOnlyList<(int Id, string Name)>>();
            var elements = new Dictionary<long, IReadOnlyList<(string Name, int Cell, int Element)>>();
            var objects = new Dictionary<long, IReadOnlyList<(string Name, int Cell)>>();
            var placement = new Dictionary<long, (IReadOnlyList<int>, IReadOnlyList<int>)>();
            if (!File.Exists(path))
            {
                Console.WriteLine($"[Raids] {Path.GetFileName(path)} is missing: the raids' maps will show none of their hidden pieces.");
                return new Document(maps, elements, objects, placement);
            }
            try
            {
                using var json = JsonDocument.Parse(File.ReadAllText(path));
                foreach (var map in json.RootElement.GetProperty("maps").EnumerateObject())
                {
                    if (!long.TryParse(map.Name, out long mapId)) continue;
                    maps[mapId] = map.Value.EnumerateArray()
                                     .Select(s => (s[0].GetInt32(), s[1].GetString() ?? ""))
                                     .ToList();
                }
                if (json.RootElement.TryGetProperty("elements", out var named))
                {
                    foreach (var map in named.EnumerateObject())
                    {
                        if (!long.TryParse(map.Name, out long mapId)) continue;
                        elements[mapId] = map.Value.EnumerateArray()
                                             .Select(e => (e[0].GetString() ?? "", e[1].GetInt32(), e[2].GetInt32()))
                                             .ToList();
                    }
                }
                if (json.RootElement.TryGetProperty("objects", out var placed))
                {
                    foreach (var map in placed.EnumerateObject())
                    {
                        if (!long.TryParse(map.Name, out long mapId)) continue;
                        objects[mapId] = map.Value.EnumerateArray().Select(o => (o[0].GetString() ?? "", o[1].GetInt32())).ToList();
                    }
                }
                if (json.RootElement.TryGetProperty("placement", out var marked))
                {
                    foreach (var map in marked.EnumerateObject())
                    {
                        if (!long.TryParse(map.Name, out long mapId)) continue;
                        placement[mapId] = (map.Value[0].EnumerateArray().Select(c => c.GetInt32()).ToList(),
                                            map.Value[1].EnumerateArray().Select(c => c.GetInt32()).ToList());
                    }
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[Raids] Could not read {Path.GetFileName(path)}: {ex.Message}");
            }
            return new Document(maps, elements, objects, placement);
        }

        // ─── The data ───────────────────────────────────────────────────────────────────────

        /// <summary>A map's staging sequences.</summary>
        public static IReadOnlyList<(int Id, string Name)> Of(long mapId)
            => Maps.TryGetValue(mapId, out var sequences) ? sequences : Array.Empty<(int, string)>();

        /// <summary>
        /// A map's elements named for its stagings: the name, the cell, and the element's id when it
        /// is a clickable one (0 for a drawing). A name may stand on several cells.
        /// </summary>
        public static IReadOnlyList<(string Name, int Cell, int Element)> ElementsOf(long mapId)
            => Loaded.Elements.TryGetValue(mapId, out var elements) ? elements : Array.Empty<(string, int, int)>();

        /// <summary>
        /// The pedestals' objects a map has drawn for good, and where: the Guardián's arena has the
        /// eight, one beside each of its glyphs (tools/extract_raid_stagings.py).
        /// </summary>
        public static IReadOnlyList<(string Name, int Cell)> ObjectsOf(long mapId)
            => Loaded.Objects.TryGetValue(mapId, out var objects) ? objects : Array.Empty<(string, int)>();

        /// <summary>
        /// The placement a raid map flags for itself, red and blue, or null: the players' side is the
        /// red -- four red cells and one blue on the guardians' arenas, four and Belladona's one on
        /// the chess's, one red in the corridor's, two in each of the Reina's jails.
        /// </summary>
        public static (IReadOnlyList<int> Red, IReadOnlyList<int> Blue)? PlacementOf(long mapId)
            => Loaded.Placement.TryGetValue(mapId, out var marked) ? marked : null;

        /// <summary>What <see cref="IdOf"/> gives for a name the map has no sequence for: 0 is a uniqueId ("Light_0").</summary>
        public const int None = -1;

        /// <summary>The uniqueId a map gives a sequence by its name, or <see cref="None"/> when the map has none so called.</summary>
        public static int IdOf(long mapId, string name)
        {
            foreach (var (id, sequence) in Of(mapId))
                if (sequence == name) return id;
            return None;
        }

        /// <summary>The map of a list at a world position, or 0: where a guide names an enigma's map by its position.</summary>
        public static long MapAt(IEnumerable<long> maps, int x, int y)
        {
            using var connection = new Microsoft.Data.Sqlite.SqliteConnection(DatabaseManager.WorldConnectionString);
            connection.Open();
            foreach (long map in maps)
            {
                using var command = connection.CreateCommand();
                command.CommandText = "SELECT PosX, PosY FROM MapPositions WHERE MapId = $m;";
                command.Parameters.AddWithValue("$m", map);
                using var reader = command.ExecuteReader();
                if (reader.Read() && reader.GetInt32(0) == x && reader.GetInt32(1) == y) return map;
            }
            return 0;
        }

        /// <summary>Whether a map has a sequence so called.</summary>
        public static bool Has(long mapId, string name) => IdOf(mapId, name) != None;

        // ─── What stands ────────────────────────────────────────────────────────────────────

        /// <summary>What a raid has standing on a map, in the order it was shown.</summary>
        public static IReadOnlyList<int> Standing(RaidInstance raid, long mapId)
        {
            if (raid == null || !_standing.TryGetValue(raid.Id, out var maps) || !maps.TryGetValue(mapId, out var ids))
                return Array.Empty<int>();
            lock (ids) return ids.ToList();
        }

        /// <summary>A staging kept standing on a map for a raid: shown again to whoever loads the map.</summary>
        public static void Keep(RaidInstance raid, long mapId, int id)
        {
            if (raid == null || id == None) return;
            var ids = _standing.GetOrAdd(raid.Id, _ => new()).GetOrAdd(mapId, _ => new List<int>());
            lock (ids)
            {
                ids.Remove(id);
                ids.Add(id);
            }
        }

        /// <summary>Stagings that no longer stand on a map for a raid.</summary>
        public static void Drop(RaidInstance raid, long mapId, params int[] ids)
        {
            if (raid == null || !_standing.TryGetValue(raid.Id, out var maps) || !maps.TryGetValue(mapId, out var kept)) return;
            lock (kept) kept.RemoveAll(ids.Contains);
        }

        /// <summary>A raid over: nothing of it stands any more.</summary>
        public static void Forget(RaidInstance raid)
        {
            if (raid != null) _standing.TryRemove(raid.Id, out _);
        }

        // ─── Showing them ───────────────────────────────────────────────────────────────────

        /// <summary>hpm: { f1 progression %, f2 intensity %, f3 the sequence's uniqueId }.</summary>
        public static byte[] BuildPlay(int id, int progression)
            => Pb.New().VarIfNotZero(1, progression).Var(2, FullIntensity).Var(3, id).Build();

        internal static Jondo.Unity.Protocol.Game.StagingEvent StandingEvent(int id) => new(AtEnd, FullIntensity, id);

        /// <summary>
        /// A staging played from its start to the raid's members on a map, and kept standing
        /// there when it is to be seen again. Nothing when the map has no sequence so called.
        /// </summary>
        public static async Task PlayAsync(RaidInstance raid, long mapId, string name, bool keep)
        {
            int id = IdOf(mapId, name);
            if (raid == null || id == None) return;
            if (keep) Keep(raid, mapId, id);
            await TellRaidOnMapAsync(raid, mapId, ConnectionProtocol.Push(Op.Hpm, BuildPlay(id, FromStart)));
        }

        /// <summary>
        /// A map loaded by a raid member: what his raid has standing on it, at its end. The client
        /// keeps the hpm that arrive while it is still loading and plays them once it is ready.
        /// </summary>
        public static async Task OnMapLoadedAsync(GameSession session)
        {
            long mapId = session.MapId;
            var raid = GuildRaidManager.RaidOf(session.CharacterId);
            foreach (int id in Standing(raid, mapId))
                await session.SendAsync(StandingEvent(id));

            // And on the Sima's floors, their light.
            if (raid != null) await GuildRaidLights.OnMapLoadedAsync(session, raid, mapId);
        }

        /// <summary>The raid's red flash for a life lost, on whichever of its maps its members stand.</summary>
        public static async Task LifeLostAsync(RaidInstance raid)
        {
            if (raid == null) return;
            foreach (long member in raid.Members.ToList())
            {
                var session = SessionRegistry.FindByCharacter(member);
                if (session == null || session.State.IsInFight) continue;
                int id = IdOf(session.State.MapId, LifeLost);
                if (id != None) await session.SendAsync(ConnectionProtocol.Push(Op.Hpm, BuildPlay(id, FromStart)));
            }
        }

        /// <summary>
        /// An element of an enigma offered again, or no more, to the raid's members on its map: its
        /// declaration sent again with its skill usable or not (iwm).
        /// </summary>
        public static async Task RedeclareAsync(RaidInstance raid, long mapId, int elementId, InteractiveActionKind kind, bool usable)
        {
            var interactive = InteractiveRegistry.OnMap(mapId).FirstOrDefault(i => i.Element.Id == elementId);
            var action = interactive?.Actions.FirstOrDefault(a => a.Kind == kind);
            if (action == null) return;
            await TellRaidOnMapAsync(raid, mapId, ConnectionProtocol.Push(Op.Iwm,
                ConnectionProtocol.BuildElementRedeclared(action.SkillInstanceId, action.SkillId, elementId,
                                                          interactive.Type, usable, action.SkillNameId)));
        }

        /// <summary>A frame to the raid's members standing on a map, out of a fight.</summary>
        public static async Task TellRaidOnMapAsync(RaidInstance raid, long mapId, byte[] frame)
        {
            foreach (long member in raid.Members.ToList())
            {
                var session = SessionRegistry.FindByCharacter(member);
                if (session != null && session.State.MapId == mapId && !session.State.IsInFight) await session.SendAsync(frame);
            }
        }
    }
}
