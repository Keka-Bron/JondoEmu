using System.Linq;
using Jondo.Unity.Launcher;
using Jondo.Unity.Server.Handlers;
using Jondo.Unity.Server.Managers;
using Jondo.Unity.World.Fights;
using Xunit;

namespace Jondo.Unity.Tests.Combat
{
    /// <summary>
    /// Master Cuerbok, the boss of his library, run through the real engine on the real data.
    /// </summary>
    /// <remarks>
    /// The guide (Dofus pour les noobs, «Bibliothèque du Maître Corbac»): no invulnerability nor
    /// special mechanic; his Carapace d'Ailes, from turn 3 and every 4 turns, takes 12 AP and 5 MP
    /// off him for 2 turns, returns 400 damage when he is hit, and «quand il commence son tour
    /// sous l'effet de ce sort, il inflige 135 de dégâts»; and «Il ne peut y avoir qu'un seul
    /// Corbac invoqué sur le terrain».
    /// </remarks>
    [Collection("MapManager")]
    public class MasterCuerbokTests
    {
        private const int Cuerbok = 289;
        private const int Crow = 281;
        private const int Shell = 630;          // Caparazón de Alas

        private static Fighter Boss(int cell) => new()
        {
            Id = -1, TeamId = 1, CellId = cell, MaxHP = 4500, CurrentHP = 4500, Level = 110,
            IsMonster = true, MonsterId = Cuerbok, MaxAP = 12, CurrentAP = 12, MaxMP = 6, CurrentMP = 6,
        };

        private static Fighter Person(long id, int cell) => new()
        {
            Id = id, TeamId = 0, CellId = cell, MaxHP = 5000, CurrentHP = 5000, Level = 150,
            MaxAP = 12, CurrentAP = 12, MaxMP = 6, CurrentMP = 6,
        };

        /// <summary>
        /// The shell's rows with no duration of their own -- the 400 returned when hit, its grade 2
        /// cast at the start of his turn -- live as long as the look and the AP and MP it took off
        /// him: through his next turn, not only the round of the cast.
        /// </summary>
        [Fact]
        public void The_shell_waits_on_his_next_turn_as_long_as_its_look()
        {
            var fight = new FightInstance(1, 1);
            var boss = Boss(300);
            var me = Person(10, 250);
            fight.AddMonster(boss); fight.AddPlayer(me);
            const int round = 3;

            var outcomes = EffectEngine.Resolver(fight, boss, Shell, 1, boss, EffectEngine.AlLanzar, round,
                                                 celdaApuntada: boss.CellId);
            Assert.Contains(outcomes, o => o.FilaArmada && o.Sobre == boss && o.Efecto.Triggers == "TB");
            int lookUntil = outcomes.Where(o => o.Buff != null && o.Sobre == boss).Max(o => o.Buff.CaducaEnRonda);
            Assert.Equal(round + 2, lookUntil);

            FightHandler.EngancharLoPendiente(outcomes, Shell, 1, boss.Id, round);

            var hook = Assert.Single(boss.Buffs.ActiveSpells, h => h.Hechizo == Shell && h.Filas != null);
            Assert.True(hook.Vivo(round + 1));      // his next turn, still in the shell
            // Gone with the look: in round + 2, at his own turn start, as that row falls.
            Assert.Equal(lookUntil, hook.CaducaEnRonda);
            Assert.True(hook.FallsAtItsCastersTurn);
            Assert.False(hook.Vivo(round + 3));
        }

        /// <summary>One crow at a time: the content caps him, and nothing else of his side.</summary>
        [Fact]
        public void He_controls_one_crow_at_a_time()
        {
            Assert.True(System.IO.File.Exists(Paths.ContentFile(MonsterSummonCaps.File)));
            MonsterSummonCaps.Reload();
            var fight = new FightInstance(1, 1);
            var boss = Boss(300);
            fight.AddMonster(boss);
            Assert.Equal(1, FightHandler.SummonLimitFor(boss, round: 1));
            Assert.Equal(0, FightHandler.SummonLimitFor(new Fighter { Id = -2, TeamId = 1, IsMonster = true, MonsterId = 823 }, round: 1));

            var crow = new Fighter { Id = -5, TeamId = 1, CellId = 301, IsMonster = true, MonsterId = Crow,
                                     MaxHP = 2100, CurrentHP = 2100, Invocador = boss.Id, SummonCost = 1 };
            fight.AddMonster(crow);
            Assert.Equal(1, FightHandler.UsedSummonCapacity(fight, boss));
        }

        [Fact]
        public void The_caps_file_reads_its_rows()
        {
            var rows = MonsterSummonCaps.Parse("{\"caps\":[{\"monster\":289,\"cap\":1},{\"monster\":5,\"cap\":0},{\"cap\":2}]}");
            Assert.Equal(new[] { (289, 1) }, rows);
        }
    }
}
