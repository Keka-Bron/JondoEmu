using System;
using System.Collections.Generic;

namespace Jondo.Unity.World.Quests
{
    /// <summary>
    /// What the engine can look up about a character in order to judge a start condition.
    /// </summary>
    /// <remarks>
    /// An interface rather than the character class itself so that the evaluator can be tested
    /// without a server, a database or a logged-in player. Every method here is cheap and
    /// side-effect free: the evaluator calls them in whatever order the condition happens to be
    /// written in, and short-circuits, so anything expensive would be paid for unpredictably.
    /// </remarks>
    public interface IQuestFacts
    {
        /// <summary>The character's level, for <c>PL</c>.</summary>
        int Level { get; }

        /// <summary>The map the character is standing on, for <c>Pm</c>.</summary>
        long MapId { get; }

        /// <summary>Whether a quest has been finished, for <c>Qf</c> and <c>Qc</c>.</summary>
        bool Finished(int questId);

        /// <summary>Whether a quest is under way right now, for <c>Qa</c>.</summary>
        bool Active(int questId);

        /// <summary>Whether one objective has been ticked off, for <c>Qo</c>.</summary>
        bool ObjectiveDone(int objectiveId);

        /// <summary>
        /// Whether an achievement has been earned, for <c>OA</c>.
        /// </summary>
        /// <remarks>
        /// Defaulted to false rather than made abstract because the criterion language is shared
        /// and the two users of it are not: a quest's start condition never mentions an
        /// achievement, and 2,157 of the achievements' own objectives are nothing but <c>OA</c> —
        /// achievements that need other achievements. Anything that only judges quests can leave
        /// this alone and get the old behaviour.
        /// </remarks>
        bool AchievementDone(int achievementId) => false;
    }

    /// <summary>What came of judging a condition.</summary>
    public readonly struct CriterionVerdict
    {
        public CriterionVerdict(bool met, IReadOnlyList<string> skipped, bool broke = false)
        {
            Met = met;
            Skipped = skipped;
            Broke = broke;
        }

        /// <summary>
        /// True when the string could not be read as a condition at all.
        /// </summary>
        /// <remarks>
        /// Kept apart from <see cref="Skipped"/> because the two mean opposite things about this
        /// code. An operator this engine does not model is expected and permanent — there are 23 of
        /// them and no plan to support alignment. A condition that will not parse is a bug in this
        /// class or a change in the client's format, and it should be shouted about rather than
        /// counted alongside the ordinary gaps. Every one of the 1,976 conditions in the catalogue
        /// parses today.
        /// </remarks>
        public bool Broke { get; }

        /// <summary>Whether the character may be offered the quest.</summary>
        public bool Met { get; }

        /// <summary>
        /// The operators in the condition that this engine cannot judge, and therefore let through.
        /// </summary>
        /// <remarks>
        /// Reported rather than swallowed, because a quest offered on a condition that was only
        /// half read is a different thing from one offered on a condition that passed, and only the
        /// caller knows whether that matters. The editor shows it; the server logs it once.
        /// </remarks>
        public IReadOnlyList<string> Skipped { get; }

        /// <summary>True when every term was understood.</summary>
        public bool FullyJudged => Skipped.Count == 0;
    }

    /// <summary>
    /// Reads and judges one of Ankama's criterion strings.
    /// </summary>
    /// <remarks>
    /// Named for quests because that is where it was first needed, but the language is shared: an
    /// achievement's objectives are written in exactly the same one, brackets and bare <c>!</c>
    /// and all. The only difference is which operators turn up — quests lean on <c>PL</c> and
    /// <c>Qf</c>, achievements on <c>OA</c> and <c>Ef</c> — so the reader is common and the set of
    /// operators it understands is a single list.
    ///
    /// Ankama writes the whole thing as one string per quest, and the grammar was measured over all
    /// 1,976 of them rather than assumed:
    ///
    /// <code>
    ///   condition := term | condition '&amp;' condition | condition '|' condition | '(' condition ')'
    ///   term      := OP CMP VALUE (',' VALUE)*
    ///   OP        := two letters      29 of them in use
    ///   CMP       := '=' | '!' | '&gt;' | '&lt;'
    /// </code>
    ///
    /// Three things about that are easy to get wrong and were checked:
    ///
    /// <b>Not-equal is <c>!</c> on its own, never <c>!=</c>.</b> <c>Qa!496</c> means "quest 496 is
    /// not active". There is not one <c>!=</c> in the whole file, and reading <c>Qf!55</c> as if the
    /// <c>!</c> were noise would inverte 236 conditions.
    ///
    /// <b>There are brackets, and they nest three deep.</b> 170 quests use them. A flat left-to-right
    /// reader would get <c>PL&gt;8&amp;((Pm=69207040&amp;(Qc=715|Qo=4594))|…)</c> wrong.
    ///
    /// <b>Precedence turns out not to matter, but is implemented anyway.</b> 168 conditions mix
    /// <c>&amp;</c> and <c>|</c>, and in every one of them the mixing is bracketed explicitly — only
    /// 7 have a bare <c>|</c> at the top level and those bracket their <c>&amp;</c> groups. So the
    /// data never relies on it. <c>&amp;</c> binds tighter here, as it does in C, which is the
    /// reading that agrees with all 168.
    ///
    /// <b>What it cannot judge, it lets through and says so.</b> Seven operators are understood —
    /// <c>PL Qf Qa Qc Qo Pm OA</c> — and the six that quests use cover every term of 935 of the
    /// 1,976 start conditions. The rest
    /// need alignment, guild rank, server flags and other things this emulator does not model at
    /// all. Refusing those would mean 53% of quests could never be started by anybody, which is a
    /// worse answer than offering them slightly early; the terms that <em>are</em> understood in
    /// those conditions are still enforced, and <see cref="CriterionVerdict.Skipped"/> names the
    /// ones that were not.
    /// </remarks>
    public static class QuestCriterion
    {
        /// <summary>Judges a condition. An empty condition is met.</summary>
        public static CriterionVerdict Judge(string criterion, IQuestFacts facts)
        {
            var skipped = new List<string>();
            if (string.IsNullOrWhiteSpace(criterion))
            {
                return new CriterionVerdict(true, skipped);
            }

            var reader = new Reader(criterion, facts, skipped);
            try
            {
                bool met = reader.Condition();

                // Trailing rubbish means the string was not what this grammar says it is, and a
                // half-read condition that reports a clean verdict is the worst of both.
                if (!reader.Done)
                {
                    skipped.Add(criterion.Substring(reader.At));
                    return new CriterionVerdict(true, skipped, broke: true);
                }

                return new CriterionVerdict(met, skipped);
            }
            catch (FormatException)
            {
                skipped.Add(criterion);
                return new CriterionVerdict(true, skipped, broke: true);
            }
        }

        /// <summary>Whether this engine knows what an operator means.</summary>
        public static bool Understands(string op) => op switch
        {
            "PL" or "Qf" or "Qa" or "Qc" or "Qo" or "Pm" or "OA" => true,
            _ => false,
        };

        /// <summary>
        /// A recursive-descent reader over one condition.
        /// </summary>
        /// <remarks>
        /// A struct-free nested class rather than a regular expression because of the brackets: a
        /// pattern can find the terms but cannot tell which of them an <c>|</c> three levels down
        /// belongs to, and that is the whole difficulty.
        /// </remarks>
        private sealed class Reader
        {
            private readonly string _text;
            private readonly IQuestFacts _facts;
            private readonly List<string> _skipped;

            public Reader(string text, IQuestFacts facts, List<string> skipped)
            {
                _text = text;
                _facts = facts;
                _skipped = skipped;
            }

            public int At { get; private set; }

            public bool Done => At >= _text.Length;

            /// <summary>The whole thing: a run of AND groups joined by OR.</summary>
            public bool Condition()
            {
                bool value = AndGroup();
                while (Peek() == '|')
                {
                    At++;

                    // No short-circuit: the right-hand side is read even when the answer is already
                    // known, so that whatever it skips still gets reported. Skipping quietly is the
                    // failure this class is written to avoid.
                    bool right = AndGroup();
                    value = value || right;
                }

                return value;
            }

            private bool AndGroup()
            {
                bool value = Single();
                while (Peek() == '&')
                {
                    At++;
                    bool right = Single();
                    value = value && right;
                }

                return value;
            }

            private bool Single()
            {
                if (Peek() == '(')
                {
                    At++;
                    bool inside = Condition();
                    if (Peek() != ')') throw new FormatException("unclosed bracket");
                    At++;
                    return inside;
                }

                return Term();
            }

            private bool Term()
            {
                int start = At;

                // Two letters, and they are case sensitive: Pa and PA are different operators, and
                // so are Pj and PJ.
                if (At + 2 > _text.Length || !char.IsLetter(_text[At]) || !char.IsLetter(_text[At + 1]))
                {
                    throw new FormatException("expected an operator");
                }

                string op = _text.Substring(At, 2);
                At += 2;

                if (Done) throw new FormatException("operator with nothing after it");
                char comparison = _text[At];

                // Four comparisons, and then E, which appears exactly twice in the whole catalogue
                // — POE14271 and POE11563 — where every other PO term uses =, ! or >. It is
                // Ankama's typo, not a fifth comparison, and it is accepted here only so that two
                // conditions are not thrown away over it. PO is not understood anyway.
                if (comparison is not ('=' or '!' or '>' or '<' or 'E'))
                {
                    throw new FormatException("bad comparison");
                }

                At++;

                // One value, or several separated by commas. Usually a number, but not always:
                // PJ>a,199 in quest 1751 starts with a letter. Both operators that do this are
                // unknown to this engine, so the value is read as a token and only turned into a
                // number when something is going to use it.
                var values = new List<string>();
                while (true)
                {
                    values.Add(Value());
                    if (Peek() != ',') break;
                    At++;
                }

                if (!Understands(op))
                {
                    string term = _text.Substring(start, At - start);
                    if (!_skipped.Contains(term)) _skipped.Add(term);
                    return true;
                }

                // Every understood operator carries a plain number in all 1,976 conditions. One
                // that did not would be a change in the format, and saying so is better than
                // quietly judging it as zero.
                if (!long.TryParse(values[0], out long value))
                {
                    throw new FormatException($"{op} with a value that is not a number");
                }

                return Judge(op, comparison, value);
            }

            /// <summary>Everything up to the next thing that ends a value.</summary>
            private string Value()
            {
                int start = At;
                while (!Done && _text[At] is not ('&' or '|' or '(' or ')' or ',')) At++;
                if (At == start) throw new FormatException("expected a value");

                return _text.Substring(start, At - start);
            }

            private bool Judge(string op, char comparison, long value)
            {
                switch (op)
                {
                    case "PL":
                        // Strictly greater and strictly less. PL>29 is level 30 and up: the level
                        // the client shows on the quest card is one more than the number here.
                        return Compare(_facts.Level, comparison, value);

                    case "Pm":
                        return Compare(_facts.MapId, comparison, value);

                    // Qf is "finished". Qc is the same question asked by a different name: it holds
                    // quest ids too — 70 of 70 — and it turns up beside Qa in conditions that read
                    // "(Qa=890|Qc=890)", which is "quest 890 is under way or has been done". Read
                    // as anything else that pair means nothing.
                    case "Qf":
                    case "Qc":
                        return Truth(_facts.Finished((int)value), comparison);

                    case "Qa":
                        return Truth(_facts.Active((int)value), comparison);

                    // Qo holds objective ids — 116 of 116 — and its comparison is usually '>'
                    // rather than '='. Both are read as "that objective is ticked off": the
                    // conditions it appears in are of the form "(Qa=1523&Qo>8635)|Qf=1523", which
                    // is a quest under way and past a given point, or already over.
                    case "Qo":
                        return Truth(_facts.ObjectiveDone((int)value), comparison);

                    // OA is "achievement obtained", and it is the commonest operator in the whole
                    // achievement catalogue: 2,157 objectives use it. Achievement 8520 is nothing
                    // but (OA=8518) and (OA=8519) — a badge for having the two badges.
                    case "OA":
                        return Truth(_facts.AchievementDone((int)value), comparison);

                    default:
                        return true;
                }
            }

            private static bool Compare(long have, char comparison, long value) => comparison switch
            {
                '=' => have == value,
                '!' => have != value,
                '>' => have > value,
                '<' => have < value,
                _ => true,
            };

            /// <summary>A yes-or-no fact, where '!' asks for the opposite.</summary>
            private static bool Truth(bool have, char comparison) => comparison == '!' ? !have : have;

            private char Peek() => Done ? '\0' : _text[At];
        }
    }
}
