using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Text.Json;

namespace Jondo.Unity.Server.Network
{
    /// <summary>
    /// The world-entry sequence, read from <c>content/world/entry.json</c> instead of the .bin.
    /// </summary>
    /// <remarks>
    /// Entering the world used to be a recording played back: three binary files taken from one
    /// real session and replayed to everybody. That is how the friends list, the guild, the spouse,
    /// the titles, the quest journal (twice — <c>idu</c>, and then <c>idr</c> carrying the same
    /// thing) and the achievements all reached players they did not belong to. Each was found late
    /// and by accident, because a blob of bytes cannot be reviewed and nobody can diff it.
    ///
    /// The bytes are the same. What changes is that they are now written down field by field, in a
    /// file git can show a diff of, so the next thing that belongs to somebody else can be seen
    /// before it ships rather than after. <c>tools/decode_world_entry.py</c> produced it and proves
    /// the point on every run: all 355 captured frames re-encode to the bytes they came from, so
    /// this is not a summary of the capture, it is the capture, legible.
    ///
    /// <b>This is a step, not the destination.</b> 70 frames are still structure copied out of
    /// somebody's session. None of them carries a name or an id — that was checked one at a time —
    /// but the list should shrink as each message learns to build itself, and the file is laid out
    /// so that removing one is a one-line diff.
    /// </remarks>
    public static class WorldEntryContent
    {
        public const string AuthoredFile = "world/entry.json";

        private static readonly Dictionary<string, List<byte[]>> _blocks
            = new Dictionary<string, List<byte[]>>(StringComparer.Ordinal);

        /// <summary>How many frames a block holds. Zero when the file was not read.</summary>
        public static int Count(string block)
            => _blocks.TryGetValue(block, out var frames) ? frames.Count : 0;

        public static IReadOnlyList<byte[]> Frames(string block)
            => _blocks.TryGetValue(block, out var frames)
                ? frames
                : (IReadOnlyList<byte[]>)Array.Empty<byte[]>();

        public static bool Ready => _blocks.Count > 0;

        /// <summary>
        /// Reads the file. A missing one is reported and leaves every block empty.
        /// </summary>
        public static void Load(string path, Action<string>? complain = null)
        {
            _blocks.Clear();
            if (!File.Exists(path))
            {
                complain?.Invoke($"[World] No está {path}: nadie podrá entrar al mundo.");
                return;
            }

            try
            {
                using var doc = JsonDocument.Parse(File.ReadAllText(path));
                if (!doc.RootElement.TryGetProperty("blocks", out var blocks)) return;

                foreach (var block in blocks.EnumerateObject())
                {
                    var frames = new List<byte[]>();
                    foreach (var row in block.Value.EnumerateArray())
                    {
                        byte[]? frame = Frame(row);
                        if (frame != null) frames.Add(frame);
                    }

                    _blocks[block.Name] = frames;
                }
            }
            catch (Exception ex)
            {
                complain?.Invoke($"[World] {path} no se ha podido leer: {ex.Message}");
                _blocks.Clear();
            }
        }

        /// <summary>
        /// One row back into the bytes it came from.
        /// </summary>
        /// <remarks>
        /// A row that says <c>built</c> is one the server makes for itself from the database, and
        /// it still has to be emitted here: the frame goes down the same pipeline as the rest, and
        /// <see cref="WorldEntry"/> recognises it by its opcode and swaps the body. Leaving it out
        /// would silently drop the character's own jobs, spells and inventory.
        /// </remarks>
        private static byte[]? Frame(JsonElement row)
        {
            // A frame the decoder could not take apart is kept whole rather than lost.
            if (row.TryGetProperty("frame", out var whole) && whole.ValueKind == JsonValueKind.String)
            {
                return Convert.FromBase64String(whole.GetString() ?? "");
            }

            if (!row.TryGetProperty("opcode", out var opcode) || opcode.ValueKind != JsonValueKind.String)
                return null;

            var inner = Pb.New().Bytes(1, Encoding.ASCII.GetBytes("type.ankama.com/" + opcode.GetString()));

            if (row.TryGetProperty("payload", out var payload))
            {
                Write(inner, payload);
            }
            else if (row.TryGetProperty("built", out _))
            {
                // The body is replaced further down, but the field has to exist: a frame whose f2
                // is missing entirely is a different message on the wire from one whose f2 is
                // empty, and the capture always carries it.
                inner.Bytes(2, Array.Empty<byte>());
            }

            // The wrappers, outermost last. The envelope is two deep — f1 { f1 { f1: url, f2: body } }
            // — and the numbers are kept rather than assumed because the outer one says whether the
            // server is pushing or answering, and the client does not accept one where it wants the
            // other.
            var built = inner;
            if (row.TryGetProperty("path", out var path) && path.ValueKind == JsonValueKind.Array)
            {
                var numbers = new List<int>();
                foreach (var step in path.EnumerateArray())
                {
                    if (step.TryGetInt32(out int number)) numbers.Add(number);
                }

                for (int i = numbers.Count - 1; i >= 0; i--)
                {
                    built = Pb.New().Msg(numbers[i], built);
                }
            }

            return built.Build();
        }

        /// <summary>One field of the tree onto the builder, in the order the file carries it.</summary>
        /// <remarks>
        /// Order matters and is not decoration: protobuf lets fields arrive in any order, but these
        /// bytes are compared against the capture frame by frame, and a builder that sorted by
        /// field number — which <c>ProtoMessage.ToByteArray</c> does — would produce a different
        /// message that happens to mean the same thing. The test would then fail for a reason that
        /// looks like a bug and is not.
        /// </remarks>
        private static void Write(Pb pb, JsonElement field)
        {
            if (field.ValueKind == JsonValueKind.Array)
            {
                foreach (var one in field.EnumerateArray()) Write(pb, one);
                return;
            }

            if (!field.TryGetProperty("n", out var n) || !n.TryGetInt32(out int number)) return;

            if (field.TryGetProperty("v", out var varint) && varint.TryGetUInt64(out ulong value))
            {
                pb.Var(number, unchecked((long)value));
                return;
            }

            if (field.TryGetProperty("msg", out var message))
            {
                var body = Pb.New();
                Write(body, message);
                pb.Msg(number, body);
                return;
            }

            if (field.TryGetProperty("raw", out var raw) && raw.ValueKind == JsonValueKind.String)
            {
                pb.Bytes(number, Convert.FromBase64String(raw.GetString() ?? ""));
                return;
            }

            if (field.TryGetProperty("i64", out var i64) && i64.ValueKind == JsonValueKind.String)
            {
                pb.Fixed64(number, Convert.FromBase64String(i64.GetString() ?? ""));
                return;
            }

            if (field.TryGetProperty("i32", out var i32) && i32.ValueKind == JsonValueKind.String)
            {
                pb.Fixed32(number, Convert.FromBase64String(i32.GetString() ?? ""));
            }
        }
    }
}
