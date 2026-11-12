using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Sockets;
using System.Threading.Tasks;
using Jondo.Unity.Protocol;
using Jondo.Unity.Server.Network;
using Jondo.Unity.World.Maps;

namespace Jondo.Unity.Server.Managers
{
    /// <summary>
    /// The abyssal base under Sufokia (subareas 804 to 806): its doors, which open when the lights
    /// beside them are lit, and the levers that light them for a Foggernaut battery each.
    /// </summary>
    /// <remarks>
    /// The client's maps have three graphics for it, and nothing in its data ties one to another:
    ///
    ///   2422  a lever -- "Utilizar (palanca)", the client's type 127 and skill 244
    ///   2423  a light -- on the door's own cell or beside it, one per lever it waits for
    ///   2425  a door  -- on the cell of the passage (graphic 42154) the 2.73 world graph goes through
    ///
    /// The base has 19 levers and 19 lights. Most rooms hold a door with as many levers as lights,
    /// and those are paired on the map, nearest first. Six lights have no lever in their room and
    /// six levers stand in rooms without a door: "ouvrir une porte à partir de leviers présents dans
    /// d'autres salles" (Dofus pour les noobs, "Chemin vers le donjon Aquadôme de Merkator"). Those
    /// go to the nearest room that wants one, and on a map with two, the lower lever goes to the
    /// nearer room -- which gives the guide's own walk: the lever under the balcony of the Undertow
    /// Rooms opens the next room's door with the one of the globe room, and "le levier le plus en
    /// bas" of the Abyss Rooms' upper room opens the pool room's door with the one beside it.
    ///
    /// Each lever spends a battery ("Vous perdez 1 x Pile Steamer", every door of the guide; "Une
    /// pile est nécessaire pour utiliser ce levier.", the client's text 332690), and the guide's
    /// count holds: twelve batteries from the main lift to the Aquadome's entrance, the twelve
    /// Gustavo hands over in «Prestigio y contaminación».
    ///
    /// How long a light stays lit and a door open is not measured. The guide's two-lever doors are
    /// worked "en simultané" by two characters in two rooms, and whoever pulled the far lever has
    /// to "se dépêcher de revenir avant que la porte ne se referme": INFERRED, twenty seconds for a
    /// light and thirty for a door. Neither is the lit or open state measured: INFERRED, 1 for both.
    /// The state is the map's and not each viewer's, as anybody passes a door somebody opened.
    /// </remarks>
    public static class AbyssalBase
    {
        /// <summary>Splash Zone, Undertow Rooms, Abyss Rooms.</summary>
        public static readonly IReadOnlyList<int> SubAreas = new[] { 804, 805, 806 };

        public const int LeverGfx = 2422;
        public const int LightGfx = 2423;
        public const int DoorGfx = 2425;

        /// <summary>The client's type 127 "Palanca" and skill 244 "Utilizar (palanca)".</summary>
        public const int LeverType = 127;
        public const int LeverSkill = 244;

        /// <summary>«Pila steamer», what each lever spends.</summary>
        public const int Battery = 14795;

        public static readonly TimeSpan LightTime = TimeSpan.FromSeconds(20);
        public static readonly TimeSpan OpenTime = TimeSpan.FromSeconds(30);

        /// <summary>A light lit, a door open (INFERRED); 0 is the element as its map draws it.</summary>
        public const int Lit = 1;
        public const int Open = 1;

        public sealed class Door
        {
            public long MapId { get; init; }
            public Interactives.Element Element { get; init; }

            /// <summary>The passage on the door's cell, or 0 when the server has none.</summary>
            public int Passage { get; init; }
            public List<Light> Lights { get; } = new();
        }

        public sealed class Light
        {
            public long MapId { get; init; }
            public Interactives.Element Element { get; init; }
            public Door Door { get; init; } = null!;
        }

        public sealed class Lever
        {
            public long MapId { get; init; }
            public Interactives.Element Element { get; init; }
            public Light? Light { get; set; }
        }

        private sealed class Tables
        {
            public readonly Dictionary<(long Map, int Element), Lever> Levers = new();
            public readonly Dictionary<(long Map, int Element), Light> Lights = new();
            public readonly Dictionary<(long Map, int Element), Door> Doors = new();
            public readonly Dictionary<(long Map, int Passage), Door> ByPassage = new();
        }

        private static volatile Tables? _tables;
        private static readonly object _build = new();

        private static readonly object _state = new();
        private static readonly Dictionary<Light, DateTime> _litUntil = new();
        private static readonly Dictionary<Door, DateTime> _openUntil = new();

        private static Tables Current
        {
            get
            {
                var tables = _tables;
                if (tables != null) return tables;
                lock (_build)
                {
                    return _tables ??= Build();
                }
            }
        }

        public static IEnumerable<Lever> Levers => Current.Levers.Values;

        /// <summary>The pairing read again on the next question: the passages may have changed.</summary>
        public static void Reset()
        {
            lock (_build) _tables = null;
        }

        public static Lever? LeverOf(long mapId, int elementId)
            => Current.Levers.TryGetValue((mapId, elementId), out var lever) ? lever : null;

        public static Door? DoorOf(long mapId, int elementId)
            => Current.Doors.TryGetValue((mapId, elementId), out var door) ? door : null;

        public static Door? DoorOfPassage(long mapId, int passage)
            => Current.ByPassage.TryGetValue((mapId, passage), out var door) ? door : null;

        // ─── The pairing ────────────────────────────────────────────────────────────────────

        private static Tables Build()
        {
            var tables = new Tables();
            var spare = new List<Lever>();
            var waiting = new List<Light>();

            foreach (int subArea in SubAreas)
            foreach (long mapId in DatabaseManager.MapsOfSubArea(subArea))
            {
                var elements = Interactives.ElementsOf(mapId);
                var doors = new List<Door>();
                foreach (var element in elements.Where(e => e.Gfx == DoorGfx))
                {
                    int passage = elements.Where(e => e.Id != element.Id && e.Cell == element.Cell
                                                      && TeleportManager.TryGet(mapId, e.Id, out _))
                                          .Select(e => e.Id).FirstOrDefault();
                    var door = new Door { MapId = mapId, Element = element, Passage = passage };
                    doors.Add(door);
                    tables.Doors[(mapId, element.Id)] = door;
                    if (passage != 0) tables.ByPassage[(mapId, passage)] = door;
                }
                if (doors.Count == 0 && !elements.Any(e => e.Gfx == LeverGfx)) continue;

                var lights = new List<Light>();
                foreach (var element in elements.Where(e => e.Gfx == LightGfx).OrderBy(e => e.Id))
                {
                    var door = doors.OrderBy(d => MapGeometry.Distance(d.Element.Cell, element.Cell))
                                    .ThenBy(d => d.Element.Id).FirstOrDefault();
                    if (door == null) continue;
                    var light = new Light { MapId = mapId, Element = element, Door = door };
                    door.Lights.Add(light);
                    lights.Add(light);
                    tables.Lights[(mapId, element.Id)] = light;
                }

                var levers = elements.Where(e => e.Gfx == LeverGfx)
                                     .Select(e => new Lever { MapId = mapId, Element = e }).ToList();
                foreach (var lever in levers) tables.Levers[(mapId, lever.Element.Id)] = lever;

                // On the map, the nearest pairs first.
                var pairs = levers.SelectMany(lever => lights.Select(light => (lever, light)))
                                  .OrderBy(p => MapGeometry.Distance(p.lever.Element.Cell, p.light.Element.Cell))
                                  .ThenBy(p => p.lever.Element.Id).ThenBy(p => p.light.Element.Id);
                var taken = new HashSet<Light>();
                foreach (var (lever, light) in pairs)
                {
                    if (lever.Light != null || taken.Contains(light)) continue;
                    lever.Light = light;
                    taken.Add(light);
                }
                spare.AddRange(levers.Where(l => l.Light == null));
                waiting.AddRange(lights.Where(l => !taken.Contains(l)));
            }

            // Then from room to room: the nearest rooms first, the lowest lever of its room.
            while (spare.Count > 0 && waiting.Count > 0)
            {
                var best = spare.Select(l => l.MapId).Distinct()
                    .SelectMany(from => waiting.Select(w => w.MapId).Distinct().Select(to => (from, to)))
                    .OrderBy(p => RoomDistance(p.from, p.to)).ThenBy(p => p.from).ThenBy(p => p.to)
                    .First();
                var lever = spare.Where(l => l.MapId == best.from).OrderByDescending(l => l.Element.Cell).First();
                var light = waiting.Where(w => w.MapId == best.to).OrderBy(w => w.Element.Id).First();
                lever.Light = light;
                spare.Remove(lever);
                waiting.Remove(light);
            }

            Console.WriteLine($"[AbyssalBase] {tables.Doors.Count} doors, {tables.Lights.Count} lights, " +
                              $"{tables.Levers.Count} levers ({tables.Levers.Values.Count(l => l.Light == null)} without a light).");
            return tables;
        }

        private static double RoomDistance(long a, long b)
        {
            var one = MapManager.GetMapInfo(a);
            var other = MapManager.GetMapInfo(b);
            if (one == null || other == null) return double.MaxValue;
            double dx = one.PosX - other.PosX, dy = one.PosY - other.PosY;
            return Math.Sqrt(dx * dx + dy * dy);
        }

        // ─── The state ──────────────────────────────────────────────────────────────────────

        public static bool IsLit(Light light)
        {
            lock (_state) return _litUntil.TryGetValue(light, out var until) && until > DateTime.UtcNow;
        }

        public static bool IsOpen(Door door)
        {
            lock (_state) return _openUntil.TryGetValue(door, out var until) && until > DateTime.UtcNow;
        }

        /// <summary>A light's or a door's state for whoever loads its map, or 0 for any other element.</summary>
        public static int StateOf(long mapId, int elementId)
        {
            var tables = Current;
            if (tables.Lights.TryGetValue((mapId, elementId), out var light)) return IsLit(light) ? Lit : 0;
            if (tables.Doors.TryGetValue((mapId, elementId), out var door)) return IsOpen(door) ? Open : 0;
            return 0;
        }

        public static bool IsDoorOrLight(long mapId, int elementId)
        {
            var tables = Current;
            return tables.Lights.ContainsKey((mapId, elementId)) || tables.Doors.ContainsKey((mapId, elementId));
        }

        /// <summary>Whether a passage lets through: anything but a shut door's.</summary>
        public static bool MayPass(long mapId, int elementId)
        {
            var door = DoorOfPassage(mapId, elementId);
            return door == null || door.Lights.Count == 0 || IsOpen(door);
        }

        // ─── The lever ──────────────────────────────────────────────────────────────────────

        /// <summary>
        /// A lever pulled: a battery spent, its light lit, and the door open when that was the last
        /// of its lights. Without a battery nothing moves and the client's own words say why.
        /// </summary>
        public static async Task PullAsync(NetworkStream stream, long mapId, int elementId, int skillId)
        {
            var state = SessionContext.State;
            await Jondo.Protocol.NetworkMessage.WriteFrameAsync(stream,
                ConnectionProtocol.Push(Op.Iwi, ConnectionProtocol.BuildInteractiveUseEnded(elementId, skillId)));

            var lever = LeverOf(mapId, elementId);
            if (lever?.Light == null) return;

            if (!await Equipment.TakeAsync(stream, Battery, 1))
            {
                await Jondo.Protocol.NetworkMessage.WriteFrameAsync(stream,
                    ConnectionProtocol.Push(Op.Lqn, ConnectionProtocol.BuildNotice(
                        Handlers.CommandTexts.Get(state.Language, "abyss.lever.battery"))));
                return;
            }
            await Jondo.Protocol.NetworkMessage.WriteFrameAsync(stream,
                ConnectionProtocol.Push(Op.Lqn, ConnectionProtocol.BuildSystemMessage(InfoMessages.ItemLost, "1", Battery.ToString())));
            await SessionRegistry.BroadcastToMapAsync(mapId, ConnectionProtocol.Push(Op.Iwn,
                ConnectionProtocol.BuildElementInUse(elementId, skillId, state.CharacterId)));

            var light = lever.Light;
            var door = light.Door;
            bool opens;
            DateTime now = DateTime.UtcNow;
            lock (_state)
            {
                _litUntil[light] = now + LightTime;
                opens = door.Lights.All(l => _litUntil.TryGetValue(l, out var until) && until > now)
                        && !(_openUntil.TryGetValue(door, out var open) && open > now);
                if (opens)
                {
                    _openUntil[door] = now + OpenTime;
                    foreach (var other in door.Lights) _litUntil[other] = now + OpenTime;
                }
            }
            Console.WriteLine($"[AbyssalBase] {state.CharacterName} pulls lever {elementId} on {mapId}: " +
                              $"light {light.Element.Id} of door {door.Element.Id} on {door.MapId}" +
                              (opens ? ", which opens." : "."));

            await ShowAsync(light.MapId, light.Element, Lit);
            if (!opens)
            {
                _ = PutOutLaterAsync(light, LightTime);
                return;
            }

            await ShowAsync(door.MapId, door.Element, Open);
            _ = ShutLaterAsync(door, OpenTime);
        }

        private static Task ShowAsync(long mapId, Interactives.Element element, int value)
            => SessionRegistry.BroadcastToMapAsync(mapId, ConnectionProtocol.Push(Op.Iwf,
                   ConnectionProtocol.BuildElementState(element.Cell, element.Id, value)));

        private static async Task PutOutLaterAsync(Light light, TimeSpan after)
        {
            try
            {
                await Task.Delay(after + TimeSpan.FromMilliseconds(200));
                if (IsLit(light)) return;
                await ShowAsync(light.MapId, light.Element, 0);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[AbyssalBase] Light {light.Element.Id}: {ex.Message}");
            }
        }

        private static async Task ShutLaterAsync(Door door, TimeSpan after)
        {
            try
            {
                await Task.Delay(after + TimeSpan.FromMilliseconds(200));
                if (IsOpen(door)) return;
                await ShowAsync(door.MapId, door.Element, 0);
                foreach (var light in door.Lights)
                {
                    if (!IsLit(light)) await ShowAsync(light.MapId, light.Element, 0);
                }
                Console.WriteLine($"[AbyssalBase] Door {door.Element.Id} on {door.MapId} shuts.");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[AbyssalBase] Door {door.Element.Id}: {ex.Message}");
            }
        }

        /// <summary>A passage refused for its shut door: the client hears why.</summary>
        public static Task TellShutAsync(NetworkStream stream)
            => Jondo.Protocol.NetworkMessage.WriteFrameAsync(stream,
                   ConnectionProtocol.Push(Op.Lqn, ConnectionProtocol.BuildNotice(
                       Handlers.CommandTexts.Get(SessionContext.State.Language, "abyss.door.shut"))));
    }
}
