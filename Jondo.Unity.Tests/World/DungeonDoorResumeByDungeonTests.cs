using Jondo.Unity.Server.Managers;
using Xunit;

namespace Jondo.Unity.Tests.World
{
    /// <summary>
    /// Lorkos (783) keeps the door of the Minotot Room and of the Minotoror's Labyrinth, and has a
    /// resume reply for each. At the Minotot Room he has to offer the one that names it, not the
    /// lowest of his, which resumes the Labyrinth.
    /// </summary>
    public class DungeonDoorResumeByDungeonTests
    {
        private const int Lorkos = 783;
        private const long ResumeLabyrinth = 8925, ResumeMinototRoom = 8927;

        [Fact]
        public void The_reply_that_names_the_dungeon_wins()
        {
            var replies = new[] { (ResumeLabyrinth, "Retomar el Centro del Laberinto del Minotauroro donde lo dejaste."),
                                  (ResumeMinototRoom, "Retomar la Sala del Minotot donde la dejaste.") };
            Assert.Equal(ResumeMinototRoom, DungeonDoor.ResumeNaming(replies, "Sala del Minotot", ResumeLabyrinth));
            Assert.Equal(ResumeLabyrinth, DungeonDoor.ResumeNaming(replies, "Otra mazmorra", ResumeLabyrinth));
        }

        [Fact]
        public void Lorkos_resumes_the_Minotot_Room_with_its_own_reply()
        {
            Npcs.Initialize();
            DungeonDoor.Initialize();
            if (DungeonDoor.For(Lorkos).Resume == 0) return;   // no world base to read

            Assert.Equal(ResumeMinototRoom, DungeonDoor.ResumeFor(Lorkos, "Sala del Minotot"));
            Assert.Equal(DungeonDoor.For(Lorkos).Resume, DungeonDoor.ResumeFor(Lorkos, null));
        }
    }
}
