using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;

namespace Jondo.Unity.Protocol.Wire
{
    /// <summary>One frame as the server wrote it down.</summary>
    public sealed class TrafficEntry
    {
        /// <summary>The log only keeps the time of day; there is no date in it.</summary>
        public TimeSpan Time { get; init; }

        /// <summary>The marker as written: <c>C-&gt;S</c>, <c>S-&gt;C</c> or <c>GAME_C-&gt;S</c>.</summary>
        public string Direction { get; init; } = "";

        public bool FromClient => Direction.Contains("C->S", StringComparison.Ordinal);

        /// <summary>The length the log claims. Kept separately so a mismatch can be seen.</summary>
        public int DeclaredBytes { get; init; }

        public byte[] Raw { get; init; } = Array.Empty<byte>();

        /// <summary>What was inside, opened by <see cref="Envelope"/>.</summary>
        public EnvelopeFrame Frame { get; init; }

        /// <summary>Where in the file this row started. Lets the editor point back at it.</summary>
        public long Offset { get; init; }

        public string Opcode => Frame.Found ? Frame.Opcode : "(unopened)";

        public string Shape => Frame.Found ? ProtoShape.Of(Frame.Payload) : ProtoShape.Unreadable;

        public override string ToString()
            => $"{Time:hh\\:mm\\:ss\\.fff} {Direction} {Opcode} ({DeclaredBytes} b)";
    }

    /// <summary>
    /// Reads <c>logs/gameserver_traffic.log</c>, from the end and as it grows.
    /// </summary>
    /// <remarks>
    /// The plan for live traffic called for a tap in the proxy pushing frames out over HTTP, which
    /// is the right answer when the viewer is a browser. It is the wrong one here. The server
    /// already writes every frame to this file — that is where the 110 MB came from — so a tap
    /// would be a second copy of the same data, and it would come with a socket to secure, a
    /// protocol to keep in step, and a hard dependency on the server being up.
    ///
    /// Reading the file instead gets three things for free that the tap does not have: it works
    /// with the server stopped, it can look at what happened before the editor was opened, and it
    /// adds no surface to attack. The cost is a poll instead of a push, which for a human watching
    /// a list does not matter.
    ///
    /// The file is opened shared for writing and deleting, because the server has it open and is
    /// appending to it. Truncation is watched for: the log is wiped between runs, and a reader
    /// holding a stale offset would otherwise sit reading nothing for ever.
    /// </remarks>
    public sealed class TrafficLogReader
    {
        private readonly string _path;
        private readonly List<byte> _pending = new List<byte>();
        private Record _record;
        private bool _dropFirstRecord;

        public TrafficLogReader(string path)
        {
            _path = path;
            _record = new Record(0);
        }

        public string Path => _path;

        /// <summary>How far into the file the next read starts.</summary>
        public long Position { get; private set; }

        /// <summary>True when the file shrank under us, which means it was wiped and restarted.</summary>
        public bool Restarted { get; private set; }

        public bool Exists => File.Exists(_path);

        public long Length
        {
            get
            {
                try { return new FileInfo(_path).Length; }
                catch { return 0; }
            }
        }

        /// <summary>
        /// Moves to roughly the last <paramref name="window"/> bytes, so opening the editor on a
        /// 110 MB log does not mean reading 110 MB.
        /// </summary>
        /// <remarks>
        /// It lands mid-record on purpose and throws the first, partial one away: scanning
        /// backwards for a record boundary costs the same and buys one extra row.
        /// </remarks>
        public void SeekToTail(long window = 2 * 1024 * 1024)
        {
            long length = Length;
            Restart(length > window ? length - window : 0, dropFirst: length > window);
        }

        public void SeekToStart() => Restart(0, dropFirst: false);

        private void Restart(long position, bool dropFirst)
        {
            Position = position;
            _pending.Clear();
            _record = new Record(position);
            _dropFirstRecord = dropFirst;
        }

        /// <summary>
        /// Reads whatever has appeared since last time, up to <paramref name="max"/> entries.
        /// </summary>
        /// <remarks>
        /// A half-written record at the end is kept back rather than parsed: the server may be
        /// mid-write, and half a hex line read as a whole one is a corrupt frame in the list that
        /// nobody can tell apart from a real one. The part-built record survives between calls,
        /// which is what makes a record straddling two reads come out in one piece.
        /// </remarks>
        public IReadOnlyList<TrafficEntry> ReadNew(int max = 2000)
        {
            var entries = new List<TrafficEntry>();
            Restarted = false;
            if (!Exists) return entries;

            long length = Length;
            if (length < Position)
            {
                // Wiped and started again.
                Restarted = true;
                Restart(0, dropFirst: false);
                length = Length;
            }

            if (length <= Position) return entries;

            try
            {
                using var file = new FileStream(_path, FileMode.Open, FileAccess.Read,
                                                FileShare.ReadWrite | FileShare.Delete);
                file.Seek(Position, SeekOrigin.Begin);

                var buffer = new byte[64 * 1024];
                bool stopped = false;

                while (!stopped && entries.Count < max)
                {
                    int read = file.Read(buffer, 0, buffer.Length);
                    if (read <= 0) break;

                    for (int i = 0; i < read; i++)
                    {
                        if (buffer[i] != (byte)'\n')
                        {
                            _pending.Add(buffer[i]);
                            continue;
                        }

                        string line = Decode(_pending);
                        _pending.Clear();

                        var entry = _record.Take(line, ref _dropFirstRecord);
                        if (entry == null) continue;

                        entries.Add(entry);
                        if (entries.Count < max) continue;

                        // Stop on a record boundary, and leave the file position exactly there so
                        // the next call carries on without re-reading or skipping anything.
                        Position = file.Position - (read - i - 1);
                        stopped = true;
                        break;
                    }

                    if (!stopped) Position = file.Position;
                }
            }
            catch (IOException)
            {
                // The server has it open and is writing to it; a failed read now is a read that
                // succeeds in half a second. Nothing to report and nothing to do.
            }

            return entries;
        }

        private static string Decode(List<byte> bytes)
        {
            int count = bytes.Count;
            if (count > 0 && bytes[count - 1] == (byte)'\r') count--;
            if (count == 0) return "";
            return Encoding.UTF8.GetString(bytes.GetRange(0, count).ToArray());
        }

        /// <summary>
        /// One record being put back together, line by line.
        /// </summary>
        /// <remarks>
        /// Line based rather than split on the row of dashes, because the dashes are a detail of
        /// how the log happens to be written today and the header line is not. It also means a
        /// record missing its <c>Str:</c> line, or carrying an extra one, still comes out whole.
        /// </remarks>
        private sealed class Record
        {
            private TimeSpan _time;
            private string _direction = "";
            private int _declared;
            private byte[]? _raw;
            private long _offset;
            private bool _open;
            private long _seen;

            public Record(long startsAt) => _seen = startsAt;

            /// <summary>Feeds one line in. Returns a record when one finished on this line.</summary>
            public TrafficEntry? Take(string line, ref bool dropThisOne)
            {
                long start = _seen;
                _seen += line.Length + 1;

                if (line.Length > 0 && line[0] == '[')
                {
                    // A header arriving while one is already open means the last one was cut
                    // short. What was read of it is dropped: the editor is being asked what the
                    // traffic was, not what two thirds of it was.
                    TrafficEntry? finished = null;
                    if (_open && _raw != null && !dropThisOne) finished = Build();
                    if (_open) dropThisOne = false;

                    _open = true;
                    _raw = null;
                    _offset = start;
                    ParseHeader(line, out _time, out _direction, out _declared);
                    return finished;
                }

                if (!_open) return null;

                if (line.StartsWith("Hex: ", StringComparison.Ordinal))
                {
                    _raw = ParseHex(line.AsSpan(5));
                    return null;
                }

                if (line.Length >= 10 && line[0] == '-')
                {
                    TrafficEntry? finished = _raw != null && !dropThisOne ? Build() : null;

                    // Whatever became of the first, partial record, everything after it is whole.
                    dropThisOne = false;
                    _open = false;
                    _raw = null;
                    return finished;
                }

                return null;
            }

            private TrafficEntry Build()
            {
                byte[] raw = _raw ?? Array.Empty<byte>();
                return new TrafficEntry
                {
                    Time = _time,
                    Direction = _direction,
                    DeclaredBytes = _declared,
                    Raw = raw,
                    Frame = Envelope.ReadLogged(raw),
                    Offset = _offset,
                };
            }

            private static void ParseHeader(string line, out TimeSpan time, out string direction,
                                            out int declared)
            {
                time = TimeSpan.Zero;
                direction = "";
                declared = 0;

                int close = line.IndexOf(']');
                if (close > 1)
                {
                    TimeSpan.TryParseExact(line.AsSpan(1, close - 1), @"hh\:mm\:ss\.fff",
                                           CultureInfo.InvariantCulture, out time);
                }

                int open = line.IndexOf('(', close < 0 ? 0 : close);
                if (open < 0) open = line.Length;

                int from = Math.Min(close + 2, line.Length);
                direction = line.Substring(from, Math.Max(0, Math.Min(open, line.Length) - from)).Trim();

                if (open < line.Length)
                {
                    int space = line.IndexOf(' ', open);
                    if (space > open)
                    {
                        int.TryParse(line.AsSpan(open + 1, space - open - 1),
                                     NumberStyles.Integer, CultureInfo.InvariantCulture, out declared);
                    }
                }
            }

            private static byte[] ParseHex(ReadOnlySpan<char> text)
            {
                var bytes = new List<byte>(text.Length / 3 + 1);
                int high = -1;
                foreach (char c in text)
                {
                    int digit = c switch
                    {
                        >= '0' and <= '9' => c - '0',
                        >= 'A' and <= 'F' => c - 'A' + 10,
                        >= 'a' and <= 'f' => c - 'a' + 10,
                        _ => -1,
                    };

                    if (digit < 0) { high = -1; continue; }
                    if (high < 0) { high = digit; continue; }

                    bytes.Add((byte)((high << 4) | digit));
                    high = -1;
                }

                return bytes.ToArray();
            }
        }
    }
}
