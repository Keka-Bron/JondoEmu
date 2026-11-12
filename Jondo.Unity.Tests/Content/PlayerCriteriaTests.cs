using System.IO;
using System.Linq;
using Jondo.Unity.Launcher;
using Jondo.Unity.Server;
using Jondo.Unity.Server.Managers;
using Jondo.Unity.Server.Network;
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

        /// <summary>
        /// Mt and Ms are about the monster that drops: Thor Pestruz (a boss) gives his egg for «Un
        /// huevo para Zurcarák», «Qa=1517&amp;PO!15317&amp;Mt=1&amp;Ms=0»; without a monster they are unknown.
        /// </summary>
        [Fact]
        public void The_dropping_monster_answers_Mt_and_Ms()
        {
            var thor = new PlayerCriteria.Facts(null, null, null, 0, _ => 0, MonsterId: 3618);
            Assert.True(Met("Mt=1&Ms=0", thor));
            Assert.False(Met("Mt=0", thor));
            Assert.False(Met("Ms=1", thor));

            var tofu = new PlayerCriteria.Facts(null, null, null, 0, _ => 0, MonsterId: 98);
            Assert.True(Met("Mt=0&Ms=0", tofu));

            var nobody = new PlayerCriteria.Facts(null, null, null, 0, _ => 0);
            Assert.False(Met("Mt=0", nobody));
            Assert.False(Met("Ms=0", nobody));
        }

        /// <summary>
        /// The young's equipment off the piwis and the like, «(SC!5|ST!5)&amp;PL&gt;0&amp;PL&lt;59»: on a classic
        /// server, not the fifth Temporis, for whoever is below level 59. The server's letters are the
        /// character's in session; without one they stay unknown.
        /// </summary>
        [Fact]
        public void The_youngs_equipment_drops_on_a_classic_server()
        {
            var session = GameSession.SinSocket();
            using (SessionContext.Push(session))
            {
                session.State.CharacterLevel = 20;
                var facts = new PlayerCriteria.Facts(session.State, null, null, 0, _ => 0);
                Assert.True(Met("(SC!5|ST!5)&PL>0&PL<59", facts));
                Assert.False(Met("SC=5", facts));
                Assert.False(Met("ST=5", facts));

                // The date, Paris's: «SG=08&Sd>18&Sd<27» is the end of August.
                var today = OsavoraSeasons.InParis(System.DateTimeOffset.UtcNow);
                Assert.True(Met($"SG={today.Month:00}&Sd={today.Day}", facts));
                Assert.False(Met($"SG!{today.Month}", facts));

                // Still strict: a letter nobody answers keeps the row from dropping.
                Assert.False(Met("SC!5&Sv!946,0", facts));

                session.State.CharacterLevel = 60;
                Assert.False(Met("(SC!5|ST!5)&PL>0&PL<59", facts));
            }

            var nobody = new PlayerCriteria.Facts(null, null, null, 0, _ => 0);
            Assert.False(Met("SC!5", nobody));
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

        /// <summary>«QF&gt;q,0»: the quest finished at least once, as Cuerbax asks of «Proteger y servir».</summary>
        [Fact]
        public void A_quest_finished_once_is_counted()
        {
            if (!File.Exists(Paths.QuestsJson)) return;
            var book = new QuestCatalogue();
            if (book.Step(1237) == null) return;
            var log = new QuestLog(book, () => 200, () => 0);
            var facts = new PlayerCriteria.Facts(null, log, book, 0, _ => 0);
            Assert.False(Met("QF>632,0", facts));
            log.Restore(632, 1237, null, true);
            Assert.True(Met("QF>632,0", facts));
            Assert.True(Met("QF=632", facts));
            Assert.False(Met("QF>632,0&QF>633,0", facts));
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
