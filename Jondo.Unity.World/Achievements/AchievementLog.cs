using System;
using System.Collections.Generic;

namespace Jondo.Unity.World.Achievements
{
    /// <summary>What happened when an achievement was looked at.</summary>
    public readonly struct AchievementMove
    {
        public AchievementMove(int achievementId, bool earned, IReadOnlyList<int> alsoEarned)
        {
            AchievementId = achievementId;
            Earned = earned;
            AlsoEarned = alsoEarned;
        }

        public int AchievementId { get; }

        /// <summary>True when it was earned just now, false when it already was or is not yet.</summary>
        public bool Earned { get; }

        /// <summary>
        /// The achievements that fell out of this one being earned, and the ones after those.
        /// </summary>
        /// <remarks>
        /// Achievement 8520 is nothing but "have 8518 and 8519", so earning the second of those
        /// earns 8520 in the same breath, and whatever is built on 8520 after it. The cascade is
        /// returned rather than announced from inside because the caller is the only thing that
        /// knows how to tell the client, and because a rule that sends packets is a rule that
        /// cannot be tested without a socket.
        /// </remarks>
        public IReadOnlyList<int> AlsoEarned { get; }

        public static AchievementMove Nothing(int id)
            => new AchievementMove(id, false, Array.Empty<int>());
    }

    /// <summary>
    /// One character's achievements: what is earned, and what has been paid for.
    /// </summary>
    /// <remarks>
    /// Two facts per achievement and they are not the same fact. <b>Earned</b> is the game saying
    /// you did it. <b>Claimed</b> is the reward having been handed over, and it is separate because
    /// Ankama's client asks for the reward with a packet of its own — the capture
    /// <c>Logros\aceptar recompensas de un logro</c> is nothing but the player pressing that
    /// button. An engine that paid on earning would be a different game, and one that lost the
    /// distinction would pay twice.
    ///
    /// Knows nothing about the server, the database or the wire, the same as the quest log, and for
    /// the same reason: the cascade is the part that has to be right and it should be testable
    /// without any of them.
    /// </remarks>
    public sealed class AchievementLog
    {
        private readonly AchievementCatalogue _book;
        private readonly Quests.IQuestFacts _facts;

        private readonly HashSet<int> _earned = new HashSet<int>();
        private readonly HashSet<int> _claimed = new HashSet<int>();

        /// <summary>
        /// How deep the cascade of achievements-needing-achievements is allowed to go.
        /// </summary>
        /// <remarks>
        /// A guard, not a limit anybody should reach: the deepest real chain in the catalogue is
        /// two — 8520 needs 8518 and 8519, and nothing needs 8520. It exists because the data is
        /// regenerated from a client this project does not control, and a criterion that ever said
        /// an achievement needed itself would otherwise hang the server on somebody's login.
        /// </remarks>
        private const int CascadeLimit = 16;

        public AchievementLog(AchievementCatalogue book, Quests.IQuestFacts facts)
        {
            _book = book;
            _facts = facts;
        }

        public IReadOnlyCollection<int> Earned => _earned;
        public IReadOnlyCollection<int> Claimed => _claimed;

        public bool Has(int achievementId) => _earned.Contains(achievementId);

        public bool WasClaimed(int achievementId) => _claimed.Contains(achievementId);

        /// <summary>Everything earned and not yet paid for.</summary>
        public IEnumerable<int> Unclaimed()
        {
            foreach (int id in _earned)
            {
                if (!_claimed.Contains(id)) yield return id;
            }
        }

        /// <summary>What the achievement points add up to. The number the client shows as a score.</summary>
        public int Points
        {
            get
            {
                int total = 0;
                foreach (int id in _earned) total += _book.Of(id)?.Points ?? 0;
                return total;
            }
        }

        /// <summary>Puts one back on from the database, asking nothing.</summary>
        public void Restore(int achievementId, bool claimed)
        {
            _earned.Add(achievementId);
            if (claimed) _claimed.Add(achievementId);
        }

        /// <summary>Marks the reward as handed over. Returns false when there was nothing to pay.</summary>
        public bool MarkClaimed(int achievementId)
            => _earned.Contains(achievementId) && _claimed.Add(achievementId);

        /// <summary>
        /// Looks at one achievement and earns it if every objective now holds.
        /// </summary>
        public AchievementMove Check(int achievementId)
        {
            if (_earned.Contains(achievementId)) return AchievementMove.Nothing(achievementId);
            if (!Holds(achievementId)) return AchievementMove.Nothing(achievementId);

            _earned.Add(achievementId);
            return new AchievementMove(achievementId, true, Cascade(achievementId));
        }

        /// <summary>
        /// Everything a finished quest might have earned.
        /// </summary>
        /// <remarks>
        /// Only the achievements that name that quest are looked at — the index exists so this is
        /// a handful rather than 2,780 — and each of those still has to pass all of its objectives,
        /// because an achievement that wants three quests is not earned by the first of them.
        /// </remarks>
        public List<int> AfterQuest(int questId)
        {
            var earned = new List<int>();
            foreach (int candidate in _book.WaitingOnQuest(questId))
            {
                var move = Check(candidate);
                if (!move.Earned) continue;

                earned.Add(move.AchievementId);
                earned.AddRange(move.AlsoEarned);
            }

            return earned;
        }

        /// <summary>Whether every objective of an achievement holds right now.</summary>
        public bool Holds(int achievementId)
        {
            var achievement = _book.Of(achievementId);
            if (achievement == null) return false;

            // An achievement with no objectives at all is not earned by having nothing: 322 of
            // them are like that, and treating an empty list as "done" would hand every one of
            // them out on login, with whatever items they carry.
            if (achievement.Objectives.Count == 0) return false;

            foreach (var objective in achievement.Objectives)
            {
                if (objective.Criterion.Length == 0) return false;

                var verdict = Quests.QuestCriterion.Judge(objective.Criterion, Facts());
                if (!verdict.Met) return false;

                // And an objective whose every term was skipped is not an objective that passed.
                // Without this, "kill 500 gobballs" — which this engine cannot count — would be
                // satisfied by silence.
                if (verdict.Broke || verdict.Skipped.Count > 0) return false;
            }

            return true;
        }

        /// <summary>The achievements that this one being earned has now made possible.</summary>
        private List<int> Cascade(int justEarned)
        {
            var earned = new List<int>();
            var queue = new Queue<int>();
            queue.Enqueue(justEarned);

            int rounds = 0;
            while (queue.Count > 0 && rounds++ < CascadeLimit)
            {
                int at = queue.Dequeue();
                foreach (int candidate in _book.WaitingOnAchievement(at))
                {
                    if (_earned.Contains(candidate) || !Holds(candidate)) continue;

                    _earned.Add(candidate);
                    earned.Add(candidate);
                    queue.Enqueue(candidate);
                }
            }

            return earned;
        }

        /// <summary>
        /// The character, as the criterion reader sees them — with the achievements filled in.
        /// </summary>
        /// <remarks>
        /// A wrapper rather than making the session state implement <c>AchievementDone</c> itself,
        /// so that the answer comes from <em>this</em> log. Otherwise an achievement's own cascade
        /// would be judged against whatever the caller happened to pass in, and the half-built
        /// state in the middle of a cascade is exactly when that matters.
        /// </remarks>
        private Quests.IQuestFacts Facts() => new WithBadges(_facts, this);

        private sealed class WithBadges : Quests.IQuestFacts
        {
            private readonly Quests.IQuestFacts _inner;
            private readonly AchievementLog _log;

            public WithBadges(Quests.IQuestFacts inner, AchievementLog log)
            {
                _inner = inner;
                _log = log;
            }

            public int Level => _inner.Level;
            public long MapId => _inner.MapId;
            public bool Finished(int questId) => _inner.Finished(questId);
            public bool Active(int questId) => _inner.Active(questId);
            public bool ObjectiveDone(int objectiveId) => _inner.ObjectiveDone(objectiveId);
            public bool AchievementDone(int achievementId) => _log.Has(achievementId);
        }
    }
}
