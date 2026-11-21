using System.Linq;
using Jondo.Unity.Server.Managers;
using Jondo.Unity.World.Fights;
using Xunit;

namespace Jondo.Unity.Tests.Combat
{
    /// <summary>
    /// Miauvizor's game (Reto de Miauvizor): where he ends his turn, his face of the die summons
    /// dice on every cell of that face at an even distance and heals on the others (6235 at an
    /// even cell, 6330 at an odd one, "181 4282" and "402 6474" over fixed cell lists).
    /// </summary>
    /// <remarks>
    /// Dofus pour les noobs («Défi du Chaloeil»): «Si une entité est présente sur une case où
    /// devrait apparaître un Dé, ce dernier n'est pas invoqué».
    /// </remarks>
    public class MiauvizorDiceTests
    {
        private const int Dice = 4282, EvenFace = 6235, Miauvizor = 4263;

        [Fact]
        public void A_die_is_not_summoned_where_somebody_stands()
        {
            var effects = SpellEffects.De(EvenFace, 1);
            var summon = effects.FirstOrDefault(e => e.EffectId == 181);
            if (summon == null || summon.CeldasFijas.Count < 2) return;   // no data

            var fight = new FightInstance(1, 1);
            int bossCell = summon.CeldasFijas[0], playerCell = summon.CeldasFijas[1];
            var boss = new Fighter
            {
                Id = -1, TeamId = 1, CellId = bossCell, MaxHP = 22000, CurrentHP = 22000, Level = 220,
                IsMonster = true, MonsterId = Miauvizor, MaxAP = 20, CurrentAP = 20,
            };
            var player = new Fighter { Id = 10, TeamId = 0, CellId = playerCell, MaxHP = 1000, CurrentHP = 1000, Level = 200 };
            fight.AddMonster(boss);
            fight.AddPlayer(player);

            var cast = EffectEngine.Resolver(fight, boss, EvenFace, 1, boss, EffectEngine.AlLanzar, fight.RoundNumber,
                                             celdaApuntada: boss.CellId);
            var dice = cast.Where(o => o.Invoca == Dice).Select(o => o.CasillaDeLaInvocacion).ToList();

            Assert.DoesNotContain(bossCell, dice);
            Assert.DoesNotContain(playerCell, dice);
            Assert.Equal(summon.CeldasFijas.Skip(2).OrderBy(c => c), dice.OrderBy(c => c));
        }
    }
}
