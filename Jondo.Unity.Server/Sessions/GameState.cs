using Jondo.Unity.Launcher;
using System;
using System.Collections.Generic;

namespace Jondo.Unity.Server
{
    /// <summary>
    /// The character's state, for the code that has not yet been migrated to sessions.
    ///
    /// It NO longer stores anything: each property forwards to the current connection's session. It is the façade
    /// docs/multiplayer.md asked for in order to migrate file by file instead of changing 263 places
    /// at once.
    ///
    /// Until now this class still had fields of its own WITH DEFAULT VALUES. The sessions
    /// refactor converted all the writers, but not all the readers, so the
    /// 263 remaining accesses received those values from months ago: level 40, and map
    /// 154010883. Hence a level 200 character saved in Amakna appeared in Incarnam
    /// —the default map of line 18— without anything warning. Neither of the two states
    /// was wrong: it is that there were two, and the code read the one nobody maintained any more.
    ///
    /// What is left to do is replace the uses with SessionContext.State file by file;
    /// meanwhile, everything points to the same place and cannot drift apart again.
    /// </summary>
    public static class GameState
    {
        private static SessionState S => Network.SessionContext.State;

        // Who he is
        public static long CharacterId { get => S.CharacterId; set => S.CharacterId = value; }
        public static string CharacterName { get => S.CharacterName; set => S.CharacterName = value; }
        public static int CharacterLevel { get => S.CharacterLevel; set => S.CharacterLevel = value; }
        public static int Breed { get => S.Breed; set => S.Breed = value; }
        public static int Sex { get => S.Sex; set => S.Sex = value; }
        public static byte[]? PlayerActorDetails { get => S.PlayerActorDetails; set => S.PlayerActorDetails = value; }
        public static byte[]? LookBytes { get => S.LookBytes; set => S.LookBytes = value; }

        // Where he is
        public static long MapId { get => S.MapId; set => S.MapId = value; }
        public static int CellId { get => S.CellId; set => S.CellId = value; }
        public static int Orientation { get => S.Orientation; set => S.Orientation = value; }
        public static long Kamas { get => S.Kamas; set => S.Kamas = value; }

        /// <summary>The ACCUMULATED experience, not the current level's.</summary>
        public static long Experience { get => S.Experience; set => S.Experience = value; }

        // The fight
        public static bool IsInFight { get => S.IsInFight; set => S.IsInFight = value; }
        public static long CurrentFightMobId { get => S.CurrentFightMobId; set => S.CurrentFightMobId = value; }

        // The characteristics and the capital
        public static int CharacterRemainingPoints { get => S.CharacterRemainingPoints; set => S.CharacterRemainingPoints = value; }
        public static int StatVitality { get => S.StatVitality; set => S.StatVitality = value; }
        public static int StatWisdom { get => S.StatWisdom; set => S.StatWisdom = value; }
        public static int StatStrength { get => S.StatStrength; set => S.StatStrength = value; }
        public static int StatIntelligence { get => S.StatIntelligence; set => S.StatIntelligence = value; }
        public static int StatChance { get => S.StatChance; set => S.StatChance = value; }
        public static int StatAgility { get => S.StatAgility; set => S.StatAgility = value; }

        // What the scrolls gave, apart from the points, and the sum of both.
        public static int ScrolledVitality { get => S.ScrolledVitality; set => S.ScrolledVitality = value; }
        public static int ScrolledWisdom { get => S.ScrolledWisdom; set => S.ScrolledWisdom = value; }
        public static int ScrolledStrength { get => S.ScrolledStrength; set => S.ScrolledStrength = value; }
        public static int ScrolledIntelligence { get => S.ScrolledIntelligence; set => S.ScrolledIntelligence = value; }
        public static int ScrolledChance { get => S.ScrolledChance; set => S.ScrolledChance = value; }
        public static int ScrolledAgility { get => S.ScrolledAgility; set => S.ScrolledAgility = value; }
        public static int TotalVitality => S.TotalVitality;
        public static int TotalWisdom => S.TotalWisdom;
        public static int TotalStrength => S.TotalStrength;
        public static int TotalIntelligence => S.TotalIntelligence;
        public static int TotalChance => S.TotalChance;
        public static int TotalAgility => S.TotalAgility;

        // The inventory and the equipment
        public static List<PlayerItem> GetInventoryCopy() => S.GetInventoryCopy();
        public static void SetInventory(List<PlayerItem> items) => S.SetInventory(items);
        public static void AddInventoryItem(PlayerItem item) => S.AddInventoryItem(item);
        public static void ClearInventory() => S.ClearInventory();
        public static PlayerItem? GetInventoryItem(long uid) => S.GetInventoryItem(uid);
        public static Dictionary<long, EquippedItemInfo> GetEquippedItemsCopy() => S.GetEquippedItemsCopy();
        public static void SetEquippedItem(long uid, EquippedItemInfo info) => S.SetEquippedItem(uid, info);
        public static void RemoveEquippedItem(long uid) => S.RemoveEquippedItem(uid);
        public static void ClearEquippedItems() => S.ClearEquippedItems();
    }

    public class PlayerItem
    {
        public long Uid { get; set; }
        public int ItemId { get; set; }
        public int Quantity { get; set; }
        public int Position { get; set; }
        public Dictionary<int, int> Effects { get; set; } = new Dictionary<int, int>();

        /// <summary>
        /// The effects just as they were stored, [[effect, value, die, side], ...].
        ///
        /// It is kept to return them to the base untouched. The dictionary above only
        /// keeps the effect and its value, and writing that back would lose the dice —the
        /// weapons' damage, for one— as soon as the item was saved for any reason.
        /// </summary>
        public string RawEffects { get; set; } = "";
    }

    public class EquippedItemInfo
    {
        public int Slot { get; set; }
        public Dictionary<int, int> Stats { get; } = new Dictionary<int, int>();
    }
}
