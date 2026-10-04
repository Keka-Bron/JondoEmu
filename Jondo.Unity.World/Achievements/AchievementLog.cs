using System;
using System.Collections.Generic;
using Jondo.Unity.World.Content;
using Jondo.Unity.World.Quests;

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

    /// <summary>How far one objective has got: what the window draws as a bar.</summary>
    public readonly struct ObjectiveProgress
    {
        public ObjectiveProgress(int objectiveId, long current, long maximum)
        {
            ObjectiveId = objectiveId;
            Current = current;
            Maximum = maximum;
        }

        public int ObjectiveId { get; }
        public long Current { get; }
        public long Maximum { get; }

        public bool Done => Current >= Maximum;
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
    /// without any of them. Everything it needs to know about the character comes through
    /// <see cref="IQuestFacts"/>, including the tallies — monsters beaten, zones explored, items
    /// held — which the server answers from its own counters.
    /// </remarks>
    public sealed class AchievementLog
    {
        private readonly AchievementCatalogue _book;
        private readonly IQuestFacts _facts;

        private readonly HashSet<int> _earned = new HashSet<int>();
        private readonly HashSet<int> _claimed = new HashSet<int>();

        /// <summary>
        /// How deep the cascade of achievements-needing-achievements is allowed to go.
        /// </summary>
        /// <remarks>
        /// A guard, not a limit anybody should reach: the deepest real chain in the catalogue is
        /// short. It exists because the data is regenerated from a client this project does not
        /// control, and a criterion that ever said an achievement needed itself would otherwise
        /// hang the server on somebody's login.
        /// </remarks>
        private const int CascadeLimit = 64;

        public AchievementLog(AchievementCatalogue book, IQuestFacts facts)
        {
            _book = book;
            _facts = facts;
        }

        public AchievementCatalogue Book => _book;

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
        /// Looks at every one of these and returns what was earned, cascade included, in order.
        /// </summary>
        /// <remarks>
        /// What every trigger comes down to. The catalogue's indexes say which achievements a
        /// finished quest, a fallen monster or a new zone could have moved, and each of those still
        /// has to pass all of its objectives: an achievement that wants three quests is not earned
        /// by the first of them.
        /// </remarks>
        public List<int> CheckAll(IEnumerable<int> candidates)
        {
            var earned = new List<int>();
            foreach (int candidate in new List<int>(candidates))
            {
                var move = Check(candidate);
                if (!move.Earned) continue;

                earned.Add(move.AchievementId);
                earned.AddRange(move.AlsoEarned);
            }

            return earned;
        }

        /// <summary>Every achievement in the catalogue, looked at once. What entering the world does.</summary>
        /// <remarks>
        /// So that what a character already did before this engine could judge it — a level, a
        /// zone, a quest — is recognised the next time they play, rather than never.
        /// </remarks>
        public List<int> CheckEverything()
        {
            var ids = new List<int>();
            foreach (var achievement in _book.All()) ids.Add(achievement.Id);
            return CheckAll(ids);
        }

        /// <summary>Everything a finished quest might have earned.</summary>
        public List<int> AfterQuest(int questId) => CheckAll(_book.WaitingOnQuest(questId));

        /// <summary>Whether every objective of an achievement holds right now.</summary>
        public bool Holds(int achievementId)
        {
            var achievement = _book.Of(achievementId);
            if (achievement == null) return false;

            // An achievement whose objectives could not be read at all is not earned by having
            // nothing: 322 of them name objectives the client does not describe, and the link file
            // ties 272 of those to something. Treating an empty list as "done" would hand the other
            // 50 out on login, with whatever items they carry.
            if (achievement.Objectives.Count == 0) return false;

            var facts = Facts();
            foreach (var objective in achievement.Objectives)
            {
                if (!Holds(objective, facts)) return false;
            }

            return true;
        }

        /// <summary>
        /// Whether one objective holds.
        /// </summary>
        /// <remarks>
        /// <b>What cannot be judged does not pass.</b> The opposite rule from a quest's start
        /// condition, and deliberately: "kill 500 gobballs" is not satisfied by this engine being
        /// unable to count gobballs. It is judged with three answers, though, so that an unknown
        /// term an OR has already made irrelevant does not block anything: <c>SC=0|(SC=5&amp;ST!7)</c>
        /// is true on a classic server whatever ST means.
        /// </remarks>
        private bool Holds(AchievementObjective objective, IQuestFacts facts)
        {
            if (objective.Link != null) return Holds(objective.Link, facts);
            if (objective.Criterion.Length == 0) return false;

            return Criterion.Evaluate(objective.Criterion, condition => JudgeOne(condition, facts))
                   == Content.Answer.True;
        }

        /// <summary>One term, judged by the same reader the quests use, with a third answer for "cannot say".</summary>
        private static Answer JudgeOne(Condition condition, IQuestFacts facts)
        {
            var verdict = QuestCriterion.Judge(condition.ToString(), facts);
            if (verdict.Broke || !verdict.FullyJudged) return Content.Answer.Unknown;
            return verdict.Met ? Content.Answer.True : Content.Answer.False;
        }

        private static bool Holds(AchievementLink link, IQuestFacts facts) => link.Kind switch
        {
            AchievementLink.Explore => (facts.Count(AchievementCatalogue.ExploreKey, link.Subarea, "") ?? 0) > 0,
            AchievementLink.Level => facts.Level >= link.LevelNeeded,
            AchievementLink.JobLevel => (facts.Count(AchievementCatalogue.JobKey, link.LevelNeeded, "") ?? 0) >= link.Jobs,
            AchievementLink.Craft => (facts.Scalar(AchievementCatalogue.CraftKey) ?? 0) >= link.Count,
            AchievementLink.Quest => facts.Finished(link.QuestId),
            AchievementLink.Monster => (facts.Count("EM", link.MonsterId, "") ?? 0) > 0,
            _ => false,
        };

        // ─── Progress, for the window ─────────────────────────────────────────────

        /// <summary>
        /// Every objective of an achievement with how far it has got, in the order the window
        /// draws them.
        /// </summary>
        /// <remarks>
        /// What the real server sends in the detailed lists. Measured in
        /// <c>Chats\usando todos los chats</c>: an objective that counts to a hundred travels as
        /// 91 of 100 ("Ez&gt;1,99", the maximum being the threshold plus one); every other one as
        /// 0 or 1 of 1. The ones still to do come first there and the ones done after, which is
        /// the order kept here.
        /// </remarks>
        public List<ObjectiveProgress> Progress(int achievementId)
        {
            var list = new List<ObjectiveProgress>();
            var achievement = _book.Of(achievementId);
            if (achievement == null) return list;

            bool earned = Has(achievementId);
            var facts = Facts();
            var done = new List<ObjectiveProgress>();

            foreach (var objective in achievement.Objectives)
            {
                var progress = ProgressOf(objective, facts, earned);
                (progress.Done ? done : list).Add(progress);
            }

            list.AddRange(done);
            return list;
        }

        private ObjectiveProgress ProgressOf(AchievementObjective objective, IQuestFacts facts, bool earned)
        {
            long maximum = 1;
            long current = 0;

            if (objective.Link != null)
            {
                switch (objective.Link.Kind)
                {
                    case AchievementLink.Craft:
                        maximum = Math.Max(1, objective.Link.Count);
                        current = facts.Scalar(AchievementCatalogue.CraftKey) ?? 0;
                        break;
                    case AchievementLink.JobLevel:
                        maximum = Math.Max(1, objective.Link.Jobs);
                        current = facts.Count(AchievementCatalogue.JobKey, objective.Link.LevelNeeded, "") ?? 0;
                        break;
                }
            }
            else
            {
                // A tally with a real threshold is drawn as a bar: "Ez>1,99" is out of 100. A
                // plain yes-or-no, "Ef>3567,0" or "PL>189", is out of 1.
                foreach (var term in objective.Terms)
                {
                    if (term.Comparison != '>' || term.Threshold < 1) continue;
                    maximum = term.Threshold + 1;
                    current = facts.Count(term.Op, term.Key, term.Flag) ?? 0;
                    break;
                }
            }

            // Earned is earned: an achievement already obtained shows full, even when the thing
            // it counted has since gone — an item spent, a level lost to a reset.
            if (earned || Holds(objective, facts)) current = maximum;
            return new ObjectiveProgress(objective.Id, Math.Min(Math.Max(0, current), maximum), maximum);
        }

        /// <summary>
        /// The achievements closest to being earned, for the window's summary page.
        /// </summary>
        /// <remarks>
        /// Measured in <c>Chats\usando todos los chats</c>: six achievements, each with at least one
        /// objective done and the rest not, none of them earned, from six different categories —
        /// 91 of 100, 5 of 6, 892 of 1000, 5 of 6, 2 of 3, 9 of 11. How the real server picks
        /// them is not in the capture; the closest first, up to that same six, is this engine's
        /// reading of it.
        /// </remarks>
        public List<int> AlmostFinished(int limit = 6)
        {
            var scored = new List<(double Ratio, int Id)>();
            var facts = Facts();

            foreach (var achievement in _book.All())
            {
                if (Has(achievement.Id) || achievement.Objectives.Count == 0) continue;

                double sum = 0;
                bool started = false;
                foreach (var objective in achievement.Objectives)
                {
                    var p = ProgressOf(objective, facts, false);
                    if (p.Current > 0) started = true;
                    sum += (double)p.Current / p.Maximum;
                }

                if (!started) continue;
                scored.Add((sum / achievement.Objectives.Count, achievement.Id));
            }

            scored.Sort((a, b) => a.Ratio != b.Ratio ? b.Ratio.CompareTo(a.Ratio) : a.Id.CompareTo(b.Id));

            var ids = new List<int>();
            foreach (var (_, id) in scored)
            {
                if (ids.Count >= limit) break;
                ids.Add(id);
            }

            return ids;
        }

        /// <summary>The achievements built on this one that have become earnable with it.</summary>
        private List<int> Cascade(int justEarned)
        {
            var earned = new List<int>();
            var queue = new Queue<int>();
            queue.Enqueue(justEarned);

            // Points are an operator too: "Oa>999" is a thousand achievement points, so every
            // badge earned is a candidate for the ones that count them.
            var candidates = new List<int>();

            int rounds = 0;
            while (queue.Count > 0 && rounds++ < CascadeLimit)
            {
                int at = queue.Dequeue();
                candidates.Clear();
                candidates.AddRange(_book.WaitingOnAchievement(at));
                candidates.AddRange(_book.WaitingOn("Oa"));

                foreach (int candidate in candidates)
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
        private IQuestFacts Facts() => new WithBadges(_facts, this);

        private sealed class WithBadges : IQuestFacts
        {
            private readonly IQuestFacts _inner;
            private readonly AchievementLog _log;

            public WithBadges(IQuestFacts inner, AchievementLog log)
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

            // Oa is "achievement points", and the log is the one that knows those.
            public long? Scalar(string op) => op == "Oa" ? _log.Points : _inner.Scalar(op);

            // Ob guards 1,063 rewards, 1,060 of them with the achievement's own id: "Ob!1045" on
            // a reward of 1045. It cannot mean "has it" — whoever claims has it — so it is read as
            // "has already been paid for it". INFERRED: on Ankama's server it is the account; here
            // achievements are per character, so it is this character, and the reward pays once.
            public long? Count(string op, long key, string flag)
                => op == "Ob" ? (_log.WasClaimed((int)key) ? 1 : 0) : _inner.Count(op, key, flag);
        }

        // ─── Paying ───────────────────────────────────────────────────────────────

        /// <summary>
        /// Whether a reward's own condition lets this character have it.
        /// </summary>
        /// <remarks>
        /// A reward that cannot be judged is <b>not</b> paid: letting an unreadable condition
        /// through hands over an item nobody earned. Asked before the achievement is marked paid,
        /// because <c>Ob!&lt;itself&gt;</c> is the commonest condition there is.
        /// </remarks>
        public bool Owed(AchievementReward reward)
        {
            if (reward.Criterion.Length == 0) return true;
            var facts = Facts();
            return Criterion.Evaluate(reward.Criterion, condition => JudgeOne(condition, facts))
                   == Content.Answer.True;
        }

        /// <summary>
        /// What claiming an achievement hands over to a character of that level, conditions
        /// judged, experience and kamas worked out with the client's formula.
        /// </summary>
        public AchievementPayout Payout(int achievementId, int playerLevel)
        {
            var payout = new AchievementPayout();
            var achievement = _book.Of(achievementId);
            if (achievement == null) return payout;

            foreach (var reward in achievement.Rewards)
            {
                if (!Owed(reward)) continue;

                payout.Experience += RewardFormula.Experience(playerLevel, achievement.Level, reward.ExperienceRatio);
                payout.Kamas += RewardFormula.Kamas(reward.KamasScale ? playerLevel : achievement.Level,
                                                    reward.KamasRatio);
                payout.Items.AddRange(reward.Items);
                payout.Emotes.AddRange(reward.Emotes);
                payout.Titles.AddRange(reward.Titles);
                payout.Ornaments.AddRange(reward.Ornaments);
                payout.Spells.AddRange(reward.Spells);
                payout.GuildPoints += reward.GuildPoints;
            }

            return payout;
        }
    }

    /// <summary>Everything one achievement's claim hands over.</summary>
    public sealed class AchievementPayout
    {
        public long Experience { get; set; }
        public long Kamas { get; set; }
        public long GuildPoints { get; set; }
        public List<(int Item, int Count)> Items { get; } = new List<(int, int)>();
        public List<int> Emotes { get; } = new List<int>();
        public List<int> Titles { get; } = new List<int>();
        public List<int> Ornaments { get; } = new List<int>();
        public List<int> Spells { get; } = new List<int>();
    }
}
