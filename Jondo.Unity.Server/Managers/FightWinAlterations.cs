using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Sockets;
using System.Text.Json;
using System.Threading.Tasks;
using Jondo.Unity.Launcher;
using Jondo.Unity.Server.Network;
using Jondo.Unity.World.Fights;

namespace Jondo.Unity.Server.Managers
{
    /// <summary>
    /// The alterations a won fight hands over for a monster beaten in it, and the ones a won fight
    /// on a map takes back (content/fights/win_alterations.json).
    /// </summary>
    /// <remarks>
    /// Written for Lord Crow's library as Ankama remade it on 21 January 2025: each of its three
    /// unique monsters leaves its feather, an alteration of the client's (632-634, category 20),
    /// and Virgil lets the holder of all three on to Capsálocke. Another dungeon that works the
    /// same way is a row, not code.
    /// </remarks>
    public static class FightWinAlterations
    {
        public const string File = "fights/win_alterations.json";

        public sealed class Win
        {
            /// <summary>The monster whose defeat hands the alteration over.</summary>
            public int Monster { get; init; }

            /// <summary>Only when beaten in this subarea, or anywhere when zero.</summary>
            public int SubArea { get; init; }

            /// <summary>The alteration handed over.</summary>
            public int Alteration { get; init; }

            /// <summary>The map where a won fight takes it back, or zero: never.</summary>
            public long LostAt { get; init; }
        }

        private static readonly List<Win> _wins = new();

        private static volatile bool _loaded;
        private static readonly object _lock = new object();

        /// <summary>Reads the file once, at boot, with its log line.</summary>
        public static void Load() => Ensure();

        /// <summary>Drops what was read, so the next question reads the file again (the content reload).</summary>
        public static void Forget()
        {
            lock (_lock)
            {
                _wins.Clear();
                _loaded = false;
            }
        }

        /// <summary>The rows read.</summary>
        public static IReadOnlyList<Win> All
        {
            get
            {
                Ensure();
                return _wins;
            }
        }

        /// <summary>The alterations a win over these monsters in that subarea hands over.</summary>
        public static IReadOnlyList<int> Gained(IEnumerable<int> beaten, int subArea)
        {
            var monsters = beaten.ToHashSet();
            return All.Where(w => monsters.Contains(w.Monster) && (w.SubArea == 0 || w.SubArea == subArea))
                      .Select(w => w.Alteration).Distinct().ToList();
        }

        /// <summary>The alterations a win on that map takes back.</summary>
        public static IReadOnlyList<int> Lost(long mapId)
            => mapId == 0 ? Array.Empty<int>() : All.Where(w => w.LostAt == mapId).Select(w => w.Alteration).Distinct().ToList();

        /// <summary>
        /// The end of a fight for the character of the session: on a win, the alterations its
        /// beaten monsters hand over, and those the fight's map takes back.
        /// </summary>
        public static async Task FightEndedAsync(NetworkStream stream, FightInstance fight, bool won)
        {
            if (!won || All.Count == 0) return;
            var state = SessionContext.State;
            if (fight.Buscar(state.CharacterId) == null) return;

            long map = fight.RoleplayMapId;
            int subArea = MapManager.GetMapInfo(map)?.SubAreaId ?? 0;
            var beaten = fight.Rojo.Where(m => !m.EsInvocado && m.MonsterId > 0).Select(m => m.MonsterId);

            foreach (int alteration in Gained(beaten, subArea))
            {
                if (Alterations.Has(state.CharacterId, alteration)) continue;
                if (await Handlers.AlterationHandler.GainAsync(stream, alteration) != null)
                    Console.WriteLine($"[Alterations] {state.CharacterName} gains {alteration} for a win on {map}.");
            }

            foreach (int alteration in Lost(map))
            {
                if (await Handlers.AlterationHandler.LoseAsync(stream, alteration))
                    Console.WriteLine($"[Alterations] {state.CharacterName} loses {alteration} with the win on {map}.");
            }
        }

        private static void Ensure()
        {
            if (_loaded) return;
            lock (_lock)
            {
                if (_loaded) return;
                try
                {
                    Read();
                }
                finally
                {
                    _loaded = true;   // a missing file counts as tried
                }
            }
        }

        private static void Read()
        {
            string path = Paths.ContentFile(File);
            if (!System.IO.File.Exists(path)) return;

            try
            {
                _wins.AddRange(Parse(System.IO.File.ReadAllText(path)));
                Console.WriteLine($"[Alterations] {_wins.Count} alteration(s) handed over by a won fight.");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[Alterations] Could not read {File}: {ex.Message}");
            }
        }

        /// <summary>The rows of the file's text; those without a monster or an alteration are left out.</summary>
        public static List<Win> Parse(string json)
        {
            var wins = new List<Win>();
            using var doc = JsonDocument.Parse(json);
            if (!doc.RootElement.TryGetProperty("wins", out var list) || list.ValueKind != JsonValueKind.Array) return wins;

            foreach (var row in list.EnumerateArray())
            {
                var win = new Win
                {
                    Monster = (int)Number(row, "monster"),
                    SubArea = (int)Number(row, "subarea"),
                    Alteration = (int)Number(row, "alteration"),
                    LostAt = Number(row, "lostAt"),
                };
                if (win.Monster > 0 && win.Alteration > 0) wins.Add(win);
            }
            return wins;
        }

        private static long Number(JsonElement row, string name)
            => row.TryGetProperty(name, out var value) && value.TryGetInt64(out long number) ? number : 0;
    }
}
