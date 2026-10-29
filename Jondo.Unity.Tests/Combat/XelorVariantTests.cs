using System.Linq;
using System.Threading.Tasks;
using Jondo.Unity.Server.Handlers;
using Jondo.Unity.Server.Managers;
using Jondo.Unity.World.Fights;
using Xunit;

namespace Jondo.Unity.Tests.Combat
{
    /// <summary>
    /// The Xelor's variant spells, as they were tried in the client: what each one does on the
    /// board, read off its own rows.
    /// </summary>
    [Collection("MapManager")]
    public class XelorVariantTests
    {
        private const long Xelor = 9_970_201;
        private const int Remanencia = 13255;

        private static Fighter Character(int cell) => new()
        {
            Id = Xelor, Name = "X", CellId = cell, Level = 200, MaxHP = 3000, CurrentHP = 3000,
            MaxAP = 12, CurrentAP = 12, MaxMP = 6, CurrentMP = 6, Breed = 5,
        };

        private static Fighter Monster(long id, int cell) => new()
        {
            Id = id, Name = "M" + id, IsMonster = true, CellId = cell, Level = 50, MaxHP = 900, CurrentHP = 900,
            MaxAP = 6, CurrentAP = 6, MaxMP = 3, CurrentMP = 3, MonsterId = 494,
        };

        private static FightInstance Board(Fighter xelor, params Fighter[] monsters)
        {
            // A board of its own, with no ground data: map 0 is another test's.
            var fight = new FightInstance(4105, 990_004_105);
            int cell = xelor.CellId;
            fight.AddPlayer(xelor);
            xelor.CellId = cell;
            foreach (var monster in monsters)
            {
                int where = monster.CellId;
                fight.AddMonster(monster);
                monster.CellId = where;
            }
            return fight;
        }

        /// <summary>
        /// Remanencia on oneself: "teletransporta al objetivo a su posición anterior por cada PM que
        /// utilice", at the end of the turn for an ally. Every step hooks 30846 at grade 2 once more,
        /// so five steps are five returns, and the Xelor ends where he started walking. Refreshed
        /// into one hook, he went back a single cell.
        /// </summary>
        [Fact]
        public async Task Remanencia_returns_one_cell_per_MP_walked()
        {
            var xelor = Character(468);
            var fight = Board(xelor, Monster(-1, 100));

            await FightHandler.AplicarEfectosAsync(null, fight, xelor, Remanencia, 1, xelor, EffectEngine.AlLanzar, 468);
            Assert.Contains(xelor.Buffs.ActiveSpells, a => a.Hechizo == Remanencia);

            int[] walk = { 468, 454, 440, 426, 412, 398 };
            xelor.Andar(walk);
            for (int step = 1; step < walk.Length; step++)
            {
                await FightHandler.EngancheAsync(null, fight, xelor, EffectEngine.AlAndar);
                await FightHandler.EngancheAsync(null, fight, xelor, EffectEngine.AlUsarUnPM);
            }
            var returns = Assert.Single(xelor.Buffs.ActiveSpells, a => a.Hechizo == 30846);
            Assert.Equal(5, returns.Veces);

            await FightHandler.EngancheAsync(null, fight, xelor, EffectEngine.AlAcabarElTurno);

            Assert.Equal(468, xelor.CellId);
        }

        /// <summary>
        /// Regulador takes a round off the target's buffs, and says so: a jwe 1075 the client
        /// shortens its panel by. Untold, the two-round poison lasted one and still read two.
        /// </summary>
        [Fact]
        public void Regulador_shortens_the_buffs_and_tells_the_client()
        {
            var xelor = Character(300);
            var poutch = Monster(-1, 301);
            var fight = Board(xelor, poutch);
            int n = 0;
            poutch.Buffs.Poner(new Buff { EffectId = 950, Estado = 2328, HechizoOrigen = 30842, NivelOrigen = 6, CaducaEnRonda = 3 }, () => ++n);
            poutch.Buffs.PonerEstado(2328);
            poutch.Buffs.Poner(new Buff { EffectId = 950, Estado = 6135, HechizoOrigen = 13255, NivelOrigen = 1, CaducaEnRonda = 2 }, () => ++n);
            poutch.Buffs.PonerEstado(6135);

            var outcomes = EffectEngine.Resolver(fight, xelor, 13248, 1, poutch, EffectEngine.AlLanzar, 1, celdaApuntada: poutch.CellId);

            var shortened = Assert.Single(outcomes, o => o.RondasAcortadas > 0);
            Assert.Equal(1, shortened.RondasAcortadas);
            Assert.Equal(2, Assert.Single(poutch.Buffs.Puestos).CaducaEnRonda);
            // The one that fell takes its state with it.
            Assert.DoesNotContain(6135, poutch.Buffs.Estados);
            Assert.Contains(2328, poutch.Buffs.Estados);
        }

        /// <summary>
        /// Reloj de Arena's delayed blow: two rounds on, 30843 at grade 3 is cast on the target and
        /// its "99 in a circle of 2" burns him and whoever stands by (the capture's frames
        /// 1361-1364). Handed whole to the engine, its blow was taken for the root's and dropped.
        /// </summary>
        [Fact]
        public async Task Reloj_de_Arena_burns_around_its_target_when_the_delay_is_up()
        {
            var xelor = Character(250);
            var target = Monster(-1, 300);
            var beside = Monster(-2, 301);
            var fight = Board(xelor, target, beside);
            target.Buffs.Poner(new Buff
            {
                EffectId = 1160, Dado = 30843, Cara = 3, HechizoOrigen = 30843, NivelOrigen = 1,
                Quien = xelor.Id, Pendiente = true, Disparador = EffectEngine.Esperando,
                EmpiezaEnRonda = fight.RoundNumber, CaducaEnRonda = fight.RoundNumber, Apila = true,
            }, fight.SiguienteEmbrujo);
            int targetLife = target.CurrentHP, besideLife = beside.CurrentHP;

            await FightHandler.ApplyDuePendingAsync(null, fight);

            Assert.True(target.CurrentHP < targetLife);
            Assert.True(beside.CurrentHP < besideLife);
        }

        /// <summary>
        /// Desecamiento bounces to the nearest entity in a circle of 2, and every bounce burns its
        /// enemy: 30962 at grade 1 marks him (6140) and then hits "A,e6140", read off the state he
        /// had when the bounce reached him -- 137 on -3 and 137 on -1 in the capture, frames
        /// 143-147. Read live, the mark it had just put made the blow skip him.
        /// </summary>
        [Fact]
        public void Desecamiento_burns_every_enemy_it_bounces_to()
        {
            var xelor = Character(250);
            var target = Monster(-1, 300);
            var next = Monster(-2, 301);
            var fight = Board(xelor, target, next);

            var outcomes = EffectEngine.Resolver(fight, xelor, 13284, 1, target, EffectEngine.AlLanzar, 1, celdaApuntada: target.CellId);

            var bounce = Assert.Single(outcomes, o => o.NestedDamage);
            Assert.Equal(next, bounce.Sobre);
            Assert.Equal(98, bounce.Efecto.EffectId);
        }

        /// <summary>
        /// Inestabilidad goes off when the Xelor makes or takes a telefrag (13325, "1026 a,A,*e3620
        /// on the whole board"): once, on him alone -- the enemy on the glyph's centre stays put
        /// (capture, frames 101-103) -- and its 3620 keeps the next one from setting it off again.
        /// Run for each fighter the 1026 named, it went off without end.
        /// </summary>
        [Fact]
        public async Task Inestabilidad_goes_off_once_on_the_xelor_alone()
        {
            var xelor = Character(300);
            var enemy = Monster(-1, 301);
            var fight = Board(xelor, enemy);
            xelor.Andar(new[] { 270, 285, 300 });

            await FightHandler.AplicarEfectosAsync(null, fight, xelor, 13301, 1, xelor, EffectEngine.AlLanzar, 300);
            Assert.Equal(2, fight.Glifos.Count);
            var aura = Assert.Single(fight.Glifos, g => g.Tipo == 1091);
            Assert.Equal(Disparo.AlPonerse, aura.Cuando);

            await FightHandler.AplicarEfectosAsync(null, fight, xelor, 13325, 1, xelor, EffectEngine.AlLanzar, xelor.CellId);
            Assert.Equal(285, xelor.CellId);
            Assert.Equal(301, enemy.CellId);
            Assert.True(xelor.Buffs.TieneEstado(3620));

            // The next telefrag finds the 3620: the glyph stays quiet, and the state goes.
            await FightHandler.AplicarEfectosAsync(null, fight, xelor, 13325, 1, xelor, EffectEngine.AlLanzar, xelor.CellId);
            Assert.Equal(285, xelor.CellId);
            Assert.False(xelor.Buffs.TieneEstado(3620));
        }

        /// <summary>
        /// Reloj de Bolsillo's glyph is laid on the enemy's cell a turn later, and its turn-end blow
        /// burns that circle (frames 2215-2327 of its capture), wherever the Xelor stands: the
        /// aura's "2794 C" is hooked on him as it is laid, with the glyph's cell. Fired only when he
        /// started his turn on the glyph, and from his own cell, it hit around him or not at all.
        /// </summary>
        [Fact]
        public async Task Reloj_de_Bolsillo_burns_the_glyph_s_circle_at_the_end_of_the_xelor_s_turn()
        {
            var xelor = Character(100);
            var target = Monster(-1, 300);
            var beside = Monster(-2, 301);
            var byTheXelor = Monster(-3, 101);
            var fight = Board(xelor, target, beside, byTheXelor);

            await FightHandler.AplicarEfectosAsync(null, fight, xelor, 30847, 2, target, EffectEngine.AlLanzar, 300);
            var glyph = Assert.Single(fight.Glifos);
            Assert.Equal(Disparo.AlPonerse, glyph.Cuando);
            var hook = Assert.Single(xelor.Buffs.ActiveSpells, a => a.Hechizo == 30847);
            Assert.Equal(300, hook.Celda);

            int targetLife = target.CurrentHP, besideLife = beside.CurrentHP, farLife = byTheXelor.CurrentHP;
            await FightHandler.EngancheAsync(null, fight, xelor, EffectEngine.AlAcabarElTurno);

            Assert.True(target.CurrentHP < targetLife);
            Assert.True(beside.CurrentHP < besideLife);
            Assert.Equal(farLife, byTheXelor.CurrentHP);
            Assert.Empty(fight.Glifos);
        }

        /// <summary>
        /// Premonición's glyph only marks the ground ("a,A,F50000" names nobody): it does not go off
        /// at the target's turn start. The swap comes with the delayed 13314 at the Xelor's next
        /// turn, and the move it makes sets off the target's hook -- the telefrag (capture, frames
        /// 192-200). Fired by the glyph too, the two swapped twice and ended where they began.
        /// </summary>
        [Fact]
        public async Task Premonicion_swaps_once_a_turn_later_and_makes_a_telefrag()
        {
            var xelor = Character(250);
            var target = Monster(-1, 300);
            var fight = Board(xelor, target);

            await FightHandler.AplicarEfectosAsync(null, fight, xelor, 13293, 1, target, EffectEngine.AlLanzar, 300);
            var glyph = Assert.Single(fight.Glifos);
            Assert.Equal(Disparo.AlPonerse, glyph.Cuando);
            Assert.False(glyph.SeDisparaAlEmpezarElTurno);
            Assert.Equal(250, xelor.CellId);

            for (int turn = 0; turn < 4 && fight.RoundNumber < 2; turn++) fight.NextTurn();
            await FightHandler.ApplyDuePendingAsync(null, fight);

            Assert.Equal(300, xelor.CellId);
            Assert.Equal(250, target.CellId);
            Assert.True(xelor.Buffs.TieneEstado(3622));
            Assert.True(target.Buffs.TieneEstado(251));
        }

        /// <summary>The frame, as the Eniripsa captures send it: f3 who, f14 1075, f19 { f1 -1, f2 whose }.</summary>
        [Fact]
        public void The_effects_duration_frame_is_the_capture_s()
        {
            var frame = Jondo.Unity.Server.Network.FightProtocol.BuildEffectsDuration(1234, -1, -1);
            Assert.Equal("18d20970b3089a011608ffffffffffffffffff0110ffffffffffffffffff01", System.Convert.ToHexString(frame).ToLowerInvariant());
        }

        /// <summary>
        /// The same hook put again in another round starts over: the steps of last turn are not
        /// walked back twice.
        /// </summary>
        [Fact]
        public void A_hook_from_another_round_starts_over()
        {
            var buffs = new Fighter { Id = 1, MaxHP = 10, CurrentHP = 10 }.Buffs;
            buffs.Enganchar(30846, 2, 5, 1, 3, apilarHasta: -1);
            buffs.Enganchar(30846, 2, 5, 1, 3, apilarHasta: -1);
            Assert.Equal(2, buffs.ActiveSpells.Single().Veces);

            buffs.Enganchar(30846, 2, 6, 1, 4, apilarHasta: -1);
            Assert.Equal(1, buffs.ActiveSpells.Single().Veces);

            // And a refresh off a cast, with no trigger behind it, piles nothing.
            buffs.Enganchar(30846, 2, 6, 1, 4);
            Assert.Equal(1, buffs.ActiveSpells.Single().Veces);
        }
    }
}
