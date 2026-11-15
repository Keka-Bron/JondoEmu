using System.Linq;
using Jondo.Unity.Server.Handlers;
using Jondo.Unity.Server.Network;
using Jondo.Unity.World.Fights;
using Xunit;

namespace Jondo.Unity.Tests.Combat
{
    /// <summary>
    /// The hero mode's probe: a character fighter another one plays by hand, as a summon is
    /// played -- his turn and his bar go to his player -- but with a character's bar.
    /// </summary>
    public class HeroProbeTests
    {
        private const long Master = 53000000024;

        private static Fighter Hero()
        {
            var hero = new Fighter { Id = 7, Breed = 8, Level = 200, MaxMP = 3, IsBot = true };
            hero.SpellIds.AddRange(new[] { 12736, 13434 });
            hero.SpellGrades[12736] = 4;
            return hero;
        }

        [Fact]
        public void A_hero_is_his_players_to_play_with_his_class_spells()
        {
            var hero = Hero();
            FightHandler.PlayAsHero(hero, Master);

            Assert.True(hero.IsPlayedAlly);
            Assert.True(hero.ControlledBy(Master));
            Assert.False(hero.ControlledBy(Master + 1));
            Assert.False(hero.PlaysOnItsOwn);
            Assert.Equal(new[] { (12736, 4), (13434, 1) }, hero.HechizosDeInvocado);
        }

        /// <summary>
        /// A character's bar -- the weapon first, class spells of origin 1, the fist on slot 0 --
        /// with his player in f4, where a summon's bar carries its owner.
        /// </summary>
        [Fact]
        public void A_heros_bar_is_a_characters_with_his_player_as_controller()
        {
            byte[] jyy = FightProtocol.BuildSpellBar(7, new[] { (12736, 4) },
                new[] { (0, FightProtocol.HechizoCuerpoACuerpo), (1, 12736) }, conArma: true, controllerId: Master);
            var fields = ProtoMessage.Parse(jyy).Fields;

            Assert.Equal(7, Assert.Single(fields, f => f.FieldNumber == 3).VarIntValue);
            Assert.Equal(Master, Assert.Single(fields, f => f.FieldNumber == 4).VarIntValue);
            Assert.Equal(2, fields.Count(f => f.FieldNumber == 6));
            Assert.Equal(2, fields.Count(f => f.FieldNumber == 7));
        }

        [Fact]
        public void A_characters_own_bar_still_names_him_twice()
        {
            var fields = ProtoMessage.Parse(FightProtocol.BuildSpellBar(7, new[] { (12736, 4) },
                new[] { (1, 12736) })).Fields;

            Assert.Equal(7, Assert.Single(fields, f => f.FieldNumber == 4).VarIntValue);
        }
    }
}
