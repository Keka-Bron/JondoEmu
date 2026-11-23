using System.Linq;
using System.Threading.Tasks;
using Jondo.Unity.Server.Handlers;
using Jondo.Unity.Server.Managers;
using Jondo.Unity.World.Fights;
using Xunit;

namespace Jondo.Unity.Tests.Combat
{
    /// <summary>
    /// The Giant Kralamoure (423) stands behind 900% resistances. His Kraken (1103) waits
    /// on a blow of each element (DE DF DW DA) and puts the state (35-38) that lets him summon the
    /// matching tentacle, the one way to bring those resistances down. A blow the resistances
    /// swallowed whole set nothing off, so no tentacle ever came and he could not be hurt.
    /// </summary>
    [Collection("MapManager")]
    public class KralamarKrakenTests
    {
        private const int Kralamar = 423, Kraken = 1103;

        [Fact]
        public async Task A_blow_his_resistances_swallow_still_marks_its_element()
        {
            if (!SpellEffects.De(Kraken, 1).Any(row => row.EffectId == 950)) return;   // no spell data
            var player = new Fighter
            {
                Id = 9_970_423, Name = "P", CellId = 250, Level = 200, MaxHP = 5000, CurrentHP = 5000,
                MaxAP = 12, CurrentAP = 12, MaxMP = 6, CurrentMP = 6, Breed = 8,
            };
            var boss = new Fighter
            {
                Id = -1, Name = "K", IsMonster = true, Level = 180, MaxHP = 8000, CurrentHP = 8000, TeamId = 1,
                MaxAP = 10, CurrentAP = 10, MaxMP = 4, CurrentMP = 4, MonsterId = Kralamar,
            };
            var fight = new FightInstance(Kralamar, 990_000_423);
            fight.AddPlayer(player);
            fight.AddMonster(boss);
            player.CellId = 250; boss.CellId = 251;
            fight.StartFight();

            // His template's resistances, 900% in every element.
            boss.NeutralResPct = boss.EarthResPct = boss.FireResPct = boss.WaterResPct = boss.AirResPct = 900;
            await FightHandler.AplicarEfectosAsync(null, fight, boss, Kraken, 1, boss,
                                                   EffectEngine.AlLanzar, boss.CellId);
            int[] elementStates = { 35, 36, 37, 38 };
            Assert.DoesNotContain(elementStates, s => boss.Buffs.TieneEstado(s));

            int life = boss.CurrentHP;
            var blow = SpellEffects.De(6773, 1).First(e => e.EffectId == 97);
            await FightHandler.UnGolpeAsync(null, fight, player, 6773, blow, 4, boss, 95, 0, false);
            Assert.Equal(life, boss.CurrentHP);
            Assert.Contains(elementStates, s => boss.Buffs.TieneEstado(s));
        }
    }
}
