using Jondo.Unity.Protocol.Game.Wire;
using Xunit;

namespace Jondo.Unity.Protocol.Game.Tests;

public sealed class ShortcutStorageTests
{
    [Fact]
    public void Stored_shortcuts_are_still_decoded_from_the_historical_schema()
    {
        // August Shortcut.slot_id (tag 2), shortcut_spell (tag 6), spell_id (tag 2).
        var shortcut = ShortcutStorageCodec.Decode(Convert.FromHexString("10023202102A"));
        Assert.Equal(new Shortcut(2, new ShortcutCaseShortcutSpell(new(42))), shortcut);
    }
}
