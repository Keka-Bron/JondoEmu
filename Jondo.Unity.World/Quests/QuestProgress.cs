using System;
using System.Collections.Generic;
using System.Text;

namespace Jondo.Unity.World.Quests
{
    /// <summary>Where one character has got to on one quest.</summary>
    public sealed class QuestRun
    {
        public QuestRun(int questId, int stepId)
        {
            QuestId = questId;
            StepId = stepId;
        }

        public int QuestId { get; }

        /// <summary>The step being worked on. Zero once the quest is over.</summary>
        public int StepId { get; internal set; }

        /// <summary>Whether the whole quest is done.</summary>
        public bool Finished { get; internal set; }

        /// <summary>
        /// The objectives ticked off on the step in hand.
        /// </summary>
        /// <remarks>
        /// Cleared when the step changes, on purpose: the client is told the objectives of the
        /// current step and nothing else, and keeping the old ones would mean a repeatable quest's
        /// second run started with everything already done.
        /// </remarks>
        public HashSet<int> Done { get; } = new HashSet<int>();

        /// <summary>
        /// How far along an objective that needs several of something is.
        /// </summary>
        /// <remarks>
        /// Only the objectives that count things ever appear here, and only while they are part
        /// way: as soon as one reaches what it needs it moves to <see cref="Done"/> and its tally
        /// is dropped. Keeping finished tallies would double the size of the row for no gain, and
        /// would make "is it done" answerable two different ways, which is how the two answers end
        /// up disagreeing.
        /// </remarks>
        public Dictionary<int, int> Counted { get; } = new Dictionary<int, int>();

        public override string ToString()
            => Finished ? $"quest {QuestId} finished" : $"quest {QuestId} on step {StepId}";
    }

    /// <summary>What ticking an objective off changed.</summary>
    /// <remarks>
    /// <see cref="FinishedStep"/> is carried rather than worked out afterwards on purpose. The
    /// packet that tells the client a step is done names the step that <em>was</em> in hand, and
    /// by the time the caller sees this the log has already moved on — so the caller would have to
    /// find it by walking the quest's step list backwards from the new one, which is both fiddly
    /// and wrong for the last step of a quest, where there is no new one to walk back from.
    /// </remarks>
    public readonly struct QuestMove
    {
        public QuestMove(bool objectiveTicked, int finishedStep, int nextStep, bool questFinished)
        {
            ObjectiveTicked = objectiveTicked;
            FinishedStep = finishedStep;
            NextStep = nextStep;
            QuestFinished = questFinished;
        }

        /// <summary>False when the objective was already done, or was not part of this step.</summary>
        public bool ObjectiveTicked { get; }

        /// <summary>The step that was just completed. Zero when none was.</summary>
        public int FinishedStep { get; }

        /// <summary>True when that was the last objective the step wanted.</summary>
        public bool StepFinished => FinishedStep != 0;

        /// <summary>The step now in hand. Zero when the quest is over.</summary>
        public int NextStep { get; }

        public bool QuestFinished { get; }

        /// <summary>Nothing happened, so nothing needs sending to the client.</summary>
        public bool Nothing => !ObjectiveTicked && !StepFinished && !QuestFinished;

        public static QuestMove None => new QuestMove(false, 0, 0, false);
    }

    /// <summary>
    /// One character's quest log: what is under way, how far, and what is over.
    /// </summary>
    /// <remarks>
    /// Deliberately knows nothing about the server, the database or the wire. It is the piece that
    /// decides <em>what happened</em>, and everything about telling the client and writing it down
    /// is somebody else's job — which is what lets the whole thing be tested without a running
    /// server, and lets the editor show a character's progress without linking against one.
    ///
    /// It implements <see cref="IQuestFacts"/> so that a start condition can be judged straight
    /// against it. Level and map are not its business, so they come in from outside.
    ///
    /// <b>Not thread-safe, on purpose.</b> One of these belongs to one character and is touched
    /// from that character's connection. Locking it would suggest it can be shared, which would be
    /// a worse mistake than the one the lock prevents.
    /// </remarks>
    public sealed class QuestLog : IQuestFacts
    {
        private readonly Dictionary<int, QuestRun> _runs = new Dictionary<int, QuestRun>();
        private readonly QuestCatalogue _book;

        public QuestLog(QuestCatalogue book, Func<int> level, Func<long> map)
        {
            _book = book;
            _level = level;
            _map = map;
        }

        private readonly Func<int> _level;
        private readonly Func<long> _map;

        public int Level => _level();
        public long MapId => _map();

        /// <summary>Every quest this character has touched, finished or not.</summary>
        public IReadOnlyDictionary<int, QuestRun> Runs => _runs;

        public bool Finished(int questId) => _runs.TryGetValue(questId, out var run) && run.Finished;

        public bool Active(int questId) => _runs.TryGetValue(questId, out var run) && !run.Finished;

        public bool ObjectiveDone(int objectiveId)
        {
            foreach (var run in _runs.Values)
            {
                if (run.Done.Contains(objectiveId)) return true;
            }

            return false;
        }

        public QuestRun? Run(int questId) => _runs.TryGetValue(questId, out var run) ? run : null;

        /// <summary>The quests under way, for the list the client asks for on login.</summary>
        public IEnumerable<QuestRun> Doing()
        {
            foreach (var run in _runs.Values)
            {
                if (!run.Finished) yield return run;
            }
        }

        /// <summary>
        /// Whether this character could be offered a quest right now.
        /// </summary>
        /// <remarks>
        /// Three questions, in the order that costs least: is it already in hand or over, does the
        /// quest exist and have a step to start on, and only then the condition, which is the
        /// expensive one because it walks a parsed expression.
        ///
        /// A finished quest can be offered again only when it says it is repeatable. Ankama's own
        /// repeat limit is not enforced here beyond that, because the count of how many times a
        /// character has done a quest is not kept — a deliberate gap, noted rather than faked.
        /// </remarks>
        public bool CanStart(int questId, out CriterionVerdict verdict)
        {
            verdict = new CriterionVerdict(false, Array.Empty<string>());

            var quest = _book.Of(questId);
            if (quest == null || quest.Steps.Count == 0) return false;

            if (_runs.TryGetValue(questId, out var run))
            {
                if (!run.Finished) return false;
                if (!quest.Repeatable) return false;
            }

            verdict = QuestCriterion.Judge(quest.Criterion, this);
            return verdict.Met;
        }

        /// <summary>
        /// Puts a quest in hand at its first step. Returns null when it could not be started.
        /// </summary>
        public QuestRun? Start(int questId)
        {
            if (!CanStart(questId, out _)) return null;

            var quest = _book.Of(questId)!;
            var run = new QuestRun(questId, quest.Steps[0].Id);
            _runs[questId] = run;
            return run;
        }

        /// <summary>
        /// Puts a quest in hand without asking whether it is allowed. For loading from the database.
        /// </summary>
        /// <remarks>
        /// Separate from <see cref="Start"/> and named so that the difference is impossible to miss.
        /// Loading has to be unconditional: a character who started a quest at level 30 and has
        /// since been reset, or whose quest's condition has changed under them in a patch, still
        /// has that quest in their log, and silently dropping it on login would lose their
        /// progress with no message anywhere.
        /// </remarks>
        public QuestRun Restore(int questId, int stepId, string? packed, bool finished)
        {
            var run = new QuestRun(questId, stepId) { Finished = finished };
            Unpack(packed, run);
            _runs[questId] = run;
            return run;
        }

        /// <summary>
        /// Ticks one objective off, and works out what that finished.
        /// </summary>
        /// <remarks>
        /// The whole cascade lives here rather than in the handler because it is the part that has
        /// to be right and the part nothing else can check: an objective completes a step, a step
        /// completes a quest, and each of those has a packet the client is waiting for. Splitting
        /// it across the two handlers that can trigger it — the dialogue one and the fight one —
        /// would mean two copies of a rule that must not differ.
        /// </remarks>
        public QuestMove Tick(int questId, int objectiveId, int amount = 0)
        {
            if (!_runs.TryGetValue(questId, out var run) || run.Finished) return QuestMove.None;

            var step = _book.Step(run.StepId);
            if (step == null) return QuestMove.None;

            // Only an objective of the step in hand counts. One from a step already passed, or from
            // a step still to come, is the client being wrong or a handler being confused, and
            // either way accepting it would move a quest on for the wrong reason.
            QuestObjective? wanted = null;
            foreach (var objective in step.Objectives)
            {
                if (objective.Id == objectiveId) { wanted = objective; break; }
            }

            if (wanted == null || run.Done.Contains(objectiveId)) return QuestMove.None;

            // An objective that wants several of something is not finished by one of them. The
            // count comes from the objective itself, so a caller cannot get it wrong by passing
            // the number it happens to have to hand.
            if (amount > 0 && wanted.Needed > 1)
            {
                run.Counted.TryGetValue(objectiveId, out int already);
                int now = already + amount;

                if (now < wanted.Needed)
                {
                    run.Counted[objectiveId] = now;
                    return new QuestMove(true, 0, run.StepId, false);
                }

                run.Counted.Remove(objectiveId);
            }

            if (!run.Done.Add(objectiveId)) return QuestMove.None;

            foreach (var objective in step.Objectives)
            {
                if (!run.Done.Contains(objective.Id))
                {
                    return new QuestMove(true, 0, run.StepId, false);
                }
            }

            int finished = run.StepId;
            return new QuestMove(true, finished, Advance(run), run.Finished);
        }

        /// <summary>
        /// Finishes the step in hand outright, whatever its objectives say.
        /// </summary>
        /// <remarks>
        /// Needed because the client decides some objectives for itself. In the tutorial capture the
        /// client sends the server "objective 19163 is done" and the server takes its word: those
        /// are free-text objectives — 5,670 of the 15,547 — that ask the player to click something
        /// the server never sees.
        /// </remarks>
        public QuestMove FinishStep(int questId)
        {
            if (!_runs.TryGetValue(questId, out var run) || run.Finished) return QuestMove.None;

            var step = _book.Step(run.StepId);
            if (step != null)
            {
                foreach (var objective in step.Objectives) run.Done.Add(objective.Id);
            }

            int finished = run.StepId;
            return new QuestMove(true, finished, Advance(run), run.Finished);
        }

        /// <summary>Moves a run on to the step after the one it is on, or ends it.</summary>
        private int Advance(QuestRun run)
        {
            var quest = _book.Of(run.QuestId);
            if (quest == null)
            {
                run.Finished = true;
                run.StepId = 0;
                return 0;
            }

            int at = -1;
            for (int i = 0; i < quest.Steps.Count; i++)
            {
                if (quest.Steps[i].Id == run.StepId) { at = i; break; }
            }

            if (at < 0 || at + 1 >= quest.Steps.Count)
            {
                run.Finished = true;
                run.StepId = 0;
                run.Done.Clear();
                run.Counted.Clear();
                return 0;
            }

            run.StepId = quest.Steps[at + 1].Id;
            run.Done.Clear();
            run.Counted.Clear();
            return run.StepId;
        }

        // ─── Writing it down ──────────────────────────────────────────────────────

        /// <summary>
        /// One run's progress, as the database column holds it.
        /// </summary>
        /// <remarks>
        /// Two halves separated by a bar: the objectives already finished, and then the ones part
        /// way through with their tally. <c>18390,18391|18392:3</c> is two done and a third three
        /// of the way to whatever it needs.
        ///
        /// Text rather than a table of its own because it is only ever read and written whole, for
        /// one quest of one character, and a second table would be a join to maintain for a column
        /// nothing will ever query by.
        /// </remarks>
        public static string Pack(QuestRun run)
        {
            var text = new StringBuilder();
            foreach (int objective in run.Done)
            {
                if (text.Length > 0) text.Append(',');
                text.Append(objective);
            }

            if (run.Counted.Count == 0) return text.ToString();

            text.Append('|');
            bool first = true;
            foreach (var pair in run.Counted)
            {
                if (!first) text.Append(',');
                text.Append(pair.Key).Append(':').Append(pair.Value);
                first = false;
            }

            return text.ToString();
        }

        /// <summary>
        /// Reads that column back into a run. Anything unreadable in it is dropped, not thrown.
        /// </summary>
        /// <remarks>
        /// It is a text column in a database people poke at by hand, and it is read on login. A
        /// parse that threw would take somebody's character out of the game over a stray comma.
        /// </remarks>
        public static void Unpack(string? packed, QuestRun into)
        {
            if (string.IsNullOrEmpty(packed)) return;

            string[] halves = packed.Split('|');

            foreach (string piece in halves[0].Split(','))
            {
                if (int.TryParse(piece, out int objective)) into.Done.Add(objective);
            }

            if (halves.Length < 2) return;

            foreach (string piece in halves[1].Split(','))
            {
                string[] pair = piece.Split(':');
                if (pair.Length == 2 && int.TryParse(pair[0], out int objective)
                                     && int.TryParse(pair[1], out int count) && count > 0)
                {
                    into.Counted[objective] = count;
                }
            }
        }
    }
}
