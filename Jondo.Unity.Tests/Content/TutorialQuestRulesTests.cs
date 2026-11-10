using System.IO;
using System.Linq;
using Jondo.Unity.Launcher;
using Jondo.Unity.World.Maps;
using Jondo.Unity.World.Quests;
using Xunit;

namespace Jondo.Unity.Tests.Content
{
    /// <summary>
    /// The engine rules the guided tutorial needed, against the real catalogue.
    /// </summary>
    public class TutorialQuestRulesTests
    {
        private static bool Available => File.Exists(Paths.QuestsJson);

        /// <summary>
        /// An objective of a step already left behind is done: 1629's start condition is
        /// «Qo>19250|Qf=2502», and the tutorial capture hands 1629 over while 2502 is still under
        /// way, two steps past 19250. The run's own list is emptied on every step, so read alone it
        /// said «not done» the moment 19250 closed its step.
        /// </summary>
        [Fact]
        public void An_objective_of_a_step_left_behind_counts_as_done()
        {
            if (!Available) return;
            var book = new QuestCatalogue();
            var log = new QuestLog(book, () => 2, () => 0);

            Assert.NotNull(log.Start(2502));
            Assert.False(log.ObjectiveDone(19250));
            Assert.False(log.CanStart(1629, out _));

            // Through 3184, the step whose one objective is 19250.
            while (log.Run(2502)!.StepId != 3185) log.FinishStep(2502);

            Assert.True(log.ObjectiveDone(19250));
            Assert.True(log.ObjectiveDone(19163));
            Assert.False(log.ObjectiveDone(19251));
            Assert.True(log.CanStart(1629, out _));
        }

        /// <summary>
        /// 1629 and 1630 still name the old celestial temple's rooms; the catalogue reads the maps
        /// 3.6 rebuilt them on (content/maps/moved.json), so «hablar con el maestro Curro» is done
        /// where he stands.
        /// </summary>
        [Fact]
        public void The_old_temple_rooms_read_as_the_new_ones()
        {
            if (!Available || !File.Exists(Paths.ContentFile(MovedMaps.AuthoredFile))) return;
            MovedMaps.Forget();
            Assert.Equal(241440769, MovedMaps.Now(153093380));
            Assert.Equal(191105026, MovedMaps.Now(191105026));

            var book = new QuestCatalogue();
            var curro = book.Step(2249)!.Objectives.Single(o => o.Id == 9656);
            Assert.Equal(241440769, curro.MapId);
        }
    }
}
