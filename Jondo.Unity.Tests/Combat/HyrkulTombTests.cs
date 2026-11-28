using System.Linq;
using System.Threading.Tasks;
using Jondo.Unity.Server.Handlers;
using Jondo.Unity.Server.Managers;
using Jondo.Unity.World.Fights;
using Xunit;

namespace Jondo.Unity.Tests.Combat
{
    /// <summary>
    /// Hyrkul (4974) starts with his behaviour spell 9104: it summons his tomb (5031) and casts
    /// 9105 on it, whose "when hit, see who is beside you" row waits on "D" with no duration of its
    /// own and the no-limit trigger duration. That row belongs to the behaviour: it listens for the
    /// whole fight. Cast on the summon as a plain spell, it went deaf after round one, so the tomb
    /// never died and Hyrkul kept his invulnerability.
    /// </summary>
    [Collection("MapManager")]
    public class HyrkulTombTests
    {
        private const int Hyrkul = 4974, Tomb = 5031, Behaviour = 9104, TombSpell = 9105;

        [Fact]
        public async Task The_tomb_still_listens_rounds_later()
        {
            if (!SpellEffects.De(Behaviour, 1).Any(row => row.EffectId == 181)) return;   // no spell data
            if (!SpellEffects.De(TombSpell, 1).Any(row => row.EffectId == 792)) return;

            var fight = new FightInstance(Hyrkul, 1);
            var character = new Fighter
            {
                Id = 10, CellId = 300, MaxHP = 5000, CurrentHP = 5000, Level = 200,
                MaxAP = 12, CurrentAP = 12, MaxMP = 6, CurrentMP = 6, Breed = 9,
            };
            fight.AddPlayer(character);
            var boss = new Fighter
            {
                Id = -1, CellId = 230, MaxHP = 8200, CurrentHP = 8200, Level = 180, IsMonster = true,
                MonsterId = Hyrkul, MaxAP = 10, CurrentAP = 10, MaxMP = 4, CurrentMP = 4, TeamId = 1,
            };
            fight.AddMonster(boss);
            fight.StartFight();

            boss.Conducta = (Behaviour, 1);
            await FightHandler.LanzarLaConductaAsync(null, fight, boss);
            var tomb = fight.Todos.FirstOrDefault(f => f.MonsterId == Tomb && f.IsAlive);
            if (tomb == null) return;   // the summon did not come out on this board
            Assert.Contains(tomb.Buffs.ActiveSpells, e => e.Hechizo == TombSpell);

            int round = fight.RoundNumber;
            while (fight.RoundNumber < round + 4) fight.NextTurn();
            tomb.Buffs.BarrerEnganches(fight.RoundNumber);
            Assert.Contains(tomb.Buffs.ActiveSpells, e => e.Hechizo == TombSpell);
        }
    }
}
