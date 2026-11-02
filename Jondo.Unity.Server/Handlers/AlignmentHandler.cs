using System;
using System.Linq;
using System.Net.Sockets;
using System.Threading.Tasks;
using Jondo.Unity.Protocol;
using Jondo.Unity.Server.Managers;
using Jondo.Unity.Server.Network;

namespace Jondo.Unity.Server.Handlers
{
    /// <summary>
    /// The two NPCs of the alignment (<see cref="Alignments"/>): Chantal Inea, who has one swear
    /// loyalty to Bonta or Brakmar, and Morry Gann, who makes one neutral again for a fee.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Their sentences and replies are the client's own (NPCs 5064 and 7658); which leads to which is
    /// read off their texts, the way the dialogue trees are (content/npcs/dialogues.json), and is
    /// written here because what they offer depends on the character: his side, his grade's fee,
    /// his kamas and his last change.
    /// </para>
    /// <para>
    /// Chantal: «Hola, joven. El conflicto que enfrenta las ciudades de Bonta y Brakmar vuelve a
    /// aflorar...» offers «Jurar lealtad a Bonta.», «Jurar lealtad a Brakmar.» and «Necesito tiempo
    /// para pensar.»; each city asks «¿estás seguro?» with the oath again and «He cambiado de idea.».
    /// To whoever has sworn already she offers «Ya lo he decidido.».
    /// </para>
    /// <para>
    /// Morry Gann: «Quiero abandonar mi alineamiento actual.» to the sworn; under two weeks since
    /// the last change, «Lo siento...»; else the warning, the fees -- each of the ten steps its own
    /// sentence, «¿Cuánto debería pagar en mi situación?» only for his --, the «¿DE VERDAD QUIERES
    /// RENUNCIAR...?», and «Pagar N kamas y firmar...» for his step and his city, which takes the
    /// kamas and leaves him neutral: «Una firma aquí, aquí y aquí...». Without the kamas, «Aunque no
    /// creo que dispongas de suficientes fondos... Lo siento.».
    /// </para>
    /// </remarks>
    public static class AlignmentHandler
    {
        public const int Chantal = 5064, Morry = 7658;

        // Chantal Inea's sentences and replies.
        private const long SwearIntro = 36023, ConfirmBonta = 36024, ConfirmBrakmar = 36025;
        private const long ToBonta = 47609, BackFromBonta = 47610, SwearBonta = 47611,
                           ToBrakmar = 47612, BackFromBrakmar = 47613, SwearBrakmar = 47614,
                           AlreadyDecided = 47615, NeedTime = 47616;

        // Morry Gann's sentences: the ten fees from FirstCost (and again from FirstCostAgain), the
        // twenty signatures from FirstSigned, a step's Bontarian then its Brakmarian.
        private const long Intro = 57403, NoChangeLately = 57404, Warning = 57405, CostRange = 57406,
                           TooSoon = 57407, FirstCost = 57408, ReallySure = 57418, PayAndSign = 57419,
                           NotEnough = 57420, CostRangeAgain = 57421, FirstCostAgain = 57422, FirstSigned = 57432;

        // And his replies: a step's «¿Cuánto...?» and «Gracias...» go two by two, its «Pagar...» and
        // «Marcharte, libre...» four by four, the Bontarian first.
        private const long WantToLeave = 78119, Leave = 78120, KeepListening = 78117, LeaveTooSoon = 78118,
                           WantNeutral = 78114, HowMuch = 78115, Hesitate = 78113, LeaveFees = 78116,
                           FirstAsk = 78122, FirstThanks = 78121, NoLongerFollow = 78144,
                           StayBonta = 78141, StayBrakmar = 78142, LeavePoor = 78145,
                           HowMuchProcedure = 78166, ChangeMind = 78167,
                           FirstAskAgain = 78147, FirstThanksAgain = 78146, FirstPay = 78169, FirstFree = 78168;

        private static readonly (long, long[]) Close = (0, Array.Empty<long>());

        public static bool Owns(int npc) => npc == Chantal || npc == Morry;

        private static long Me => SessionContext.State.CharacterId;
        private static long Now => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

        /// <summary>What the NPC opens on for the character in session.</summary>
        public static (long Question, long[] Replies) Opening(int npc)
        {
            var mine = Alignments.Of(Me);
            if (npc == Chantal) return (SwearIntro, ChantalIntroReplies(mine));
            return (Intro, mine.Sworn ? new[] { WantToLeave, Leave } : new[] { Leave });
        }

        private static long[] ChantalIntroReplies(Alignments.Alignment mine)
            => mine.Sworn ? new[] { AlreadyDecided } : new[] { ToBonta, ToBrakmar, NeedTime };

        /// <summary>A reply: the next sentence and its replies, or a sentence of zero to close.</summary>
        public static async Task<(long Question, long[] Replies)> ReplyAsync(NetworkStream stream, int npc, long reply)
        {
            var mine = Alignments.Of(Me);
            if (npc == Chantal)
            {
                if (reply == ToBonta && !mine.Sworn) return (ConfirmBonta, new[] { SwearBonta, BackFromBonta });
                if (reply == ToBrakmar && !mine.Sworn) return (ConfirmBrakmar, new[] { SwearBrakmar, BackFromBrakmar });
                if (reply == BackFromBonta || reply == BackFromBrakmar) return (SwearIntro, ChantalIntroReplies(mine));
                if ((reply == SwearBonta || reply == SwearBrakmar) && !mine.Sworn)
                    await SwitchAsync(stream, reply == SwearBonta ? Alignments.Bonta : Alignments.Brakmar);
                return Close;
            }

            if (!mine.Sworn) return Close;
            int step = Alignments.LeaveStep(Alignments.Level());
            bool brakmar = mine.Side == Alignments.Brakmar;
            long fee = Alignments.LeaveCost(step);

            if (reply == WantToLeave)
                return Alignments.MayChange(mine, Now) ? (NoChangeLately, new[] { KeepListening }) : (TooSoon, new[] { LeaveTooSoon });
            if (reply == KeepListening) return (Warning, new[] { WantNeutral, HowMuch, Hesitate });
            if (reply == HowMuch) return (CostRange, new[] { FirstAsk + 2 * step, LeaveFees });
            if (reply == FirstAsk + 2 * step) return (FirstCost + step, new[] { FirstThanks + 2 * step });
            if (reply == WantNeutral) return (ReallySure, new[] { NoLongerFollow, brakmar ? StayBrakmar : StayBonta });
            if (reply == NoLongerFollow)
                return SessionContext.State.Kamas >= fee
                    ? (PayAndSign, new[] { FirstPay + 4 * step + (brakmar ? 2 : 0), HowMuchProcedure, ChangeMind })
                    : (NotEnough, new[] { LeavePoor });
            if (reply == HowMuchProcedure) return (CostRangeAgain, new[] { FirstAskAgain + 2 * step });
            if (reply == FirstAskAgain + 2 * step) return (FirstCostAgain + step, new[] { FirstThanksAgain + 2 * step });
            if (reply == FirstPay + 4 * step + (brakmar ? 2 : 0))
            {
                if (!Alignments.MayChange(mine, Now) || SessionContext.State.Kamas < fee) return Close;
                SessionContext.State.Kamas -= fee;
                DatabaseManager.SaveCurrentCharacter();
                await WriteAsync(stream, ConnectionProtocol.Push(Op.Ivf, ConnectionProtocol.BuildKamas(SessionContext.State.Kamas)));
                await WriteAsync(stream, ConnectionProtocol.Push(Op.Lqn,
                    ConnectionProtocol.BuildSystemMessage(InfoMessages.KamasLost, fee.ToString())));
                await SwitchAsync(stream, Alignments.Neutral);
                return (FirstSigned + 2 * step + (brakmar ? 1 : 0), new[] { FirstFree + 4 * step + (brakmar ? 2 : 0) });
            }
            return Close;
        }

        /// <summary>
        /// The side changed: kept, and the character drawn again with it, his sheet and rank too.
        /// </summary>
        /// <remarks>
        /// Leaving a city forgets what was done for its militia, as Morry Gann warns before the
        /// signature: «¡Perderás todo lo que has dedicado a la milicia de tu ciudad, ya sea Bonta o
        /// Brakmar! No podrás dar marcha atrás.» -- «tu perdras ton grade au sein de ta milice et tous
        /// les avantages qui sont liés». Its alignment and order quests go back to not done, so the
        /// level and the rank start again from nothing if he ever swears to it again.
        /// </remarks>
        private static async Task SwitchAsync(NetworkStream stream, int side)
        {
            int left = Alignments.CriterionSide(Me);
            if (side == Alignments.Neutral && left != 0)
            {
                foreach (int quest in Alignments.QuestsOfSide(left)) Quests.Forget(quest);
            }

            Alignments.Set(Me, side, Now);
            HeadHunts.Forget(Me);
            await SlotLookHandler.RedrawAsync(stream);
            await WriteAsync(stream, ConnectionProtocol.Push(Op.Kub, ConnectionProtocol.BuildCharacteristics()));
            await WriteAsync(stream, ConnectionProtocol.Push(Op.Hod, BuildRank(Alignments.Rank())));
            Console.WriteLine($"[Alignment] {SessionContext.State.CharacterName} is now " +
                              (side == Alignments.Bonta ? "Bontarian" : side == Alignments.Brakmar ? "Brakmarian" : "neutral") + ".");
        }

        /// <summary>
        /// The alignment tab's PvP switch (hnu { 1 on }): the sheet with its new status, and the
        /// character drawn again for the map, wings and all.
        /// </summary>
        /// <remarks>
        /// The client's own: AlignmentUi.OnPvPChange raises zn with the switch's new state, which
        /// the alignment frame (fhb) sends as hnu -- f1 true to switch it on, nothing to switch it
        /// off. Neither the captures nor the client say what else the real server answers; the
        /// tab reads the status back from the sheet (OnCharacterStatsList), so the sheet is sent.
        /// A neutral one has no PvP to switch.
        /// </remarks>
        public static async Task PvpAsync(NetworkStream stream, byte[] payload)
        {
            byte[]? hnu = ConnectionProtocol.ReadPayload(payload, "hnu");
            if (hnu == null || !Alignments.Of(Me).Sworn) return;
            bool on = ProtoMessage.Parse(hnu).Fields.Any(f => f.FieldNumber == 1 && f.WireType == 0 && f.VarIntValue != 0);
            // A prey whose hunt is open cannot fold his wings: «No puedes ocultar tu alineamiento en
            // una zona de agresión», and the sheet puts the switch back on.
            if (!on && HeadHunts.HuntedNow(Me))
            {
                await WriteAsync(stream, ConnectionProtocol.Push(Op.Lqn,
                    ConnectionProtocol.BuildInfoMessage(InfoMessages.Warning, HeadHunts.CannotHide)));
                await WriteAsync(stream, ConnectionProtocol.Push(Op.Kub, ConnectionProtocol.BuildCharacteristics()));
                return;
            }

            Alignments.SetPvp(Me, on);
            await WriteAsync(stream, ConnectionProtocol.Push(Op.Kub, ConnectionProtocol.BuildCharacteristics()));
            await SlotLookHandler.RedrawAsync(stream);
            Console.WriteLine($"[Alignment] {SessionContext.State.CharacterName} switches PvP {(on ? "on" : "off")}.");
        }

        /// <summary>
        /// A quest finished: an alignment quest raises the level the sheet shows, an order's
        /// apprenticeship gives the rank its alignment tab shows (<see cref="Alignments.Progression"/>).
        /// </summary>
        public static async Task AfterQuestAsync(NetworkStream stream, int questId)
        {
            var (level, rank) = Alignments.Moves(questId);
            if (level)
            {
                await WriteAsync(stream, ConnectionProtocol.Push(Op.Kub, ConnectionProtocol.BuildCharacteristics()));
                Console.WriteLine($"[Alignment] {SessionContext.State.CharacterName}'s alignment level is {Alignments.Level()}.");
            }
            if (rank)
            {
                await WriteAsync(stream, ConnectionProtocol.Push(Op.Hod, BuildRank(Alignments.Rank())));
                Console.WriteLine($"[Alignment] {SessionContext.State.CharacterName}'s rank is {Alignments.Rank()}.");
            }
        }

        /// <summary>
        /// The rank (hod): f1 the client's AlignmentRanks id, left out at 0 as on a new character.
        /// f2, a map of bool to int, and f3, a bool, never travel in the captures; fhc reads f1 alone.
        /// </summary>
        public static byte[] BuildRank(int rank) => Pb.New().VarIfNotZero(1, rank).Build();

        private static Task WriteAsync(NetworkStream stream, byte[] frame)
            => Jondo.Protocol.NetworkMessage.WriteFrameAsync(stream, frame);
    }
}
