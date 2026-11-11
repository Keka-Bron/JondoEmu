using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Jondo.Unity.Protocol;
using Jondo.Unity.Server;
using Jondo.Unity.Server.Handlers;
using Jondo.Unity.Server.Managers;
using Jondo.Unity.Server.Network;
using Microsoft.Data.Sqlite;
using Xunit;

namespace Jondo.Unity.Tests.Economy
{
    /// <summary>
    /// Recycling and the alliance's nugget pot: the client's reckoning, the neutral prism, a tax
    /// collector's collection shared with the pot, and the pot handed out into the banks.
    /// </summary>
    [Collection("forgemagic")]
    public sealed class RecyclingTests
    {
        /// <summary>A resource worth nuggets, favoured in subarea 103.</summary>
        internal const int Resource = 274;

        /// <summary>A resource only bosses drop.</summary>
        internal const int BossDrop = 840;

        /// <summary>A recipe of two base resources: 1673 ×1 and 377 ×3.</summary>
        private const int Crafted = 49;

        private const long CharacterA = 8_930_100_001, AccountA = 8_930_000_001;

        public RecyclingTests()
        {
            if (RecipeManager.Count == 0) RecipeManager.Initialize();
            if (MapManager.GetMapInfo(RecycleHandler.PrismMap) == null) MapManager.Initialize();
            Recycling.Forget();
        }

        internal static double Worth(int gid)
        {
            using var connection = new SqliteConnection(DatabaseManager.WorldConnectionString);
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = "SELECT json_extract(Data, '$.recyclingNuggets') FROM ItemTemplates WHERE Id = $id;";
            command.Parameters.AddWithValue("$id", gid);
            return Convert.ToDouble(command.ExecuteScalar());
        }

        internal static void ClearBag(long character)
        {
            using var connection = new SqliteConnection(DatabaseManager.WorldConnectionString);
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = "DELETE FROM CharacterItems WHERE CharacterId = $c;";
            command.Parameters.AddWithValue("$c", character);
            command.ExecuteNonQuery();
        }

        internal static byte[] Kcr(int delta, long uid) => ConnectionProtocol.Push(Op.Kcr, Pb.New().Var(1, delta).Var(2, uid).Build());
        internal static byte[] Kep() => ConnectionProtocol.Push(Op.Kep, Pb.New().Var(1, 1).Build());

        internal static long Var(byte[] body, int field)
            => ProtoMessage.Parse(body).Fields.FirstOrDefault(f => f.FieldNumber == field && f.WireType == 0)?.VarIntValue ?? 0;

        internal static async Task<byte[]> Until(ClientPipe pipe, string opcode)
        {
            while (true)
            {
                var (got, body) = await pipe.NextAny();
                if (got == opcode) return body;
            }
        }

        [Fact]
        public void Nuggets_are_reckoned_as_the_client_estimates_them()
        {
            double resource = Worth(Resource);
            Assert.True(resource > 0);
            Assert.Equal(10 * resource, Recycling.NuggetsOf(Resource, 10, 0), 9);
            // In a subarea it is favoured in: ×1.5.
            Assert.Equal(1.5 * Recycling.NuggetsOf(Resource, 10, 0), Recycling.NuggetsOf(Resource, 10, 103), 9);
            // Only bosses drop it: ×3.
            Assert.Equal(3 * Worth(BossDrop), Recycling.NuggetsOf(BossDrop, 1, 0), 9);
            // An item with a recipe breaks into its base resources, ×1.5.
            Assert.Equal(1.5 * 2 * (1 * Worth(1673) + 3 * Worth(377)), Recycling.NuggetsOf(Crafted, 2, 0), 9);

            Assert.Equal(1.0, Recycling.AdjacencyBonus(0, 0));
            Assert.Equal(1.125, Recycling.AdjacencyBonus(1, 3));
            Assert.Equal((5L, 5L), Recycling.Split(10.9, 50, 50, 1.0));
        }

        [Fact]
        public async Task The_neutral_prism_turns_resources_into_nuggets_for_the_player()
        {
            ClearBag(CharacterA);
            try
            {
                await using var pipe = await ClientPipe.OpenAsync(AccountA, CharacterA, RecycleHandler.PrismMap, "Recicla");
                using (SessionContext.Push(pipe.Session))
                {
                    Equipment.LoadFrom(CharacterA);

                    // Under level 50: «No cumples los requisitos».
                    pipe.Session.State.CharacterLevel = 40;
                    await RecycleHandler.OpenAtPrismAsync(pipe.ToClient, RecycleHandler.PrismElement, RecycleHandler.RecycleSkill);
                    await pipe.Next(Op.Iwn);
                    Assert.Equal(ConnectionProtocol.BuildInfoMessage(InfoMessages.Warning, RecycleHandler.NotAllowed), await pipe.Next(Op.Lqn));
                    Assert.Null(pipe.Session.State.Workshop);

                    pipe.Session.State.CharacterLevel = 60;
                    var stack = Equipment.Create(Resource, 20, Array.Empty<Equipment.ItemEffect>())!;
                    await RecycleHandler.OpenAtPrismAsync(pipe.ToClient, RecycleHandler.PrismElement, RecycleHandler.RecycleSkill);
                    await pipe.Next(Op.Iwn);
                    var kga = await pipe.Next("kga");
                    Assert.Equal(100, Var(kga, 2));
                    Assert.Equal(0, Var(kga, 5));

                    Assert.True(await WorkshopHandler.MoveAsync(pipe.ToClient, Kcr(20, stack.Uid)));
                    await pipe.Next(Op.Kfb);

                    long expected = (long)Math.Floor(Recycling.NuggetsOf(Resource, 20,
                        MapManager.GetMapInfo(RecycleHandler.PrismMap)!.SubAreaId));
                    Assert.True(await WorkshopHandler.CraftAsync(pipe.ToClient, Kep()));
                    var kgh = await Until(pipe, "kgh");
                    Assert.Equal(expected, Var(kgh, 2));
                    Assert.Equal(0, Var(kgh, 1));
                    Assert.Equal(ConnectionProtocol.BuildInfoMessage(InfoMessages.Info, RecycleHandler.Recycled,
                        expected.ToString(), Recycling.Nugget.ToString()), await pipe.Next(Op.Lqn));

                    Assert.Null(Equipment.ByUid(stack.Uid));
                    Assert.Equal(expected, Equipment.All.Where(i => i.Template == Recycling.Nugget).Sum(i => i.Quantity));
                }
            }
            finally
            {
                ClearBag(CharacterA);
            }
        }
    }

    /// <summary>
    /// A tax collector's collection recycled half for the pot, and the pot handed out. Among the
    /// alliance tests: it points the alliances at a base of its own.
    /// </summary>
    [Collection("alliances")]
    public sealed class AllianceNuggetsTests : IDisposable
    {
        private const int Resource = RecyclingTests.Resource, BossDrop = RecyclingTests.BossDrop;
        private const long CharacterA = 8_930_100_011, AccountA = 8_930_000_011;
        private readonly string _alliances;

        public AllianceNuggetsTests()
        {
            if (RecipeManager.Count == 0) RecipeManager.Initialize();
            Recycling.Forget();
            _alliances = Path.Combine(Path.GetTempPath(), $"jondo-pepitas-{Guid.NewGuid():N}.db");
            AllianceStore.ConnectionStringOverride = $"Data Source={_alliances}";
        }

        public void Dispose()
        {
            AllianceStore.ConnectionStringOverride = null;
            TestDatabases.ReleasePool(_alliances);
            try { File.Delete(_alliances); } catch (IOException) { }
        }

        private static void ClearBag(long character) => RecyclingTests.ClearBag(character);

        /// <summary>
        /// The alliance's share kept in its prisms by their zones' levels -- the rounding's leftover
        /// to the highest --, taken out loose ones first then prism by prism, and a prism's held
        /// nuggets shared at its war's end: half to the winner, half to the dominant.
        /// </summary>
        [Fact]
        public void The_share_goes_to_the_prisms_and_a_war_shares_it_out()
        {
            const long Alliance = 8_930_500_001, Other = 8_930_500_002;
            const long Low = 8_930_600_001, High = 8_930_600_002;
            void Empty()
            {
                AllianceNuggets.TakeRetained(Low);
                AllianceNuggets.TakeRetained(High);
                foreach (long id in new[] { Alliance, Other }) AllianceNuggets.TryTake(id, AllianceNuggets.PotOf(id));
            }
            Empty();
            try
            {
                AllianceNuggets.Collect(Alliance, 101, new[] { (Low, 50), (High, 150) });
                Assert.Equal(25, AllianceNuggets.RetainedIn(Low));
                Assert.Equal(76, AllianceNuggets.RetainedIn(High));
                Assert.Equal(101, AllianceNuggets.PotOf(Alliance));

                Assert.False(AllianceNuggets.TryTake(Alliance, 102));
                Assert.True(AllianceNuggets.TryTake(Alliance, 30));
                Assert.Equal(0, AllianceNuggets.RetainedIn(Low));
                Assert.Equal(71, AllianceNuggets.RetainedIn(High));

                var (toWinner, toDominant) = AllianceNuggets.ShareOutAfterWar(High, Other, Alliance);
                Assert.Equal((36L, 35L), (toWinner, toDominant));
                Assert.Equal(0, AllianceNuggets.RetainedIn(High));
                Assert.Equal(36, AllianceNuggets.PotOf(Other));
                Assert.Equal(35, AllianceNuggets.PotOf(Alliance));

                // Without prisms it stays loose; a war with nobody else gives the winner all.
                AllianceNuggets.Collect(Other, 4, System.Array.Empty<(long, int)>());
                Assert.Equal(40, AllianceNuggets.PotOf(Other));
                AllianceNuggets.Collect(Alliance, 10, new[] { (Low, 1) });
                Assert.Equal((10L, 0L), AllianceNuggets.ShareOutAfterWar(Low, Alliance, 0));
            }
            finally
            {
                Empty();
            }
        }
        private static byte[] Kcr(int delta, long uid) => RecyclingTests.Kcr(delta, uid);
        private static byte[] Kep() => RecyclingTests.Kep();
        private static long Var(byte[] body, int field) => RecyclingTests.Var(body, field);
        private static Task<byte[]> Until(ClientPipe pipe, string opcode) => RecyclingTests.Until(pipe, opcode);

        [Fact]
        public async Task A_tax_collector_shares_with_the_pot_which_is_handed_out_into_the_banks()
        {
            long characterB = DatabaseManager.CreateCharacter(8_930_000_002, 1, "Pepitero-Dos", 8, 0, 1,
                new long[] { -1, -1, -1, -1, -1 }, 191105026, 60, 0, 0, Array.Empty<(int, int)>());
            Assert.True(characterB > 0);
            ClearBag(CharacterA);
            try
            {
                var alliance = AllianceStore.Create(CharacterA, "Los Pepiteros", "PEP", 1, 2, 3, 0, 4);
                AllianceStore.Join(characterB, alliance.Id, rank: 3);
                AllianceNuggets.TryTake(alliance.Id, AllianceNuggets.PotOf(alliance.Id));
                Assert.Equal(0, AllianceNuggets.PotOf(alliance.Id));

                await using var a = await ClientPipe.OpenAsync(AccountA, CharacterA, 191105026, "Pepitero-Uno");
                await using var b = await ClientPipe.OpenAsync(8_930_000_002, characterB, 191105026, "Pepitero-Dos");
                long pot;
                using (SessionContext.Push(a.Session))
                {
                    Equipment.LoadFrom(CharacterA);
                    const int subArea = 95;
                    Assert.True(await RecycleHandler.OpenForTaxCollectorAsync(a.ToClient, alliance.Id, subArea,
                        new[] { (Resource, 100), (BossDrop, 2) }));
                    var kga = ProtoMessage.Parse(await a.Next("kga")).Fields;
                    Assert.Equal(2, kga.Count(f => f.FieldNumber == 1));
                    Assert.Equal(50, kga.Single(f => f.FieldNumber == 2).VarIntValue);
                    Assert.Equal(50, kga.Single(f => f.FieldNumber == 5).VarIntValue);

                    // The boss drop is kept: out of the window, into the bag.
                    var loot = a.Session.State.Workshop!.Recycle!.Loot.Single(l => l.Value.Gid == BossDrop).Key;
                    await WorkshopHandler.MoveAsync(a.ToClient, Kcr(-2, loot));
                    await Until(a, Op.Kfs);
                    Assert.Equal(2, Equipment.All.Where(i => i.Template == BossDrop).Sum(i => i.Quantity));

                    var session = a.Session.State.Workshop!.Recycle!;
                    var (player, toPot) = Recycling.Split(Recycling.NuggetsOf(Resource, 100, subArea), 50, 50, session.Bonus);
                    Assert.True(player > 0 && toPot > 0);
                    await WorkshopHandler.CraftAsync(a.ToClient, Kep());
                    var kgh = await Until(a, "kgh");
                    Assert.Equal(toPot, Var(kgh, 1));
                    Assert.Equal(player, Var(kgh, 2));
                    await a.Next(Op.Lqn);
                    pot = AllianceNuggets.PotOf(alliance.Id);
                    Assert.Equal(toPot, pot);

                    // Closing: nothing left in the window, nothing more given.
                    await WorkshopHandler.CloseAsync(a.Session);
                    await a.Next(Op.Khd);
                    Assert.Equal(2, Equipment.All.Where(i => i.Template == BossDrop).Sum(i => i.Quantity));

                    // The pot, to the founder, who holds every right.
                    await RecycleHandler.ShowPotAsync(a.ToClient);
                    Assert.Equal(pot, Var(await a.Next("mec"), 1));
                }

                // Handed out: B's share in his bank, the pot the less.
                AllianceNuggets.Add(alliance.Id, 7);
                using (SessionContext.Push(a.Session))
                {
                    var meb = Pb.New().Msg(1, Pb.New().Var(2, characterB).Var(3, 5)).Build();
                    await RecycleHandler.DistributeAsync(a.ToClient, ConnectionProtocol.Push("meb", meb));
                    Assert.Equal(pot + 2, Var(await a.Next("mec"), 1));
                }
                Assert.Equal(pot + 2, AllianceNuggets.PotOf(alliance.Id));
                Assert.Equal(ConnectionProtocol.BuildInfoMessage(InfoMessages.Info, RecycleHandler.SharedInBank,
                    "Pepitero-Dos", "5", "Pepitero-Uno", "Los Pepiteros"), await b.Next(Op.Lqn));
                Assert.Contains(Bank.ItemsOf(8_930_000_002), i => i.Gid == Recycling.Nugget && i.Quantity == 5);

                // B has no right: he cannot hand it out.
                using (SessionContext.Push(b.Session))
                {
                    var meb = Pb.New().Msg(1, Pb.New().Var(2, CharacterA).Var(3, 1)).Build();
                    await RecycleHandler.DistributeAsync(b.ToClient, ConnectionProtocol.Push("meb", meb));
                }
                Assert.Equal(pot + 2, AllianceNuggets.PotOf(alliance.Id));
            }
            finally
            {
                ClearBag(CharacterA);
                DatabaseManager.DeleteCharacter(characterB, 8_930_000_002);
                using var connection = new SqliteConnection(DatabaseManager.WorldConnectionString);
                connection.Open();
                using var command = connection.CreateCommand();
                command.CommandText = "DELETE FROM BankItems WHERE AccountId = 8930000002;";
                try { command.ExecuteNonQuery(); } catch (SqliteException) { }
            }
        }
    }
}
