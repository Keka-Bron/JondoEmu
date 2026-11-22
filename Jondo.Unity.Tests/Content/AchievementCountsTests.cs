using System.Linq;
using System.Threading.Tasks;
using Jondo.Unity.Server;
using Jondo.Unity.Server.Managers;
using Jondo.Unity.Server.Network;
using Jondo.Unity.World.Achievements;
using Jondo.Unity.World.Quests;
using Xunit;

namespace Jondo.Unity.Tests.Content
{
    /// <summary>
    /// The achievements' criteria the server now answers: mounts born (EB), rides caught (HD), guild
    /// missions by super category (NC), guild tokens (NT), challenges won (EA, Ea), items used (EI) or
    /// broken (Eu), items crafted (EC), subareas explored (ES), the best Koliseo league (lB) and
    /// one's own guild hall (HP) -- each judged, so what is not met is refused rather than unknown.
    /// </summary>
    [Collection("MapManager")]   // the Koliseo's clock and seasons, shared with KoliseoLadderTests
    public class AchievementCountsTests
    {
        private static CriterionVerdict Judge(string criterion) => QuestCriterion.Judge(criterion, new Achievements.CharacterFacts());

        [Fact]
        public void A_challenge_group_counts_as_high_level_only_at_or_above_the_character()
        {
            Assert.True(Achievements.HighLevelChallengeGroup(200, 200));
            Assert.True(Achievements.HighLevelChallengeGroup(201, 200));
            Assert.False(Achievements.HighLevelChallengeGroup(199, 200));
        }

        [Fact]
        public void The_tallies_answer_their_criteria()
        {
            var session = GameSession.SinSocket();
            using (SessionContext.Push(session))
            {
                var tallies = session.State.AchievementTallies;
                tallies[("EB", 0)] = 10;
                tallies[("NC", 1)] = 5;
                tallies[("NT", 0)] = 120;
                tallies[("EA", 0)] = 10;
                tallies[("EI", 12823)] = 1;
                tallies[("HD", 33001)] = 1;
                tallies[("Eu", 0)] = 100;
                tallies[(AchievementCatalogue.CraftKey, 0)] = 100;
                tallies[(AchievementCatalogue.ExploreKey, 320)] = 1;

                Assert.True(Judge("(EB>0,9)").Met);                // «Comadrona»: 10 born
                Assert.False(Judge("(EB>0,99)").Met);
                Assert.True(Judge("(EB>0,99)").FullyJudged);
                Assert.True(Judge("NC>1,4").Met);                  // five dungeon missions
                Assert.False(Judge("NC>2,0").Met);
                Assert.True(Judge("NT>99").Met);                   // gremichas
                Assert.True(Judge("(EA>9)").Met);
                Assert.False(Judge("(Ea>9)").Met);
                Assert.True(Judge("(EI>12823,0)").Met);            // a sweet eaten
                Assert.True(Judge("HD>33001,0").Met);              // an almond dragoturkey caught
                Assert.True(Judge("(Eu>0,99)").Met);
                Assert.True(Judge("(EC>0,99)").Met);
                Assert.True(Judge("(ES>320,0)").Met);
            }
        }

        [Fact]
        public async Task A_Nowel_crafting_achievement_counts_only_scarlet_gifts()
        {
            Achievements.Load();
            var session = GameSession.SinSocket();
            using var context = SessionContext.Push(session);

            const int incarnamBread = 468, scarletGift = 13007;
            const string apprenticeCriterion = "(EC>13007,4)";
            session.State.AchievementTallies[(AchievementCatalogue.CraftKey, 0)] = 100;

            await Achievements.AfterCraftAsync(session, incarnamBread, 5);
            Assert.True(Judge("(EC>0,104)").Met);
            var progress = Judge(apprenticeCriterion);
            Assert.True(progress.FullyJudged);
            Assert.False(progress.Met);

            await Achievements.AfterCraftAsync(session, scarletGift, 4);
            Assert.False(Judge(apprenticeCriterion).Met);
            await Achievements.AfterCraftAsync(session, scarletGift, 1);
            Assert.True(Judge(apprenticeCriterion).Met);
            Assert.False(Judge("(EC>13007,24)").Met);
        }

        /// <summary>
        /// Maps been on (BI), the dream trials won (EJ) and the Infinite Dreams' tallies «en
        /// intensidad X o superior», kept per difficulty: 600 fragments in a Paradoja II are 600
        /// «Paradoja I o superior» and none «Pesadilla I o superior».
        /// </summary>
        [Fact]
        public void Maps_trials_and_dreams_answer_their_criteria()
        {
            var session = GameSession.SinSocket();
            using (SessionContext.Push(session))
            {
                var tallies = session.State.AchievementTallies;
                tallies[("BI", 85983744)] = 1;
                tallies[("EJ", 9016)] = 1;
                tallies[("ET", 5)] = 600;
                tallies[("ET", 2)] = 500;
                tallies[("Et", 9)] = 1000;

                Assert.True(Judge("(BI=85983744)").Met);           // «A lo largo y a lo ancho»
                Assert.False(Judge("(BI=85985792)").Met);
                Assert.True(Judge("(BI=85985792)").FullyJudged);
                Assert.True(Judge("EJ>9016,0").Met);               // «Prueba FONSOCAC»
                Assert.False(Judge("EJ>9017,0").Met);
                Assert.True(Judge("ET>1,999").Met);                // 1100 at Sueño I or harder
                Assert.False(Judge("ET>4,999").Met);               // 600 at Paradoja I or harder
                Assert.True(Judge("ET>4,499").Met);
                Assert.False(Judge("ET>8,0").Met);
                Assert.True(Judge("Et>8,999").Met);                // «Pesadilla monstruosa»
                Assert.True(Judge("EZ>1,499").FullyJudged);
            }
        }

        /// <summary>
        /// The character as the quests' start conditions ask about him: his class (PG), a job's
        /// level (PJ), and an alignment this server does not have (Ps, Pa: neutral).
        /// </summary>
        [Fact]
        public void Class_jobs_and_alignment_are_judged()
        {
            var session = GameSession.SinSocket();
            session.State.Breed = 13;
            session.State.Jobs[26] = new JobExperience.Progress { Experience = JobExperience.Floor(80) };
            using (SessionContext.Push(session))
            {
                Assert.True(Judge("PG=13").Met);                   // «The Tymador Job»
                Assert.False(Judge("PG=14").Met);
                Assert.True(Judge("PJ>26,79").Met);                // the alchemist past 79
                Assert.False(Judge("Pj>26,99").Met);
                Assert.False(Judge("Ps=1&Pa=0").Met);              // nobody is Bontarian
                Assert.True(Judge("Ps=1&Pa=0").FullyJudged);
                Assert.True(Judge("Ps=0").Met);
                Assert.True(Judge("BT=1").Met);                    // the client's AlwaysTrueCriterion
                session.State.Sex = 1;
                session.State.Kamas = 5000;
                session.State.Emotes.Add(8);
                Assert.True(Judge("PS=1&PK>999").Met);
                Assert.False(Judge("PE!8").Met);                   // the emote is known already
                Assert.True(Judge("PE!9&OV>2879").Met);
            }
        }

        [Fact]
        public void Where_one_stands_and_one_s_league_are_judged_too()
        {
            var session = GameSession.SinSocket();
            session.State.CharacterId = 9_300_901;
            session.State.MapId = 191105026;
            using (SessionContext.Push(session))
            {
                var hall = Judge("HP=1,1");
                Assert.True(hall.FullyJudged);
                Assert.False(hall.Met);                            // no guild, no hall

                int seasons = KoliseoLadder.LastSeasonNumber();
                try
                {
                    var league = Judge("lB=6|lB=7");
                    Assert.True(league.FullyJudged);
                    Assert.False(league.Met);                      // never placed
                }
                finally
                {
                    KoliseoLadder.EraseSeasonsAfter(seasons);
                }
            }
        }

    }
}
