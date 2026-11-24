using System.Collections.Generic;
using System.Linq;
using Jondo.Unity.Server.Network;
using Jondo.Unity.World.Fights;
using Xunit;

namespace Jondo.Unity.Tests.Combat
{
    /// <summary>
    /// Kickroach's 5284 is one draw over three outcomes on the aimed cell: a teleport, an
    /// invisibility or one of five summons. A cast that drew a summon left the newborn on the aimed
    /// cell, and the harness judged the undrawn teleport row "blocked" by it, as if the cast had
    /// resolved both. The engine draws once (EffectEngine.Sortear); the verdict was the harness's.
    /// </summary>
    public sealed class KickroachDrawVerdictTests
    {
        private const int Spell = 5284, Kickroach = 3945, Summon = 3940, Aimed = 330;

        private static string TeleportVerdict(bool summonedByThisCast)
        {
            var fight = new FightInstance(1, 1);
            var player = new Fighter { Id = 10, TeamId = 0, Level = 40, CellId = 200, MaxHP = 2000, CurrentHP = 2000 };
            var boss = new Fighter { Id = -1, TeamId = 1, IsMonster = true, MonsterId = Kickroach, Level = 40, CellId = 300,
                                     MaxHP = 3000, CurrentHP = 3000 };
            var summon = new Fighter { Id = -6, TeamId = 1, IsMonster = true, MonsterId = Summon, Level = 40, CellId = Aimed,
                                       MaxHP = 100, CurrentHP = 100 };
            fight.AddPlayer(player);
            fight.AddMonster(boss);
            if (!summonedByThisCast) fight.AddMonster(summon);
            var before = fight.Todos.ToDictionary(f => f.Id, f => (f.CurrentHP, f.CurrentAP, f.CurrentMP, f.CellId, 1, f.IsAlive));
            var statesBefore = fight.Todos.ToDictionary(f => f.Id, f => new HashSet<int>(f.Buffs.Estados));
            if (summonedByThisCast) fight.AddMonster(summon);

            var verdicts = ControlApi.VerifyEffects(fight, boss, Spell, 1, 1, before, statesBefore,
                                                    new List<(Fighter Who, Buff Row)>(), new List<Glifo>(),
                                                    f => f == boss ? "boss" : f == player ? "player" : "ally",
                                                    castCell: Aimed);
            return verdicts.Select(v => (Effect: (int)v.GetType().GetProperty("effect")!.GetValue(v)!,
                                         Verdict: (string)v.GetType().GetProperty("verdict")!.GetValue(v)!))
                           .First(v => v.Effect == Jondo.Unity.World.Combat.EffectSupport.Teleport).Verdict;
        }

        [Fact]
        public void An_undrawn_teleport_is_not_blocked_by_the_summon_the_same_cast_drew()
        {
            Assert.Equal("chance", TeleportVerdict(summonedByThisCast: true));
        }

        [Fact]
        public void A_teleport_aimed_at_a_cell_taken_before_the_cast_is_still_blocked()
        {
            Assert.Equal("blocked", TeleportVerdict(summonedByThisCast: false));
        }
    }
}
