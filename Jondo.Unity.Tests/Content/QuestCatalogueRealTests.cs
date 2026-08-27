using System;
using System.Collections.Generic;
using System.IO;
using Jondo.Unity.Launcher;
using Jondo.Unity.World.Quests;
using Xunit;

namespace Jondo.Unity.Tests.Content
{
    /// <summary>
    /// The catalogue and the criterion reader, against the real 1,976 quests.
    /// </summary>
    /// <remarks>
    /// Everything else about the criterion reader is tested on strings written here, which proves
    /// the grammar is implemented but not that the grammar is <em>right</em>. Only the real file
    /// can say that, and it says it about all of it at once: 1,976 conditions, 29 operators,
    /// brackets three deep.
    ///
    /// These skip when <c>datos/quests_3.6.10.10.json</c> is not on the machine, which is a real
    /// weakness — a test that skips proves nothing — and they are here anyway because the failure
    /// they guard against is exactly the kind that every constructed test passes: a condition shape
    /// nobody thought to write down. The build that matters runs where the file is.
    /// </remarks>
    public class QuestCatalogueRealTests
    {
        private static bool Available => File.Exists(Paths.QuestsJson);

        /// <summary>A character who has done nothing, which is enough to exercise the reader.</summary>
        private sealed class Nobody : IQuestFacts
        {
            public int Level => 1;
            public long MapId => 0;
            public bool Finished(int questId) => false;
            public bool Active(int questId) => false;
            public bool ObjectiveDone(int objectiveId) => false;
        }

        [Fact]
        public void Every_criterion_in_the_catalogue_parses()
        {
            if (!Available) return;   // a checkout without datos/ is a normal thing

            var catalogue = new QuestCatalogue();
            Assert.True(catalogue.Ready);

            var broken = new List<string>();
            foreach (var quest in catalogue.All())
            {
                if (quest.Criterion.Length == 0) continue;

                var verdict = QuestCriterion.Judge(quest.Criterion, new Nobody());
                if (verdict.Broke) broken.Add($"quest {quest.Id}: {quest.Criterion}");
            }

            Assert.True(broken.Count == 0,
                $"{broken.Count} conditions would not parse, first few: " +
                string.Join(" | ", broken.GetRange(0, Math.Min(5, broken.Count))));
        }

        [Fact]
        public void The_catalogue_holds_what_it_is_supposed_to()
        {
            if (!Available) return;   // a checkout without datos/ is a normal thing

            var catalogue = new QuestCatalogue();

            // Not exact numbers: those change the day somebody re-runs the extractor against a new
            // client, and a test that has to be edited on every patch gets edited without being
            // read. Floors, so a collapse is caught and a patch is not.
            Assert.True(catalogue.QuestCount > 1_900, $"only {catalogue.QuestCount} quests");
            Assert.True(catalogue.StepCount > 2_100, $"only {catalogue.StepCount} steps");
            Assert.True(catalogue.ObjectiveCount > 15_000, $"only {catalogue.ObjectiveCount} objectives");

            // The join the whole quest section exists for. If this collapses, quests and NPC
            // dialogue have come apart and nothing else here would notice.
            Assert.True(catalogue.SpokenSteps > 1_200, $"only {catalogue.SpokenSteps} spoken steps");
        }

        [Fact]
        public void The_quest_from_the_capture_reads_back_exactly()
        {
            if (!Available) return;   // a checkout without datos/ is a normal thing

            // Measured, not chosen: this is the quest in
            // "Misiones\hablar con NPC y aceptar una mision". The client opened a dialogue on map
            // 212863492, the server walked it to line 50071, and then pushed ief {2432}. Every one
            // of those numbers has to still come back out of the catalogue, because the engine is
            // going to reproduce that exchange.
            var quest = new QuestCatalogue().Of(2432);

            Assert.NotNull(quest);
            Assert.Single(quest!.Givers);
            Assert.Equal(6617, quest.Givers[0].NpcId);
            Assert.Equal(212863492L, quest.Givers[0].MapId);
            Assert.Equal("PL>89", quest.Criterion);

            var step = Assert.Single(quest.Steps);
            Assert.Equal(3089, step.Id);
            Assert.Equal(50071, step.DialogId);
            Assert.Equal(14, step.Objectives.Count);
        }

        [Fact]
        public void A_level_90_character_can_be_offered_that_quest_and_a_level_89_cannot()
        {
            if (!Available) return;   // a checkout without datos/ is a normal thing

            var quest = new QuestCatalogue().Of(2432)!;

            Assert.False(QuestCriterion.Judge(quest.Criterion, new AtLevel(89)).Met);
            Assert.True(QuestCriterion.Judge(quest.Criterion, new AtLevel(90)).Met);
        }

        [Fact]
        public void The_chain_out_of_Astrub_is_read_the_right_way_round()
        {
            if (!Available) return;   // a checkout without datos/ is a normal thing

            // 56 needs 55, 57 needs 56, and so on. An off-by-one here would draw the whole
            // questline backwards and it would look perfectly sensible.
            var catalogue = new QuestCatalogue();

            Assert.Contains(55, catalogue.Of(56)!.Requires);
            Assert.Contains(56, catalogue.Of(57)!.Requires);
            Assert.Contains(57, catalogue.Of(58)!.Requires);
            Assert.DoesNotContain(57, catalogue.Of(56)!.Requires);
        }

        private sealed class AtLevel : IQuestFacts
        {
            private readonly int _level;
            public AtLevel(int level) => _level = level;

            public int Level => _level;
            public long MapId => 0;
            public bool Finished(int questId) => false;
            public bool Active(int questId) => false;
            public bool ObjectiveDone(int objectiveId) => false;
        }
    }
}
