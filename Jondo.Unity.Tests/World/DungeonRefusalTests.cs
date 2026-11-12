using System.Collections.Generic;
using System.Reflection;
using System.Threading.Tasks;
using Jondo.Unity.Protocol;
using Jondo.Unity.Server;
using Jondo.Unity.Server.Handlers;
using Jondo.Unity.Server.Managers;
using Jondo.Unity.Server.Network;
using Jondo.Unity.Tests.Economy;
using Xunit;

namespace Jondo.Unity.Tests.World
{
    [Collection("MapManager")]
    public class DungeonRefusalTests
    {
        [Theory]
        [InlineData(false, false, false)]
        [InlineData(false, true, false)]
        [InlineData(false, true, true)]
        [InlineData(true, false, true)]
        [InlineData(true, false, false)]
        public async Task An_unavailable_selected_item_sends_a_red_bold_notice_and_keeps_the_connection(
            bool useKeyring, bool hasKeyring, bool hasKey)
        {
            const int dungeonId = int.MaxValue, npcId = int.MaxValue;
            const long entrance = -990000001, keyReply = 24970;
            var dungeons = (Dictionary<int, DungeonManager.Dungeon>)DungeonManager.All;
            var guardians = (Dictionary<int, DungeonDoor.Options>)typeof(DungeonDoor)
                .GetField("_byNpc", BindingFlags.NonPublic | BindingFlags.Static)!.GetValue(null)!;
            var dungeon = new DungeonManager.Dungeon
            {
                Id = 0, Name = "Cripta de Chadalid", EntranceMapId = entrance,
                OnKeyring = true,
            };
            dungeon.Rooms.Add(entrance - 1);
            dungeon.Required.Add((999999, 1));
            dungeons.Add(dungeonId, dungeon);
            guardians.Add(npcId, new DungeonDoor.Options { Key = keyReply, Keyring = keyReply + 1 });

            try
            {
                // Dungeon 0 avoids database access. The other entry item, if present, must never
                // be substituted for the one selected by the player.
                await using var pipe = await ClientPipe.OpenAsync(990000001, 990000002, entrance);
                using (SessionContext.Push(pipe.Session))
                {
                    pipe.Session.State.Language = "fr";
                    pipe.Session.State.CharacterLevel = 200;
                    pipe.Session.State.OpenDialogueNpcId = npcId;
                    if (hasKeyring) Equipment.Add(1, DungeonHandler.Keyring, 1, Equipment.Bag, null);
                    if (hasKey) Equipment.Add(2, 999999, 3, Equipment.Bag, null);
                    long selectedReply = useKeyring ? keyReply + 1 : keyReply;
                    Assert.Contains(keyReply, DungeonHandler.DoorReplies(npcId, entrance));

                    byte[] expected = ConnectionProtocol.BuildNotice(
                        "<color=#FF0000><b>Vous ne possédez pas l'objet nécessaire.</b></color>");
                    Assert.False(await DungeonHandler.AtTheDoorAsync(pipe.ToClient, entrance, selectedReply));
                    Assert.Equal(expected, await pipe.Next(Op.Lqn));
                    Assert.Equal(entrance, pipe.Session.State.MapId);
                    Assert.Equal(hasKeyring ? 1 : 0, Equipment.ByUid(1)?.Quantity ?? 0);
                    Assert.Equal(hasKey ? 3 : 0, Equipment.ByUid(2)?.Quantity ?? 0);

                    // Another request on the same TCP stream must still receive its response.
                    Assert.False(await DungeonHandler.AtTheDoorAsync(pipe.ToClient, entrance, selectedReply));
                    Assert.Equal(expected, await pipe.Next(Op.Lqn));
                    Assert.True(pipe.ToClient.CanWrite);
                }
            }
            finally
            {
                guardians.Remove(npcId);
                dungeons.Remove(dungeonId);
            }
        }

        [Theory]
        [InlineData(true, true, true, true, true)]
        [InlineData(true, true, true, false, false)]
        [InlineData(true, false, true, true, false)]
        [InlineData(true, true, false, true, true)]
        [InlineData(false, true, false, true, false)]
        [InlineData(false, true, true, true, true)]
        [InlineData(false, false, true, false, true)]
        [InlineData(false, false, false, false, false)]
        public async Task The_selected_method_never_falls_back_to_the_other_item(
            bool useKeyring, bool hasKeyring, bool hasKey, bool freeEntry, bool accepted)
        {
            await using var pipe = await ClientPipe.OpenAsync(990000003, 990000004, 1);
            using (SessionContext.Push(pipe.Session))
            {
                var dungeon = new DungeonManager.Dungeon { OnKeyring = true };
                dungeon.Required.Add((999999, 1));
                if (hasKeyring) Equipment.Add(1, DungeonHandler.Keyring, 1, Equipment.Bag, null);
                if (hasKey) Equipment.Add(2, 999999, 3, Equipment.Bag, null);
                var method = useKeyring ? DungeonHandler.EntryMethod.Keyring : DungeonHandler.EntryMethod.Key;

                Assert.Equal(accepted, DungeonHandler.TryTakeTheKey(dungeon, method, freeEntry,
                    out long uid, out int item));
                Assert.Equal(accepted && !useKeyring ? 2 : 0, uid);
                Assert.Equal(accepted ? (useKeyring ? DungeonHandler.Keyring : 999999) : 0, item);
                Assert.Equal(hasKeyring ? 1 : 0, Equipment.ByUid(1)?.Quantity ?? 0);
                Assert.Equal(hasKey ? 3 : 0, Equipment.ByUid(2)?.Quantity ?? 0);

                dungeon.OnKeyring = false;
                Assert.False(DungeonHandler.TryTakeTheKey(dungeon, DungeonHandler.EntryMethod.Keyring,
                    true, out _, out _));
            }
        }

        /// <summary>
        /// The keyring reply is only offered while the keyring can open the door: not without one in
        /// the bag, nor at a dungeon it does not take. The key's reply is never held back.
        /// </summary>
        [Fact]
        public async Task The_keyring_is_not_offered_when_it_cannot_open()
        {
            const int dungeonId = int.MaxValue - 1, npcId = int.MaxValue - 1;
            const long entrance = -990000011, keyReply = 24970, keyringReply = 24973;
            var dungeons = (Dictionary<int, DungeonManager.Dungeon>)DungeonManager.All;
            var guardians = (Dictionary<int, DungeonDoor.Options>)typeof(DungeonDoor)
                .GetField("_byNpc", BindingFlags.NonPublic | BindingFlags.Static)!.GetValue(null)!;
            // Dungeon 0: its free entry is always left, with no database to ask.
            var dungeon = new DungeonManager.Dungeon { Id = 0, Name = "Cripta de Chadalid", EntranceMapId = entrance, OnKeyring = true };
            dungeon.Rooms.Add(entrance - 1);
            dungeons.Add(dungeonId, dungeon);
            guardians.Add(npcId, new DungeonDoor.Options { Key = keyReply, Keyring = keyringReply });

            try
            {
                await using var pipe = await ClientPipe.OpenAsync(990000021, 990000022, entrance);
                using (SessionContext.Push(pipe.Session))
                {
                    Assert.True(DungeonHandler.KeyringOfferedForNothing(npcId, entrance, keyringReply));
                    Assert.False(DungeonHandler.KeyringOfferedForNothing(npcId, entrance, keyReply));

                    Equipment.Add(1, DungeonHandler.Keyring, 1, Equipment.Bag, null);
                    Assert.False(DungeonHandler.KeyringOfferedForNothing(npcId, entrance, keyringReply));

                    dungeon.OnKeyring = false;
                    Assert.True(DungeonHandler.KeyringOfferedForNothing(npcId, entrance, keyringReply));
                    Assert.False(DungeonHandler.KeyringOfferedForNothing(npcId, entrance, keyReply));
                }
            }
            finally
            {
                guardians.Remove(npcId);
                dungeons.Remove(dungeonId);
            }
        }

        /// <summary>
        /// A guardian that declares neither reply lets the player in on any answer: with the keyring
        /// while this week's entry is left, else with the key. One that asks opens only on its own.
        /// </summary>
        [Fact]
        public async Task A_guardian_that_asks_nothing_opens_on_any_reply()
        {
            const long other = 12345, keyReply = 24970;
            await using var pipe = await ClientPipe.OpenAsync(990000008, 990000009, 1);
            using (SessionContext.Push(pipe.Session))
            {
                var dungeon = new DungeonManager.Dungeon { OnKeyring = true };
                dungeon.Required.Add((999999, 1));
                var silent = new DungeonDoor.Options();
                var asking = new DungeonDoor.Options { Key = keyReply, Keyring = keyReply + 1 };
                var key = DungeonHandler.EntryMethod.Key;
                var keyring = DungeonHandler.EntryMethod.Keyring;

                Assert.Equal(key, DungeonHandler.Chosen(dungeon, silent, other, true));
                Assert.Equal(key, DungeonHandler.Chosen(dungeon, silent, 0, true));
                Assert.Null(DungeonHandler.Chosen(dungeon, asking, other, true));

                Equipment.Add(1, DungeonHandler.Keyring, 1, Equipment.Bag, null);
                Assert.Equal(keyring, DungeonHandler.Chosen(dungeon, silent, other, true));
                Assert.Equal(key, DungeonHandler.Chosen(dungeon, silent, other, false));
                Assert.Equal(key, DungeonHandler.Chosen(dungeon, asking, keyReply, true));
                Assert.Equal(keyring, DungeonHandler.Chosen(dungeon, asking, keyReply + 1, false));

                // A door that asks for nothing takes neither, and one that refuses the keyring never
                // gets it.
                dungeon.Required.Clear();
                Assert.Equal(key, DungeonHandler.Chosen(dungeon, silent, other, true));
                dungeon.Required.Add((999999, 1));
                dungeon.OnKeyring = false;
                Assert.Equal(key, DungeonHandler.Chosen(dungeon, silent, other, true));
            }
        }

        /// <summary>
        /// At a guardian that asks nothing, the answer reaches the door: without the key it is
        /// refused out loud, where before nothing at all happened.
        /// </summary>
        [Fact]
        public async Task A_guardian_that_asks_nothing_refuses_out_loud()
        {
            const int dungeonId = int.MaxValue - 1, npcId = int.MaxValue - 1;
            const long entrance = -990000002;
            var dungeons = (Dictionary<int, DungeonManager.Dungeon>)DungeonManager.All;
            var dungeon = new DungeonManager.Dungeon
            {
                Id = 0, Name = "Mazmorra de las Larvas", EntranceMapId = entrance, OnKeyring = true,
            };
            dungeon.Rooms.Add(entrance - 1);
            dungeon.Required.Add((999999, 1));
            dungeons.Add(dungeonId, dungeon);

            try
            {
                await using var pipe = await ClientPipe.OpenAsync(990000010, 990000011, entrance);
                using (SessionContext.Push(pipe.Session))
                {
                    pipe.Session.State.Language = "en";
                    pipe.Session.State.CharacterLevel = 200;
                    pipe.Session.State.OpenDialogueNpcId = npcId;
                    Assert.Empty(DungeonHandler.DoorReplies(npcId, entrance));

                    Assert.False(await DungeonHandler.AtTheDoorAsync(pipe.ToClient, entrance, 12345));
                    Assert.Equal(ConnectionProtocol.BuildNotice(
                            "<color=#FF0000><b>You do not possess the required item.</b></color>"),
                        await pipe.Next(Op.Lqn));
                    Assert.Equal(entrance, pipe.Session.State.MapId);
                }
            }
            finally { dungeons.Remove(dungeonId); }
        }

        /// <summary>
        /// With the keyring in the bag but this week's entry used, the player is told so and when
        /// it comes back, not that the item is missing. Picking the key says the key is missing.
        /// </summary>
        [Fact]
        public async Task A_used_up_keyring_says_when_it_comes_back()
        {
            await using var pipe = await ClientPipe.OpenAsync(990000012, 990000013, 1);
            using (SessionContext.Push(pipe.Session))
            {
                pipe.Session.State.Language = "es";
                var thursday = new System.DateTime(2026, 10, 15);
                var dungeon = new DungeonManager.Dungeon { Name = "Cripta de Chadalid", OnKeyring = true };
                dungeon.Required.Add((999999, 1));
                var key = DungeonHandler.EntryMethod.Key;
                var keyring = DungeonHandler.EntryMethod.Keyring;
                string missing = CommandTexts.Get("dungeon.missing_item");

                Assert.Equal(missing, DungeonHandler.Refusal(dungeon, keyring, true, false, thursday));

                Equipment.Add(1, DungeonHandler.Keyring, 1, Equipment.Bag, null);
                string usedUp = DungeonHandler.Refusal(dungeon, keyring, true, false, thursday);
                Assert.Contains("Cripta de Chadalid", usedUp);
                Assert.Contains("martes 20/10", usedUp);
                Assert.Equal(usedUp, DungeonHandler.Refusal(dungeon, key, false, false, thursday));
                Assert.Equal(missing, DungeonHandler.Refusal(dungeon, key, true, false, thursday));

                dungeon.OnKeyring = false;
                Assert.Equal(missing, DungeonHandler.Refusal(dungeon, key, false, false, thursday));
            }
        }

        [Theory]
        [InlineData(1)]
        [InlineData(3)]
        public async Task A_key_entry_spends_one_key_and_announces_the_remaining_stack(int quantity)
        {
            const long character = 990000006, uid = 990000007;
            await using var pipe = await ClientPipe.OpenAsync(990000005, character, 1);
            using (SessionContext.Push(pipe.Session))
            {
                Assert.True(DatabaseManager.InsertCharacterItem(uid, character, 999999, quantity,
                    Equipment.Bag, null));
                try
                {
                    Equipment.Add(uid, 999999, quantity, Equipment.Bag, null);
                    Equipment.Add(1, DungeonHandler.Keyring, 1, Equipment.Bag, null);
                    var dungeon = new DungeonManager.Dungeon { OnKeyring = true };
                    dungeon.Required.Add((999999, 1));
                    Assert.True(DungeonHandler.TryTakeTheKey(dungeon, DungeonHandler.EntryMethod.Key,
                        true, out long selected, out _));

                    Assert.True(await ItemUseHandler.SpendOneAsync(pipe.ToClient, selected, weigh: false));
                    Assert.Equal(quantity - 1, Equipment.ByUid(uid)?.Quantity ?? 0);
                    Assert.Equal(1, Equipment.ByUid(1)!.Quantity);
                    Assert.Equal(quantity > 1
                        ? ConnectionProtocol.BuildItemQuantity(uid, quantity - 1)
                        : ConnectionProtocol.BuildItemGone(uid),
                        await pipe.Next(quantity > 1 ? Op.Ivj : Op.Ium));
                    var saved = DatabaseManager.LoadInventory(character);
                    Assert.Equal(quantity - 1, saved.Find(i => i.Uid == uid)?.Quantity ?? 0);
                }
                finally { DatabaseManager.DestroyCharacterItem(character, uid, 0); }
            }
        }
    }
}
