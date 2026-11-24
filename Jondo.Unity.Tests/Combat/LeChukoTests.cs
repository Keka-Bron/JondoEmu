using System.Linq;
using System.Threading.Tasks;
using Jondo.Unity.Server;
using Jondo.Unity.Server.Handlers;
using Jondo.Unity.Server.Managers;
using Jondo.Unity.World.Fights;
using Jondo.Unity.World.Maps;
using Xunit;

namespace Jondo.Unity.Tests.Combat
{
    /// <summary>
    /// Le Chuko (230), the boss of the Navio de Le Chuko: his Golpe de Sable Maldito (6104)
    /// hits 41-45 neutral on whoever he faces, and his passive 6103 casts 6145 at the start of
    /// every turn, which steals 26-30 water life from his foe above 30% life.
    /// </summary>
    [Collection("MapManager")]
    public class LeChukoTests
    {
        private const int Chuko = 230, Sand = 6104, Passive = 6145, Cursed = 307;
        private const long Osabana = 234618880;

        private static bool HasData => SpellEffects.De(Sand, 1).Count > 0 && SpellEffects.De(Passive, 2).Count > 0;

        private static (FightInstance Fight, Fighter Boss, Fighter Player) Fight()
        {
            if (!MapManager.WalkableCells.ContainsKey(Osabana)) MapManager.Initialize();
            MobSpawnManager.EnsureMonsterData();
            var fight = new FightInstance(9941, Osabana, Osabana);
            var player = new Fighter { Id = 7411, TeamId = 0, CellId = 300, MaxHP = 5000, CurrentHP = 5000, Level = 200, Initiative = 1 };
            fight.AddPlayer(player);
            int next = MapGeometry.GetNeighbors(300).First();
            var boss = new Fighter { Id = -1, TeamId = 1, CellId = next, MaxHP = 4500, CurrentHP = 4500, IsMonster = true,
                                     MonsterId = Chuko, Level = 90, Initiative = 100 };
            fight.AddMonster(boss);
            fight.StartFight();
            return (fight, boss, player);
        }

        /// <summary>A monster's own spell cast as its turn casts it: the rows in the order they are written.</summary>
        private static Task Cast(FightInstance fight, Fighter caster, Fighter target, int spell, int grade)
            => FightHandler.LanzarPorOrdenAsync(null, fight, caster, spell, grade, target, target.CellId,
                                                EffectEngine.AlLanzar, EffectEngine.EfectosSorteados(spell, grade, false));

        [Fact]
        public async Task The_cursed_sand_blow_takes_life_and_curses()
        {
            if (!HasData) return;
            var (fight, boss, player) = Fight();
            await Cast(fight, boss, player, Sand, 1);
            Assert.True(player.Buffs.TieneEstado(Cursed));
            // 41 to 45 neutral, with nothing on the victim but its life.
            Assert.InRange(player.MaxHP - player.CurrentHP, 41, 45);
        }

        /// <summary>
        /// A shield on the victim soaks the whole blow: the life stays, and the curse and the
        /// power it takes (rows after the blow) still land. This is what a player
        /// with a shield up showed as "the blow took no life".
        /// </summary>
        [Fact]
        public async Task A_shield_soaks_the_blow_and_the_curse_still_lands()
        {
            if (!HasData) return;
            var (fight, boss, player) = Fight();
            player.Escudar(500, 99);
            await Cast(fight, boss, player, Sand, 1);
            Assert.Equal(player.MaxHP, player.CurrentHP);
            Assert.InRange(500 - player.PuntosDeEscudo, 41, 45);
            Assert.True(player.Buffs.TieneEstado(Cursed));
        }

        /// <summary>The 26 to 30 water steal of the passive's grade 2, on a foe above 30% life.</summary>
        [Fact]
        public async Task The_passive_steals_water_life_from_a_foe_above_a_third()
        {
            if (!HasData) return;
            var (fight, boss, player) = Fight();
            boss.CurrentHP = 3000;
            await Cast(fight, boss, player, Passive, 2);
            int stolen = player.MaxHP - player.CurrentHP;
            Assert.InRange(stolen, 26, 30);
            // A steal heals half of what it takes.
            Assert.Equal(3000 + stolen / 2, boss.CurrentHP);
            // Above 30% life none of the summons of the lower branch come.
            Assert.Single(fight.Todos, f => f.IsMonster);
        }

        /// <summary>Below 50% life the sand also calls its sub-spell; at full life it does not.</summary>
        [Fact]
        public async Task The_sand_calls_nobody_at_full_life()
        {
            if (!HasData) return;
            var (fight, boss, player) = Fight();
            await Cast(fight, boss, player, Sand, 1);
            Assert.Single(fight.Todos, f => f.IsMonster);
        }
    }
}
