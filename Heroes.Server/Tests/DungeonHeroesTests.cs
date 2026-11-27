using System.Collections.Generic;
using Jondo.Unity.Protocol;
using Jondo.Unity.Server;
using Jondo.Unity.Server.Handlers;
using Jondo.Unity.Server.Managers;
using Jondo.Unity.Server.Network;
using Jondo.Unity.ServerMods;
using Jondo.Unity.Tests.Economy;
using Xunit;

namespace Jondo.Unity.Tests.World
{
    /// <summary>The master's heroes at a dungeon's door: a way in for each of them out of the group's bags, or nobody.</summary>
    [Collection("Heroes gameplay")]
    public sealed class DungeonHeroesTests : IDisposable
    {
        private readonly HeroesTestPackage _package = new();

        public void Dispose() => _package.Dispose();

        private static DungeonFollowerOptions M(long id, int keys, bool keyring = false, bool wantsKey = false)
            => new(id, keys, keyring, wantsKey);

        private Dictionary<long, long>? Plan(DungeonFollowerOptions master,
            IReadOnlyList<DungeonFollowerOptions> followers, out int missing)
        {
            var plan = _package.Groups.PlanDungeonAdmission(new(master.CharacterId, master.Keys, true, followers));
            missing = plan.MissingKeys;
            if (missing == 0) return plan.Followers.ToDictionary(way => way.CharacterId, way => way.PayerId);
            Assert.Empty(plan.Followers);
            return null;
        }

        [Fact]
        public void Each_hero_goes_in_on_his_own_key_first()
        {
            var plan = Plan(M(1, 5), new[] { M(2, 1), M(3, 1) }, out int missing);
            Assert.Equal(0, missing);
            Assert.Equal(new Dictionary<long, long> { [2] = 2, [3] = 3 }, plan);
        }

        [Fact]
        public void Heroes_without_a_key_take_the_masters_then_another_heroes_spare()
        {
            // One of four heroes has his key: the three others take two of the master's and the third's spare.
            var plan = Plan(M(1, 2), new[] { M(2, 0), M(3, 0), M(4, 2), M(5, 0) }, out int missing);
            Assert.Equal(0, missing);
            Assert.Equal(1, plan![2]);
            Assert.Equal(1, plan[3]);
            Assert.Equal(4, plan[4]);
            Assert.Equal(4, plan[5]);
        }

        [Fact]
        public void A_hero_with_his_keyring_spends_no_key()
        {
            var plan = Plan(M(1, 0), new[] { M(2, 3, keyring: true), M(3, 0) }, out int missing);
            Assert.Equal(0, missing);
            Assert.Equal(0, plan![2]);
            Assert.Equal(2, plan[3]);
        }

        /// <summary>
        /// A hero whose quest wants the key handed to the guardian (Tassa's laboratory) gives one
        /// rather than use his keyring -- his own, else the group's --, and uses the keyring only when
        /// the group has none left. He went in by his keyring with his key in his bag, and his quest
        /// stayed behind his master's (2026-11-21, through the MCP).
        /// </summary>
        [Fact]
        public void A_hero_whose_quest_wants_the_key_gives_one_rather_than_use_his_keyring()
        {
            var own = Plan(M(1, 0), [M(2, 1, keyring: true, wantsKey: true)], out int missing);
            Assert.Equal(0, missing);
            Assert.Equal(2, own![2]);

            var masters = Plan(M(1, 1), [M(2, 0, keyring: true, wantsKey: true)], out missing);
            Assert.Equal(1, masters![2]);

            var none = Plan(M(1, 0), [M(2, 0, keyring: true, wantsKey: true)], out missing);
            Assert.Equal(0, missing);
            Assert.Equal(0, none![2]);

            // Without a keyring and with no key in the group he is short, as anybody.
            Assert.Null(Plan(M(1, 0), [M(2, 0, wantsKey: true)], out missing));
            Assert.Equal(1, missing);
        }

        [Fact]
        public void Short_of_keys_nobody_goes_in_and_the_shortfall_is_counted()
        {
            var plan = Plan(M(1, 1), new[] { M(2, 0), M(3, 0), M(4, 0) }, out int missing);
            Assert.Null(plan);
            Assert.Equal(2, missing);
        }

        [Fact]
        public async Task The_door_rejects_the_whole_group_then_accepts_a_retry_with_two_payers()
        {
            const long account = 8_900_000_314, entrance = 990_314_001, room = 990_314_002;
            const int dungeonId = int.MaxValue - 12, key = 999999;
            var dungeon = new DungeonManager.Dungeon { Id = dungeonId, EntranceMapId = entrance, OnKeyring = true };
            dungeon.Rooms.Add(room);
            dungeon.Required.Add((key, 1));
            var dungeons = (Dictionary<int, DungeonManager.Dungeon>)DungeonManager.All;
            dungeons.Add(dungeonId, dungeon);
            MapManager.Maps.Add(room, new() { MapId = room });

            string tag = Guid.NewGuid().ToString("N")[..6];
            long Create(string name) => DatabaseManager.CreateCharacter(account, 1, name + tag, 1, 0, 1,
                [-1, -1, -1, -1, -1], entrance, 50, 0, 0, []);
            long master = Create("Master"), payer = Create("Payer"), guest = Create("Guest"),
                ring = Create("Ring"), fighting = Create("Fight");
            long masterKey = DatabaseManager.NextItemUid();
            Assert.True(DatabaseManager.InsertCharacterItem(masterKey, master, key, 2, Equipment.Bag, null));
            long ringUid = DatabaseManager.NextItemUid();
            Assert.True(DatabaseManager.InsertCharacterItem(ringUid, ring, DungeonHandler.Keyring, 1, Equipment.Bag, null));

            await using var pipe = await ClientPipe.OpenAsync(account, master, entrance);
            try
            {
                using var turn = await pipe.Session.EnterTurnAsync();
                using var context = SessionContext.Push(pipe.Session);
                Equipment.LoadFrom(master);
                foreach (long hero in new[] { payer, guest, ring, fighting })
                    Assert.Equal("ok", _package.Groups.Connect(pipe.Session, hero));
                CompanionSessions.SessionOf(fighting)!.State.IsInFight = true;
                var members = new[] { pipe.Session, CompanionSessions.SessionOf(payer)!, CompanionSessions.SessionOf(guest)!, CompanionSessions.SessionOf(ring)! };

                void AssertRejected()
                {
                    Assert.All(members, session => Assert.Equal(entrance, session.MapId));
                    Assert.All(members, session => Assert.Equal(0, DungeonProgress.SavedRoom(session.CharacterId, dungeon)));
                    Assert.Equal(2, Equipment.ByUid(masterKey)!.Quantity);
                    Assert.Equal(2, Assert.Single(DatabaseManager.LoadInventory(master), item => item.Uid == masterKey).Quantity);
                    Assert.True(DungeonKeyring.FreeEntryLeft(ring, dungeonId, DateTime.Now));
                }

                Assert.False(await DungeonHandler.AtTheDoorAsync(pipe.ToClient, entrance));
                AssertRejected();

                // The planner can see a key in the follower's live bag. If it disappeared from
                // storage, the master's earlier debit must roll back with the failed final debit.
                long payerKey = DatabaseManager.NextItemUid();
                using (SessionContext.Push(CompanionSessions.SessionOf(payer)!))
                    Equipment.Add(payerKey, key, 1, Equipment.Bag, null);
                Assert.False(await DungeonHandler.AtTheDoorAsync(pipe.ToClient, entrance));
                AssertRejected();
                using (SessionContext.Push(CompanionSessions.SessionOf(payer)!))
                    Assert.Equal(1, Equipment.ByUid(payerKey)!.Quantity);

                Assert.True(DatabaseManager.InsertCharacterItem(payerKey, payer, key, 1, Equipment.Bag, null));
                Assert.True(await DungeonHandler.AtTheDoorAsync(pipe.ToClient, entrance));
                Assert.All(members, session => Assert.Equal(room, session.MapId));
                Assert.All(members, session => Assert.Equal(room, DungeonProgress.SavedRoom(session.CharacterId, dungeon)));
                Assert.Equal(entrance, CompanionSessions.SessionOf(fighting)!.MapId);
                Assert.Equal(0, DungeonProgress.SavedRoom(fighting, dungeon));
                Assert.Null(Equipment.ByUid(masterKey));
                using (SessionContext.Push(CompanionSessions.SessionOf(payer)!))
                    Assert.Null(Equipment.ByUid(payerKey));
                Assert.DoesNotContain(DatabaseManager.LoadInventory(master), item => item.Uid == masterKey);
                Assert.DoesNotContain(DatabaseManager.LoadInventory(payer), item => item.Uid == payerKey);
                Assert.False(DungeonKeyring.FreeEntryLeft(ring, dungeonId, DateTime.Now));
                Assert.Contains(DatabaseManager.LoadInventory(ring), item => item.Uid == ringUid);
            }
            finally
            {
                if (CompanionSessions.SessionOf(fighting) is { } follower) follower.State.IsInFight = false;
                _package.Groups.DisconnectAll(master);
                MapManager.Maps.Remove(room);
                dungeons.Remove(dungeonId);
            }
        }
    }
}
