using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using Jondo.Unity.Launcher;
using Microsoft.Data.Sqlite;

namespace Jondo.Unity.Server.Managers
{
    /// <summary>
    /// A guild's weekly activity: the tier it chose ("franja de actividad"), the activity points it
    /// gathered this week, and each member's tokens ("gremichas") of the week.
    /// </summary>
    /// <remarks>
    /// THE TIERS are the client's (datos/guild_missions_*.json, extract_guild_missions.py): five, each
    /// with four milestones of activity points and the guild experience each milestone gives.
    ///
    /// THE RULES are the DofusPourLesNoobs guilds guide's, and the captures agree where they reach:
    ///   - only the leader and the ranks with the right "Gestionar la franja de actividad" choose it;
    ///   - the first choice holds at once; a change afterwards holds from the next week, the tier
    ///     itself staying from week to week;
    ///   - the points start again every week, on the weekly reset (<see cref="GuildStore.WeekOf"/>),
    ///     and stop at the tier's last milestone;
    ///   - a milestone reached gives the guild its experience;
    ///   - a member's tokens stop at 250 a week, 300 once the guild is level 13;
    ///   - a contribution of 10,000 kamas gives 10 tokens and 100 activity points (the capture's jff
    ///     goes from {100, 10} to {200, 20} with it).
    ///
    /// Until the missions are done, contributions are the only activity there is.
    /// </remarks>
    public static class GuildActivity
    {
        public sealed record Milestone(int Id, int Level, long ActivityPoints, int Experience, int AcknowledgmentPoints);

        public sealed record Tier(int Id, string Name, int Level, int RecommendedPlayers, int RerollCost,
                                  IReadOnlyList<Milestone> Milestones)
        {
            /// <summary>Where the week's points stop: the last milestone.</summary>
            public long MaxPoints => Milestones.Count == 0 ? 0 : Milestones.Max(m => m.ActivityPoints);
        }

        private sealed record Document(List<Tier> Activities);

        private static Document _data;
        private static readonly object _lock = new();

        private static Document Data
        {
            get
            {
                if (_data != null) return _data;
                lock (_lock)
                {
                    _data ??= Load(Paths.GuildMissionsJson);
                    return _data;
                }
            }
        }

        private static Document Load(string path)
        {
            if (!File.Exists(path))
            {
                Console.WriteLine($"[Guild] {path} not found: no activity tier can be chosen.");
                return new Document(new());
            }
            var options = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };
            var document = JsonSerializer.Deserialize<Document>(File.ReadAllText(path), options);
            return new Document(document?.Activities ?? new());
        }

        public static IReadOnlyList<Tier> Tiers => Data.Activities;

        public static Tier TierOf(int id) => Data.Activities.FirstOrDefault(t => t.Id == id);

        /// <summary>The client's guild right 40, "Gestionar la franja de actividad".</summary>
        public const int ManageActivityRight = 40;

        /// <summary>A member's tokens of the week stop here...</summary>
        public const int TokenCap = 250;

        /// <summary>...and here once the guild has reached <see cref="HigherCapLevel"/>.</summary>
        public const int HigherTokenCap = 300;
        public const int HigherCapLevel = 13;

        /// <summary>What a contribution gives besides its guild kamas.</summary>
        public const int ContributionTokens = 10;
        public const int ContributionActivityPoints = 100;

        public static int TokenCapOf(GuildStore.Guild guild)
            => guild != null && guild.Level >= HigherCapLevel ? HigherTokenCap : TokenCap;

        /// <summary>A guild's week: its tier, the one waiting for next week, its points, its preferences.</summary>
        public sealed record Week(long GuildId, string Name, int ActivityId, int NextActivityId, long Points,
                                  byte[] Preferences, byte[] NextPreferences)
        {
            public bool HasActivity => ActivityId > 0;

            /// <summary>The tier waiting for next week, when it differs from this week's.</summary>
            public int PendingActivityId => NextActivityId > 0 && NextActivityId != ActivityId ? NextActivityId : 0;
        }

        /// <summary>
        /// A guild's week as it stands: the stored one, or, the first time it is asked, the last
        /// one's tier and preferences -- the waiting ones if a change was waiting -- with no points.
        /// </summary>
        public static Week Of(long guildId, DateTimeOffset when)
        {
            string week = GuildStore.WeekOf(when);
            using var connection = GuildStore.Open();
            var query = connection.CreateCommand();
            query.CommandText = @"
                SELECT Week, ActivityId, NextActivityId, Points, Preferences, NextPreferences
                FROM GuildActivityWeeks WHERE GuildId = $g AND Week <= $w
                ORDER BY Week DESC LIMIT 1;";
            query.Parameters.AddWithValue("$g", guildId);
            query.Parameters.AddWithValue("$w", week);
            using var reader = query.ExecuteReader();
            if (!reader.Read()) return new Week(guildId, week, 0, 0, 0, Array.Empty<byte>(), null);

            int activity = reader.GetInt32(1), next = reader.GetInt32(2);
            byte[] preferences = reader.IsDBNull(4) ? Array.Empty<byte>() : (byte[])reader[4];
            byte[] nextPreferences = reader.IsDBNull(5) ? null : (byte[])reader[5];
            if (reader.GetString(0) == week)
                return new Week(guildId, week, activity, next, reader.GetInt64(3), preferences, nextPreferences);
            return new Week(guildId, week, next > 0 ? next : activity, 0, 0, nextPreferences ?? preferences, null);
        }

        private static void Save(Week week)
        {
            using var connection = GuildStore.Open();
            var save = connection.CreateCommand();
            save.CommandText = @"
                INSERT INTO GuildActivityWeeks (GuildId, Week, ActivityId, NextActivityId, Points, Preferences, NextPreferences)
                VALUES ($g, $w, $a, $n, $p, $pr, $np)
                ON CONFLICT (GuildId, Week) DO UPDATE SET
                    ActivityId = $a, NextActivityId = $n, Points = $p, Preferences = $pr, NextPreferences = $np;";
            save.Parameters.AddWithValue("$g", week.GuildId);
            save.Parameters.AddWithValue("$w", week.Name);
            save.Parameters.AddWithValue("$a", week.ActivityId);
            save.Parameters.AddWithValue("$n", week.NextActivityId);
            save.Parameters.AddWithValue("$p", week.Points);
            save.Parameters.AddWithValue("$pr", week.Preferences ?? Array.Empty<byte>());
            save.Parameters.AddWithValue("$np", (object)week.NextPreferences ?? DBNull.Value);
            save.ExecuteNonQuery();
        }

        /// <summary>
        /// The guild chooses a tier, and its preferences with it: at once if it had none, from next
        /// week otherwise (choosing this week's again takes a waiting change back). Null when the
        /// tier does not exist.
        /// </summary>
        public static Week Choose(long guildId, int activityId, byte[] preferences, DateTimeOffset when)
        {
            if (TierOf(activityId) == null) return null;
            preferences ??= Array.Empty<byte>();
            var week = Of(guildId, when);
            week = !week.HasActivity
                ? week with { ActivityId = activityId, NextActivityId = 0, Preferences = preferences, NextPreferences = null }
                : week with
                {
                    NextActivityId = activityId == week.ActivityId ? 0 : activityId,
                    NextPreferences = preferences.AsSpan().SequenceEqual(week.Preferences) ? null : preferences,
                };
            Save(week);
            return week;
        }

        /// <summary>A member's tokens of the week.</summary>
        public static int TokensOf(long characterId, DateTimeOffset when)
        {
            using var connection = GuildStore.Open();
            var query = connection.CreateCommand();
            query.CommandText = "SELECT Tokens FROM GuildTokenWeeks WHERE CharacterId = $c AND Week = $w;";
            query.Parameters.AddWithValue("$c", characterId);
            query.Parameters.AddWithValue("$w", GuildStore.WeekOf(when));
            var value = query.ExecuteScalar();
            return value == null || value is DBNull ? 0 : Convert.ToInt32(value);
        }

        /// <summary>What an activity gave: tokens to the member, points and experience to the guild.</summary>
        public sealed record Credited(int Tokens, long Points, long Experience, int Level);

        /// <summary>
        /// A member's activity: his tokens, up to the week's cap, and the guild's points, up to the
        /// tier's last milestone, each milestone passed giving the guild its experience. A guild with
        /// no tier gathers no points.
        /// </summary>
        public static Credited Credit(GuildStore.Guild guild, long characterId, int tokens, long points, DateTimeOffset when)
        {
            if (guild == null) return new Credited(0, 0, 0, 0);
            string weekName = GuildStore.WeekOf(when);

            int had = TokensOf(characterId, when);
            int given = Math.Clamp(TokenCapOf(guild) - had, 0, Math.Max(0, tokens));
            if (given > 0)
            {
                using var connection = GuildStore.Open();
                var add = connection.CreateCommand();
                add.CommandText = @"
                    INSERT INTO GuildTokenWeeks (CharacterId, Week, Tokens) VALUES ($c, $w, $t)
                    ON CONFLICT (CharacterId, Week) DO UPDATE SET Tokens = Tokens + $t;";
                add.Parameters.AddWithValue("$c", characterId);
                add.Parameters.AddWithValue("$w", weekName);
                add.Parameters.AddWithValue("$t", given);
                add.ExecuteNonQuery();
            }

            var week = Of(guild.Id, when);
            var tier = TierOf(week.ActivityId);
            if (tier == null || points <= 0) return new Credited(given, 0, 0, guild.Level);

            long before = week.Points;
            long after = Math.Min(tier.MaxPoints, before + points);
            Save(week with { Points = after });

            int experience = tier.Milestones.Where(m => m.ActivityPoints > before && m.ActivityPoints <= after)
                                            .Sum(m => m.Experience);
            int level = experience > 0 ? GuildStore.AddGuildExperience(guild.Id, experience) : guild.Level;
            if (experience > 0)
                Program.LogDebug($"[Guild] «{guild.Name}» reaches a milestone of tier {tier.Level}: +{experience} experience, level {level}.");
            return new Credited(given, after - before, experience, level);
        }
    }
}
