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
            "iel",   // its followed quests, 1869 and 2406, in the little box in the corner
            "mft",   // its 954 achievements
            "ivi",   // its counters: 9,694 pairs of id and value
        };

        /// <summary>
        /// Quest ids belonging to the recorded player. None of them may reach the wire.
        /// </summary>
        /// <remarks>
        /// 1869 is "El daño de Búril" and 2406 "Cuando el despertar no es más que un sueño". They
        /// outlived the removal of the journal because they travelled in a different message: the
        /// iel is the followed-quest box, not the journal, and it REPLACES the list rather than
        /// adding to it. So the server sent the right journal -- the log said so -- and the box
        /// still showed two quests belonging to somebody else and none belonging to the character.
        /// A quest just picked up did appear, because the live ief adds it; logging back in made it
        /// vanish again.
        /// </remarks>
        private static readonly int[] RecordedQuests = { 1869, 2406 };

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
            "kva", "hms", "ivx", "itg",
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
                var actual = WorldEntryContent.Rows(block);

                if (expected.Count != actual.Count)
                {
                    wrong.Add($"{block}: la captura tiene {expected.Count} tramas que se mandan y " +
                              $"el manifiesto {actual.Count}");
                    continue;
                }

                for (int i = 0; i < expected.Count; i++)
                {
                    string opcode = OpcodeOf(expected[i]);
                    if (OpcodeOf(actual[i].Frame) != opcode)
                    {
                        wrong.Add($"{block}[{i}]: la captura trae {opcode} y el manifiesto " +
                                  $"{OpcodeOf(actual[i].Frame)}; el orden ha cambiado");
                        continue;
                    }

                    // Las que el servidor rehace llevan el sobre y el cuerpo vacío a propósito.
                    if (Rebuilt.Contains(opcode)) continue;

                    if (expected[i].AsSpan().SequenceEqual(actual[i].Frame)) continue;

                    wrong.Add($"{block}[{i}] ({opcode}): " +
                              $"{expected[i].Length} bytes en la captura, {actual[i].Frame.Length} construidos");
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
                foreach (var row in WorldEntryContent.Rows(block))
                {
                    if (Dropped.Contains(row.Opcode)) found.Add($"{block}: {row.Opcode}");
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

        [Fact]
        public void The_two_shortcut_bars_are_told_apart_by_the_manifest()
        {
            if (!Available(out _)) return;

            WorldEntryContent.Load(Paths.ContentFile(Manifest));

            // There are two itg frames and the only thing separating them now is this label. The
            // server used to look inside the payload — f6 items, f9 spells — and that payload is no
            // longer carried, so if the label ever stops matching what WorldEntry compares against,
            // BOTH bars come out as the item bar and the spell bar is empty. That has happened once
            // already by another route, and it is invisible until a client connects.
            var bars = new List<string>();
            foreach (var (block, _) in Blocks)
            {
                foreach (var row in WorldEntryContent.Rows(block))
                {
                    if (row.Opcode == "itg") bars.Add(row.Built);
                }
            }

            Assert.Equal(2, bars.Count);
            Assert.Single(bars.Where(bar => bar == WorldEntry.SpellBarLabel));

            // And the RIGHT one of the two, which the count alone does not say. The label was
            // written backwards once, from a comment in the source that claimed "f6 for items and
            // f9 for spells" while the code three methods below it did the opposite and was right.
            // So this is measured from the capture instead of read from anybody's prose: the slots
            // of the spell bar carry field 6, and its 44 values are real spell ids.
            byte[] mapBlock = File.ReadAllBytes(Paths.Resolve("world_etapa3_mapa.bin"));
            var captured = RawFrames(mapBlock).Where(f => OpcodeOf(f) == "itg").ToList();
            var rows = WorldEntryContent.Rows(WorldEntry.BlockMap)
                .Where(r => r.Opcode == "itg").ToList();

            Assert.Equal(captured.Count, rows.Count);
            for (int i = 0; i < captured.Count; i++)
            {
                bool spells = CarriesField6(captured[i]);
                bool labelled = rows[i].Built == WorldEntry.SpellBarLabel;
                Assert.True(spells == labelled,
                    $"la barra itg[{i}] lleva f6={spells} y el manifiesto la etiqueta como " +
                    $"«{rows[i].Built}»: una de las dos está al revés");
            }
        }

        /// <summary>Whether any slot of this itg carries field 6, which is the spell bar.</summary>
        private static bool CarriesField6(byte[] frame)
        {
            byte[]? payload = ConnectionProtocol.ReadPayload(frame, "itg");
            if (payload == null) return false;

            foreach (var entry in ProtoMessage.Parse(payload).Fields)
            {
                if (entry.FieldNumber != 1 || entry.WireType != 2) continue;
                foreach (var slot in ProtoMessage.Parse(entry.BytesValue).Fields)
                {
                    if (slot.FieldNumber == 6) return true;
                }
            }

            return false;
        }


        [Fact]
        public void Nothing_in_the_manifest_carries_the_recorded_character()
        {
            if (!Available(out _)) return;

            // The guard that matters, and the one that would have caught every leak this file has
            // had. Not "is opcode X gone" — that question has been answered yes while the same data
            // walked out through opcode Y twice — but "is the recorded character's id or name
            // anywhere in what we are about to send".
            //
            // The id and the name are read out of the capture at run time rather than written here:
            // they belong to a real player and have no business in source. That also means this
            // test keeps working if the blocks are ever regenerated from a different session.
            byte[] block = File.ReadAllBytes(Paths.Resolve("world_etapa1_tras_elegir_personaje.bin"));
            byte[]? kva = RawFrames(block)
                .Select(frame => ConnectionProtocol.ReadPayload(frame, "kva"))
                .FirstOrDefault(payload => payload != null && payload.Length > 0);

            Assert.NotNull(kva);

            long id = BiggestNumberIn(kva!);
            Assert.True(id > 0, "no se ha podido leer el id del personaje capturado");

            WorldEntryContent.Load(Paths.ContentFile(Manifest));

            var carrying = new List<string>();
            foreach (var (name, _) in Blocks)
            {
                foreach (var row in WorldEntryContent.Rows(name))
                {
                    if (Contains(row.Frame, id)) carrying.Add($"{name}: {row.Opcode}");
                }
            }

            Assert.True(carrying.Count == 0,
                "el manifiesto sigue llevando el id del personaje grabado en: " +
                string.Join(", ", carrying.Distinct()));
        }

        /// <summary>The largest varint anywhere in a message. The character id is the big one.</summary>
        private static long BiggestNumberIn(byte[] payload)
        {
            long biggest = 0;
            foreach (var field in ProtoMessage.Parse(payload).Fields)
            {
                if (field.WireType == 0) biggest = Math.Max(biggest, field.VarIntValue);
                else if (field.WireType == 2 && field.BytesValue is { Length: > 0 })
                {
                    try { biggest = Math.Max(biggest, BiggestNumberIn(field.BytesValue)); }
                    catch (Exception) { /* not a message; nothing to read */ }
                }
            }

            return biggest;
        }

        /// <summary>Whether the frame carries that number as a varint, at any depth.</summary>
        /// <remarks>
        /// Searched as bytes rather than by walking the tree, because the tree walk would have to
        /// agree with the very parser under test. A varint is a unique byte sequence, so finding it
        /// is enough to say the number is in there.
        /// </remarks>
        private static bool Contains(byte[] frame, long number)
        {
            var needle = new List<byte>();
            ulong left = (ulong)number;
            do
            {
                byte b = (byte)(left & 0x7F);
                left >>= 7;
                needle.Add(left != 0 ? (byte)(b | 0x80) : b);
            }
            while (left != 0);

            for (int i = 0; i + needle.Count <= frame.Length; i++)
            {
                bool hit = true;
                for (int j = 0; j < needle.Count; j++)
                {
                    if (frame[i + j] != needle[j]) { hit = false; break; }
                }

                if (hit) return true;
            }

            return false;
        }


        [Fact]
        public void The_jobs_frame_keeps_the_body_the_server_reads_from_it()
        {
            if (!Available(out _)) return;

            // El irq es el unico que el servidor rehace Y necesita leer: su cuerpo dice QUE oficios
            // existen y en que orden los quiere el cliente, que son datos del juego. Quitarselo dejo
            // al personaje entrando sin un solo oficio durante horas, y sin ruido: ReadPayload
            // devuelve un array vacio en vez de null, asi que la guarda no salto, el bucle no dio
            // una vuelta y la linea de consola se suprimia sola al ser cero.
            //
            // El catalogo no vale de sustituto: la tabla Jobs trae 23 y la captura lista 20.
            WorldEntryContent.Load(Paths.ContentFile(Manifest));

            var irq = WorldEntryContent.Rows(WorldEntry.BlockAfterCharacter)
                .FirstOrDefault(row => row.Opcode == "irq");

            Assert.NotNull(irq);

            byte[]? payload = ConnectionProtocol.ReadPayload(irq!.Frame, "irq");
            Assert.NotNull(payload);
            Assert.True(payload!.Length > 0, "el irq del manifiesto viene sin cuerpo");

            int oficios = ProtoMessage.Parse(payload).Fields.Count(f => f.FieldNumber == 1);
            Assert.Equal(20, oficios);
        }

        [Fact]
        public void The_followed_quest_box_of_the_recorded_player_is_not_sent()
        {
            // The frame itself. It was the last piece of that player's quest state still on the
            // wire after the journal had been taken out of both blocks.
            WorldEntryContent.Load(Paths.ContentFile(Manifest));

            foreach (string block in new[]
                     { WorldEntry.BlockAfterCharacter, WorldEntry.BlockAfterConfirm, WorldEntry.BlockMap })
            {
                Assert.DoesNotContain(WorldEntryContent.Rows(block), row => row.Opcode == "iel");
            }
        }

        [Fact]
        public void And_neither_are_the_quest_ids_it_carried()
        {
            // Belt and braces, and not the same assertion: this one would still fail if those two
            // quests came back inside some other opcode, which is exactly how they survived the
            // first two attempts at removing them.
            WorldEntryContent.Load(Paths.ContentFile(Manifest));

            foreach (int questId in RecordedQuests)
            {
                byte[] varint = Varint(questId);

                foreach (string block in new[]
                         { WorldEntry.BlockAfterCharacter, WorldEntry.BlockAfterConfirm, WorldEntry.BlockMap })
                {
                    foreach (var row in WorldEntryContent.Rows(block))
                    {
                        Assert.False(Contains(row.Frame, varint),
                                     $"la misión {questId} del jugador grabado sigue viajando en un {row.Opcode}");
                    }
                }
            }
        }

        private static byte[] Varint(int value)
        {
            var bytes = new List<byte>();
            uint left = (uint)value;
            while (true)
            {
                byte piece = (byte)(left & 0x7F);
                left >>= 7;
                if (left == 0) { bytes.Add(piece); break; }
                bytes.Add((byte)(piece | 0x80));
            }
            return bytes.ToArray();
        }

        private static bool Contains(byte[] haystack, byte[] needle)
        {
            for (int i = 0; i + needle.Length <= haystack.Length; i++)
            {
                bool hit = true;
                for (int j = 0; j < needle.Length; j++)
                {
                    if (haystack[i + j] != needle[j]) { hit = false; break; }
                }

                if (hit) return true;
            }
            return false;
        }
    }
}
