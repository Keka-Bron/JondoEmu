using System.IO;
using System.Linq;
using Jondo.Unity.Launcher;
using Jondo.Unity.Server;
using Jondo.Unity.Server.Managers;
using Jondo.Unity.World.Content;
using Jondo.Unity.World.Quests;
using Xunit;

namespace Jondo.Unity.Tests.Content
{
    /// <summary>
    /// The monsters' quest drops: their criteria judged strictly for whoever would receive them.
    /// </summary>
    public class PlayerCriteriaTests
    {
        private static bool Met(string criterion, PlayerCriteria.Facts facts)
            => Criterion.Met(criterion, condition => PlayerCriteria.Resolve(condition, facts));

        /// <summary>«PO!x» none, «PO=x» one at least, «PO&lt;x,n» fewer than n.</summary>
        [Fact]
        public void What_is_held_is_counted()
        {
            int held = 0;
            var facts = new PlayerCriteria.Facts(null, null, null, 0, _ => held);
            Assert.True(Met("PO!11683", facts));
            Assert.False(Met("PO=11683", facts));
            Assert.True(Met("PO<2269,10", facts));
            held = 10;
            Assert.False(Met("PO!11683", facts));
            Assert.True(Met("PO=11683", facts));
            Assert.False(Met("PO<2269,10", facts));
            Assert.True(Met("PO>2269,9", facts));
        }

        /// <summary>The hunter's meat: job 41 above 9, a hunting weapon in hand.</summary>
        [Fact]
        public void Meat_falls_to_hunters_with_their_weapon()
        {
            int level = 10;
            bool weapon = true;
            var facts = new PlayerCriteria.Facts(null, null, null, 0, _ => 0, job => job == 41 ? level : 0, () => weapon);
            Assert.True(Met("Pj>41,9&CU>0", facts));
            Assert.False(Met("Pj>41,19&CU>0", facts));
            weapon = false;
            Assert.False(Met("Pj>41,9&CU>0", facts));
            Assert.True(Met("PJ=41", facts));
            Assert.False(Met("PJ=28", facts));
        }

        /// <summary>The server's own letters are not known here, and an unknown row never drops.</summary>
        [Fact]
        public void What_is_not_known_does_not_drop()
        {
            var facts = new PlayerCriteria.Facts(null, null, null, 0, _ => 0);
            Assert.False(Met("Ms=0&Mt=1&Sc=947", facts));
            Assert.False(Met("Pj>41,9&CU>0", facts));   // nobody's jobs to ask
            Assert.False(Met("PL>9", facts));   // no character to ask
            Assert.True(Met("Pm=98567687|PO!1", facts));
        }

        /// <summary>
        /// Quest 632's step 1237: «Qs=1237&amp;Qo&gt;4096&amp;Qo&lt;4098&amp;PO!11683», the step under way with
        /// objective 4097 still to do and the item not yet held.
        /// </summary>
        [Fact]
        public void A_quest_item_drops_while_its_objective_is_pending()
        {
            if (!File.Exists(Paths.QuestsJson)) return;
            var book = new QuestCatalogue();
            if (book.Step(1237) == null) return;
            var log = new QuestLog(book, () => 200, () => 0);
            var run = log.Restore(632, 1237, null, false);

            int held = 0;
            var facts = new PlayerCriteria.Facts(null, log, book, 0, _ => held);
            const string item = "Qs=1237&Qo>4096&Qo<4098&PO!11683";
            // «Qo>4096» is 4096 done and «Qo<4098» is 4098 not done: before 4096 nothing drops.
            Assert.False(Met(item, facts));
            foreach (var objective in book.Step(1237)!.Objectives.Where(o => o.Id <= 4096)) run.Done.Add(objective.Id);
            Assert.True(Met(item, facts));
            Assert.True(Met("Qa=632&PO<2269,10", facts));
            Assert.False(Met("Qs=1238", facts));
            Assert.False(Met("Qa=633", facts));

            held = 1;
            Assert.False(Met(item, facts));

            // Every objective of the step done: nothing pending, nothing drops.
            held = 0;
            foreach (var objective in book.Step(1237)!.Objectives) run.Done.Add(objective.Id);
            Assert.False(Met(item, facts));
        }

        /// <summary>Gein's own table: his follower, to whoever is on his quest's step.</summary>
        [Fact]
        public void A_monsters_quest_drop_keeps_its_criterion()
        {
            if (!File.Exists(Paths.WorldDb)) return;
            var drop = DatabaseManager.GetMonsterDrops(3761, 0).SingleOrDefault(d => d.ObjectId == 15585);
            if (drop == null) return;
            Assert.StartsWith("Qs=2174", drop.ReceiverCriterion);
        }
    }
}
