using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Jondo.Unity.Server;
using Jondo.Unity.Server.Handlers;
using Jondo.Unity.Server.Managers;
using Jondo.Unity.World.Fights;
using Xunit;

namespace Jondo.Unity.Tests.Combat
{
    /// <summary>
    /// Solar (5100), the Forgelava boss. His behaviour casts Aurora (9436) at the fight's start, and
    /// each phase spell hooks the next one three turns later: Aurora (state 575), Zenith (576),
    /// Twilight (577) and Nadir (578), then Aurora again. Nadir is his only vulnerable phase.
    /// </summary>
    [Collection("MapManager")]
    public class SolarCycleTests
    {
        private const int Solar = 5100;
        private static readonly int[] Phases = { 575, 576, 577, 578 };

        [Fact]
        public async Task The_four_phases_follow_each_other_and_loop()
        {
            (int Spell, int Grade) behaviour = (0, 0);
            for (int grade = 0; grade <= 5 && behaviour.Spell == 0; grade++)
                behaviour = SpellCriteria.SpellOfLevel(DatabaseManager.GetMonsterGradeStats(Solar, grade)?.StartingSpellLevelId ?? 0);
            if (behaviour.Spell == 0) return;

            var player = new Fighter
            {
                Id = 9_970_510, Name = "P", TeamId = 0, CellId = 400, Level = 200, MaxHP = 50000, CurrentHP = 50000,
                MaxAP = 12, CurrentAP = 12, MaxMP = 6, CurrentMP = 6, Breed = 8,
            };
            var boss = new Fighter
            {
                Id = -1, Name = "Solar", IsMonster = true, TeamId = 1, CellId = 200, Level = 200, MaxHP = 19000,
                CurrentHP = 19000, MaxAP = 12, CurrentAP = 12, MaxMP = 0, CurrentMP = 0, MonsterId = Solar,
            };
            var fight = new FightInstance(1, 990_005_100);
            fight.AddPlayer(player); fight.AddMonster(boss);
            fight.StartFight();
            boss.Conducta = behaviour;
            await FightHandler.LanzarLaConductaAsync(null, fight, boss);

            var seen = new List<int>();
            var trace = new List<string>();
            for (int turn = 0; turn < 22; turn++)
            {
                while (fight.CurrentFighter != boss) fight.NextTurn();
                await FightHandler.EngancheAsync(null, fight, boss, EffectEngine.AlEmpezarElTurno);
                int phase = Phases.FirstOrDefault(p => boss.Buffs.Estados.Contains(p));
                if (seen.Count == 0 || seen[^1] != phase) seen.Add(phase);
                trace.Add($"r{fight.RoundNumber} st={phase} hooks=" + string.Join(";", boss.Buffs.ActiveSpells.Select(e => $"{e.Hechizo}-{e.Grado}@{e.PuestoEnRonda}..{e.CaducaEnRonda}")));
                await FightHandler.EngancheAsync(null, fight, boss, EffectEngine.AlAcabarElTurno);
                fight.NextTurn();
            }
            Assert.True(seen.Take(6).SequenceEqual(new[] { 575, 576, 577, 578, 575, 576 }),
                        $"phases seen: {string.Join(",", seen)} | {string.Join(" | ", trace)}");
        }
    }
}
