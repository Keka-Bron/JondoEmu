using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Jondo.Unity.Protocol;
using Jondo.Unity.Server.Managers;
using Jondo.Unity.Server.Network;

namespace Jondo.Unity.Server.Bots
{
    /// <summary>
    /// The bots in the cities' war. Three of every five swear to a city -- Bonta the even numbers,
    /// Brakmar the odd --; from level 20 they wear their wings and live on the war ground, the
    /// area next to the home one with the most maps that allow an aggression (the Cania
    /// Plains, beside Astrub), coming in on its front, the stretch nearest to the home area. There they look for
    /// the enemy and aggress one of their size, and from the hunters' level they take an order
    /// from the Cazacabezas board, through the mod's own door, and go after their prey while its
    /// hunt is open.
    /// </summary>
    internal sealed partial class SimulatedPlayer
    {
        /// <summary>The level from which a sworn bot wears its wings and goes to the war ground.</summary>
        internal const int WarLevel = 20;

        /// <summary>When each character was last set upon by a bot: nobody is set upon again within five minutes.</summary>
        private static readonly ConcurrentDictionary<long, DateTime> LastAggressed = new();
        private static readonly TimeSpan Respite = TimeSpan.FromMinutes(5);

        /// <summary>How many of the war ground's maps make its front, and how far a bot goes looking for an enemy.</summary>
        private const int FrontMaps = 40, SeekingRange = 8;

        /// <summary>The area it walks in: the home one, or the war ground once it is at war.</summary>
        private int _range = -1;
        private bool _atWar;

        /// <summary>Its session's token, the one a launcher would hand the client's mods.</summary>
        private string _token = "";

        /// <summary>The city a bot's number swears it to, or neutral for two of every five.</summary>
        internal static int SideOf(int number)
            => number % 5 >= 3 ? Alignments.Neutral : number % 2 == 0 ? Alignments.Bonta : Alignments.Brakmar;

        /// <summary>
        /// The area next to the home one -- one way out away -- with the most maps that allow an
        /// aggression; the home one itself if there is none.
        /// </summary>
        internal static readonly Lazy<int> WarGround = new(() =>
        {
            int home = HomeArea.Value;
            var allowing = MapManager.Maps.Values
                .Where(m => Alignments.MapAllowsAggression(m.MapId))
                .GroupBy(m => Interactives.AreaOfSubArea(m.SubAreaId))
                .ToDictionary(g => g.Key, g => g.Count());
            var next = new HashSet<int>();
            foreach (var map in MapManager.Maps.Values.Where(m => Interactives.AreaOfSubArea(m.SubAreaId) == home).ToList())
            {
                foreach (var way in Exits.GetOrAdd(map.MapId, WaysOut))
                {
                    int area = AreaOf(way.To);
                    if (area >= 0 && area != home) next.Add(area);
                }
            }
            return next.Where(a => allowing.GetValueOrDefault(a) > 0)
                       .OrderByDescending(a => allowing.GetValueOrDefault(a))
                       .DefaultIfEmpty(home).First();
        });

        /// <summary>
        /// The front, the maps a bot at war comes in on: the war ground's nearest to the home
        /// area's start, outdoors, allowing an aggression, with a way out and floor to stand on --
        /// close enough together for the two cities to meet.
        /// </summary>
        private static readonly Lazy<List<long>> Front = new(() => MapManager.Maps.Values
            .Where(m => m.Outdoor && Interactives.AreaOfSubArea(m.SubAreaId) == WarGround.Value
                        && Alignments.MapAllowsAggression(m.MapId) && !MapManager.IsArena(m.MapId))
            .Select(m => m.MapId)
            .OrderBy(id => Apart(id, Handlers.CharacterCreationHandler.StartingMap)).ThenBy(id => id)
            .Where(id => Exits.GetOrAdd(id, WaysOut).Count > 0 && MobSpawnManager.GetInnerWalkableCells(id).Count > 0)
            .Take(FrontMaps)
            .ToList());

        /// <summary>The steps between two maps by their coordinates; a great many when either is unknown.</summary>
        internal static int Apart(long a, long b)
        {
            var one = MapManager.GetMapInfo(a);
            var two = MapManager.GetMapInfo(b);
            return one == null || two == null ? int.MaxValue : Math.Abs(one.PosX - two.PosX) + Math.Abs(one.PosY - two.PosY);
        }

        /// <summary>
        /// Before it comes in: its city sworn the first time, with an honour drawn as its level is
        /// drawn -- a bot is born with a past --, and from the war's level a place on the war
        /// ground, unless it is there already.
        /// </summary>
        private void JoinTheWar(int level)
        {
            _range = HomeArea.Value;
            int side = SideOf(_number);
            if (side == Alignments.Neutral) return;
            if (!Alignments.Of(_characterId).Sworn)
            {
                Alignments.Set(_characterId, side, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
                Alignments.AddHonour(_characterId, _dice.Next(0, Math.Min(level, 200) * 60 + 1));
            }
            if (level < WarLevel || !Alignments.Of(_characterId).Sworn || WarGround.Value == HomeArea.Value) return;

            _atWar = true;
            _range = WarGround.Value;
            if (AreaOf(DatabaseManager.MapOfOffline(_characterId)) == _range || Front.Value.Count == 0) return;
            long map = Front.Value[_dice.Next(Front.Value.Count)];
            var cells = MobSpawnManager.GetInnerWalkableCells(map);
            DatabaseManager.SetPlaceOffline(_characterId, map, cells[_dice.Next(cells.Count)]);
        }

        /// <summary>Its wings out, with its sheet's switch (hnu), once it is in the world at war.</summary>
        private async Task WingsAsync()
        {
            var alignment = Alignments.Of(_characterId);
            if (_atWar && alignment.Sworn && !alignment.PvpOn) await SendAsync("hnu", Pb.New().Var(1, 1).Build());
        }

        /// <summary>
        /// An enemy on its map it may aggress («Agredir», the hph without its f2): of the other
        /// city with its wings out, not fighting, of its size -- three fifths of its level at the
        /// least, for a bot does not humiliate -- and not set upon by a bot in the last five minutes,
        /// unless it is the prey it hunts.
        /// </summary>
        private async Task<bool> AggressAsync(GameSession session, CancellationToken stop, long prey = 0)
        {
            long map = session.State.MapId;
            if (!Alignments.MapAllowsAggression(map)) return false;
            var enemies = Enemies(session, prey).Where(s => s.MapId == map).ToList();
            if (enemies.Count == 0) return false;
            var enemy = enemies[_dice.Next(enemies.Count)];
            LastAggressed[enemy.CharacterId] = DateTime.UtcNow;

            await WalkNextToAsync(session, enemy.State.CellId, stop);
            if (session.State.MapId != map || enemy.MapId != map || enemy.State.IsInFight) return false;
            Console.WriteLine($"[Bots] {_name} attacks {enemy.State.CharacterName}.");
            await SendAsync(Op.Hph, Pb.New().Var(1, enemy.CharacterId).Build());
            for (int waited = 0; waited < 50 && !session.State.IsInFight; waited++) await Task.Delay(100, stop);
            return session.State.IsInFight;
        }

        /// <summary>The characters in the world it may aggress, wherever they are (see <see cref="AggressAsync"/>).</summary>
        private IEnumerable<GameSession> Enemies(GameSession session, long prey = 0)
        {
            int level = session.State.CharacterLevel;
            var now = DateTime.UtcNow;
            return SessionRegistry.All()
                .Where(s => s.IsInWorld && s.HasCharacter && s.CharacterId != _characterId
                            && (prey == 0 || s.CharacterId == prey)
                            && !s.State.IsInFight && s.State.FightId == 0
                            && Alignments.MayAggress(_characterId, level, s.CharacterId, s.State.CharacterLevel)
                            && Math.Min(s.State.CharacterLevel, 200) * 5 >= Math.Min(level, 200) * 3
                            && (prey != 0 || !LastAggressed.TryGetValue(s.CharacterId, out var last) || now - last > Respite));
        }

        /// <summary>
        /// A step towards the nearest enemy it could aggress, eight maps away at the most on the
        /// war ground: the war is fought where the enemy is.
        /// </summary>
        private async Task<bool> SeekAsync(GameSession session, CancellationToken stop)
        {
            long here = session.State.MapId;
            var nearest = Enemies(session)
                .Where(s => s.MapId != here && AreaOf(s.MapId) == _range && Alignments.MapAllowsAggression(s.MapId))
                .Select(s => (Map: s.MapId, Far: Apart(here, s.MapId)))
                .Where(e => e.Far <= SeekingRange)
                .OrderBy(e => e.Far)
                .FirstOrDefault();
            if (nearest.Map == 0 || !await TowardsAsync(session, nearest.Map, stop)) return false;
            // Its enemy may still be there: it is set upon on arrival, before it walks on.
            await AggressAsync(session, stop);
            return true;
        }

        /// <summary>Back to the front when a hunt took it away from the war ground: a step towards its nearest map.</summary>
        private async Task<bool> BackToTheFrontAsync(GameSession session, CancellationToken stop)
        {
            long here = session.State.MapId;
            if (AreaOf(here) == _range || Front.Value.Count == 0) return false;
            return await TowardsAsync(session, Front.Value.OrderBy(m => Apart(here, m)).First(), stop);
        }

        /// <summary>The way out that brings it nearest to a map, if one brings it nearer at all.</summary>
        private async Task<bool> TowardsAsync(GameSession session, long map, CancellationToken stop)
        {
            int far = Apart(session.State.MapId, map);
            var nearer = Exits.GetOrAdd(session.State.MapId, WaysOut).Where(w => Apart(w.To, map) < far).ToList();
            if (nearer.Count == 0) return false;
            return await TakeTheWayAsync(session, nearer.OrderBy(w => Apart(w.To, map)).First(), stop);
        }

        // ─── The Cazacabezas ─────────────────────────────────────────────────────────────────

        /// <summary>
        /// An order from the board, now and then, as a player takes one in the Cazacabezas window:
        /// the same requests to the control API, with its session's token. Only a prey in the world
        /// is chosen -- a bot is not in for days to wait for one.
        /// </summary>
        private void TakeAnOrder(GameSession session)
        {
            if (!HeadHunts.On || HeadHunts.OrderOf(_characterId) != null
                || HeadHunts.CannotHunt(_characterId, session.State.CharacterLevel) != null) return;
            if (Ask("board") is not JsonElement board || !board.TryGetProperty("prey", out var list)) return;
            var online = list.EnumerateArray()
                .Where(p => p.GetProperty("online").GetBoolean() && p.GetProperty("hunters").GetInt32() < HeadHunts.HuntersPerTarget)
                .Select(p => p.GetProperty("id").GetInt64())
                .ToList();
            if (online.Count == 0) return;
            long target = online[_dice.Next(online.Count)];
            if (Ask("take", ",\"target\":" + target) != null)
                Console.WriteLine($"[Bots] {_name} takes a head hunter's order.");
        }

        /// <summary>One of the mod's requests, answered as the mod is answered; null unless it went well.</summary>
        private JsonElement? Ask(string route, string more = "")
        {
            var answer = ControlApi.Responder("/api/headhunts/" + route, "POST", "{\"token\":\"" + _token + "\"" + more + "}");
            if (answer is not { Codigo: 200 } ok) return null;
            return JsonDocument.Parse(ok.Json).RootElement.Clone();
        }

        /// <summary>
        /// Its hunt while it is open: on the prey's map, «Agredir» on it; elsewhere the way out
        /// nearest to where the prey is, as a player follows the hunt's sightings.
        /// </summary>
        private async Task<bool> HuntAsync(GameSession session, CancellationToken stop)
        {
            var order = HeadHunts.OrderOf(_characterId);
            if (order == null || !order.Open(HeadHunts.Clock())) return false;
            var prey = SessionRegistry.FindByCharacter(order.Target);
            if (prey == null || !prey.IsInWorld || prey.State.IsInFight) return false;
            if (prey.MapId == session.State.MapId) return await AggressAsync(session, stop, prey.CharacterId);

            var there = MapManager.GetMapInfo(prey.MapId);
            return there != null && there.Outdoor && await TowardsAsync(session, prey.MapId, stop);
        }
    }
}
