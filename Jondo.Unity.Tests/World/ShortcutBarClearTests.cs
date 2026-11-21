using System;
using System.Linq;
using System.Threading.Tasks;
using Jondo.Unity.Server.Handlers;
using Jondo.Unity.Server.Managers;
using Jondo.Unity.Server.Network;
using Jondo.Unity.Tests.Economy;
using Xunit;

namespace Jondo.Unity.Tests.World
{
    /// <summary>The bars' «vaciar» (iuz): every slot emptied with its ivr, and an emptied spell bar stays empty.</summary>
    public sealed class ShortcutBarClearTests
    {
        [Fact]
        public async Task Both_bars_are_emptied_slot_by_slot()
        {
            const long character = 8_900_100_101;
            ItemShortcuts.Forget(character);
            try
            {
                await using var pipe = await ClientPipe.OpenAsync(8_900_000_101, character, 990_100_101, "Vaciar-Uno");
                using (SessionContext.Push(pipe.Session))
                {
                    // The item bar: two slots, emptied with ivr { 2 slot } each.
                    ItemShortcuts.Put(character, 3, Pb.New().Var(2, 3).Msg(9, Pb.New().Var(2, 289).Var(3, 1)).Build());
                    ItemShortcuts.Put(character, 7, Pb.New().Var(2, 7).Msg(9, Pb.New().Var(2, 289).Var(3, 2)).Build());
                    await ItemBarHandler.ClearAsync(pipe.ToClient, ConnectionProtocol.Push("iuz", Array.Empty<byte>()));
                    Assert.Equal(new byte[] { 0x10, 0x03 }, await pipe.Next("ivr"));
                    Assert.Equal(new byte[] { 0x10, 0x07 }, await pipe.Next("ivr"));
                    Assert.Empty(ItemShortcuts.Of(character));

                    // The spell bar: ivr { 1 1, 2 slot }, and nothing left saved.
                    SpellChoices.PutInBar(1, 12736);
                    SpellChoices.PutInBar(4, 12738);
                    await ItemBarHandler.ClearAsync(pipe.ToClient, ConnectionProtocol.Push("iuz", Pb.New().Var(1, 1).Build()));
                    Assert.Equal(ConnectionProtocol.BuildShortcutRemoved(1, 1), await pipe.Next("ivr"));
                    Assert.Equal(ConnectionProtocol.BuildShortcutRemoved(4, 1), await pipe.Next("ivr"));
                    Assert.Empty(SpellChoices.Bar);
                }
            }
            finally
            {
                ItemShortcuts.Forget(character);
            }
        }

        [Fact]
        public void An_emptied_bar_is_not_filled_again_and_a_new_one_is()
        {
            var known = new[] { new SpellTable.KnownSpell(12736, 12736, 1), new SpellTable.KnownSpell(12738, 12738, 1) };
            var empty = new System.Collections.Generic.Dictionary<int, int>();

            // Never written: every known spell, and the fist.
            Assert.Equal(3, FightSpellLayout.Build(known, empty).Bar.Count);
            // Emptied by the player: nothing, not even the fist, which is saved and taken off like a spell.
            var emptied = FightSpellLayout.Build(known, empty, seed: false);
            Assert.Empty(emptied.Bar);
        }
    }
}
