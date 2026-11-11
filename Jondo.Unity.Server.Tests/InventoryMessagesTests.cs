using Jondo.Unity.Protocol;
using Jondo.Unity.Protocol.Game;
using Jondo.Unity.Protocol.Game.Wire;
using Jondo.Unity.Server.Handlers;
using Jondo.Unity.Server.Managers;
using Jondo.Unity.Server.Network;
using Xunit;

namespace Jondo.Unity.Server.Tests;

public sealed class InventoryMessagesTests
{
    private static readonly GameCodec Wire = new();
    private static readonly InventoryMessages Messages = new(new ItemEffectMessages((id, value, num, side) =>
        id == 91_002 ? (EffectFields.AsDice, value, num, side) : (EffectFields.AsNumber, value, 0, 0)));

    [Fact]
    public void Snapshot_keeps_kamas_item_and_effect_order_positions_quantities_and_values()
    {
        var state = new SessionState { Kamas = 9_000_000_000 };
        var amulet = Item(int.MaxValue, 81_001, 0, 1);
        amulet.Effects.AddRange([new(91_001, 0, 0, 0), new(-1, 100, 0, 0),
            new(91_002, 12, 5_000_000_000, 28), new(988, 0, 0, 0, "Crafter")]);
        var stack = Item(71_001, 81_002, Equipment.Bag, 7);
        state.EquipmentItems.Add(amulet.Uid, amulet);
        state.EquipmentItems.Add(stack.Uid, stack);

        var snapshot = Messages.Snapshot(state);

        Assert.Equal(9_000_000_000, snapshot.Kamas);
        Assert.Collection(snapshot.Objects,
            first =>
            {
                Assert.Equal(0, first.Position);
                Assert.False(first.Favorite);
                Assert.Equal(81_001, first.Item.Gid);
                Assert.Equal(int.MaxValue, first.Item.Uid);
                Assert.Equal(1, first.Item.Quantity);
                Assert.Empty(first.Item.StorageQuantities);
                Assert.Equal(new ObjectEffect[] { new(91_001, new ObjectEffectCaseValueInt(0)),
                    new(91_002, new ObjectEffectCaseValueDice(new(12, 5_000_000_000, 28))),
                    new(988, new ObjectEffectCaseValueString("Crafter")) }, first.Item.Effects);
            },
            second =>
            {
                Assert.Equal(Equipment.Bag, second.Position);
                Assert.False(second.Favorite);
                Assert.Equal(81_002, second.Item.Gid);
                Assert.Equal(71_001, second.Item.Uid);
                Assert.Equal(7, second.Item.Quantity);
                Assert.Empty(second.Item.Effects);
                Assert.Empty(second.Item.StorageQuantities);
            });
    }

    [Theory]
    [InlineData(-10)]
    [InlineData(0)]
    public void Nonpositive_quantities_keep_the_existing_minimum_one_policy(int quantity)
    {
        var state = new SessionState();
        state.EquipmentItems.Add(71_001, Item(71_001, 81_001, Equipment.Bag, quantity));
        Assert.Equal(1, Assert.Single(Messages.Snapshot(state).Objects).Item.Quantity);
    }

    [Fact]
    public void Snapshot_is_detached_from_later_item_effect_and_session_changes()
    {
        var state = new SessionState { Kamas = 123 };
        var item = Item(71_001, 81_001, 0, 7);
        item.Effects.Add(new(91_001, 42, 0, 0));
        state.EquipmentItems.Add(item.Uid, item);
        var snapshot = Messages.Snapshot(state);
        state.Kamas = 999;
        item.Template = 81_002;
        item.Uid = 71_002;
        item.Position = Equipment.Bag;
        item.Quantity = 9;
        item.Effects.Clear();
        state.EquipmentItems.Clear();

        Assert.Equal(123, snapshot.Kamas);
        var saved = Assert.Single(snapshot.Objects);
        Assert.Equal(0, saved.Position);
        Assert.Equal(81_001, saved.Item.Gid);
        Assert.Equal(71_001, saved.Item.Uid);
        Assert.Equal(7, saved.Item.Quantity);
        Assert.Equal(new ObjectEffect(91_001, new ObjectEffectCaseValueInt(42)), Assert.Single(saved.Item.Effects));
    }

    [Fact]
    public async Task Empty_snapshot_still_sends_one_event_with_zero_kamas_and_no_uid()
    {
        var target = Recipient();
        await Messages.SendAsync(target.Session);
        var frame = Assert.Single(target.Frames);
        var inventory = Assert.IsType<InventoryContentEvent>(frame.Content);
        Assert.Equal(0, inventory.Kamas);
        Assert.Empty(inventory.Objects);
        Assert.Null(frame.Uid);
        Assert.Equal(1, target.Codec.Encodes);
        Assert.Equal(0, target.Codec.Decodes);
    }

    [Fact]
    public async Task Each_recipient_uses_its_own_state_and_codec_under_a_conflicting_ambient_context()
    {
        var first = Recipient();
        var second = Recipient();
        first.Session.State.Kamas = 5_000_000_000;
        second.Session.State.Kamas = 17;
        first.Session.State.EquipmentItems.Add(71_001, Item(71_001, 81_001, 0, 2));
        second.Session.State.EquipmentItems.Add(71_002, Item(71_002, 81_002, Equipment.Bag, 9));
        using var context = SessionContext.Push(second.Session);

        await Messages.SendAsync(first.Session);
        await Messages.SendAsync(second.Session);

        var a = Assert.IsType<InventoryContentEvent>(Assert.Single(first.Frames).Content);
        var b = Assert.IsType<InventoryContentEvent>(Assert.Single(second.Frames).Content);
        Assert.Equal(5_000_000_000, a.Kamas);
        Assert.Equal(17, b.Kamas);
        Assert.Equal(71_001, Assert.Single(a.Objects).Item.Uid);
        Assert.Equal(71_002, Assert.Single(b.Objects).Item.Uid);
        Assert.Same(second.Session, SessionContext.Actual);
        Assert.Equal(1, first.Codec.Encodes);
        Assert.Equal(1, second.Codec.Encodes);
        Assert.Equal(0, first.Codec.Decodes + second.Codec.Decodes);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Workshop_inventory_and_close_preserve_order_and_use_the_explicit_session(bool close)
    {
        var target = Recipient();
        var wrong = Recipient();
        target.Session.State.Kamas = 123;
        target.Session.State.EquipmentItems.Add(71_001, Item(71_001, 81_001, 0, 2));
        target.Session.State.Workshop = new();
        wrong.Session.State.Workshop = new();
        using var context = SessionContext.Push(wrong.Session);

        if (close) await WorkshopHandler.CloseAsync(target.Session);
        else await WorkshopHandler.InventoryAsync(target.Session);

        var names = target.Frames.Select(frame => frame.Content.GetType().Name).ToArray();
        Assert.Equal(close ? [nameof(ExchangeLeaveEvent), nameof(InventoryContentEvent), nameof(TagStoragesRefreshEvent)]
            : new[] { nameof(InventoryContentEvent), nameof(TagStoragesRefreshEvent) }, names);
        var inventory = Assert.Single(target.Frames.Select(frame => frame.Content).OfType<InventoryContentEvent>());
        Assert.Equal(123, inventory.Kamas);
        Assert.Equal(71_001, Assert.Single(inventory.Objects).Item.Uid);
        if (close) Assert.Null(target.Session.State.Workshop);
        Assert.NotNull(wrong.Session.State.Workshop);
        Assert.Empty(wrong.Frames);
        Assert.Same(wrong.Session, SessionContext.Actual);
        Assert.Equal(1, target.Codec.Encodes);
        Assert.Equal(0, target.Codec.Decodes);
    }

    [Theory]
    [InlineData("commission", true)]
    [InlineData("commission", false)]
    [InlineData("marketplace", true)]
    public async Task Commission_and_marketplace_closing_use_the_recipient_snapshot_at_the_existing_position(string boundary, bool accepted)
    {
        var names = new List<string>();
        var codec = new SpyCodec(null);
        var target = new GameSession(codec, packet => { names.Add(Wire.ReadEnvelope(packet).Alias); return Task.CompletedTask; });
        var wrong = Recipient();
        target.State.Kamas = 5_000_000_000;
        target.State.EquipmentItems.Add(71_001, Item(71_001, 81_001, 0, 2));
        wrong.Session.State.Kamas = 42;
        using var context = SessionContext.Push(wrong.Session);

        if (boundary == "commission")
            await CommissionHandler.CloseWindowAsync(target, new Commission { Accepted = accepted });
        else
        {
            using var owner = SessionContext.Push(target);
            await MarketplaceHandler.CloseAsync();
        }

        Assert.Equal(boundary == "commission"
            ? accepted ? new[] { Op.Kcl, Op.Khd, Op.Ivx, Op.Hlm } : [Op.Khd, Op.Hlm]
            : [Op.Khd, Op.Ivx, Op.Hlm], names);
        if (accepted)
        {
            var frame = Assert.Single(codec.Frames);
            var inventory = Assert.IsType<InventoryContentEvent>(frame.Content);
            Assert.Equal(5_000_000_000, inventory.Kamas);
            Assert.Equal(71_001, Assert.Single(inventory.Objects).Item.Uid);
            Assert.Null(frame.Uid);
        }
        else Assert.Empty(codec.Frames);
        Assert.Empty(wrong.Frames);
        Assert.Same(wrong.Session, SessionContext.Actual);
    }

    [Theory]
    [InlineData("snapshot")]
    [InlineData("codec")]
    [InlineData("transport")]
    public async Task Inventory_refresh_failure_stops_before_storage_refresh_without_retry(string stage)
    {
        var failure = new IOException("test inventory send failure");
        var codec = new SpyCodec(stage == "codec" ? failure : null);
        int sends = 0;
        var session = new GameSession(codec, _ => { sends++; return Task.FromException(failure); });
        if (stage == "snapshot")
            session.State.EquipmentItems.Add(71_001, Item((long)int.MaxValue + 1, 81_001, 0, 1));

        if (stage == "snapshot") await Assert.ThrowsAsync<OverflowException>(() => WorkshopHandler.InventoryAsync(session));
        else Assert.Same(failure, await Assert.ThrowsAsync<IOException>(() => WorkshopHandler.InventoryAsync(session)));
        Assert.Equal(stage == "snapshot" ? 0 : 1, codec.Frames.Count);
        Assert.All(codec.Frames, frame => Assert.IsType<InventoryContentEvent>(frame.Content));
        Assert.Equal(stage == "transport" ? 1 : 0, sends);
    }

    private sealed class SpyCodec(Exception? failure) : IGameCodec
    {
        internal List<GameFrame> Frames { get; } = [];
        public GameFrame Decode(ReadOnlySpan<byte> payload) => throw new InvalidOperationException("No production decode expected.");
        public byte[] Encode(GameFrame frame)
        {
            Frames.Add(frame);
            if (failure != null) throw failure;
            return Wire.Encode(frame);
        }
    }

    internal static Equipment.Item Item(long uid, int gid, int position, int quantity)
        => new() { Uid = uid, Template = gid, Position = position, Quantity = quantity };

    private static (GameSession Session, List<GameFrame> Frames, GameMessagingTests.CountingCodec Codec) Recipient()
    {
        var frames = new List<GameFrame>();
        var codec = new GameMessagingTests.CountingCodec();
        var session = new GameSession(codec, packet => { frames.Add(Wire.Decode(packet)); return Task.CompletedTask; });
        return (session, frames, codec);
    }
}
