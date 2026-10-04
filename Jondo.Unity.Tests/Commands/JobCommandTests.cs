using System;
using Jondo.Unity.Launcher;
using Jondo.Unity.Server.Handlers;
using Jondo.Unity.Server.Network;
using Xunit;

namespace Jondo.Unity.Tests.Commands
{
    /// <summary>".oficio" and ".oficios": putting jobs at a level, one or all of them.</summary>
    public class JobCommandTests
    {
        [Theory]
        [InlineData(".oficio")]
        [InlineData(".oficios")]
        public void A_game_master_may(string command)
            => Assert.Equal(Roles.GameMaster, CommandHandler.RequiredRole(command));

        /// <summary>
        /// Which jobs count: the magi's skills have no recipe and no resource, and they are jobs
        /// all the same -- the real isd lists all six. Pergamago and Bestiólogo, whose skills name
        /// items but have no recipe, are the two it leaves out.
        /// </summary>
        [Fact]
        public void The_magi_are_jobs_and_the_recipeless_are_not()
        {
            var magus = new Jondo.Unity.Server.Managers.SkillDefinition { Id = 113, ParentJobId = 44, IsForgemagus = true };
            var gatherer = new Jondo.Unity.Server.Managers.SkillDefinition { Id = 6, ParentJobId = 2, GatheredResourceItem = 303 };
            var crafter = new Jondo.Unity.Server.Managers.SkillDefinition { Id = 20, ParentJobId = 11 };
            var scroll = new Jondo.Unity.Server.Managers.SkillDefinition { Id = 383, ParentJobId = 75 };
            Func<int, int> recipes = skill => skill == 20 ? 484 : 0;

            Assert.True(ArtisanHandler.IsPractised(44, new[] { magus }, recipes));
            Assert.True(ArtisanHandler.IsPractised(2, new[] { gatherer }, recipes));
            Assert.True(ArtisanHandler.IsPractised(11, new[] { crafter }, recipes));
            Assert.True(ArtisanHandler.IsPractised(WorkshopHandler.BaseJob, Array.Empty<Jondo.Unity.Server.Managers.SkillDefinition>(), recipes));
            Assert.False(ArtisanHandler.IsPractised(75, new[] { scroll }, recipes));
        }

        /// <summary>
        /// All the jobs go in one irq, one f1 each: the entry into the world's, whose first two
        /// are a level-200 woodcutter and a level-41 smith.
        /// </summary>
        [Fact]
        public void Every_job_goes_in_one_irq()
            => Assert.Equal(Convert.FromHexString("0a0d080218c80120b0a51828f496200a10080b10c4860118292090800128ca8501"),
                            ConnectionProtocol.BuildJobsExperience(new[]
                            {
                                (2, 0L, 200, 398000L, 527220L),
                                (11, 17220L, 41, 16400L, 17098L),
                            }));
    }
}
