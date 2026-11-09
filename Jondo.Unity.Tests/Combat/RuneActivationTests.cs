using System.Linq;
using System.Threading.Tasks;
using Jondo.Unity.Protocol;
using Jondo.Unity.Server.Handlers;
using Jondo.Unity.Server.Network;
using Jondo.Unity.World.Fights;
using Jondo.Unity.World.Maps;
using Xunit;
using Xunit.Abstractions;

namespace Jondo.Unity.Tests.Combat
{
    /// <summary>
    /// The Huppermage's runes set off by the spells that read them: an earth spell leaves an earth
    /// rune under the foe it hits (Onda Sísmica), and Runificación, Manifestación, Prisma Rúnico
    /// and Sobrecarga Rúnica each activate it, as their sheets say.
    /// </summary>
    public class RuneActivationTests
    {
        private const long Hupper = 54000000101, Foe = 54000000102;
        private const int OndaSismica = 13668, Earth = 292;
        private readonly ITestOutputHelper _out;
        public RuneActivationTests(ITestOutputHelper output) => _out = output;

        private static async Task CastAsHim(FightInstance fight, PortalTests.Wire mine, int spell, int cell)
        {
            var active = (System.Collections.Concurrent.ConcurrentDictionary<long, FightInstance>)typeof(FightHandler)
                .GetField("_activeFights", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)!.GetValue(null)!;
            active[fight.FightId] = fight;
            try
            {
                using (SessionContext.Push(mine.Session))
                    await FightHandler.CastAsync(mine.Session.Stream, ConnectionProtocol.Push(Op.Jwh, Pb.New().Var(1, cell).Var(4, spell).Build()));
            }
            finally { active.TryRemove(fight.FightId, out _); }
        }

        private async Task<(FightInstance Fight, Fighter Me, Fighter Foe, PortalTests.Wire Wire)> RuneUnderTheFoe()
        {
            var fight = new FightInstance(Hupper, 1);
            var me = new Fighter { Id = Hupper, Breed = 17, MaxHP = 4000, CurrentHP = 4000, Level = 200, MaxAP = 30, CurrentAP = 30, MaxMP = 6, CurrentMP = 6 };
            var foe = new Fighter { Id = Foe, Breed = 8, MaxHP = 40000, CurrentHP = 40000, Level = 200, MaxAP = 12, CurrentAP = 12, MaxMP = 6, CurrentMP = 6 };
            fight.AddPlayer(me); fight.AddOpponent(foe);
            var (x, y) = MapGeometry.CellToPoint(300);
            me.CellId = 300;
            foe.CellId = MapGeometry.PointToCell(x + 3, y);
            fight.StartFight();
            var wire = await PortalTests.Wire.Open(Hupper);
            await CastAsHim(fight, wire, OndaSismica, foe.CellId);
            return (fight, me, foe, wire);
        }

        [Theory]
        [InlineData(13670)] // Runificación
        [InlineData(13710)] // Manifestación
        [InlineData(13724)] // Sobrecarga Rúnica
        public async Task Setting_off_the_rune_under_a_foe_hurts_him(int spell)
        {
            var (fight, me, foe, wire) = await RuneUnderTheFoe();
            await using var _ = wire;
            var runes = fight.Glifos.Where(g => g.Dueno == me.Id && g.Cubre(foe.CellId)).ToList();
            Assert.NotEmpty(runes);
            int life = foe.CurrentHP, ap = me.CurrentAP;
            await CastAsHim(fight, wire, spell, foe.CellId);
            _out.WriteLine($"spell {spell}: AP {ap} -> {me.CurrentAP}, foe {life} -> {foe.CurrentHP}");
            Assert.True(me.CurrentAP < ap, "the cast was refused");
            Assert.True(foe.CurrentHP < life, $"spell {spell} on his rune did not hurt him");
        }

        [Fact]
        public async Task Prisma_Runico_on_an_earth_rune_shortens_and_marks_the_foes_round_it()
        {
            var (fight, me, foe, wire) = await RuneUnderTheFoe();
            await using var _ = wire;
            int ap = me.CurrentAP;
            foe.Buffs.QuitarEstado(Earth);
            await CastAsHim(fight, wire, 13719, foe.CellId);
            _out.WriteLine($"Prisma Rúnico: AP {ap} -> {me.CurrentAP}, foe earth state {foe.Buffs.TieneEstado(Earth)}");
            Assert.True(me.CurrentAP < ap, "the cast was refused");
            Assert.True(foe.Buffs.TieneEstado(Earth), "the earth rune's prism left no earth state on the foe beside it");
        }
        [Fact]
        public async Task The_earth_rune_s_own_spell_steals_with_the_activation_states()
        {
            var fight = new FightInstance(Hupper, 1);
            var me = new Fighter { Id = Hupper, Breed = 17, MaxHP = 4000, CurrentHP = 4000, Level = 200, MaxAP = 30, CurrentAP = 30, MaxMP = 6, CurrentMP = 6 };
            var foe = new Fighter { Id = Foe, Breed = 8, MaxHP = 40000, CurrentHP = 40000, Level = 200, MaxAP = 12, CurrentAP = 12, MaxMP = 6, CurrentMP = 6 };
            fight.AddPlayer(me); fight.AddOpponent(foe);
            var (x, y) = MapGeometry.CellToPoint(300);
            me.CellId = 300;
            foe.CellId = MapGeometry.PointToCell(x + 3, y);
            me.Buffs.PonerEstado(296); me.Buffs.PonerEstado(6971);
            await FightHandler.CastEffectsAsync(null, fight, me, 13690, 1, foe, foe.CellId, false,
                                                Jondo.Unity.Server.Managers.EffectEngine.EfectosSorteados(13690, 1, false));
            _out.WriteLine($"13690 straight: foe {foe.CurrentHP}");
            Assert.True(foe.CurrentHP < 40000);
        }
        [Fact]
        public async Task The_rune_fired_alone_steals_with_the_activation_states()
        {
            var (fight, me, foe, wire) = await RuneUnderTheFoe();
            await using var _ = wire;
            var rune = fight.Glifos.First(g => g.Dueno == me.Id && g.Cubre(foe.CellId));
            me.Buffs.PonerEstado(296); me.Buffs.PonerEstado(6971);
            int life = foe.CurrentHP;
            var fire = typeof(FightHandler).GetMethod("FireOneGlyphAsync", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)!;
            await (Task)fire.Invoke(null, new object?[] { wire.Session.Stream, fight, rune, foe, false, -1, false })!;
            _out.WriteLine($"rune fired alone: foe {life} -> {foe.CurrentHP}");
            Assert.True(foe.CurrentHP < life);
        }
        /// <summary>«Ocasiona daños al enemigo objetivo por cada runa activada»: one he left behind too.</summary>
        [Fact]
        public async Task Sobrecarga_Runica_hurts_its_target_once_per_rune_empty_ones_too()
        {
            var (fight, me, foe, wire) = await RuneUnderTheFoe();
            await using var _ = wire;
            var (x, y) = MapGeometry.CellToPoint(foe.CellId);
            await FightHandler.WalkPathAsync(fight, foe, new[] { foe.CellId, MapGeometry.PointToCell(x, y + 1) });
            me.CurrentAP = 30;
            await CastAsHim(fight, wire, OndaSismica, foe.CellId);
            Assert.Equal(2, fight.Glifos.Count(g => g.Dueno == me.Id && g.Tipo == 2022));
            int life = foe.CurrentHP;
            await CastAsHim(fight, wire, 13724, foe.CellId);
            _out.WriteLine($"Sobrecarga with two runes: foe {life} -> {foe.CurrentHP}");
            Assert.True(life - foe.CurrentHP > 10, $"two runes, a blow of about 10 each: lost {life - foe.CurrentHP}");
        }
        /// <summary>Contribución on himself with a foe in the earth state: «Tierra: aumenta los PM».</summary>
        [Fact]
        public async Task Contribucion_gives_movement_for_a_foe_in_the_earth_state()
        {
            var (fight, me, foe, wire) = await RuneUnderTheFoe();
            await using var _ = wire;
            Assert.True(foe.Buffs.TieneEstado(Earth));
            int ap = me.CurrentAP;
            await CastAsHim(fight, wire, 13701, me.CellId);
            var mp = me.Buffs.Puestos.Where(r => r.EffectId == 128).ToList();
            _out.WriteLine($"Contribución: AP {ap} -> {me.CurrentAP}, MP rows {mp.Count}, foe earth {foe.Buffs.TieneEstado(Earth)}");
            Assert.True(me.CurrentAP < ap, "the cast was refused");
            Assert.NotEmpty(mp);
        }
    }
}
