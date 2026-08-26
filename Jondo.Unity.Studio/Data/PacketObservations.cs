using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using Jondo.Unity.Protocol;
using Jondo.Unity.Protocol.Wire;
using Microsoft.Data.Sqlite;

namespace Jondo.Unity.Studio.Data
{
    /// <summary>What has actually been seen of one opcode carrying one payload shape.</summary>
    public sealed class PacketObservation
    {
        public string Opcode { get; init; } = "";

        public string Shape { get; init; } = "";

        /// <summary>The envelope's root field: 1 a push, 2 the client asking, 3 an answer.</summary>
        public int RootField { get; set; }

        public long Occurrences { get; set; }

        /// <summary>Why the server wrote it down, when it was the server that did. Empty otherwise.</summary>
        public string Reason { get; set; } = "";

        /// <summary>Where it was seen: the server's own table, the traffic log, or both.</summary>
        public string Seen { get; set; } = "";

        public int PayloadBytes { get; set; }

        public string SampleHex { get; set; } = "";

        public DateTimeOffset LastSeen { get; set; }

        public string Direction => RootField switch
        {
            1 => "server →",
            2 => "→ client",
            3 => "server ↩",
            _ => "",
        };
    }

    /// <summary>
    /// Everything that has been observed about packets, as opposed to everything anybody decided.
    /// </summary>
    /// <remarks>
    /// Two sources, and they are not the same list:
    ///
    ///   <c>bases/paquetes.db</c>   what the <em>server</em> met and had no handler for, or
    ///                              silenced. Small, authoritative, and the actual to-do list.
    ///   <c>logs/gameserver_traffic.log</c>  every frame either side sent. 110 MB, and it holds
    ///                              834 distinct opcode-and-shape pairs across 242 opcodes.
    ///
    /// The database is loaded on opening the page because it is tiny. The log is not: scanning it
    /// is a button, because a hundred megabytes of hex is a few seconds of work and doing it every
    /// time the editor opens would make the editor feel broken.
    /// </remarks>
    public sealed class PacketObservations
    {
        private readonly Dictionary<(string Opcode, string Shape), PacketObservation> _rows
            = new Dictionary<(string, string), PacketObservation>();

        public IEnumerable<PacketObservation> Rows => _rows.Values;

        public int Count => _rows.Count;

        /// <summary>What went wrong on the way, if anything.</summary>
        public List<string> Complaints { get; } = new List<string>();

        /// <summary>True once the traffic log has been scanned, which is not automatic.</summary>
        public bool ScannedTheLog { get; private set; }

        public long LogBytesScanned { get; private set; }

        /// <summary>
        /// The opcodes the emulator has a name for: the 253 constants in <see cref="Op"/>.
        /// </summary>
        /// <remarks>
        /// A crude signal and a useful one. It does not mean the packet is answered — plenty of
        /// those constants are only used to recognise something and ignore it — but "the emulator
        /// has never heard of this three-letter code" is a different kind of unknown from "the
        /// emulator knows the name and still does nothing", and the two want different work.
        /// </remarks>
        public static readonly HashSet<string> Named = BuildNamed();

        private static HashSet<string> BuildNamed()
        {
            var names = new HashSet<string>(StringComparer.Ordinal);
            foreach (var field in typeof(Op).GetFields(BindingFlags.Public | BindingFlags.Static))
            {
                if (!field.IsLiteral || field.FieldType != typeof(string)) continue;
                if (field.GetRawConstantValue() is string value && value.Length == 3) names.Add(value);
            }

            return names;
        }

        /// <summary>Reads the server's own table of what it could not handle.</summary>
        public void LoadServerTable(string connectionString)
        {
            try
            {
                using var connection = new SqliteConnection(connectionString);
                connection.Open();

                using var command = connection.CreateCommand();
                command.CommandText = @"
                    SELECT Opcode, RootField, Kind, Signature, Occurrences, LastSeen,
                           PayloadBytes, SampleHex
                    FROM PaquetesSinAtender;";

                using var reader = command.ExecuteReader();
                while (reader.Read())
                {
                    var row = Row(reader.GetString(0), reader.GetString(3));
                    row.RootField = reader.GetInt32(1);
                    row.Occurrences += reader.GetInt64(4);
                    row.Reason = reader.GetInt32(2) switch
                    {
                        1 => "silenced",
                        2 => "undecodable",
                        _ => "unhandled",
                    };
                    row.PayloadBytes = reader.GetInt32(6);
                    row.SampleHex = reader.GetString(7);
                    row.Seen = Add(row.Seen, "server");

                    if (DateTimeOffset.TryParse(reader.GetString(5), out var last)) row.LastSeen = last;
                }
            }
            catch (SqliteException ex)
            {
                // A missing table is the normal state on a machine that has never run the server.
                if (!ex.Message.Contains("no such table", StringComparison.OrdinalIgnoreCase))
                {
                    Complaints.Add($"paquetes.db could not be read: {ex.Message}");
                }
            }
            catch (Exception ex)
            {
                Complaints.Add($"paquetes.db could not be read: {ex.Message}");
            }
        }

        /// <summary>
        /// Walks the whole traffic log and counts every opcode and shape in it.
        /// </summary>
        /// <remarks>
        /// This is the one that tells you what the protocol looks like rather than what the server
        /// tripped over, and it covers both directions: the server's own output is in there too,
        /// which is how a reply we send can be compared against one that was captured.
        /// </remarks>
        public void ScanTrafficLog(string path, Action<long, long>? progress = null)
        {
            if (!File.Exists(path))
            {
                Complaints.Add($"{System.IO.Path.GetFileName(path)} is not there; nothing to scan.");
                return;
            }

            var reader = new TrafficLogReader(path);
            long total = reader.Length;
            reader.SeekToStart();

            while (true)
            {
                var batch = reader.ReadNew(20_000);
                if (batch.Count == 0) break;

                foreach (var entry in batch)
                {
                    if (!entry.Frame.Found) continue;

                    var row = Row(entry.Frame.Opcode, entry.Shape);
                    row.Occurrences++;
                    row.Seen = Add(row.Seen, "traffic");
                    if (row.RootField == 0) row.RootField = entry.Frame.RootField;
                    if (row.SampleHex.Length == 0)
                    {
                        row.PayloadBytes = entry.Frame.Payload.Length;
                        row.SampleHex = Convert.ToHexString(
                            entry.Frame.Payload.Length <= 512
                                ? entry.Frame.Payload
                                : entry.Frame.Payload[..512]);
                    }
                }

                progress?.Invoke(reader.Position, total);
                if (reader.Position >= total) break;
            }

            LogBytesScanned = reader.Position;
            ScannedTheLog = true;
        }

        private PacketObservation Row(string opcode, string shape)
        {
            var key = (opcode, shape);
            if (_rows.TryGetValue(key, out var row)) return row;

            row = new PacketObservation { Opcode = opcode, Shape = shape };
            _rows[key] = row;
            return row;
        }

        private static string Add(string seen, string what)
            => seen.Length == 0 ? what : (seen.Contains(what, StringComparison.Ordinal) ? seen : seen + " + " + what);
    }
}
