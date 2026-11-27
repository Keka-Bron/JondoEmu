using System.Linq;
using Jondo.Unity.Server.Handlers;
using Jondo.Unity.Server.Network;
using Jondo.Unity.World.Fights;
using Xunit;

namespace Jondo.Unity.Tests.Combat
{
    /// <summary>
    /// A character ally controlled by another player, as a summon is
    /// played -- his turn and his bar go to his player -- but with a character's bar.
    /// </summary>
    public class AllyControlTests
    {
        private const long Master = 53000000024;

        private static Fighter Ally()
        {
            var ally = new Fighter { Id = 7, Breed = 8, Level = 200, MaxMP = 3, IsBot = true };
            ally.SpellIds.AddRange(new[] { 12736, 13434 });
            ally.SpellGrades[12736] = 4;
            return ally;
        }

        [Fact]
        public void A_controlled_ally_uses_its_class_spells()
        {
            var ally = Ally();
            FightHandler.AssignAllyController(ally, Master);

            Assert.True(ally.IsPlayedAlly);
            Assert.True(ally.ControlledBy(Master));
            Assert.False(ally.ControlledBy(Master + 1));
            Assert.False(ally.PlaysOnItsOwn);
            Assert.Equal(new[] { (12736, 4), (13434, 1) }, ally.HechizosDeInvocado);
        }

        /// <summary>
        /// A summon handed to a player of the other side (Dramak's Pantin) keeps the spells it came
        /// out with, the Desenlace its player has to cast on Dramak among them.
        /// </summary>
        [Fact]
        public void A_summon_handed_over_keeps_its_own_bar()
        {
            var pantin = new Fighter { Id = -5, Level = 1, Invocador = -1 };
            pantin.HechizosDeInvocado = new[] { (30669, 1), (3854, 1) };
            FightHandler.AssignAllyController(pantin, Master);

            Assert.True(pantin.ControlledBy(Master));
            Assert.Equal(new[] { (30669, 1), (3854, 1) }, pantin.HechizosDeInvocado);
        }

        /// <summary>
        /// A character's bar -- the weapon first, class spells of origin 1, the fist on slot 0 --
        /// with his player in f4, where a summon's bar carries its owner.
        /// </summary>
        [Fact]
        public void A_controlled_ally_keeps_its_own_spell_bar()
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
