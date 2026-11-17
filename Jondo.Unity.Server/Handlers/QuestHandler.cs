using System;
using System.Net.Sockets;
using System.Threading.Tasks;
using Jondo.Unity.Protocol;
using Jondo.Unity.Server.Managers;
using Jondo.Unity.Server.Network;
using Jondo.Unity.World.Quests;

namespace Jondo.Unity.Server.Handlers
{
    /// <summary>
    /// The three things the client says about quests.
    /// </summary>
    /// <remarks>
    /// All three shapes were read off the captures, and the reading is not a guess: across the 401
    /// files, every one of the 448 <c>idu</c> frames names a step that really belongs to the quest
    /// it names, and every one of the 1,479 objectives really belongs to that step. The
    /// repository's own notes filed <c>ieo</c>/<c>idu</c> as interactive-element traffic and
    /// <c>idz</c>/<c>idw</c> as connection extras; that was an old guess and it is wrong.
    ///
    /// <b>The client is trusted about some objectives, and it has to be.</b> <c>idw</c> is the client
    /// saying an objective is finished, and 5,670 of the game's 15,547 objectives are free text —
    /// "click the thing" — which the server never sees happen. Two things keep that from being a
    /// hole: <see cref="QuestClaims"/> takes the client's word only for a free-text objective nothing
    /// on the server watches, and <see cref="QuestLog.Tick"/> only for one of the step the character
    /// is actually on.
    /// </remarks>
    public static class QuestHandler
    {
        /// <summary>
        /// The client asks where a quest has got to (ieo). Answered with idu.
        /// </summary>
        public static async Task StepAsync(NetworkStream stream, byte[] payload)
        {
            byte[]? ieo = ConnectionProtocol.ReadPayload(payload, Op.Ieo);
            if (ieo == null) return;

            // Field 2, not 1: measured over 195 frames. Reading field 1 would answer about
            // whatever happened to be there and look like it worked.
            int questId = Field(ieo, 2);
            if (questId == 0) return;

            await Quests.SendStepAsync(stream, questId);
        }

        /// <summary>
        /// The client says an objective is done (idw): f1 quest, f2 objective.
        /// </summary>
        public static async Task ObjectiveAsync(NetworkStream stream, byte[] payload)
        {
            byte[]? idw = ConnectionProtocol.ReadPayload(payload, Op.Idw);
            if (idw == null) return;

            int questId = Field(idw, 1);
            int objectiveId = Field(idw, 2);
            if (questId == 0 || objectiveId == 0) return;

            // Only an objective the client alone can know is done (QuestClaims): a fight, an item
            // handed over or anything else the server works out is not taken on its word.
            if (!QuestClaims.ClientMayClaim(Quests.Book?.Objective(objectiveId), Quests.Bindings.Of(objectiveId)))
            {
                Console.WriteLine($"[Quest] {SessionContext.State.CharacterName} claimed objective {objectiveId} " +
                                  $"of quest {questId}, which the client cannot finish. Ignored.");
                return;
            }

            await Quests.TickAsync(stream, questId, objectiveId);
        }

        /// <summary>
        /// The client follows one of its quests (iec): f1 quest, f2 the objective or -1 for all.
        /// </summary>
        /// <remarks>
        /// Seen 7 times across 4 captures, always right after a quest starts -- the client follows a
        /// quest as it starts -- and always with -1: its quest tracker (epw) sends it when a quest
        /// or an objective is followed, and idv when it no longer is (epm.basn). Answered with the
        /// current step, which is what the box draws, and kept so the box comes back on entering
        /// the world (<see cref="FollowedQuests"/>).
        /// </remarks>
        public static async Task DetailAsync(NetworkStream stream, byte[] payload)
        {
            byte[]? iec = ConnectionProtocol.ReadPayload(payload, Op.Iec);
            if (iec == null) return;

            int questId = Field(iec, 1);
            if (questId == 0) return;

            if (Quests.Log?.Active(questId) == true) FollowedQuests.Follow(SessionContext.State.CharacterId, questId);
            await Quests.SendStepAsync(stream, questId);
        }

        /// <summary>
        /// The client no longer follows a quest (idv): f1 quest, f2 -1. The real server answers
        /// nothing («barco hacia vulkania», five of them).
        /// </summary>
        public static void Unfollow(byte[] payload)
        {
            byte[]? idv = ConnectionProtocol.ReadPayload(payload, "idv");
            if (idv == null) return;
            int questId = Field(idv, 1);
            if (questId != 0) FollowedQuests.Unfollow(SessionContext.State.CharacterId, questId);
        }

        /// <summary>The followed quests' box put in a new order (iek { 1 [quests] }); answered with nothing.</summary>
        public static void Reorder(byte[] payload)
        {
            byte[]? iek = ConnectionProtocol.ReadPayload(payload, "iek");
            if (iek == null) return;
            var quests = BankHandler.PackedOrSingle(ProtoMessage.Parse(iek).Fields, 1).Select(q => (int)q).ToList();
            FollowedQuests.Order(SessionContext.State.CharacterId, quests);
        }

        /// <summary>
        /// The «start the quest» button of a document (ido { f1 quest }): a wanted poster, a
        /// book's page. The client's document window sends it (ScrollUI.OnStartQuest), and so
        /// does a quest link inside the text, followed by kla to close the window.
        /// </summary>
        /// <remarks>
        /// «carteles de se busca» presses it on the three posters of Astrub, quests 1563 to 1565,
        /// all three already started: the real server answers lqn { 1, 626 }, «No puedes empezar
        /// una misión ya empezada», and the kld that closes the window. The start itself is
        /// <see cref="Quests.StartAsync"/>, under the same conditions as from an NPC. Only with a
        /// document open: the button exists nowhere else.
        /// </remarks>
        public static async Task StartFromDocumentAsync(NetworkStream stream, byte[] payload)
        {
            byte[]? ido = ConnectionProtocol.ReadPayload(payload, "ido");
            if (ido == null || !SessionContext.State.DocumentOpen) return;

            int questId = Field(ido, 1);
            if (questId == 0) return;

            if (Quests.Log?.Active(questId) == true)
            {
                await Jondo.Protocol.NetworkMessage.WriteFrameAsync(stream, ConnectionProtocol.Push(Op.Lqn,
                    ConnectionProtocol.BuildInfoMessage(InfoMessages.Warning, AlreadyStarted)));
                return;
            }

            if (!await Quests.StartAsync(stream, questId))
                Console.WriteLine($"[Quests] {SessionContext.State.CharacterName} cannot start quest {questId} from a document.");
        }

        /// <summary>Info 626, a warning: «No puedes empezar una misión ya empezada».</summary>
        public const int AlreadyStarted = 626;

        /// <summary>
        /// One varint field, or zero.
        /// </summary>
        /// <remarks>
        /// Walks the fields rather than indexing them. <c>ProtoMessage.Parse</c> is hardened
        /// against a malformed body by stopping and returning what it managed to read, so the
        /// field wanted may simply not be there — and indexing <c>Fields[0]</c> on a truncated
        /// frame is how a handler takes the server down instead of ignoring a bad packet.
        /// </remarks>
        private static int Field(byte[] body, int number)
        {
            foreach (var field in ProtoMessage.Parse(body).Fields)
            {
                if (field.FieldNumber == number && field.WireType == 0)
                {
                    long value = field.VarIntValue;

                    // Quest, step and objective ids all fit in an int. A varint that does not is
                    // not a big id, it is a negative number the client wrote — iec carries -1 —
                    // and letting it through as a wrapped int would look like a real id.
                    return value > 0 && value <= int.MaxValue ? (int)value : 0;
                }
            }

            return 0;
        }
    }
}
