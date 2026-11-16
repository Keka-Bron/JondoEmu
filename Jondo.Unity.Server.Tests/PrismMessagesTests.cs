using Jondo.Unity.Protocol.Game;
using Jondo.Unity.Server.Managers;
using Jondo.Unity.Server.Network;
using Xunit;

namespace Jondo.Unity.Server.Tests;

public sealed class PrismMessagesTests
{
    private static readonly AllianceStore.Alliance Alliance = new()
    {
        Name = "DistinctAlliance", Uuid = "00000000-0000-4000-8000-000000009671", Tag = "XYZ",
        EmblemSymbol = 101, EmblemSymbolColor = 202, EmblemBackground = 303, EmblemSymbolRgb = 404,
    };

    [Theory]
    [InlineData(0, 0, 0)]
    [InlineData(1, 7_000_000_003, 1)]
    [InlineData(2, 7_000_000_003, 2)]
    [InlineData(3, 0, 3)]
    public void Foreign_state_omits_owner_fields_and_preserves_date_units_and_alliance(int state, long next, int expectedState)
    {
        var messages = new PrismMessages(new((_, _, _, _) => throw new InvalidOperationException()),
            _ => throw new InvalidOperationException("Foreign crystals must not be loaded."));
        var prism = new PrismStore.Prism { State = state, NextChangeUtcMs = next, PlacedUtcMs = 1_784_886_505_835, CrystalGid = 91_001 };
        var result = messages.State(prism, Alliance);
        Assert.Equal((PrismState)expectedState, result.State);
        Assert.Equal(next > 0 && state != 0 ? next : prism.PlacedUtcMs, result.NextEvolutionDate);
        Assert.Equal(1_784_886_505, result.PlacementDate);
        Assert.Equal("XYZ", result.AllianceInformation!.Tag);
        Assert.Equal(Alliance.Uuid, result.AllianceInformation.Uid);
        Assert.Equal(new SocialEmblem(101, 202, 303, 404), result.AllianceInformation.Emblem);
        Assert.Null(result.CristalNumberLeft);
        Assert.Null(result.Cristal);
    }

    [Theory]
    [InlineData(0, 4)]
    [InlineData(4, 0)]
    [InlineData(7, 0)]
    public void Own_crystal_absence_retains_optional_zero_and_both_required_item_wrappers(int used, int left)
    {
        var messages = new PrismMessages(new((_, _, _, _) => throw new InvalidOperationException()),
            _ => throw new InvalidOperationException("Empty slots must not load item effects."));
        var result = messages.State(new() { CrystalsUsed = used }, Alliance, own: true);
        Assert.Equal(left, result.CristalNumberLeft);
        Assert.NotNull(result.Cristal);
        Assert.Equal(PrismCristalType.NoCristal, result.Cristal.Type);
        var inventory = result.Cristal.CristalObject;
        Assert.Equal(0, inventory.Position);
        Assert.False(inventory.Favorite);
        Assert.Equal(0, inventory.Item.Gid);
        Assert.Equal(0, inventory.Item.Quantity);
        Assert.Equal(0, inventory.Item.Uid);
        Assert.Empty(inventory.Item.Effects);
        Assert.Empty(inventory.Item.StorageQuantities);
    }

    [Theory]
    [InlineData(PrismStore.Normal, PrismCristalType.NoCristal)]
    [InlineData(PrismStore.Pacified, PrismCristalType.Protective)]
    public void Own_equipped_crystal_has_detached_effects_and_checked_synthetic_uid(int state, PrismCristalType expected)
    {
        var source = new List<Equipment.ItemEffect> { new(91_011, 73, 0, 0), new(91_012, 0, 0, 0, "original text"), new(-1, 1, 0, 0) };
        var loaded = new List<int>();
        var messages = new PrismMessages(new((_, value, _, _) => (EffectFields.AsNumber, value, 0, 0)),
            gid => { loaded.Add(gid); return source; });
        var prism = new PrismStore.Prism { Id = 67_123, CrystalGid = 91_001, State = state, CrystalsUsed = 2 };
        var result = messages.State(prism, Alliance, own: true);
        source.Clear();
        prism.CrystalGid = 0;
        prism.State = PrismStore.Weakened;
        var crystal = result.Cristal!;
        Assert.Equal(expected, crystal.Type);
        Assert.Equal(2, result.CristalNumberLeft);
        Assert.Equal(91_001, crystal.CristalObject.Item.Gid);
        Assert.Equal(2_000_067_123, crystal.CristalObject.Item.Uid);
        Assert.Equal(1, crystal.CristalObject.Item.Quantity);
        Assert.Equal(new[] { 91_001 }, loaded);
        Assert.Collection(crystal.CristalObject.Item.Effects,
            effect => Assert.Equal(new ObjectEffect(91_011, new ObjectEffectCaseValueInt(73)), effect),
            effect => Assert.Equal(new ObjectEffect(91_012, new ObjectEffectCaseValueString("original text")), effect));
        Assert.Throws<OverflowException>(() => messages.State(new() { Id = int.MaxValue, CrystalGid = 91_001 }, Alliance, true));
    }

    [Fact]
    public void Unsupported_state_and_crystal_source_failures_propagate()
    {
        var failure = new IOException("crystal catalogue failed");
        var messages = new PrismMessages(ItemEffectMessages.FromCatalogue, _ => throw failure);
        Assert.Throws<InvalidOperationException>(() => messages.State(new() { State = 17 }, Alliance));
        Assert.Same(failure, Assert.Throws<IOException>(() => messages.State(new() { CrystalGid = 91_001 }, Alliance, true)));
    }
}
