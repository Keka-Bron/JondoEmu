using System.Linq;
using Jondo.Unity.Server.Managers;
using Jondo.Unity.Server.Network;
using Xunit;

namespace Jondo.Unity.Tests.World
{
    /// <summary>
    /// A monster group's look on the map: the whole of it, colours and scale included, as the real
    /// server sends it -- not the bones alone, which drew every monster at 100.
    /// </summary>
    public class MonsterLookTests
    {
        private static Jondo.Unity.Protocol.Game.EntityLook GroupLook(string look)
        {
            var group = new MobSpawnManager.MobGroup
            {
                MobId = -1, CellId = 300,
                Members = { new MobSpawnManager.MobMember { Monster = new MobSpawnManager.MonsterData { Id = 666, Look = look }, Level = 50 } },
            };
            return MapActorMessages.MonsterGroup(group, false).ActorInformation!.Look;
        }

        /// <summary>
        /// The golden wild Dragopavo (666) as "hablar por canal Comunidad (española)" has it: bones
        /// 706, scale 115, and its four colours, each with its index in the high byte.
        /// </summary>
        [Fact]
        public void A_group_carries_its_colours_and_scale_as_the_capture_does()
        {
            var look = GroupLook("{706||1=#FFD700,2=#FFD700,3=#43624C,4=7758915|115}");
            Assert.Equal(706, look.Bones);
            Assert.Equal(new[] { 115 }, look.Scales);
            Assert.Equal(new[] { 54747724, 50321152, 33543936, 74867779 }.OrderBy(c => c), look.Colors.OrderBy(c => c));
        }

        /// <summary>The Conde Kontatrás, "{2069|||150}": half again as big, as a person is, not a third smaller.</summary>
        [Fact]
        public void The_Conde_Kontatras_is_drawn_at_his_scale()
        {
            var look = GroupLook("{2069|||150}");
            Assert.Equal(2069, look.Bones);
            Assert.Equal(new[] { 150 }, look.Scales);
            Assert.Empty(look.Colors);
        }
    }
}
