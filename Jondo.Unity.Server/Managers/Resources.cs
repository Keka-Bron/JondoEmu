using Jondo.Unity.Launcher;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;

namespace Jondo.Unity.Server.Managers
{
    /// <summary>What state a resource is in. It is the f4 of the jss's f15.</summary>
    public enum ResourceState
    {
        /// <summary>Full. The real server does not send the field.</summary>
        Full = 0,

        /// <summary>Agotado: alguien acaba de recogerlo.</summary>
        Depleted = 1,

        /// <summary>Someone is harvesting it right now.</summary>
        Busy = 2,
    }

    /// <summary>
    /// The world's harvestable resources: wheat, ash trees, fishing spots, minerals.
    ///
    /// ─── How a resource is recognised ───────────────────────────────────────────────────────
    ///
    /// The client knows where each element is and with which drawing, but not what it is: the TYPE and the
    /// SKILL are put by the server. So the 305 captures are crossed with the client's
    /// dump —tools/recursos_recoleccion.py does it— and out comes drawing → (type, skill). From the
    /// skill, the client's catalogue gives the profession and which item is obtained, because
    /// <c>gatheredRessourceItem</c> is in skills.json.
    ///
    /// 60 drawings come out, which are 25,090 resources on 4,507 maps and the six gathering professions.
    /// Miner and Hunter come out weak —415 and 325— because the captures barely set foot in mines or
    /// hunting zones; with more captures they go up on their own, without touching code.
    ///
    /// ─── How it is declared in the jss ──────────────────────────────────────────────────────
    ///
    /// With a twist that has to be respected or the client draws it wrong:
    ///
    ///   full      f11 { f1:1, f2:0, f4 { uid, skill }, f5: element, f6: type }   f15 without f4
    ///   depleted  f11 { f1:1,       f3 { uid, skill }, f5: element, f6: type }   f15 f4 = 1
    ///   in use    same as depleted, but the f15 carries f4 = 2
    ///
    /// That is, the skill changes field: it goes in 4 when it can be used and in 3 when
    /// it cannot. Checked on the 25 ash trees of one same map, without a single exception.
    ///
    /// The f2 of the full element is 0 for wood, wheat and sage, and 1 or 3 for the two
    /// fishing spots. What distinguishes those values has not been worked out, so 0 is sent: it is what was measured in
    /// three of the four professions and the client draws it right all the same.
    ///
    /// ─── The state is not stored ────────────────────────────────────────────────────────────
    ///
    /// It lives in memory and belongs to the whole server, not to each player: if one reaps a wheat, the one
    /// next to him sees it reaped. On restarting they all come back full, which is the same that would happen after
    /// the regrowth time.
    /// </summary>
    public static class Resources
    {
        /// <summary>How long a resource takes to be full again.</summary>
        public static readonly TimeSpan Regrowth = TimeSpan.FromMinutes(5);

        /// <summary>How long the harvesting gesture lasts. From the iwn's f3: 30 tenths.</summary>
        public const int GatherTenths = 30;

        /// <summary>Un recurso concreto puesto en un mapa.</summary>
        public sealed class Resource
        {
            public long MapId { get; init; }
            public int ElementId { get; init; }
            public int Cell { get; init; }
            public int Gfx { get; init; }
            public int Type { get; init; }
            public int SkillId { get; init; }
            public int JobId { get; init; }
            public int ItemId { get; init; }
            public int LevelMin { get; init; }
        }

        private sealed class Kind
        {
            public int Type;
            public int SkillId;
            public int JobId;
            public int ItemId;
            public int LevelMin;
        }

        private static readonly Dictionary<int, Kind> _byGfx = new();
        private static readonly Dictionary<long, List<Resource>> _byMap = new();
        private static readonly Dictionary<(long, int), Resource> _byElement = new();

        /// <summary>When each depleted resource is full again. No entry = full.</summary>
        private static readonly ConcurrentDictionary<(long, int), DateTime> _spent = new();

        /// <summary>The ones someone is harvesting right now.</summary>
        private static readonly ConcurrentDictionary<(long, int), bool> _busy = new();

        public static int Count => _byElement.Count;
        public static int MapCount => _byMap.Count;

        public static void Initialize()
        {
            _byGfx.Clear();
            _byMap.Clear();
            _byElement.Clear();
            _spent.Clear();
            _busy.Clear();

            string path = Paths.Resolve("recursos_3.6.10.10.json");
            if (!File.Exists(path))
            {
                Console.WriteLine($"[Recursos] Falta {Path.GetFileName(path)}; sin él no hay " +
                                  "recolección. Genéralo con tools/recursos_recoleccion.py.");
                return;
            }

            try
            {
                using var doc = JsonDocument.Parse(File.ReadAllText(path));
                if (!doc.RootElement.TryGetProperty("recursos", out var list)) return;

                foreach (var entry in list.EnumerateObject())
                {
                    if (!int.TryParse(entry.Name, out int gfx)) continue;
                    var v = entry.Value;
                    _byGfx[gfx] = new Kind
                    {
                        Type = v.GetProperty("tipo").GetInt32(),
                        SkillId = v.GetProperty("habilidad").GetInt32(),
                        JobId = v.TryGetProperty("oficio", out var j) ? j.GetInt32() : 0,
                        ItemId = v.TryGetProperty("objeto", out var i) ? i.GetInt32() : 0,
                        LevelMin = v.TryGetProperty("nivel", out var n) ? n.GetInt32() : 1,
                    };
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[Recursos] No se han podido leer los recursos: {ex.Message}");
                return;
            }

            foreach (long mapId in Interactives.MapIds)
            {
                List<Resource>? here = null;
                foreach (var element in Interactives.ElementsOf(mapId))
                {
                    if (element.Cell == 0) continue;
                    if (!_byGfx.TryGetValue(element.Gfx, out var kind)) continue;
                    // A passage hung off it makes it a door, whatever its graphic says: the GM
                    // island's chest, 479462, has the placeholder graphic 682 every olivioleta
                    // tree has, and as a tree it asked for woodcutting 90 to be opened.
                    // TeleportManager is read first (Program), so its passages are known here.
                    if (TeleportManager.TryGet(mapId, element.Id, out _)) continue;

                    var resource = new Resource
                    {
                        MapId = mapId,
                        ElementId = element.Id,
                        Cell = element.Cell,
                        Gfx = element.Gfx,
                        Type = kind.Type,
                        SkillId = kind.SkillId,
                        JobId = kind.JobId,
                        ItemId = kind.ItemId,
                        LevelMin = kind.LevelMin,
                    };

                    var clave = (mapId, element.Id);
                    if (_byElement.ContainsKey(clave)) continue;
                    _byElement.Add(clave, resource);
                    (here ??= new List<Resource>()).Add(resource);
                }
                if (here != null) _byMap.Add(mapId, here);
            }

            var oficios = new SortedDictionary<int, int>();
            foreach (var r in _byElement.Values)
            {
                oficios.TryGetValue(r.JobId, out int n);
                oficios[r.JobId] = n + 1;
            }

            Console.WriteLine($"[Recursos] {_byElement.Count} recolectables en {_byMap.Count} " +
                              $"mapas, {_byGfx.Count} gráficos, {oficios.Count} oficios.");
        }

        public static IReadOnlyList<Resource> On(long mapId)
            => _byMap.TryGetValue(mapId, out var list)
                ? list
                : (IReadOnlyList<Resource>)Array.Empty<Resource>();

        public static bool TryGet(long mapId, int elementId, out Resource resource)
            => _byElement.TryGetValue((mapId, elementId), out resource!);

        public static bool Is(long mapId, int elementId) => _byElement.ContainsKey((mapId, elementId));

        /// <summary>
        /// What state it is in. A depleted resource comes back on its own when the regrowth passes, so no
        /// timer is needed: the time is checked when someone asks.
        /// </summary>
        public static ResourceState StateOf(long mapId, int elementId)
        {
            var clave = (mapId, elementId);
            if (_busy.ContainsKey(clave)) return ResourceState.Busy;
            if (!_spent.TryGetValue(clave, out var cuando)) return ResourceState.Full;
            if (DateTime.UtcNow >= cuando)
            {
                _spent.TryRemove(clave, out _);
                return ResourceState.Full;
            }
            return ResourceState.Depleted;
        }

        /// <summary>Takes the resource to harvest it. Returns false if another got there first.</summary>
        public static bool TryHold(long mapId, int elementId)
        {
            if (StateOf(mapId, elementId) != ResourceState.Full) return false;
            return _busy.TryAdd((mapId, elementId), true);
        }

        /// <summary>It has been harvested: it stays depleted until it regrows.</summary>
        public static void Spend(long mapId, int elementId)
        {
            var clave = (mapId, elementId);
            _spent[clave] = DateTime.UtcNow + Regrowth;
            _busy.TryRemove(clave, out _);
        }

        /// <summary>
        /// Is the profession level of the player looking at this map enough?
        ///
        /// It is checked here and not only on clicking because the real game does not even let you try: on hovering
        /// over a resource that is beyond you, the icon comes out red just as if it
        /// were depleted. The client does that on its own, as long as the server declares the
        /// skill as not pressable. Warning through the chat was wrong for two reasons: it is not
        /// what the game does, and that line goes out through the general channel and everybody reads it.
        /// </summary>
        public static bool WithinReach(long mapId, int elementId)
        {
            if (!_byElement.TryGetValue((mapId, elementId), out var resource)) return true;
            return Network.SessionContext.State.JobLevel(resource.JobId) >= resource.LevelMin;
        }

        /// <summary>It has been let go without harvesting —the player left, or something failed—.</summary>
        public static void Release(long mapId, int elementId)
            => _busy.TryRemove((mapId, elementId), out _);
    }
}
