using System;
using System.Collections.Generic;
using Jondo.Unity.Protocol;
using Jondo.Unity.Server.Managers;

namespace Jondo.Unity.Server.Network
{
    /// <summary>
    /// The workshop's messages, craft station and magus table alike, each measured in a 3.6.10.10
    /// capture: the tutorial's ring for a craft, the grinder's rune fusion for a craft of several
    /// and a stack, the two smithmagic sessions for the runes.
    /// </summary>
    public static class WorkshopProtocol
    {
        /// <summary>The position every item in a workshop carries: the bag.</summary>
        private const int Bag = Equipment.Bag;

        /// <summary>A workshop opens (kgq): { f1: skill }.</summary>
        public static byte[] BuildOpened(int skillId) => Pb.New().Var(1, skillId).Build();

        /// <summary>
        /// Something enters the workshop (kfb): { f1 { f1: 63, f5: item }, f2: true when the other
        /// one put it, f3: 0.0 }. The float is written even at zero on a workshop of one's own
        /// (1d 00000000 closes every one of the tutorial's); a commission, a trade and the grinder
        /// send none.
        /// </summary>
        public static byte[] BuildAdded(int gid, IEnumerable<Equipment.ItemEffect> effects, int quantity, long uid,
                                        bool withFloat = true, bool remote = false)
        {
            var kfb = Pb.New().Msg(1, Pb.New().Var(1, Bag).Msg(5, ConnectionProtocol.ItemBody(gid, effects, quantity, uid)));
            if (remote) kfb.Var(2, 1);
            if (withFloat) kfb.Fixed32(3, BitConverter.GetBytes(0f));
            return kfb.Build();
        }

        /// <summary>Something leaves the workshop (kfs): { f1: uid, f2: true when the other one took it }.</summary>
        public static byte[] BuildRemoved(long uid, bool remote = false)
            => Pb.New().Var(1, uid).VarIfNotZero(2, remote ? 1 : 0).Build();

        /// <summary>Something in the workshop changes (kex): { f1: true when the other one did it, f2 { f1: 63, f5: item } }.</summary>
        public static byte[] BuildModified(int gid, IEnumerable<Equipment.ItemEffect> effects, int quantity, long uid,
                                           bool remote = false)
            => Pb.New()
                .VarIfNotZero(1, remote ? 1 : 0)
                .Msg(2, Pb.New().Var(1, Bag).Msg(5, ConnectionProtocol.ItemBody(gid, effects, quantity, uid)))
                .Build();

        /// <summary>How many times the recipe will be crafted (kgl): { f1: count }.</summary>
        public static byte[] BuildCount(int count) => Pb.New().Var(1, count).Build();

        /// <summary>The ingredients make no recipe (kdr, empty): the gift wrap tried with the wrong paper.</summary>
        public static byte[] BuildImpossible() => Array.Empty<byte>();

        /// <summary>
        /// A craft went through (kdr): { f2 { f4: item }, f3: 2 }. One craft reports the whole
        /// item with a uid of its own; several report only the template and how many.
        /// </summary>
        public static byte[] BuildCrafted(int gid, IEnumerable<Equipment.ItemEffect> effects, int count, long uid)
        {
            var item = count == 1
                ? ConnectionProtocol.ItemBody(gid, effects, 1, uid)
                : Pb.New().Var(1, gid).Var(3, count);
            return Pb.New().Msg(2, Pb.New().Msg(4, item)).Var(3, Success).Build();
        }

        /// <summary>
        /// A rune went in or did not (kdr): { f2 { f1: pool change, f3: pool, f4: item }, f3: 1 | 2 }.
        /// The pool change is written even at zero (08 00 in every one of them); the pool only
        /// when there is one.
        /// </summary>
        public static byte[] BuildRuneResult(Forgemagic.Result result, int gid, long uid)
        {
            var f2 = Pb.New().Var(1, (int)result.PoolChange);
            if (result.Pool > 0) f2.Fixed32(3, BitConverter.GetBytes((float)result.Pool));
            f2.Msg(4, ConnectionProtocol.ItemBody(gid, result.Effects, 1, uid));
            return Pb.New().Msg(2, f2).Var(3, result.Succeeded ? Success : Failure).Build();
        }

        /// <summary>Every rune closes with kdb { f2: true }.</summary>
        public static byte[] BuildRuneDone() => Pb.New().Var(2, 1).Build();

        /// <summary>Items that arrive in the bag (itf): { f1 (repeated) { f1: 63, f5: item } }.</summary>
        public static byte[] BuildItemsArrived(IEnumerable<(int Gid, IReadOnlyList<Equipment.ItemEffect> Effects, int Quantity, long Uid)> items)
        {
            var itf = Pb.New();
            foreach (var (gid, effects, quantity, uid) in items)
                itf.Msg(1, Pb.New().Var(1, Bag).Msg(5, ConnectionProtocol.ItemBody(gid, effects, quantity, uid)));
            return itf.Build();
        }

        /// <summary>A stack that grows (itu): { f1 { f2: uid, f3: total } }.</summary>
        public static byte[] BuildStackGrew(long uid, int total)
            => Pb.New().Msg(1, Pb.New().Var(2, uid).Var(3, total)).Build();

        /// <summary>
        /// A stack the workshop took from (ivj): { f2 { f1: left, f2: 1 }, f3 { f2: uid, f3: left } }.
        /// Inside a workshop the ivj carries that f2 as well; the gathering one does not.
        /// </summary>
        public static byte[] BuildStackUsed(long uid, int left)
            => Pb.New()
                .Msg(2, Pb.New().Var(1, left).Var(2, 1))
                .Msg(3, Pb.New().Var(2, uid).Var(3, left))
                .Build();

        /// <summary>
        /// A job goes up a level (isz): { f1 { f2: job, f4 (repeated): its skills }, f3: level }.
        /// A craft skill is { f2 { f1: 100 }, f4: skill }; a gathering one { f1 { f1 { f2: most,
        /// f3: least }, f2: 30 }, f4: skill }, the 30 being the tenths the gesture lasts.
        /// </summary>
        public static byte[] BuildJobLevelUp(int jobId, int level, IEnumerable<(int Skill, bool Gathers, int Least, int Most)> skills)
        {
            var job = Pb.New().Var(2, jobId);
            foreach (var (skill, gathers, least, most) in skills)
            {
                var entry = gathers
                    ? Pb.New().Msg(1, Pb.New().Msg(1, Pb.New().Var(2, most).Var(3, least)).Var(2, Resources.GatherTenths))
                    : Pb.New().Msg(2, Pb.New().Var(1, CraftProbability));
                job.Msg(4, entry.Var(4, skill));
            }
            return Pb.New().Msg(1, job).Var(3, level).Build();
        }

        // ─── A commission ───────────────────────────────────────────────────────────────────

        /// <summary>The magus' role in kbl and kgu.</summary>
        public const int CrafterRole = 10;

        /// <summary>The customer's.</summary>
        public const int CustomerRole = 11;

        /// <summary>kdv's reason when the magus stands too far from the workshop.</summary>
        public const int TooFar = 3;

        /// <summary>A commission waiting for an answer (kgu): { f1: the other, f2: my role, f3: who invited }.</summary>
        public static byte[] BuildCommissionRequested(long other, int role, long inviter)
            => Pb.New().Var(1, other).Var(2, role).Var(3, inviter).Build();

        /// <summary>The invitation cannot be (kdv): { f1: reason }.</summary>
        public static byte[] BuildCommissionImpossible(int reason) => Pb.New().Var(1, reason).Build();

        /// <summary>The magus' window (keg): { f2: skill }.</summary>
        public static byte[] BuildCrafterOpened(int skillId) => Pb.New().Var(2, skillId).Build();

        /// <summary>The customer's window (kgw): { f1: the magus' job level, f2: skill }.</summary>
        public static byte[] BuildCustomerOpened(int crafterLevel, int skillId)
            => Pb.New().Var(1, crafterLevel).Var(2, skillId).Build();

        /// <summary>Somebody else's job (iss): { f1 { job, next, level, floor, experience }, f2: whose }.</summary>
        public static byte[] BuildOtherJob(int jobId, long next, int level, long floor, long experience, long who)
            => Pb.New().Msg(1, Pb.New()
                    .Var(1, jobId).VarIfNotZero(2, next).VarIfNotZero(3, level)
                    .VarIfNotZero(4, floor).VarIfNotZero(5, experience))
                .Var(2, who).Build();

        /// <summary>The customer's offer gains an item (ked): { f3 { f1: 63, f5: item }, f4: true when the other one put it }.</summary>
        public static byte[] BuildOffered(int gid, IEnumerable<Equipment.ItemEffect> effects, int quantity, long uid, bool remote)
            => Pb.New()
                .Msg(3, Pb.New().Var(1, Bag).Msg(5, ConnectionProtocol.ItemBody(gid, effects, quantity, uid)))
                .VarIfNotZero(4, remote ? 1 : 0)
                .Build();

        /// <summary>The customer's offer loses an item (keo): { f1: uid }.</summary>
        public static byte[] BuildUnoffered(long uid) => Pb.New().Var(1, uid).Build();

        /// <summary>Somebody is ready or not (kgt): { f3: true, f4: who }, the f3 left out for "not".</summary>
        public static byte[] BuildReady(bool ready, long who)
            => Pb.New().VarIfNotZero(3, ready ? 1 : 0).Var(4, who).Build();

        /// <summary>What the customer pays (kcl): { f1: kamas }, empty once paid.</summary>
        public static byte[] BuildPayment(long kamas) => Pb.New().VarIfNotZero(1, kamas).Build();

        /// <summary>The chat log's line for a payment (lqs): { f3: 64, f4: "+", f4: amount }.</summary>
        public static byte[] BuildPaymentLine(long amount)
            => Pb.New().Var(3, PaymentLine).Str(4, "+")
                 .Str(4, amount.ToString(System.Globalization.CultureInfo.InvariantCulture)).Build();

        /// <summary>lqn 594, "Pago: {0} kamas.", with "+" and the amount.</summary>
        public static byte[] BuildPaymentInfo(long amount)
            => ConnectionProtocol.BuildInfoMessage(0, PaymentMessage, "+",
                                                   amount.ToString(System.Globalization.CultureInfo.InvariantCulture));

        private const int PaymentLine = 64;
        private const int PaymentMessage = 594;

        // ─── The grinder ────────────────────────────────────────────────────────────────────

        /// <summary>
        /// What each broken item gave (kfp): { f1 (repeated) { f1: uid, f3 (repeated) { f1: rune,
        /// f2: how many }, f4: coefficient, f5: coefficient } }. The two floats are the item's
        /// breaking coefficient: 0.4029 and 0.4029 for the captured amulet.
        /// </summary>
        public static byte[] BuildBroken(IEnumerable<(long Uid, IReadOnlyList<(int Rune, int Quantity)> Runes, float Coefficient)> items)
        {
            var kfp = Pb.New();
            foreach (var (uid, runes, coefficient) in items)
            {
                var entry = Pb.New().Var(1, uid);
                foreach (var (rune, quantity) in runes) entry.Msg(3, Pb.New().Var(1, rune).Var(2, quantity));
                entry.Fixed32(4, BitConverter.GetBytes(coefficient)).Fixed32(5, BitConverter.GetBytes(coefficient));
                kfp.Msg(1, entry);
            }
            return kfp.Build();
        }

        /// <summary>The f3 of kdr.</summary>
        public const int Failure = 1;
        public const int Success = 2;

        /// <summary>The f1 of a craft skill's entry in isz: 100 on all four of the captures'.</summary>
        private const int CraftProbability = 100;
    }
}
