using System;
using System.Collections.Generic;
using System.Linq;

namespace Jondo.Unity.Server.Managers
{
    /// <summary>
    /// The guild chest: where it stands, its tabs, and who may look, put in and take out.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Measured on "Interactivos varios/entrar en banco bonta-abrir cofre gremio-usarlo-abrir cofre
    /// personal del banco.pcapng", frames 26-69: the chest is element 524415 of the Bonta bank
    /// (map 217059328), graphic 70671, declared with type 388 and skill 184, "Utilizar". The same
    /// graphic stands in 24 maps of the client's data -- the banks and a few more -- and every one
    /// of them is a guild chest here.
    /// </para>
    /// <para>
    /// It is the GUILD's: every member opens the same one, whichever bank it is. Its content is
    /// kept per guild and per tab in <see cref="StorageStacks"/>. A new guild has one tab: that is
    /// what the ivl of every guild founded in the captures says ("Gremio/muchas acciones en mi
    /// gremio como lider", frame 10); the others come with the guild's level (<see cref="TabsOf"/>).
    /// </para>
    /// <para>
    /// The rights are the ranks' own (<see cref="GuildStore.Rank.Rights"/>), three per tab, and
    /// they are the client's table (GuildRightsDataRoot) and the ivl alike: tab 1 is 8 look, 24
    /// put in, 23 take out; tab 2 31, 30, 29; tab 3 32, 33, 34; tab 4 35, 36, 37. In the capture
    /// the player -- rank 2 of Guild-A, whose rights the 9 August login lists as 1 2 5 6 7 8 13 14
    /// 15 23 24 25 26 -- opens tab 1, puts in and takes out, and is refused tab 2 (jll { f2: 2 }
    /// answered lqn 1/654 "No tienes el derecho de consultar el cofre de gremio", frames 38-39).
    /// </para>
    /// </remarks>
    public static class GuildChests
    {
        public const int Gfx = 70671;
        public const int Type = 388;
        public const int UseSkill = 184;

        /// <summary>kbk's f1: the guild chest's kind among the storages, 22.</summary>
        public const int WindowKind = 22;

        /// <summary>ivl's f5 for every tab: 76. Unknown meaning.</summary>
        public const int TabF5 = 76;

        /// <summary>The client's texts for a refusal (lqn type 1): look, put in, take out, no guild, that type.</summary>
        public const int NoRightToLook = 654;
        public const int NoRightToPut = 655;
        public const int NoRightToTake = 656;
        public const int NoGuild = 659;
        public const int TypeRefused = 661;

        /// <summary>One tab of the chest.</summary>
        public sealed class Tab
        {
            public int Index { get; init; }
            public int ConsultRight { get; init; }
            public int DepositRight { get; init; }
            public int WithdrawRight { get; init; }
            public string NameKey { get; init; } = "";
            public IReadOnlyList<long> ItemTypes { get; init; } = Array.Empty<long>();
        }

        /// <summary>
        /// The item types a tab takes: 1 to 334 but for 26 numbers, the list of "Gremio/muchas
        /// acciones", frame 10. The 26 left out are ids the client has no item type for, and every
        /// one of the client's 239 types is in: in practice the tab takes anything.
        /// </summary>
        private static readonly IReadOnlyList<long> AllTypes = BuildAllTypes();

        private static IReadOnlyList<long> BuildAllTypes()
        {
            var missing = new HashSet<long> { 67, 101, 117, 120, 135, 170, 191, 193, 194, 204, 208, 210, 224,
                                              227, 235, 237, 239, 257, 263, 270, 285, 290, 292, 295, 296, 320 };
            var types = new List<long>();
            for (long t = 1; t <= 334; t++)
            {
                if (!missing.Contains(t)) types.Add(t);
            }
            return types;
        }

        /// <summary>
        /// The four tabs a guild can have, with their rights and default names. The names are the
        /// captures' for the first three (tab 1 "guild.chest.tab.4.name", 2 ".2.", 3 ".1."); the
        /// fourth's is the one key left, an inference.
        /// </summary>
        public static readonly IReadOnlyList<Tab> Tabs = new[]
        {
            new Tab { Index = 1, ConsultRight = 8,  DepositRight = 24, WithdrawRight = 23, NameKey = "guild.chest.tab.4.name", ItemTypes = AllTypes },
            new Tab { Index = 2, ConsultRight = 31, DepositRight = 30, WithdrawRight = 29, NameKey = "guild.chest.tab.2.name", ItemTypes = AllTypes },
            new Tab { Index = 3, ConsultRight = 32, DepositRight = 33, WithdrawRight = 34, NameKey = "guild.chest.tab.1.name", ItemTypes = AllTypes },
            new Tab { Index = 4, ConsultRight = 35, DepositRight = 36, WithdrawRight = 37, NameKey = "guild.chest.tab.3.name", ItemTypes = AllTypes },
        };

        /// <summary>
        /// How many tabs a guild has: the founding one, and one more for each «Pestaña de cofre» its
        /// level has reached -- the client's GuildLevelRewardData, picto "guildBank", at levels 3, 7
        /// and 11 (datos/guild_level_rewards_*.json, tools/extract_guild_level_rewards.py).
        /// </summary>
        public static int TabsOf(long guildId)
        {
            int level;
            try { level = GuildStore.ById(guildId)?.Level ?? 1; }
            catch { level = 1; }
            return TabsAtLevel(level);
        }

        /// <summary>The tabs a guild of this level has, four at most.</summary>
        public static int TabsAtLevel(int level)
            => Math.Min(Tabs.Count, 1 + TabLevels.Count(at => at <= level));

        /// <summary>The levels a chest tab comes at, from the client's guild level rewards.</summary>
        private static IReadOnlyList<int> TabLevels => _tabLevels ??= ReadTabLevels();
        private static IReadOnlyList<int>? _tabLevels;

        private static IReadOnlyList<int> ReadTabLevels()
        {
            string path = Jondo.Unity.Launcher.Paths.Resolve("guild_level_rewards_3.6.10.11.json");
            var levels = new List<int>();
            try
            {
                if (!System.IO.File.Exists(path)) return levels;
                using var doc = System.Text.Json.JsonDocument.Parse(System.IO.File.ReadAllText(path));
                foreach (var reward in doc.RootElement.GetProperty("rewards").EnumerateArray())
                {
                    if (reward.GetProperty("picto").GetString() == "guildBank")
                        levels.Add(reward.GetProperty("level").GetInt32());
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[Guild chest] Could not read the guild level rewards: {ex.Message}");
            }
            return levels;
        }

        /// <summary>The tabs of this guild, in order.</summary>
        public static IReadOnlyList<Tab> TabsOfGuild(long guildId) => Tabs.Take(TabsOf(guildId)).ToList();

        public static Tab? TabOf(long guildId, int index)
            => index >= 1 && index <= TabsOf(guildId) ? Tabs[index - 1] : null;

        private static readonly Dictionary<long, List<Interactives.Element>> _byMap = new();

        public static int Count { get; private set; }

        public static void Initialize()
        {
            _byMap.Clear();
            Count = 0;
            foreach (long mapId in Interactives.MapIds)
            {
                List<Interactives.Element>? here = null;
                foreach (var element in Interactives.ElementsOf(mapId))
                {
                    if (element.Gfx != Gfx) continue;
                    (here ??= new List<Interactives.Element>()).Add(element);
                    Count++;
                }
                if (here != null) _byMap[mapId] = here;
            }
            Console.WriteLine($"[Guild chest] {Count} on {_byMap.Count} maps.");
        }

        /// <summary>The guild chests standing on this map.</summary>
        public static IReadOnlyList<Interactives.Element> On(long mapId)
            => _byMap.TryGetValue(mapId, out var found)
                ? found
                : (IReadOnlyList<Interactives.Element>)Array.Empty<Interactives.Element>();

        /// <summary>The rights a rank's list holds, read as the packed numbers it is.</summary>
        public static HashSet<int> RightsOf(byte[]? packed)
            => GuildRankRights.Decode(packed).ToHashSet();

        /// <summary>The rights this character has in his guild, by his rank; empty outside a guild.</summary>
        public static HashSet<int> RightsOf(long characterId)
        {
            var member = GuildStore.MemberOf(characterId);
            if (member == null) return new HashSet<int>();
            foreach (var rank in GuildStore.Ranks(member.GuildId))
            {
                if (rank.Id == member.Rank) return RightsOf(rank.Rights);
            }
            return new HashSet<int>();
        }
    }
}
