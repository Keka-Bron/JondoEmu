using System.Linq;
using Jondo.Unity.Server.Managers;
using Jondo.Unity.World.Fights;
using Jondo.Unity.World.Maps;
using Xunit;

namespace Jondo.Unity.Tests.Combat
{
    /// <summary>
    /// What a spell does with the blow that set it off: returns a share of it (1223 and its
    /// elements), heals the attacker with it (786), heals with it (2973, 2020). Checked against
    /// the Yopuka's Masacre and the Xelor's cómplice.
    /// </summary>
    public class ReturnedDamageTests
    {
        private static Fighter Character(long id, int team, int cell) => new()
        {
            Id = id, TeamId = team, CellId = cell, MaxHP = 2000, CurrentHP = 2000, Level = 200,
            MaxAP = 12, CurrentAP = 12, MaxMP = 6, CurrentMP = 6,
        };

        private static Fighter Monster(long id, int cell) => new()
        {
            Id = id, TeamId = 1, CellId = cell, MaxHP = 900, CurrentHP = 900, Level = 50,
            MaxAP = 6, CurrentAP = 6, MaxMP = 3, CurrentMP = 3, IsMonster = true, MonsterId = 494,
        };

        private static int Beside(int cell, int dx, int dy)
        {
            var (x, y) = MapGeometry.CellToPoint(cell);
            return MapGeometry.PointToCell(x + dx, y + dy);
        }

        /// <summary>
        /// Masacre on -1, then the Yopuka's 64 of water on it: -1 casts 13127, and 30% comes back
        /// on its ally next to it as "jwe f3=Yopuka f14=1227 f40{f2=-2 f3=19 f4=3}" -- frame 167 of
        /// "yopuka-masacre". In the element of the blow, as the family's water one, and in the
        /// name of the Yopuka whose hook it is, although -1 casts the spell that returns it.
        /// </summary>
        [Fact]
        public void Masacre_returns_thirty_percent_of_the_blow_in_its_element_in_the_Yopukas_name()
        {
            var fight = new FightInstance(1, 1);
            var yopuka = Character(53721170019, 0, 300);
            var target = Monster(-1, 330);
            var ally = Monster(-2, Beside(330, 1, 0));
            fight.AddPlayer(yopuka); fight.AddOpponent(target); fight.AddOpponent(ally);

            fight.TriggeringAttacker = yopuka;
            fight.DanoDelDisparo = 64;
            fight.ElementoDelDisparo = 3;
            var outcomes = EffectEngine.Resolver(fight, yopuka, 13112, 1, target, EffectEngine.AlRecibirDano, 1,
                                                 celdaApuntada: target.CellId);

            var returned = Assert.Single(outcomes, o => o.NestedDamage);
            Assert.Equal(ally, returned.Sobre);
            Assert.Equal(1227, returned.Efecto.EffectId);
            Assert.Equal(3, returned.DamageElement);
            Assert.Equal(yopuka, returned.Caster);
            Assert.Equal(target, returned.AnimationCaster);
            Assert.Equal(19, EffectEngine.BaseDelModo(returned.Efecto, 30, yopuka, ally, fight));
        }

        /// <summary>
        /// The Xelor hits his cómplice for 92 of air with a spell (DS): the cómplice's hook casts
        /// 29242 three times -- grade 3, grade 4 on the Xelor through "h,P,O", grade 5 -- and 75%
        /// lands on the enemy beside it, "jwe f3=-5 f14=1225 f40{f2=-3 f3=69 f4=4}" (frame 222 of
        /// the cómplice capture). Hit by anybody but its summoner, it returns nothing.
        /// </summary>
        [Fact]
        public void The_complice_returns_its_summoners_spell_damage()
        {
            var fight = new FightInstance(1, 1);
            var xelor = Character(53720973411, 0, 300);
            var complice = new Fighter
            {
                Id = -5, TeamId = 0, CellId = 330, MaxHP = 600, CurrentHP = 600, Level = 200,
                IsMonster = true, MonsterId = 5144, Invocador = xelor.Id,
            };
            var enemy = Monster(-3, Beside(330, 1, 0));
            var stranger = Character(7, 1, 100);
            fight.AddPlayer(xelor); fight.Invocar(complice, xelor); fight.AddOpponent(enemy); fight.AddOpponent(stranger);

            fight.TriggeringAttacker = xelor;
            fight.DanoDelDisparo = 92;
            fight.ElementoDelDisparo = 4;
            var outcomes = EffectEngine.Resolver(fight, complice, 29242, 1, complice, EffectEngine.AlRecibirDanoDeHechizo, 1,
                                                 celdaApuntada: complice.CellId);

            var returned = Assert.Single(outcomes, o => o.NestedDamage);
            Assert.Equal(enemy, returned.Sobre);
            Assert.Equal(1225, returned.Efecto.EffectId);
            Assert.Equal(complice, returned.Caster);
            Assert.Equal(69, EffectEngine.BaseDelModo(returned.Efecto, 75, complice, enemy, fight));

            fight.TriggeringAttacker = stranger;
            outcomes = EffectEngine.Resolver(fight, complice, 29242, 1, complice, EffectEngine.AlRecibirDanoDeHechizo, 1,
                                             celdaApuntada: complice.CellId);
            Assert.DoesNotContain(outcomes, o => o.NestedDamage);
        }

        /// <summary>1223 goes out as the family's one of the blow's element: 1224 neutral to 1228 earth.</summary>
        [Fact]
        public void A_returned_blow_takes_the_element_of_the_one_that_set_it_off()
        {
            Assert.Equal(1224, EffectEngine.DelElemento(1223, 0));
            Assert.Equal(1225, EffectEngine.DelElemento(1223, 4));
            Assert.Equal(1226, EffectEngine.DelElemento(1223, 2));
            Assert.Equal(1227, EffectEngine.DelElemento(1223, 3));
            Assert.Equal(1228, EffectEngine.DelElemento(1223, 1));
            Assert.Equal(1127, EffectEngine.DelElemento(1123, 3));
            Assert.Equal(1227, EffectEngine.DelElemento(1227, 1));   // already elemental
        }

        /// <summary>
        /// Presa leaves its "786 under D" on the prey: whoever hits it heals 15% of the blow --
        /// "cura a las entidades que atacan al objetivo una parte de los daños ocasionados".
        /// </summary>
        [Fact]
        public void The_prey_heals_whoever_hits_it()
        {
            var fight = new FightInstance(1, 1);
            var uginak = Character(1, 0, 300);
            var ally = Character(2, 0, 290);
            var prey = Monster(-1, 301);
            ally.CurrentHP = 1000;
            fight.AddPlayer(uginak); fight.AddPlayer(ally); fight.AddOpponent(prey);

            fight.TriggeringAttacker = ally;
            fight.DanoDelDisparo = 200;
            var outcomes = EffectEngine.Resolver(fight, uginak, 13748, 1, prey, EffectEngine.AlRecibirDano, 1,
                                                 celdaApuntada: prey.CellId);

            var heal = Assert.Single(outcomes, o => o.Cura > 0);
            Assert.Equal(ally, heal.Sobre);
            Assert.Equal(30, heal.Cura);
        }

        /// <summary>
        /// Mala Sombra's 28716 at grade 2, set off by a blow on the enemy, heals the Sram's allies
        /// around him (not the Sram, "g") by half of it, and takes its own hook away.
        /// </summary>
        [Fact]
        public void Mala_Sombra_heals_the_allies_around_by_half_the_blow()
        {
            var fight = new FightInstance(1, 1);
            var sram = Character(1, 0, Beside(330, 0, 1));
            var ally = Character(2, 0, Beside(330, 1, 0));
            var enemy = Monster(-1, 330);
            ally.CurrentHP = 1000; sram.CurrentHP = 1000;
            fight.AddPlayer(sram); fight.AddPlayer(ally); fight.AddOpponent(enemy);

            fight.DanoDelDisparo = 120;
            var outcomes = EffectEngine.Resolver(fight, sram, 28716, 2, enemy, EffectEngine.AlLanzar, 1,
                                                 celdaApuntada: enemy.CellId);

            var heal = Assert.Single(outcomes, o => o.Cura > 0);
            Assert.Equal(ally, heal.Sobre);
            Assert.Equal(60, heal.Cura);
        }
    }
}
