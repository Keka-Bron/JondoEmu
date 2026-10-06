using Jondo.Unity.Launcher;
using System;
using System.Collections.Generic;

namespace Jondo.Unity.Server
{
    /// <summary>Mutable game data owned by exactly one network session.</summary>
    public sealed class SessionState
    {
        // Player Identity
        public long CharacterId { get; set; }
        public string CharacterName { get; set; } = "";
        public int CharacterLevel { get; set; } = 1;
        public int Breed { get; set; }
        public int Sex { get; set; }
        public string Language { get; set; } = "es";
        public byte[]? PlayerActorDetails { get; set; }
        public byte[]? LookBytes { get; set; }

        // Positioning
        public long MapId { get; set; }
        public int CellId { get; set; }
        public int Orientation { get; set; } = 1;

        /// <summary>
        /// Destination of the last <c>jrw</c> movement, awaiting its
        /// <c>jqi</c> confirmation. A null map value means there is nothing to confirm.
        /// </summary>
        /// <remarks>
        /// The client does not ask for an <c>iwo</c> for some exits laid on the ground: it walks
        /// to their cell and then confirms the end of the movement. This state must stay specific to
        /// the session, otherwise one player's confirmation could trigger another's exit.
        /// </remarks>
        public long PendingMovementMapId { get; set; }
        public int PendingMovementCellId { get; set; } = -1;

        public long Kamas { get; set; }

        /// <summary>The character's ACCUMULATED experience, not the current level's.</summary>
        public long Experience { get; set; }

        /// <summary>
        /// When the client's life regeneration counter was last started (the ktz), so that the
        /// kuq that stops it at fight entry can say how many ticks it ran. Default when it never
        /// was.
        /// </summary>
        public DateTime RegenerationStartedUtc { get; set; }

        // Combat State
        public bool IsInFight { get; set; }

        /// <summary>
        /// This session came back into a fight that was already running, and the client has
        /// not asked for the board yet. Cleared by the burst that answers that request.
        /// </summary>
        public bool FightRejoinPending { get; set; }

        /// <summary>
        /// Where this player came from on entering a fight, to return him there at the end.
        ///
        /// They were two statics of the fight handler, one for the whole server: the second to
        /// start fighting overwrote the first's place, and at the end both appeared where
        /// the last one was.
        /// </summary>
        public long RoleplayMapId { get; set; }
        public int RoleplayCellId { get; set; }

        /// <summary>Which fight this character is in. Zero when they are not in one.</summary>
        /// <remarks>
        /// Needed because <see cref="MapId"/> stops telling fights apart the moment a fight starts.
        /// Two fights on the same roleplay map resolve to the SAME arena through
        /// <c>MapManager.ResolveArenaMapId</c> -- one arena per roleplay map, by design, because the
        /// arena is a real map from the game files and its cell layout is what the fight is drawn
        /// on. So both groups sit on one map id, and anything that reaches "everybody on this map"
        /// reaches the other fight as well. Map chat did exactly that: you could read what the
        /// strangers fighting beside you were saying, and they could read you.
        ///
        /// The fight packets themselves were never affected -- those go to the fight's own list of
        /// participants and never through the map. It is only what broadcasts by map that has to
        /// ask this question.
        /// </remarks>
        public long FightId { get; set; }
        public long CurrentFightMobId { get; set; }

        // Characteristics / Capital
        public int CharacterRemainingPoints { get; set; }
        public int StatVitality { get; set; }
        public int StatWisdom { get; set; }
        public int StatStrength { get; set; }

        /// <summary>Where this session connected from. It is kept for next time.</summary>
        public string ClientIp { get; set; } = "";

        /// <summary>When and where he connected from last time, read before overwriting it.</summary>
        public DatabaseManager.LastVisit? PreviousVisit { get; set; }

        /// <summary>
        /// The experience of each of this character's professions, loaded on entering and saved in the
        /// base every time it goes up. It lives here and not in a static because two players at once
        /// have different professions.
        /// </summary>
        public Dictionary<int, Managers.JobExperience.Progress> Jobs { get; } = new();

        /// <summary>
        /// This character's quests: which ones he carries, which step he is on and what he has met.
        /// </summary>
        /// <remarks>
        /// Here and not in a static, for the same reason as the professions and with more cause: a quest is
        /// checked on every sentence of every dialogue, so a shared dictionary would make
        /// talking to an NPC move the quest of the one next to you.
        ///
        /// <summary>The interactives this character has ever used.</summary>
        /// <remarks>
        /// It is filled on entering the world and grows with each use. The NPCs' reply filter
        /// reads it: there are conversations whose option must not exist until something has been read, like the
        /// job offer of the Incarnam tavern.
        /// </remarks>
        public HashSet<int> ElementsUsed { get; set; } = new HashSet<int>();

        /// It is <c>null</c> until entering the world. <c>Managers.Quests.LoadFrom</c> sets it,
        /// because it needs the catalogue, which is from another project and weighs 3 MB: building it here
        /// would force loading it also in the sessions that never get to play.
        /// </remarks>
        public World.Quests.QuestLog? Quests { get; set; }

        /// <summary>This character's achievements. Null until entering the world, like the quests.</summary>
        public World.Achievements.AchievementLog? Achievements { get; set; }

        /// <summary>
        /// The tallies achievements count — monsters beaten, zones entered, items crafted — by
        /// kind and key, loaded on entering the world and written as they change.
        /// </summary>
        public Dictionary<(string Kind, long Key), long> AchievementTallies { get; set; } = new();

        /// <summary>The dungeon and anomaly challenges this character has validated, for <c>EH</c>.</summary>
        public HashSet<int> ChallengesDone { get; set; } = new HashSet<int>();

        /// <summary>The level the level-based achievements were last looked at on, so a map change does not look again for nothing.</summary>
        public int AchievementLevelChecked { get; set; }

        /// <summary>Achievements a fight may have earned, looked at once the character is back on the map.</summary>
        public HashSet<int> AchievementsPending { get; } = new HashSet<int>();

        /// <summary>The emotes this character can play: the starting ones and the ones learned.</summary>
        public HashSet<int> Emotes { get; set; } = new HashSet<int>();

        /// <summary>When the last emote played, for the gap the real server keeps between two.</summary>
        public DateTime LastEmoteUtc { get; set; } = DateTime.MinValue;

        /// <summary>When the last smiley went up, for the same gap.</summary>
        public DateTime LastSmileyUtc { get; set; } = DateTime.MinValue;

        /// <summary>What level a profession is at. Zero experience is level 1, not level zero.</summary>
        public int JobLevel(int jobId)
            => Jobs.TryGetValue(jobId, out var progress) ? progress.Level : 1;

        /// <summary>Adds experience to a profession and says whether it went up.</summary>
        public bool AddJobExperience(int jobId, long amount, out long total, out int level)
        {
            bool sube = Managers.JobExperience.Add(Jobs, jobId, amount, out var progress);
            total = progress.Experience;
            level = progress.Level;
            return sube;
        }
        public int StatIntelligence { get; set; }
        public int StatChance { get; set; }
        public int StatAgility { get; set; }

        /// <summary>
        /// What the scrolls gave, per characteristic, kept apart from the points the player spent.
        /// </summary>
        /// <remarks>
        /// The two are different things to the client and to the cost of the next point: the
        /// sheet draws them as "Base" and "Adicional", the next point of strength is priced off the
        /// base alone, and the capital the player has left is the capital minus the base. Measured:
        /// every scrolled character in the captures carries its scrolls in f3 of the
        /// characteristic and its spent points in f2, and never the sum in either. Keeping the
        /// scrolls inside the base was what made a fresh level 200 show 183 points to spend
        /// instead of 995.
        /// </remarks>
        public int ScrolledVitality { get; set; }
        public int ScrolledWisdom { get; set; }
        public int ScrolledStrength { get; set; }
        public int ScrolledIntelligence { get; set; }
        public int ScrolledChance { get; set; }
        public int ScrolledAgility { get; set; }

        /// <summary>The characteristic as the game uses it: points spent plus scrolls.</summary>
        public int TotalVitality => StatVitality + ScrolledVitality;
        public int TotalWisdom => StatWisdom + ScrolledWisdom;
        public int TotalStrength => StatStrength + ScrolledStrength;
        public int TotalIntelligence => StatIntelligence + ScrolledIntelligence;
        public int TotalChance => StatChance + ScrolledChance;
        public int TotalAgility => StatAgility + ScrolledAgility;

        // Session-local UI/dialog state. These used to be static fields in handlers.
        public long OpenZaapMapId { get; set; }

        /// <summary>
        /// Which door the house one is in was entered through, to leave through that same one.
        ///
        /// Several doors in the world can lead to the same interior, so without this one leaves through
        /// the first that leads there and the player appears in another neighbourhood. If there is nothing —because
        /// he disconnected inside— the door the data says is used, which at least exists.
        /// </summary>
        public long HouseEntryMapId { get; set; }

        /// <summary>The street cell it was entered from.</summary>
        public int HouseEntryCell { get; set; }

        /// <summary>
        /// The street door one came in by: which house this is, since several doors lead to the
        /// same interior and each door is a house of its own, with its own owner and chests.
        /// </summary>
        public int HouseEntryElementId { get; set; }

        /// <summary>The house window this character has open -- a sale, a purchase, a code keypad -- if any.</summary>
        public Handlers.HouseHandler.Dialog? HouseDialog { get; set; }

        /// <summary>The house chest, bin or guild chest this character has open, if any.</summary>
        public Handlers.StorageHandler.Window? Storage { get; set; }

        /// <summary>
        /// Which map the haven bag was entered from, to go back there with the same key.
        /// </summary>
        /// <remarks>
        /// It is needed because the client sends THE SAME message to enter and to leave: in
        /// «Movimiento/ir al merkasako y volver.pcapng» the player's two requests, #1 and
        /// #8, are a jbn with the body 10a28280c8e708 byte for byte, and the server answers the
        /// first with the bag's map and the second with a world map. So whoever
        /// decides the direction is the server, looking at where the player is, and for that one has to
        /// know where he came from.
        ///
        /// Separate from RoleplayMapId, which belongs to the fight: one can enter the haven bag and fight
        /// inside, and sharing the field would leave whoever leaves the fight in the street.
        /// </remarks>
        public long HavenBagEntryMapId { get; set; }

        /// <summary>The world cell the haven bag was entered from.</summary>
        public int HavenBagEntryCell { get; set; }
        public bool IsChestOpen { get; set; }

        /// <summary>
        /// The map where this character opened the bank, or zero when the bank is not open.
        /// A map rather than a flag: see <see cref="Handlers.BankHandler.IsOpen"/>.
        /// </summary>
        public long BankMapId { get; set; }

        /// <summary>The workshop this character has open -- craft station, magus table, grinder -- if any.</summary>
        public Handlers.WorkshopHandler.Bench? Workshop { get; set; }

        /// <summary>The commission this character is in, as the magus or as the customer, if any.</summary>
        public Handlers.Commission? Commission { get; set; }

        /// <summary>The trade with another player this character is in, asked or open, if any.</summary>
        public Handlers.Trade? Trade { get; set; }

        /// <summary>The marketplace this character has open, to buy or to sell, if any.</summary>
        public Handlers.MarketplaceWindow? Marketplace { get; set; }

        /// <summary>
        /// This character's settings as an artisan, job by job: the minimum level asked of a
        /// customer, whether they craft for free, and whether they are in the public list.
        /// </summary>
        public Dictionary<int, Handlers.ArtisanHandler.Setting> CrafterSettings { get; } = new();

        /// <summary>Whose list this character is reading in the artisans' directory; zero when none.</summary>
        public int DirectoryJob { get; set; }

        /// <summary>
        /// Forgegod mode (".forjadios on"): no rune fails, no cap nor restriction applies at the
        /// forge. Administrators only, and for this session only -- it is off again at the next login.
        /// </summary>
        public bool ForgeGod { get; set; }
        public bool IsHavenBagEditing { get; set; }
        public List<Managers.HavenBagStore.Furniture> PendingHavenBagFurniture { get; }
            = new List<Managers.HavenBagStore.Furniture>();
        public int WardrobeDraftTitle { get; set; }
        public int WardrobeDraftOrnament { get; set; }
        public bool IsWardrobeDraftLoaded { get; set; }
        public long OpenNpcShopId { get; set; }
        public int OpenNpcShopNpcId { get; set; }

        /// <summary>
        /// Which conversation is open and where it is at.
        ///
        /// It has to be stored because the client, on choosing a reply, sends the ioy with the
        /// reply's id AND NOTHING ELSE: neither which NPC nor which sentence it came from. Without this there is no way
        /// of knowing which line it leads to, and that is why the dialogue could only have one sentence.
        ///
        /// It goes in the session state and not in a static like everything else: with eight clients at
        /// once, a static one would make one player's reply advance another's conversation.
        /// </summary>
        public int OpenDialogueNpcId { get; set; }

        /// <summary>The map where it was opened, which is part of which conversation it is.</summary>
        public long OpenDialogueMapId { get; set; }

        /// <summary>Which sentence it is on right now.</summary>
        public long OpenDialogueMessage { get; set; }

        // Per-character manager caches. These must never be static: loading the second account
        // would otherwise replace the first account's equipment, appearance and spell bar.
        internal Dictionary<long, Managers.Equipment.Item> EquipmentItems { get; }
            = new Dictionary<long, Managers.Equipment.Item>();
        internal Dictionary<int, int> ChosenSpells { get; } = new Dictionary<int, int>();
        internal Dictionary<int, int> SpellBar { get; } = new Dictionary<int, int>();
        internal long SpellChoicesCharacterId { get; set; }

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

    // PlayerItem and EquippedItemInfo live in GameState.cs: the copy here lost RawEffects.
}
