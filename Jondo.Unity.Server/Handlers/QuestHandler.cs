using System;
using System.Net.Sockets;
using System.Threading.Tasks;
using Jondo.Unity.Protocol;
using Jondo.Unity.Server.Managers;
using Jondo.Unity.Server.Network;

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
    /// <b>The client is trusted about objectives, and it has to be.</b> <c>idw</c> is the client
    /// saying an objective is finished, and 5,670 of the game's 15,547 objectives are free text —
    /// "click the thing" — which the server never sees happen. <see cref="QuestLog.Tick"/> is what
    /// keeps that from being a hole: it accepts an objective only if it belongs to the step the
    /// character is actually on, so the worst a lying client can do is finish a quest it already
    /// has, in the order that quest was written.
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

            await Quests.TickAsync(stream, questId, objectiveId);
        }

        /// <summary>
        /// The client asks about one of its quests (iec): f1 quest.
        /// </summary>
        /// <remarks>
        /// Seen 7 times across 4 captures, always right after a quest starts, and always carrying
        /// the quest that just started plus a second field of -1. Answering it with the current
        /// step is what the client is plainly waiting for; the -1 is not understood and is left
        /// alone rather than given a meaning it has not earned.
        /// </remarks>
        public static async Task DetailAsync(NetworkStream stream, byte[] payload)
        {
            byte[]? iec = ConnectionProtocol.ReadPayload(payload, Op.Iec);
            if (iec == null) return;

            int questId = Field(iec, 1);
            if (questId == 0) return;

            await Quests.SendStepAsync(stream, questId);
        }

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
