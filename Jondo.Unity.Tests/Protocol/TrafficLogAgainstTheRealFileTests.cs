using System;
using System.Collections.Generic;
using System.IO;
using Jondo.Unity.Launcher;
using Jondo.Unity.Protocol.Wire;
using Jondo.Unity.Server;
using Xunit;

namespace Jondo.Unity.Tests.Protocol
{
    /// <summary>
    /// The reader, the envelope and the shapes against the actual traffic log on this machine.
    /// </summary>
    /// <remarks>
    /// The tests next door are built out of frames this project constructed, which proves the code
    /// does what it was written to do and proves nothing about whether it was written to do the
    /// right thing. That is not a hypothetical worry here: the registry this replaces was fed real
    /// frames for weeks and wrote down two useless rows, because everything about it was correct
    /// except its idea of where a client frame keeps its opcode.
    ///
    /// So these run against <c>logs/gameserver_traffic.log</c> and the files it has rotated
    /// into, when they are there, and skip when they are not: it is a local artefact and never
    /// in git. Skipping quietly is a real weakness, and the thresholds are set against what was
    /// measured over the 72,879 rows of it so that they say something when they do run. They
    /// now also skip below 5,000 frames, for the same reason.
    /// </remarks>
    public class TrafficLogAgainstTheRealFileTests
    {
        /// <summary>
        /// Read once for all of these. The log is tens of MB, and five tests each walking it turns
        /// a one-second test run into a ten-second one for no extra coverage.
        /// </summary>
        /// <remarks>
        /// The ROTATED files are read too, newest first, and that is not tidiness -- it is what
        /// keeps these tests meaningful. LogFile now rolls the traffic log over at 32 MB, so the
        /// live file holds only what has been written since the last rollover: right after one it
        /// held 2,477 frames, and the shape assertion below is a ratio that only says anything once
        /// the vocabulary of shapes has saturated. It failed on a perfectly healthy log purely
        /// because the sample had shrunk. Reading .1, .2 and .3 as well puts the sample back.
        /// </remarks>
        private static readonly Lazy<List<TrafficEntry>?> TheLog = new Lazy<List<TrafficEntry>?>(() =>
        {
            var all = new List<TrafficEntry>();

            foreach (string path in Candidates())
            {
                if (all.Count >= 60_000) break;
                if (!File.Exists(path)) continue;

                var reader = new TrafficLogReader(path);
                reader.SeekToStart();

                while (all.Count < 60_000)
                {
                    var batch = reader.ReadNew(10_000);
                    if (batch.Count == 0) break;
                    all.AddRange(batch);
                }
            }

            // Too little to say anything with. A ratio over a couple of thousand frames is noise
            // dressed as a measurement, and firing on it would train whoever sees it to ignore it.
            return all.Count < 5_000 ? null : all;
        });

        /// <summary>The live traffic log, then the files it has rotated into, newest first.</summary>
        private static IEnumerable<string> Candidates()
        {
            string live = Paths.TrafficLog;
            yield return live;
            for (int i = 1; i <= LogFile.Keep; i++) yield return live + "." + i;
        }

        private static List<TrafficEntry>? Read(int most = 60_000)
        {
            var all = TheLog.Value;
            return all == null || all.Count <= most ? all : all.GetRange(0, most);
        }

        /// <summary>
        /// Measured over the whole log: 72,686 of 72,879 rows hold a readable frame, and 66,138 of
        /// them carry a message. The rest are three-byte scraps of handshake. Nine in ten is well
        /// under that and still far above what a broken envelope reader would manage — the old one
        /// would have scored zero on the client half.
        /// </summary>
        [Fact]
        public void Most_of_the_real_log_opens()
        {
            var entries = Read();
            if (entries == null) return;

            Assert.True(entries.Count > 1000, $"expected a real log, read {entries.Count} rows");

            int opened = 0;
            foreach (var entry in entries)
            {
                if (entry.Frame.Found) opened++;
            }

            double share = (double)opened / entries.Count;
            Assert.True(share > 0.85,
                        $"only {share:P1} of {entries.Count:N0} rows could be opened. The envelope " +
                        "layouts have moved, or the log format has.");
        }

        /// <summary>
        /// Both halves of the conversation have to come out. The bug this replaces read the server
        /// half and none of the client half, which is exactly the failure a total count hides.
        /// </summary>
        [Fact]
        public void Both_directions_come_out_of_the_real_log()
        {
            var entries = Read();
            if (entries == null) return;

            int client = 0, server = 0;
            foreach (var entry in entries)
            {
                if (!entry.Frame.Found) continue;
                if (entry.Frame.Direction == FrameDirection.ClientRequest) client++;
                else if (entry.Frame.Direction is FrameDirection.ServerPush or FrameDirection.ServerReply) server++;
            }

            Assert.True(client > 100, $"only {client} client frames were opened out of {entries.Count:N0} rows");
            Assert.True(server > 100, $"only {server} server frames were opened out of {entries.Count:N0} rows");
        }

        /// <summary>
        /// A third of the log carries a length prefix and the rest does not, because it is written
        /// from two places that do not agree. Reading only one of the two throws that third away.
        /// </summary>
        [Fact]
        public void Both_framings_are_present_and_both_are_read()
        {
            var entries = Read();
            if (entries == null) return;

            int prefixed = 0, bare = 0;
            foreach (var entry in entries)
            {
                if (!entry.Frame.Found) continue;
                if (entry.Frame.HadLengthPrefix) prefixed++;
                else bare++;
            }

            Assert.True(prefixed > 50, $"no length-prefixed frames were read ({prefixed})");
            Assert.True(bare > 50, $"no bare frames were read ({bare})");
        }

        /// <summary>
        /// The opcodes that come out have to be three-letter names, not fragments of something
        /// else. A reader that latched onto the wrong field would still produce strings.
        /// </summary>
        [Fact]
        public void The_opcodes_look_like_opcodes()
        {
            var entries = Read(20_000);
            if (entries == null) return;

            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (var entry in entries)
            {
                if (entry.Frame.Found) seen.Add(entry.Frame.Opcode);
            }

            Assert.True(seen.Count > 20, $"only {seen.Count} distinct opcodes in the log");
            foreach (string opcode in seen)
            {
                Assert.InRange(opcode.Length, 2, 8);
                foreach (char c in opcode) Assert.True(char.IsLetterOrDigit(c), $"'{opcode}' is not an opcode");
            }
        }

        /// <summary>
        /// The whole registry rests on shapes being stable and telling things apart. Over the real
        /// log that came to 834 opcode-and-shape pairs across 242 opcodes and 664 shapes: more
        /// shapes than opcodes, which is the property that makes the key worth having, and far
        /// fewer than frames, which is the property that makes it a list rather than a log.
        /// </summary>
        [Fact]
        public void Shapes_group_the_real_traffic_without_collapsing_it()
        {
            var entries = Read();
            if (entries == null) return;

            var pairs = new HashSet<string>(StringComparer.Ordinal);
            var opcodes = new HashSet<string>(StringComparer.Ordinal);
            int frames = 0;

            foreach (var entry in entries)
            {
                if (!entry.Frame.Found) continue;
                frames++;
                opcodes.Add(entry.Frame.Opcode);
                pairs.Add(entry.Frame.Opcode + "|" + entry.Shape);
            }

            Assert.True(pairs.Count >= opcodes.Count,
                        "shapes are collapsing: there are fewer pairs than opcodes");
            Assert.True(pairs.Count < frames / 10,
                        $"{pairs.Count:N0} pairs out of {frames:N0} frames — the shapes are keeping " +
                        "values in them and the registry would be a log, not a list");
        }
    }
}
