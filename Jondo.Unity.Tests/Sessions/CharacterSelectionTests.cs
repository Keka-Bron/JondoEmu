using Jondo.Unity.Protocol;
using Jondo.Unity.Server.Handlers;
using Jondo.Unity.Server.Network;
using Xunit;

namespace Jondo.Unity.Tests.Sessions
{
    public class CharacterSelectionTests
    {
        [Theory]
        [InlineData("kvw")]
        [InlineData("ksl")]
        public void Normal_selection_reads_the_id_from_field_one(string opcode)
        {
            byte[] frame = ConnectionProtocol.Push(opcode, Pb.New().Var(1, 424242).Build());

            Assert.Equal(424242, CharacterSelectionHandler.ReadSelectedCharacterId(frame));
        }

        [Fact]
        public void Selection_after_creation_reads_field_two_instead_of_the_success_flag()
        {
            byte[] frame = ConnectionProtocol.Push(Op.Kvl,
                Pb.New().Var(1, 1).Var(2, 987654321).Build());

            Assert.Equal(987654321, CharacterSelectionHandler.ReadSelectedCharacterId(frame));
        }

        [Fact]
        public void Missing_character_id_is_rejected()
        {
            byte[] frame = ConnectionProtocol.Push(Op.Kvl, Pb.New().Var(1, 1).Build());

            Assert.Equal(0, CharacterSelectionHandler.ReadSelectedCharacterId(frame));
        }
    }
}
