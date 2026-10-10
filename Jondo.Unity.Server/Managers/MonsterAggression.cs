using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Jondo.Unity.Server.Handlers;
using Jondo.Unity.Server.Network;
using Jondo.Unity.World.Content;
using Jondo.Unity.World.Maps;
using Microsoft.Data.Sqlite;

namespace Jondo.Unity.Server.Managers
{
    /// <summary>
    /// Monsters that jump on whoever comes too close, by the client's own rule: the one its
    /// roleplay service uses to decide whether to draw a group's aggression zone
    /// (<c>RoleplayEntitiesService.xzf</c>).
    /// </summary>
    /// <remarks>
    /// The rule, read from the client:
    ///
    ///   the map allows monster aggression (its MapInformationFlags carry
    ///   CapabilityAllowMonsterAggression, 1 &lt;&lt; 20);
    ///   the group's leader is aggressive (aggressiveZoneSize &gt; 0, <c>MonsterData.isAggressive</c>);
    ///   the leader's aggressiveImmunityCriterion is not met;
    ///   the group's zone is the widest of its aggressive members', and its aggression level the
    ///   highest of their grade level less their aggressiveLevelDiff (<c>MonsterData.AggressionLevel</c>);
    ///   and the player's level, counted up to 200, is not above that level.
    ///
    /// A group that would jump attacks after its aggressiveAttackDelay -- 3 s for the abyss's --
    /// if the player is still within its zone then.
    ///
    /// Only inside the guild raids for now: there it is the darkness of the Gigalodón's floors,
    /// whose monsters carry <c>(PB=1131&amp;RV!7,n1_worldlight,0)|…</c> -- immune while the floor has
    /// light -- and Willorque, whose zone is 50 cells and has no immunity at all. The rest of
    /// the world keeps its peaceful monsters until that is wanted: it is <see cref="Applies"/>.
    /// </remarks>
    public static class MonsterAggression
    {
        /// <summary>The client counts the player's level up to this.</summary>
        public const int LevelCap = 200;

        /// <summary>MapInformationFlags.CapabilityAllowMonsterAggression.</summary>
        public const int AllowMonsterAggression = 1 << 20;

        /// <summary>How often the raids' players are looked at, for the dark that falls on them standing still.</summary>
        public static readonly TimeSpan SweepEvery = TimeSpan.FromSeconds(1);

        /// <summary>What a monster's data says of its aggression.</summary>
        public readonly record struct Profile(int Zone, int LevelDiff, string Immunity, int DelayMs)
        {
            public bool Aggressive => Zone > 0;
        }

        /// <summary>A group that would jump: its zone, its aggression level, its leader's immunity and delay.</summary>
        public sealed record Threat(MobSpawnManager.MobGroup Group, int Zone, int Level, string Immunity, int DelayMs);

        private static readonly ConcurrentDictionary<int, Profile> _profiles = new();
        private static readonly ConcurrentDictionary<long, bool> _maps = new();
        private static readonly ConcurrentDictionary<long, long> _pending = new();
        private static int _sweeping;

        /// <summary>For the tests: a monster's aggression, or a map's permission, by hand.</summary>
        internal static void Declare(int template, Profile profile) => _profiles[template] = profile;
        internal static void DeclareMap(long mapId, bool allows) => _maps[mapId] = allows;

        // ─── The data ───────────────────────────────────────────────────────────────────────

        /// <summary>A monster's aggression, from its template, read once.</summary>
        public static Profile ProfileOf(int template) => _profiles.GetOrAdd(template, ReadProfile);

        private static Profile ReadProfile(int template)
        {
            try
            {
                using var connection = new SqliteConnection(DatabaseManager.WorldConnectionString);
                connection.Open();
                using var command = connection.CreateCommand();
                command.CommandText = "SELECT Data FROM MonsterTemplates WHERE Id = $id;";
                command.Parameters.AddWithValue("$id", template);
                if (command.ExecuteScalar() is not string data) return default;
                using var doc = JsonDocument.Parse(data);
                var root = doc.RootElement;
                int Int(string name) => root.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetInt32() : 0;
                string immunity = root.TryGetProperty("aggressiveImmunityCriterion", out var c) ? c.GetString() ?? "" : "";
                return new Profile(Int("aggressiveZoneSize"), Int("aggressiveLevelDiff"), immunity, Int("aggressiveAttackDelay"));
            }
            catch (Exception ex)
            {
                Program.LogDebug($"[Aggression] Could not read monster {template}: {ex.Message}");
                return default;
            }
        }

        /// <summary>Whether a map lets its monsters aggress, by its flags in the client's data.</summary>
        public static bool MapAllows(long mapId) => _maps.GetOrAdd(mapId, ReadMap);

        private static bool ReadMap(long mapId)
        {
            try
            {
                using var connection = new SqliteConnection(DatabaseManager.WorldConnectionString);
                connection.Open();
                using var command = connection.CreateCommand();
                command.CommandText = "SELECT Data FROM MapTemplates WHERE Id = $id;";
                command.Parameters.AddWithValue("$id", mapId);
                if (command.ExecuteScalar() is not string data) return false;
                using var doc = JsonDocument.Parse(data);
                return doc.RootElement.TryGetProperty("m_flags", out var flags)
                       && (flags.GetInt64() & AllowMonsterAggression) != 0;
            }
            catch (Exception ex)
            {
                Program.LogDebug($"[Aggression] Could not read map {mapId}: {ex.Message}");
                return false;
            }
        }

        /// <summary>Where monsters aggress at all: for now, the guild raids' floors.</summary>
        public static bool Applies(long mapId) => GuildRaidManager.IsRaidMap(mapId);

        // ─── The rule ───────────────────────────────────────────────────────────────────────

        /// <summary>What the client works out for a group, or null when its leader is no aggressor.</summary>
        public static Threat ThreatOf(MobSpawnManager.MobGroup group)
        {
            if (group?.Members == null || group.Members.Count == 0) return null;
            var leader = ProfileOf(group.Members[0].Monster?.Id ?? 0);
            if (!leader.Aggressive) return null;

            int zone = 0, level = int.MinValue;
            foreach (var member in group.Members)
            {
                var profile = ProfileOf(member.Monster?.Id ?? 0);
                if (!profile.Aggressive) continue;
                zone = Math.Max(zone, profile.Zone);
                level = Math.Max(level, member.Level - profile.LevelDiff);
            }
            return new Threat(group, zone, level, leader.Immunity, leader.DelayMs);
        }

        /// <summary>
        /// Whether a group jumps on a player of this level at this cell, his immunity told by
        /// <paramref name="immune"/> (what is not known counts as immune: a monster that might not
        /// aggress does not).
        /// </summary>
        public static bool Jumps(Threat threat, int playerLevel, int playerCell, Func<string, bool> immune)
        {
            if (threat == null) return false;
            if (MapGeometry.Distance(threat.Group.CellId, playerCell) > threat.Zone) return false;
            if (Math.Min(playerLevel, LevelCap) > threat.Level) return false;
            return string.IsNullOrWhiteSpace(threat.Immunity) || !immune(threat.Immunity);
        }

        /// <summary>The nearest group that would jump on this character where he stands, or null.</summary>
        public static Threat Find(long characterId, int playerLevel, long mapId, int cell)
        {
            if (!Applies(mapId) || !MapAllows(mapId)) return null;
            var groups = MobSpawnManager.GetMobsForMap(mapId).Where(g => !FightHandler.IsGroupFighting(g.MobId));
            return Nearest(groups, GuildRaidManager.ResolverFor(characterId), playerLevel, cell);
        }

        /// <summary>
        /// The nearest of these groups that would jump on a player of this level at this cell, his
        /// criteria answered by <paramref name="resolver"/>.
        /// </summary>
        public static Threat Nearest(IEnumerable<MobSpawnManager.MobGroup> groups, Criterion.Resolver resolver,
                                     int playerLevel, int cell)
        {
            bool Immune(string criterion) => Criterion.Evaluate(criterion, resolver) != Answer.False;
            return groups.OrderBy(g => MapGeometry.Distance(g.CellId, cell))
                         .Select(ThreatOf)
                         .FirstOrDefault(t => Jumps(t, playerLevel, cell, Immune));
        }

        // ─── Watching ───────────────────────────────────────────────────────────────────────

        /// <summary>
        /// Looks around a character where he stands and, when a group would jump on him, has it do
        /// so after its delay -- if he is still within its reach then, and still out of a fight.
        /// </summary>
        public static void Watch(GameSession session)
        {
            var state = session?.State;
            if (state == null || session.Stream == null || state.CharacterId == 0 || state.IsInFight) return;
            var threat = Find(state.CharacterId, state.CharacterLevel, state.MapId, state.CellId);
            if (threat == null || !_pending.TryAdd(state.CharacterId, threat.Group.MobId)) return;

            long characterId = state.CharacterId;
            _ = Task.Run(async () =>
            {
                try
                {
                    await Task.Delay(Math.Max(0, threat.DelayMs));
                    if (!await session.UnoCadaVez.WaitAsync(TimeSpan.FromSeconds(5))) return;
                    try
                    {
                        using (SessionContext.Push(session))
                        {
                            var now = session.State;
                            if (now.IsInFight || SessionRegistry.FindByCharacter(characterId) != session) return;
                            var still = Find(characterId, now.CharacterLevel, now.MapId, now.CellId);
                            if (still == null) return;
                            Program.LogDebug($"[Aggression] Group {still.Group.MobId} jumps on {characterId} " +
                                             $"at cell {now.CellId} of map {now.MapId}.");
                            await FightHandler.InitiateFightFromMobCollision(session.Stream, still.Group, now.MapId);
                        }
                    }
                    finally
                    {
                        session.UnoCadaVez.Release();
                    }
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"[Aggression] The attack on {characterId} failed: {ex.Message}");
                }
                finally
                {
                    _pending.TryRemove(characterId, out _);
                }
            });
        }

        /// <summary>
        /// Keeps looking at the raids' players every second while any raid runs: that is what
        /// catches the light running out on someone standing still, as well as whoever walks or
        /// arrives within a zone. One sweep at a time; it stops when the last raid is over.
        /// </summary>
        public static void EnsureSweeping()
        {
            if (Interlocked.Exchange(ref _sweeping, 1) != 0) return;
            _ = Task.Run(async () =>
            {
                try
                {
                    while (GuildRaidManager.AnyRunning)
                    {
                        foreach (var session in GameNodeProxy.SesionesVivas.Values.ToList())
                        {
                            if (!session.HasCharacter || !Applies(session.State.MapId)) continue;
                            try { Watch(session); }
                            catch (Exception ex) { Program.LogDebug($"[Aggression] Sweep: {ex.Message}"); }
                        }
                        await Task.Delay(SweepEvery);
                    }
                }
                finally
                {
                    Interlocked.Exchange(ref _sweeping, 0);
                }
            });
        }
    }
}
