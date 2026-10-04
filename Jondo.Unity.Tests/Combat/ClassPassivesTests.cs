using System.Linq;
using Jondo.Unity.Server.Managers;
using Jondo.Unity.World.Fights;
using Xunit;

namespace Jondo.Unity.Tests.Combat
{
    /// <summary>
    /// The spells a class carries into a fight on its own, and what the Tymador's does at his
    /// turn start: the state his walls double on, and two rungs for every bomb of his.
    /// </summary>
    public class ClassPassivesTests
    {
        private const int Tymador = 13;
        private const int Explobomba = 3112;

        private static Fighter Rogue(long id, int team, int cell) => new()
        {
            Id = id, TeamId = team, CellId = cell, MaxHP = 2000, CurrentHP = 2000, Level = 200,
        };

        private static Fighter Bomb(long id, Fighter owner, int cell) => new()
        {
            Id = id, TeamId = owner.TeamId, CellId = cell, MaxHP = 90, CurrentHP = 90,
            IsMonster = true, MonsterId = Explobomba, GradeIndex = 3, Invocador = owner.Id,
            SummonCost = 0, JuegaTurno = false, Level = 3,
        };

        /// <summary>
        /// The sismobomba capture's cascade: 25200 Explobomba, 25199 Tornabomba, 25197 Bomba de
        /// Agua, 25198 Sismobomba -- the initial spells drawn with the icons of the four bombs he
        /// knows, at his grade of each -- and La Astucia del Tymador last; a variant taken swaps
        /// its own in.
        /// </summary>
        [Fact]
        public void The_initial_spells_follow_the_icons_and_the_passive_closes()
        {
            var plain = ClassPassives.ForFight(Tymador, new[] { (13444, 3), (13435, 3), (13436, 2), (13491, 3), (13431, 3) });
            Assert.Equal(new[] { (25200, 3), (25199, 3), (25197, 2), (25198, 3), (20488, 1) }, plain);

            var resilient = ClassPassives.ForFight(Tymador, new[] { (13471, 2), (13444, 3) });
            Assert.Equal(new[] { (24847, 2), (25200, 3), (20488, 1) }, resilient);

            Assert.Equal(20488, ClassPassives.PassiveOf(Tymador));
            Assert.Equal(21977, ClassPassives.PassiveOf(9));
        }

        [Fact]
        public void At_his_turn_start_the_passive_marks_him_and_climbs_every_bomb_two_rungs()
        {
            var fight = new FightInstance(1, 1);
            var me = Rogue(10, 0, 300);
            var enemy = Rogue(20, 1, 400);
            fight.AddPlayer(me); fight.AddOpponent(enemy);
            var one = Bomb(-1, me, 260);
            var two = Bomb(-2, me, 230);
            fight.Invocar(one, me); fight.Invocar(two, me);
            one.Buffs.PonerEstado(2484); two.Buffs.PonerEstado(2484);

            var outcomes = EffectEngine.Resolver(fight, me, 20488, 1, me, EffectEngine.AlEmpezarElTurno,
                                                 fight.RoundNumber, celdaApuntada: 300);

            Assert.Contains(2483, me.Buffs.Estados);
            Assert.Equal(3, Combo.LevelOf(one));
            Assert.Equal(3, Combo.LevelOf(two));
            Assert.Contains(outcomes, o => o.HechizoOrigen == 20497 && o.Sobre == one);
            Assert.Contains(outcomes, o => o.HechizoOrigen == 20500 && o.Sobre == two);

            EffectEngine.Resolver(fight, me, 20488, 1, me, EffectEngine.AlAcabarElTurno,
                                  fight.RoundNumber, celdaApuntada: 300);
            Assert.DoesNotContain(2483, me.Buffs.Estados);
        }
    }
}
