using System;
using System.Collections.Generic;
using System.Linq;
using Jondo.Unity.Server.Network;
using Microsoft.Data.Sqlite;

namespace Jondo.Unity.Server.Managers
{
    /// <summary>Take the full mount share, then the full guild share; convert only the recipient credits.</summary>
    public static class ExperienceSharing
    {
        public readonly record struct Split(long Character, long MountTaken, long Mount, long GuildTaken, long Guild);

        /// <summary>Giny/Stump's strict level-gap thresholds, with no custom guild multiplier.</summary>
        public static long Convert(long taken, int characterLevel, int recipientLevel)
        {
            long gap = (long)Math.Max(1, characterLevel) - Math.Max(1, recipientLevel);
            decimal percent = gap > 70 ? 1 : gap > 60 ? 1.5m : gap > 50 ? 2 : gap > 40 ? 3
                : gap > 30 ? 4 : gap > 20 ? 6 : gap > 10 ? 8 : 10;
            return (long)(Math.Max(0, taken) * percent / 100m);
        }

        public static Split Distribute(long experience, int level, int mountPercent, int mountLevel,
                                       int guildPercent, int guildLevel)
        {
            long remaining = Math.Max(0, experience);
            long mount = (long)(remaining * (decimal)Math.Clamp(mountPercent, 0, 90) / 100m);
            remaining -= mount;
            long guild = (long)(remaining * (decimal)Math.Clamp(guildPercent, 0, 90) / 100m);
            return new Split(remaining - guild, mount, Convert(mount, level, mountLevel),
                             guild, Convert(guild, level, guildLevel));
        }

        private static SqliteConnection Open()
        {
            var connection = GuildStore.Open();
            using var command = connection.CreateCommand();
            command.CommandText = @"CREATE TABLE IF NOT EXISTS CharacterMountExperienceShare (
                CharacterId INTEGER PRIMARY KEY, Percent INTEGER NOT NULL DEFAULT 0);";
            command.ExecuteNonQuery();
            return connection;
        }

        public static int MountPercent(long characterId)
        {
            using var connection = Open();
            using var command = connection.CreateCommand();
            command.CommandText = "SELECT Percent FROM CharacterMountExperienceShare WHERE CharacterId = $c;";
            command.Parameters.AddWithValue("$c", characterId);
            return command.ExecuteScalar() is long percent ? (int)Math.Clamp(percent, 0, 90) : 0;
        }

        public static bool SetMountPercent(long characterId, int percent)
        {
            if (characterId <= 0 || percent < 0 || percent > 90) return false;
            using var connection = Open();
            using var command = connection.CreateCommand();
            command.CommandText = @"INSERT INTO CharacterMountExperienceShare (CharacterId, Percent) VALUES ($c, $p)
                ON CONFLICT(CharacterId) DO UPDATE SET Percent = excluded.Percent;";
            command.Parameters.AddWithValue("$c", characterId);
            command.Parameters.AddWithValue("$p", percent);
            command.ExecuteNonQuery();
            return true;
        }

        internal sealed class Plan
        {
            internal GameSession Session;
            internal Equipment.Item? Mount;
            internal List<Equipment.ItemEffect>? MountEffects;
            internal long GuildId;
            internal Split Amounts;
            internal bool Applied;
            internal bool Notified;
        }

        /// <summary>Read all recipient levels before any credit can level up a shared guild.</summary>
        internal static Plan Prepare(GameSession session, long experience)
        {
            var plan = new Plan { Session = session, Amounts = new Split(Math.Max(0, experience), 0, 0, 0, 0) };
            if (experience <= 0) return plan;
            using var context = SessionContext.Push(session);
            // In this client, wearing the real ride in slot 8 is riding it. Pets/petsmounts do not qualify.
            plan.Mount = Equipment.All.FirstOrDefault(i => i.Position == Mounts.Slot && Rides.IsRide(i.Template));
            int mountPercent = plan.Mount == null ? 0 : MountPercent(session.State.CharacterId);
            var member = GuildStore.MemberOf(session.State.CharacterId);
            var guild = member == null ? null : GuildStore.GuildOf(session.State.CharacterId);
            plan.GuildId = guild?.Id ?? 0;
            plan.Amounts = Distribute(experience, session.State.CharacterLevel, mountPercent,
                plan.Mount == null ? 1 : Rides.LevelOf(plan.Mount.Effects),
                guild == null ? 0 : member!.ExperiencePercent, guild?.Level ?? 1);
            if (plan.Mount != null && plan.Amounts.Mount > 0)
            {
                plan.MountEffects = Rides.WithExperience(plan.Mount.Template, plan.Mount.Effects, plan.Amounts.Mount);
                long actual = Rides.ExperienceOf(plan.MountEffects) - Rides.ExperienceOf(plan.Mount.Effects);
                plan.Amounts = plan.Amounts with { Mount = Math.Max(0, actual) };
            }
            return plan;
        }

        private static readonly object CreditGate = new();

        /// <summary>Credit all recipients atomically, once per plan, before sending any result screen.</summary>
        internal static void Credit(IEnumerable<Plan> plans)
        {
            lock (CreditGate)
            {
                var pending = plans.Where(p => !p.Applied).Distinct().ToList();
                if (pending.Count == 0) return;
                if (pending.All(p => p.Amounts.Mount == 0 && p.Amounts.Guild == 0))
                {
                    foreach (var plan in pending) plan.Applied = true;
                    return;
                }
                using var connection = Open();
                using var transaction = connection.BeginTransaction();
                var guilds = new HashSet<long>();
                foreach (var plan in pending)
                {
                    using var command = connection.CreateCommand();
                    command.Transaction = transaction;
                    command.Parameters.AddWithValue("$c", plan.Session.State.CharacterId);
                    if (plan.Amounts.Mount > 0 && plan.Mount != null && plan.MountEffects != null)
                    {
                        command.CommandText = @"UPDATE CharacterItems SET Effects = $e WHERE CharacterId = $c
                            AND Uid = $u AND Gid = $gid AND Position = $slot;";
                        command.Parameters.AddWithValue("$e", Forgemagic.Serialize(plan.MountEffects));
                        command.Parameters.AddWithValue("$u", plan.Mount.Uid);
                        command.Parameters.AddWithValue("$gid", plan.Mount.Template);
                        command.Parameters.AddWithValue("$slot", Mounts.Slot);
                        if (command.ExecuteNonQuery() != 1) throw new InvalidOperationException("The donated mount is no longer equipped.");
                    }
                    if (plan.Amounts.Guild > 0)
                    {
                        command.CommandText = @"UPDATE GuildMembers SET Experience = Experience + $x
                            WHERE CharacterId = $c AND GuildId = $g;";
                        command.Parameters.AddWithValue("$g", plan.GuildId);
                        command.Parameters.AddWithValue("$x", plan.Amounts.Guild);
                        if (command.ExecuteNonQuery() != 1) throw new InvalidOperationException("The donor is no longer in the guild.");
                        command.CommandText = "UPDATE Guilds SET Experience = Experience + $x WHERE Id = $g;";
                        if (command.ExecuteNonQuery() != 1) throw new InvalidOperationException("The donated guild no longer exists.");
                        guilds.Add(plan.GuildId);
                    }
                }
                foreach (long guild in guilds)
                {
                    using var command = connection.CreateCommand();
                    command.Transaction = transaction;
                    command.Parameters.AddWithValue("$g", guild);
                    command.CommandText = "SELECT Experience FROM Guilds WHERE Id = $g;";
                    long experience = System.Convert.ToInt64(command.ExecuteScalar());
                    command.Parameters.AddWithValue("$l", GuildLevels.LevelFor(experience));
                    command.CommandText = "UPDATE Guilds SET Level = $l WHERE Id = $g AND Level < $l;";
                    command.ExecuteNonQuery();
                }
                transaction.Commit();
                foreach (var plan in pending)
                {
                    plan.Applied = true;
                    if (plan.Amounts.Mount <= 0 || plan.Mount == null || plan.MountEffects == null) continue;
                    using var context = SessionContext.Push(plan.Session);
                    plan.Mount.Effects.Clear();
                    plan.Mount.Effects.AddRange(plan.MountEffects);
                    Equipment.RememberWorn(plan.Mount.Uid, plan.Mount.Position, plan.Mount.Effects);
                    var legacy = plan.Session.State.GetInventoryItem(plan.Mount.Uid);
                    if (legacy != null)
                    {
                        legacy.RawEffects = Forgemagic.Serialize(plan.Mount.Effects);
                        legacy.Effects.Clear();
                        foreach (var effect in plan.Mount.Effects)
                            legacy.Effects[effect.Effect] = (int)Math.Clamp(effect.Value, int.MinValue, int.MaxValue);
                    }
                }
            }
        }
    }
}
