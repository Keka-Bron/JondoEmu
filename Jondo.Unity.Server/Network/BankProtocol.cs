using System;
using System.Collections.Generic;
using System.Globalization;

namespace Jondo.Unity.Server.Network
{
    /// <summary>
    /// The bank's own frames. Everything else the bank says -- an item arriving, an item leaving,
    /// the pods, the purse, the dialogue closing -- is the frame every other storage already sends,
    /// and is built where those are.
    /// </summary>
    /// <remarks>
    /// All of it measured on "Interactivos varios/entrar en banco bonta-abrir cofre
    /// gremio-usarlo-abrir cofre personal del banco.pcapng", the only bank visit in the captures;
    /// frame numbers are positions in <c>hilo.tramas</c> of that file.
    /// </remarks>
    public static class BankProtocol
    {
        /// <summary>
        /// kci's f1 when the bank opens: int.MaxValue, frame 83. The house chest and the bin say
        /// 100 there, the haven bag 2147483647 as well; read as the number of slots, which the bank
        /// does not limit. An inference about the meaning; the value is the capture's.
        /// </summary>
        public const int Slots = int.MaxValue;

        /// <summary>
        /// kci's f3 when the bank opens: 16, frame 83. Which storage it is: 4 a house or guild
        /// chest, 17 the bin, 19 the haven bag chest, 16 the bank (see docs/world.md).
        /// </summary>
        public const int Kind = 16;

        /// <summary>
        /// What the banker's "Quiero consultar mi cofre." carries in the ios, frame 74:
        /// f2 { f1: 63535, f3 { f1: 196 } }. The f3 of a reply is an effect the reply announces --
        /// the kamas mountain's lists 194, 193 and 351 (see
        /// <see cref="ConnectionProtocol.BuildNpcQuestion(long, IEnumerable{long})"/>) -- and 196 is
        /// an effect with no text in the client, so what the client draws with it is not known.
        /// Measured on the Bontarian banker; given to every banker's bank reply by inference.
        /// </summary>
        public const int ConsultEffect = 196;

        /// <summary>
        /// The fee, told once paid: lqn { f2: 20, f4: "1397" }, frame 81. Type 0 (so no f1), text
        /// 20 of the client's table: "Has pagado $quantity{0} kamas para acceder a este cofre."
        /// </summary>
        public const int FeePaid = 20;

        /// <summary>
        /// Type 4, text 10 of the client's own table: "Necesitas por lo menos $quantity{0} kamas
        /// para acceder a tu cofre." NOT in any capture -- nobody was ever short at the counter --
        /// but it is the client's sentence for exactly that, so it is what a player who cannot pay
        /// is told. The type is the table the sentence is in.
        /// </summary>
        public const int FeeUnpaidType = 4;
        public const int FeeUnpaid = 10;

        /// <summary>The bank opens (kci): "08ffffffff071810", frame 83.</summary>
        public static byte[] BuildOpened() => Pb.New().Var(1, Slots).Var(3, Kind).Build();

        /// <summary>
        /// What is in the bank (iwb): every stack in f1, as any storage sends it, and the kamas in
        /// f2.
        /// </summary>
        /// <remarks>
        /// f2 is where the kamas go by the client's own schema -- iwb is { repeated item f1 = 1,
        /// int64 f2 = 2 } in datos/protocolo_3.6.10.10.proto -- and it is the only number the
        /// bank's opening burst has room for (frames 80 to 84). The capture's bank held no kamas,
        /// so its f2 never travels and proto3 has nothing to show; the reading is an inference, and
        /// zero keeps the capture's bytes.
        /// </remarks>
        public static byte[] BuildContent(IEnumerable<Managers.HavenBagStore.StoredItem> items, long kamas)
        {
            byte[] stacks = ConnectionProtocol.BuildStorageContent(items);
            byte[] purse = Pb.New().VarIfNotZero(2, kamas).Build();
            return Join(stacks, purse);
        }

        /// <summary>
        /// A banker's question (ios): the NPC question with the numbers its sentence needs, in f3.
        /// </summary>
        /// <remarks>
        /// ios is { f1: message, f2 (repeated): replies, f3 (repeated string): parameters }; the
        /// greeting's "te costará #1 kamas" is filled with the fee -- "1397", frame 74. Built as the
        /// usual question with the parameters behind, which is where the capture has them.
        /// </remarks>
        public static byte[] BuildQuestion(long messageId, IEnumerable<long> replies,
                                           IReadOnlyDictionary<long, IReadOnlyList<long>>? replyParameters,
                                           IEnumerable<string> messageParameters)
        {
            byte[] question = ConnectionProtocol.BuildNpcQuestion(messageId, replies, replyParameters!);
            var tail = Pb.New();
            foreach (string parameter in messageParameters) tail.Str(3, parameter ?? "");
            return Join(question, tail.Build());
        }

        /// <summary>The fee paid, as the server says it: lqn { f2: 20, f4: fee }, frame 81.</summary>
        public static byte[] BuildFeePaid(long fee)
            => ConnectionProtocol.BuildSystemMessage(FeePaid, fee.ToString(CultureInfo.InvariantCulture));

        /// <summary>The fee that could not be paid. Inference; see <see cref="FeeUnpaid"/>.</summary>
        public static byte[] BuildFeeUnpaid(long fee)
            => ConnectionProtocol.BuildInfoMessage(FeeUnpaidType, FeeUnpaid, fee.ToString(CultureInfo.InvariantCulture));

        private static byte[] Join(byte[] first, byte[] second)
        {
            var both = new byte[first.Length + second.Length];
            Buffer.BlockCopy(first, 0, both, 0, first.Length);
            Buffer.BlockCopy(second, 0, both, first.Length, second.Length);
            return both;
        }
    }
}
