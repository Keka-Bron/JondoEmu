using System;
using System.Linq;
using System.Net.Sockets;
using System.Threading.Tasks;
using Jondo.Unity.Launcher;
using Jondo.Unity.Server.Network;

namespace Jondo.Unity.Server.Handlers
{
    /// <summary>
    /// The client's own staff console (Quantum Console): a line typed there arrives as mfb
    /// { f1: the line, f2: a flag }, and the console waits for an answer or says «Server timeout».
    /// It is answered with mez { f1: the line, f2: the text, f3: kind } and then mew { f1: success },
    /// both carrying the request's id. The admin menu (menuadmin.xml) sends its commands here too.
    /// </summary>
    internal static class StaffConsoleHandler
    {
        public static async Task HandleAsync(NetworkStream stream, byte[] payload)
        {
            byte[]? mfb = ConnectionProtocol.ReadPayload(payload, "mfb");
            if (mfb == null) return;
            string line = ProtoMessage.Parse(mfb).Fields
                .Where(f => f.FieldNumber == 1 && f.WireType == 2)
                .Select(f => System.Text.Encoding.UTF8.GetString(f.BytesValue)).FirstOrDefault() ?? "";
            line = line.Trim();
            // Unlike the client's other messages (id -1), the console's carries a request id and
            // waits for the answer that repeats it -- a push, with no id, is a «Server timeout».
            long id = ConnectionProtocol.RequestId(payload);
            Console.WriteLine($"[Staff console] {SessionContext.State.CharacterName}: {line}");

            // The console is the staff's: the same role check as the chat's commands, per command.
            int role = DatabaseManager.GetAccountRole(SessionContext.Current.AccountId);
            if (!Roles.AlMenos(role, Roles.Moderador))
            {
                await AnswerAsync(stream, id, line, "Not allowed.", 1);
                return;
            }

            if (line.Length == 0 || line.Equals("help", StringComparison.OrdinalIgnoreCase))
            {
                await AnswerAsync(stream, id, line, "Commands (the chat's, without the dot): " +
                                  string.Join(", ", CommandHandler.Names.Select(n => n.TrimStart('.'))), 0);
                return;
            }

            // The chat's administrator commands, typed without their dot. What they say back still
            // goes to the chat, where they have always answered.
            bool handled = await CommandHandler.TryHandleAsync(stream, "." + line, 0, SessionContext.Current.AccountId);
            await AnswerAsync(stream, id, line, handled ? $"Done: .{line} (see the chat)" : $"Unknown command: {line}. Type help.",
                              handled ? 0 : 1);
        }

        private static async Task AnswerAsync(NetworkStream stream, long requestId, string line, string text, int kind)
        {
            await Jondo.Protocol.NetworkMessage.WriteFrameAsync(stream, ConnectionProtocol.Answer("mez",
                Pb.New().Str(1, line).Str(2, text).VarIfNotZero(3, kind).Build(), requestId));
            // The mez only prints the text: the pending command ends with the mew { f1: bool } the
            // client's console class handles with a request context; without it, «Server timeout».
            await Jondo.Protocol.NetworkMessage.WriteFrameAsync(stream, ConnectionProtocol.Answer("mew",
                Pb.New().Var(1, kind == 1 ? 0 : 1).Build(), requestId));
        }
    }
}
