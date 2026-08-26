using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;

namespace Jondo.Unity.World.Content
{
    /// <summary>How far along we are with one kind of packet.</summary>
    /// <remarks>
    /// The ladder matters more than the labels. "Unknown" and "handled" are the only two states the
    /// code can work out on its own — everything in between is what a person learned and has
    /// nowhere else to put, and without somewhere to put it the learning gets done again every few
    /// months.
    /// </remarks>
    public enum PacketStatus
    {
        /// <summary>Seen, and nothing is known about it.</summary>
        Unknown = 0,

        /// <summary>Somebody has worked out what it is and given it a name.</summary>
        Named = 1,

        /// <summary>What its fields mean is written down, measured against real traffic.</summary>
        Documented = 2,

        /// <summary>The server answers it properly.</summary>
        Handled = 3,

        /// <summary>Understood, and deliberately left alone. Says so, instead of looking forgotten.</summary>
        Ignored = 4,
    }

    /// <summary>
    /// What a note is about: one opcode with one payload shape.
    /// </summary>
    /// <remarks>
    /// Both halves are needed, and both were measured over the 72,879 frames in the traffic log,
    /// which carry 834 distinct (opcode, shape) pairs across 242 opcodes and 664 shapes.
    ///
    /// <b>Shape alone is not enough.</b> Only 10 of the 664 shapes are shared by more than one
    /// opcode — but those ten are the trivial ones (<c>(empty)</c>, <c>1:v</c>, <c>1:v,2:v</c>…) and
    /// between them they cover 180 of the opcodes. Filing by shape would tip half the protocol into
    /// ten buckets.
    ///
    /// <b>Opcode alone is not enough either.</b> 59 of the 242 opcodes turn up with more than one
    /// shape, and <c>jss</c> alone has 185. Filing by opcode would hide exactly the variety the
    /// list is opened to see.
    ///
    /// The shape is still what carries the knowledge across a patch, but not by being the key on
    /// its own: when Ankama rotates the names, <c>protocolbuilder</c>'s structural matcher produces
    /// an old-to-new opcode table — that is where <c>datos/mapeo_3.6.10.10_a_3.6.10.11.tsv</c> came
    /// from — and these keys get rewritten through it. A shape that matches on both sides is what
    /// makes that mapping trustworthy in the first place.
    ///
    /// <see cref="AnyShape"/> exists for the notes that are about the opcode itself rather than
    /// about one of its payloads, which is the sane way to say something about all 185 shapes of
    /// <c>jss</c> at once.
    /// </remarks>
    public readonly struct PacketShapeKey : IEquatable<PacketShapeKey>
    {
        /// <summary>A shape of <c>*</c>: the note is about the opcode, whatever it carries.</summary>
        public const string AnyShape = "*";

        public PacketShapeKey(string opcode, string shape)
        {
            Opcode = opcode ?? "";
            Shape = string.IsNullOrEmpty(shape) ? AnyShape : shape;
        }

        public string Opcode { get; }

        public string Shape { get; }

        public bool IsAboutEveryShape => Shape == AnyShape;

        public bool Equals(PacketShapeKey other)
            => string.Equals(Opcode, other.Opcode, StringComparison.Ordinal)
            && string.Equals(Shape, other.Shape, StringComparison.Ordinal);

        public override bool Equals(object? obj) => obj is PacketShapeKey other && Equals(other);

        public override int GetHashCode() => HashCode.Combine(Opcode, Shape);

        public override string ToString() => $"{Opcode} {Shape}";
    }

    /// <summary>What a person worked out about one packet.</summary>
    public sealed class PacketNote
    {
        public string Opcode { get; init; } = "";

        public string Shape { get; init; } = PacketShapeKey.AnyShape;

        public PacketStatus Status { get; init; } = PacketStatus.Unknown;

        /// <summary>A real name for it, ideally one of the 513 the client still ships.</summary>
        public string Name { get; init; } = "";

        /// <summary>What it means, what the fields are, what the server should do about it.</summary>
        public string Notes { get; init; } = "";

        public PacketShapeKey Key => new PacketShapeKey(Opcode, Shape);

        public override string ToString()
            => Name.Length > 0 ? $"{Opcode} · {Name}" : Opcode;
    }

    /// <summary>
    /// The authored layer for packets: what somebody worked out about the traffic.
    /// </summary>
    /// <remarks>
    /// The counts, the timestamps and the samples live in <c>bases/paquetes.db</c>, which the
    /// server writes and which is generated data like any other — wipeable, and not something to
    /// hand-edit. What does <em>not</em> belong there is the part a person contributed, because a
    /// database in <c>bases/</c> is neither reviewable in a pull request nor safe from the next
    /// regeneration.
    ///
    /// So the two are kept apart on purpose: observations in the database, conclusions in
    /// <c>content/packets/shapes.json</c>. Deleting the database costs a few counters. Deleting
    /// this file costs everything anybody ever learned, and git will not let that happen quietly.
    /// </remarks>
    public static class PacketShapeContent
    {
        /// <summary>The authored file, relative to the content root.</summary>
        public const string AuthoredFile = "packets/shapes.json";

        public static ContentStore<PacketShapeKey, PacketNote> Load(string? authoredPath,
                                                                    Action<string>? report = null)
        {
            var store = new ContentStore<PacketShapeKey, PacketNote>();
            if (string.IsNullOrEmpty(authoredPath) || !File.Exists(authoredPath)) return store;

            var from = Origin.Authored(System.IO.Path.GetFileName(authoredPath));
            try
            {
                using var doc = JsonDocument.Parse(File.ReadAllText(authoredPath));
                if (!doc.RootElement.TryGetProperty("packets", out var list)) return store;

                foreach (var entry in list.EnumerateArray())
                {
                    string opcode = Text(entry, "opcode");
                    if (opcode.Length == 0) continue;

                    string shape = entry.TryGetProperty("shape", out var s) && s.ValueKind == JsonValueKind.String
                        ? (s.GetString() ?? PacketShapeKey.AnyShape)
                        : PacketShapeKey.AnyShape;

                    var key = new PacketShapeKey(opcode, shape);

                    if (entry.TryGetProperty("remove", out var gone) && gone.ValueKind == JsonValueKind.True)
                    {
                        store.Erase(key, from);
                        continue;
                    }

                    store.Put(key, new PacketNote
                    {
                        Opcode = opcode,
                        Shape = key.Shape,
                        Status = ParseStatus(Text(entry, "status")),
                        Name = Text(entry, "name"),
                        Notes = Text(entry, "notes"),
                    }, from);
                }
            }
            catch (Exception ex)
            {
                report?.Invoke($"[Content] {System.IO.Path.GetFileName(authoredPath)} is unreadable: {ex.Message}");
            }

            return store;
        }

        /// <summary>
        /// Writes the authored file back out.
        /// </summary>
        /// <remarks>
        /// Rows go out sorted by opcode and then by shape, and the file is written whole rather
        /// than appended to. Both of those are for git: a file whose row order depends on the order
        /// a dictionary happened to enumerate produces a diff of the entire file every time
        /// somebody changes one line, and a diff nobody can read is a diff nobody reviews.
        ///
        /// It writes to a temporary file first and then moves it over the real one. The editor and
        /// a running server can both have this file open, and half a JSON file on disk because the
        /// editor was closed mid-write is a boot failure nobody would connect to what they did.
        /// </remarks>
        public static void Save(string path, IEnumerable<PacketNote> notes, IEnumerable<string>? comment = null)
        {
            var ordered = new List<PacketNote>(notes);
            ordered.Sort((a, b) =>
            {
                int byOpcode = string.CompareOrdinal(a.Opcode, b.Opcode);
                return byOpcode != 0 ? byOpcode : string.CompareOrdinal(a.Shape, b.Shape);
            });

            var options = new JsonWriterOptions
            {
                Indented = true,
                // The shapes are full of { } : and , and a shape escaped as { is unreadable,
                // which defeats the point of the file being text a person can review.
                Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
            };

            using var buffer = new MemoryStream();
            using (var writer = new Utf8JsonWriter(buffer, options))
            {
                writer.WriteStartObject();

                writer.WritePropertyName("_comment");
                writer.WriteStartArray();
                foreach (string line in comment ?? DefaultComment) writer.WriteStringValue(line);
                writer.WriteEndArray();

                writer.WritePropertyName("packets");
                writer.WriteStartArray();
                foreach (var note in ordered)
                {
                    writer.WriteStartObject();
                    writer.WriteString("opcode", note.Opcode);
                    writer.WriteString("shape", note.Shape);
                    writer.WriteString("status", note.Status.ToString().ToLowerInvariant());
                    if (note.Name.Length > 0) writer.WriteString("name", note.Name);
                    if (note.Notes.Length > 0) writer.WriteString("notes", note.Notes);
                    writer.WriteEndObject();
                }

                writer.WriteEndArray();
                writer.WriteEndObject();
            }

            string? folder = System.IO.Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(folder)) Directory.CreateDirectory(folder);

            string temporary = path + ".writing";
            File.WriteAllBytes(temporary, buffer.ToArray());
            File.Move(temporary, path, overwrite: true);
        }

        public static PacketStatus ParseStatus(string? text)
            => Enum.TryParse(text, ignoreCase: true, out PacketStatus status) ? status : PacketStatus.Unknown;

        private static string Text(JsonElement element, string name)
            => element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
                ? value.GetString() ?? ""
                : "";

        private static readonly string[] DefaultComment =
        {
            "The authored layer for packets: what a person worked out about the traffic.",
            "",
            "The counts, the timestamps and the raw samples are NOT here. They live in",
            "bases/paquetes.db, which the server writes and which is generated data. Here go the",
            "conclusions, because those are what no tool can reproduce and what git has to keep.",
            "",
            "One row per opcode and payload shape:",
            "",
            "  { \"opcode\": \"jss\", \"shape\": \"*\",     \"status\": \"named\", \"name\": \"...\" }",
            "  { \"opcode\": \"kqz\", \"shape\": \"2:s,3:s\", \"status\": \"documented\", \"notes\": \"...\" }",
            "",
            "A shape of * is a note about the opcode whatever it carries, which is the sane way to",
            "say something about all 185 shapes jss turns up with.",
            "",
            "Both halves are needed, measured over the 72,879 frames of the traffic log: 10 of the",
            "664 shapes are shared by more than one opcode and between them cover 180 of the 242,",
            "and 59 of those 242 opcodes carry more than one shape.",
            "",
            "status: unknown, named, documented, handled, ignored.",
        };
    }
}
