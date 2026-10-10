using System;
using System.Collections.Generic;
using Jondo.Unity.World.Content;
using Microsoft.Data.Sqlite;

namespace Jondo.Unity.Server.Managers
{
    /// <summary>
    /// The Gigalodón's salt deposits: crystals on its floors that give a salt of the depths each
    /// to the raid's shared pool, and grow back in five to ten minutes (the guides).
    /// </summary>
    /// <remarks>
    /// The client names the pieces: interactive type 464 is "Sal de las profundidades" and skill
    /// 470 is "Recolectar sal de las profundidades", of job 1 (anyone), giving item 32464 with the
    /// pickaxe's animation. Which graphic stands for them is the server's to say, and no capture
    /// enters a raid: they are graphic 6943, a crystal cluster whose animation cracks it open --
    /// what a pickaxe does -- and the one interactive of the floors spread over them in numbers,
    /// 34 on 25 maps of floors -1 to -5 (two more stand on cell 0, which the world's resources skip
    /// too), none on Willorque's, which has no light to feed.
    ///
    /// The salt is the raid's, not the gatherer's: it goes into the pool the luminomachines burn,
    /// the same one the monsters' salt goes into.
    /// </remarks>
    public static class GuildRaidSaltDeposits
    {
        /// <summary>The deposits' graphic in the client's maps.</summary>
        public const int DepositGfx = 6943;

        /// <summary>The client's interactive type "Sal de las profundidades".</summary>
        public const int DepositType = 464;

        /// <summary>The client's skill "Recolectar sal de las profundidades".</summary>
        public const int GatherSkill = 470;

        /// <summary>What a deposit gives, and how soon it is back (the guides).</summary>
        public const int SaltPerDeposit = 1;
        public static readonly TimeSpan FewestRegrowth = TimeSpan.FromMinutes(5);
        public static readonly TimeSpan MostRegrowth = TimeSpan.FromMinutes(10);

        /// <summary>The deposits on the abyss's floors, as gatherable resources.</summary>
        public static List<Resources.Resource> Read()
        {
            var deposits = new List<Resources.Resource>();
            var abyss = Raids.Of(Raids.Gigalodon);
            if (abyss == null) return deposits;

            var (job, item, level) = SkillOf(GatherSkill);
            foreach (int subArea in abyss.Floors)
            foreach (long mapId in DatabaseManager.MapsOfSubArea(subArea))
            foreach (var element in Interactives.ElementsOf(mapId))
            {
                if (element.Gfx != DepositGfx || element.Cell == 0) continue;
                deposits.Add(new Resources.Resource
                {
                    MapId = mapId,
                    ElementId = element.Id,
                    Cell = element.Cell,
                    Gfx = element.Gfx,
                    Type = DepositType,
                    SkillId = GatherSkill,
                    JobId = job,
                    ItemId = item,
                    LevelMin = level,
                });
            }
            return deposits;
        }

        /// <summary>A skill's job, the item it gathers and the level it asks, from the client's catalogue.</summary>
        private static (int Job, int Item, int Level) SkillOf(int skill)
        {
            try
            {
                using var connection = new SqliteConnection(DatabaseManager.WorldConnectionString);
                connection.Open();
                using var command = connection.CreateCommand();
                command.CommandText = "SELECT ParentJobId, GatheredResourceItem, LevelMin FROM Skills WHERE Id = $id;";
                command.Parameters.AddWithValue("$id", skill);
                using var reader = command.ExecuteReader();
                if (reader.Read()) return (reader.GetInt32(0), reader.GetInt32(1), Math.Max(1, reader.GetInt32(2)));
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[Raids] Could not read skill {skill}: {ex.Message}");
            }
            return (1, Luminomachine.SaltItem, 1);
        }

        public static bool IsDeposit(Resources.Resource resource)
            => resource != null && resource.Type == DepositType && resource.SkillId == GatherSkill;

        /// <summary>How long a gathered deposit takes to grow back: five to ten minutes.</summary>
        public static TimeSpan Regrowth(Random dice)
            => FewestRegrowth + TimeSpan.FromSeconds(dice.Next(0, (int)(MostRegrowth - FewestRegrowth).TotalSeconds + 1));

        /// <summary>A deposit's salt into the pool of the gatherer's raid. False outside one.</summary>
        public static bool IntoThePool(long characterId, int salt)
        {
            var raid = GuildRaidManager.RaidOf(characterId);
            if (raid == null || raid.RaidId != Raids.Gigalodon || salt <= 0) return false;
            raid.Add(RaidInstance.SaltVariable, salt);
            return true;
        }
    }
}
