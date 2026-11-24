using System.Linq;
using System.Threading.Tasks;
using Jondo.Unity.Server.Handlers;
using Jondo.Unity.Server.Managers;
using Jondo.Unity.World.Fights;
using Jondo.Unity.World.Maps;
using Xunit;

namespace Jondo.Unity.Tests.Combat
{
    /// <summary>
    /// Kanniboul village's 212 and its 6095: a blow, the state 848 on itself, and its target casts
    /// 6169 -- a 765 on its foes in 848, under "D", and the 848 taken off. The one sneezed on takes
    /// every blow dealt to 212 for a turn, his own as well: under a monster's 765 the one hitting
    /// is not left out, as he is under the Sacrier's (LinkedDamageTests).
    /// </summary>
    [Collection("MapManager")]
    public class KanniboulSneezeTests
    {
        private const int Fipi = 212, Sneeze = 6095;

        private static readonly SpellEffect Blow = new()
        {
            EffectId = 97, EffectUid = 900111, DiceNum = 100, DiceSide = 100, Element = 1, TargetMask = "A",
        };

        [Fact]
        public async Task The_one_sneezed_on_takes_his_own_blows_on_it()
        {
            if (SpellEffects.De(Sneeze, 1).Count == 0) return;   // no spell data
            var fight = new FightInstance(1, 1);
            var me = new Fighter
            {
                Id = 10, TeamId = 0, CellId = 300, MaxHP = 5000, CurrentHP = 5000, Level = 150,
                MaxAP = 12, CurrentAP = 12, MaxMP = 6, CurrentMP = 6,
            };
            var fipi = new Fighter
            {
                Id = -1, TeamId = 1, CellId = MapGeometry.GetNeighbors(300).First(), MaxHP = 300, CurrentHP = 300,
                Level = 60, IsMonster = true, MonsterId = Fipi, MaxAP = 6, CurrentAP = 6, MaxMP = 3, CurrentMP = 3,
            };
            fight.AddPlayer(me); fight.AddOpponent(fipi);
            fight.StartFight();

            await FightHandler.LanzarPorOrdenAsync(null, fight, fipi, Sneeze, 1, me, me.CellId,
                                                   EffectEngine.AlLanzar, SpellEffects.De(Sneeze, 1));
            Assert.Contains(fipi.Buffs.Puestos, r => r.EffectId == EffectEngine.InterceptaLosDanos && r.Quien == me.Id);

            int mine = me.CurrentHP;
            await FightHandler.HurtAsync(null, fight, me, 1, 1, fipi, fipi.CellId, tirada: new[] { Blow });
            Assert.Equal(300, fipi.CurrentHP);
            Assert.True(me.CurrentHP < mine);
        }
            /// <summary>
        /// The boss 2960's behaviour (startingSpellId 16496, 2413 grade 1) has every ally cast
        /// grade 2 on himself: a hook, "when I am hurt, cast 6172", whose 2020 heals the allies
        /// within two cells of the one hurt by half of what he took.
        /// </summary>
        [Fact]
        public async Task The_moon_blessing_heals_the_allies_round_the_one_hurt()
        {
            if (SpellEffects.De(2413, 1).Count == 0) return;
            var fight = new FightInstance(1, 1);
            var me = new Fighter
            {
                Id = 10, TeamId = 0, CellId = 100, MaxHP = 5000, CurrentHP = 5000, Level = 150,
                MaxAP = 12, CurrentAP = 12, MaxMP = 6, CurrentMP = 6,
            };
            Fighter Mob(long id, int template, int cell, int hp) => new()
            {
                Id = id, TeamId = 1, CellId = cell, MaxHP = 2000, CurrentHP = hp, Level = 60, IsMonster = true,
                MonsterId = template, MaxAP = 6, CurrentAP = 6, MaxMP = 3, CurrentMP = 3,
            };
            var boss = Mob(-1, 2960, 400, 2000);
            var hit = Mob(-2, 4151, 300, 2000);
            var near = Mob(-3, 211, MapGeometry.GetNeighbors(300).First(), 1000);
            fight.AddPlayer(me); fight.AddOpponent(boss); fight.AddOpponent(hit); fight.AddOpponent(near);
            fight.StartFight();
            boss.Conducta = (2413, 1);
            await FightHandler.LanzarLaConductaAsync(null, fight, boss);
            var doll = Mob(-4, 2961, MapGeometry.GetNeighbors(300).Skip(1).First(), 500);
            doll.Invocador = boss.Id;
            fight.AddOpponent(doll);
            // Rounds later: the blessing lasts the fight.
            while (fight.RoundNumber < 4) fight.NextTurn();

            await FightHandler.HurtAsync(null, fight, me, 1, 1, hit, hit.CellId, tirada: new[] { Blow });
            int taken = 2000 - hit.CurrentHP;
            Assert.True(taken > 0);
            Assert.InRange(near.CurrentHP, 1000 + taken / 2 - 1, 1000 + taken / 2 + 1);
            Assert.InRange(doll.CurrentHP, 500 + taken / 2 - 1, 500 + taken / 2 + 1);
        }
    }
}
