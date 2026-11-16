using System.Linq;
using System.Text.Json;
using Jondo.Unity.Server.Handlers;
using Jondo.Unity.Server.Managers;
using Jondo.Unity.World.Fights;
using Xunit;

namespace Jondo.Unity.Tests.Combat
{
    /// <summary>
    /// A quest's fight rules (content/fights/npc_fights.json): «En la encrucijada de los mundos»
    /// lends the Yokai Nana and is won with the five yokais asleep at once; Master Kuatinbe fights
    /// with his Pandasiman from the start.
    /// </summary>
    public class QuestFightRulesTests
    {
        private static Fighter Monster(long id, int template)
            => new() { Id = id, TeamId = 1, CellId = 300 + (int)-id, MaxHP = 1000, CurrentHP = 1000, Level = 100,
                       MaxAP = 8, CurrentAP = 8, MaxMP = 3, CurrentMP = 3, IsMonster = true, MonsterId = template };

        private static Fighter Player()
            => new() { Id = 1, TeamId = 0, CellId = 200, MaxHP = 3000, CurrentHP = 3000, Level = 200,
                       MaxAP = 7, CurrentAP = 7, MaxMP = 3, CurrentMP = 3, Initiative = 1000 };

        /// <summary>The four fields of a row, and nothing for a row without them.</summary>
        [Fact]
        public void A_row_reads_its_rules()
        {
            using var row = JsonDocument.Parse("""
                { "grants": [16932], "passives": [ { "monster": 6250, "spell": 17066 } ],
                  "states": [ { "monster": 6237, "state": 56 } ], "winState": 1923 }
                """);
            var rules = NpcFights.RulesOf(row.RootElement);
            Assert.Equal(new[] { 16932 }, rules.Grants);
            Assert.Equal((6250, 17066), Assert.Single(rules.Passives));
            Assert.Equal((6237, 56), Assert.Single(rules.States));
            Assert.Equal(1923, rules.WinState);

            using var plain = JsonDocument.Parse("""{ "monsters": [ { "monster": 6237, "grade": 0 } ] }""");
            Assert.Same(FightInstance.QuestRules.None, NpcFights.RulesOf(plain.RootElement));
        }

        /// <summary>
        /// The spell lent goes on the player's bar, the passive and the state on the monsters of
        /// that template only.
        /// </summary>
        [Fact]
        public void The_rules_go_on_the_fight_built()
        {
            var fight = new FightInstance(1, 1);
            var player = Player();
            var master = Monster(-1, 6250);
            var yokai = Monster(-2, 6237);
            fight.AddPlayer(player); fight.AddOpponent(master); fight.AddOpponent(yokai);

            FightHandler.ApplyRules(fight, player, new FightInstance.QuestRules(
                new[] { 16932 }, new[] { (6250, 17066) }, new[] { (6237, 56) }, 1923));

            Assert.Contains((16932, 1), player.GrantedSpells);
            Assert.Contains(17066, master.Buffs.Actitudes);
            Assert.DoesNotContain(17066, yokai.Buffs.Actitudes);
            Assert.True(yokai.Buffs.TieneEstado(56));
            Assert.False(master.Buffs.TieneEstado(56));
            Assert.Equal(1923, fight.WinState);
        }

        /// <summary>Won only when every living enemy holds the state at the same moment.</summary>
        [Fact]
        public void The_fight_is_won_with_every_enemy_asleep()
        {
            var fight = new FightInstance(1, 1);
            var yokais = Enumerable.Range(1, 5).Select(i => Monster(-i, 6237 + i % 3)).ToList();
            fight.AddPlayer(Player());
            foreach (var yokai in yokais) fight.AddOpponent(yokai);

            Assert.False(fight.EnemiesAllHoldTheWinState());
            fight.WinState = 1923;
            foreach (var yokai in yokais.Take(4)) yokai.Buffs.PonerEstado(1923);
            Assert.False(fight.EnemiesAllHoldTheWinState());

            yokais[4].Buffs.PonerEstado(1923);
            Assert.True(fight.EnemiesAllHoldTheWinState());

            yokais[0].Buffs.QuitarEstado(1923);
            Assert.False(fight.EnemiesAllHoldTheWinState());
        }
    }
}
