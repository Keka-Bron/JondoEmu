using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Sockets;
using System.Threading.Tasks;
using Jondo.Unity.Server.Network;
using Microsoft.Data.Sqlite;

namespace Jondo.Unity.Server.Managers
{
    /// <summary>
    /// The guild raids' weekly frieze, per player: a raid's best score this week unlocks the steps
    /// of its frieze (the client's guildraidsrewards data: score, experience, kamas, items), and the
    /// player claims them from the rewards screen -- the steps of ONE raid a week.
    /// </summary>
    /// <remarks>
    /// All of it read from the client. Its rewards screen keeps, per raid, the steps unlocked and the
    /// week's best score (hxm f3), and the steps claimed with the raid they came from (hxm f5); it
    /// enables "claim" only when nothing of another raid was claimed this week, and asks for
    /// confirmation the first time ("you can only claim one raid a week"). The week starts at the
    /// weekly reset the captures measure, Tuesday 05:00 UTC.
    /// </remarks>
    public static class GuildRaidRewards
    {
        /// <summary>The result of a claim (RaidClaimRewardResponse f1), as the client translates it.</summary>
        public enum ClaimResult
        {
            Done = 0, NoRewardToClaim = 1, RewardsAlreadyClaimed = 2, InvalidRaid = 3, InventoryFull = 4,
            SubscriptionRequired = 5,
        }

        /// <summary>The week a moment belongs to: the date of the weekly reset that opened it.</summary>
        public static string WeekOf(DateTimeOffset when)
            => GuildProtocol.NextWeeklyReset(when.UtcDateTime).AddDays(-7).ToString("yyyy-MM-dd");

        private static SqliteConnection Open()
        {
            var connection = new SqliteConnection(GuildStore.ConnectionStringOverride ?? DatabaseManager.WorldConnectionString);
            connection.Open();
            var create = connection.CreateCommand();
            create.CommandText = @"
                CREATE TABLE IF NOT EXISTS GuildRaidPlayerScores (
                    CharacterId INTEGER NOT NULL,
                    Week TEXT NOT NULL,
                    RaidId INTEGER NOT NULL,
                    Best INTEGER NOT NULL DEFAULT 0,
                    PRIMARY KEY (CharacterId, Week, RaidId)
                );
                CREATE TABLE IF NOT EXISTS GuildRaidClaims (
                    CharacterId INTEGER NOT NULL,
                    Week TEXT NOT NULL,
                    RaidId INTEGER NOT NULL,
                    RewardId INTEGER NOT NULL,
                    PRIMARY KEY (CharacterId, Week, RewardId)
                );";
            create.ExecuteNonQuery();
            return connection;
        }

        /// <summary>The character's best score of the week in each raid he played.</summary>
        public static Dictionary<int, long> BestScores(long characterId, DateTimeOffset now)
        {
            using var connection = Open();
            var query = connection.CreateCommand();
            query.CommandText = "SELECT RaidId, Best FROM GuildRaidPlayerScores WHERE CharacterId = $c AND Week = $w;";
            query.Parameters.AddWithValue("$c", characterId);
            query.Parameters.AddWithValue("$w", WeekOf(now));
            var best = new Dictionary<int, long>();
            using var reader = query.ExecuteReader();
            while (reader.Read()) best[reader.GetInt32(0)] = reader.GetInt64(1);
            return best;
        }

        /// <summary>The steps of a raid's frieze a score reaches, in order.</summary>
        public static List<int> UnlockedBy(int raidId, long score)
            => GuildRaidCatalogue.RewardsOf(raidId).Where(r => r.Score <= score).Select(r => r.Id).ToList();

        /// <summary>
        /// Records a raid the character played to its end, and returns the steps it unlocked that his
        /// week's best had not already: the "rewards to claim" of the final score screen.
        /// </summary>
        public static List<int> RecordRun(long characterId, int raidId, long score, DateTimeOffset now)
        {
            long before = BestScores(characterId, now).TryGetValue(raidId, out long b) ? b : 0;
            if (score <= before) return new List<int>();

            using var connection = Open();
            var upsert = connection.CreateCommand();
            upsert.CommandText = @"
                INSERT INTO GuildRaidPlayerScores (CharacterId, Week, RaidId, Best) VALUES ($c, $w, $r, $s)
                ON CONFLICT(CharacterId, Week, RaidId) DO UPDATE SET Best = MAX(Best, $s);";
            upsert.Parameters.AddWithValue("$c", characterId);
            upsert.Parameters.AddWithValue("$w", WeekOf(now));
            upsert.Parameters.AddWithValue("$r", raidId);
            upsert.Parameters.AddWithValue("$s", score);
            upsert.ExecuteNonQuery();

            var already = UnlockedBy(raidId, before);
            return UnlockedBy(raidId, score).Where(id => !already.Contains(id)).ToList();
        }

        /// <summary>What the character claimed this week: the raid and its steps, or null.</summary>
        public static (int RaidId, List<int> Rewards)? Claimed(long characterId, DateTimeOffset now)
        {
            using var connection = Open();
            var query = connection.CreateCommand();
            query.CommandText = "SELECT RaidId, RewardId FROM GuildRaidClaims WHERE CharacterId = $c AND Week = $w ORDER BY RewardId;";
            query.Parameters.AddWithValue("$c", characterId);
            query.Parameters.AddWithValue("$w", WeekOf(now));
            int raid = 0;
            var rewards = new List<int>();
            using var reader = query.ExecuteReader();
            while (reader.Read())
            {
                raid = reader.GetInt32(0);
                rewards.Add(reader.GetInt32(1));
            }
            return rewards.Count == 0 ? null : (raid, rewards);
        }

        /// <summary>
        /// The steps waiting to be claimed: those unlocked and not claimed of the raid already chosen
        /// this week, or, with none chosen yet, every step unlocked.
        /// </summary>
        public static List<int> Pending(long characterId, DateTimeOffset now)
        {
            var best = BestScores(characterId, now);
            var claimed = Claimed(characterId, now);
            if (claimed is { } c)
                return UnlockedBy(c.RaidId, best.TryGetValue(c.RaidId, out long s) ? s : 0)
                       .Where(id => !c.Rewards.Contains(id)).ToList();
            return best.OrderBy(kv => kv.Key).SelectMany(kv => UnlockedBy(kv.Key, kv.Value)).ToList();
        }

        /// <summary>
        /// Claims the unlocked steps of a raid (RaidClaimRewardsRequest f1, the raid's id), in the
        /// claiming player's own session: experience, kamas and items, step by step. Nothing of a
        /// raid other than the one already claimed this week.
        /// </summary>
        public static async Task<(ClaimResult Result, List<int> Rewards)> ClaimAsync(NetworkStream stream, long characterId,
                                                                                       int raidId, DateTimeOffset now)
        {
            var none = new List<int>();
            if (GuildRaidCatalogue.Of(raidId) == null) return (ClaimResult.InvalidRaid, none);
            var claimed = Claimed(characterId, now);
            if (claimed is { } c && c.RaidId != raidId) return (ClaimResult.RewardsAlreadyClaimed, none);

            var unlocked = UnlockedBy(raidId, BestScores(characterId, now).TryGetValue(raidId, out long best) ? best : 0);
            if (unlocked.Count == 0) return (ClaimResult.NoRewardToClaim, none);
            var owed = unlocked.Where(id => claimed is not { } d || !d.Rewards.Contains(id)).ToList();
            if (owed.Count == 0) return (ClaimResult.RewardsAlreadyClaimed, none);

            using (var connection = Open())
            using (var transaction = connection.BeginTransaction())
            {
                foreach (int id in owed)
                {
                    var insert = connection.CreateCommand();
                    insert.Transaction = transaction;
                    insert.CommandText = "INSERT OR IGNORE INTO GuildRaidClaims (CharacterId, Week, RaidId, RewardId) VALUES ($c, $w, $r, $id);";
                    insert.Parameters.AddWithValue("$c", characterId);
                    insert.Parameters.AddWithValue("$w", WeekOf(now));
                    insert.Parameters.AddWithValue("$r", raidId);
                    insert.Parameters.AddWithValue("$id", id);
                    insert.ExecuteNonQuery();
                }
                transaction.Commit();
            }

            if (stream != null)
            {
                var steps = GuildRaidCatalogue.RewardsOf(raidId).Where(r => owed.Contains(r.Id)).ToList();
                long kamas = steps.Sum(r => r.Kamas), experience = steps.Sum(r => r.Experience);
                await CharacterRewards.GiveKamasAsync(stream, kamas);
                await CharacterRewards.GiveExperienceAsync(stream, experience);
                foreach (var step in steps)
                foreach (var item in step.Items)
                {
                    if (!await Equipment.GiveAsync(stream, item.Id, Math.Max(1, item.Quantity)))
                        Console.WriteLine($"[Raids] Item {item.Id} of frieze step {step.Id} could not be given.");
                }
                if (kamas > 0 || experience > 0) CharacterRewards.Save();
            }
            Console.WriteLine($"[Raids] {characterId} claims raid {raidId}'s steps {string.Join(",", owed)}.");
            return (ClaimResult.Done, owed);
        }
    }
}
