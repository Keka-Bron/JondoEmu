using System.IO;
using Jondo.Unity.Launcher;
using Jondo.Unity.World.Quests;
using Xunit;

namespace Jondo.Unity.Tests.Content
{
    /// <summary>
    /// A character's quest log: starting, ticking objectives off, and the cascade.
    /// </summary>
    /// <remarks>
    /// The cascade is the part worth testing hard. An objective finishes a step, a step finishes a
    /// quest, and each of those has a packet the client sits waiting for — so a quest that advances
    /// one notch too far or one notch too few leaves the client showing a step the server does not
    /// think it is on, and nothing anywhere reports it.
    ///
    /// These run against the real catalogue when it is on the machine, because the shapes that
    /// matter — a quest with one step, a quest with many, a step with fourteen objectives — are
    /// already in it and inventing them would only test the inventions.
    /// </remarks>
    public class QuestLogTests
    {
        private static bool Available => File.Exists(Paths.QuestsJson);

        private static QuestLog LogAt(int level, out QuestCatalogue book)
        {
            book = new QuestCatalogue();
            var at = level;
            return new QuestLog(book, () => at, () => 0);
        }

        [Fact]
        public void A_quest_whose_level_is_too_low_cannot_be_started()
        {
            if (!Available) return;

            // Quest 2432 wants PL>89.
            var log = LogAt(50, out _);
            Assert.False(log.CanStart(2432, out _));
            Assert.Null(log.Start(2432));
        }

        [Fact]
        public void Starting_puts_it_on_the_first_step()
        {
            if (!Available) return;

            var log = LogAt(90, out var book);
            var run = log.Start(2432);

            Assert.NotNull(run);
            Assert.Equal(book.Of(2432)!.Steps[0].Id, run!.StepId);
            Assert.False(run.Finished);
            Assert.True(log.Active(2432));
            Assert.False(log.Finished(2432));
        }

        [Fact]
        public void The_same_quest_cannot_be_started_twice()
        {
            if (!Available) return;

            var log = LogAt(90, out _);
            Assert.NotNull(log.Start(2432));
            Assert.Null(log.Start(2432));
        }

        [Fact]
        public void An_objective_from_another_step_does_nothing()
        {
            if (!Available) return;

            // Not an error to report, but it must not move the quest: it means a handler is
            // confused or the client is out of step, and taking its word would advance a quest for
            // the wrong reason.
            var log = LogAt(90, out _);
            log.Start(2432);

            Assert.True(log.Tick(2432, 999_999).Nothing);
        }

        [Fact]
        public void Ticking_the_same_objective_twice_only_counts_once()
        {
            if (!Available) return;

            var log = LogAt(90, out var book);
            var run = log.Start(2432)!;
            int first = book.Step(run.StepId)!.Objectives[0].Id;

            Assert.True(log.Tick(2432, first).ObjectiveTicked);
            Assert.True(log.Tick(2432, first).Nothing);
        }

        [Fact]
        public void The_step_only_finishes_when_every_objective_is_done()
        {
            if (!Available) return;

            var log = LogAt(90, out var book);
            var run = log.Start(2432)!;
            var step = book.Step(run.StepId)!;

            // Quest 2432's only step has fourteen objectives. Thirteen of them must not finish it.
            Assert.Equal(14, step.Objectives.Count);

            for (int i = 0; i < step.Objectives.Count - 1; i++)
            {
                var move = log.Tick(2432, step.Objectives[i].Id);
                Assert.True(move.ObjectiveTicked);
                Assert.False(move.StepFinished);
            }

            var last = log.Tick(2432, step.Objectives[^1].Id);
            Assert.True(last.StepFinished);
        }

        [Fact]
        public void The_last_step_finishing_finishes_the_quest()
        {
            if (!Available) return;

            var log = LogAt(90, out _);
            log.Start(2432);

            var move = log.FinishStep(2432);

            Assert.True(move.StepFinished);
            Assert.True(move.QuestFinished);
            Assert.Equal(0, move.NextStep);
            Assert.True(log.Finished(2432));
            Assert.False(log.Active(2432));
        }

        [Fact]
        public void A_quest_with_several_steps_walks_through_them_in_order()
        {
            if (!Available) return;

            var book = new QuestCatalogue();

            // The tutorial quest from the capture: the server pushed step 3226, then 3228, then
            // 3227 — declaration order, which is not numeric order, which is exactly why the
            // catalogue keeps the quest's own list rather than sorting by id.
            var quest = book.Of(2545);
            Assert.NotNull(quest);
            Assert.True(quest!.Steps.Count > 1);

            int at = 0;
            var log = new QuestLog(book, () => 200, () => 0);
            var run = log.Start(2545);
            Assert.NotNull(run);

            while (!run!.Finished && at < 50)
            {
                Assert.Equal(quest.Steps[at].Id, run.StepId);
                log.FinishStep(2545);
                at++;
            }

            Assert.Equal(quest.Steps.Count, at);
            Assert.True(log.Finished(2545));
        }

        [Fact]
        public void Moving_on_clears_the_objectives_of_the_step_left_behind()
        {
            if (!Available) return;

            var book = new QuestCatalogue();
            var log = new QuestLog(book, () => 200, () => 0);
            var run = log.Start(2545)!;

            int firstStep = run.StepId;
            log.FinishStep(2545);

            if (!run.Finished)
            {
                Assert.NotEqual(firstStep, run.StepId);
                Assert.Empty(run.Done);
            }
        }

        [Fact]
        public void A_restored_quest_comes_back_wherever_it_was_left()
        {
            if (!Available) return;

            // Loading has to be unconditional. A character whose quest's condition no longer holds
            // — a patch changed it, or they were reset — still has that quest, and dropping it on
            // login would lose their progress with no message anywhere.
            var book = new QuestCatalogue();
            var log = new QuestLog(book, () => 1, () => 0);

            var run = log.Restore(2432, 3089, "18390,18391", finished: false);

            Assert.True(log.Active(2432));
            Assert.Equal(3089, run.StepId);
            Assert.Equal(2, run.Done.Count);
            Assert.True(log.ObjectiveDone(18390));
        }

        [Fact]
        public void A_finished_quest_is_a_prerequisite_for_the_next_one()
        {
            if (!Available) return;

            // The whole point of the chain: 56 needs 55.
            var book = new QuestCatalogue();
            var log = new QuestLog(book, () => 60, () => 0);

            Assert.False(log.CanStart(56, out _));

            log.Restore(55, 0, "", finished: true);
            Assert.True(log.CanStart(56, out var verdict));

            // 56 is Ps=1&Pa=1&PL>29&Qf=55: two alignment terms this engine does not model, which
            // it must say it let through rather than pretend it checked.
            Assert.False(verdict.FullyJudged);
            Assert.False(verdict.Broke);
        }

        // ─── The column the database holds ────────────────────────────────────────

        [Fact]
        public void Packing_and_unpacking_a_run_round_trips()
        {
            var run = new QuestRun(1, 2);
            Assert.Equal("", QuestLog.Pack(run));

            run.Done.Add(1);
            run.Done.Add(2);
            Assert.Equal("1,2", QuestLog.Pack(run));

            run.Counted[7] = 3;
            Assert.Equal("1,2|7:3", QuestLog.Pack(run));

            var back = new QuestRun(1, 2);
            QuestLog.Unpack("1,2|7:3", back);
            Assert.Equal(new[] { 1, 2 }, back.Done);
            Assert.Equal(3, back.Counted[7]);
        }

        [Fact]
        public void Rubbish_in_that_column_is_dropped_rather_than_thrown()
        {
            // It is a text column in a database people poke at by hand, and it is read on login.
            // Throwing here would take somebody's character out of the game over a stray comma.
            var run = new QuestRun(1, 2);
            QuestLog.Unpack("1,,x,3|bad,7:z,8:4,9:", run);

            Assert.Equal(new[] { 1, 3 }, run.Done);
            Assert.Equal(4, run.Counted[8]);
            Assert.Single(run.Counted);

            var empty = new QuestRun(1, 2);
            QuestLog.Unpack(null, empty);
            QuestLog.Unpack("", empty);
            Assert.Empty(empty.Done);
        }

        // ─── Objectives that want several of something ────────────────────────────

        [Fact]
        public void A_count_objective_is_not_finished_by_one()
        {
            if (!Available) return;

            // Type 6 is "beat #2 x #1 in one fight" and parameter1 is how many: 788 objectives use
            // it and the count runs from 1 to 8. Ticking such an objective once must not finish it,
            // or every "kill five" in the game becomes a "kill one".
            var book = new QuestCatalogue();

            QuestObjective? counted = null;
            foreach (var quest in book.All())
            {
                foreach (var step in quest.Steps)
                {
                    foreach (var objective in step.Objectives)
                    {
                        if (objective.Needed > 1 && step.Objectives.Count == 1) counted = objective;
                        if (counted != null) break;
                    }
                    if (counted != null) break;
                }
                if (counted != null) break;
            }

            Assert.NotNull(counted);

            var step2 = book.Step(counted!.StepId)!;
            var log = new QuestLog(book, () => 200, () => 0);
            var run = log.Restore(step2.QuestId, step2.Id, "", finished: false);

            for (int i = 1; i < counted.Needed; i++)
            {
                var partial = log.Tick(step2.QuestId, counted.Id, 1);
                Assert.True(partial.ObjectiveTicked);
                Assert.False(partial.StepFinished);
                Assert.Equal(i, run.Counted[counted.Id]);
            }

            var last = log.Tick(step2.QuestId, counted.Id, 1);
            Assert.True(last.StepFinished);
            Assert.Empty(run.Counted);
        }

        [Fact]
        public void Several_at_once_finishes_a_count_objective()
        {
            if (!Available) return;

            // A fight that kills three of them at once has to count as three, not as one: type 6
            // says "in one fight" precisely because that is the intended way to do it.
            var book = new QuestCatalogue();
            var log = new QuestLog(book, () => 200, () => 0);

            foreach (var quest in book.All())
            {
                foreach (var step in quest.Steps)
                {
                    if (step.Objectives.Count != 1) continue;
                    var objective = step.Objectives[0];
                    if (objective.Needed <= 1) continue;

                    log.Restore(quest.Id, step.Id, "", finished: false);
                    var move = log.Tick(quest.Id, objective.Id, objective.Needed);
                    Assert.True(move.StepFinished);
                    return;
                }
            }
        }
    }
}
