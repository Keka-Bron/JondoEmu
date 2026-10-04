using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Jondo.Unity.Protocol;
using Jondo.Unity.Server;
using Jondo.Unity.Server.Handlers;
using Jondo.Unity.Server.Managers;
using Jondo.Unity.Server.Network;
using Jondo.Unity.Tests.Economy;
using Microsoft.Data.Sqlite;
using Xunit;

namespace Jondo.Unity.Tests.World
{
    /// <summary>
    /// The guild chest against "Interactivos varios/entrar en banco bonta-abrir cofre
    /// gremio-usarlo-abrir cofre personal del banco.pcapng", frames 26-69, and the rights of the
    /// ranks that open it: per guild, per tab, look, put in, take out.
    /// </summary>
    /// <remarks>
    /// The guilds live in a scratch base, as in the other guild tests (the same collection, for the
    /// same static switch); the chest's stacks in world.db, under guild ids no real guild has.
    /// </remarks>
    [Collection("guild raids")]
    public class GuildChestTests : IDisposable
    {
        private readonly string _file;

        public GuildChestTests()
        {
            _file = Path.Combine(Path.GetTempPath(), $"jondo-cofre-gremio-{Guid.NewGuid():N}.db");
            GuildStore.ConnectionStringOverride = $"Data Source={_file}";
        }

        public void Dispose()
        {
            GuildStore.ConnectionStringOverride = null;
            SqliteConnection.ClearAllPools();
            try { File.Delete(_file); } catch (IOException) { }
        }

        private static byte[] Hex(string hex) => Convert.FromHexString(hex);

        // ─── The frames ─────────────────────────────────────────────────────────

        [Fact]
        public void A_guild_s_one_tab_is_the_capture_s()
        {
            // "Gremio/muchas acciones en mi gremio como lider", frame 10: a guild with one tab.
            Assert.Equal(Hex("0a92040aed030102030405060708090a0b0c0d0e0f101112131415161718191a1b1c1d1e1f202122232425262728292a2b2c2d2e2f303132333435363738393a3b3c3d3e3f4041424445464748494a4b4c4d4e4f505152535455565758595a5b5c5d5e5f6061626364666768696a6b6c6d6e6f70717273747677797a7b7c7d7e7f8001810182018301840185018601880189018a018b018c018d018e018f0190019101920193019401950196019701980199019a019b019c019d019e019f01a001a101a201a301a401a501a601a701a801a901ab01ac01ad01ae01af01b001b101b201b301b401b501b601b701b801b901ba01bb01bc01bd01be01c001c301c401c501c601c701c801c901ca01cb01cd01ce01cf01d101d301d401d501d601d701d801d901da01db01dc01dd01de01df01e101e201e401e501e601e701e801e901ea01ec01ee01f001f101f201f301f401f501f601f701f801f901fa01fb01fc01fd01fe01ff01800282028302840285028602880289028a028b028c028d028f0290029102920293029402950296029702980299029a029b029c029e029f02a002a102a302a502a602a902aa02ab02ac02ad02ae02af02b002b102b202b302b402b502b602b702b802b902ba02bb02bc02bd02be02bf02c102c202c302c402c502c602c702c802c902ca02cb02cc02cd02ce02100118172018284c30083a166775696c642e63686573742e7461622e342e6e616d65"),
                         StorageProtocol.BuildGuildChestTabs(GuildChests.TabsOfGuild(1)));
        }

        [Fact]
        public void Opening_it_is_frames_29_to_32()
        {
            Assert.Equal(Hex("081610011864"), StorageProtocol.BuildGuildChestOpened(1));
            Assert.Equal(Hex("0a0c53616372692d4d6173746572"), StorageProtocol.BuildGuildChestViewers(new[] { "Sacri-Master" }));
            Assert.Equal(Hex("0a0c53616372692d4d6173746572"), StorageProtocol.BuildGuildChestViewer("Sacri-Master"));

            // Frame 39: the second tab refused, "No tienes el derecho de consultar el cofre de gremio."
            Assert.Equal(Hex("0801108e05"), ConnectionProtocol.BuildInfoMessage(InfoMessages.Warning, GuildChests.NoRightToLook));
        }

        [Fact]
        public void Each_tab_has_its_three_rights_from_the_client_s_table()
        {
            // Tab 1: 8 look, 24 put in, 23 take out; tab 2: 31, 30, 29; tab 3: 32, 33, 34 (ivl f6, f4, f3).
            Assert.Equal((8, 24, 23), (GuildChests.Tabs[0].ConsultRight, GuildChests.Tabs[0].DepositRight, GuildChests.Tabs[0].WithdrawRight));
            Assert.Equal((31, 30, 29), (GuildChests.Tabs[1].ConsultRight, GuildChests.Tabs[1].DepositRight, GuildChests.Tabs[1].WithdrawRight));
            Assert.Equal((32, 33, 34), (GuildChests.Tabs[2].ConsultRight, GuildChests.Tabs[2].DepositRight, GuildChests.Tabs[2].WithdrawRight));

            // The ranks a guild is founded with: the leader and rank 2 have tab 1's three; 3 and 4 none.
            var ranks = GuildStore.DefaultRanks(1);
            foreach (var rank in ranks)
            {
                var rights = GuildChests.RightsOf(rank.Rights);
                bool all = rights.Contains(8) && rights.Contains(24) && rights.Contains(23);
                Assert.Equal(rank.Id <= 2, all);
            }

            // Hezbola's rank 2 at the 9 August login: tab 1 yes, tab 2 no -- frames 38-39.
            var hezbola = GuildChests.RightsOf(new byte[] { 1, 2, 5, 6, 7, 8, 13, 14, 15, 23, 24, 25, 26 });
            Assert.Contains(GuildChests.Tabs[0].ConsultRight, hezbola);
            Assert.DoesNotContain(GuildChests.Tabs[1].ConsultRight, hezbola);
        }

        // ─── At the chest ───────────────────────────────────────────────────────

        private const long GuildId = 8_830_000_001;
        private const long BankMap = 217059328;
        private const int Chest = 524415;

        private void MakeGuild()
        {
            GuildStore.GuildOf(0);   // the tables
            using var connection = new SqliteConnection(GuildStore.ConnectionStringOverride);
            connection.Open();
            using var insert = connection.CreateCommand();
            insert.CommandText = "INSERT INTO Guilds (Id, Name, Level, FoundedUtc) VALUES ($id, 'Cofre', 1, '2026-09-26T00:00:00Z');";
            insert.Parameters.AddWithValue("$id", GuildId);
            insert.ExecuteNonQuery();
        }

        private static void Forget(params long[] characters)
        {
            StorageStacks.TakeAll($"guild:{GuildId}:");
            using var connection = new SqliteConnection(DatabaseManager.WorldConnectionString);
            connection.Open();
            foreach (long character in characters)
            {
                using var bag = connection.CreateCommand();
                bag.CommandText = "DELETE FROM CharacterItems WHERE CharacterId = $c;";
                bag.Parameters.AddWithValue("$c", character);
                bag.ExecuteNonQuery();
            }
        }

        private static byte[] Kcr(long quantity, long uid)
            => ConnectionProtocol.Push(Op.Kcr, Pb.New().Var(1, quantity).Var(2, uid).Build());

        [Fact]
        public async Task The_leader_opens_it_puts_in_takes_out_and_is_refused_a_tab_there_is_not()
        {
            const long leader = 8_830_100_001;
            MakeGuild();
            GuildStore.Join(leader, GuildId, GuildStore.RankLeader);
            Forget(leader);

            await using var pipe = await ClientPipe.OpenAsync(8_830_000_101, leader, BankMap, "Sacri-Master");
            try
            {
                using (SessionContext.Push(pipe.Session))
                {
                    long uid = DatabaseManager.NextItemUid();
                    Assert.True(DatabaseManager.InsertCharacterItem(uid, leader, 17626, 1, Equipment.Bag, null));
                    Equipment.Add(uid, 17626, 1, Equipment.Bag, null);

                    // Frames 26-32.
                    await GuildChestHandler.OpenAsync(pipe.ToClient, Chest, GuildChests.UseSkill);
                    Assert.Equal(ConnectionProtocol.BuildElementInUse(Chest, 184, leader), await pipe.Next(Op.Iwn));
                    Assert.Equal(StorageProtocol.BuildGuildChestTabs(GuildChests.TabsOfGuild(GuildId)), await pipe.Next(Op.Ivl));
                    Assert.Equal(Hex("081610011864"), await pipe.Next(Op.Kbk));
                    Assert.Empty(await pipe.Next(Op.Iwb));
                    Assert.Equal(Hex("0a0c53616372692d4d6173746572"), await pipe.Next(Op.Jlo));
                    Assert.Equal(Hex("0a0c53616372692d4d6173746572"), await pipe.Next(Op.Jlq));

                    // Frames 48-51: in, under a new uid.
                    Assert.True(await StorageHandler.MoveAsync(pipe.ToClient, Kcr(1, uid)));
                    await pipe.Next(Op.Itd);
                    Assert.Equal(ConnectionProtocol.BuildItemGone(uid), await pipe.Next(Op.Ium));
                    await pipe.Next(Op.Iun);
                    var stored = Assert.Single(StorageStacks.ItemsOf(StorageStacks.GuildChest(GuildId, 1)));
                    Assert.NotEqual(uid, stored.Uid);

                    // Frames 61-64: out with -1.
                    Assert.True(await StorageHandler.MoveAsync(pipe.ToClient, Kcr(-1, stored.Uid)));
                    await pipe.Next(Op.Iua);
                    Assert.Equal(ConnectionProtocol.BuildItemGone(stored.Uid), await pipe.Next(Op.Itc));
                    await pipe.Next(Op.Iun);

                    // Frames 38-39: a tab the guild does not have.
                    Assert.True(await GuildChestHandler.SelectTabAsync(pipe.ToClient,
                        ConnectionProtocol.Push(Op.Jll, Hex("1002"))));
                    Assert.Equal(Hex("0801108e05"), await pipe.Next(Op.Lqn));

                    // Frames 68-69.
                    Assert.True(await StorageHandler.CloseAsync(pipe.ToClient));
                    Assert.Equal(Hex("180b"), await pipe.Next(Op.Khd));
                }
            }
            finally
            {
                Forget(leader);
            }
        }

        [Fact]
        public async Task A_rank_without_the_right_does_not_look_put_in_or_take_out()
        {
            const long newcomer = 8_830_100_002, officer = 8_830_100_003, stranger = 8_830_100_004;
            MakeGuild();
            GuildStore.Join(newcomer, GuildId, GuildStore.RankNewcomer);
            GuildStore.Join(officer, GuildId, 2);
            Forget(newcomer, officer, stranger);

            try
            {
                // A newcomer (rank 4, no rights at all) is refused the chest itself.
                await using (var pipe = await ClientPipe.OpenAsync(8_830_000_102, newcomer, BankMap))
                {
                    using (SessionContext.Push(pipe.Session))
                    {
                        await GuildChestHandler.OpenAsync(pipe.ToClient, Chest, GuildChests.UseSkill);
                        await pipe.Next(Op.Iwn);
                        Assert.Equal(Hex("0801108e05"), await pipe.Next(Op.Lqn));
                        Assert.False(StorageHandler.IsOpen);
                    }
                }

                // Somebody in no guild: "Debes formar parte de un gremio...", 659.
                await using (var pipe = await ClientPipe.OpenAsync(8_830_000_104, stranger, BankMap))
                {
                    using (SessionContext.Push(pipe.Session))
                    {
                        await GuildChestHandler.OpenAsync(pipe.ToClient, Chest, GuildChests.UseSkill);
                        await pipe.Next(Op.Iwn);
                        Assert.Equal(ConnectionProtocol.BuildInfoMessage(InfoMessages.Warning, GuildChests.NoGuild), await pipe.Next(Op.Lqn));
                        Assert.False(StorageHandler.IsOpen);
                    }
                }

                // Rank 2 opens it; with "Dejar" taken off the rank, putting in is refused (655) and nothing moves.
                var rank2 = GuildStore.Ranks(GuildId).Single(r => r.Id == 2);
                rank2.Rights = rank2.Rights.Where(r => r != GuildChests.Tabs[0].DepositRight).ToArray();
                GuildStore.SaveRank(rank2);

                await using (var pipe = await ClientPipe.OpenAsync(8_830_000_103, officer, BankMap))
                {
                    using (SessionContext.Push(pipe.Session))
                    {
                        long uid = DatabaseManager.NextItemUid();
                        Assert.True(DatabaseManager.InsertCharacterItem(uid, officer, 17626, 1, Equipment.Bag, null));

                        await GuildChestHandler.OpenAsync(pipe.ToClient, Chest, GuildChests.UseSkill);
                        await pipe.Take(6);
                        Assert.True(StorageHandler.IsOpen);

                        Assert.True(await StorageHandler.MoveAsync(pipe.ToClient, Kcr(1, uid)));
                        Assert.Equal(ConnectionProtocol.BuildInfoMessage(InfoMessages.Warning, GuildChests.NoRightToPut), await pipe.Next(Op.Lqn));
                        Assert.Empty(StorageStacks.ItemsOf(StorageStacks.GuildChest(GuildId, 1)));
                        Assert.Equal(1, HavenBagStore.FromInventory(officer, uid)?.Quantity);
                    }
                }
            }
            finally
            {
                Forget(newcomer, officer, stranger);
            }
        }
    }
}
