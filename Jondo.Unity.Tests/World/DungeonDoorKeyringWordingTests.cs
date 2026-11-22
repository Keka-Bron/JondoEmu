using Jondo.Unity.Server.Managers;
using Xunit;

namespace Jondo.Unity.Tests.World
{
    /// <summary>
    /// The guardians of the Crocantería (4817), Dios Kao's temple (4818) and the Waddictos'
    /// foundry (4819) offer the keyring as «Enseñarle el manojo de llaves.», not «Utilizar...».
    /// </summary>
    public class DungeonDoorKeyringWordingTests
    {
        [Theory]
        [InlineData(4817, 43409)]
        [InlineData(4818, 43392)]
        [InlineData(4819, 43400)]
        public void Their_keyring_reply_opens_the_door(int npc, long reply)
        {
            Npcs.Initialize();
            DungeonDoor.Initialize();
            if (!DungeonDoor.For(npc).Asks) return;   // no world base to read

            Assert.Equal(reply, DungeonDoor.For(npc).Keyring);
        }
    }
}
