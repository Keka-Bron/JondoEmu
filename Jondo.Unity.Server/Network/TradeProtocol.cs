using System.Collections.Generic;
using Jondo.Unity.Protocol;
using Jondo.Unity.Server.Managers;

namespace Jondo.Unity.Server.Network
{
    /// <summary>
    /// The messages of a trade between two players, as the two captures of Intercambio/ have them:
    /// "intercambio completo mandando peticion a otro-exito" (the one asking) and "peticion recibida
    /// de intercambio rechazada-luego peticion recibida aceptada e intercambio completo exitoso"
    /// (the one asked). The items laid down (kfb) and the ready flags (kgt) are the workshop's own
    /// messages, <see cref="WorkshopProtocol.BuildAdded"/> and <see cref="WorkshopProtocol.BuildReady"/>.
    /// </summary>
    public static class TradeProtocol
    {
        /// <summary>The kind of exchange a trade between two players is: 1, in kfz f4 and kbg f3.</summary>
        public const int PlayerTrade = 1;

        /// <summary>How the trade window closes (khd f3): 11, as a shop's or a chest's.</summary>
        public const int Closed = 11;

        /// <summary>
        /// Someone asks someone else to trade (kfz), sent the same to both:
        /// { f1: who asks, f2: who is asked, f4: 1 }.
        /// </summary>
        public static byte[] BuildRequested(long source, long target)
            => Pb.New().Var(1, source).Var(2, target).Var(4, PlayerTrade).Build();

        /// <summary>
        /// The trade window opens (kbg), the same to both:
        /// { f2: the asker's pods capacity, f3: 1, f4: the asked's capacity, f5: the asker's pods
        /// carried, f6: the asker, f7: the asked, f8: the asked's pods carried }. Which is which
        /// comes from the two captures seen from either side: f6 is the one who asked in both.
        /// </summary>
        public static byte[] BuildStarted(long source, long sourceCapacity, long sourceCarried,
                                          long target, long targetCapacity, long targetCarried)
            => Pb.New()
                .VarIfNotZero(2, sourceCapacity)
                .Var(3, PlayerTrade)
                .VarIfNotZero(4, targetCapacity)
                .VarIfNotZero(5, sourceCarried)
                .Var(6, source)
                .Var(7, target)
                .VarIfNotZero(8, targetCarried)
                .Build();

        /// <summary>The kamas one side puts in (ket): { f1: kamas, f3: true when it is the other one's }.</summary>
        public static byte[] BuildKamas(long kamas, bool remote)
            => Pb.New().VarIfNotZero(1, kamas).VarIfNotZero(3, remote ? 1 : 0).Build();

        /// <summary>
        /// One side's pods once the trade is done (keq): { f1: capacity, f2: true when it is the other
        /// one's, f3: carried }. The asker's goes first, then the asked's.
        /// </summary>
        public static byte[] BuildWeight(long capacity, long carried, bool remote)
            => Pb.New().VarIfNotZero(1, capacity).VarIfNotZero(2, remote ? 1 : 0).VarIfNotZero(3, carried).Build();

        /// <summary>
        /// The trade window closes (khd): { f1: true when the trade went through, f3: 11 }. A refusal
        /// or a cancel is the f3 alone.
        /// </summary>
        public static byte[] BuildClosed(bool done)
            => Pb.New().VarIfNotZero(1, done ? 1 : 0).Var(3, Closed).Build();

        /// <summary>
        /// A stack's new quantity (ivj): { f3 { f2: uid, f3: quantity } }. What was given out of a
        /// stack, or what was stacked onto one of the same.
        /// </summary>
        public static byte[] BuildQuantity(long uid, int quantity)
            => Pb.New().Msg(3, Pb.New().Var(2, uid).Var(3, quantity)).Build();

        /// <summary>A stack laid down, taken back or changed, seen from either side (kfb).</summary>
        public static byte[] BuildLaid(int gid, IEnumerable<Equipment.ItemEffect> effects, int quantity, long uid, bool remote)
            => WorkshopProtocol.BuildAdded(gid, effects, quantity, uid, withFloat: false, remote: remote);
    }
}
