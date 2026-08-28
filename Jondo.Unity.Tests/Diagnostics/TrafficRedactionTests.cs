using System;
using System.IO;
using System.Linq;
using System.Text;
using Jondo.Unity.Server;
using Jondo.Unity.Server.Diagnostics;
using Xunit;

namespace Jondo.Unity.Tests.Diagnostics
{
    /// <summary>
    /// The session token, and whether it is still readable in the traffic log.
    /// </summary>
    public class TrafficRedactionTests
    {
        private static byte[] Frame(string text) => Encoding.ASCII.GetBytes(text);

        [Fact]
        public void A_token_is_not_readable_afterwards()
        {
            string token = Guid.NewGuid().ToString("N");
            byte[] frame = Frame("GET /connect?token=" + token + " HTTP/1.1");

            string after = Encoding.ASCII.GetString(TrafficRedaction.Scrub(frame, frame.Length));

            Assert.DoesNotContain(token, after);
            Assert.Contains("GET /connect?token=", after);
        }

        [Fact]
        public void And_it_is_not_readable_on_the_hex_line_either()
        {
            // The half a string-level mask would have missed. The traffic log prints every frame
            // twice, as text and as hex; masking the text and leaving the hex means the token is
            // still there, one ASCII byte per pair, on the line directly above.
            string token = Guid.NewGuid().ToString("N");
            byte[] frame = Frame("token=" + token);

            byte[] clean = TrafficRedaction.Scrub(frame, frame.Length);
            string hex = BitConverter.ToString(clean).Replace("-", "");
            string tokenAsAsciiHex = string.Concat(token.Select(c => ((int)c).ToString("X2")));

            Assert.DoesNotContain(tokenAsAsciiHex, hex);
        }

        [Fact]
        public void Two_tokens_in_one_frame_both_go()
        {
            string one = Guid.NewGuid().ToString("N");
            string other = Guid.NewGuid().ToString("N");
            byte[] frame = Frame(one + " y " + other);

            string after = Encoding.ASCII.GetString(TrafficRedaction.Scrub(frame, frame.Length));

            Assert.DoesNotContain(one, after);
            Assert.DoesNotContain(other, after);
            Assert.Contains(" y ", after);
        }

        [Fact]
        public void A_token_at_the_very_end_goes_too()
        {
            // The off-by-one this kind of scan gets wrong: the run ends because the buffer ends,
            // not because a non-hex byte turned up. The loop goes one past the length on purpose.
            byte[] frame = Frame("t=" + Guid.NewGuid().ToString("N"));

            string after = Encoding.ASCII.GetString(TrafficRedaction.Scrub(frame, frame.Length));

            Assert.Equal("t=" + new string('x', 32), after);
        }

        [Fact]
        public void Ordinary_traffic_comes_through_byte_for_byte()
        {
            // What the log exists for. If protobuf frames came back altered, the redaction would
            // have cost more than the token was worth.
            byte[] frame = { 0x0A, 0x13, 0x74, 0x79, 0x70, 0x65, 0x2E, 0x61, 0x6E, 0x6B, 0x61, 0x6D, 0x61 };

            Assert.Equal(frame, TrafficRedaction.Scrub(frame, frame.Length));
        }

        [Fact]
        public void A_frame_with_nothing_to_hide_is_not_even_copied()
        {
            // Runs twice per frame on the hot path. Allocating a copy of every combat packet so it
            // can come out identical is the kind of cost that gets a good idea reverted.
            byte[] frame = Frame("Sacri-Master entra en el mapa");

            Assert.Same(frame, TrafficRedaction.Scrub(frame, frame.Length));
        }

        [Theory]
        [InlineData(31)]   // one short of a token: left alone
        [InlineData(32)]   // a token
        [InlineData(40)]   // longer than a token, still masked: nothing legitimate looks like this
        public void The_threshold_is_the_length_of_a_guid(int hexChars)
        {
            byte[] frame = Frame(new string('a', hexChars));

            string after = Encoding.ASCII.GetString(TrafficRedaction.Scrub(frame, frame.Length));

            Assert.Equal(hexChars < TrafficRedaction.TokenLength
                             ? new string('a', hexChars)
                             : new string('x', hexChars), after);
        }

        [Fact]
        public void Nothing_in_gives_nothing_back_rather_than_throwing()
        {
            Assert.Empty(TrafficRedaction.Scrub(Array.Empty<byte>(), 0));
            Assert.Empty(TrafficRedaction.Scrub(null!, 10));
            Assert.Empty(TrafficRedaction.Scrub(new byte[] { 1, 2 }, 0));
        }

        [Fact]
        public void A_length_longer_than_the_array_is_clamped_not_thrown()
        {
            byte[] frame = { 1, 2, 3 };
            Assert.Equal(frame, TrafficRedaction.Scrub(frame, 9999));
        }
    }

    /// <summary>
    /// The cap on the log files, which until now there was not one of.
    /// </summary>
    /// <remarks>
    /// The traffic log had reached 112 MB on this machine with nothing in the code that would ever
    /// have stopped it. A disk that fills up stops the server, so an unbounded debug log is not only
    /// a mess: it is a way to take the server down by playing on it for long enough.
    /// </remarks>
    public class LogRotationTests : IDisposable
    {
        private readonly string _folder =
            Path.Combine(Path.GetTempPath(), "jondo-log-" + Guid.NewGuid().ToString("N"));

        public LogRotationTests() => Directory.CreateDirectory(_folder);

        public void Dispose()
        {
            try { Directory.Delete(_folder, recursive: true); } catch { }
        }

        private string At(string name) => Path.Combine(_folder, name);

        [Fact]
        public void The_live_file_never_grows_far_past_the_cap()
        {
            string live = At("traffic.log");
            var log = new LogFile(() => live);

            string line = new string('a', 64 * 1024);
            for (int i = 0; i < (int)(LogFile.MaxBytes / line.Length) + 4; i++) log.WriteLine(line);

            // Slightly over is expected: the counter is in characters, so it trips on the write
            // that crosses the line rather than before it.
            long size = new FileInfo(live).Length;
            Assert.True(size < LogFile.MaxBytes + line.Length * 2L,
                        $"el fichero vivo se quedó en {size} bytes");
            Assert.True(File.Exists(live + ".1"), "no se ha rotado");
        }

        [Fact]
        public void Only_so_many_old_files_are_kept()
        {
            string live = At("debug.log");
            var log = new LogFile(() => live);

            string line = new string('b', 256 * 1024);
            long lines = LogFile.MaxBytes / line.Length;
            for (int i = 0; i < lines * (LogFile.Keep + 3); i++) log.WriteLine(line);

            Assert.False(File.Exists(live + "." + (LogFile.Keep + 1)),
                         "hay más ficheros viejos de los que se prometió guardar");
            Assert.True(File.Exists(live + "." + LogFile.Keep));
        }

        [Fact]
        public void What_was_already_there_counts_towards_the_cap()
        {
            // A server restarted every ten minutes would otherwise never rotate at all: each run
            // would start its own count from zero and append to the same growing file.
            string live = At("activity.log");
            File.WriteAllBytes(live, new byte[LogFile.MaxBytes - 16]);

            var log = new LogFile(() => live);
            log.WriteLine(new string('c', 64));

            Assert.True(File.Exists(live + ".1"), "no contó lo que ya había en el fichero");
        }
    }
}
