using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Net.Sockets;
using System.Text.Json;
using System.Threading.Tasks;
using System.Text.RegularExpressions;
using Jondo.Unity.World.Content;
using Jondo.Unity.World.Fights;
using Microsoft.Data.Sqlite;

namespace Jondo.Unity.Server.Managers
{
    /// <summary>
    /// The Santuario's four guardians: one in the middle of each zone, with four companions, in
    /// fights of four players at most (the guides). Beating the four is what opens the castle.
    /// </summary>
    /// <remarks>
    /// WHERE: each in the zone its name gives -- the Vigilante de la Obra, the Guardián del Enclave,
    /// the Defensor de la Reserva, the Centinela de la Corte (the Patio de Efedra) -- on the zone's
    /// central map, the one the hub's portal arrives at (GuildRaidPassages.ArrivalOf). The client's
    /// world places none of them: no group of the Santuario carries a guardian, nor its bosses.
    ///
    /// WHEN: the guides wake them once the four enigmas are solved. The enigmas cannot be shown
    /// with this client (their pedestals, flowers and boards are map stagings, which it only plays
    /// in fights), so the guardians stand from the raid's start, or the raid could not be finished.
    ///
    /// WITH WHOM: "four monsters, one of each kind" -- the Santuario's Trilipín, Machacamelia,
    /// Muguetégida and Daliana, the castle's colourless ones -- and the Centinela, its four
    /// obelisks besides.
    ///
    /// THE CENTINELA'S COLOUR: in the guides it is the colour the Obra takes when its enigma is
    /// solved. Its obelisks and its spells list a version per colour -- "Check color == 1" to "4",
    /// "Caleidohueso color == 1 / feu" -- and the data picks none: the server does
    /// (<see cref="ChosenCasts"/>), by the raid's colour, drawn at its start since the Obra cannot
    /// give it. The players read it off the element of its hits, as the guides advise.
    ///
    /// THE VIGILANTE AND THE GUARDIÁN start invulnerable, and what frees them is the client's own
    /// data: scripts written for the fight's scene, a carrier of neither side ("Sce", with "Def"
    /// and "Atq" for the sides), which the guardian itself stands for here
    /// (<see cref="FightInstance.SceneStandIn"/>).
    ///
    ///   The Vigilante carries "Check Mob" (32099). Each kind of companion's death casts its
    ///   "mob == N" and its "mob != N": the first frees it (32108: invulnerability off, 120 % damage
    ///   taken, 110 % for its companions, Check Mob gone), the second ends the fight.
    ///
    ///   The Guardián carries "Check délock" (32090) and lays its eight "Glyphe Indice" (o_hint_1
    ///   to 8). A step on one casts its "o_hint_N &lt; 5" -- one more on the counter of states,
    ///   that glyph gone -- or its "o_hint_N &gt; 4", which ends the fight. The fourth step frees
    ///   it and takes the other glyphs away. Where they go is the server's: spread over the board.
    ///
    /// Which companion and which glyphs are right is, in the guides, what the Enclave's and the
    /// Obra's enigmas showed. With those impossible here, any companion and any glyph is right
    /// (his call, 2026-10-10): the "== N" and "&lt; 5" versions go through, the others never.
    /// A glyph's check goes to every attacker on the board; it counts once.
    /// </remarks>
    public static class GuildRaidGuardians
    {
        /// <summary>The guardians by the zone they guard, in the raid's zone order.</summary>
        public static readonly IReadOnlyDictionary<int, int> GuardianOfZone = new Dictionary<int, int>
        {
            [1] = 8318,     // Vigilante de la Obra
            [2] = 8317,     // Guardián del Enclave
            [3] = 8319,     // Defensor de la Reserva
            [4] = 8316,     // Centinela de la Corte, the Patio de Efedra's
        };

        /// <summary>One of each of the Santuario's kinds: Trilipín, Machacamelia, Muguetégida, Daliana.</summary>
        public static readonly IReadOnlyList<int> Companions = new[] { 8285, 8286, 8287, 8288 };

        /// <summary>The Centinela's obelisks: escarlata, azulado, ambarino, viridina.</summary>
        public static readonly IReadOnlyList<int> Obelisks = new[] { 8320, 8321, 8322, 8323 };

        public const int Centinela = 8316;
        public const int Vigilante = 8318;
        public const int Guardian = 8317;

        /// <summary>The client's state 56, invulnerable, which the guardians' data puts on them.</summary>
        public const int Invulnerable = 56;

        /// <summary>The Vigilante's "Check Mob", the watcher of its companions' deaths.</summary>
        public const int CheckMob = 32099;

        /// <summary>The Guardián's "Check délock", which frees it at the fourth glyph.</summary>
        public const int CheckUnlock = 32090;

        /// <summary>The Guardián's eight "Glyphe Indice", o_hint_1 to o_hint_8.</summary>
        public static readonly IReadOnlyList<int> GlyphSpells = new[] { 32064, 32069, 32070, 32071, 32072, 32073, 32074, 32075 };

        /// <summary>How far apart, at least, the glyphs are laid when the board has room.</summary>
        public const int GlyphSpacing = 3;

        /// <summary>The guardians' fights take four players at most (the guides).</summary>
        public const int PeopleCap = 4;

        /// <summary>The raid's colour, 1 to 4 as the client's spells number them.</summary>
        public const string ColourVariable = "Santuario_Color";

        // ─── Placing them ───────────────────────────────────────────────────────────────────

        /// <summary>A guardian's group: it, its companions, and the Centinela's obelisks.</summary>
        public static List<int> GroupOf(int guardian)
        {
            var members = new List<int> { guardian };
            members.AddRange(Companions);
            if (guardian == Centinela) members.AddRange(Obelisks);
            return members;
        }

        /// <summary>Puts each guardian with its group on its zone's central map. How many were placed.</summary>
        public static int Place(RaidKind kind)
        {
            if (kind == null || kind.Id != Raids.EternalGardens) return 0;
            int placed = 0;
            foreach (var (zone, guardian) in GuardianOfZone)
            {
                if (GuildRaidPassages.ArrivalOf(kind, zone) is not { } arrival) continue;
                var group = MobSpawnManager.SpawnComposed(arrival.MapId, GroupOf(guardian).Select(m => (m, 0)));
                if (group == null) continue;
                placed++;
                Program.LogDebug($"[Raids] Guardian {guardian} placed on map {arrival.MapId}, cell {group.CellId}.");
            }
            return placed;
        }

        /// <summary>Draws the raid's colour, which the Centinela's obelisks and spells follow.</summary>
        public static int DrawColour(RaidInstance raid, Random dice)
        {
            int colour = dice.Next(1, 5);
            raid.Set(ColourVariable, colour);
            return colour;
        }

        // ─── In the fight ───────────────────────────────────────────────────────────────────

        public static bool IsGuardianFight(FightInstance fight)
            => fight.Rojo.Any(m => m.IsMonster && GuardianOfZone.Values.Contains(m.MonsterId));

        /// <summary>
        /// A fight just built: a guardian's takes four players a side, and the Vigilante and the
        /// Guardián stand for its scene and carry their checks, cast with the attitudes before the
        /// first turn.
        /// </summary>
        public static void OnFightCreated(FightInstance fight)
        {
            if (!IsGuardianFight(fight)) return;
            fight.PeopleCap = PeopleCap;
            foreach (var monster in fight.Rojo.Where(m => m.IsMonster).ToList())
            {
                int check = monster.MonsterId switch { Vigilante => CheckMob, Guardian => CheckUnlock, _ => 0 };
                if (check == 0) continue;
                monster.Buffs.PonerActitud(check);
                fight.SceneStandIn = monster;
            }
        }

        /// <summary>The cells the Guardián's glyphs go on: free cells of the board, spread apart.</summary>
        public static List<int> GlyphCells(IEnumerable<int> walkable, ISet<int> taken, int count, Random dice)
        {
            var free = (walkable ?? Enumerable.Empty<int>()).Where(c => !taken.Contains(c)).OrderBy(c => c).ToList();
            var chosen = new List<int>();
            while (chosen.Count < count && free.Count > 0)
            {
                var apart = free.Where(c => chosen.All(o => Jondo.Unity.World.Maps.MapGeometry.Distance(o, c) >= GlyphSpacing)).ToList();
                var pool = apart.Count > 0 ? apart : free;
                int cell = pool[dice.Next(pool.Count)];
                chosen.Add(cell);
                free.Remove(cell);
            }
            return chosen;
        }

        /// <summary>The fight has begun: the Guardián lays its eight glyphs.</summary>
        internal static async Task OnFightStartedAsync(NetworkStream stream, FightInstance fight)
        {
            var guardian = fight?.Rojo.FirstOrDefault(m => m.IsAlive && m.IsMonster && m.MonsterId == Guardian);
            if (guardian == null) return;
            var taken = new HashSet<int>(fight.Azul.Concat(fight.Rojo).Where(f => f.IsAlive).Select(f => f.CellId));
            var cells = GlyphCells(MapManager.GetFightWalkable(fight.MapId), taken, GlyphSpells.Count, Random.Shared);
            for (int i = 0; i < cells.Count; i++)
                await Handlers.FightHandler.CastAtAsync(stream, fight, guardian, GlyphSpells[i], 1, cells[i]);
            Program.LogDebug($"[Raids] The Guardián lays its {cells.Count} glyphs on {string.Join(", ", cells)}.");
        }

        /// <summary>
        /// The verdict on a check of the Vigilante's or the Guardián's, by its admin name: "mob == N"
        /// and "o_hint_N &lt; 5" go through, "mob != N" and "o_hint_N &gt; 4" do not; null for any
        /// other spell.
        /// </summary>
        public static bool? CheckVerdict(int spell)
        {
            string admin = AdminNameOf(spell);
            if (Regex.IsMatch(admin, @"^mob\s*==\s*\d") || Regex.IsMatch(admin, @"^o_hint_\d+\s*<\s*5")) return true;
            if (Regex.IsMatch(admin, @"^mob\s*!=\s*\d") || Regex.IsMatch(admin, @"^o_hint_\d+\s*>\s*4")) return false;
            return null;
        }

        private static readonly ConcurrentDictionary<(long Fight, int Check), byte> _counted = new();

        /// <summary>
        /// Whether a check's cast goes through: a failing one never, a passing one once a fight --
        /// a glyph's goes to every attacker on the board and is one step all the same.
        /// </summary>
        public static bool Passes(FightInstance fight, int check)
        {
            if (CheckVerdict(check) != true) return false;
            return fight == null || _counted.TryAdd((fight.FightId, check), 0);
        }

        /// <summary>A fight is over: its counted checks go.</summary>
        public static void Forget(long fightId)
        {
            foreach (var key in _counted.Keys.Where(k => k.Fight == fightId).ToList()) _counted.TryRemove(key, out _);
        }

        private static readonly ConcurrentDictionary<int, string> _adminNames = new();

        /// <summary>
        /// The colour a spell is the version of, by its admin name -- "Check color == 1",
        /// "color == 3 / eau" -- or 0 when it is none.
        /// </summary>
        public static int ColourOf(int spell)
        {
            var match = Regex.Match(AdminNameOf(spell), @"color\s*==\s*(\d)");
            return match.Success ? int.Parse(match.Groups[1].Value) : 0;
        }

        /// <summary>A spell's admin name in the client's data, read once.</summary>
        private static string AdminNameOf(int spell) => _adminNames.GetOrAdd(spell, ReadAdminName);

        private static string ReadAdminName(int spell)
        {
            try
            {
                using var connection = new SqliteConnection(DatabaseManager.WorldConnectionString);
                connection.Open();
                using var command = connection.CreateCommand();
                command.CommandText = "SELECT Data FROM SpellTemplates WHERE Id = $id;";
                command.Parameters.AddWithValue("$id", spell);
                if (command.ExecuteScalar() is not string data) return "";
                using var doc = JsonDocument.Parse(data);
                return doc.RootElement.TryGetProperty("adminName", out var a) ? a.GetString() ?? "" : "";
            }
            catch
            {
                return "";
            }
        }

        /// <summary>The colour of the raid a fight is fought in, or 0.</summary>
        public static int ColourOf(FightInstance fight)
        {
            if (fight == null) return 0;
            var player = fight.Azul.Concat(fight.Rojo).FirstOrDefault(f => !f.IsMonster && !f.EsInvocado);
            var raid = player == null ? null : GuildRaidManager.RaidOf(player.Id);
            return raid == null ? 0 : (int)raid.Get(ColourVariable);
        }
    }
}
