using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using Jondo.Unity.Protocol.Wire;
using Xunit;

namespace Jondo.Unity.Tests.Protocol
{
    /// <summary>
    /// Reading the traffic log while the server is writing to it.
    /// </summary>
    /// <remarks>
    /// The awkward parts are all about timing, and they are the reason this has tests rather than
    /// just being obviously correct: the file is being appended to while it is read, a record can
    /// straddle two reads, the last record on disk can be half written, and the file gets wiped
    /// between server runs. Each of those has a way of producing a corrupt row that looks exactly
    /// like a real one, which is the worst possible failure for a tool whose whole job is to be
    /// believed about what came off the wire.
    /// </remarks>
    public class TrafficLogReaderTests : IDisposable
    {
        private readonly string _path = Path.Combine(Path.GetTempPath(),
                                                     "jondo-traffic-" + Guid.NewGuid().ToString("N") + ".log");

        public void Dispose()
        {
            try { File.Delete(_path); } catch (IOException) { }
        }

        // ─── Building a log ───────────────────────────────────────────────────────

        private static byte[] VarInt(ulong value)
        {
            var bytes = new List<byte>();
            do
            {
                byte b = (byte)(value & 0x7F);
                value >>= 7;
                if (value != 0) b |= 0x80;
                bytes.Add(b);
            }
            while (value != 0);

            return bytes.ToArray();
        }

        private static byte[] Field(int number, byte[] body)
        {
            var bytes = new List<byte>();
            bytes.AddRange(VarInt((ulong)((number << 3) | 2)));
            bytes.AddRange(VarInt((ulong)body.Length));
            bytes.AddRange(body);
            return bytes.ToArray();
        }

        private static byte[] Frame(int rootField, string opcode)
        {
            var any = new List<byte>();
            any.AddRange(Field(1, Encoding.ASCII.GetBytes(Envelope.TypePrefix + opcode)));
            any.AddRange(Field(2, new byte[] { 0x08, 0x2A }));
            return Field(rootField, Field(1, any.ToArray()));
        }

        /// <summary>One record in exactly the shape the server writes it.</summary>
        private static string Record(string time, string direction, byte[] frame)
            => $"[{time}] {direction} ({frame.Length} bytes)\n" +
               $"Hex: {BitConverter.ToString(frame)}\n" +
               "Str: whatever\n" +
               "--------------------------------------------------\n";

        private void Write(string text) => File.AppendAllText(_path, text);

        // ─── The plain cases ──────────────────────────────────────────────────────

        [Fact]
        public void A_record_comes_out_whole()
        {
            Write(Record("12:34:56.789", "C->S", Frame(2, "kqz")));

            var reader = new TrafficLogReader(_path);
            var entries = reader.ReadNew();

            var entry = Assert.Single(entries);
            Assert.Equal("kqz", entry.Opcode);
            Assert.True(entry.FromClient);
            Assert.Equal(new TimeSpan(0, 12, 34, 56, 789), entry.Time);
            Assert.Equal(FrameDirection.ClientRequest, entry.Frame.Direction);
        }

        [Fact]
        public void The_direction_the_server_writes_is_understood()
        {
            Write(Record("00:00:01.000", "S->C", Frame(1, "idu")));
            Write(Record("00:00:02.000", "GAME_C->S", Frame(2, "kqo")));

            var entries = new TrafficLogReader(_path).ReadNew();

            Assert.Equal(2, entries.Count);
            Assert.False(entries[0].FromClient);
            Assert.True(entries[1].FromClient);
        }

        [Fact]
        public void Nothing_read_twice()
        {
            Write(Record("00:00:01.000", "C->S", Frame(2, "kqz")));

            var reader = new TrafficLogReader(_path);
            Assert.Single(reader.ReadNew());
            Assert.Empty(reader.ReadNew());
        }

        [Fact]
        public void What_gets_appended_afterwards_turns_up()
        {
            Write(Record("00:00:01.000", "C->S", Frame(2, "kqz")));

            var reader = new TrafficLogReader(_path);
            Assert.Single(reader.ReadNew());

            Write(Record("00:00:02.000", "S->C", Frame(1, "idu")));
            var second = reader.ReadNew();

            Assert.Equal("idu", Assert.Single(second).Opcode);
        }

        // ─── The awkward ones ─────────────────────────────────────────────────────

        /// <summary>
        /// The server can be caught mid-write. Half a hex line read as a whole one is a corrupt
        /// frame in the list that nobody can tell from a real one, so it waits instead.
        /// </summary>
        [Fact]
        public void A_half_written_record_is_held_back_until_it_is_finished()
        {
            Write(Record("00:00:01.000", "C->S", Frame(2, "kqz")));

            byte[] frame = Frame(1, "jwe");
            Write($"[00:00:02.000] S->C ({frame.Length} bytes)\nHex: {BitConverter.ToString(frame)}\n");

            var reader = new TrafficLogReader(_path);
            Assert.Single(reader.ReadNew());          // only the finished one

            Write("Str: whatever\n--------------------------------------------------\n");
            Assert.Equal("jwe", Assert.Single(reader.ReadNew()).Opcode);
        }

        /// <summary>
        /// A record split across two reads has to survive the join. Reading in 64 KB chunks makes
        /// this happen constantly on a real log; here it is forced by reading one record at a time.
        /// </summary>
        [Fact]
        public void A_record_straddling_two_reads_comes_out_once_and_whole()
        {
            for (int i = 0; i < 5; i++) Write(Record("00:00:0" + i + ".000", "C->S", Frame(2, "kq" + (char)('a' + i))));

            var reader = new TrafficLogReader(_path);
            var all = new List<TrafficEntry>();
            for (int i = 0; i < 10; i++) all.AddRange(reader.ReadNew(1));

            Assert.Equal(5, all.Count);
            Assert.Equal(new[] { "kqa", "kqb", "kqc", "kqd", "kqe" }, all.ConvertAll(e => e.Opcode));
        }

        /// <summary>
        /// The log is wiped between server runs. A reader holding a stale offset would sit reading
        /// nothing for ever, which looks exactly like a quiet server.
        /// </summary>
        [Fact]
        public void A_wiped_log_is_noticed_and_started_again()
        {
            for (int i = 0; i < 4; i++) Write(Record("00:00:0" + i + ".000", "C->S", Frame(2, "kqz")));

            var reader = new TrafficLogReader(_path);
            Assert.Equal(4, reader.ReadNew().Count);

            File.WriteAllText(_path, "");
            Write(Record("00:00:09.000", "S->C", Frame(1, "idu")));

            var fresh = reader.ReadNew();
            Assert.True(reader.Restarted);
            Assert.Equal("idu", Assert.Single(fresh).Opcode);
        }

        /// <summary>
        /// Seeking to the tail lands mid-record on purpose. That first, partial one has to be
        /// thrown away rather than shown as a frame with no bytes in it.
        /// </summary>
        [Fact]
        public void Seeking_to_the_tail_drops_the_record_it_landed_inside()
        {
            for (int i = 0; i < 40; i++) Write(Record("00:00:01.000", "C->S", Frame(2, "kqz")));

            var reader = new TrafficLogReader(_path);
            reader.SeekToTail(600);
            var entries = reader.ReadNew();

            Assert.NotEmpty(entries);
            foreach (var entry in entries)
            {
                Assert.Equal("kqz", entry.Opcode);
                Assert.NotEmpty(entry.Raw);
            }
        }

        [Fact]
        public void A_record_with_no_hex_line_does_not_become_an_empty_frame()
        {
            Write("[00:00:01.000] C->S (4 bytes)\nStr: whatever\n" +
                  "--------------------------------------------------\n");
            Write(Record("00:00:02.000", "C->S", Frame(2, "kqz")));

            var entries = new TrafficLogReader(_path).ReadNew();

            Assert.Equal("kqz", Assert.Single(entries).Opcode);
        }

        [Fact]
        public void A_frame_that_is_not_a_message_is_kept_and_marked_rather_than_dropped()
        {
            Write(Record("00:00:01.000", "S->C", new byte[] { 0x04, 0x05, 0x06 }));

            var entry = Assert.Single(new TrafficLogReader(_path).ReadNew());

            Assert.False(entry.Frame.Found);
            Assert.Equal(new byte[] { 0x04, 0x05, 0x06 }, entry.Raw);
        }

        [Fact]
        public void A_log_that_is_not_there_is_not_an_error()
        {
            var reader = new TrafficLogReader(Path.Combine(Path.GetTempPath(), "no-such.log"));

            Assert.False(reader.Exists);
            Assert.Empty(reader.ReadNew());
        }

        [Fact]
        public void Windows_line_endings_read_the_same()
        {
            byte[] frame = Frame(2, "kqz");
            Write($"[00:00:01.000] C->S ({frame.Length} bytes)\r\nHex: {BitConverter.ToString(frame)}\r\n" +
                  "Str: whatever\r\n--------------------------------------------------\r\n");

            Assert.Equal("kqz", Assert.Single(new TrafficLogReader(_path).ReadNew()).Opcode);
        }
    }
}
