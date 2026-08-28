using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Jondo.Unity.Launcher;
using Jondo.Unity.Server.Network;
using Xunit;

namespace Jondo.Unity.Tests.Network
{
    /// <summary>
    /// The world-entry sequence, checked against the capture it was decoded from.
    /// </summary>
    /// <remarks>
    /// Entering the world stopped being a replay of three .bin files and became
    /// <c>content/world/entry.json</c>, one row per frame with the protobuf fields written out. The
    /// point of that change is reviewability — the quest journal and the achievements of the
    /// recorded account travelled for months because nobody can diff a blob — and the risk of it is
    /// that a decoder or a builder gets one field wrong and the client silently stops working.
    ///
    /// So the test is not "it loads". It is that every frame the server will send is byte for byte
    /// the frame the capture holds, in the same order. If that holds, nothing downstream can behave
    /// differently, because nothing downstream can tell the difference.
    /// </remarks>
    public class WorldEntryContentTests
    {
        private const string Manifest = "world/entry.json";

        private static readonly (string Block, string File)[] Blocks =
        {
            (WorldEntry.BlockAfterCharacter, "world_etapa1_tras_elegir_personaje.bin"),
            (WorldEntry.BlockAfterConfirm, "world_etapa2_tras_confirmar.bin"),
            (WorldEntry.BlockMap, "world_etapa3_mapa.bin"),
        };

        /// <summary>
        /// What the manifest deliberately leaves out: the recorded account's own state.
        /// </summary>
        /// <remarks>
        /// These are not "not decoded yet". Each one was read and found to belong to the player who
        /// was recorded, so it is dropped rather than rebuilt, and the comparison below has to
        /// expect them missing or it would fail for the one reason that is correct.
        /// </remarks>
        private static readonly HashSet<string> Dropped = new HashSet<string>
        {
            "idu",   // 265 frames, one per quest of that account
            "idr",   // the same journal again, whole: 261 under way and 622 called finished
            "mft",   // its 954 achievements
            "ivi",   // its counters: 9,694 pairs of id and value
        };

        /// <summary>
        /// Frames the server fills in itself, so the manifest carries only the envelope.
        /// </summary>
        /// <remarks>
        /// The body is deliberately empty here and replaced in <c>WorldEntry.Rebuilt</c> with one
        /// built from the database — the character, its jobs, its spells, its inventory, its
        /// shortcut bars. Comparing those against the capture would be comparing this character
        /// with the recorded one, which is the very thing the rebuild exists to stop. What IS
        /// compared is that they are still there and still in the same place: a frame silently
        /// missing from the sequence is how the spell bar came up empty once already.
        /// </remarks>
        private static readonly HashSet<string> Rebuilt = new HashSet<string>
        {
            "kva", "irq", "hms", "ivx", "itg",
        };

        private static IEnumerable<byte[]> RawFrames(byte[] block)
        {
            int at = 0;
            while (at < block.Length)
            {
                int length = 0, shift = 0;
                while (at < block.Length)
                {
                    byte b = block[at++];
                    length |= (b & 0x7F) << shift;
                    if ((b & 0x80) == 0) break;
                    shift += 7;
                }

                if (length <= 0 || at + length > block.Length) yield break;
                yield return block[at..(at + length)];
                at += length;
            }
        }

        private static string OpcodeOf(byte[] frame)
        {
            // The type url is plain ASCII inside the frame, so finding it does not need the whole
            // envelope taken apart — and not taking it apart is the point: this test must not share
            // the parser it is checking, or a bug in that parser would agree with itself.
            byte[] needle = System.Text.Encoding.ASCII.GetBytes("type.ankama.com/");
            for (int i = 0; i + needle.Length + 3 <= frame.Length; i++)
            {
                bool hit = true;
                for (int j = 0; j < needle.Length; j++)
                {
                    if (frame[i + j] != needle[j]) { hit = false; break; }
                }

                if (hit)
                {
                    return System.Text.Encoding.ASCII.GetString(frame, i + needle.Length, 3);
                }
            }

            return "";
        }

        private static bool Available(out string reason)
        {
            reason = "";
            if (!File.Exists(Paths.ContentFile(Manifest)))
            {
                reason = "no manifest";
                return false;
            }

            foreach (var (_, file) in Blocks)
            {
                if (!File.Exists(Paths.Resolve(file))) { reason = "no " + file; return false; }
            }

            return true;
        }

        [Fact]
        public void Every_frame_is_the_captured_frame_byte_for_byte()
        {
            if (!Available(out _)) return;

            WorldEntryContent.Load(Paths.ContentFile(Manifest));
            Assert.True(WorldEntryContent.Ready, "el manifiesto no se ha leído");

            var wrong = new List<string>();
            foreach (var (block, file) in Blocks)
            {
                var expected = RawFrames(File.ReadAllBytes(Paths.Resolve(file)))
                    .Where(frame => !Dropped.Contains(OpcodeOf(frame)))
                    .ToList();
                var actual = WorldEntryContent.Frames(block);

                if (expected.Count != actual.Count)
                {
                    wrong.Add($"{block}: la captura tiene {expected.Count} tramas que se mandan y " +
                              $"el manifiesto {actual.Count}");
                    continue;
                }

                for (int i = 0; i < expected.Count; i++)
                {
                    string opcode = OpcodeOf(expected[i]);
                    if (OpcodeOf(actual[i]) != opcode)
                    {
                        wrong.Add($"{block}[{i}]: la captura trae {opcode} y el manifiesto " +
                                  $"{OpcodeOf(actual[i])}; el orden ha cambiado");
                        continue;
                    }

                    // Las que el servidor rehace llevan el sobre y el cuerpo vacío a propósito.
                    if (Rebuilt.Contains(opcode)) continue;

                    if (expected[i].AsSpan().SequenceEqual(actual[i])) continue;

                    wrong.Add($"{block}[{i}] ({opcode}): " +
                              $"{expected[i].Length} bytes en la captura, {actual[i].Length} construidos");
                }
            }

            Assert.True(wrong.Count == 0, string.Join("\n", wrong.Take(12)));
        }

        [Fact]
        public void Nothing_that_belongs_to_the_recorded_account_survives_in_the_manifest()
        {
            if (!Available(out _)) return;

            WorldEntryContent.Load(Paths.ContentFile(Manifest));

            // The check that would have caught idr months earlier: not "is idu gone", which was
            // true and useless, but "is anything at all left carrying that journal". A frame is
            // named by its opcode, so the whole list is asked rather than the one that was noticed.
            var found = new List<string>();
            foreach (var (block, _) in Blocks)
            {
                foreach (byte[] frame in WorldEntryContent.Frames(block))
                {
                    string opcode = OpcodeOf(frame);
                    if (Dropped.Contains(opcode)) found.Add($"{block}: {opcode}");
                }
            }

            Assert.True(found.Count == 0,
                "el manifiesto todavía manda: " + string.Join(", ", found.Distinct()));
        }

        [Fact]
        public void The_three_blocks_are_all_there_and_none_is_empty()
        {
            if (!Available(out _)) return;

            WorldEntryContent.Load(Paths.ContentFile(Manifest));

            // A block that reads as empty is the failure mode with no symptom until a client tries
            // to connect: the server sends nothing, the client waits, and the log says nothing was
            // wrong. Worth one assertion.
            foreach (var (block, _) in Blocks)
            {
                Assert.True(WorldEntryContent.Count(block) > 0, $"el bloque {block} está vacío");
            }
        }
    }
}
