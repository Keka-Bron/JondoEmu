using System;
using System.Collections.Generic;

namespace Jondo.Unity.Launcher
{
    /// <summary>
    /// Server-authoritative context for one house purchase confirmation.  The client only sends
    /// the displayed price in <c>jal</c>, so the identity of the house must come from the door
    /// that opened the dialog, never from a client-supplied dwelling id.
    /// </summary>
    public sealed class PendingHousePurchaseContext
    {
        public int HouseId { get; init; }
        public long MapId { get; init; }
        public int ElementId { get; init; }
        public long ExpectedPrice { get; init; }
        public long ExpectedOwnerAccountId { get; init; }
        public bool ExpectedListed { get; init; }
        public long AccountId { get; init; }
        public long CharacterId { get; init; }
    }

    /// <summary>
    /// Session-scoped return created by a declared one-way world-graph transition.  The static
    /// return cell itself comes from version-pinned client data; the originating map belongs to
    /// this session so a player cannot use an interior exit as a free teleport.
    /// </summary>
    public sealed class PendingWorldInteractiveReturn
    {
        public long InteriorMapId { get; init; }
        public int ExitCellId { get; init; }
        public long ReturnMapId { get; init; }
        public int ReturnCellId { get; init; }
        public int EntryElementId { get; init; }
    }

    /// <summary>Mutable game data owned by exactly one network session.</summary>
    public sealed class SessionState
    {
        // Player Identity
        public long CharacterId { get; set; }
        public string CharacterName { get; set; } = "";
        public int CharacterLevel { get; set; } = 1;
        public int Breed { get; set; }
        public int Sex { get; set; }
        public byte[]? PlayerActorDetails { get; set; }
        public byte[]? LookBytes { get; set; }

        // Positioning
        private long _mapId;
        public long MapId
        {
            get => _mapId;
            set
            {
                if (_mapId != value) PendingHousePurchase = null;
                if (_mapId != value && PendingWorldInteractiveReturn != null &&
                    value != PendingWorldInteractiveReturn.InteriorMapId)
                    PendingWorldInteractiveReturn = null;
                _mapId = value;
            }
        }
        public int CellId { get; set; }
        public int Orientation { get; set; } = 1;
        public long Kamas { get; set; }

        /// <summary>The character's ACCUMULATED experience, not the current level's.</summary>
        public long Experience { get; set; }

        // Combat State
        public bool IsInFight { get; set; }

        /// <summary>
        /// De dónde salió este jugador al entrar en combate, para devolverlo ahí al acabar.
        ///
        /// Eran dos estáticos del manejador de combate, uno para todo el servidor: el segundo que
        /// entrara a pelear pisaba el sitio del primero, y al terminar los dos aparecían donde
        /// estaba el último.
        /// </summary>
        public long RoleplayMapId { get; set; }
        public int RoleplayCellId { get; set; }
        public long CurrentFightMobId { get; set; }

        // Characteristics / Capital
        public int CharacterRemainingPoints { get; set; }
        public int StatVitality { get; set; }
        public int StatWisdom { get; set; }
        public int StatStrength { get; set; }
        public int StatIntelligence { get; set; }
        public int StatChance { get; set; }
        public int StatAgility { get; set; }

        // Permanent combat resources.  The AP/MP spent while a fight turn is in progress belong
        // to Fighter and intentionally reset at the next turn; these are the character's saved
        // bases, to which level and equipment bonuses are applied.
        public int BaseActionPoints { get; set; } = 6;
        public int BaseMovementPoints { get; set; } = 3;

        // Session-local UI/dialog state. These used to be static fields in handlers.
        public long OpenZaapMapId { get; set; }

        /// <summary>
        /// Street position used to enter the current house. Multiple doors can lead to one
        /// interior, so this remains session-scoped and lets the character leave by the same door.
        /// </summary>
        public long HouseEntryMapId { get; set; }

        /// <summary>Street cell used to enter the current house.</summary>
        public int HouseEntryCell { get; set; }
        /// <summary>Persistent house instance selected by the most recent door action.</summary>
        public int OpenHouseId { get; set; }
        /// <summary>The exact door and price awaiting a <c>jal</c> confirmation.</summary>
        public PendingHousePurchaseContext? PendingHousePurchase { get; set; }
        public void ClearPendingHousePurchase() => PendingHousePurchase = null;
        public PendingWorldInteractiveReturn? PendingWorldInteractiveReturn { get; set; }
        public bool IsChestOpen { get; set; }
        public bool IsHavenBagEditing { get; set; }
        public List<Managers.HavenBagStore.Furniture> PendingHavenBagFurniture { get; }
            = new List<Managers.HavenBagStore.Furniture>();
        public int WardrobeDraftTitle { get; set; }
        public int WardrobeDraftOrnament { get; set; }
        public bool IsWardrobeDraftLoaded { get; set; }
        public long OpenNpcShopId { get; set; }
        public int OpenNpcShopNpcId { get; set; }
        /// <summary>
        /// Contextual id of the NPC conversation currently shown to this client.  A dialog close
        /// is a different protocol branch from a shop or a zaap close and must receive the NPC
        /// kld reason.
        /// </summary>
        public long OpenNpcDialogId { get; set; }

        // Per-character manager caches. These must never be static: loading the second account
        // would otherwise replace the first account's equipment, appearance and spell bar.
        internal Dictionary<long, Managers.Equipment.Item> EquipmentItems { get; }
            = new Dictionary<long, Managers.Equipment.Item>();
        internal Dictionary<int, int> ChosenSpells { get; } = new Dictionary<int, int>();
        internal Dictionary<int, int> SpellBar { get; } = new Dictionary<int, int>();
        internal long SpellChoicesCharacterId { get; set; }
        /// <summary>
        /// True once the player has accepted, edited, or cleared the default spell bar.  An empty
        /// dictionary without this bit means a new character and is eligible for default slots;
        /// an empty dictionary with it means the player deliberately cleared the bar.
        /// </summary>
        internal bool SpellBarInitialized { get; set; }

        // Thread-Safety Synchronization Lock
        private readonly object _lock = new object();

        // Inventory / Items (Private Backing Fields)
        private readonly List<PlayerItem> _inventory = new List<PlayerItem>();

        // Equipped Items Cache (Private Backing Fields)
        private readonly Dictionary<long, EquippedItemInfo> _equippedItems = new Dictionary<long, EquippedItemInfo>();

        public List<PlayerItem> GetInventoryCopy()
        {
            lock (_lock)
            {
                return new List<PlayerItem>(_inventory);
            }
        }

        public void SetInventory(List<PlayerItem> items)
        {
            lock (_lock)
            {
                _inventory.Clear();
                _inventory.AddRange(items);
            }
        }

        public void AddInventoryItem(PlayerItem item)
        {
            lock (_lock)
            {
                _inventory.Add(item);
            }
        }

        public void ClearInventory()
        {
            lock (_lock)
            {
                _inventory.Clear();
            }
        }

        public PlayerItem? GetInventoryItem(long uid)
        {
            lock (_lock)
            {
                return _inventory.Find(i => i.Uid == uid);
            }
        }

        public Dictionary<long, EquippedItemInfo> GetEquippedItemsCopy()
        {
            lock (_lock)
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

        public void SetEquippedItem(long uid, EquippedItemInfo info)
        {
            lock (_lock)
            {
                _equippedItems[uid] = info;
            }
        }

        public void RemoveEquippedItem(long uid)
        {
            lock (_lock)
            {
                _equippedItems.Remove(uid);
            }
        }

        public void ClearEquippedItems()
        {
            lock (_lock)
            {
                _equippedItems.Clear();
            }
        }
    }

    // PlayerItem y EquippedItemInfo viven en GameState.cs: la copia de aqui perdia RawEffects.
}
