using System.Linq;
using System.Threading.Tasks;
using Jondo.Unity.Server.Handlers;
using Jondo.Unity.Server.Managers;
using Jondo.Unity.World.Fights;
using Xunit;

namespace Jondo.Unity.Tests.Combat
{
    /// <summary>
    /// Merkator's Mer Cure (4009), his behaviour: every monster of his side carries a 792 under
    /// DM that casts 4007, pushing the allies around it. Hitting one of them next to him in melee
    /// is what moves him, and moving him is what lifts his invulnerability.
    /// </summary>
    [Collection("MapManager")]
    public class MerkatorMerCureTests
    {
        private const int MerCure = 4009;

        [Fact]
        public async Task A_melee_blow_on_his_ally_next_to_him_pushes_him()
        {
            if (!SpellEffects.De(MerCure, 1).Any(row => row.EffectId == 792)) return;   // no spell data
            var player = new Fighter
            {
                Id = 9_970_601, Name = "P", CellId = 300, Level = 200, MaxHP = 5000, CurrentHP = 5000,
                MaxAP = 12, CurrentAP = 12, MaxMP = 6, CurrentMP = 6, Breed = 8,
            };
            var merkator = new Fighter
            {
                Id = -1, Name = "Merkator", IsMonster = true, Level = 200, MaxHP = 19000, CurrentHP = 19000,
                MaxAP = 12, CurrentAP = 12, MaxMP = 4, CurrentMP = 4, MonsterId = 3534,
            };
            var ally = new Fighter
            {
                Id = -2, Name = "Ally", IsMonster = true, Level = 200, MaxHP = 4000, CurrentHP = 4000,
                MaxAP = 10, CurrentAP = 10, MaxMP = 4, CurrentMP = 4, MonsterId = 3535,
            };
            var fight = new FightInstance(3534, 990_003_534);
            fight.AddPlayer(player);
            fight.AddMonster(merkator);
            fight.AddMonster(ally);
            player.CellId = 328; ally.CellId = 314; merkator.CellId = 300;   // a line, one cell apart
            fight.StartFight();
            merkator.Conducta = (MerCure, 1);
            await FightHandler.LanzarLaConductaAsync(null, fight, merkator);

            fight.TriggeringAttacker = player;
            await FightHandler.DispararAsync(null, fight, ally, EffectEngine.CuandoMePeganDeCerca);
            fight.TriggeringAttacker = null;
            Assert.NotEqual(300, merkator.CellId);
        }
    }
}
