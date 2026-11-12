using System;
using System.Collections.Generic;
using System.Linq;
using Jondo.Unity.Server.Network;
using Jondo.Unity.World.Content;
using Jondo.Unity.World.Quests;

namespace Jondo.Unity.Server.Managers
{
    /// <summary>
    /// A criterion judged strictly for whoever is in session: met only when every letter it asks
    /// is one this knows and holds. What the monsters' own loot tables gate their quest items with.
    /// </summary>
    /// <remarks>
    /// <para>
    /// 8,639 rows of the monsters' «drops» carry a criterion, and most are quest items:
    /// «Qa=385&amp;PO&lt;2269,10» (quest 385 under way, fewer than ten in the bag),
    /// «Qs=1237&amp;Qo&gt;4096&amp;Qo&lt;4098&amp;PO!11683» (its step, an objective of it pending, none held yet),
    /// «(Pm=190578688|…)&amp;Qo=14179&amp;PO&lt;19756,10» (on those maps). The letters are the client's own
    /// (mw.ood, the criteria factory: PO ObjectCriterion, Qa/Qf/Qc QuestCriterion, Qo
    /// QuestObjectiveCriterion, Pm MapCriterion, PB SubareaCriterion, Po AreaCriterion, PL
    /// LevelCriterion, PZ SubscribeCriterion, HA AlterationCriterion, OA AchievementCriterion) but
    /// for Qs, which the client does not have and the data uses for a quest's step.
    /// </para>
    /// <para>
    /// In a drop, Qo asks about the objectives still to do in the quests' current steps: «Qo=14179»
    /// is «kill ten larvae» pending, and «Qo&gt;4096&amp;Qo&lt;4098» is objective 4097 among those pending
    /// -- step 1237's objectives run 4074 to 4103. INFERRED, as the order of an objective's ids.
    /// Mt and Ms are about the monster that drops (see Resolve). The character's other letters --
    /// the server's type and season, the yearly events, the date -- are asked of the achievement
    /// engine (ByCharacter). What nobody answers -- Sv, OO, Ot… the server's own letters -- leaves
    /// the row Unknown, and an Unknown row does not drop: «never dropping them is preferable to
    /// always dropping them» still holds for them.
    /// </para>
    /// <para>
    /// The meats: «Pj&gt;41,9&amp;CU&gt;0» on «Carne picada», «Pj&gt;41,19» on «Carne manida» -- job 41 is
    /// the Cazador, above level 9, 19. INFERRED: CU is a hunting weapon in hand, effect 795 «Arma
    /// de caza», which 43 weapons carry: a hunter's meat, as the game always had it.
    /// </para>
    /// </remarks>
    public static class PlayerCriteria
    {
        /// <summary>
        /// Whether whoever is in session meets the criterion, standing on <paramref name="mapId"/> --
        /// a fight's roleplay map, not its arena -- and holding what <paramref name="held"/> counts.
        /// </summary>
        public static bool Met(string criterion, long mapId, Func<int, int>? held = null, int monsterId = 0)
            => Criterion.Met(criterion, ResolverFor(mapId, held, monsterId));

        /// <summary>The resolver itself, for whoever is in session.</summary>
        /// <remarks><paramref name="monsterId"/>: the monster whose loot is judged, for Mt and Ms.</remarks>
        public static Criterion.Resolver ResolverFor(long mapId, Func<int, int>? held = null, int monsterId = 0)
        {
            var state = SessionContext.State;
            held ??= Equipment.HowMany;
            var facts = new Facts(state, Quests.Log, Quests.Book, mapId, held, state.JobLevel, HuntingWeaponInHand,
                                  Equipment.HowManyWorn, monsterId);
            return condition => Resolve(condition, facts);
        }

        /// <summary>«Arma de caza».</summary>
        public const int HuntingWeaponEffect = 795;

        /// <summary>The weapon's slot.</summary>
        private const int WeaponSlot = 1;

        private static bool HuntingWeaponInHand()
            => Equipment.InSlot(WeaponSlot)?.Effects.Any(e => e.Effect == HuntingWeaponEffect) ?? false;

        /// <summary>What a condition asks of a character, as plain facts, so it can be answered without a session.</summary>
        internal readonly record struct Facts(SessionState? State, QuestLog? Log, QuestCatalogue? Book, long MapId,
                                              Func<int, int> Held, Func<int, int>? JobLevel = null,
                                              Func<bool>? HuntingWeapon = null, Func<int, int>? Worn = null,
                                              int MonsterId = 0);

        internal static Answer Resolve(Condition condition, Facts facts)
        {
            char op = condition.Operator;
            long value = condition.Value;
            switch (condition.Code)
            {
                case "PL": return facts.State == null ? Answer.Unknown : Compare(facts.State.CharacterLevel, op, value);
                case "Pm": return Compare(facts.MapId, op, value);
                case "PB": return Compare(DatabaseManager.SubAreaOfMap(facts.MapId), op, value);
                case "Po": return Compare(Interactives.AreaOfSubArea(DatabaseManager.SubAreaOfMap(facts.MapId)), op, value);

                // Every account is subscribed here (Subscription).
                case "PZ": return Compare(1, op, value);

                // The character's sex, 0 a man and 1 a woman; an attitude he knows; the pods he has free
                // (what he carries weighs nothing here, so his capacity): the guardian of the bridge of
                // death's questions.
                case "PS": return facts.State == null ? Answer.Unknown : Compare(facts.State.Sex, op, value);
                case "PE": return facts.State == null ? Answer.Unknown : Truth(facts.State.Emotes.Contains((int)value), op);
                case "Pw": return facts.State == null ? Answer.Unknown : Compare(1000 + 5L * facts.State.TotalStrength, op, value);

                // His alignment's side: 0 neutral, 1 Bonta, 2 Brakmar (Alignments).
                case "Ps": return facts.State == null ? Answer.Unknown : Compare(Alignments.CriterionSide(facts.State.CharacterId), op, value);

                // Its level and rank, from the quests he finished (Alignments.Progression).
                case "Pa":
                case "Pr":
                    if (facts.State == null || facts.Log == null) return Answer.Unknown;
                    {
                        var finished = facts.Log.Runs.Values.Where(r => r.Finished).Select(r => r.QuestId);
                        int side = Alignments.CriterionSide(facts.State.CharacterId);
                        return Compare(condition.Code == "Pa" ? Alignments.Index().Level(side, finished)
                                                              : Alignments.Index().Rank(side, finished), op, value);
                    }

                case "PO":
                    // Worn, «E», or not worn, «X»: the Kama Baraka asks for the Dados de la Suerte on.
                    if (condition.Operator is 'E' or 'X')
                    {
                        if (facts.Worn == null || condition.Args.Count == 0 || !int.TryParse(condition.Args[0], out int worn))
                            return Answer.Unknown;
                        return Truth(facts.Worn(worn) > 0, condition.Operator == 'E' ? '=' : '!');
                    }
                    return Holding(condition, facts.Held);

                case "Qa": return facts.Log == null ? Answer.Unknown : Truth(facts.Log.Active((int)value), op);
                case "Qf":
                case "Qc": return facts.Log == null ? Answer.Unknown : Truth(facts.Log.Finished((int)value), op);

                // How many times a quest was finished: «QF>1423,0» at least once, which Cuerbax asks
                // before his evaluations. One run is kept per quest, so it counts as once.
                case "QF":
                    if (facts.Log == null || condition.Args.Count == 0 || !int.TryParse(condition.Args[0], out int quest))
                        return Answer.Unknown;
                    return condition.Args.Count == 1
                        ? Truth(facts.Log.Finished(quest), op)
                        : Compare(facts.Log.Finished(quest) ? 1 : 0, op, value);
                case "Qs":
                    return facts.Log == null ? Answer.Unknown : Truth(facts.Log.Doing().Any(r => r.StepId == value), op);
                case "Qo": return Pending(facts, op, value);

                // A job: «PJ=28» had at all, «Pj>41,9» above a level.
                case "PJ":
                case "Pj":
                    return Job(condition, facts.JobLevel);
                case "CU":
                    return facts.HuntingWeapon == null ? Answer.Unknown : Compare(facts.HuntingWeapon() ? 1 : 0, op, value);

                case "HA":
                    return facts.State == null ? Answer.Unknown : Truth(Alterations.Has(facts.State.CharacterId, (int)value), op);
                // The loot's own two letters, about the monster that drops it. Mt is whether he is a
                // boss -- the value-4 bit of his m_flags (Dreams.IsBoss): of the 1,804 rows that ask
                // it, 1,268 «Mt=1» are dungeon bosses' and 495 «Mt=0» are not (the bosses' pets, Thor
                // Pestruz's egg for «Un huevo para Zurcarák»; the treasure maps' pieces off ordinary
                // monsters); the 40 others are bosses outside the dungeons' list (Grozilla, Demoloch)
                // and Kabaal. Ms is only ever asked «=0», in 1,629 rows beside Mt: whatever the
                // server tallies there, an ordinary fight is the 0 -- INFERRED, or none of those rows
                // would ever drop.
                case "Mt": return facts.MonsterId == 0 ? Answer.Unknown : Compare(Dreams.IsBoss(facts.MonsterId) ? 1 : 0, op, value);
                case "Ms": return facts.MonsterId == 0 ? Answer.Unknown : Compare(0, op, value);
                case "OA":
                    return facts.State?.Achievements == null ? Answer.Unknown : Truth(facts.State.Achievements.Has((int)value), op);

                default: return ByCharacter(condition, facts);
            }
        }

        /// <summary>
        /// A letter the loot does not judge itself, as the achievement engine answers it for the
        /// character in session (<see cref="Achievements.CharacterFacts"/>, which the quests and the
        /// items' criteria read too); Unknown when it does not know it either.
        /// </summary>
        /// <remarks>
        /// The 117 pieces of equipment the monsters drop to the young, «(SC!5|ST!5)&amp;PL&gt;0&amp;PL&lt;59»
        /// on the piwis and the like, never fell: SC and ST were nobody's here, and an unknown row
        /// does not drop. SC is the server's game type, ST its season (the client's
        /// ServerTypeCriterion and SeasonCriterion): not the fifth Temporis on this classic server.
        /// The same reader answers the yearly events' «Sc=944», the days an item has been held
        /// («DD&lt;30,21172»), the world events' «WE» and the date's «SG»/«Sd», with the shapes
        /// <see cref="Jondo.Unity.World.Quests.QuestCriterion"/> gives them: «DD&lt;days,item»,
        /// «OP&gt;key,n» a tally compared with n, «OP=n» a number, or «OP=key» at least one held.
        /// Only for the character in session: the reader asks the session, not these facts.
        /// </remarks>
        private static Answer ByCharacter(Condition condition, Facts facts)
        {
            if (facts.State == null || !ReferenceEquals(facts.State, SessionContext.State) || condition.Args.Count == 0)
                return Answer.Unknown;
            if (!long.TryParse(condition.Args[0], out long first)) return Answer.Unknown;
            var character = new Achievements.CharacterFacts();
            string code = condition.Code;
            char op = condition.Operator;

            if (code is "DD" or "DH" or "DM" && condition.Args.Count >= 2)
            {
                if (!long.TryParse(condition.Args[1], out long item)) return Answer.Unknown;
                long? since = character.Count(code, item, "");
                return since.HasValue ? Compare(since.Value, op, first) : Answer.Unknown;
            }

            if (condition.Args.Count >= 2)
            {
                if (!long.TryParse(condition.Args[1], out long threshold)) return Answer.Unknown;
                long? tally = character.Count(code, first, condition.Args.Count >= 3 ? condition.Args[2] : "");
                return tally.HasValue ? Compare(tally.Value, op, threshold) : Answer.Unknown;
            }

            long? scalar = character.Scalar(code);
            if (scalar.HasValue) return Compare(scalar.Value, op, first);

            long? held = character.Count(code, first, "");
            if (!held.HasValue) return Answer.Unknown;
            return op switch
            {
                '=' => Bool(held.Value > 0),
                '!' => Bool(held.Value == 0),
                _ => Answer.Unknown,
            };
        }

        /// <summary>
        /// «PO!19649» none held, «PO=21619» one at least, «PO&lt;2269,10» fewer than ten, «PO&gt;x,n» more
        /// than n.
        /// </summary>
        private static Answer Holding(Condition condition, Func<int, int> held)
        {
            if (condition.Args.Count == 0 || !int.TryParse(condition.Args[0], out int item)) return Answer.Unknown;
            int have = held(item);
            if (condition.Args.Count == 1)
            {
                return condition.Operator switch
                {
                    '=' => Bool(have > 0),
                    '!' => Bool(have == 0),
                    _ => Answer.Unknown,
                };
            }
            return long.TryParse(condition.Args[1], out long count) ? Compare(have, condition.Operator, count) : Answer.Unknown;
        }

        private static Answer Job(Condition condition, Func<int, int>? level)
        {
            if (level == null || condition.Args.Count == 0 || !int.TryParse(condition.Args[0], out int job)) return Answer.Unknown;
            int have = level(job);
            if (condition.Args.Count == 1)
            {
                return condition.Operator switch
                {
                    '=' => Bool(have > 0),
                    '!' => Bool(have == 0),
                    _ => Answer.Unknown,
                };
            }
            return long.TryParse(condition.Args[1], out long wanted) ? Compare(have, condition.Operator, wanted) : Answer.Unknown;
        }

        /// <summary>The objectives still to do in the current steps of the quests under way, compared.</summary>
        private static Answer Pending(Facts facts, char op, long value)
        {
            if (facts.Log == null || facts.Book == null) return Answer.Unknown;
            var pending = new List<int>();
            foreach (var run in facts.Log.Doing())
            {
                var step = facts.Book.Step(run.StepId);
                if (step == null) continue;
                pending.AddRange(step.Objectives.Select(o => o.Id).Where(id => !run.Done.Contains(id)));
            }
            return op switch
            {
                '=' => Bool(pending.Contains((int)value)),
                '!' => Bool(!pending.Contains((int)value)),
                // '>' and '<' are «done» and «not done», as the client words them (its texts
                // ui.grimoire.quest.objective done / notDone beside pending / notPending): the
                // samples of «Genio en movimiento» drop on «Qo<13805», the step's very first
                // objective, which no other reading lets through.
                '>' => Bool(facts.Log.ObjectiveDone((int)value)),
                '<' => Bool(!facts.Log.ObjectiveDone((int)value)),
                _ => Answer.Unknown,
            };
        }

        private static Answer Compare(long have, char op, long value) => op switch
        {
            '=' => Bool(have == value),
            '!' => Bool(have != value),
            '>' => Bool(have > value),
            '<' => Bool(have < value),
            _ => Answer.Unknown,
        };

        private static Answer Truth(bool have, char op) => op switch
        {
            '=' => Bool(have),
            '!' => Bool(!have),
            _ => Answer.Unknown,
        };

        private static Answer Bool(bool yes) => yes ? Answer.True : Answer.False;
    }
}
