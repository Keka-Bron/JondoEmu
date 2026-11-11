using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Jondo.Unity.Protocol.Game;
using Jondo.Unity.Protocol.Game.Wire;
using Jondo.Unity.Server;
using Jondo.Unity.Server.Managers;
using Jondo.Unity.Server.Network;
using Jondo.Unity.Tests.Economy;
using Microsoft.Data.Sqlite;
using Xunit;

namespace Jondo.Unity.Tests.Sessions;

[Collection("forgemagic")]
public sealed class AfterSelectionFlowTests
{
    private static readonly GameCodec Wire = new();

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(8)]
    public void Catalogue_snapshots_use_exact_piece_tiers_and_detached_lists(int pieces)
    {
        int[] templates = [2411, 2414, 2416, 2419, 2422, 2425, 2428, 18666];
        var worn = templates.Take(pieces).ToArray();
        var snapshots = ItemSets.Equipped(worn.Append(int.MaxValue));
        if (pieces == 0) { Assert.Empty(snapshots); return; }
        var snapshot = Assert.Single(snapshots);
        Assert.Equal(1, snapshot.Id);
        Assert.Equal(worn, snapshot.Templates);
        (int, long)[] expected = pieces switch
        {
            1 => [],
            2 => [(118, 5), (126, 5), (125, 5)],
            3 => [(118, 10), (126, 10), (125, 10)],
            8 => [(430, 5), (422, 5), (118, 50), (126, 50), (424, 5), (111, 1), (125, 50)],
            _ => throw new Exception("Unexpected test tier"),
        };
        Assert.Equal(expected, snapshot.Effects);
        Assert.Equal(expected, ItemSets.BonusesFor(worn));
        ((int[])snapshot.Templates)[0] = int.MaxValue;
        if (pieces > 1) ((ValueTuple<int, long>[])snapshot.Effects)[0] = (-1, -1);
        var again = Assert.Single(ItemSets.Equipped(worn));
        Assert.Equal(worn, again.Templates);
        Assert.Equal(expected, again.Effects);
    }

    [Fact]
    public void Multiple_sets_are_sorted_and_single_piece_cosmetic_sets_are_not_lost()
    {
        var sets = ItemSets.Equipped([19596, 2411, 2414]);
        Assert.Equal(new[] { 1, 952 }, sets.Select(set => set.Id));
        Assert.Equal(new[] { 19596 }, sets[1].Templates);
        Assert.Empty(sets[1].Effects);
    }

    [Fact]
    public async Task Framed_after_selection_reads_owned_inventory_catalogue_and_account_storage_on_each_entry()
    {
        const long account = 8_299_101, otherAccount = 8_299_102;
        const long character = 8_299_001, otherCharacter = 8_299_002;
        Bank.Initialize();
        Forget();
        try
        {
            Assert.True(await Bank.AddKamasAsync(account, 9_000_000_001));
            Assert.True(await Bank.AddKamasAsync(otherAccount, 17));
            foreach (var (template, quantity, position) in new[] { (2411, 1, 0), (2414, 1, 2), (303, 7, Equipment.Bag) })
                Assert.True(DatabaseManager.InsertCharacterItem(DatabaseManager.NextItemUid(), character, template, quantity, position, null));
            var ownCodec = new CountingCodec();
            var otherCodec = new CountingCodec();
            await using var own = await ClientPipe.OpenAsync(account, character, 191105026, codec: ownCodec);
            await using var other = await ClientPipe.OpenAsync(otherAccount, otherCharacter, 191105026, codec: otherCodec);
            using (SessionContext.Push(own.Session)) Equipment.LoadFrom(character);
            own.Session.State.Kamas = 123;
            own.Session.State.StatStrength = 17;
            own.Session.State.ScrolledStrength = 3;
            other.Session.State.Kamas = 456;

            using var context = SessionContext.Push(other.Session);
            using var turn = await own.Session.EnterTurnAsync();
            var first = await Send(own);
            var inventory = Assert.Single(first.OfType<InventoryContentEvent>());
            Assert.Equal(123, inventory.Kamas);
            Assert.Equal(3, inventory.Objects.Count);
            int carried = DatabaseManager.GetItemRealWeight(2411) + DatabaseManager.GetItemRealWeight(2414)
                + 7 * DatabaseManager.GetItemRealWeight(303);
            Assert.True(carried > 0);
            Assert.Equal(new InventoryWeightEvent(carried, 1100), Assert.Single(first.OfType<InventoryWeightEvent>()));
            Assert.Equal(new StorageKamasUpdateEvent(9_000_000_001), Assert.Single(first.OfType<StorageKamasUpdateEvent>()));
            var set = Assert.Single(first.OfType<SetUpdateEvent>());
            Assert.Equal(1, set.SetId);
            Assert.Equal(new[] { 2411, 2414 }, set.ObjectsUid);
            Assert.Equal(new ObjectEffect[] { new(118, new ObjectEffectCaseValueInt(5)),
                new(126, new ObjectEffectCaseValueInt(5)), new(125, new ObjectEffectCaseValueInt(5)) }, set.Effects);
            Assert.Empty(Assert.Single(first.OfType<TagStoragesRefreshEvent>()).TagInformation);
            Assert.False(Assert.Single(first.OfType<SpouseStatusEvent>()).HasSpouse);
            Assert.Empty(Assert.Single(first.OfType<BonusesEvent>()).Bonuses);
            Assert.Equal(-1, Assert.Single(first.OfType<CharacterCapabilitiesEvent>()).GuildEmblemSymbolCategory);
            var colors = Assert.Single(first.OfType<CharacterAppearanceCollectionEvent>()).CharacterColors!;
            Assert.Equal(1, colors.SlotsUnlocked);
            Assert.Equal(1, colors.SlotsTotal);
            Assert.Single(colors.ColorPalettes);
            var configuration = Assert.Single(first.OfType<ConfigurationEvent>()).GuildConfiguration!;
            Assert.Equal(new GuildActivityConfiguration(32441, 12, 3, 2, 32442, 100), configuration.Activity);
            Assert.Equal(new GuildContributionConfiguration(10000, 10, 100, 10), configuration.Contribution);
            var firstLook = Assert.Single(first.OfType<OutfitLookUpdatedEvent>());
            Assert.Equal(ConnectionProtocol.LookIdOf(character), firstLook.OutfitId);
            Assert.NotNull(firstLook.Look);

            // A later entry observes current state and storage, never a cached initial snapshot.
            own.Session.State.EquipmentItems.Clear();
            Assert.True(await Bank.AddKamasAsync(account, 13));
            var second = await Send(own);
            Assert.Empty(second.OfType<SetUpdateEvent>());
            Assert.Empty(Assert.Single(second.OfType<InventoryContentEvent>()).Objects);
            Assert.Equal(new InventoryWeightEvent(0, 1100), Assert.Single(second.OfType<InventoryWeightEvent>()));
            Assert.Equal(new StorageKamasUpdateEvent(9_000_000_014), Assert.Single(second.OfType<StorageKamasUpdateEvent>()));
            Assert.Equal(0, otherCodec.Encodes);
            Assert.Equal(65, ownCodec.Encodes);
            Assert.Equal(0, ownCodec.Decodes);
            using (SessionContext.Push(own.Session))
            using (await other.Session.EnterTurnAsync())
            {
                var others = await Send(other);
                Assert.Equal(new StorageKamasUpdateEvent(17), Assert.Single(others.OfType<StorageKamasUpdateEvent>()));
                Assert.Equal(456, Assert.Single(others.OfType<InventoryContentEvent>()).Kamas);
                Assert.Equal(new InventoryWeightEvent(0, 1000), Assert.Single(others.OfType<InventoryWeightEvent>()));
                Assert.Empty(others.OfType<SetUpdateEvent>());
                Assert.Equal(ConnectionProtocol.LookIdOf(otherCharacter), Assert.Single(others.OfType<OutfitLookUpdatedEvent>()).OutfitId);
                Assert.NotEqual(firstLook.OutfitId, Assert.Single(others.OfType<OutfitLookUpdatedEvent>()).OutfitId);
            }
            Assert.Equal(32, otherCodec.Encodes);
            Assert.Equal(0, otherCodec.Decodes);
            Assert.Equal(65, ownCodec.Encodes);
            Assert.Same(other.Session, SessionContext.Actual);
            await own.Quiet();
            await other.Quiet();
        }
        finally { Forget(); }

        static void Forget()
        {
            using var connection = new SqliteConnection(DatabaseManager.WorldConnectionString);
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = "DELETE FROM BankAccounts WHERE AccountId IN ($a,$b); DELETE FROM CharacterItems WHERE CharacterId IN ($c,$d);";
            command.Parameters.AddWithValue("$a", account);
            command.Parameters.AddWithValue("$b", otherAccount);
            command.Parameters.AddWithValue("$c", character);
            command.Parameters.AddWithValue("$d", otherCharacter);
            command.ExecuteNonQuery();
        }
    }

    private static async Task<GamePayload[]> Send(ClientPipe pipe)
    {
        var state = pipe.Session.State;
        var initial = new GameEntryInitialMessages([], [], state, new([], _ => []), 0, 0, () => new([]),
            () => new("1970-01-01T00:00:00.000Z", []), () => new([]),
            () => CharacterSelectionMessages.Success(new() { Id = state.CharacterId, Name = "Selected", Level = 1 }, new([], 3, 1, [], [], [])),
            () => InventoryMessages.FromCatalogue.Snapshot(state), () => new([]), () => null, () => null, AfterSelectionMessages.Live,
            InitialPresentationMessages.Live(new() { Id = state.CharacterId, Name = "Selected", Breed = 1, Level = 1 }));
        int count = await initial.SendAsync(pipe.Session)
            .WaitAsync(TimeSpan.FromSeconds(5));
        var result = new List<GamePayload>();
        for (int i = 0; i < count; i++)
        {
            byte[] packet = await Jondo.Protocol.NetworkMessage.ReadFrameAsync(pipe.FromServer).WaitAsync(TimeSpan.FromSeconds(5));
            var frame = Wire.Decode(packet);
            Assert.Null(frame.Uid);
            result.Add(frame.Content);
        }
        Assert.IsType<BonusesEvent>(result[^6]);
        Assert.IsType<CharacterLoadingCompleteEvent>(result[^5]);
        Assert.IsType<OutfitLookUpdatedEvent>(result[^1]);
        return result.ToArray();
    }

    private sealed class CountingCodec : IGameCodec
    {
        internal int Encodes { get; private set; }
        internal int Decodes { get; private set; }
        public GameFrame Decode(ReadOnlySpan<byte> bytes) { Decodes++; return Wire.Decode(bytes); }
        public byte[] Encode(GameFrame frame) { Encodes++; return Wire.Encode(frame); }
    }
}
