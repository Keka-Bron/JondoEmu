using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using Jondo.Unity.Server.Handlers;
using Jondo.Unity.Server.Managers;
using Jondo.Unity.World.Fights;
using Jondo.Unity.World.Maps;
using Xunit;

namespace Jondo.Unity.Tests.Combat
{
    /// <summary>
    /// An ally who dies in the player's place (content/fights/npc_fights.json "sacrifice"): the
    /// bodyguard of «La bande des Toxines» takes no damage from his own side, and on the blow that
    /// would kill the player he falls instead and the fight is over, won.
    /// </summary>
    public class QuestSacrificeTests
    {
        private const int Guard = 5155;
        private const int Toxin = 5156;

        private static Fighter Monster(long id, int template, int team)
            => new() { Id = id, TeamId = team, CellId = 300, MaxHP = 1000, CurrentHP = 1000, Level = 100,
                       MaxAP = 8, CurrentAP = 8, MaxMP = 3, CurrentMP = 3, IsMonster = true, MonsterId = template };

        private static Fighter Player()
            => new() { Id = 53000000077, TeamId = 0, CellId = 300, MaxHP = 500, CurrentHP = 500, Level = 100,
                       MaxAP = 7, CurrentAP = 7, MaxMP = 3, CurrentMP = 3, Initiative = 1000 };

        private static int Neighbour(int cell)
            => Enumerable.Range(0, 560).First(c => MapGeometry.Distance(cell, c) == 1);

        private static (FightInstance Fight, Fighter Player, Fighter Guard, Fighter Toxin) Ambush()
        {
            var fight = new FightInstance(1, 1);
            var player = Player();
            var guard = Monster(-1, Guard, 0);
            var toxin = Monster(-2, Toxin, 1);
            fight.AddPlayer(player);
            fight.Azul.Add(guard);
            guard.TeamId = 0;
            fight.AddMonster(toxin);
            player.CellId = 300;
            guard.CellId = Neighbour(300);
            toxin.CellId = Enumerable.Range(0, 560).First(c => MapGeometry.Distance(300, c) == 1 && c != guard.CellId);
            fight.StartFight();
            FightHandler.ApplyRules(fight, player, new FightInstance.QuestRules(
                System.Array.Empty<int>(), System.Array.Empty<(int, int)>(), System.Array.Empty<(int, int)>(), 0)
            {
                SacrificeAlly = Guard, SacrificeMessage = 855, SacrificeTakes = 19428, WinMessage = 854,
                AllyProof = new[] { Guard },
            });
            return (fight, player, guard, toxin);
        }

        [Fact]
        public void A_row_reads_the_sacrifice_and_the_ally_proof()
        {
            using var row = JsonDocument.Parse("""
                { "allies": [ { "monster": 5155, "grade": 0 } ], "allyProof": [5155],
                  "sacrifice": { "ally": 5155, "message": 855, "takes": 19428 }, "winMessage": 854 }
                """);
            var rules = NpcFights.RulesOf(row.RootElement);
            Assert.Equal(Guard, rules.SacrificeAlly);
            Assert.Equal(855, rules.SacrificeMessage);
            Assert.Equal(19428, rules.SacrificeTakes);
            Assert.Equal(854, rules.WinMessage);
            Assert.Equal(new[] { Guard }, rules.AllyProof);
            Assert.False(rules.IsEmpty);
        }

        [Fact]
        public void The_rules_mark_the_guard_and_the_fight()
        {
            var (fight, _, guard, toxin) = Ambush();
            Assert.True(guard.ImmuneToAllies);
            Assert.False(toxin.ImmuneToAllies);
            Assert.Equal(Guard, fight.SacrificeAlly);
            Assert.Equal(855, fight.SacrificeMessage);
            Assert.Equal(19428, fight.SacrificeTakes);
            Assert.Equal(854, fight.WinMessage);
        }

        [Fact]
        public async Task The_guard_takes_no_damage_from_his_side()
        {
            var (fight, player, guard, _) = Ambush();
            var blow = SpellEffects.De(6773, 1).First(e => e.EffectId == 97);
            await FightHandler.UnGolpeAsync(null, fight, player, 6773, blow, 4, guard, 95, 0, false);
            Assert.Equal(guard.MaxHP, guard.CurrentHP);
        }

        [Fact]
        public async Task The_guard_dies_for_the_player_and_the_fight_is_won()
        {
            var (fight, player, guard, toxin) = Ambush();
            player.CurrentHP = 1;
            var blow = SpellEffects.De(6773, 1).First(e => e.EffectId == 97);
            await FightHandler.UnGolpeAsync(null, fight, toxin, 6773, blow, 4, player, 95, 0, false);

            Assert.True(fight.Sacrificed);
            Assert.False(guard.IsAlive);
            Assert.Equal(1, player.CurrentHP);
            Assert.Equal(player.TeamId, fight.WonByEffect);
        }

        [Fact]
        public async Task Once_the_guard_is_gone_the_player_falls()
        {
            var (fight, player, guard, toxin) = Ambush();
            guard.CurrentHP = 0;
            player.CurrentHP = 1;
            var blow = SpellEffects.De(6773, 1).First(e => e.EffectId == 97);
            await FightHandler.UnGolpeAsync(null, fight, toxin, 6773, blow, 4, player, 95, 0, false);

            Assert.False(fight.Sacrificed);
            Assert.False(player.IsAlive);
        }
    }
}
