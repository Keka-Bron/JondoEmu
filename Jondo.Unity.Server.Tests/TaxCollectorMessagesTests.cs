using Jondo.Unity.Protocol.Game;
using Jondo.Unity.Protocol.Game.Wire;
using Jondo.Unity.Server.Managers;
using Jondo.Unity.Server.Network;
using Xunit;

namespace Jondo.Unity.Server.Tests;

public sealed class TaxCollectorMessagesTests
{
    [Fact]
    public void Collector_snapshot_keeps_worn_effects_and_characteristic_cases_detached()
    {
        var collector = new TaxCollectorStore.TaxCollector
        {
            Uuid = "collector", OwnerId = 5_000_000_001, OwnerName = "Placer", MapId = 5_000_000_002,
            FirstNameId = 35, LastNameId = 109, SubAreaId = 42, PlacedUtcS = 1_800_000_000,
        };
        var alliance = new AllianceStore.Alliance { Name = "Alliance", Uuid = "alliance", Tag = "ALL",
            EmblemSymbolColor = 17, EmblemBackgroundShape = 28 };
        var equipment = new List<HavenBagStore.StoredItem> { new() { Uid = 19, Gid = 83, Quantity = 1, Effects = "[[118,23]]" } };
        var spells = new SortedDictionary<int, int> { [0] = 168, [2] = 202 };
        var sheet = new List<(int, long, long)> { (Stat.ActionPoints, 6, 2), (Stat.Prospecting, 200, 50), (Stat.EnergyPoints, 700, 15) };
        var effects = new ItemEffectMessages((_, value, _, _) => (EffectFields.AsNumber, value, 0, 0));
        var snapshot = TaxCollectorMessages.Information(collector, alliance, TaxCollectorState.StateWaitingForHelp,
            -4, 9, 37, 6_000_000_001, equipment, spells, sheet, _ => 279, effects);
        equipment.Clear();
        spells.Clear();
        sheet.Clear();

        var codec = new GameCodec();
        var envelope = codec.Decode(codec.Encode(new(new AllianceInsiderInformationEvent([snapshot], [], null, []))));
        Assert.Null(envelope.Uid);
        var result = Assert.Single(Assert.IsType<AllianceInsiderInformationEvent>(envelope.Content).TaxCollectors);
        Assert.Equal(new MapCoordinates(-4, 9), result.Coordinates);
        Assert.Equal((35, 109), (result.FirstNameId, result.LastNameId));
        Assert.Equal((collector.OwnerId, collector.MapId, collector.PlacedUtcS), (result.CallerId, result.MapId, result.HiredDate));
        Assert.Equal(new TaxCollectorLootInformation(37, 6_000_000_001), Assert.IsType<TaxCollectorComplementaryInformationCaseLootInformation>(
            Assert.Single(result.ComplementaryInformation).ComplementaryInformationCase).Value);
        var worn = Assert.Single(result.Equipments);
        Assert.Equal(5, worn.Position);
        Assert.Equal(19, worn.Item.Uid);
        Assert.Equal(new ObjectEffect(118, new ObjectEffectCaseValueInt(23)), Assert.Single(worn.Item.Effects));
        Assert.Equal(new[] { new TaxCollectorOrderedSpell(168, 0), new TaxCollectorOrderedSpell(202, 2) }, result.Spells);
        Assert.Equal(2, Assert.IsType<CharacterCharacteristicCaseUsable>(result.CharacterCharacteristics[0].CharacteristicCase).Value.Equipment);
        Assert.Equal(50, Assert.IsType<CharacterCharacteristicCaseDetailed>(result.CharacterCharacteristics[1].CharacteristicCase).Value.Equipment);
        Assert.Equal(715, Assert.IsType<CharacterCharacteristicCaseValue>(result.CharacterCharacteristics[2].CharacteristicCase).Value.Total);
        Assert.Equal(17, result.Alliance!.Emblem!.BackgroundShapeId);
    }
}
