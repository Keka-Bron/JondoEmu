using System;
using System.Linq;
using System.Threading.Tasks;
using Jondo.Unity.Server.Handlers;
using Jondo.Unity.Server.Managers;
using Jondo.Unity.Server.Network;
using Jondo.Unity.World.Fights;
using Jondo.Unity.World.Maps;
using Xunit;

namespace Jondo.Unity.Tests.Combat
{
    [Collection("MapManager")]
    public class ForgelanceTests
    {
        private const long Player = 54000000022;
        // LancePierre packets 20 and 55, including colours, equipment and a follower.
        private const string DisarmedLook = "0a18e8b6960fa9fedc13c182851aaaafa72795ab9e2cbffa8032100318012a0137320c95199218be1a870dd313b1283a0c0a08100318bb0b2a01552001";
        private const string ArmedLook = "0a18e8b6960fa9fedc13c182851aaaafa72795ab9e2cbffa8032100318012a0137320e95199218be1a870dd313b128c21c3a0c0a08100318bb0b2a01552001";

        private static Fighter Person(long id = Player, int breed = 20, int cell = 300) => new()
        {
            Id = id, Breed = breed, CellId = cell, Level = 4, CurrentHP = 1000, MaxHP = 1000,
            MaxAP = 6, CurrentAP = 6, MaxMP = 3, CurrentMP = 3,
            IsBot = true, BotLook = Convert.FromHexString(DisarmedLook),
        };

        private static FightInstance Board(Fighter owner, params Fighter[] others)
        {
            var fight = new FightInstance(98765, 0);
            int cell = owner.CellId;
            fight.AddPlayer(owner); owner.CellId = cell;
            foreach (var other in others)
            {
                cell = other.CellId;
                if (other.TeamId == 0) fight.AddPlayer(other); else fight.AddMonster(other);
                other.CellId = cell;
            }
            return fight;
        }

        private static long Value(byte[] body, int field)
            => ProtoMessage.Parse(body).Fields.FirstOrDefault(f => f.FieldNumber == field)?.VarIntValue ?? 0;
        private static byte[] Sub(byte[] body, int field)
            => ProtoMessage.Parse(body).Fields.First(f => f.FieldNumber == field).BytesValue;

        [Theory]
        [InlineData(true, 437, "1896b89a95c90170d01dca010f0801100318b50320e1b60128cea401")]
        [InlineData(false, 411, "1896b89a95c90170d01dca010d1003189b0320e1b60128cea401")]
        public void Script_action_matches_Trident_capture_bytes(bool critical, int cell, string hex)
            => Assert.Equal(Convert.FromHexString(hex), ForgelanceProtocol.Script(Player, 23393, 3, cell, 21070, critical));

        [Fact]
        public void Low_level_script_uses_rank_one_rather_than_the_spell_level_id()
        {
            var body = ForgelanceProtocol.Script(Player, 23754, 1, 300, 21226, false);
            Assert.Equal(3792, Value(body, 14));
            Assert.Equal(1, Value(Sub(body, 25), 2));
            Assert.Equal(300, Value(Sub(body, 25), 3));
        }

        [Fact]
        public void Armed_disarmed_and_guard_looks_preserve_the_rest_of_the_capture()
        {
            var owner = Person();
            var armed = ForgelanceProtocol.Look(Convert.FromHexString(DisarmedLook), owner, 2061);
            // Compare parsed fields: the skin field can be serialized after the follower.
            AssertLook(Convert.FromHexString(ArmedLook), armed);
            var guard = ForgelanceProtocol.Look(armed, owner, 2062);
            Assert.Equal(7156, Value(guard, 3));
            AssertLook(armed, ForgelanceProtocol.Look(guard, owner, 2061));
            owner.Buffs.PonerEstado(3361);
            AssertLook(Convert.FromHexString(DisarmedLook), ForgelanceProtocol.Look(guard, owner, 0));
            AssertLook(armed, ForgelanceProtocol.Look(armed, Person(), 2061));
        }

        private static void AssertLook(byte[] expected, byte[] actual)
        {
            var a = ProtoMessage.Parse(expected).Fields.OrderBy(f => f.FieldNumber).ToList();
            var b = ProtoMessage.Parse(actual).Fields.OrderBy(f => f.FieldNumber).ToList();
            Assert.Equal(a.Count, b.Count);
            for (int i = 0; i < a.Count; i++)
            {
                Assert.Equal(a[i].FieldNumber, b[i].FieldNumber);
                Assert.Equal(a[i].VarIntValue, b[i].VarIntValue);
                Assert.Equal(a[i].BytesValue, b[i].BytesValue);
            }
        }

        [Theory]
        [InlineData(23393, 21070, 21071)]
        [InlineData(23754, 21226, 21227)]
        [InlineData(23741, 21072, 21073)]
        public void Base_spells_produce_scripts_before_damage_and_never_fake_buffs(int spell, int first, int second)
        {
            var owner = Person(); owner.Buffs.PonerEstado(3360);
            var enemy = Person(-1, 0, 314); enemy.IsMonster = true; enemy.TeamId = 1;
            var fight = Board(owner, enemy);
            var outcomes = EffectEngine.Resolver(fight, owner, spell, 1, enemy, EffectEngine.AlLanzar,
                fight.RoundNumber, celdaApuntada: enemy.CellId, orderedForgelance: true);
            var scripts = outcomes.Where(o => o.Marcador && o.Efecto.EffectId == 3792).ToList();
            Assert.Equal(new[] { first, second }, scripts.Select(o => o.Efecto.Value));
            Assert.All(scripts, o => { Assert.Null(o.Buff); Assert.Equal(314, o.CeldaDelLanzamiento); });
            Assert.DoesNotContain(owner.Buffs.Puestos, b => b.EffectId == 3792);
            var damage = outcomes.First(o => o.NestedDamage);
            Assert.True(outcomes.IndexOf(scripts.Last()) < outcomes.IndexOf(damage));
        }

        [Fact]
        public void Effondrement_announces_its_child_once_at_an_empty_aimed_cell()
        {
            var owner = Person(cell: 260); owner.Buffs.PonerEstado(3360);
            var fight = Board(owner);
            var outcomes = EffectEngine.Resolver(fight, owner, 23736, 1, null, EffectEngine.AlLanzar,
                fight.RoundNumber, celdaApuntada: 300, orderedForgelance: true);
            var cast = Assert.Single(outcomes, o => o.ForgelanceCast && o.HechizoOrigen == 30711);
            Assert.Equal(300, cast.CeldaDelLanzamiento);
            Assert.Null(cast.Sobre);
            Assert.DoesNotContain(outcomes, o => o.NestedDamage);
        }

        [Fact]
        public async Task Effondrement_child_on_the_wire_has_no_manual_flag_and_keeps_the_aim()
        {
            var owner = Person(cell: 260); owner.Buffs.PonerEstado(3360);
            var fight = Board(owner);
            await using var wire = await PortalTests.Wire.Open(Player);
            await FightHandler.AplicarEfectosAsync(wire.Session.Stream, fight, owner, 23736, 1, null,
                EffectEngine.AlLanzar, 300, orderedForgelance: true);
            var frames = await wire.Drain();
            var cast = Assert.Single(frames, f => f.Op == "jwe" && Value(f.Payload, 14) == 300);
            var detail = Sub(cast.Payload, 7);
            Assert.Equal(300, Value(detail, 6));
            Assert.Equal(30711, Value(Sub(detail, 7), 2));
            Assert.DoesNotContain(ProtoMessage.Parse(detail).Fields, f => f.FieldNumber is 2 or 8);
        }

        [Fact]
        public async Task Returning_from_guard_is_broadcast_to_both_fighters()
        {
            var owner = Person(); var ally = Person(Player + 1, 9, 314);
            var fight = Board(owner, ally);
            await using var a = await PortalTests.Wire.Open(owner.Id);
            await using var b = await PortalTests.Wire.Open(ally.Id);
            await FightHandler.AnnounceAppearanceAsync(a.Session.Stream, owner, 2061, fight);
            foreach (var wire in new[] { a, b })
            {
                var frame = Assert.Single(await wire.Drain());
                Assert.Equal(149, Value(frame.Payload, 14));
                AssertLook(Convert.FromHexString(ArmedLook), Sub(Sub(frame.Payload, 26), 3));
            }
        }

        [Fact]
        public async Task Disarmed_icon_switches_near_own_lance_including_diagonals_then_clears()
        {
            var owner = Person(); owner.Buffs.PonerEstado(3361);
            var fight = Board(owner);
            var (x, y) = MapGeometry.CellToPoint(owner.CellId);
            var lance = new Fighter { Id = -20, MonsterId = 7139, IsMonster = true,
                Invocador = owner.Id, CurrentHP = 100, MaxHP = 100, CellId = MapGeometry.PointToCell(x + 1, y + 1) };
            int cell = lance.CellId; fight.Invocar(lance, owner); lance.CellId = cell;
            await using var wire = await PortalTests.Wire.Open(Player);
            await FightHandler.SyncForgelanceProximityAsync(wire.Session.Stream, fight);
            Assert.True(owner.Buffs.TieneEstado(3589));
            var frames = await wire.Drain();
            var icon = Assert.Single(frames, f => f.Op == "jxm" && Value(Sub(f.Payload, 1), 3) == 950);
            var row = Sub(Sub(icon.Payload, 1), 1);
            Assert.Equal(3589, Value(row, 10)); Assert.Equal(2, Value(row, 15));
            owner.CellId = 100;
            await FightHandler.SyncForgelanceProximityAsync(wire.Session.Stream, fight);
            Assert.False(owner.Buffs.TieneEstado(3589));
            Assert.True(owner.Buffs.TieneEstado(3361));
            Assert.Contains(await wire.Drain(), f => f.Op == "jya");
        }

        [Fact]
        public void Another_players_lance_and_other_classes_do_not_activate_the_forgelance_path()
        {
            var owner = Person(); var other = Person(Player + 1);
            var fight = Board(owner, other);
            var lance = new Fighter { Id = -20, MonsterId = 7139, IsMonster = true, CurrentHP = 100 };
            fight.Invocar(lance, other);
            Assert.Null(ForgelanceCombat.OwnLance(fight, owner));
            var cra = Person(Player + 2, 9);
            var effect = new SpellEffect { EffectId = 3792, Value = 21070, TargetMask = "C", Triggers = "I" };
            var outcomes = EffectEngine.ResolveEffects(fight, cra, 23393, 1, cra, EffectEngine.AlLanzar,
                fight.RoundNumber, new[] { effect });
            Assert.DoesNotContain(outcomes, o => o.Marcador || o.ForgelanceCast || o.NestedDamage);
        }

        [Theory]
        [InlineData(23393, 96, 13, 15)]
        [InlineData(23754, 99, 17, 19)]
        [InlineData(23741, 98, 14, 16)]
        public async Task Base_cast_sends_both_scripts_before_one_damage_line(int spell, int damageAction, int min, int max)
        {
            var owner = Person(); owner.Buffs.PonerEstado(3360);
            var enemy = Person(-1, 0, 314); enemy.IsMonster = true; enemy.TeamId = 1;
            var fight = Board(owner, enemy);
            await using var wire = await PortalTests.Wire.Open(Player);
            await FightHandler.AplicarEfectosAsync(wire.Session.Stream, fight, owner, spell, 1, enemy,
                EffectEngine.AlLanzar, 314, orderedForgelance: true);
            var frames = await wire.Drain();
            var actions = frames.Where(f => f.Op == "jwe").Select(f => Value(f.Payload, 14)).ToList();
            Assert.Equal(2, actions.Count(a => a == 3792));
            Assert.Equal(1, actions.Count(a => a == damageAction));
            Assert.True(actions.LastIndexOf(3792) < actions.IndexOf(damageAction));
            Assert.InRange(1000 - enemy.CurrentHP, min, max);
        }

        [Theory]
        [InlineData(23393, true)]
        [InlineData(23393, false)]
        [InlineData(23754, true)]
        [InlineData(23754, false)]
        public async Task A_lethal_hit_drops_the_remaining_target_effects_but_survivors_still_move(int spell, bool lethal)
        {
            var owner = Person(); owner.Buffs.PonerEstado(3360);
            var enemy = Person(-1, 0, 327); enemy.IsMonster = true; enemy.TeamId = 1;
            enemy.CurrentHP = lethal ? 1 : 1000;
            var fight = Board(owner, enemy);
            await using var wire = await PortalTests.Wire.Open(Player);

            // Cell 314 is free between caster 300 and target 327, so Trident can pull.
            await FightHandler.AplicarEfectosAsync(wire.Session.Stream, fight, owner, spell, 1, enemy,
                EffectEngine.AlLanzar, 327, orderedForgelance: true);
            var frames = await wire.Drain();
            var moves = frames.Where(f => f.Op == "jwe" && Value(f.Payload, 14) == 5
                && Value(Sub(f.Payload, 38), 2) == enemy.Id).ToList();

            Assert.Equal(!lethal, enemy.IsAlive);
            if (!lethal)
            {
                Assert.Single(moves);
                Assert.DoesNotContain(frames, f => f.Op == "jwe" && Value(f.Payload, 14) == 103);
                return;
            }

            Assert.Empty(moves);
            int death = frames.FindIndex(f => f.Op == "jwe" && Value(f.Payload, 14) == 103
                && Value(Sub(f.Payload, 4), 1) == enemy.Id);
            Assert.True(death >= 0);
            Assert.DoesNotContain(frames.Skip(death + 1), f => f.Op == "jxm"
                && Value(Sub(Sub(f.Payload, 1), 1), 2) == enemy.Id);
            if (spell == 23754)
                Assert.False(enemy.Buffs.TieneEstado(6032)); // Estoc's state follows its damage.
        }

        [Fact]
        public async Task An_area_kill_does_not_drop_the_surviving_targets_effects()
        {
            var owner = Person(); owner.Buffs.PonerEstado(3360);
            var dead = Person(-1, 0, 328); dead.IsMonster = true; dead.TeamId = 1; dead.CurrentHP = 1;
            var alive = Person(-2, 0, 329); alive.IsMonster = true; alive.TeamId = 1;
            var fight = Board(owner, dead, alive);
            var effects = new[]
            {
                new SpellEffect { EffectId = 96, DiceNum = 10, DiceSide = 10, TargetMask = "A", Forma = 'C', Tamano = 2 },
                new SpellEffect { EffectId = 950, Value = 6032, Duration = 1, TargetMask = "A", Forma = 'C', Tamano = 2 },
                new SpellEffect { EffectId = 5, DiceNum = 1, Value = 1, TargetMask = "A", Forma = 'C', Tamano = 2 },
            };
            await using var wire = await PortalTests.Wire.Open(Player);
            await FightHandler.AplicarEfectosAsync(wire.Session.Stream, fight, owner, 23754, 1, dead,
                EffectEngine.AlLanzar, 328, tirada: effects, orderedForgelance: true);
            var frames = await wire.Drain();

            Assert.False(dead.IsAlive);
            Assert.True(alive.IsAlive);
            Assert.False(dead.Buffs.TieneEstado(6032));
            Assert.True(alive.Buffs.TieneEstado(6032));
            var moved = frames.Where(f => f.Op == "jwe" && Value(f.Payload, 14) == 5)
                .Select(f => Value(Sub(f.Payload, 38), 2)).ToList();
            Assert.DoesNotContain(dead.Id, moved);
            Assert.Contains(alive.Id, moved);
        }

        [Fact]
        public async Task A_child_cast_keeps_its_cell_without_naming_a_target_killed_by_the_parent()
        {
            var owner = Person(); owner.Buffs.PonerEstado(3360);
            var enemy = Person(-1, 0, 328); enemy.IsMonster = true; enemy.TeamId = 1; enemy.CurrentHP = 1;
            var fight = Board(owner, enemy);
            var effects = new[]
            {
                new SpellEffect { EffectId = 96, DiceNum = 10, DiceSide = 10, TargetMask = "A" },
                new SpellEffect { EffectId = 2960, DiceNum = 30711, DiceSide = 1, TargetMask = "A" },
            };
            await using var wire = await PortalTests.Wire.Open(Player);
            await FightHandler.AplicarEfectosAsync(wire.Session.Stream, fight, owner, 23736, 1, enemy,
                EffectEngine.AlLanzar, 328, tirada: effects, orderedForgelance: true);
            var frames = await wire.Drain();

            Assert.False(enemy.IsAlive);
            var cast = Assert.Single(frames, f => f.Op == "jwe" && Value(f.Payload, 14) == 300);
            var detail = Sub(cast.Payload, 7);
            Assert.Equal(30711, Value(Sub(detail, 7), 2));
            Assert.Equal(328, Value(detail, 6));
            Assert.DoesNotContain(ProtoMessage.Parse(detail).Fields, f => f.FieldNumber == 2);
        }

        [Fact]
        public async Task Throwing_and_retrieving_own_lance_restore_the_armed_state_and_power()
        {
            var owner = Person(); owner.Buffs.PonerActitud(24387);
            var enemy = Person(-1, 0, 400); enemy.IsMonster = true; enemy.TeamId = 1;
            var fight = Board(owner, enemy);
            await using var wire = await PortalTests.Wire.Open(Player);
            await FightHandler.AplicarEfectosAsync(wire.Session.Stream, fight, owner, 24387, 1, owner,
                EffectEngine.AlLanzar, owner.CellId);
            await FightHandler.AplicarEfectosAsync(wire.Session.Stream, fight, owner, 24390, 3, owner,
                EffectEngine.AlLanzar, owner.CellId);
            await FightHandler.AplicarEfectosAsync(wire.Session.Stream, fight, owner, 23719, 1, null,
                EffectEngine.AlLanzar, 314, orderedForgelance: true);
            var lance = ForgelanceCombat.OwnLance(fight, owner);
            Assert.NotNull(lance);
            Assert.True(owner.Buffs.TieneEstado(3361));
            Assert.False(owner.Buffs.TieneEstado(3360));
            Assert.True(owner.Buffs.TieneEstado(3589));
            await wire.Drain();
            await FightHandler.AplicarEfectosAsync(wire.Session.Stream, fight, owner, 23393, 1, enemy,
                EffectEngine.AlLanzar, enemy.CellId, orderedForgelance: true);
            Assert.False(lance.IsAlive);
            Assert.True(owner.Buffs.TieneEstado(3360));
            Assert.False(owner.Buffs.TieneEstado(3361));
            Assert.False(owner.Buffs.TieneEstado(3589));
            Assert.Contains(owner.Buffs.Puestos, b => b.HechizoOrigen == 30736 && b.Cuanto == 30);
        }
    }
}
