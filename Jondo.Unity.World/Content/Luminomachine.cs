using System;
using System.Collections.Generic;

namespace Jondo.Unity.World.Content
{
    /// <summary>
    /// The luminomachine: the contraption a raid floor's light is bought from, with salt.
    ///
    /// Everything here is read off the client's own data, not invented. The machine is NPC 8007,
    /// whose name is "Luminomáquina"; the fuel is item 32464, "Sal de las profundidades", a raid
    /// resource; and the machine's own reply catalogue spells out the whole rule:
    ///
    ///     "Dejar 1 sal de las profundidades para iluminar la primera franja."
    ///     "Dejar 4 sales de las profundidades para iluminar la segunda franja."
    ///     "Dejar 3 sales de las profundidades para iluminar la segunda franja."
    ///     ...
    ///     "Ir a recoger 6 sales de las profundidades para la franja de luz siguiente."
    ///
    /// Four bands of light, and one more band costs 1, then 3, then 6, then 10 — which is exactly
    /// what the salt itself carries written on its tooltip: "Depositar sal en una luminomáquina
    /// aumenta la intensidad de la luz. Cada tramo de luz adicional requiere más sal (1-3-6-10)".
    /// Jumping several bands at once pays the sum, and the seventy-six replies agree with that
    /// arithmetic down to the last one: 1+3 = 4 to buy two bands from the dark, 3+6+10 = 19 to go
    /// from the first band to the last.
    /// </summary>
    /// <remarks>
    /// WHAT IS NOT MEASURED, and it matters: no capture contains a raid, so nothing here has been
    /// seen on the wire. What is measured is the CONTENT — the machine, the salt, the costs, the
    /// reply ids and the message ids — and that is a great deal, because the client resolves a
    /// reply id to its own text: send the wrong one and the player reads a different sentence.
    ///
    /// Two readings are ours and are marked as such where they are made: which of the five reply
    /// blocks belongs to which floor, and the fact that the machine is asked once rather than
    /// twice (see <see cref="GenericReply"/>).
    /// </remarks>
    public static class Luminomachine
    {
        /// <summary>The NPC the machine is, "Luminomáquina" in the client's own catalogue.</summary>
        public const int NpcId = 8007;

        /// <summary>What feeds it: "Sal de las profundidades", a raid resource (item type 315).</summary>
        public const int SaltItem = 32464;

        /// <summary>The brightest a floor gets: the fourth band, the one the replies call "última".</summary>
        public const int MostLight = 4;

        /// <summary>How many machines there are: one per floor that has light.</summary>
        public const int Machines = 5;

        /// <summary>
        /// The salt one more band costs, from the dark upwards.
        /// </summary>
        /// <remarks>
        /// Written on the salt's own tooltip as "(1-3-6-10)" and confirmed by every one of the
        /// machine's deposit replies. The four "Ir a recoger N" replies are these same four
        /// numbers, which is what makes them the price of ONE band and not of a jump.
        /// </remarks>
        public static readonly IReadOnlyList<int> Steps = new[] { 1, 3, 6, 10 };

        /// <summary>"No tocar la máquina.", the way out that every machine offers.</summary>
        public const long DontTouchReply = 83106;

        /// <summary>
        /// "*chisporrotea, alimentada por la energía de la sal*" — the machine already running.
        /// </summary>
        /// <remarks>
        /// It is the one the template carries as its ambient bubble (dialogData, mood 2), and the
        /// only one of the six whose wording is not "*se pone a temblar y a zumbar*". A machine
        /// that is already burning salt is exactly the one with nothing left to ask for, so it is
        /// what a floor at full light says. Our reading, not a measurement.
        /// </remarks>
        public const long LitMessage = 60275;

        /// <summary>What the machine of a floor says when it still wants salt.</summary>
        /// <remarks>
        /// 60276 to 60280, five messages for five floors, all with the same wording. Which one goes
        /// with which floor cannot be measured -- they are identical -- so they are taken in order.
        /// </remarks>
        public static long MessageFor(int floor)
            => floor >= 1 && floor <= Machines ? LitMessage + floor : LitMessage;

        // ─── The reply catalogue ────────────────────────────────────────────────

        /// <summary>The first of the seventy-five replies that come in five blocks of fifteen.</summary>
        private const long FirstReply = 82839;

        /// <summary>Ten deposits, four "go and fetch" and the bare one, per floor.</summary>
        private const int BlockSize = 15;

        /// <summary>
        /// The ten deposits in the order the machine lists them: every jump from every band.
        /// </summary>
        private static readonly (int From, int To)[] Deposits =
        {
            (0, 1), (0, 2), (0, 3), (0, 4),
            (1, 2), (1, 3), (1, 4),
            (2, 3), (2, 4),
            (3, 4),
        };

        /// <summary>Where the four "Ir a recoger N sales" sit inside a block.</summary>
        private const int FirstFetch = 10;

        /// <summary>
        /// Where the bare "Dejar sal de las profundidades." sits inside a block.
        /// </summary>
        /// <remarks>
        /// It is in the data and we do not send it. It would be the first screen of a two-screen
        /// conversation -- ask to leave salt, then be told how much -- and there is no capture to
        /// say whether the real server does that. One screen with the amounts on it asks the player
        /// for one click instead of two and says strictly more, so that is what goes out. If a
        /// capture ever shows two screens, this is the reply that opens the second.
        /// </remarks>
        private const int GenericReply = 14;

        /// <summary>
        /// The reply id for one slot of one floor's block.
        /// </summary>
        /// <remarks>
        /// The blocks are fifteen consecutive ids each, and the four later ones list the bare
        /// reply last while the FIRST one lists it second. That is not a rule, it is just how the
        /// ids came out, so it is written down rather than derived -- and
        /// <c>LuminomachineTests</c> re-reads all seventy-six texts out of world.db to prove the
        /// table still matches what the client will draw.
        /// </remarks>
        private static long ReplyAt(int floor, int slot)
        {
            long start = FirstReply + (floor - 1) * BlockSize;
            if (floor != 1) return start + slot;

            if (slot == GenericReply) return start + 1;
            if (slot == 0) return start;
            return start + slot + 1;
        }

        /// <summary>The reply that pays its way from one band of light to another.</summary>
        public static long DepositReply(int floor, int from, int to)
        {
            for (int slot = 0; slot < Deposits.Length; slot++)
            {
                if (Deposits[slot].From == from && Deposits[slot].To == to) return ReplyAt(floor, slot);
            }

            return 0;
        }

        /// <summary>The reply that says how much salt the next band would want.</summary>
        public static long FetchReply(int floor, int light)
            => light >= 0 && light < MostLight ? ReplyAt(floor, FirstFetch + light) : 0;

        /// <summary>The salt a jump from one band to another costs: the steps in between, added up.</summary>
        public static int Cost(int from, int to)
        {
            if (from < 0 || to > MostLight || to <= from) return 0;

            int total = 0;
            for (int step = from; step < to; step++) total += Steps[step];
            return total;
        }

        // ─── What to offer, and what an answer meant ────────────────────────────

        /// <summary>One thing the machine is willing to do right now.</summary>
        public readonly struct Choice
        {
            public Choice(int floor, int from, int to, int cost)
            {
                Floor = floor;
                From = from;
                To = to;
                Cost = cost;
            }

            /// <summary>Whose floor's machine this was.</summary>
            public int Floor { get; }

            /// <summary>The band of light it is at now.</summary>
            public int From { get; }

            /// <summary>The band it would be left at, or <see cref="From"/> when nothing is bought.</summary>
            public int To { get; }

            /// <summary>The salt it wants.</summary>
            public int Cost { get; }

            /// <summary>Whether this one actually buys light.</summary>
            public bool Buys => To > From;
        }

        /// <summary>
        /// The replies to put in front of someone standing at a machine.
        /// </summary>
        /// <remarks>
        /// Everything he can afford, cheapest first, and nothing he cannot: the client draws a
        /// reply as a button and a button that cannot work is worse than no button. When he cannot
        /// afford even one more band, the machine's own "Ir a recoger N sales de las profundidades
        /// para la franja de luz siguiente" says how short he is, which is precisely the reply the
        /// data carries for that case.
        ///
        /// At full light there is nothing to offer, and at floors with no light -- the sixth of the
        /// Abyss, which has no variable of its own -- there is no machine to begin with.
        /// </remarks>
        public static IReadOnlyList<long> RepliesFor(int floor, int light, int salt)
        {
            var replies = new List<long>();
            if (floor < 1 || floor > Machines) return replies;

            light = Math.Clamp(light, 0, MostLight);
            if (light < MostLight)
            {
                bool any = false;
                for (int to = light + 1; to <= MostLight; to++)
                {
                    if (salt < Cost(light, to)) continue;
                    replies.Add(DepositReply(floor, light, to));
                    any = true;
                }

                if (!any) replies.Add(FetchReply(floor, light));
            }

            replies.Add(DontTouchReply);
            return replies;
        }

        /// <summary>What the machine says while it is being asked.</summary>
        public static long MessageAt(int floor, int light)
            => light >= MostLight ? LitMessage : MessageFor(floor);

        /// <summary>Whether a reply belongs to a luminomachine at all.</summary>
        public static bool Owns(long reply)
            => reply == DontTouchReply ||
               (reply >= FirstReply && reply < FirstReply + Machines * BlockSize);

        /// <summary>
        /// What a chosen reply meant, or null when it was none of the machine's.
        /// </summary>
        /// <remarks>
        /// A reply that buys nothing -- "No tocar la máquina.", or the one that sends him off to
        /// find more salt -- still comes back as a <see cref="Choice"/> so the caller can close the
        /// conversation instead of letting it fall through to the ordinary handling, which would
        /// look up a dialogue tree this NPC does not have.
        /// </remarks>
        public static Choice? Read(long reply)
        {
            if (reply == DontTouchReply) return new Choice(0, 0, 0, 0);
            if (!Owns(reply)) return null;

            long offset = reply - FirstReply;
            int floor = (int)(offset / BlockSize) + 1;
            int raw = (int)(offset % BlockSize);

            // The first block's bare reply sits second, so everything behind it shifts one back.
            int slot = raw;
            if (floor == 1)
            {
                if (raw == 1) slot = GenericReply;
                else if (raw > 1) slot = raw - 1;
            }

            if (slot < Deposits.Length)
            {
                var jump = Deposits[slot];
                return new Choice(floor, jump.From, jump.To, Cost(jump.From, jump.To));
            }

            if (slot < GenericReply)
            {
                int light = slot - FirstFetch;
                return new Choice(floor, light, light, Cost(light, light + 1));
            }

            return new Choice(floor, 0, 0, 0);
        }
    }
}
