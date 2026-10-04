using System.Linq;
using Jondo.Unity.Server.Managers;
using Jondo.Unity.Server.Network;
using Jondo.Unity.World.Combat;
using Jondo.Unity.World.Fights;
using Jondo.Unity.World.Maps;
using Xunit;

namespace Jondo.Unity.Tests.Combat
{
    /// <summary>
    /// The pieces the Tymador kit is written on, each of them in the common engine: the P/p
    /// letters of a target mask, the f exclusion, the inner edge of a zone, the vitality
    /// percentage, the end of turn, the shield row, and what a summon is to its owner.
    /// </summary>
    public class SummonerMaskTests
    {
        private const int Patada = 13434;
        private const int UltimoAliento = 13446;
        private const int Tymadura = 13431;
        private const int Explobomba = 3112;

        private static Fighter Rogue(long id, int team, int cell) => new()
        {
            Id = id, TeamId = team, CellId = cell, MaxHP = 2000, CurrentHP = 2000, Level = 200,
            Vitality = 1150,
        };

        private static Fighter Bomb(long id, Fighter owner, int cell) => new()
        {
            Id = id, TeamId = owner.TeamId, CellId = cell, MaxHP = 90, CurrentHP = 90,
            IsMonster = true, MonsterId = Explobomba, GradeIndex = 3, Invocador = owner.Id,
            SummonCost = 0, JuegaTurno = false, Level = 3,
        };

        /// <summary>Patada's shield goes to the caster's own bombs and to nobody else's.</summary>
        [Fact]
        public void P_is_the_casters_own_summons()
        {
            var fight = new FightInstance(1, 1);
            var me = Rogue(10, 0, 300);
            var otherRogue = Rogue(11, 0, 302);
            var enemy = Rogue(20, 1, 304);
            fight.AddPlayer(me); fight.AddPlayer(otherRogue); fight.AddOpponent(enemy);
            int ring = MapGeometry.PointToCell(MapGeometry.CellToPoint(301).X + 1, MapGeometry.CellToPoint(301).Y);
            var mine = Bomb(-1, me, ring);
            var theirs = Bomb(-2, otherRogue, MapGeometry.PointToCell(MapGeometry.CellToPoint(301).X - 1, MapGeometry.CellToPoint(301).Y));
            fight.Invocar(mine, me); fight.Invocar(theirs, otherRogue);

            var outcomes = EffectEngine.Resolver(fight, me, Patada, 3, null, EffectEngine.AlLanzar,
                                                 fight.RoundNumber, celdaApuntada: 301);

            Assert.True(mine.PuntosDeEscudo > 0, "the caster's bomb is shielded");
            Assert.Equal(0, theirs.PuntosDeEscudo);          // "a,P,F3112": not the other Rogue's
            Assert.Equal(0, enemy.PuntosDeEscudo);
            Assert.Equal(0, me.PuntosDeEscudo);              // "No afecta al lanzador"
            Assert.Contains(outcomes, o => o.Escudo > 0 && o.Sobre == mine
                                           && o.Efecto.EffectId == EffectEngine.ShieldPanelEffect);
        }

        /// <summary>
        /// "h,P": a bomb's Encendimiento (20509) reaches its summoner, who is no summon -- the
        /// capture's jxm of effect 296 is cast by bomb -9 on the player. P conditions summons
        /// and lets everybody else through.
        /// </summary>
        [Fact]
        public void P_conditions_summons_and_lets_the_summoner_through()
        {
            var fight = new FightInstance(1, 1);
            var me = Rogue(10, 0, 300);
            var enemy = Rogue(20, 1, 304);
            fight.AddPlayer(me); fight.AddOpponent(enemy);
            var mine = Bomb(-1, me, 301);
            fight.Invocar(mine, me);

            var outcomes = EffectEngine.Resolver(fight, mine, 20509, 4, null, EffectEngine.AlLanzar,
                                                 fight.RoundNumber, celdaApuntada: 301);

            Assert.Contains(outcomes, o => o.Sobre == me);
            Assert.DoesNotContain(outcomes, o => o.Sobre == enemy);
        }

        /// <summary>175% of the caster's level, as the capture: 350 at level 200, 700 after a second one.</summary>
        [Fact]
        public void The_shield_is_a_percentage_of_the_casters_level_and_stacks()
        {
            var fight = new FightInstance(1, 1);
            var me = Rogue(10, 0, 300);
            fight.AddPlayer(me);
            var (x, y) = MapGeometry.CellToPoint(301);
            var mine = Bomb(-1, me, MapGeometry.PointToCell(x + 1, y));
            fight.Invocar(mine, me);

            EffectEngine.Resolver(fight, me, Patada, 3, null, EffectEngine.AlLanzar, fight.RoundNumber, celdaApuntada: 301);
            Assert.Equal(350, mine.PuntosDeEscudo);

            // The kick moved it: the second one is aimed a cell short of where it stands now,
            // the way the capture re-aims each Patada at the bomb's new place.
            var (bx, by) = MapGeometry.CellToPoint(mine.CellId);
            int again = MapGeometry.PointToCell(bx - 1, by);
            EffectEngine.Resolver(fight, me, Patada, 3, null, EffectEngine.AlLanzar, fight.RoundNumber, celdaApuntada: again);
            Assert.Equal(700, mine.PuntosDeEscudo);
            Assert.Equal(2, mine.Buffs.Puestos.Count(b => b.EffectId == EffectEngine.ShieldPanelEffect));
        }

        /// <summary>The inner edge: rings on the cross, a hole in the circle, the ring's thickness.</summary>
        [Fact]
        public void The_inner_edge_of_a_zone_leaves_the_near_cells_out()
        {
            int centre = 301;
            var ring3 = Zone.Casillas(Zone.Aspa, 3, 260, centre, minimo: 3);
            Assert.All(ring3, c => Assert.Equal(3, MapGeometry.Distance(centre, c)));
            Assert.Equal(4, ring3.Count);

            var cross = Zone.Casillas(Zone.Aspa, 6, 260, centre, minimo: 1);
            Assert.DoesNotContain(centre, cross);
            Assert.Contains(cross, c => MapGeometry.Distance(centre, c) == 1);

            var around = Zone.Casillas(Zone.Circulo, 2, 260, centre, minimo: 1);
            Assert.DoesNotContain(centre, around);
            Assert.Equal(12, around.Count);

            // The Q is the straight cross without its centre, as the client's zone factory
            // builds it: Salto's Q1/1 and a bare Q1 are both the four cells around the arrival,
            // and a Q2 is the X2 less its centre.
            var salto = Zone.Casillas(Zone.CruzRecta, 1, 260, centre, minimo: 1);
            Assert.DoesNotContain(centre, salto);
            Assert.Equal(4, salto.Count);
            Assert.All(salto, c => Assert.Equal(1, MapGeometry.Distance(centre, c)));
            Assert.Equal(salto.OrderBy(c => c), Zone.Casillas(Zone.CruzRecta, 1, 260, centre).OrderBy(c => c));
            var cruz = Zone.Casillas(Zone.CruzRecta, 2, 260, centre);
            Assert.DoesNotContain(centre, cruz);
            Assert.Equal(8, cruz.Count);
            Assert.Equal(Zone.Casillas(Zone.Aspa, 2, 260, centre).Where(c => c != centre).OrderBy(c => c),
                         cruz.OrderBy(c => c));

            // The ring is the O: exactly param1 away.
            var edge = Zone.Casillas(Zone.Anillo, 2, 260, centre);
            Assert.All(edge, c => Assert.Equal(2, MapGeometry.Distance(centre, c)));
        }

        /// <summary>
        /// The T is the bar across the cast: measured on seven impacts with positions
        /// (Cencerro, Magmacha Calcinada, Flecha de Pelea, Impacto Aplastante, Espora Dyka),
        /// every victim on the perpendicular of the cast or on the centre. Espada Destructora
        /// cast from next door never reaches the caster's own cell.
        /// </summary>
        [Fact]
        public void The_T_is_the_bar_across_the_cast()
        {
            // Flecha de Pelea in "entrar a sueño mediante invitacion": cast from 342 at 269,
            // the victim at 256, one cell across.
            var bar = Zone.Casillas(Zone.Barra, 2, 342, 269);
            Assert.Contains(269, bar);
            Assert.Contains(256, bar);
            Assert.Equal(5, bar.Count);
            var (cx, cy) = MapGeometry.CellToPoint(269);
            Assert.All(bar, c =>
            {
                var (x, y) = MapGeometry.CellToPoint(c);
                Assert.Equal(cx, x);           // the cast runs along x, so the bar runs along y
            });

            // Espada Destructora from the cell next to the target: a T1 with the target on the
            // caster's axis holds the target and the two cells beside it, not the caster.
            int caster = MapGeometry.PointToCell(cx - 1, cy);
            var t1 = Zone.Casillas(Zone.Barra, 1, caster, 269);
            Assert.Equal(3, t1.Count);
            Assert.DoesNotContain(caster, t1);
            Assert.Contains(MapGeometry.PointToCell(cx, cy + 1), t1);
            Assert.Contains(MapGeometry.PointToCell(cx, cy - 1), t1);
        }

        /// <summary>
        /// The half circle is the client's: the aimed cell and two diagonal rays bent back
        /// towards the caster, a V around the target, none of it nearer the caster than the
        /// target is.
        /// </summary>
        [Fact]
        public void The_half_circle_bends_back_towards_the_caster()
        {
            int centre = 301;
            var (x, y) = MapGeometry.CellToPoint(centre);
            int caster = MapGeometry.PointToCell(x - 3, y);
            var half = Zone.Casillas(Zone.MedioCirculo, 2, caster, centre);
            Assert.Equal(new[] { centre, MapGeometry.PointToCell(x - 1, y - 1), MapGeometry.PointToCell(x - 1, y + 1),
                                 MapGeometry.PointToCell(x - 2, y - 2), MapGeometry.PointToCell(x - 2, y + 2) },
                         half);
            int away = MapGeometry.Distance(caster, centre);
            Assert.All(half, c => Assert.True(MapGeometry.Distance(caster, c) >= away));
        }

        /// <summary>
        /// Último Aliento: -50% of the 1,150 of maximum life of the capture's naked Tymador is
        /// -575, off the maximum, shown as 153. Of the life, not of the vitality characteristic:
        /// that character had 100 of it, the scrolls' worth.
        /// </summary>
        [Fact]
        public void A_vitality_percentage_moves_the_maximum_by_the_flat_points()
        {
            var fight = new FightInstance(1, 1);
            var me = Rogue(10, 0, 300);
            me.MaxHP = 1150; me.CurrentHP = 1150; me.Vitality = 100;
            fight.AddPlayer(me);

            var outcomes = EffectEngine.Resolver(fight, me, UltimoAliento, 2, me, EffectEngine.AlLanzar,
                                                 fight.RoundNumber, celdaApuntada: 300);

            Assert.Equal(1150 - 575, me.MaxHP);
            Assert.Equal(1150 - 575, me.CurrentHP);
            var row = Assert.Single(me.Buffs.Puestos, b => b.EffectId == EffectSupport.VitalityFlatMalus);
            Assert.Equal(-575, row.Cuanto);
            Assert.Equal(11, row.Caracteristica);
            Assert.Contains(outcomes, o => o.Efecto.EffectId == EffectSupport.VitalityFlatMalus && o.Efecto.DiceNum == 575);
        }

        /// <summary>Tymadura ends the caster's turn: the engine says so, the handler passes it.</summary>
        [Fact]
        public void Tymadura_asks_for_the_turn_to_end()
        {
            var fight = new FightInstance(1, 1);
            var me = Rogue(10, 0, 300);
            fight.AddPlayer(me);

            var outcomes = EffectEngine.Resolver(fight, me, Tymadura, 3, me, EffectEngine.AlLanzar,
                                                 fight.RoundNumber, celdaApuntada: 257);

            Assert.Contains(outcomes, o => o.AcabaElTurno);
        }

        /// <summary>
        /// Mosquete's combo: the Tymador casts 20643 at his bomb, the BOMB casts 20497 on
        /// itself and climbs a rung, then 20500 and the once-a-turn mark 2523. A chained cast
        /// used to be handed to the parent caster at the parent's aimed cell, and "C" landed on
        /// the Tymador instead of the bomb.
        /// </summary>
        [Fact]
        public void Mosquete_gives_the_bomb_a_combo_through_the_chain()
        {
            var fight = new FightInstance(1, 1);
            var me = Rogue(10, 0, 300);
            var enemy = Rogue(20, 1, 400);
            fight.AddPlayer(me); fight.AddOpponent(enemy);
            var (x, y) = MapGeometry.CellToPoint(260);
            var mine = Bomb(-1, me, MapGeometry.PointToCell(x + 1, y));
            fight.Invocar(mine, me);
            mine.Buffs.PonerEstado(2484);

            var outcomes = EffectEngine.Resolver(fight, me, 13473, 2, null, EffectEngine.AlLanzar,
                                                 fight.RoundNumber, celdaApuntada: 260);

            Assert.Contains(2485, mine.Buffs.Estados);
            Assert.DoesNotContain(2484, mine.Buffs.Estados);
            Assert.Contains(2523, mine.Buffs.Estados);
            Assert.Contains(outcomes, o => o.HechizoOrigen == 20497 && o.Sobre == mine);
            Assert.DoesNotContain(me.Buffs.Estados, e => e == 2485);
        }

        /// <summary>
        /// A bomb is born in Combo I, and its own spell is what puts it there: Encendimiento's
        /// 1017 hands La Astucia del Tymador back to the summoner, whose 792 casts the ladder on
        /// the bomb once -- state 2484 and nothing above it, as the sismobomba capture has it.
        /// </summary>
        [Fact]
        public void A_bomb_is_born_in_combo_one_through_its_own_spell()
        {
            var fight = new FightInstance(1, 1);
            var me = Rogue(10, 0, 300);
            var enemy = Rogue(20, 1, 400);
            fight.AddPlayer(me); fight.AddOpponent(enemy);
            var mine = Bomb(-1, me, 260);
            fight.Invocar(mine, me);

            EffectEngine.Resolver(fight, mine, 13468, 1, mine, EffectEngine.AlLanzar,
                                  fight.RoundNumber, celdaApuntada: 260);

            Assert.Equal(1, Combo.LevelOf(mine));
            Assert.Contains(2484, mine.Buffs.Estados);
            Assert.DoesNotContain(2485, mine.Buffs.Estados);
        }

        /// <summary>
        /// Polvo on a fresh bomb leaves it Unmovable (97) and marked (2511) and gives it no
        /// combo: its two ladder casts ask for 2511 on the bomb before the cast, and the real
        /// server sends five buffs and no 20497 -- both casts of "tymador-polvo". The two
        /// combos are the X trigger's, with the explosion: they come when the bomb is
        /// destroyed, resolved with the bomb still standing -- the killing blow fires them
        /// before taking the last point -- so it climbs two rungs and casts its explosion.
        /// </summary>
        [Fact]
        public void Polvo_gives_no_combo_on_the_cast_and_two_with_the_explosion_on_death()
        {
            var fight = new FightInstance(1, 1);
            var me = Rogue(10, 0, 300);
            var enemy = Rogue(20, 1, 400);
            fight.AddPlayer(me); fight.AddOpponent(enemy);
            var mine = Bomb(-1, me, 260);
            fight.Invocar(mine, me);
            mine.Buffs.PonerEstado(2484);

            EffectEngine.Resolver(fight, me, 13441, 2, mine, EffectEngine.AlLanzar,
                                  fight.RoundNumber, celdaApuntada: 260);

            Assert.Contains(97, mine.Buffs.Estados);
            Assert.Contains(2511, mine.Buffs.Estados);
            Assert.Equal(1, Combo.LevelOf(mine));

            var onDeath = EffectEngine.Resolver(fight, me, 13441, 2, mine, EffectEngine.AlMorir,
                                                fight.RoundNumber, celdaApuntada: 260);

            Assert.Equal(3, Combo.LevelOf(mine));
            Assert.Contains(onDeath, o => o.HechizoOrigen == Bombs.Explosion(Explobomba));
        }

        /// <summary>Who plays and who does not, off the template flag.</summary>
        [Fact]
        public void A_summon_plays_by_its_template_flag()
        {
            Assert.True(Summons.De(3120, 3).Juega);      // Tymobot
            Assert.True(Summons.De(8348, 1).Juega);      // Baliza de Supervivencia
            Assert.False(Summons.De(3112, 3).Juega);     // Explobomba
            Assert.False(Summons.De(8347, 1).Juega);     // Baliza táctica
        }

        /// <summary>The Tymobot at grade 3: 30938 at grade 1, the other three at grade 3.</summary>
        [Fact]
        public void A_summon_knows_its_spells_at_the_grade_its_grade_opens()
        {
            var bot = Summons.De(3120, 3);

            Assert.Equal(new[] { (30938, 1), (13453, 3), (13451, 3), (13452, 3) }, bot.Hechizos);
            Assert.Equal(new[] { (31154, 3), (31156, 3) }, Summons.De(8070, 1).Hechizos);
        }

        /// <summary>The owner's jyy for the Tymobot -12 of the capture, slot by slot.</summary>
        [Fact]
        public void The_summons_spell_bar_is_the_owners()
        {
            byte[] jyy = FightProtocol.BuildSummonSpellBar(-12, 53721497699,
                new[] { (13451, 3), (13452, 3), (30938, 1), (13453, 3) });

            Assert.Equal("18f4ffffffffffffffff0120e380b490c80132070803188b69200632070803188c6920063208080118daf1" +
                         "01200632070803188d6920063a053203108b693a0710013203108c693a081002320410daf1013a0710033203108d69",
                         string.Concat(jyy.Select(b => b.ToString("x2"))));
        }

        /// <summary>
        /// A summon that can act -- a step to take or a spell of its own: the Tymobot, the
        /// walking bomb, an Osamodas' animal -- is its summoner's to play; one with nothing
        /// to play -- a beacon, a bomb -- hands its turn on by itself, with no jyj to anybody.
        /// </summary>
        [Fact]
        public void A_summon_is_played_by_its_summoner_when_it_has_anything_to_play()
        {
            var me = Rogue(10, 0, 300);
            var bomb = Bomb(-1, me, 301);
            var bot = Bomb(-2, me, 302);
            bot.MaxMP = 5; bot.HechizosDeInvocado = new[] { (13451, 3), (30938, 1) };
            var beacon = Bomb(-3, me, 303);
            beacon.MaxMP = -1; beacon.MaxAP = 0;
            Assert.True(me.ControlledBy(10));
            Assert.True(bomb.PlaysOnItsOwn);
            Assert.False(bomb.ControlledBy(10));
            Assert.False(bot.PlaysOnItsOwn);
            Assert.True(bot.ControlledBy(10));
            Assert.False(bot.ControlledBy(11));
            Assert.True(beacon.PlaysOnItsOwn);
            Assert.False(me.ControlledBy(11));
        }
    }
}
