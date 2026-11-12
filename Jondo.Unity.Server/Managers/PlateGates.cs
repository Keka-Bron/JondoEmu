using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Sockets;
using System.Text.Json;
using System.Threading.Tasks;
using Jondo.Unity.Launcher;
using Jondo.Unity.Protocol;
using Jondo.Unity.Server.Network;

namespace Jondo.Unity.Server.Managers
{
    /// <summary>
    /// Gates that open while characters stand on every one of their plates, and shut again a
    /// while later (content/maps/gates.json).
    /// </summary>
    /// <remarks>
    /// The wabbits' tic-tac-toe room under the Cawotte island, «Viaje al centro del wabbit»: «il
    /// vous faut être 3 minimum pour ouvrir la grille et ces 3 personnes doivent en être au même
    /// point ou plus loin que vous dans ces quêtes. Placez les personnages sur les dalles marquées
    /// d'un point violet», then «si vous ne lui parlez pas avant que la grille se referme, lorsque
    /// la grille se refermera votre personnage sera remis à l'extérieur de la grille» (Dofus pour
    /// les noobs).
    ///
    /// A shut gate is the map as the client draws it: its cells are not walkable in the client's
    /// own map data. Opening it is the jqx the client keeps for map obstacles, a list of
    /// jrg { f1: state, f2: cell } -- read in the client (enq, the map's cell service): each
    /// entry sets its cell's «mov» to (state == 0), so 0 opens a cell and 1 shuts it. The jss
    /// carries the same list in its f3 for whoever comes onto the map while it stands open.
    ///
    /// The gate's own elements are drawn only with a placement (f15), as the abyssal base's
    /// doors: INFERRED, 1 for open, the absence of a state for shut. How long it stays open is
    /// not measured: INFERRED, thirty seconds, as long as an abyssal door.
    /// </remarks>
    public static class PlateGates
    {
        public const string AuthoredFile = "maps/gates.json";

        /// <summary>An open gate's elements (INFERRED); 0 is the element as its map draws it.</summary>
        public const int Open = 1;

        /// <summary>A jrg's state: the cell walkable, or not.</summary>
        private const int Walkable = 0;
        private const int Blocked = 1;

        public sealed class Gate
        {
            public long MapId { get; init; }

            /// <summary>The gate's own elements, drawn open or shut.</summary>
            public int[] Elements { get; init; } = Array.Empty<int>();

            /// <summary>The cells the gate stands on: walkable only while it is open.</summary>
            public int[] Obstacles { get; init; } = Array.Empty<int>();

            /// <summary>The plates, each wanting a character on it at once.</summary>
            public int[] Plates { get; init; } = Array.Empty<int>();

            /// <summary>What each character on a plate has to meet to count, or empty for anybody.</summary>
            public string Criterion { get; init; } = "";

            /// <summary>The cells behind the gate, its own included.</summary>
            public int[] Inside { get; init; } = Array.Empty<int>();

            /// <summary>Where whoever is caught behind it when it shuts is put back.</summary>
            public int Outside { get; init; }

            /// <summary>Who may stay behind it when it shuts: whoever meets this; empty for nobody.</summary>
            public string Stays { get; init; } = "";

            public TimeSpan OpenFor { get; init; } = TimeSpan.FromSeconds(30);
        }

        private static Dictionary<long, Gate>? _gates;
        private static readonly object _lock = new();
        private static readonly Dictionary<long, DateTime> _openUntil = new();

        /// <summary>For a content reload: read the file again.</summary>
        public static void Forget()
        {
            lock (_lock) _gates = null;
        }

        public static Gate? On(long mapId) => Load().TryGetValue(mapId, out var gate) ? gate : null;

        public static bool IsOpen(long mapId)
        {
            lock (_lock) return _openUntil.TryGetValue(mapId, out var until) && until > DateTime.UtcNow;
        }

        /// <summary>Whether that element is a gate's, drawn open or shut.</summary>
        public static bool IsGateElement(long mapId, int elementId)
            => On(mapId) is { } gate && gate.Elements.Contains(elementId);

        public static int StateOf(long mapId) => IsOpen(mapId) ? Open : 0;

        /// <summary>
        /// The gate as it stands, for whoever comes onto its map: its cells walkable in the jss's
        /// f3 while it is open, and its elements' placements -- without them they are not drawn.
        /// </summary>
        public static void AddToMap(Pb jss, long mapId)
        {
            if (On(mapId) is not { } gate || !IsOpen(mapId)) return;
            foreach (int cell in gate.Obstacles) jss.Msg(3, Obstacle(cell, Walkable));
        }

        /// <summary>
        /// A character has stopped on a cell: on the last free plate, with everybody on the plates
        /// meeting the gate's criterion, the gate opens.
        /// </summary>
        public static async Task SteppedAsync(long mapId, int cell)
        {
            if (On(mapId) is not { } gate || !gate.Plates.Contains(cell) || IsOpen(mapId)) return;

            var standing = new Dictionary<int, string>();
            foreach (var session in SessionRegistry.OnMap(mapId))
            {
                int at = session.State.CellId;
                if (!gate.Plates.Contains(at) || standing.ContainsKey(at)) continue;
                if (session.State.IsInFight) continue;
                bool counts;
                using (SessionContext.Push(session))
                    counts = gate.Criterion.Length == 0 || PlayerCriteria.Met(gate.Criterion, mapId);
                if (counts) standing[at] = session.State.CharacterName;
            }

            if (standing.Count < gate.Plates.Length) return;

            lock (_lock)
            {
                if (_openUntil.TryGetValue(mapId, out var until) && until > DateTime.UtcNow) return;
                _openUntil[mapId] = DateTime.UtcNow + gate.OpenFor;
            }
            Console.WriteLine($"[PlateGates] The gate of {mapId} opens: {string.Join(", ", standing.Values)} on its plates.");
            await ShowAsync(gate, open: true);
            _ = ShutLaterAsync(gate);
        }

        private static async Task ShutLaterAsync(Gate gate)
        {
            try
            {
                await Task.Delay(gate.OpenFor + TimeSpan.FromMilliseconds(200));
                if (IsOpen(gate.MapId)) return;
                await ShowAsync(gate, open: false);
                Console.WriteLine($"[PlateGates] The gate of {gate.MapId} shuts.");

                foreach (var session in SessionRegistry.OnMap(gate.MapId))
                {
                    if (session.Stream == null || session.State.IsInFight) continue;
                    if (!gate.Inside.Contains(session.State.CellId)) continue;
                    using var turn = await session.EnterTurnAsync();
                    using (SessionContext.Push(session))
                    {
                        if (session.State.MapId != gate.MapId || !gate.Inside.Contains(session.State.CellId)) continue;
                        if (gate.Stays.Length > 0 && PlayerCriteria.Met(gate.Stays, gate.MapId)) continue;
                        Console.WriteLine($"[PlateGates] {session.State.CharacterName} is put back outside the gate of {gate.MapId}.");
                        await Handlers.TeleportHandler.ToMapAsync(session.Stream, gate.MapId, gate.Outside);
                    }
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[PlateGates] The gate of {gate.MapId}: {ex.Message}");
            }
        }

        private static async Task ShowAsync(Gate gate, bool open)
        {
            var list = Pb.New();
            foreach (int cell in gate.Obstacles) list.Msg(1, Obstacle(cell, open ? Walkable : Blocked));
            await SessionRegistry.BroadcastToMapAsync(gate.MapId, ConnectionProtocol.Push(Op.Jqx, list.Build()));

            foreach (int elementId in gate.Elements)
            {
                var element = Interactives.ByElementId(gate.MapId, elementId);
                if (element.Id == 0) continue;
                await SessionRegistry.BroadcastToMapAsync(gate.MapId, ConnectionProtocol.Push(Op.Iwf,
                    ConnectionProtocol.BuildElementState(element.Cell, element.Id, open ? Open : 0)));
            }
        }

        /// <summary>jrg { f1: state, f2: cell }; a zero state is left out, as proto3 writes it.</summary>
        private static Pb Obstacle(int cell, int state) => Pb.New().VarIfNotZero(1, state).Var(2, cell);

        private static Dictionary<long, Gate> Load()
        {
            lock (_lock)
            {
                if (_gates != null) return _gates;
                var gates = new Dictionary<long, Gate>();
                try
                {
                    string path = Paths.ContentFile(AuthoredFile);
                    if (File.Exists(path))
                    {
                        using var doc = JsonDocument.Parse(File.ReadAllText(path));
                        foreach (var entry in doc.RootElement.GetProperty("gates").EnumerateArray())
                        {
                            var gate = new Gate
                            {
                                MapId = entry.GetProperty("map").GetInt64(),
                                Elements = Ints(entry, "elements"),
                                Obstacles = Ints(entry, "obstacles"),
                                Plates = Ints(entry, "plates"),
                                Criterion = Text(entry, "criterion"),
                                Inside = Ints(entry, "inside"),
                                Outside = entry.TryGetProperty("outside", out var o) ? o.GetInt32() : 0,
                                Stays = Text(entry, "stays"),
                                OpenFor = TimeSpan.FromSeconds(entry.TryGetProperty("openSeconds", out var s) ? s.GetInt32() : 30),
                            };
                            if (gate.MapId > 0 && gate.Plates.Length > 0 && gate.Obstacles.Length > 0) gates[gate.MapId] = gate;
                        }
                    }
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"[Content] {AuthoredFile} is unreadable: {ex.Message}");
                }
                _gates = gates;
                return gates;
            }
        }

        private static int[] Ints(JsonElement entry, string name)
            => entry.TryGetProperty(name, out var list) && list.ValueKind == JsonValueKind.Array
                ? list.EnumerateArray().Select(x => x.GetInt32()).ToArray()
                : Array.Empty<int>();

        private static string Text(JsonElement entry, string name)
            => entry.TryGetProperty(name, out var t) && t.ValueKind == JsonValueKind.String ? t.GetString() ?? "" : "";
    }
}
