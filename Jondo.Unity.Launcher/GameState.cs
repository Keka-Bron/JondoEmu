using System;
using System.Collections.Generic;

namespace Jondo.Unity.Launcher
{
    public static class GameState
    {
        // Player Identity
        public static long CharacterId { get; set; } = 13825558L;
        public static string CharacterName { get; set; } = "[#KEKA-BRON#]";
        public static int CharacterLevel { get; set; } = 40;
        public static int Breed { get; set; } = 9;
        public static int Sex { get; set; } = 1;
        public static byte[] PlayerActorDetails { get; set; } = null;
        public static byte[] LookBytes { get; set; } = null;

        // Positioning
        public static long MapId { get; set; } = 154010883L;
        public static int CellId { get; set; } = 280;
        public static int Orientation { get; set; } = 1;
        public static long Kamas { get; set; } = 50000;

        /// <summary>The character's ACCUMULATED experience, not the current level's.</summary>
        public static long Experience { get; set; }

        // Combat State
        public static bool IsInFight { get; set; } = false;
        public static long CurrentFightMobId { get; set; } = 0;

        // Characteristics / Capital
        public static int CharacterRemainingPoints { get; set; } = 195;
        public static int StatVitality { get; set; } = 0;
        public static int StatWisdom { get; set; } = 0;
        public static int StatStrength { get; set; } = 0;
        public static int StatIntelligence { get; set; } = 0;
        public static int StatChance { get; set; } = 0;
        public static int StatAgility { get; set; } = 0;

        // Thread-Safety Synchronization Lock
        private static readonly object Lock = new object();

        // Inventory / Items (Private Backing Fields)
        private static readonly List<PlayerItem> _inventory = new List<PlayerItem>();

        // Equipped Items Cache (Private Backing Fields)
        private static readonly Dictionary<long, EquippedItemInfo> _equippedItems = new Dictionary<long, EquippedItemInfo>();

        public static List<PlayerItem> GetInventoryCopy()
        {
            lock (Lock)
            {
                return new List<PlayerItem>(_inventory);
            }
        }

        public static void SetInventory(List<PlayerItem> items)
        {
            lock (Lock)
            {
                _inventory.Clear();
                _inventory.AddRange(items);
            }
        }

        public static void AddInventoryItem(PlayerItem item)
        {
            lock (Lock)
            {
                _inventory.Add(item);
            }
        }

        public static void ClearInventory()
        {
            lock (Lock)
            {
                _inventory.Clear();
            }
        }

        public static PlayerItem? GetInventoryItem(long uid)
        {
            lock (Lock)
            {
                return _inventory.Find(i => i.Uid == uid);
            }
        }

        public static Dictionary<long, EquippedItemInfo> GetEquippedItemsCopy()
        {
            lock (Lock)
            {
                var dict = new Dictionary<long, EquippedItemInfo>();
                foreach (var kvp in _equippedItems)
                {
                    var info = new EquippedItemInfo { Slot = kvp.Value.Slot };
                    foreach (var stat in kvp.Value.Stats)
                    {
                        info.Stats[stat.Key] = stat.Value;
                    }
                    dict[kvp.Key] = info;
                }
                return dict;
            }
        }

        public static void SetEquippedItem(long uid, EquippedItemInfo info)
        {
            lock (Lock)
            {
                _equippedItems[uid] = info;
            }
        }

        public static void RemoveEquippedItem(long uid)
        {
            lock (Lock)
            {
                _equippedItems.Remove(uid);
            }
        }

        public static void ClearEquippedItems()
        {
            lock (Lock)
            {
                _equippedItems.Clear();
            }
        }
    }

    public class PlayerItem
    {
        public long Uid { get; set; }
        public int ItemId { get; set; }
        public int Quantity { get; set; }
        public int Position { get; set; } // Equipment slot or inventory position
        public Dictionary<int, int> Effects { get; set; } = new Dictionary<int, int>();

        /// <summary>
        /// Los efectos tal cual estaban guardados, [[efecto, valor, dado, cara], ...].
        ///
        /// Se conserva para devolverlos a la base sin tocar. El diccionario de arriba sólo se
        /// queda con el efecto y su valor, y volver a escribir eso perdería los dados —el daño de
        /// las armas, sin ir más lejos— en cuanto el objeto se guardase por cualquier motivo.
        /// </summary>
        public string RawEffects { get; set; } = "";
    }

    public class EquippedItemInfo
    {
        public int Slot { get; set; }
        public Dictionary<int, int> Stats { get; } = new Dictionary<int, int>();
    }
}
