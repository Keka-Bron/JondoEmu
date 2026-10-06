using System.Collections.Generic;
using System.Linq;
using Jondo.Unity.Server.Managers;
using Jondo.Unity.World.Fights;
using Jondo.Unity.World.Maps;
using Xunit;

namespace Jondo.Unity.Tests.Combat
{
    /// <summary>
    /// Effect 1009, "Activa una bomba": what the Rogue's Detonador is made of.
    /// </summary>
    /// <remarks>
    /// The engine did not implement it, so Detonador did nothing at all -- and neither did the
    /// other 35 spells that carry it. What it does is make the targeted bomb cast its own
    /// explosion, and the explosion carries the rest: the damage of its element in a circle of
    /// radius two, the 141 that kills the bomb, and another 1009 for the bombs it reaches.
    ///
    /// The bomb-to-explosion table is measured over the 22 Rogue captures, 96 explosions in all,
    /// and the client's own spell types agree with it one element at a time.
    /// </remarks>
    public class BombExplosionTests
    {
        private const int Detonador = 13432;
        private const int Explobomba = 3112;
        private const int ExplosionTymadora = 13455;

        private const int DanoAgua = 96, DanoTierra = 97, DanoAire = 98, DanoFuego = 99;

        /// <summary>The Rogue's four, of the ten the client's table brings.</summary>
        private static readonly int[] DelTymador = { 3112, 3113, 3114, 5161 };

        private static Fighter Vivo(long id, int team, int cell) => new()
        {
            Id = id, TeamId = team, CellId = cell,
            MaxHP = 500, CurrentHP = 500, MaxAP = 6, CurrentAP = 6,
        };

        private static Fighter Bomba(long id, Fighter dueno, int cell, int template = Explobomba)
        {
            var b = Vivo(id, dueno.TeamId, cell);
            b.IsMonster = true;
            b.MonsterId = template;
            b.GradeIndex = 3;
            b.Invocador = dueno.Id;
            b.SummonCost = 0;
            b.JuegaTurno = false;
            return b;
        }

        /// <summary>Cells at exactly this distance from the centre, in cell order.</summary>
        private static List<int> ADistancia(int centro, int distancia, int cuantas)
        {
            var salida = new List<int>();
            for (int cell = 0; cell < 560 && salida.Count < cuantas; cell++)
            {
                if (cell != centro && MapGeometry.Distance(centro, cell) == distancia)
                    salida.Add(cell);
            }
            return salida;
        }

        [Fact]
        public void The_detonator_is_built_on_effect_1009()
        {
            var activar = Assert.Single(SpellEffects.De(Detonador, 3),
                e => e.EffectId == EffectEngine.ActivarBomba);

            // Only the Rogue's FOUR. The client's table brings ten bombs -- there are more
            // around the world, from monsters and dungeons -- and the Detonador names its own.
            foreach (int template in DelTymador)
            {
                Assert.Contains("F" + template, activar.TargetMask);
                Assert.True(Bombs.Is(template));
            }
        }

        [Fact]
        public void Every_bomb_explosion_kills_the_bomb_and_hits_a_circle_of_two()
        {
            foreach (int template in DelTymador)
            {
                int explosion = Bombs.Explosion(template);
                var efectos = SpellEffects.De(explosion, 3);
                Assert.Contains(efectos, e => e.EffectId == 141);        // kills itself
                Assert.Contains(efectos, e => e.Forma == Zone.Circulo && e.Tamano == 2);
                // The explosion's own "activa una bomba" is the sheet's copy, for the client
                // only, and is not read: the chain reaction is the queue behind the 1009 that
                // set the first bomb off, which takes the bombs the explosion reaches.
                Assert.DoesNotContain(efectos, e => e.EffectId == EffectEngine.ActivarBomba);
                Assert.True(efectos.Any(e => e.EffectId is DanoAgua or DanoTierra
                                                        or DanoAire or DanoFuego),
                            $"la explosión {explosion} de la plantilla {template} no hace daño");
            }
        }

        [Fact]
        public void Detonating_an_explobomba_burns_what_stands_within_two_cells()
        {
            int centro = 270;
            var cerca = ADistancia(centro, 2, 2);
            var lejos = ADistancia(centro, 4, 1);
            Assert.Equal(2, cerca.Count);
            Assert.Single(lejos);

            var fight = new FightInstance(1, 1);
            var tymador = Vivo(10, team: 0, cell: 300);
            var bomba = Bomba(-2, tymador, centro);
            fight.AddPlayer(tymador);
            fight.AddPlayer(bomba);

            var dentro = cerca.Select((c, i) => Vivo(-10 - i, 1, c)).ToList();
            var fuera = Vivo(-99, 1, lejos[0]);
            foreach (var e in dentro) fight.AddMonster(e);
            fight.AddMonster(fuera);

            var salida = EffectEngine.Resolver(fight, tymador, Detonador, 3, bomba,
                                               EffectEngine.AlLanzar, fight.RoundNumber,
                                               celdaApuntada: bomba.CellId);

            // The bomb casts ITS explosion and dies in it.
            Assert.Contains(salida, o => o.HechizoOrigen == ExplosionTymadora);
            Assert.Contains(salida, o => o.Fulmina && o.Sobre == bomba);

            // And it burns the two who fall inside the circle, but not the one outside.
            var quemados = salida
                .Where(o => o.Efecto.EffectId == DanoFuego && o.HechizoOrigen == ExplosionTymadora)
                .Select(o => o.Sobre)
                .Distinct()
                .ToList();
            Assert.Equal(2, quemados.Count);
            foreach (var e in dentro) Assert.Contains(e, quemados);
            Assert.DoesNotContain(fuera, quemados);

            // And it goes out through the NESTED damage path, which is the one that sends the client the
            // cast animation from the bomb. Through the root path the hit would be
            // applied all the same but nobody would see anything explode.
            Assert.All(salida.Where(o => o.Efecto.EffectId == DanoFuego),
                       o => Assert.True(o.NestedDamage, "el daño de la explosión no es anidado"));
            Assert.All(salida.Where(o => o.Efecto.EffectId == DanoFuego),
                       o => Assert.Equal(2, o.DamageElement));
        }

        [Fact]
        public void A_bomb_never_goes_off_twice_in_the_same_chain()
        {
            // Two bombs one cell apart: each explosion reaches the other, and without a
            // brake they would set each other off until the engine's depth ran out.
            int centro = 270;
            int vecina = ADistancia(centro, 1, 1)[0];

            var fight = new FightInstance(1, 1);
            var tymador = Vivo(10, team: 0, cell: 300);
            var primera = Bomba(-2, tymador, centro);
            var segunda = Bomba(-3, tymador, vecina, template: 3113);
            fight.AddPlayer(tymador);
            fight.AddPlayer(primera);
            fight.AddPlayer(segunda);

            var salida = EffectEngine.Resolver(fight, tymador, Detonador, 3, primera,
                                               EffectEngine.AlLanzar, fight.RoundNumber,
                                               celdaApuntada: primera.CellId);

            foreach (var bomba in new[] { primera, segunda })
            {
                int veces = salida.Count(o => o.Fulmina && o.Sobre == bomba);
                Assert.True(veces <= 1, $"la bomba {bomba.Id} se ha matado {veces} veces");
            }
        }

        [Fact]
        public void A_bomb_thrown_at_somebody_goes_off_instead_of_being_placed()
        {
            const int ExplosionAlObjetivo = 13456;
            int celda = 270;
            var cerca = ADistancia(celda, 2, 1);

            var fight = new FightInstance(1, 1);
            var tymador = Vivo(10, team: 0, cell: 300);
            var victima = Vivo(-2, team: 1, cell: celda);
            var alLado = Vivo(-3, team: 1, cerca[0]);
            fight.AddPlayer(tymador);
            fight.AddMonster(victima);
            fight.AddMonster(alLado);

            var salida = EffectEngine.Resolver(fight, tymador, 13444, 3, victima,
                                               EffectEngine.AlLanzar, fight.RoundNumber,
                                               celdaApuntada: celda);

            // Not one bomb placed, and yet the target's explosion.
            Assert.DoesNotContain(salida, o => o.Invoca != 0);
            Assert.Contains(salida, o => o.HechizoOrigen == ExplosionAlObjetivo);

            var quemados = salida
                .Where(o => o.Efecto.EffectId == DanoFuego)
                .Select(o => o.Sobre).Distinct().ToList();
            Assert.Contains(victima, quemados);
            Assert.Contains(alLado, quemados);

            // And the Rogue is still alive: the target's explosion does NOT carry the 141 with mask C
            // that the one the bomb casts on itself does carry.
            Assert.DoesNotContain(salida, o => o.Fulmina && o.Sobre == tymador);
        }

        [Fact]
        public void A_bomb_thrown_at_an_empty_cell_is_still_placed()
        {
            var fight = new FightInstance(1, 1);
            var tymador = Vivo(10, team: 0, cell: 300);
            fight.AddPlayer(tymador);

            var salida = EffectEngine.Resolver(fight, tymador, 13444, 3, null,
                                               EffectEngine.AlLanzar, fight.RoundNumber,
                                               celdaApuntada: 270);

            Assert.Contains(salida, o => o.Invoca == Explobomba);
            Assert.DoesNotContain(salida, o => o.HechizoOrigen == 13456);
        }

        [Fact]
        public void Detonating_one_bomb_of_a_wall_sets_off_the_whole_chain()
        {
            // Four bombs in a row, each three cells from the previous one. A wall takes three
            // at most, so there are two walls and the third bomb is in both: the chain
            // has to cross through it and reach the fourth.
            var fight = new FightInstance(1, 1);
            var tymador = Vivo(10, team: 0, cell: 20);
            fight.AddPlayer(tymador);

            var (x, y) = MapGeometry.CellToPoint(200);
            var bombas = new List<Fighter>();
            for (int i = 0; i < 4; i++)
            {
                int celda = MapGeometry.PointToCell(x, y + i * 3);
                Assert.True(celda >= 0);
                var b = Bomba(-2 - i, tymador, celda);
                bombas.Add(b);
                fight.AddPlayer(b);
            }

            var salida = EffectEngine.Resolver(fight, tymador, Detonador, 3, bombas[0],
                                               EffectEngine.AlLanzar, fight.RoundNumber,
                                               celdaApuntada: bombas[0].CellId);

            // All four have killed themselves, and each ONCE only.
            foreach (var bomba in bombas)
            {
                Assert.Equal(1, salida.Count(o => o.Fulmina && o.Sobre == bomba));
            }
        }

        [Fact]
        public void A_bomb_next_door_goes_off_even_without_a_wall()
        {
            // Two side by side do NOT make a wall -- two cells have to be left -- but the explosion's
            // radius-two circle takes it down all the same.
            var fight = new FightInstance(1, 1);
            var tymador = Vivo(10, team: 0, cell: 20);
            fight.AddPlayer(tymador);

            var (x, y) = MapGeometry.CellToPoint(200);
            var primera = Bomba(-2, tymador, MapGeometry.PointToCell(x, y));
            var pegada = Bomba(-3, tymador, MapGeometry.PointToCell(x, y + 1));
            fight.AddPlayer(primera);
            fight.AddPlayer(pegada);

            Assert.Empty(BombWalls.Of(new[] { tymador, primera, pegada }, tymador));

            var salida = EffectEngine.Resolver(fight, tymador, Detonador, 3, primera,
                                               EffectEngine.AlLanzar, fight.RoundNumber,
                                               celdaApuntada: primera.CellId);

            Assert.Equal(1, salida.Count(o => o.Fulmina && o.Sobre == primera));
            Assert.Equal(1, salida.Count(o => o.Fulmina && o.Sobre == pegada));
        }

        [Fact]
        public void The_blast_radius_comes_from_the_spell_and_is_two()
        {
            foreach (int template in DelTymador)
            {
                Assert.Equal(2, EffectEngine.RadioDeLaExplosion(Bombs.Explosion(template), 3));
            }
        }

        [Fact]
        public void A_bomb_out_of_line_stays_where_it_is()
        {
            var fight = new FightInstance(1, 1);
            var tymador = Vivo(10, team: 0, cell: 20);
            fight.AddPlayer(tymador);

            var (x, y) = MapGeometry.CellToPoint(200);
            var enLinea = Bomba(-2, tymador, MapGeometry.PointToCell(x, y + 3));
            var suelta = Bomba(-3, tymador, MapGeometry.PointToCell(x + 5, y + 9));
            fight.AddPlayer(enLinea);
            fight.AddPlayer(suelta);

            var salida = EffectEngine.Resolver(fight, tymador, Detonador, 3, enLinea,
                                               EffectEngine.AlLanzar, fight.RoundNumber,
                                               celdaApuntada: enLinea.CellId);

            Assert.Contains(salida, o => o.Fulmina && o.Sobre == enLinea);
            Assert.DoesNotContain(salida, o => o.Fulmina && o.Sobre == suelta);
        }

        [Fact]
        public void Nothing_happens_when_the_target_is_not_a_bomb()
        {
            var fight = new FightInstance(1, 1);
            var tymador = Vivo(10, team: 0, cell: 300);
            var bicho = Vivo(-2, team: 1, cell: 270);
            bicho.IsMonster = true;
            bicho.MonsterId = 8070;                 // a tofu, which is not a bomb
            fight.AddPlayer(tymador);
            fight.AddMonster(bicho);

            var salida = EffectEngine.Resolver(fight, tymador, Detonador, 3, bicho,
                                               EffectEngine.AlLanzar, fight.RoundNumber,
                                               celdaApuntada: bicho.CellId);

            Assert.DoesNotContain(salida, o => o.HechizoOrigen == ExplosionTymadora);
        }
    }
}
