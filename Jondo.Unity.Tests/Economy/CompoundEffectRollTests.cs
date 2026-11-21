using System;
using System.Linq;
using Jondo.Unity.Server;
using Jondo.Unity.Server.Managers;
using Xunit;

namespace Jondo.Unity.Tests.Economy
{
    /// <summary>
    /// A rolled item keeps its compound effects as its template has them. The 1175 of a dofus, a
    /// trophy or a weapon is its spell and that spell's grade, and the template reader sorted the
    /// two as if they were a range: an item rolled from it (a drop, a craft, an admin grant with
    /// random stats) carried spell 1 at a grade the size of a spell id, and the client's tooltip
    /// broke on it.
    /// </summary>
    [Collection("forgemagic")]
    public sealed class CompoundEffectRollTests
    {
        [Theory]
        [InlineData(694)]     // Crimson Dofus
        [InlineData(32117)]   // a weapon with its spell
        [InlineData(20358)]   // a shield with its spell
        public void A_rolled_item_keeps_its_spell_and_grade_in_their_order(int gid)
        {
            var raw = DatabaseManager.GetItemEffectsData(DatabaseManager.GetItemTemplatePossibleEffects(gid))
                .FirstOrDefault(e => e.EffectId == Alterations.FightSpell);
            if (raw == null || Forgemagic.TemplateOf(gid) is not { } template) return;   // no data

            foreach (var effects in new[] { Forgemagic.Roll(template, new Random(1)), Forgemagic.Perfect(template) })
            {
                var spell = Assert.Single(effects, e => e.Effect == Alterations.FightSpell);
                Assert.Equal(raw.DiceNum, spell.DiceNum);
                Assert.Equal(raw.DiceSide, spell.DiceSide);
                Assert.Equal(raw.Value, spell.Value);
            }
        }
    }
}
