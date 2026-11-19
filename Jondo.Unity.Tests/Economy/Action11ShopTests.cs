using Jondo.Unity.Protocol;
using Jondo.Unity.Server;
using Jondo.Unity.Server.Handlers;
using Jondo.Unity.Server.Managers;
using Jondo.Unity.Server.Network;
using Microsoft.Data.Sqlite;
using Xunit;
using QuestEngine = Jondo.Unity.Server.Managers.Quests;

namespace Jondo.Unity.Tests.Economy;

[CollectionDefinition("Action 11 shops", DisableParallelization = true)]
public sealed class Action11ShopCollection { }

[Collection("Action 11 shops")]
public sealed class Action11ShopTests
{
    private const int Tokageko = 6831, Badge = 27504, Hat = 27423;
    private const long TokagekoMap = 191105026;

    [Fact]
    public async Task Tokageko_grants_one_item_debits_badges_and_persists_the_exchange_date()
    {
        Load();
        const long character = 8_900_113_831;
        await using var pipe = await ClientPipe.OpenAsync(character, character, TokagekoMap);
        using var session = SessionContext.Push(pipe.Session);
        pipe.Session.State.Kamas = 50_000;
        var shop = Assert.IsType<TokenShops.Shop>(TokenShops.Of(Tokageko));
        Assert.Equal(Badge, shop.TokenGid);
        var catalogue = NpcShops.CatalogueOf(Tokageko);
        Assert.Equal(18, catalogue.Count);
        Assert.Equal(new[] { (10L, 13), (15L, 2), (20L, 1), (30L, 1), (60L, 1) },
            catalogue.GroupBy(gid => TokenShops.PriceOf(shop, gid)).OrderBy(g => g.Key)
                .Select(g => (g.Key, g.Count())));

        var catalogueMessage = ProtoMessage.Parse(await Open(pipe, Tokageko, TokagekoMap));
        Assert.Contains(catalogueMessage.Fields, f => f.FieldNumber == 3 && f.VarIntValue == Badge);
        Assert.Equal(TokenShops.PriceOf(shop, Hat), ShopPrice(catalogueMessage, Hat));
        await Buy(pipe, Hat);
        Assert.Empty(DatabaseManager.LoadInventory(character));
        Assert.Equal(50_000, pipe.Session.State.Kamas);

        int cost = (int)TokenShops.PriceOf(shop, Hat);
        long stack = Give(character, Badge, cost - 1);
        await Buy(pipe, Hat);
        Assert.Equal(cost - 1, Assert.Single(DatabaseManager.LoadInventory(character)).Quantity);
        using (var db = new SqliteConnection(DatabaseManager.WorldConnectionString))
        {
            db.Open();
            using var update = db.CreateCommand();
            update.CommandText = "UPDATE CharacterItems SET Quantity = $n WHERE CharacterId = $id AND Uid = $uid;";
            update.Parameters.AddWithValue("$id", character);
            update.Parameters.AddWithValue("$uid", stack);
            update.Parameters.AddWithValue("$n", cost);
            Assert.Equal(1, update.ExecuteNonQuery());
        }
        Equipment.Remove(stack, cost - 1);
        Equipment.Add(stack, Badge, cost, Equipment.Bag, "[]");

        var before = DateTimeOffset.UtcNow;
        await Buy(pipe, Hat);
        var bought = Assert.Single(DatabaseManager.LoadInventory(character));
        Assert.Equal(Hat, bought.ItemId);
        Assert.Equal(1, bought.Quantity);
        Assert.Equal(50_000, pipe.Session.State.Kamas);
        Assert.Equal(0, Equipment.HowMany(Badge));
        var effects = Equipment.ParseEffects(bought.RawEffects);
        var expiry = Assert.Single(effects, e => e.Effect is 981 or 982 or 983);
        Assert.Equal(983, expiry.Effect);
        Assert.InRange(expiry.Value, before.AddDays(180).ToUnixTimeSeconds(),
            DateTimeOffset.UtcNow.AddDays(180).ToUnixTimeSeconds());
        Assert.Equal(EffectFields.AsDate, EffectFields.Shape(expiry.Effect, expiry.Value, 0, 0).Field);
        Assert.True(ItemBinding.BlocksTransfer(effects, before));
        Assert.False(ItemBinding.BlocksTransfer(effects, DateTimeOffset.FromUnixTimeSeconds(expiry.Value)));
        Assert.Null(TradeHandler.Give(bought.Uid, 1));
    }

    [Fact]
    public async Task Niki_shows_and_charges_kamas_and_rejects_a_purchase_until_the_quest_is_complete()
    {
        Load();
        const int seller = 5994, sauce = 23823, quest = 2205;
        const long map = 211295232, character = 8_900_115_994;
        QuestEngine.Load();
        await using var pipe = await ClientPipe.OpenAsync(character, character, map);
        using var session = SessionContext.Push(pipe.Session);
        QuestEngine.LoadFrom(character);
        pipe.Session.State.Kamas = 1999;
        Assert.Null(TokenShops.Of(seller));
        var catalogue = ProtoMessage.Parse(await Open(pipe, seller, map));
        Assert.DoesNotContain(catalogue.Fields, f => f.FieldNumber == 3);
        Assert.DoesNotContain(sauce, ShopItems(catalogue));
        await Buy(pipe, sauce);
        Assert.Empty(DatabaseManager.LoadInventory(character));

        QuestEngine.Log!.Restore(quest, QuestEngine.Book!.Of(quest)!.Steps[^1].Id, null, true);
        Assert.Equal(2000, ShopPrice(ProtoMessage.Parse(await Open(pipe, seller, map)), sauce));
        await Buy(pipe, sauce);
        Assert.Empty(DatabaseManager.LoadInventory(character));
        pipe.Session.State.Kamas = 2500;
        await Buy(pipe, sauce);
        Assert.Equal(sauce, Assert.Single(DatabaseManager.LoadInventory(character)).ItemId);
        Assert.Equal(500, pipe.Session.State.Kamas);
    }

    [Fact]
    public void Veteran_thresholds_and_native_permanent_links_are_preserved()
    {
        Load();
        const int harvey = 2759, groom = 15777, waterClock = 34376, ess = 2002;
        Assert.NotNull(TokenShops.Of(harvey));
        Assert.Equal(14635, TokenShops.Of(harvey)!.TokenGid);
        Assert.DoesNotContain(6660, NpcShops.CatalogueOf(harvey));
        Assert.DoesNotContain(groom, NpcShops.CatalogueFor(harvey, _ => true, () => 1349.99));
        Assert.False(NpcShops.CanBuy(harvey, groom, _ => true));
        Assert.Contains(groom, NpcShops.CatalogueFor(harvey, _ => true, () => 1350));
        Assert.False(NpcShops.CanBuy(ess, waterClock, _ => true, quantity: 2));

        var purchased = NpcShops.PurchasedEffects(harvey, groom, DateTimeOffset.UtcNow);
        var account = Equipment.ParseEffects(purchased);
        var marker = Assert.Single(account, e => e.Effect is 981 or 982 or 983);
        Assert.True(marker.Effect == 983, $"Factory effects: {NpcShops.EffectsOf(groom)}. Purchased effects: {purchased}.");
        Assert.Equal(982, ItemEffectMessages.FromCatalogue.Effect(marker)!.Action);
        Assert.True(ItemBinding.BlocksTransfer(account, DateTimeOffset.UtcNow));
        Assert.False(ItemBinding.CharacterOnly(account));
        var character = Equipment.ParseEffects(NpcShops.PurchasedEffects(ess, 34374, DateTimeOffset.UtcNow));
        Assert.True(ItemBinding.CharacterOnly(character));
        Assert.True(ItemBinding.BlocksTransfer(character, DateTimeOffset.UtcNow));
        Assert.Equal(Enumerable.Range(26265, 15),
            NpcShops.CatalogueOf(6670).Where(id => id is >= 26265 and <= 26279).OrderBy(id => id));
    }

    [Fact]
    public void A_certificate_requires_neither_the_item_nor_the_title()
    {
        Load();
        Titles.Initialize();
        const int rhia = 2346, certificate = 32388, title = 511;
        const long map = 128452097, character = 8_900_112_346;
        var session = GameSession.SinSocket();
        session.State.CharacterId = character;
        session.State.MapId = map;
        using var context = SessionContext.Push(session);
        bool Meets(string rule) => PlayerCriteria.Met(rule, map);

        Assert.Contains(certificate, NpcShops.CatalogueFor(rhia, Meets));
        Assert.False(NpcShops.CanBuy(rhia, certificate, Meets, quantity: 2));
        long uid = Give(character, certificate, 1);
        Assert.False(NpcShops.CanBuy(rhia, certificate, Meets));
        Assert.True(DatabaseManager.DestroyCharacterItem(character, uid, 1));
        Equipment.Remove(uid, 1);
        Titles.Grant(character, new[] { title });
        Assert.DoesNotContain(certificate, NpcShops.CatalogueFor(rhia, Meets));
        Assert.False(NpcShops.CanBuy(rhia, certificate, Meets));
    }

    private static void Load()
    {
        Npcs.Initialize();
        NpcShops.Initialize();
        TokenShops.Initialize();
        EffectFields.Initialize();
        DatabaseManager.EnsureProgressionTables();
    }

    private static async Task<byte[]> Open(ClientPipe pipe, int npc, long map)
    {
        var seller = Assert.Single(Npcs.OnMap(map), n => n.NpcId == npc);
        await NpcHandler.InteractAsync(pipe.Session, ConnectionProtocol.Push(Op.Iov,
            Pb.New().Var(1, Npcs.TradeCosmetics).Var(2, map).Var(3, seller.ContextualId).Build()));
        return await pipe.Next(Op.Kbd);
    }

    private static IEnumerable<int> ShopItems(ProtoMessage catalogue)
        => catalogue.Fields.Where(f => f.FieldNumber == 1).Select(f =>
            (int)ProtoMessage.Parse(f.BytesValue).Fields.Single(value => value.FieldNumber == 1).VarIntValue);

    private static long ShopPrice(ProtoMessage catalogue, int gid)
    {
        var item = catalogue.Fields.Where(f => f.FieldNumber == 1).Select(f => ProtoMessage.Parse(f.BytesValue))
            .Single(row => row.Fields.Any(f => f.FieldNumber == 1 && f.VarIntValue == gid));
        var offer = ProtoMessage.Parse(item.Fields.Single(f => f.FieldNumber == 3).BytesValue);
        return offer.Fields.Single(f => f.FieldNumber == 2).VarIntValue;
    }

    private static async Task Buy(ClientPipe pipe, int gid)
        => await NpcHandler.BuyAsync(pipe.ToClient, ConnectionProtocol.Push(Op.Kea, Pb.New().Var(1, gid).Var(2, 1).Build()));

    private static long Give(long character, int gid, int quantity)
    {
        long uid = DatabaseManager.InsertNewCharacterItem(character, gid, quantity, Equipment.Bag, "[]");
        Assert.True(uid > 0);
        Equipment.Add(uid, gid, quantity, Equipment.Bag, "[]");
        return uid;
    }
}
