using Jondo.Unity.Server.Handlers;
using Jondo.Unity.World.Fights;
using Xunit;

namespace Jondo.Unity.Tests.Combat
{
    /// <summary>
    /// A spell's global cooldown holds for the caster's whole side, monsters too: Kabaal's
    /// Menace Creciente (19791, SpellLevels GlobalCooldown 3) is one rune for the room, then
    /// none from any monster for three turns. Players and monsters do not share it.
    /// </summary>
    public sealed class MonsterGlobalCooldownTests
    {
        private const int MenaceCreciente = 19791;

        private static Fighter Monster(int id, int cell)
            => new Fighter { Id = id, TeamId = 1, CellId = cell, MaxHP = 1000, CurrentHP = 1000, Level = 200, IsMonster = true };

        [Fact]
        public void A_monster_s_global_cooldown_waits_for_the_monsters_of_its_side()
        {
            var fight = new FightInstance(1, 1);
            var player = new Fighter { Id = 10, TeamId = 0, CellId = 300, MaxHP = 1000, CurrentHP = 1000, Level = 200, Breed = 1 };
            var caster = Monster(-1, 310);
            var ally = Monster(-2, 311);
            var waitingLonger = Monster(-3, 312);
            waitingLonger.Recarga[MenaceCreciente] = 5;
            fight.AddPlayer(player);
            fight.AddOpponent(caster);
            fight.AddOpponent(ally);
            fight.AddOpponent(waitingLonger);

            var changed = FightHandler.ApplyGlobalCooldown(fight, caster, MenaceCreciente, 3, 3);

            Assert.Equal(3, ally.Recarga[MenaceCreciente]);
            Assert.Equal(5, waitingLonger.Recarga[MenaceCreciente]);
            Assert.False(caster.Recarga.ContainsKey(MenaceCreciente));   // its own interval is set apart
            Assert.False(player.Recarga.ContainsKey(MenaceCreciente));
            Assert.Equal(new[] { ally }, changed);
        }

        [Fact]
        public void A_player_s_global_cooldown_leaves_the_monsters_on_his_side_alone()
        {
            var fight = new FightInstance(1, 1);
            var player = new Fighter { Id = 10, TeamId = 0, CellId = 300, MaxHP = 1000, CurrentHP = 1000, Level = 200, Breed = 1 };
            var friend = new Fighter { Id = 11, TeamId = 0, CellId = 301, MaxHP = 1000, CurrentHP = 1000, Level = 200, Breed = 1 };
            var summon = new Fighter { Id = -5, TeamId = 0, CellId = 302, MaxHP = 100, CurrentHP = 100, Level = 1, IsMonster = true };
            fight.AddPlayer(player);
            fight.AddPlayer(friend);
            fight.AddPlayer(summon);

            FightHandler.ApplyGlobalCooldown(fight, player, MenaceCreciente, 3, 3);

            Assert.Equal(3, friend.Recarga[MenaceCreciente]);
            Assert.False(summon.Recarga.ContainsKey(MenaceCreciente));
        }
    }
}
