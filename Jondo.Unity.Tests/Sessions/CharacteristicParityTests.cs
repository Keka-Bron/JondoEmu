using System;
using System.Linq;
using Jondo.Unity.Protocol.Game;
using Jondo.Unity.Server;
using Jondo.Unity.Server.Handlers;
using Jondo.Unity.Server.Managers;
using Jondo.Unity.Server.Network;
using Jondo.Unity.World.Fights;
using Xunit;

namespace Jondo.Unity.Tests.Sessions;

[CollectionDefinition("characteristic parity", DisableParallelization = true)]
public sealed class CharacteristicParityCollection;

[Collection("characteristic parity")]
public sealed class CharacteristicParityTests
{
    private static void Add(GameSession session, long uid, int template, int slot, params (int Effect, int Value)[] effects)
    {
        var item = new Equipment.Item { Uid = uid, Template = template, Position = slot };
        item.Effects.AddRange(effects.Select(e => new Equipment.ItemEffect(e.Effect, e.Value, 0, 0)));
        session.State.EquipmentItems[uid] = item;
    }

    private static CharacterCharacteristicDetailedUsable Usable(CharacterCharacteristics sheet, int id)
        => Assert.IsType<CharacterCharacteristicCaseUsable>(sheet.Characteristics.Single(s => s.CharacteristicId == id).CharacteristicCase).Value;
    private static int Total(CharacterCharacteristicDetailedUsable value)
        => value.Base + value.Context + value.Equipment + value.Temporary + value.Additional + value.Alignment;

    [Theory]
    [InlineData(99, 0, 0, 6)]
    [InlineData(100, 0, 0, 7)]
    [InlineData(200, 0, 0, 7)]
    [InlineData(354, 0, 0, 7)]
    [InlineData(99, 8, 3, 6)]
    [InlineData(100, 8, 3, 7)]
    public void AP_MP_contributions_and_caps_agree_without_hiding_equipment(int level, int apCap, int mpCap, int innate)
    {
        var previous = ServerSettings.Current;
        try
        {
            ServerSettings.UseForTests(new() { MaxActionPoints = apCap, MaxMovementPoints = mpCap });
            var own = GameSession.SinSocket();
            own.State.CharacterLevel = level;
            Add(own, 1, 0, 1, (111, 5), (128, 2), (169, 1), (101, 6), (96, 60));
            var wrong = GameSession.SinSocket();
            wrong.State.CharacterLevel = 1;
            using var context = SessionContext.Push(wrong);
            var combat = own.Combat;
            var sheet = CharacteristicsMessages.Live.Snapshot(own).Stats;
            var ap = Usable(sheet, Stat.ActionPoints);
            var mp = Usable(sheet, Stat.MovementPoints);
            Assert.Equal(innate, ap.Base);
            Assert.Equal(5, ap.Equipment); // Weapon AP cost is not an AP bonus.
            Assert.Equal(1, mp.Equipment); // Signed MP penalty remains negative.
            Assert.Equal(apCap > 0 ? Math.Min(innate + 5, apCap) : innate + 5, Total(ap));
            Assert.Equal(mpCap > 0 ? Math.Min(4, mpCap) : 4, Total(mp));
            Assert.Equal(Total(ap), combat.GetPlayerMaxAp());
            Assert.Equal(Total(mp), combat.GetPlayerMaxMp());
            Assert.Equal(0, combat.GetEquipBonus(Stat.Shield));
            Assert.Same(wrong, SessionContext.Actual);
        }
        finally { ServerSettings.UseForTests(previous); }
    }

    [Fact]
    public void Multiple_general_sets_bonus_slots_scrolls_and_alterations_feed_combat_and_owner_sheet()
    {
        var own = GameSession.SinSocket();
        const long character = 8_941_780_001;
        own.State.CharacterId = character;
        own.State.CharacterLevel = 200;
        own.State.StatStrength = 20; own.State.ScrolledStrength = 10;
        own.State.StatVitality = 30; own.State.ScrolledVitality = 5;
        // Two pieces each from sets 1 and 3: +15 strength/vitality, +5 intelligence.
        Add(own, 1, 2411, 0, (118, 7), (125, 30));
        Add(own, 2, 2414, 1);
        Add(own, 3, 2410, 2);
        Add(own, 4, 2413, 3);
        Add(own, 5, 0, BonusSlots.BoostFood, (124, 25));
        Add(own, 6, 0, Equipment.Bag, (118, 999));
        var wrong = GameSession.SinSocket();
        using var context = SessionContext.Push(wrong);
        try
        {
            Assert.NotNull(Alterations.Gain(character, 625, DateTime.UtcNow)); // +50 initiative.
            var combat = own.Combat;
            var sheet = CharacteristicsMessages.Live.Snapshot(own).Stats;
            CharacterCharacteristicDetailed Detail(int id) => Assert.IsType<CharacterCharacteristicCaseDetailed>(
                sheet.Characteristics.Single(s => s.CharacteristicId == id).CharacteristicCase).Value;
            Assert.Equal(22, combat.GetEquipBonus(Stat.Strength));
            Assert.Equal(45, combat.GetEquipBonus(Stat.Vitality));
            Assert.Equal(25, combat.GetEquipBonus(Stat.Wisdom));
            Assert.Equal(1130, combat.GetPlayerMaxHp());
            Assert.Equal(107, combat.GetPlayerInitiative());
            Assert.Equal(Detail(Stat.Strength).Equipment, combat.GetEquipBonus(Stat.Strength));
            Assert.Equal(Detail(Stat.LifePoints).Base + Detail(Stat.Vitality).Base
                + Detail(Stat.Vitality).Additional + Detail(Stat.Vitality).Equipment, combat.GetPlayerMaxHp());
            Assert.Equal(Detail(Stat.Initiative).Base + Detail(Stat.Initiative).Temporary, combat.GetPlayerInitiative());

            using (SessionContext.Push(own))
            {
                var fighter = FightHandler.BuildPlayerFighter(new FightInstance(987_001, 191105026));
                Assert.Equal(1130, fighter.MaxHP);
                Assert.Equal(52, fighter.Strength);
                Assert.Equal(107, fighter.Initiative);
            }
            own.State.EquipmentItems.Remove(2);
            Assert.Equal(1125, own.Combat.GetPlayerMaxHp());
            Assert.Equal(97, own.Combat.GetPlayerInitiative());
            Assert.Equal(1130, combat.GetPlayerMaxHp()); // A captured calculation is detached.
            Assert.Same(wrong, SessionContext.Actual);
        }
        finally { Alterations.Lose(character, 625); Alterations.Forget(character); }
    }

    [Fact]
    public void Mutation_grade_replaces_human_equipment_and_caps_in_owner_and_actual_fighter()
    {
        var previous = ServerSettings.Current;
        try
        {
            ServerSettings.UseForTests(new() { MaxActionPoints = 2, MaxMovementPoints = 2 });
            var own = GameSession.SinSocket();
            own.State.CharacterId = 8_941_780_002;
            own.State.CharacterLevel = 200;
            own.State.StatVitality = 900;
            Add(own, 1, 0, 0, (111, 10), (125, 1000));
            var mutation = new Equipment.Item { Uid = 2, Position = Mutations.Slot };
            mutation.Effects.Add(new(Mutations.TransformsInto, 0, 3577, 1));
            own.State.EquipmentItems[2] = mutation;
            using var context = SessionContext.Push(own);
            var sheet = CharacteristicsMessages.Live.Snapshot(own).Stats;
            var fighter = FightHandler.BuildPlayerFighter(new FightInstance(987_002, 191105026));
            Assert.Equal(3577, fighter.IncarnatedMonster);
            Assert.Equal((10000, 4, 4), (fighter.MaxHP, fighter.MaxAP, fighter.MaxMP));
            Assert.Equal(4, Total(Usable(sheet, Stat.ActionPoints)));
            Assert.Equal(4, Total(Usable(sheet, Stat.MovementPoints)));
        }
        finally { ServerSettings.UseForTests(previous); }
    }
}
