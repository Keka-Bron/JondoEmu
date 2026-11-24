using System.Linq;
using Jondo.Unity.Launcher;
using Jondo.Unity.Server;
using Jondo.Unity.Server.Managers;
using Jondo.Unity.World.Fights;
using Xunit;

namespace Jondo.Unity.Tests.Combat
{
    /// <summary>
    /// The Waddicts' Foundry (dungeon 113): the Twagón that waits for Chocomatose, and the
    /// Wadulador's boost split on its ally's life.
    /// </summary>
    public class WaddictsFoundryTests
    {
        private const int Twagon = 12216;
        private const int Chocomatose = 952;

        private static (FightInstance Fight, Fighter Monster, Fighter Player) Fight()
        {
            var fight = new FightInstance(1, 1);
            var player = new Fighter { Id = 10, Level = 52, CellId = 200, MaxHP = 1000, CurrentHP = 1000 };
            var monster = new Fighter { Id = -1, IsMonster = true, MonsterId = 5691, Level = 52, CellId = 300,
                                        MaxHP = 600, CurrentHP = 600 };
            fight.AddPlayer(player);
            fight.AddMonster(monster);
            return (fight, monster, player);
        }

        [Fact]
        public void Twagon_waits_for_an_enemy_in_Chocomatose()
        {
            Assert.True(System.IO.File.Exists(Paths.ContentFile(MonsterSpellGates.File)));
            MonsterSpellGates.Reload();
            Assert.Equal(Chocomatose, MonsterSpellGates.StateFor(Twagon));

            var (fight, monster, player) = Fight();
            Assert.False(MonsterSpellGates.Open(fight, monster, Twagon));
            Assert.True(MonsterSpellGates.Open(fight, monster, 12156));

            // Chocomatose on one of his own does not open it.
            var ally = new Fighter { Id = -2, IsMonster = true, MonsterId = 5692, Level = 52, CellId = 301, MaxHP = 600, CurrentHP = 600 };
            fight.AddMonster(ally);
            ally.Buffs.PonerEstado(Chocomatose);
            Assert.False(MonsterSpellGates.Open(fight, monster, Twagon));

            player.Buffs.PonerEstado(Chocomatose);
            Assert.True(MonsterSpellGates.Open(fight, monster, Twagon));
        }

        [Fact]
        public void The_gates_file_reads_its_rows()
        {
            var rows = MonsterSpellGates.Parse("{\"gates\":[{\"spell\":12216,\"enemyState\":952},{\"spell\":5},{\"enemyState\":3}]}");
            Assert.Equal(new[] { (12216, 952) }, rows);
        }

        [Theory]
        [InlineData(240, 1076)] // under half his life: the a,V50 row
        [InlineData(600, 1171)] // not under: the a,v50 row
        public void The_Wadulador_boost_follows_its_allys_life(int life, int expected)
        {
            var (fight, monster, _) = Fight();
            var ally = new Fighter { Id = -2, IsMonster = true, MonsterId = 5652, Level = 52, CellId = 301,
                                     MaxHP = 600, CurrentHP = life };
            fight.AddMonster(ally);

            EffectEngine.Resolver(fight, monster, 12161, 1, ally, EffectEngine.AlLanzar, 0, celdaApuntada: ally.CellId);

            var effects = ally.Buffs.Puestos.Where(b => b.HechizoOrigen == 12161).Select(b => b.EffectId).ToList();
            Assert.Equal(new[] { expected }, effects);
        }
    }
}
