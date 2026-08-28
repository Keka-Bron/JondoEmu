using System;
using System.Collections.Generic;
using System.IO;

namespace Jondo.Unity.Protocol.Wire
{
    /// <summary>One declared field of one message.</summary>
    public sealed class ProtoFieldDef
    {
        public int Number { get; init; }

        /// <summary>The obfuscated name, which is all the client has. Still better than nothing.</summary>
        public string Name { get; init; } = "";

        /// <summary>As written: <c>int32</c>, <c>string</c>, another message's name, <c>map&lt;…&gt;</c>.</summary>
        public string Type { get; init; } = "";

        public bool Repeated { get; init; }

        public bool IsMap => Type.StartsWith("map<", StringComparison.Ordinal);

        /// <summary>True for the built-in types, false when the type is another declared message.</summary>
        public bool IsScalar => ProtoSchema.IsScalarType(Type);

        public override string ToString()
            => (Repeated ? "repeated " : "") + Type + " " + Name + " = " + Number;
    }

    /// <summary>One declared message.</summary>
    public sealed class ProtoMessageDef
    {
        private readonly Dictionary<int, ProtoFieldDef> _byNumber = new Dictionary<int, ProtoFieldDef>();

        public ProtoMessageDef(string name, IReadOnlyList<ProtoFieldDef> fields)
        {
            Name = name;
            Fields = fields;
            foreach (var field in fields) _byNumber[field.Number] = field;
        }

        public string Name { get; }

        public IReadOnlyList<ProtoFieldDef> Fields { get; }

        public ProtoFieldDef? Field(int number)
            => _byNumber.TryGetValue(number, out var field) ? field : null;

        public override string ToString() => $"{Name} ({Fields.Count} fields)";
    }

    /// <summary>
    /// The whole protocol as the client declares it, read out of
    /// <c>datos/protocolo_3.6.10.10.proto</c>.
    /// </summary>
    /// <remarks>
    /// This is the file <c>protocolbuilder</c> reconstructs from the client's own classes: 2,169
    /// messages and 550 enums, with real field numbers and real types. The names are the
    /// obfuscated ones, because that is all Ankama ships, but the <em>types</em> are the truth.
    ///
    /// That turns the frame view from guesswork into reading. Without it a length-delimited field
    /// has to be guessed at — is it a string, a nested message, or a blob? — and the guess is wrong
    /// often enough to matter: a two-byte language code parses perfectly well as a submessage with
    /// a field 12. With it, <c>string fytl = 3</c> settles it and <c>kqz</c> field 3 reads as
    /// <c>"es"</c>, which is exactly what it is.
    ///
    /// Nothing here fails hard when the file is missing. The editor is meant to open on a machine
    /// that has not run the extraction tools, and a frame view with numbers instead of names is
    /// still worth having.
    /// </remarks>
    public sealed class ProtoSchema
    {
        private readonly Dictionary<string, ProtoMessageDef> _messages;
        private readonly HashSet<string> _enums;

        private ProtoSchema(Dictionary<string, ProtoMessageDef> messages, HashSet<string> enums,
                            string source)
        {
            _messages = messages;
            _enums = enums;
            Source = source;
        }

        /// <summary>Which file this came out of, for the overview screen.</summary>
        public string Source { get; }

        public IReadOnlyDictionary<string, ProtoMessageDef> Messages => _messages;

        public int MessageCount => _messages.Count;

        public int EnumCount => _enums.Count;

        public static readonly ProtoSchema Empty = new ProtoSchema(
            new Dictionary<string, ProtoMessageDef>(StringComparer.Ordinal),
            new HashSet<string>(StringComparer.Ordinal), "");

        public ProtoMessageDef? Message(string? name)
            => name != null && _messages.TryGetValue(name, out var message) ? message : null;

        public bool IsEnum(string name) => _enums.Contains(name);

        /// <summary>
        /// Reads a <c>.proto</c>. Returns <see cref="Empty"/> rather than throwing when it cannot.
        /// </summary>
        public static ProtoSchema Load(string? path, Action<string>? report = null)
        {
            if (string.IsNullOrEmpty(path) || !File.Exists(path))
            {
                report?.Invoke($"{System.IO.Path.GetFileName(path ?? "(no path)")} is not there; " +
                               "frames will show field numbers instead of names.");
                return Empty;
            }

            try
            {
                return Parse(File.ReadAllLines(path), System.IO.Path.GetFileName(path));
            }
            catch (Exception ex)
            {
                report?.Invoke($"{System.IO.Path.GetFileName(path)} is unreadable: {ex.Message}");
                return Empty;
            }
        }

        /// <summary>
        /// Parses the subset of proto3 this file actually uses.
        /// </summary>
        /// <remarks>
        /// Deliberately not a general proto parser. Measured over the generated file: every message
        /// is top level, there is not one <c>oneof</c>, and the only shapes a field takes are
        /// <c>type name = n;</c>, the same with <c>repeated</c> in front, and
        /// <c>map&lt;k, v&gt; name = n;</c>. Writing a parser for the language rather than for the
        /// file would be more code doing the same job with more ways to be wrong.
        /// </remarks>
        public static ProtoSchema Parse(IEnumerable<string> lines, string source)
        {
            var messages = new Dictionary<string, ProtoMessageDef>(StringComparer.Ordinal);
            var enums = new HashSet<string>(StringComparer.Ordinal);

            string? current = null;
            bool inEnum = false;
            var fields = new List<ProtoFieldDef>();

            foreach (string raw in lines)
            {
                string line = Strip(raw);
                if (line.Length == 0) continue;

                if (current == null)
                {
                    if (StartsBlock(line, "message ", out string name))
                    {
                        current = name;
                        inEnum = false;
                        fields.Clear();
                    }
                    else if (StartsBlock(line, "enum ", out string enumName))
                    {
                        current = enumName;
                        inEnum = true;
                        enums.Add(enumName);
                    }

                    continue;
                }

                if (line[0] == '}')
                {
                    if (!inEnum) messages[current] = new ProtoMessageDef(current, fields.ToArray());
                    current = null;
                    continue;
                }

                if (inEnum) continue;

                var field = ParseField(line);
                if (field != null) fields.Add(field);
            }

            return new ProtoSchema(messages, enums, source);
        }

        private static string Strip(string line)
        {
            int comment = line.IndexOf("//", StringComparison.Ordinal);
            return (comment >= 0 ? line[..comment] : line).Trim();
        }

        private static bool StartsBlock(string line, string keyword, out string name)
        {
            name = "";
            if (!line.StartsWith(keyword, StringComparison.Ordinal)) return false;

            int brace = line.IndexOf('{');
            if (brace < 0) return false;

            name = line[keyword.Length..brace].Trim();
            return name.Length > 0;
        }

        private static ProtoFieldDef? ParseField(string line)
        {
            int equals = line.IndexOf('=');
            int semicolon = line.IndexOf(';', equals < 0 ? 0 : equals);
            if (equals < 0 || semicolon < 0) return null;

            if (!int.TryParse(line.AsSpan(equals + 1, semicolon - equals - 1).Trim(), out int number))
            {
                return null;
            }

            string head = line[..equals].Trim();

            bool repeated = head.StartsWith("repeated ", StringComparison.Ordinal);
            if (repeated) head = head["repeated ".Length..].Trim();
            else if (head.StartsWith("optional ", StringComparison.Ordinal)) head = head["optional ".Length..].Trim();

            // The name is the last word; everything before it is the type, which matters because
            // "map<int32, int32> foo" has a space inside the type.
            int split = head.LastIndexOf(' ');
            if (split <= 0) return null;

            return new ProtoFieldDef
            {
                Number = number,
                Type = head[..split].Trim(),
                Name = head[(split + 1)..].Trim(),
                Repeated = repeated,
            };
        }

        private static readonly HashSet<string> Scalars = new HashSet<string>(StringComparer.Ordinal)
        {
            "double", "float", "int32", "int64", "uint32", "uint64", "sint32", "sint64",
            "fixed32", "fixed64", "sfixed32", "sfixed64", "bool", "string", "bytes",
        };

        public static bool IsScalarType(string? type)
            => type != null && (Scalars.Contains(type) || type.StartsWith("map<", StringComparison.Ordinal));
    }
}
