using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using Jondo.Unity.Launcher;

namespace Jondo.Unity.Server.Managers
{
    /// <summary>
    /// The guild raids as the client's own data defines them (datos/guild_raids_*.json, extracted
    /// from the client bundles by extract_guild_raids.py): what each raid costs, how long it runs,
    /// how many may go, its groups, its goals, the weekly frieze of rewards and the ladder's.
    /// </summary>
    /// <remarks>
    /// The client indexes every raid screen by these ids -- 1 the Eternal Gardens Sanctuary, 2 the
    /// Gigalodon Abyss -- so the server takes them from here and from nowhere else.
    /// </remarks>
    public static class GuildRaidCatalogue
    {
        public sealed record Variable(int Id, string Icon, string Name);

        public sealed record Raid(int Id, string Name, string Description, int DurationMinutes, int PlayerHealth,
                                  int MinPlayers, int MaxPlayers, IReadOnlyList<int> Groups, IReadOnlyList<int> Goals,
                                  IReadOnlyList<Variable> Variables, int MaxScore, int Price, bool CanFinish,
                                  bool CanRestart, int Type)
        {
            public TimeSpan Duration => TimeSpan.FromMinutes(DurationMinutes);
        }

        public sealed record Group(int Id, int RaidId, string Name, int MinPlayers, int MaxPlayers);

        /// <summary>
        /// A raid's goal. <see cref="UnlocksFloor"/> is the floor it opens, as its name says ("para
        /// acceder a la planta -2"); <see cref="Damage"/> the damage it asks for ("Ocasionar 10.000 de
        /// daño al Gigalodón"). <see cref="Monsters"/> are the monsters its name names -- "Vencer a la Reina
        /// Escarlata", "Venez à bout de la Mureine" --, matched by name in Spanish and French when
        /// the data was extracted; the client's data does not link them otherwise.
        /// </summary>
        public sealed record Goal(int Id, int RaidId, string Name, int Value, IReadOnlyList<int> RequisiteForDisplay,
                                  bool ImpactProgress, int Score, IReadOnlyList<int> Monsters = null, int UnlocksFloor = 0,
                                  long Damage = 0);

        public sealed record ItemQuantity(int Id, int Quantity);

        /// <summary>A step of a raid's weekly frieze: reached at this score, worth these rewards.</summary>
        public sealed record RewardStep(int Id, int RaidId, int Order, int Score, long Kamas, long Experience,
                                        IReadOnlyList<ItemQuantity> Items);

        public sealed record LadderReward(int Id, int RaidId, int Order, IReadOnlyList<int> Positions, int Percentage,
                                          IReadOnlyList<int> Ornaments, IReadOnlyList<int> Titles, int GuildExperience,
                                          IReadOnlyList<ItemQuantity> Items);

        private sealed record Document(List<Raid> Raids, List<Group> Groups, List<Goal> Goals,
                                       List<RewardStep> Rewards, List<LadderReward> LadderRewards);

        private static Document _data;
        private static readonly object _lock = new();

        private static Document Data
        {
            get
            {
                if (_data != null) return _data;
                lock (_lock)
                {
                    _data ??= Load(Paths.GuildRaidsJson);
                    return _data;
                }
            }
        }

        private static Document Load(string path)
        {
            var empty = new Document(new(), new(), new(), new(), new());
            if (!File.Exists(path))
            {
                Console.WriteLine($"[Raids] {path} not found: no guild raid can be bought or run.");
                return empty;
            }
            var options = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };
            var document = JsonSerializer.Deserialize<Document>(File.ReadAllText(path), options) ?? empty;
            return document with
            {
                Raids = document.Raids ?? new(),
                Groups = document.Groups ?? new(),
                Goals = document.Goals ?? new(),
                Rewards = document.Rewards ?? new(),
                LadderRewards = document.LadderRewards ?? new(),
            };
        }

        public static IReadOnlyList<Raid> Raids => Data.Raids;

        public static Raid Of(int raidId) => Data.Raids.FirstOrDefault(r => r.Id == raidId);

        public static Group GroupOf(int groupId) => Data.Groups.FirstOrDefault(g => g.Id == groupId);

        public static IReadOnlyList<Goal> GoalsOf(int raidId) => Data.Goals.Where(g => g.RaidId == raidId).ToList();

        /// <summary>The frieze of a raid, in its order.</summary>
        public static IReadOnlyList<RewardStep> RewardsOf(int raidId)
            => Data.Rewards.Where(r => r.RaidId == raidId).OrderBy(r => r.Order).ToList();

        public static IReadOnlyList<LadderReward> LadderRewardsOf(int raidId)
            => Data.LadderRewards.Where(r => r.RaidId == raidId).OrderBy(r => r.Order).ToList();
    }
}
