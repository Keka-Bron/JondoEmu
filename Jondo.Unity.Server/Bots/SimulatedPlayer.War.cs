using Jondo.Unity.ServerMods;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Jondo.Unity.Protocol;
using Jondo.Unity.Server.Managers;
using Jondo.Unity.Server.Mods;
using Jondo.Unity.Server.Network;

namespace Jondo.Unity.Server.Bots
{
    /// <summary>
    /// The bots in the cities' war. Three of every five swear to a city -- Bonta the even numbers,
    /// Brakmar the odd --; from level 20 they wear their wings and live on the war ground, the
    /// area next to the home one with the most maps that allow an aggression (the Cania
    /// Plains, beside Astrub), coming in on its front, the stretch nearest to the home area. There they look for
    /// the enemy and aggress one of their size, and from the eligible level they take an encounter
    /// from an optional PvP encounter provider and follow the target while its
    /// encounter is active.
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
        /// unless it is their active encounter target.
        /// </summary>
        private async Task<bool> AggressAsync(GameSession session, CancellationToken stop, long specificTarget = 0)
        {
            long map = session.State.MapId;
            if (!Alignments.MapAllowsAggression(map)) return false;
            var enemies = Enemies(session, specificTarget).Where(s => s.MapId == map).ToList();
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
        private IEnumerable<GameSession> Enemies(GameSession session, long specificTarget = 0)
        {
            int level = session.State.CharacterLevel;
            var now = DateTime.UtcNow;
            return SessionRegistry.All()
                .Where(s => s.IsInWorld && s.HasCharacter && s.CharacterId != _characterId
                            && (specificTarget == 0 || s.CharacterId == specificTarget)
                            && !s.State.IsInFight && s.State.FightId == 0
                            && Alignments.MayAggress(_characterId, level, s.CharacterId, s.State.CharacterLevel)
                            && Math.Min(s.State.CharacterLevel, 200) * 5 >= Math.Min(level, 200) * 3
                            && (specificTarget != 0 || !LastAggressed.TryGetValue(s.CharacterId, out var last) || now - last > Respite));
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

        /// <summary>Back to the front when an encounter took it away from the war ground: a step towards its nearest map.</summary>
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

        // ─── Optional encounters ─────────────────────────────────────────────────────────────────

        /// <summary>
        /// Asks the encounter provider for a target already in the world.
        /// </summary>
        private async Task FindEncounterAsync(GameSession session)
        {
            var encounter = ServerModHooks.Get<ISimulatedPlayerEncounters>();
            if (encounter == null) return;
            try
            {
                using var context = SessionContext.Push(session);
                if (await encounter.TryFindEncounterAsync(_characterId, session.State.CharacterLevel,
                    session.IsInWorld && session.HasCharacter, _dice.Next))
                    Console.WriteLine($"[Bots] {_name} starts a PvP encounter.");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[Bots] {_name} could not start a PvP encounter: {ex.Message}");
            }
        }

        /// <summary>
        /// Its PvP encounter while it is open: on the target's map, «Agredir» on it; elsewhere the way out
        /// nearest to where the prey is, as a player follows the target's location.
        /// </summary>
        private async Task<bool> FollowEncounterAsync(GameSession session, CancellationToken stop)
        {
            if (ServerModHooks.Get<ISimulatedPlayerEncounters>()?.EncounterTarget(_characterId) is not { } target) return false;
            var targetSession = SessionRegistry.FindByCharacter(target);
            if (targetSession == null || !targetSession.IsInWorld || targetSession.State.IsInFight) return false;
            if (targetSession.MapId == session.State.MapId) return await AggressAsync(session, stop, targetSession.CharacterId);

            var there = MapManager.GetMapInfo(targetSession.MapId);
            return there != null && there.Outdoor && await TowardsAsync(session, targetSession.MapId, stop);
        }
    }
}
