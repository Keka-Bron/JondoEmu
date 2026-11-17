using System;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using Jondo.Unity.Server.Bots;

namespace Jondo.Unity.Server.Network
{
    /// <summary>
    /// The test harness's routes for a slot played without the Dofus client
    /// (<see cref="HeadlessClient"/>), and the server's console for whoever drives it. Like every
    /// /api/test route, only on a server started with JONDO_TEST_KEY.
    /// </summary>
    public static partial class ControlApi
    {
        /// <summary>
        /// { character?, autoReady? }: the slot's account in the world without a client, with that
        /// character (the account's first when none is named). Every other test route then plays it
        /// as it plays a real client's.
        /// </summary>
        private static Respuesta TestHeadless(string cuerpo)
        {
            long account = TestAccount();
            string login = TestLoginNow;
            int slot = _testSlot;
            if (SessionRegistry.All().Any(s => s.AccountId == account) && HeadlessClient.Of(slot) == null)
                return Mal(409, "a client is already connected for this slot");
            bool autoReady = !cuerpo.Contains("\"autoReady\":false");
            var (client, error) = Task.Run(() => HeadlessClient.ConnectAsync(slot, account, login, Texto(cuerpo, "character"), autoReady))
                .GetAwaiter().GetResult();
            if (client == null) return Mal(error is "no-characters" || error!.StartsWith("no-character-") ? 404 : 502, error);
            var session = SessionRegistry.FindByCharacter(client.CharacterId);
            return Bien(new
            {
                slot, character = client.CharacterId, name = client.CharacterName,
                map = session?.State.MapId ?? 0, cell = session?.State.CellId ?? 0, autoReady,
                lastFrame = client.LastFrame,
            });
        }

        /// <summary>The slot's headless client out, as a player who closes the game.</summary>
        private static Respuesta TestHeadlessStop() => Bien(new { disconnected = HeadlessClient.Disconnect(_testSlot) });

        /// <summary>
        /// { op, fields?, hex?, waitMs? }: a message sent from the headless client's own socket, as
        /// the client would send it, so it goes through the server's whole socket loop; and the frames
        /// the server sent back while waiting. Fields by number: a number is a varint, a string a
        /// string, a list of numbers a packed field, an object a submessage.
        /// </summary>
        private static Respuesta TestHeadlessSend(string cuerpo)
        {
            var client = HeadlessClient.Of(_testSlot);
            if (client == null) return Mal(404, "no headless client for this slot");
            string op = Texto(cuerpo, "op");
            if (op.Length == 0) return Mal(400, "op");
            byte[] payload;
            try
            {
                using var doc = JsonDocument.Parse(cuerpo);
                string hex = doc.RootElement.TryGetProperty("hex", out var h) && h.ValueKind == JsonValueKind.String ? h.GetString() ?? "" : "";
                payload = hex.Length > 0 ? Convert.FromHexString(hex)
                    : doc.RootElement.TryGetProperty("fields", out var fields) && fields.ValueKind == JsonValueKind.Object
                        ? Message(fields).Build()
                        : Array.Empty<byte>();
            }
            catch (Exception ex) when (ex is JsonException or FormatException or InvalidOperationException)
            {
                return Mal(400, ex.Message);
            }
            int waitMs = (int)Math.Clamp(Numero(cuerpo, "waitMs") is var w && w > 0 ? w : 1000, 0, 15000);
            long mark = client.LastFrame;
            Task.Run(() => client.SendAsync(op, payload)).GetAwaiter().GetResult();
            System.Threading.Thread.Sleep(waitMs);
            return Bien(new { sent = op, bytes = payload.Length, frames = FramesView(client, mark, 200) });
        }

        /// <summary>{ since?, most? }: the frames the server sent the headless client after <c>since</c>.</summary>
        private static Respuesta TestHeadlessFrames(string cuerpo)
        {
            var client = HeadlessClient.Of(_testSlot);
            if (client == null) return Mal(404, "no headless client for this slot");
            int most = (int)Math.Clamp(Numero(cuerpo, "most") is var m && m > 0 ? m : 200, 1, 2000);
            return Bien(new { lastFrame = client.LastFrame, frames = FramesView(client, Numero(cuerpo, "since"), most) });
        }

        /// <summary>{ since? }: the server's console lines after that id, as the launcher's window reads them.</summary>
        private static Respuesta TestLogs(string cuerpo) => new Respuesta(200, ConsoleLogBuffer.GetLogsJson(Numero(cuerpo, "since")));

        private static object FramesView(HeadlessClient client, long since, int most)
            => client.FramesSince(since).TakeLast(most).Select(f => new
            {
                id = f.Id, at = f.At.ToString("HH:mm:ss.fff"), op = f.Op, bytes = f.Payload.Length,
                fields = ProtoMessage.Parse(f.Payload).Compact(400),
            }).ToList();

        /// <summary>A protobuf message from JSON, its fields in the order of their numbers.</summary>
        private static Pb Message(JsonElement fields)
        {
            var pb = Pb.New();
            foreach (var field in fields.EnumerateObject().OrderBy(f => int.Parse(f.Name)))
            {
                int number = int.Parse(field.Name);
                var value = field.Value;
                switch (value.ValueKind)
                {
                    case JsonValueKind.Number: pb.Var(number, value.GetInt64()); break;
                    case JsonValueKind.True: pb.Var(number, 1); break;
                    case JsonValueKind.False: pb.Var(number, 0); break;
                    case JsonValueKind.String: pb.Str(number, value.GetString() ?? ""); break;
                    case JsonValueKind.Object: pb.Msg(number, Message(value)); break;
                    case JsonValueKind.Array:
                        // Numbers are packed, as the client packs its repeated scalars; objects are
                        // repeated submessages, one field each.
                        if (value.EnumerateArray().All(x => x.ValueKind == JsonValueKind.Number))
                            pb.Packed(number, value.EnumerateArray().Select(x => x.GetInt64()));
                        else
                            foreach (var item in value.EnumerateArray())
                            {
                                if (item.ValueKind == JsonValueKind.Object) pb.Msg(number, Message(item));
                                else if (item.ValueKind == JsonValueKind.String) pb.Str(number, item.GetString() ?? "");
                                else if (item.ValueKind == JsonValueKind.Number) pb.Var(number, item.GetInt64());
                            }
                        break;
                    default:
                        throw new InvalidOperationException($"field {number}: {value.ValueKind} is not a protobuf value");
                }
            }
            return pb;
        }
    }
}
