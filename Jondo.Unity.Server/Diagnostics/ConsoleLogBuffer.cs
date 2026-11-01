using Jondo.Unity.Launcher;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace Jondo.Unity.Server
{
    public static class ConsoleLogBuffer
    {
        internal sealed record LogEntry(long Id, string Message, string Time);

        private static readonly ConcurrentQueue<LogEntry> _logs = new ConcurrentQueue<LogEntry>();
        private static long _sequence = 0;
        private static readonly int MaxLogs = 1000;
        private static TextWriter? _originalOut;

        public static void Initialize()
        {
            if (_originalOut != null) return;
            _originalOut = Console.Out;
            Console.SetOut(new InterceptWriter(_originalOut));
        }

        /// <summary>
        /// And to disk as well. The console lives in a window that closes with the emulator, and what
        /// is most needed afterwards -- the dump of a client packet we do not know how to handle --
        /// went with it. Now it stays in logs/emulator_console.log.
        /// </summary>
        private static readonly object FileLock = new object();

        private static void ToFile(string text)
        {
            lock (FileLock)
            {
                try
                {
                    File.AppendAllText(Path.Combine(Paths.LogsDir, "emulator_console.log"),
                                       $"[{DateTime.Now:HH:mm:ss.fff}] {text}{Environment.NewLine}");
                }
                catch { }
            }
        }

        public static void AddLog(string text)
        {
            if (string.IsNullOrWhiteSpace(text)) return;

            ToFile(text);
            // Assign IDs and enqueue under one lock: otherwise a GUI cursor can pass an entry
            // whose writer has an ID but has not yet enqueued it.
            lock (_logs)
            {
                _logs.Enqueue(new LogEntry(++_sequence, text, DateTime.Now.ToString("HH:mm:ss")));
                while (_logs.Count > MaxLogs) _logs.TryDequeue(out _);
            }
        }

        internal static IReadOnlyList<LogEntry> ReadSince(long sinceId)
        {
            lock (_logs) return _logs.Where(log => log.Id > sinceId).ToArray();
        }

        public static string GetLogsJson(long sinceId)
        {
            var entries = new List<LogEntry>();
            foreach (var log in _logs)
            {
                if (log.Id > sinceId)
                {
                    entries.Add(log);
                }
            }

            var sb = new StringBuilder();
            sb.Append("{\"success\":true,\"lastId\":").Append(_sequence).Append(",\"logs\":[");

            for (int i = 0; i < entries.Count; i++)
            {
                if (i > 0) sb.Append(",");
                sb.Append("{\"id\":").Append(entries[i].Id)
                  .Append(",\"time\":\"").Append(entries[i].Time)
                  .Append("\",\"msg\":\"");
                AppendEscaped(sb, entries[i].Message);
                sb.Append("\"}");
            }

            sb.Append("]}");
            return sb.ToString();
        }

        /// <summary>
        /// Escapes a line so that what comes out is valid JSON.
        ///
        /// Escaping only the quote, the backslash and the newline was not enough, and it cost a
        /// whole session of blind debugging. The packet dump prints the bytes of the message, and
        /// any byte under 0x20 that survives as a character is illegal raw inside a JSON string.
        /// The document then failed to parse, the reader swallowed the exception and
        /// returned nothing, and since the window only moves its cursor forward when it is given
        /// entries, it asked for the same broken batch for ever. The console froze on the first
        /// game packet and never moved again.
        ///
        /// A lone surrogate has to be replaced, not escaped. Writing it as \\uD800 produces a
        /// document that parses, but the reader blows up on that one string while pulling the
        /// entries out, after it has already collected the earlier ones. The window then moves
        /// its cursor as far as the survivors, asks again from there, and lands on the same bad
        /// entry every time: the same freeze by another road.
        /// </summary>
        private static void AppendEscaped(StringBuilder sb, string text)
        {
            for (int i = 0; i < text.Length; i++)
            {
                char c = text[i];
                switch (c)
                {
                    case '"': sb.Append("\\\""); break;
                    case '\\': sb.Append("\\\\"); break;
                    case '\b': sb.Append("\\b"); break;
                    case '\f': sb.Append("\\f"); break;
                    case '\n': sb.Append("\\n"); break;
                    case '\r': break;
                    case '\t': sb.Append("\\t"); break;
                    default:
                        if (c < 0x20 || c == (char)0x7F)
                        {
                            sb.Append("\\u").Append(((int)c).ToString("x4"));
                        }
                        else if (char.IsHighSurrogate(c) && i + 1 < text.Length && char.IsLowSurrogate(text[i + 1]))
                        {
                            sb.Append(c).Append(text[i + 1]);
                            i++;
                        }
                        else if (char.IsSurrogate(c))
                        {
                            sb.Append('�');
                        }
                        else
                        {
                            sb.Append(c);
                        }
                        break;
                }
            }
        }

        /// <summary>
        /// Sends everything written to the console to the buffer the launcher window reads.
        ///
        /// Write and WriteLine have to be treated as one thing. The packet trace builds each line
        /// out of several Write calls to colour each part — direction, size, context, opcode — and
        /// closes it with a WriteLine. Recording only the WriteLine left the window with the tail
        /// of the line and nothing else: the description, without the opcode or the direction.
        /// So the pieces are held until the line is closed and then go in as one entry.
        /// </summary>
        private class InterceptWriter : TextWriter
        {
            private readonly TextWriter _underlying;
            private readonly StringBuilder _pending = new StringBuilder();
            private readonly object _gate = new object();

            public override Encoding Encoding => _underlying.Encoding;

            public InterceptWriter(TextWriter underlying)
            {
                _underlying = underlying;
            }

            public override void WriteLine(string? value)
            {
                _underlying.WriteLine(value);

                string line;
                lock (_gate)
                {
                    _pending.Append(value);
                    line = _pending.ToString();
                    _pending.Clear();
                }
                AddLog(line);
            }

            public override void Write(string? value)
            {
                _underlying.Write(value);
                if (string.IsNullOrEmpty(value)) return;

                lock (_gate)
                {
                    // A runaway line must not grow without bound: without a closing WriteLine the
                    // pieces would pile up for ever.
                    if (_pending.Length < 8192) _pending.Append(value);
                }
            }
        }
    }
}
