using System;
using System.Collections.Generic;

namespace Jondo.Unity.World.Content
{
    /// <summary>
    /// What a criterion answers. Unknown is not a failure: it means nobody has taught this
    /// evaluator that letter yet, and the caller decides what to do with it -- which is the
    /// whole point of having three answers instead of two. A criterion silently read as false
    /// closes content that should be open, and one read as true opens content that should not.
    /// </summary>
    public enum Answer
    {
        False = 0,
        True = 1,
        Unknown = 2,
    }

    /// <summary>
    /// One condition of a criterion, as the client's data writes it: two letters, an operator
    /// and the arguments behind it, which are one number for most and three for some.
    ///
    ///   PB=1131                     the subarea you are standing in
    ///   RV!7,n1_worldlight,0        a raid variable of namespace 7, by name, against a value
    ///   HA!885                      an alteration the character does not carry
    /// </summary>
    public readonly struct Condition
    {
        public Condition(string code, char op, IReadOnlyList<string> args)
        {
            Code = code; Operator = op; Args = args;
        }

        public string Code { get; }
        public char Operator { get; }
        public IReadOnlyList<string> Args { get; }

        /// <summary>The last argument as a number, which is what every comparison compares.</summary>
        public long Value => Args.Count > 0 && long.TryParse(Args[Args.Count - 1], out long v) ? v : 0;

        public override string ToString() => Code + Operator + string.Join(",", Args);
    }

    /// <summary>
    /// The criterion language the client's own data is written in, evaluated.
    ///
    /// It is one small grammar and a table of letters. The grammar is here; the letters are the
    /// caller's, because what "PB" or "RV" mean depends on who is asking -- a raid knows its own
    /// variables, a quest knows the character's. Measured on the strings the client ships:
    ///
    ///   (PB=1131&amp;RV!7,n1_worldlight,0)|(PB=1132&amp;RV!7,n2_worldlight,0)|…
    ///        every monster of the Gigalodón raid, in its aggressiveImmunityCriterion
    ///   HA!885&amp;RV&gt;7,Raid_Score,9999
    ///        what the bosses of that raid drop, gated on the score
    ///   RV&lt;7,Raid_Score,5000
    ///        the raid chest, which changes its look by score
    ///   (HA=50|HS=3383)&amp;Az=1&amp;Pm!28049666
    ///        an ordinary drop, to show that the same grammar runs the rest of the game
    ///
    /// The operators are AND (&amp;) and OR (|), with AND binding tighter, and parentheses. The
    /// data writes its parentheses, so precedence rarely decides anything, but it is written
    /// down rather than assumed.
    /// </summary>
    public static class Criterion
    {
        /// <summary>Answers one condition, or Unknown when it does not know the letters.</summary>
        public delegate Answer Resolver(Condition condition);

        /// <summary>
        /// Evaluates the criterion. An empty one is True: that is what the data means by a blank
        /// field, and most of the catalogue's criteria are blank.
        /// </summary>
        public static Answer Evaluate(string criterion, Resolver resolver)
        {
            if (string.IsNullOrWhiteSpace(criterion)) return Answer.True;
            int at = 0;
            var answer = Or(criterion, ref at, resolver);
            return at >= criterion.Length ? answer : Answer.Unknown;   // sobra texto: no se entendió
        }

        /// <summary>The same, with Unknown counted as the caller says.</summary>
        public static bool Met(string criterion, Resolver resolver, bool unknownCounts = false)
        {
            var answer = Evaluate(criterion, resolver);
            return answer == Answer.True || (answer == Answer.Unknown && unknownCounts);
        }

        private static Answer Or(string text, ref int at, Resolver resolver)
        {
            var left = And(text, ref at, resolver);
            while (at < text.Length && text[at] == '|')
            {
                at++;
                var right = And(text, ref at, resolver);
                left = EitherOf(left, right);
            }
            return left;
        }

        private static Answer And(string text, ref int at, Resolver resolver)
        {
            var left = Term(text, ref at, resolver);
            while (at < text.Length && text[at] == '&')
            {
                at++;
                var right = Term(text, ref at, resolver);
                left = BothOf(left, right);
            }
            return left;
        }

        private static Answer Term(string text, ref int at, Resolver resolver)
        {
            if (at < text.Length && text[at] == '(')
            {
                at++;
                var inside = Or(text, ref at, resolver);
                if (at < text.Length && text[at] == ')') at++;
                return inside;
            }

            int start = at;
            while (at < text.Length && text[at] != '&' && text[at] != '|' && text[at] != ')') at++;
            return One(text.Substring(start, at - start), resolver);
        }

        /// <summary>
        /// One condition: the letters up to the operator, the operator, and the rest split by
        /// commas. Anything that does not have that shape is Unknown, not false.
        /// </summary>
        private static Answer One(string text, Resolver resolver)
        {
            text = text.Trim();
            if (text.Length == 0) return Answer.Unknown;

            int op = -1;
            for (int i = 0; i < text.Length; i++)
            {
                if (text[i] == '=' || text[i] == '!' || text[i] == '<' || text[i] == '>' || text[i] == '~')
                {
                    op = i; break;
                }
            }
            if (op <= 0 || op == text.Length - 1) return Answer.Unknown;

            string code = text.Substring(0, op);
            string[] args = text.Substring(op + 1).Split(',');
            return resolver(new Condition(code, text[op], args));
        }

        /// <summary>
        /// AND with three answers: false and anything is false -- even when the other side is
        /// unknown, because an unmet condition already closes the door. Unknown only spreads
        /// when nothing else decides.
        /// </summary>
        private static Answer BothOf(Answer left, Answer right)
        {
            if (left == Answer.False || right == Answer.False) return Answer.False;
            if (left == Answer.Unknown || right == Answer.Unknown) return Answer.Unknown;
            return Answer.True;
        }

        /// <summary>OR, the other way round: one true opens it whatever the other side is.</summary>
        private static Answer EitherOf(Answer left, Answer right)
        {
            if (left == Answer.True || right == Answer.True) return Answer.True;
            if (left == Answer.Unknown || right == Answer.Unknown) return Answer.Unknown;
            return Answer.False;
        }

        /// <summary>
        /// The comparison the operator asks for, in numbers. The client writes "!" for "is not"
        /// and "=" for "is", and the two of them carry most of the catalogue.
        /// </summary>
        public static Answer Compare(char op, long mine, long theirs) => op switch
        {
            '=' => mine == theirs ? Answer.True : Answer.False,
            '!' => mine != theirs ? Answer.True : Answer.False,
            '<' => mine < theirs ? Answer.True : Answer.False,
            '>' => mine > theirs ? Answer.True : Answer.False,
            _ => Answer.Unknown,
        };
    }
}
