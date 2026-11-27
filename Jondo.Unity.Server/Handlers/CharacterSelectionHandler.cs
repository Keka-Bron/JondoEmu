using System;
using System.Collections.Generic;
using System.Linq;
using Jondo.Unity.Server.Network;

namespace Jondo.Unity.Server.Handlers
{
    public static class CharacterSelectionHandler
    {
        /// <summary>
        /// Effect action IDs used inside irm effect entries (f11), keyed by internal stat ID.
        /// Observed in the official capture: 125=Vitality, 174=Initiative, 138=Power.
        /// </summary>
        private static readonly Dictionary<int, int> EffectActionIdByStatId = new Dictionary<int, int>
        {
            { 1, 111 },  // AP
            { 2, 128 },  // MP
            { 10, 118 }, // Strength
            { 11, 125 }, // Vitality
            { 12, 124 }, // Wisdom
            { 13, 123 }, // Chance
            { 14, 119 }, // Agility
            { 15, 126 }, // Intelligence
            { 16, 112 }, // Damage
            { 18, 115 }, // Critical
            { 25, 138 }, // Power
            { 44, 174 }, // Initiative
        };

        private static readonly Dictionary<int, int> StatIdByEffectActionId = EffectActionIdByStatId.ToDictionary(k => k.Value, v => v.Key);

        /// <summary>
        /// A companion's character into the current session -- one with no socket --:
        /// his sheet, his bag and what he wears, as a selection loads them, and his gear's bonuses
        /// and his spells' variants and bar, as the way into the world does (GameEntryBackend);
        /// nothing of a client's: no last visit, no last connection, no actor details for a map.
        /// </summary>
        internal static bool LoadForCompanion(long characterId)
        {
            if (!DatabaseManager.LoadCharacter(characterId)) return false;
            SessionContext.State.SetInventory(DatabaseManager.LoadInventory(characterId));
            RestoreEquippedItems();
            Managers.Equipment.LoadFrom(characterId);
            Managers.SpellChoices.LoadFrom(characterId);
            // His achievements and his own tallies: his fights count for him (Achievements.ForCompanionAsync).
            Managers.Achievements.LoadFrom(characterId);
            Managers.Quests.LoadFrom(characterId);
            // And the elements he has used and the marks he holds, which his quests' rows read.
            SessionContext.State.ElementsUsed = DatabaseManager.LoadElementsUsed(characterId);
            return true;
        }

        /// <summary>
        /// Loads the selected character after checking the resolved account's ownership.
        /// The entry service supplies the id from a typed request or a resumable fight.
        /// </summary>
        public static bool SelectCharacter(long characterIdToLoad, long accountId)
        {
            if (characterIdToLoad <= 0)
            {
                Console.WriteLine("[Game Node] The character selection carried no character id.");
                return false;
            }

            // Without an account nothing is selected. This was written as «accountId > 0 && ...»,
            // so the check switched itself off exactly in the case it had to catch: a socket that
            // sends the kvw BEFORE the kqz gets here with account zero, skips the whole check and
            // loads whatever sheet it wants. And since it is written over on saving afterwards, it
            // was not just looking.
            //
            // The account is resolved when the ticket is redeemed in kqz, and if the ticket is no good
            // the session is closed right there, so on the good path this is never zero.
            if (accountId <= 0)
            {
                Console.WriteLine($"[Game Node] Character selection without a resolved account " +
                                  $"(character {characterIdToLoad}). The ticket has not been presented.");
                return false;
            }

            if (!DatabaseManager.CharacterBelongsToAccount(characterIdToLoad, accountId))
            {
                Console.WriteLine($"[Game Node] Character {characterIdToLoad} does not belong to " +
                                  $"account {accountId}.");
                return false;
            }

            Console.WriteLine($"[Game Node] Selected character {characterIdToLoad}.");

            // Release an active follower before loading the same character into the real session.
            return Managers.CompanionSessions.SelectCharacter(characterIdToLoad, () => LoadSelected(characterIdToLoad));
        }

        private static bool LoadSelected(long characterIdToLoad)
        {
            // A sex change potion used last time: done now, before the character is read.
            Managers.CharacterRemodel.ApplySexChange(characterIdToLoad);

            bool dbCharacterLoaded = DatabaseManager.LoadCharacter(characterIdToLoad);
            if (!dbCharacterLoaded)
            {
                Console.WriteLine($"[Game Node] Could not load character {characterIdToLoad}.");
                return false;
            }
            // The previous visit is read first and only then overwritten: the other way round, what the
            // player would be shown is the connection of right now, which tells him nothing.
            SessionContext.State.PreviousVisit = DatabaseManager.ReadLastVisit(characterIdToLoad);
            DatabaseManager.TouchLastConnection(characterIdToLoad, SessionContext.State.ClientIp);

            Program.LogDebug($"[Stats Init] Stats loaded from database for {Jondo.Unity.Server.Network.SessionContext.State.CharacterName} (capital: {Jondo.Unity.Server.Network.SessionContext.State.CharacterRemainingPoints}).");

            var dbInventory = DatabaseManager.LoadInventory(characterIdToLoad);
            if (dbInventory.Count == 0)
            {
                Program.LogDebug("[Inventory Seed] Database inventory is empty! Seeding starting inventory...");
                var defaultItemsGids = new[] { 10784, 10785, 10794, 10799, 10800, 10801, 10798, 10797, 19622, 23, 43, 44, 45, 52, 69, 73, 74 };
                var defaultItems = new List<PlayerItem>();
                long baseUid = 10699035;
                
                var rnd = new Random();
                foreach (var gid in defaultItemsGids)
                {
                    var item = new PlayerItem { Uid = baseUid++, ItemId = gid, Quantity = 1, Position = 63, Effects = new Dictionary<int, int>() };
                    var rids = DatabaseManager.GetItemTemplatePossibleEffects(gid);
                    var effectsData = DatabaseManager.GetItemEffectsData(rids);
                    
                    foreach (var effect in effectsData)
                    {
                        int statId = StatIdByEffectActionId.TryGetValue(effect.EffectId, out int mapped) ? mapped : effect.EffectId;
                        int val = effect.DiceSide > effect.DiceNum ? rnd.Next(effect.DiceNum, effect.DiceSide + 1) : effect.DiceNum;
                        item.Effects[statId] = val;
                    }
                    defaultItems.Add(item);
                }

                SessionContext.State.SetInventory(defaultItems);
                DatabaseManager.SeedInventory(characterIdToLoad, defaultItems);
            }
            else
            {
                Program.LogDebug($"[Inventory Load] Loaded {dbInventory.Count} items from database. Setting as active inventory.");
                SessionContext.State.SetInventory(dbInventory);
            }

            RestoreEquippedItems();
            return true;
        }

        private static void RestoreEquippedItems()
        {
            var state = SessionContext.State;
            state.ClearEquippedItems();
            foreach (var item in state.GetInventoryCopy())
                Managers.Equipment.RememberWorn(item.Uid, item.Position, item.RawEffects);
        }

        internal static bool SelectCharacterOnServer(long characterId, GameSession session)
        {
            if (!session.IsAuthenticated) return false;
            var character = DatabaseManager.GetCharacterById(characterId);
            return character != null && character.AccountId == session.AccountId
                && character.ServerId == session.ServerId
                && SelectCharacter(characterId, session.AccountId);
        }
    }
}
