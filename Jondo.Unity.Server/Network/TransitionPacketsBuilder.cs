using System;
using System.IO;
using Google.Protobuf;
using Jondo.Unity.Protocol;

namespace Jondo.Unity.Server.Network
{
    public static class TransitionPacketsBuilder
    {
        // ==========================================
        // GROUP 1: Empty / Default Payload Messages (12 packets)
        // ==========================================

        public static byte[] BuildLxsMessage() => BuildEmptyMessage(Op.Uri(Op.Lxs));
        public static byte[] BuildKltMessage() => BuildEmptyMessage("type.ankama.com/klt");
        public static byte[] BuildKlpMessage() => BuildEmptyMessage(Op.Uri(Op.Klp));

        /// <summary>Un mensaje con un unico varint en el f1.</summary>
        private static byte[] BuildSingleVarIntMessage(string typeUrl, int value)
        {
            using var ms = new MemoryStream();
            var output = new CodedOutputStream(ms);
            output.WriteTag((uint)((1 << 3) | 0));
            output.WriteInt32(value);
            output.Flush();
            return NetworkEnvelope.BuildGameNodePacket(typeUrl, ms.ToArray());
        }

        private static byte[] BuildEmptyMessage(string typeUrl)
        {
            return NetworkEnvelope.BuildGameNodePacket(typeUrl, Array.Empty<byte>());
        }

        // ==========================================
        // GROUP 2: Simple Field Messages (12 packets)
        // ==========================================

        public static byte[] BuildKkpMessage() => BuildEmptyMessage("type.ankama.com/kkp");
        public static byte[] BuildKkmMessage() => BuildEmptyMessage(Op.Uri(Op.Kkm));
        public static byte[] BuildKrbMessage() => BuildSingleVarIntMessage("type.ankama.com/krb", Jondo.Unity.Server.Network.SessionContext.State.CharacterRemainingPoints);
        public static byte[] BuildIlcMessage()
        {
            byte[] payload = new byte[] {
                0x0A, 0x16, 0x0A, 0x0F, 0x10, 0xE0, 0xE3, 0xFE, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0x01, 0x1A, 0x02, 0xEA, 0x0C, 0x10, 0x86, 0x90, 0x90, 0x49
            };
            return NetworkEnvelope.BuildGameNodePacket(Op.Uri(Op.Ilc), payload);
        }

        public static byte[] BuildJohMessage()
        {
            using var ms = new MemoryStream();
            var output = new CodedOutputStream(ms);
            output.WriteTag((uint)((2 << 3) | 0)); // Field 2, VarInt
            output.WriteInt64(Jondo.Unity.Server.Network.SessionContext.State.MapId > 0 ? Jondo.Unity.Server.Network.SessionContext.State.MapId : 154011397);
            output.Flush();
            return NetworkEnvelope.BuildGameNodePacket(Op.Uri(Op.Joh), ms.ToArray());
        }

        // ==========================================
        // GROUP 3: Complex Structured Messages (9 packets + Map/System Bursts)
        // ==========================================

        public static byte[] BuildIcgMessage()
        {
            using var ms = new MemoryStream();
            var output = new CodedOutputStream(ms);

            // Inner nested Field 1
            using var f1Ms = new MemoryStream();
            var f1Out = new CodedOutputStream(f1Ms);

            // Nested Field 3 inside Field 1
            using var f3Ms = new MemoryStream();
            var f3Out = new CodedOutputStream(f3Ms);
            f3Out.WriteTag((uint)((1 << 3) | 0));
            f3Out.WriteInt32(2251);

            // First Nested Field 2 inside Field 3
            using var f2Ms1 = new MemoryStream();
            var f2Out1 = new CodedOutputStream(f2Ms1);
            f2Out1.WriteTag((uint)((2 << 3) | 0));
            f2Out1.WriteInt32(9738);
            f2Out1.WriteTag((uint)((4 << 3) | 0));
            f2Out1.WriteInt32(1);
            f2Out1.Flush();
            f3Out.WriteTag((uint)((2 << 3) | 2));
            f3Out.WriteBytes(ByteString.CopyFrom(f2Ms1.ToArray()));

            // Second Nested Field 2 inside Field 3
            using var f2Ms2 = new MemoryStream();
            var f2Out2 = new CodedOutputStream(f2Ms2);
            f2Out2.WriteTag((uint)((2 << 3) | 0));
            f2Out2.WriteInt32(9739);
            f2Out2.WriteTag((uint)((4 << 3) | 0));
            f2Out2.WriteInt32(1);
            f2Out2.Flush();
            f3Out.WriteTag((uint)((2 << 3) | 2));
            f3Out.WriteBytes(ByteString.CopyFrom(f2Ms2.ToArray()));

            f3Out.Flush();
            f1Out.WriteTag((uint)((3 << 3) | 2));
            f1Out.WriteBytes(ByteString.CopyFrom(f3Ms.ToArray()));

            f1Out.WriteTag((uint)((4 << 3) | 0));
            f1Out.WriteInt32(1631);

            f1Out.Flush();
            output.WriteTag((uint)((1 << 3) | 2));
            output.WriteBytes(ByteString.CopyFrom(f1Ms.ToArray()));

            output.Flush();
            return NetworkEnvelope.BuildGameNodePacket("type.ankama.com/icg", ms.ToArray());
        }

        // ==========================================
        // ADDITIONAL SYSTEM / INITIALIZATION BUILDERS
        // ==========================================

        public static byte[] BuildLpeMessage()
        {
            using var ms = new MemoryStream();
            var output = new CodedOutputStream(ms);

            using var f2Ms = new MemoryStream();
            var f2Out = new CodedOutputStream(f2Ms);
            f2Out.WriteTag((uint)((1 << 3) | 2));
            f2Out.WriteString("f6e20e09-19d9-4b1a-9170-6d2d089127c5");
            f2Out.Flush();

            output.WriteTag((uint)((2 << 3) | 2));
            output.WriteBytes(ByteString.CopyFrom(f2Ms.ToArray()));
            output.Flush();

            return NetworkEnvelope.BuildGameNodePacket(Op.Uri(Op.Lpe), ms.ToArray());
        }

        public static byte[][] BuildLorList()
        {
            long[] ids = { 1121087878833, 1121084896687 };
            byte[][] list = new byte[ids.Length][];
            for (int i = 0; i < ids.Length; i++)
            {
                using var ms = new MemoryStream();
                var output = new CodedOutputStream(ms);
                output.WriteTag((uint)((1 << 3) | 0));
                output.WriteInt32(120);
                output.WriteTag((uint)((2 << 3) | 0));
                output.WriteInt64(ids[i]);
                output.Flush();
                list[i] = NetworkEnvelope.BuildGameNodePacket("type.ankama.com/lor", ms.ToArray());
            }
            return list;
        }

        /// <summary>
        /// hmd = SPELL BOOK: the spells the character has learned. This is what decides whether a
        /// spell can be cast; the shortcut bar (itp) only decides where it is drawn.
        ///
        /// This used to be four spells written as raw bytes -32426, 32435, 32443 and 32455-, which
        /// turn out to be the four minimum-level-1 Cra spells, the ones of the captured character.
        /// That is why in a fight only those four could be cast and the rest came out greyed out no
        /// matter what the bar showed: they were in the bar but not in the book.
        ///
        /// Layout, identical to the fight spell list (jvn):
        ///   hmd { f1 = 1,
        ///         f3 { f3 = 2, f4 = 1 },               &lt;- the weapon, with no id
        ///         f3 { f1 = spell, f3 = 1, f4 = 1 } }  &lt;- one per spell
        /// </summary>
        public static byte[] BuildHmdMessage()
        {
            var spells = DatabaseManager.GetPlayerAvailableSpells(Jondo.Unity.Server.Network.SessionContext.State.Breed, Jondo.Unity.Server.Network.SessionContext.State.CharacterLevel);

            using var ms = new MemoryStream();
            var output = new CodedOutputStream(ms);

            output.WriteTag((uint)((1 << 3) | 0));
            output.WriteInt32(1);

            void Entry(int? spellId)
            {
                using var eMs = new MemoryStream();
                var e = new CodedOutputStream(eMs);
                if (spellId.HasValue)
                {
                    e.WriteTag((uint)((1 << 3) | 0));
                    e.WriteInt32(spellId.Value);
                }
                e.WriteTag((uint)((3 << 3) | 0));
                e.WriteInt32(spellId.HasValue ? 1 : 2);   // 1 = spell, 2 = weapon
                e.WriteTag((uint)((4 << 3) | 0));
                e.WriteInt32(1);
                e.Flush();
                output.WriteTag((uint)((3 << 3) | 2));
                output.WriteBytes(ByteString.CopyFrom(eMs.ToArray()));
            }

            Entry(null);
            foreach (int id in spells) Entry(id);

            output.Flush();

            Program.LogDebug($"[TransitionPackets] Spell book (hmd) with {spells.Count} spell(s) " +
                             $"for breed {Jondo.Unity.Server.Network.SessionContext.State.Breed} at level {Jondo.Unity.Server.Network.SessionContext.State.CharacterLevel}.");

            return NetworkEnvelope.BuildGameNodePacket(Op.Uri(Op.Hmd), ms.ToArray());
        }

        /// <summary>
        /// Spell shortcut bar (itp). This is the one shown in roleplay mode and during the
        /// placement phase before a fight.
        ///
        /// This used to be four hand-written ids -32426, 32435, 32443 and 32455-, the ones the
        /// character of the reference capture had on its bar. That is why the player always saw
        /// four spells outside of a fight no matter how high the level, while inside the fight
        /// (where jvn rules) all of them showed up.
        ///
        /// Layout: itp { f1 = bar type, repeated f2 { f3 = slot, f4 { f1 = spell } } }.
        /// The first entry carries no slot and an empty f4, just like in the capture.
        /// </summary>
        public static byte[][] BuildItpList()
        {
            var spells = DatabaseManager.GetPlayerAvailableSpells(Jondo.Unity.Server.Network.SessionContext.State.Breed, Jondo.Unity.Server.Network.SessionContext.State.CharacterLevel);

            using var ms = new MemoryStream();
            var output = new CodedOutputStream(ms);

            output.WriteTag((uint)((1 << 3) | 0));
            output.WriteInt32(1);

            using (var emptyMs = new MemoryStream())
            {
                var emptyOut = new CodedOutputStream(emptyMs);
                emptyOut.WriteTag((uint)((4 << 3) | 2));
                emptyOut.WriteBytes(ByteString.Empty);
                emptyOut.Flush();
                output.WriteTag((uint)((2 << 3) | 2));
                output.WriteBytes(ByteString.CopyFrom(emptyMs.ToArray()));
            }

            int slot = 1;
            foreach (int spellId in spells)
            {
                using var slotMs = new MemoryStream();
                var slotOut = new CodedOutputStream(slotMs);

                slotOut.WriteTag((uint)((3 << 3) | 0));
                slotOut.WriteInt32(slot++);

                using (var spellMs = new MemoryStream())
                {
                    var spellOut = new CodedOutputStream(spellMs);
                    spellOut.WriteTag((uint)((1 << 3) | 0));
                    spellOut.WriteInt32(spellId);
                    spellOut.Flush();
                    slotOut.WriteTag((uint)((4 << 3) | 2));
                    slotOut.WriteBytes(ByteString.CopyFrom(spellMs.ToArray()));
                }

                slotOut.Flush();
                output.WriteTag((uint)((2 << 3) | 2));
                output.WriteBytes(ByteString.CopyFrom(slotMs.ToArray()));
            }

            output.Flush();
            byte[] itpSpells = ms.ToArray();

            Program.LogDebug($"[TransitionPackets] Spell bar with {spells.Count} spell(s) " +
                             $"for breed {Jondo.Unity.Server.Network.SessionContext.State.Breed} at level {Jondo.Unity.Server.Network.SessionContext.State.CharacterLevel}.");

            return new byte[][] {
                NetworkEnvelope.BuildGameNodePacket(Op.Uri(Op.Itp), itpSpells),
                NetworkEnvelope.BuildGameNodePacket(Op.Uri(Op.Itp), itpSpells),
                NetworkEnvelope.BuildGameNodePacket(Op.Uri(Op.Itp), Array.Empty<byte>())
            };
        }

        public static byte[] BuildIthMessage()
        {
            // Return the pre-recorded complete ith packet containing the official world prisms database
            // to populate the client's cartography prism registry and prevent null reference crashes
            return TransitionPayloads.ith;
        }
    }
}
