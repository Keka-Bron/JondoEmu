using System.Threading.Tasks;
using Jondo.Unity.Server.Handlers;
using Jondo.Unity.Server.Managers;
using Jondo.Unity.World.Fights;
using Jondo.Unity.World.Maps;
using Xunit;

namespace Jondo.Unity.Tests.Combat
{
    /// <summary>
    /// The Kutulu Larva's behaviour (6749): its first row, a cast of 6751 that would summon a
    /// nightmare, is a client-only row and the server leaves it (the boss summons nothing at the
    /// start). What it does run is Vacillement de l'Univers (6752 under TB, delay 1): from its
    /// second turn every character it sees is brought next to it at the start of its turn.
    /// </summary>
    [Collection("MapManager")]
    public class KutuluLarvaBehaviourTests
    {
        private const int Behaviour = 6749;
        private const int BossRoom = 239614490;

        [Fact]
        public async Task From_its_second_turn_the_larva_brings_the_player_next_to_it()
        {
            if (SpellEffects.De(Behaviour, 1).Count == 0) return;   // no spell data
            var fight = new FightInstance(1, BossRoom);
            var player = new Fighter
            {
                Id = 9_970_601, Name = "P", CellId = 383, Level = 200, MaxHP = 5000, CurrentHP = 5000,
                MaxAP = 12, CurrentAP = 12, MaxMP = 6, CurrentMP = 6, Breed = 8,
            };
            var boss = new Fighter
            {
                Id = -1, Name = "L", IsMonster = true, CellId = 299, Level = 220, MaxHP = 26000,
                CurrentHP = 26000, MaxAP = 12, CurrentAP = 12, MaxMP = 4, CurrentMP = 4, MonsterId = 4453,
            };
            fight.AddPlayer(player);
            fight.AddMonster(boss);
            player.CellId = 383; boss.CellId = 299;
            fight.StartFight();
            boss.Conducta = (Behaviour, 1);
            await FightHandler.LanzarLaConductaAsync(null, fight, boss);

            var cells = new int[2];
            for (int turn = 0; turn < 2; turn++)
            {
                player.CellId = 383;
                while (fight.CurrentFighter != boss) fight.NextTurn();
                await FightHandler.EngancheAsync(null, fight, boss, EffectEngine.AlEmpezarElTurno);
                cells[turn] = player.CellId;
                await FightHandler.EngancheAsync(null, fight, boss, EffectEngine.AlAcabarElTurno);
                fight.NextTurn();
            }

            Assert.Equal(383, cells[0]);                                   // its first turn: nothing
            Assert.Equal(1, MapGeometry.Distance(boss.CellId, cells[1]));  // its second: next to it
            Assert.DoesNotContain(fight.Todos, f => f.MonsterId == 4528);  // no nightmare summoned
        }
    }
}
