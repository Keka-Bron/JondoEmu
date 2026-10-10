using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Sockets;
using System.Threading.Tasks;
using Microsoft.Data.Sqlite;

namespace Jondo.Unity.Server.Managers
{
    /// <summary>
    /// The guild raids' weekly ladder and its rewards: who ranks where (GuildStore keeps each
    /// guild's best of the week), which reward a place earns -- the client's guildraidsladdersrewards:
    /// one place, a range of places, or a share of the guilds that scored -- and handing it out once
    /// the week is over.
    /// </summary>
    /// <remarks>
    /// A place's reward is chosen the way the client's ladder tab chooses it (RankIsValid): a single
    /// position must match, two positions are a range, and with none the place must fall within the
    /// percentage of the guilds that scored. The rewards are handed out lazily: the guild experience
    /// the first time anyone asks after the week closed, and each player's items when he next comes
    /// into the world, since items need his session. Ornaments and titles are not handed out: the
    /// wardrobe already offers every ornament to everybody, and titles have no store yet.
    /// </remarks>
    public static class GuildRaidLadder
    {
        /// <summary>The reward a place in a raid's ladder earns, or null.</summary>
        public static GuildRaidCatalogue.LadderReward RewardFor(int raidId, int place, int scored)
        {
            if (place <= 0 || scored <= 0) return null;
            foreach (var reward in GuildRaidCatalogue.LadderRewardsOf(raidId))
            {
                var positions = reward.Positions ?? Array.Empty<int>();
                bool fits = positions.Count switch
                {
                    1 => place == positions[0],
                    2 => place >= positions[0] && place <= positions[1],
                    _ => reward.Percentage != -1 && place * 100f / scored <= reward.Percentage,
                };
                if (fits) return reward;
            }
            return null;
        }

        private static SqliteConnection Open()
        {
            var connection = new SqliteConnection(GuildStore.ConnectionStringOverride ?? DatabaseManager.WorldConnectionString);
            connection.Open();
            var create = connection.CreateCommand();
            create.CommandText = @"
                CREATE TABLE IF NOT EXISTS GuildRaidLadderPaid (
                    Who INTEGER NOT NULL,
                    IsGuild INTEGER NOT NULL,
                    Week TEXT NOT NULL,
                    RaidId INTEGER NOT NULL,
                    PRIMARY KEY (Who, IsGuild, Week, RaidId)
                );";
            create.ExecuteNonQuery();
            return connection;
        }

        /// <summary>Marks something paid; false when it already was.</summary>
        private static bool MarkPaid(long who, bool isGuild, string week, int raidId)
        {
            using var connection = Open();
            var insert = connection.CreateCommand();
            insert.CommandText = "INSERT OR IGNORE INTO GuildRaidLadderPaid (Who, IsGuild, Week, RaidId) VALUES ($w, $g, $k, $r);";
            insert.Parameters.AddWithValue("$w", who);
            insert.Parameters.AddWithValue("$g", isGuild ? 1 : 0);
            insert.Parameters.AddWithValue("$k", week);
            insert.Parameters.AddWithValue("$r", raidId);
            return insert.ExecuteNonQuery() > 0;
        }

        /// <summary>A ladder reward handed to a player, for the chat line that announces it.</summary>
        public sealed record Paid(int RaidId, GuildRaidCatalogue.LadderReward Reward);

        /// <summary>
        /// Hands a player the rewards of last week's ladder that are his: for each raid he scored in
        /// last week, his guild's place. The guild's experience goes once, with the first of its
        /// members to come by. Returns what was handed, for the client's "obtained" line.
        /// </summary>
        public static async Task<List<Paid>> DeliverAsync(NetworkStream stream, long characterId, DateTimeOffset now)
        {
            var paid = new List<Paid>();
            var guild = GuildStore.GuildOf(characterId);
            if (guild == null) return paid;

            var lastWeek = now.AddDays(-7);
            string week = GuildStore.WeekOf(lastWeek);
            foreach (var (raidId, best) in GuildRaidRewards.BestScores(characterId, lastWeek))
            {
                if (best <= 0) continue;
                var row = GuildStore.LadderRowOf(guild.Id, raidId, lastWeek);
                var reward = row == null ? null : RewardFor(raidId, row.Place, GuildStore.LadderCount(raidId, lastWeek));
                if (reward == null || !MarkPaid(characterId, false, week, raidId)) continue;

                if (MarkPaid(guild.Id, true, week, raidId)) GuildStore.AddGuildExperience(guild.Id, reward.GuildExperience);
                if (stream != null)
                {
                    foreach (var item in reward.Items)
                    {
                        if (!await Equipment.GiveAsync(stream, item.Id, Math.Max(1, item.Quantity)))
                            Console.WriteLine($"[Raids] Ladder item {item.Id} of reward {reward.Id} could not be given.");
                    }
                }
                Console.WriteLine($"[Raids] {characterId} gets ladder reward {reward.Id}: place {row.Place} of raid {raidId}, week {week}.");
                paid.Add(new Paid(raidId, reward));
            }
            return paid;
        }
    }
}
