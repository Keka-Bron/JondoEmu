using System.Collections.Generic;
using Jondo.Unity.World.Fights;
using Jondo.Unity.World.Maps;
using Xunit;

namespace Jondo.Unity.Tests.Combat
{
    public class MonsterCastRulesTests
    {
        private const int SpellId = 42;
        private const int Origin = 288;
        private const int AlignedTarget = 317;
        private const int NonAlignedTarget = 289;

        [Fact]
        public void A_per_target_limit_blocks_only_the_target_that_spent_it()
        {
            var monster = FighterAt(Origin, id: -1, team: 1);

            SpellCastRules.Register(monster, SpellId, targetId: 10, minCastInterval: 0);

            Assert.Equal(
                SpellCastRules.Rejection.PerTarget,
                Check(monster, targetId: 10, maxPerTarget: 1));
            Assert.Equal(
                SpellCastRules.Rejection.None,
                Check(monster, targetId: 11, maxPerTarget: 1));
        }

        [Fact]
        public void A_minimum_interval_counts_down_at_the_end_of_the_owners_turn()
        {
            var monster = FighterAt(Origin, id: -1, team: 1);
            SpellCastRules.Register(monster, SpellId, targetId: 10, minCastInterval: 4);

            Assert.Equal(SpellCastRules.Rejection.Cooldown, Check(monster, 10, interval: 4));

            SpellCastRules.EndTurn(monster);
            Assert.Equal(3, monster.Recarga[SpellId]);
            SpellCastRules.EndTurn(monster);
            SpellCastRules.EndTurn(monster);
            SpellCastRules.EndTurn(monster);

            Assert.Equal(0, monster.Recarga[SpellId]);
            Assert.Equal(SpellCastRules.Rejection.None, Check(monster, 10, interval: 4));
        }

        [Fact]
        public void Cast_in_line_uses_the_isometric_axes_not_raw_rows_and_columns()
        {
            Assert.True(MapGeometry.AreAligned(Origin, AlignedTarget));
            Assert.False(MapGeometry.AreAligned(Origin, NonAlignedTarget));

            var monster = FighterAt(Origin, id: -1, team: 1);
            Assert.Equal(
                SpellCastRules.Rejection.None,
                Check(monster, 10, castInLine: true, targetCell: AlignedTarget));
            Assert.Equal(
                SpellCastRules.Rejection.NotInLine,
                Check(monster, 10, castInLine: true, targetCell: NonAlignedTarget));
        }

        [Fact]
        public void Monster_ai_does_not_spend_ap_on_a_non_aligned_cast()
        {
            var monster = FighterAt(Origin, id: -1, team: 1);
            var target = FighterAt(NonAlignedTarget, id: 10, team: 0);
            monster.SpellIds.Add(SpellId);

            var result = MonsterAI.ExecuteTurn(
                monster,
                new List<Fighter> { monster, target },
                spellDataFetcher: _ => Spell(castInLine: true));

            Assert.Equal(0, result.SpellId);
            Assert.Equal(monster.MaxAP, monster.CurrentAP);
        }

        [Fact]
        public void Monster_ai_records_the_target_and_cooldown_of_an_accepted_cast()
        {
            var monster = FighterAt(Origin, id: -1, team: 1);
            var target = FighterAt(AlignedTarget, id: 10, team: 0);
            monster.SpellIds.Add(SpellId);

            var result = MonsterAI.ExecuteTurn(
                monster,
                new List<Fighter> { monster, target },
                spellDataFetcher: _ => Spell(castInLine: true, interval: 3));

            Assert.Equal(SpellId, result.SpellId);
            Assert.Equal(1, result.CastCount);
            Assert.Equal(1, monster.LanzadosEsteTurno[SpellId]);
            Assert.Equal(1, monster.LanzadosPorObjetivo[(SpellId, target.Id)]);
            Assert.Equal(3, monster.Recarga[SpellId]);
        }

        private static SpellCastRules.Rejection Check(
            Fighter caster,
            long targetId,
            int maxPerTarget = 0,
            int interval = 0,
            bool castInLine = false,
            int targetCell = AlignedTarget)
            => SpellCastRules.Check(
                caster, SpellId, targetId, caster.CellId, targetCell,
                maxCastPerTurn: 0, maxPerTarget, interval, castInLine);

        private static MonsterAI.AISpellData Spell(bool castInLine, int interval = 0)
            => new MonsterAI.AISpellData
            {
                SpellId = SpellId,
                APCost = 2,
                MinRange = 1,
                MaxRange = 3,
                BaseDamageMin = 5,
                BaseDamageMax = 5,
                NeedsLineOfSight = false,
                CastInLine = castInLine,
                MaxCastPerTurn = 3,
                MaxCastPerTarget = 1,
                MinCastInterval = interval
            };

        private static Fighter FighterAt(int cell, long id, int team)
            => new Fighter
            {
                Id = id,
                Name = id < 0 ? "Monster" : "Player",
                TeamId = team,
                CellId = cell,
                IsMonster = team == 1,
                MaxHP = 100,
                CurrentHP = 100,
                MaxAP = 6,
                CurrentAP = 6,
                MaxMP = 0,
                CurrentMP = 0
            };
    }
}
