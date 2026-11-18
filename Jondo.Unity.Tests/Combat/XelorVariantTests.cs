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

        /// <summary>Turns go by until <paramref name="done"/>; fails rather than loop on a board that never gets there.</summary>
        private static void TurnsUntil(FightInstance fight, System.Func<bool> done)
        {
            for (int turn = 0; turn < 50 && !done(); turn++) fight.NextTurn();
            Assert.True(done(), "the fight never got to the turn asked for");
        }

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

        [Theory]
        [InlineData(true, 5)]
        [InlineData(false, 3)]
        public async Task Perturbation_refunds_AP_and_marks_both_fighters_only_on_a_swap(
            bool occupiedDestination, int expectedAp)
        {
            const int Perturbation = 13288, EnemyTelefrag = 251;
            var xelor = Character(330);
            xelor.Level = 3;
            xelor.CurrentAP = xelor.MaxAP = 6;
            var target = Monster(-5, 345);
            var other = Monster(-4, occupiedDestination ? 316 : 302);
            var fight = Board(xelor, target, other);
            var (grade, _, cost) = SpellEffects.GradoDe(Perturbation, xelor.Level);
            var effects = EffectEngine.EfectosSorteados(Perturbation, grade, false);

            Assert.True(await FightHandler.TryPayCastCostAsync(fight, xelor, effects, cost, _ => Task.CompletedTask));
            await FightHandler.CastEffectsAsync(null, fight, xelor, Perturbation, grade, target, 345, false, effects);

            Assert.Equal(316, target.CellId);
            Assert.Equal(occupiedDestination ? 345 : 302, other.CellId);
            Assert.Equal(occupiedDestination, target.Buffs.TieneEstado(EnemyTelefrag));
            Assert.Equal(occupiedDestination, other.Buffs.TieneEstado(EnemyTelefrag));
            Assert.Equal(expectedAp, xelor.CurrentAP);
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
            poutch.Buffs.Poner(new Buff { EffectId = 950, Estado = 2328, HechizoOrigen = 30842, NivelOrigen = 6, CaducaEnRonda = 3,
                                          Dispellable = Buffs.DispellableByADispel }, () => ++n);
            poutch.Buffs.PonerEstado(2328);
            poutch.Buffs.Poner(new Buff { EffectId = 950, Estado = 6135, HechizoOrigen = 13255, NivelOrigen = 1, CaducaEnRonda = 2,
                                          Dispellable = Buffs.DispellableByADispel }, () => ++n);
            poutch.Buffs.PonerEstado(6135);
            poutch.Buffs.Enganchar(13255, 1, 2, xelor.Id, 1);

            var outcomes = EffectEngine.Resolver(fight, xelor, 13248, 1, poutch, EffectEngine.AlLanzar, 1, celdaApuntada: poutch.CellId);

            var shortened = Assert.Single(outcomes, o => o.RondasAcortadas > 0);
            Assert.Equal(1, shortened.RondasAcortadas);
            Assert.Equal(2, Assert.Single(poutch.Buffs.Puestos).CaducaEnRonda);
            // The one that fell takes its state with it, and its spell's hook.
            Assert.DoesNotContain(6135, poutch.Buffs.Estados);
            Assert.Contains(2328, poutch.Buffs.Estados);
            Assert.DoesNotContain(poutch.Buffs.ActiveSpells, a => a.Hechizo == 13255);
        }

        /// <summary>
        /// Only the rows a dispel would take are shortened: Regulador cast on the Xelor in telefrag
        /// keeps his "+1 cast per turn" (a 3) and his "+2 AP once a turn" lock (a 2). Shortened too,
        /// both fell at once and he gained AP again and again in the same turn.
        /// </summary>
        [Fact]
        public void Regulador_leaves_the_rows_a_dispel_cannot_take()
        {
            var xelor = Character(300);
            var fight = Board(xelor, Monster(-1, 301));
            var once = xelor.Buffs.Poner(new Buff { EffectId = 950, Estado = 360, HechizoOrigen = 13249, NivelOrigen = 1,
                                                    CaducaEnRonda = 2, Dispellable = Buffs.DispellableByDeath }, fight.SiguienteEmbrujo);
            xelor.Buffs.PonerEstado(360);
            var moreCasts = xelor.Buffs.Poner(new Buff { EffectId = 290, HechizoOrigen = 30860, NivelOrigen = 1, CaducaEnRonda = 2,
                                                         Dispellable = Buffs.DispellableByStrongDispelOnly }, fight.SiguienteEmbrujo);

            Assert.Empty(xelor.Buffs.Acortar(1, 1));

            Assert.Equal(2, once.CaducaEnRonda);
            Assert.Equal(2, moreCasts.CaducaEnRonda);
            Assert.True(xelor.Buffs.TieneEstado(360));
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

        /// <summary>
        /// Premonición aimed at an empty cell takes the Xelor there a turn later: the delayed
        /// 13314 keeps the aimed cell. Kept on the Xelor himself, it sent him to his own cell.
        /// </summary>
        [Fact]
        public async Task Premonicion_takes_the_xelor_to_an_empty_cell_a_turn_later()
        {
            var xelor = Character(250);
            var fight = Board(xelor, Monster(-1, 100));

            await FightHandler.AplicarEfectosAsync(null, fight, xelor, 13293, 1, null, EffectEngine.AlLanzar, 300);
            var waiting = Assert.Single(xelor.Buffs.Puestos, b => b.Pendiente);
            Assert.Equal(300, waiting.PendingCastCell);
            Assert.Equal(250, xelor.CellId);

            for (int turn = 0; turn < 4 && fight.RoundNumber < 2; turn++) fight.NextTurn();
            Assert.Equal(xelor, fight.CurrentFighter);
            await FightHandler.ApplyDuePendingAsync(null, fight);

            Assert.Equal(300, xelor.CellId);
        }

        /// <summary>
        /// The cell, not the fighter: the enemy aimed at walks off before the Xelor's next turn,
        /// and the Xelor lands on the glyph's cell, not on the enemy's new one.
        /// </summary>
        [Fact]
        public async Task Premonicion_keeps_the_cell_when_its_target_walks_off()
        {
            var xelor = Character(250);
            var target = Monster(-1, 300);
            var fight = Board(xelor, target);

            await FightHandler.AplicarEfectosAsync(null, fight, xelor, 13293, 1, target, EffectEngine.AlLanzar, 300);
            target.MoverA(400);

            for (int turn = 0; turn < 4 && fight.RoundNumber < 2; turn++) fight.NextTurn();
            await FightHandler.ApplyDuePendingAsync(null, fight);

            Assert.Equal(300, xelor.CellId);
            Assert.Equal(400, target.CellId);
        }

        /// <summary>
        /// A delay is counted in its caster's turns: the enemy who opens the round plays before the
        /// Xelor, and the Xelor's delayed rows wait for his own turn start. Gone off at the turn
        /// that opened the round, Premonición moved him in the enemy's turn.
        /// </summary>
        [Fact]
        public async Task A_delayed_row_waits_for_its_casters_turn()
        {
            var xelor = Character(250);
            var enemy = Monster(-1, 100);
            enemy.Initiative = 5000;
            var fight = Board(xelor, enemy);

            await FightHandler.AplicarEfectosAsync(null, fight, xelor, 13293, 1, null, EffectEngine.AlLanzar, 300);

            for (int turn = 0; turn < 4 && fight.RoundNumber < 2; turn++) fight.NextTurn();
            Assert.Equal(enemy, fight.CurrentFighter);
            await FightHandler.ApplyDuePendingAsync(null, fight);
            Assert.Equal(250, xelor.CellId);
            Assert.Single(xelor.Buffs.Puestos, b => b.Pendiente);

            fight.NextTurn();
            Assert.Equal(xelor, fight.CurrentFighter);
            await FightHandler.ApplyDuePendingAsync(null, fight);
            Assert.Equal(300, xelor.CellId);
        }

        /// <summary>
        /// A waiting sub-cast keeps the cast's critical, for the chain it runs when the delay is
        /// up: Reloj de Bolsillo's delayed blow is 36 to 38 after a critical cast.
        /// </summary>
        [Fact]
        public void A_waiting_sub_cast_keeps_the_casts_critical()
        {
            var xelor = Character(250);
            var target = Monster(-1, 300);
            var fight = Board(xelor, target);

            EffectEngine.Resolver(fight, xelor, 13293, 1, null, EffectEngine.AlLanzar, 1, celdaApuntada: 300, critico: true);

            Assert.True(Assert.Single(xelor.Buffs.Puestos, b => b.Pendiente).Critico);
        }

        /// <summary>
        /// Ralentización on an enemy in telefrag steals the AP it takes: 18973, cast on him, waits
        /// on "CAPAS" with mask C -- its caster, the Xelor, removing AP -- and gives him 30839's
        /// +1 AP. Hooked on the enemy, whom the trigger never reaches, it stole nothing and stayed
        /// on him after the cast.
        /// </summary>
        [Fact]
        public async Task Ralentizacion_steals_the_AP_of_an_enemy_in_telefrag()
        {
            var xelor = Character(250);
            var enemy = Monster(-1, 252);
            var fight = Board(xelor, enemy);
            enemy.Buffs.PonerEstado(251);
            try
            {
                EffectEngine.DadoDeRetirada = () => 0.0;
                await FightHandler.AplicarEfectosAsync(null, fight, xelor, 13242, 1, enemy, EffectEngine.AlLanzar, enemy.CellId);
            }
            finally { EffectEngine.DadoDeRetiradaPorDefecto(); }

            Assert.Equal(1, xelor.Buffs.De(Fighter.CaracteristicaDePuntosDeAccion, fight.RoundNumber));
            Assert.DoesNotContain(enemy.Buffs.ActiveSpells, a => a.Hechizo == 18973);
            Assert.DoesNotContain(xelor.Buffs.ActiveSpells, a => a.Hechizo == 18973);
        }

        /// <summary>
        /// Desincronización's trap carries no duration: it stays until it is stepped on. Read as
        /// one round, it went away at the Xelor's next turn start.
        /// </summary>
        [Fact]
        public async Task Desincronizacion_s_trap_outlives_the_xelor_s_turns()
        {
            var xelor = Character(250);
            var fight = Board(xelor, Monster(-1, 100));

            await FightHandler.AplicarEfectosAsync(null, fight, xelor, 13296, 1, null, EffectEngine.AlLanzar, 300);
            var trap = Assert.Single(fight.Glifos);

            for (int turn = 0; turn < 12; turn++)
            {
                fight.NextTurn();
                await FightHandler.QuitarLosGlifosCaducadosAsync(fight, fight.CurrentFighter);
            }

            Assert.Contains(trap, fight.Glifos);
        }

        /// <summary>
        /// Rayo Oscuro is cast in line only (CastInLine, no CastInDiagonal): a cell along the row
        /// is good, the diagonal one next to it is not. Unchecked, a jwh off the line went through.
        /// </summary>
        [Fact]
        public void Rayo_oscuro_is_cast_along_its_lines_only()
        {
            int flags = FightHandler.LimitesDeGrado(13253, 3).Flags;
            Assert.NotEqual(0, flags & SpellLevelFlags.CastInLine);
            Assert.Equal(0, flags & SpellLevelFlags.CastInDiagonal);

            const int from = 357;
            var (x, y) = Jondo.Unity.World.Maps.MapGeometry.CellToPoint(from);
            int At(int dx, int dy) => Enumerable.Range(0, 560)
                .First(c => Jondo.Unity.World.Maps.MapGeometry.CellToPoint(c) == (x + dx, y + dy));

            Assert.True(SpellLevelFlags.OnItsLines(flags, from, At(3, 0)));
            Assert.True(SpellLevelFlags.OnItsLines(flags, from, At(0, -2)));
            Assert.False(SpellLevelFlags.OnItsLines(flags, from, At(1, 1)));
            Assert.False(SpellLevelFlags.OnItsLines(flags, from, At(1, 3)));
            // A level that asks for neither is cast anywhere.
            Assert.True(SpellLevelFlags.OnItsLines(0, from, At(1, 3)));
        }

        /// <summary>
        /// Pesanteur (7) is cantSwitchPosition alone: it keeps an enemy from trading places, not from
        /// being taken to a free cell. Golpe de Xelor still moves him; read as cantBeMoved, he
        /// stayed where he was.
        /// </summary>
        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public async Task Pesanteur_stops_a_swap_but_not_a_teleport_to_a_free_cell(bool occupied)
        {
            var xelor = Character(300);
            var enemy = Monster(-1, 328);
            var fight = Board(xelor, enemy);
            enemy.Buffs.PonerEstado(7);
            var (grade, _, _) = SpellEffects.GradoDe(13252, 200);

            Fighter other = null;
            if (occupied)
            {
                // Where the free cast below lands him, taken by another.
                var probe = Board(Character(300), Monster(-1, 328));
                var moved = probe.Todos.Single(f => f.Id == -1);
                await FightHandler.AplicarEfectosAsync(null, probe, probe.Todos.Single(f => f.Id == Xelor), 13252, grade,
                                                       moved, EffectEngine.AlLanzar, 328);
                other = Monster(-2, moved.CellId);
                fight.AddMonster(other);
                other.CellId = moved.CellId;
            }

            await FightHandler.AplicarEfectosAsync(null, fight, xelor, 13252, grade, enemy, EffectEngine.AlLanzar, 328);

            Assert.Equal(occupied, enemy.CellId == 328);
            if (other != null) Assert.NotEqual(328, other.CellId);   // and nobody swapped into his cell
        }

        /// <summary>
        /// Arenas del Tiempo sends its target back a cell, swapping with whoever stands there, and
        /// bounces to the nearest one not yet hit -- the one just swapped. The bounce does not swap
        /// the two back: each sub-cast had telefrags of its own, and the pair undid its move.
        /// </summary>
        [Fact]
        public async Task Arenas_del_tiempo_s_bounce_does_not_swap_the_pair_back()
        {
            var xelor = Character(100);
            var target = Monster(-1, 301);
            var fight = Board(xelor, target);
            target.Andar(new[] { 301, 300 });
            var occupant = Monster(-2, 301);
            fight.AddMonster(occupant);
            occupant.CellId = 301;

            await FightHandler.AplicarEfectosAsync(null, fight, xelor, 13292, 1, target, EffectEngine.AlLanzar, 300);

            Assert.Equal(301, target.CellId);
            Assert.Equal(300, occupant.CellId);
        }

        /// <summary>
        /// A telefrag spent on Reloj de Arena's bearer brings its blow a round nearer: the
        /// telefrag's 13265 puts 6130 and takes it off at once, and the "EON6130" hook of 30843
        /// sets the bomb to its next stage. Read after the spell, the state was gone already and
        /// the hook never went off.
        /// </summary>
        [Fact]
        public async Task A_telefrag_spent_on_reloj_de_arena_s_bearer_brings_the_blow_nearer()
        {
            var xelor = Character(250);
            var enemy = Monster(-1, 300);
            var fight = Board(xelor, enemy);
            int round = fight.RoundNumber;
            var (grade, _, _) = SpellEffects.GradoDe(13261, 200);

            await FightHandler.AplicarEfectosAsync(null, fight, xelor, 13261, grade, enemy, EffectEngine.AlLanzar, enemy.CellId);
            Assert.Contains(enemy.Buffs.Puestos, b => b.Pendiente && b.Dado == 30843 && b.Cara == 3 && b.EmpiezaEnRonda == round + 2);

            // Refracción spends it, as in the audit's fight: 13286, 13303, then the telefrag's 13265.
            enemy.Buffs.PonerEstado(251);
            var (refraccion, _, _) = SpellEffects.GradoDe(13286, 200);
            await FightHandler.AplicarEfectosAsync(null, fight, xelor, 13286, refraccion, enemy, EffectEngine.AlLanzar, enemy.CellId);
            Assert.False(enemy.Buffs.TieneEstado(251));

            Assert.DoesNotContain(enemy.Buffs.Puestos, b => b.Pendiente && b.Dado == 30843 && b.Cara == 3 && b.EmpiezaEnRonda == round + 2);
            Assert.Contains(enemy.Buffs.Puestos, b => b.Pendiente && b.Dado == 30843 && b.Cara == 3 && b.EmpiezaEnRonda == round + 1);
        }

        /// <summary>
        /// Remanencia's hook on an enemy lasts as its 6135 does, to the Xelor's next turn start:
        /// the enemy who plays first in the round still walks back. Swept at the round's first
        /// trigger, the hook was gone while its state still showed, for anybody quicker than the
        /// Xelor.
        /// </summary>
        [Fact]
        public async Task Remanencia_s_hook_lasts_to_the_xelor_s_turn_on_one_who_plays_first()
        {
            var xelor = Character(250);
            var enemy = Monster(-1, 300);
            enemy.Initiative = 5000;
            var fight = Board(xelor, enemy);
            fight.StartFight();
            TurnsUntil(fight, () => fight.CurrentFighter == xelor);
            int round = fight.RoundNumber;

            await FightHandler.AplicarEfectosAsync(null, fight, xelor, Remanencia, 1, enemy, EffectEngine.AlLanzar, enemy.CellId);
            Assert.Contains(enemy.Buffs.ActiveSpells, a => a.Hechizo == Remanencia);

            TurnsUntil(fight, () => fight.RoundNumber != round);
            Assert.Equal(enemy, fight.CurrentFighter);
            await FightHandler.EngancheAsync(null, fight, enemy, EffectEngine.AlEmpezarElTurno);

            Assert.True(enemy.Buffs.TieneEstado(6135));
            Assert.Contains(enemy.Buffs.ActiveSpells, a => a.Hechizo == Remanencia);

            // And at the Xelor's turn start, with its row.
            fight.NextTurn();
            Assert.Equal(xelor, fight.CurrentFighter);
            FightHandler.TurnStartSweep(fight, xelor);
            Assert.False(enemy.Buffs.TieneEstado(6135));
            Assert.DoesNotContain(enemy.Buffs.ActiveSpells, a => a.Hechizo == Remanencia);
        }

        /// <summary>
        /// Aguja's poison is hooked once on its target: 30842 arms its "TB" rows from inside the
        /// chain and leaves its 2328 there too, and was hooked a second time by that row. Its
        /// turn-start blow went off twice.
        /// </summary>
        [Fact]
        public async Task Aguja_s_poison_is_hooked_once()
        {
            var xelor = Character(250);
            var enemy = Monster(-1, 300);
            var fight = Board(xelor, enemy);

            await FightHandler.AplicarEfectosAsync(null, fight, xelor, 13244, 1, enemy, EffectEngine.AlLanzar, enemy.CellId);

            Assert.Single(enemy.Buffs.ActiveSpells, a => a.Hechizo == 30842);
        }

        /// <summary>
        /// Aguja's poison lasts as its 2328, two rounds to the Xelor's turn: an enemy quicker than
        /// him still takes its second tick. Swept at the round's first trigger, it was lost.
        /// </summary>
        [Fact]
        public async Task Aguja_s_poison_ticks_twice_on_one_who_plays_first()
        {
            var xelor = Character(250);
            var enemy = Monster(-1, 300);
            enemy.Initiative = 5000;
            var fight = Board(xelor, enemy);
            fight.StartFight();
            TurnsUntil(fight, () => fight.CurrentFighter == xelor);
            int round = fight.RoundNumber;

            await FightHandler.AplicarEfectosAsync(null, fight, xelor, 13244, 1, enemy, EffectEngine.AlLanzar, enemy.CellId);

            for (int tick = 1; tick <= 2; tick++)
            {
                TurnsUntil(fight, () => fight.RoundNumber >= round + tick && fight.CurrentFighter == enemy);
                int life = enemy.CurrentHP;
                await FightHandler.EngancheAsync(null, fight, enemy, EffectEngine.AlEmpezarElTurno);
                Assert.True(enemy.CurrentHP < life, $"no poison at tick {tick} (round {fight.RoundNumber})");
            }
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
