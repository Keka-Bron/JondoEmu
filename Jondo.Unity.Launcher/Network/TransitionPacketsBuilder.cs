using System;
using System.IO;
using Google.Protobuf;

namespace Jondo.Unity.Launcher.Network
{
    public static class TransitionPacketsBuilder
    {
        // ==========================================
        // GROUP 1: Empty / Default Payload Messages (12 packets)
        // ==========================================

        public static byte[] BuildHmlMessage() => BuildEmptyMessage("type.ankama.com/hml");
        public static byte[] BuildLolMessage() => BuildEmptyMessage("type.ankama.com/lol");
        public static byte[] BuildHmjMessage() => BuildEmptyMessage("type.ankama.com/hmj");
        public static byte[] BuildLxsMessage() => BuildEmptyMessage("type.ankama.com/lxs");
        public static byte[] BuildIyaMessage() => BuildEmptyMessage("type.ankama.com/iya");
        public static byte[] BuildItyMessage() => BuildEmptyMessage("type.ankama.com/ity");
        public static byte[] BuildKtjMessage() => BuildEmptyMessage("type.ankama.com/ktj");
        public static byte[] BuildLvkMessage() => BuildEmptyMessage("type.ankama.com/lvk");
        public static byte[] BuildLuyMessage() => BuildEmptyMessage("type.ankama.com/luy");
        public static byte[] BuildHhiMessage() => BuildEmptyMessage("type.ankama.com/hhi");
        public static byte[] BuildIdfMessage() => BuildEmptyMessage("type.ankama.com/idf");
        public static byte[] BuildJrfMessage() => BuildEmptyMessage("type.ankama.com/jrf");
        public static byte[] BuildKltMessage() => BuildEmptyMessage("type.ankama.com/klt");
        public static byte[] BuildKlpMessage() => BuildEmptyMessage("type.ankama.com/klp");

        private static byte[] BuildEmptyMessage(string typeUrl)
        {
            return NetworkEnvelope.BuildGameNodePacket(typeUrl, Array.Empty<byte>());
        }

        // ==========================================
        // GROUP 2: Simple Field Messages (12 packets)
        // ==========================================

        public static byte[] BuildHhfMessage() => BuildSingleVarIntMessage("type.ankama.com/hhf", 2);
        public static byte[] BuildHhhMessage() => BuildSingleVarIntMessage("type.ankama.com/hhh", 2);
        public static byte[] BuildKsvMessage() => BuildSingleVarIntMessage("type.ankama.com/ksv", 12287);
        public static byte[] BuildLouMessage() => BuildSingleVarIntMessage("type.ankama.com/lou", 312);
        public static byte[] BuildKdxMessage() => BuildSingleVarIntMessage("type.ankama.com/kdx", 2000000);
        public static byte[] BuildHnkMessage() => BuildSingleVarIntMessage("type.ankama.com/hnk", 2);
        public static byte[] BuildKkpMessage() => BuildEmptyMessage("type.ankama.com/kkp");
        public static byte[] BuildKkmMessage() => BuildEmptyMessage("type.ankama.com/kkm");
        public static byte[] BuildKrbMessage() => BuildSingleVarIntMessage("type.ankama.com/krb", 5);
        public static byte[] BuildIlcMessage()
        {
            byte[] payload = new byte[] {
                0x0A, 0x16, 0x0A, 0x0F, 0x10, 0xE0, 0xE3, 0xFE, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0x01, 0x1A, 0x02, 0xEA, 0x0C, 0x10, 0x86, 0x90, 0x90, 0x49
            };
            return NetworkEnvelope.BuildGameNodePacket("type.ankama.com/ilc", payload);
        }

        public static byte[] BuildIzuMessage()
        {
            using var ms = new MemoryStream();
            var output = new CodedOutputStream(ms);
            output.WriteTag((uint)((1 << 3) | 2));
            output.WriteString("6875369699");
            output.Flush();
            return NetworkEnvelope.BuildGameNodePacket("type.ankama.com/izu", ms.ToArray());
        }

        private static byte[] BuildSingleVarIntMessage(string typeUrl, int value)
        {
            using var ms = new MemoryStream();
            var output = new CodedOutputStream(ms);
            output.WriteTag((uint)((1 << 3) | 0));
            output.WriteInt32(value);
            output.Flush();
            return NetworkEnvelope.BuildGameNodePacket(typeUrl, ms.ToArray());
        }

        public static byte[] BuildKqoMessage()
        {
            using var ms = new MemoryStream();
            var output = new CodedOutputStream(ms);

            // Field 1 (Bytes)
            output.WriteTag((uint)((1 << 3) | 2));
            output.WriteBytes(ByteString.CopyFrom(new byte[] { 7, 11 }));

            // Field 3 (Nested Message)
            using var f3Ms = new MemoryStream();
            var f3Out = new CodedOutputStream(f3Ms);
            f3Out.WriteTag((uint)((1 << 3) | 0));
            f3Out.WriteInt32(7);
            f3Out.Flush();

            output.WriteTag((uint)((3 << 3) | 2));
            output.WriteBytes(ByteString.CopyFrom(f3Ms.ToArray()));

            output.Flush();
            return NetworkEnvelope.BuildGameNodePacket("type.ankama.com/kqo", ms.ToArray());
        }

        public static byte[] BuildBvrMessage()
        {
            var bvrMsg = new ProtoMessage();
            bvrMsg.Fields.Add(new ProtoField { FieldNumber = 1, WireType = 0, VarIntValue = GameState.Kamas });
            return NetworkEnvelope.BuildGameNodePacket("type.ankama.com/bvr", bvrMsg.ToByteArray());
        }

        public static byte[] BuildHhqMessage()
        {
            using var ms = new MemoryStream();
            var output = new CodedOutputStream(ms);
            output.WriteTag((uint)((2 << 3) | 2));
            output.WriteBytes(ByteString.CopyFrom(new byte[] { 0x83, 0x86, 0xb8, 0x49 }));
            output.Flush();
            return NetworkEnvelope.BuildGameNodePacket("type.ankama.com/hhq", ms.ToArray());
        }

        public static byte[] BuildIsfMessage()
        {
            using var ms = new MemoryStream();
            var output = new CodedOutputStream(ms);
            output.WriteTag((uint)((1 << 3) | 0));
            output.WriteInt32(9);
            output.WriteTag((uint)((2 << 3) | 0));
            output.WriteInt32(1017);
            output.Flush();
            return NetworkEnvelope.BuildGameNodePacket("type.ankama.com/isf", ms.ToArray());
        }

        public static byte[] BuildLok1Message()
        {
            using var ms = new MemoryStream();
            var output = new CodedOutputStream(ms);
            output.WriteTag((uint)((2 << 3) | 0));
            output.WriteInt32(1);
            output.WriteTag((uint)((3 << 3) | 0));
            output.WriteInt32(89);
            output.Flush();
            return NetworkEnvelope.BuildGameNodePacket("type.ankama.com/lok", ms.ToArray());
        }

        public static byte[] BuildLok2Message()
        {
            using var ms = new MemoryStream();
            var output = new CodedOutputStream(ms);
            string[] dateParts = { "2026", "06", "24", "19", "39" };
            foreach (var part in dateParts)
            {
                output.WriteTag((uint)((1 << 3) | 2));
                output.WriteString(part);
            }
            output.WriteTag((uint)((3 << 3) | 0));
            output.WriteInt32(193);
            output.Flush();
            return NetworkEnvelope.BuildGameNodePacket("type.ankama.com/lok", ms.ToArray());
        }

        public static byte[] BuildJohMessage()
        {
            using var ms = new MemoryStream();
            var output = new CodedOutputStream(ms);
            output.WriteTag((uint)((2 << 3) | 0)); // Field 2, VarInt
            output.WriteInt64(GameState.MapId > 0 ? GameState.MapId : 154011397);
            output.Flush();
            return NetworkEnvelope.BuildGameNodePacket("type.ankama.com/joh", ms.ToArray());
        }

        public static byte[] BuildIzhMessage()
        {
            using var ms = new MemoryStream();
            var output = new CodedOutputStream(ms);
            output.WriteTag((uint)((1 << 3) | 2));
            output.WriteBytes(ByteString.Empty);
            output.Flush();
            return NetworkEnvelope.BuildGameNodePacket("type.ankama.com/izh", ms.ToArray());
        }

        // ==========================================
        // GROUP 3: Complex Structured Messages (9 packets + Map/System Bursts)
        // ==========================================

        public static byte[] BuildHnqMessage()
        {
            using var ms = new MemoryStream();
            var output = new CodedOutputStream(ms);
            output.WriteTag((uint)((1 << 3) | 0));
            output.WriteInt32(100);
            output.WriteTag((uint)((2 << 3) | 0));
            output.WriteInt32(105);
            output.WriteTag((uint)((3 << 3) | 0));
            output.WriteInt32(105);
            output.Flush();
            return NetworkEnvelope.BuildGameNodePacket("type.ankama.com/hnq", ms.ToArray());
        }

        public static byte[] BuildIznMessage()
        {
            using var ms = new MemoryStream();
            var output = new CodedOutputStream(ms);

            // First nested Field 1
            using var f1Ms1 = new MemoryStream();
            var f1Out1 = new CodedOutputStream(f1Ms1);
            f1Out1.WriteTag((uint)((1 << 3) | 0));
            f1Out1.WriteInt32(1);
            f1Out1.Flush();
            output.WriteTag((uint)((1 << 3) | 2));
            output.WriteBytes(ByteString.CopyFrom(f1Ms1.ToArray()));

            // Second nested Field 1
            using var f1Ms2 = new MemoryStream();
            var f1Out2 = new CodedOutputStream(f1Ms2);
            f1Out2.WriteTag((uint)((1 << 3) | 0));
            f1Out2.WriteInt32(1);
            f1Out2.WriteTag((uint)((2 << 3) | 0));
            f1Out2.WriteInt32(1);
            f1Out2.Flush();
            output.WriteTag((uint)((1 << 3) | 2));
            output.WriteBytes(ByteString.CopyFrom(f1Ms2.ToArray()));

            output.Flush();
            return NetworkEnvelope.BuildGameNodePacket("type.ankama.com/izn", ms.ToArray());
        }

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

        public static byte[] BuildIboMessage()
        {
            using var ms = new MemoryStream();
            var output = new CodedOutputStream(ms);

            using var f1Ms = new MemoryStream();
            var f1Out = new CodedOutputStream(f1Ms);

            using var f3Ms = new MemoryStream();
            var f3Out = new CodedOutputStream(f3Ms);
            f3Out.WriteTag((uint)((1 << 3) | 0));
            f3Out.WriteInt32(2251);

            using var f2Ms1 = new MemoryStream();
            var f2Out1 = new CodedOutputStream(f2Ms1);
            f2Out1.WriteTag((uint)((2 << 3) | 0));
            f2Out1.WriteInt32(9738);
            f2Out1.WriteTag((uint)((4 << 3) | 0));
            f2Out1.WriteInt32(1);
            f2Out1.Flush();
            f3Out.WriteTag((uint)((2 << 3) | 2));
            f3Out.WriteBytes(ByteString.CopyFrom(f2Ms1.ToArray()));

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
            return NetworkEnvelope.BuildGameNodePacket("type.ankama.com/ibo", ms.ToArray());
        }

        public static byte[] BuildKojMessage()
        {
            using var ms = new MemoryStream();
            var output = new CodedOutputStream(ms);

            // Field 2 (Nested)
            using var f2Ms = new MemoryStream();
            var f2Out = new CodedOutputStream(f2Ms);

            // F1 inside F2
            using var f2f1Ms = new MemoryStream();
            var f2f1Out = new CodedOutputStream(f2f1Ms);
            f2f1Out.WriteTag((uint)((1 << 3) | 0));
            f2f1Out.WriteInt32(100);
            f2f1Out.WriteTag((uint)((2 << 3) | 0));
            f2f1Out.WriteInt32(10);
            f2f1Out.WriteTag((uint)((3 << 3) | 0));
            f2f1Out.WriteInt32(10);
            f2f1Out.WriteTag((uint)((4 << 3) | 0));
            f2f1Out.WriteInt32(10000);
            f2f1Out.Flush();
            f2Out.WriteTag((uint)((1 << 3) | 2));
            f2Out.WriteBytes(ByteString.CopyFrom(f2f1Ms.ToArray()));

            // F2 inside F2
            using var f2f2Ms = new MemoryStream();
            var f2f2Out = new CodedOutputStream(f2f2Ms);
            f2f2Out.WriteTag((uint)((2 << 3) | 0));
            f2f2Out.WriteInt32(32441);
            f2f2Out.WriteTag((uint)((3 << 3) | 0));
            f2f2Out.WriteInt32(12);
            f2f2Out.WriteTag((uint)((4 << 3) | 0));
            f2f2Out.WriteInt32(32442);
            f2f2Out.WriteTag((uint)((5 << 3) | 0));
            f2f2Out.WriteInt32(2);
            f2f2Out.WriteTag((uint)((6 << 3) | 0));
            f2f2Out.WriteInt32(100);
            f2f2Out.WriteTag((uint)((8 << 3) | 0));
            f2f2Out.WriteInt32(3);
            f2f2Out.Flush();
            f2Out.WriteTag((uint)((2 << 3) | 2));
            f2Out.WriteBytes(ByteString.CopyFrom(f2f2Ms.ToArray()));

            f2Out.WriteTag((uint)((3 << 3) | 0));
            f2Out.WriteInt64(86400000);
            f2Out.Flush();

            output.WriteTag((uint)((2 << 3) | 2));
            output.WriteBytes(ByteString.CopyFrom(f2Ms.ToArray()));

            // Field 3 (Nested)
            using var f3Ms = new MemoryStream();
            var f3Out = new CodedOutputStream(f3Ms);
            f3Out.WriteTag((uint)((1 << 3) | 0));
            f3Out.WriteInt32(5);
            f3Out.WriteTag((uint)((3 << 3) | 0));
            f3Out.WriteInt32(300);
            f3Out.WriteTag((uint)((4 << 3) | 0));
            f3Out.WriteInt32(30);
            f3Out.Flush();

            output.WriteTag((uint)((3 << 3) | 2));
            output.WriteBytes(ByteString.CopyFrom(f3Ms.ToArray()));

            output.Flush();
            return NetworkEnvelope.BuildGameNodePacket("type.ankama.com/koj", ms.ToArray());
        }

        public static byte[] BuildKyjMessage()
        {
            using var ms = new MemoryStream();
            var output = new CodedOutputStream(ms);

            int[] ratings = { 50, 300, 300, 300, 300, 10000, 10000, 700, 300, 300, 300, 300, 300, 300 };
            int[] leagues = { 13, 15, 2, 2, 2, 2, 2, 2, 2, 2, 2, 2, 2, 2 };

            for (int i = 0; i < ratings.Length; i++)
            {
                using var f1Ms = new MemoryStream();
                var f1Out = new CodedOutputStream(f1Ms);
                f1Out.WriteTag((uint)((2 << 3) | 0));
                f1Out.WriteInt32(ratings[i]);
                f1Out.WriteTag((uint)((3 << 3) | 0));
                f1Out.WriteInt32(leagues[i]);
                f1Out.Flush();

                output.WriteTag((uint)((1 << 3) | 2));
                output.WriteBytes(ByteString.CopyFrom(f1Ms.ToArray()));
            }

            output.Flush();
            return NetworkEnvelope.BuildGameNodePacket("type.ankama.com/kyj", ms.ToArray());
        }

        public static byte[] BuildLtkMessage()
        {
            using var ms = new MemoryStream();
            var output = new CodedOutputStream(ms);

            using var f2Ms = new MemoryStream();
            var f2Out = new CodedOutputStream(f2Ms);
            f2Out.WriteTag((uint)((1 << 3) | 0));
            f2Out.WriteInt32(16);

            using var f2f2Ms = new MemoryStream();
            var f2f2Out = new CodedOutputStream(f2f2Ms);

            using var f2f2f3Ms = new MemoryStream();
            var f2f2f3Out = new CodedOutputStream(f2f2f3Ms);
            f2f2f3Out.WriteTag((uint)((1890484 << 3) | 2));
            f2f2f3Out.WriteBytes(ByteString.Empty);
            f2f2f3Out.Flush();

            f2f2Out.WriteTag((uint)((3 << 3) | 2));
            f2f2Out.WriteBytes(ByteString.CopyFrom(f2f2f3Ms.ToArray()));
            f2f2Out.Flush();

            f2Out.WriteTag((uint)((2 << 3) | 2));
            f2Out.WriteBytes(ByteString.CopyFrom(f2f2Ms.ToArray()));

            f2Out.WriteTag((uint)((3 << 3) | 0));
            f2Out.WriteInt32(1);
            f2Out.Flush();

            output.WriteTag((uint)((2 << 3) | 2));
            output.WriteBytes(ByteString.CopyFrom(f2Ms.ToArray()));

            output.Flush();
            return NetworkEnvelope.BuildGameNodePacket("type.ankama.com/ltk", ms.ToArray());
        }

        public static byte[] BuildLwbMessage()
        {
            using var ms = new MemoryStream();
            var output = new CodedOutputStream(ms);

            string guid = "9832c8c5-4ab6-44ae-aeb2-899653767773";
            string date = "2026-06-21T20:58:01.714073738Z";
            byte[] signature = new byte[] { 0xa4, 0xe1, 0xb9, 0x01, 0x92, 0xa6, 0x48, 0x88, 0x8c, 0x20, 0xf5, 0xb7, 0xcb, 0x04 };
            byte[] sig2 = new byte[] { 0xa4, 0xe1, 0xb9, 0x19, 0x92, 0xa6, 0xc8, 0x20, 0x88, 0xca, 0xa0, 0x28, 0xf5, 0xb7, 0xcb, 0x34 };

            for (int i = 1; i <= 2; i++)
            {
                using var f1Ms = new MemoryStream();
                var f1Out = new CodedOutputStream(f1Ms);

                f1Out.WriteTag((uint)((3 << 3) | 0));
                f1Out.WriteInt32(3);

                using var f4Ms = new MemoryStream();
                var f4Out = new CodedOutputStream(f4Ms);
                using var f4f3Ms = new MemoryStream();
                var f4f3Out = new CodedOutputStream(f4f3Ms);
                f4f3Out.WriteTag((uint)((1890484 << 3) | 2));
                f4f3Out.WriteBytes(ByteString.CopyFrom(signature));
                f4f3Out.Flush();
                f4Out.WriteTag((uint)((3 << 3) | 2));
                f4Out.WriteBytes(ByteString.CopyFrom(f4f3Ms.ToArray()));
                f4Out.Flush();

                f1Out.WriteTag((uint)((4 << 3) | 2));
                f1Out.WriteBytes(ByteString.CopyFrom(f4Ms.ToArray()));

                f1Out.WriteTag((uint)((6 << 3) | 2));
                f1Out.WriteString(date);

                f1Out.WriteTag((uint)((7 << 3) | 0));
                f1Out.WriteInt32(137);

                using var f8Ms = new MemoryStream();
                var f8Out = new CodedOutputStream(f8Ms);
                f8Out.WriteTag((uint)((1 << 3) | 0));
                f8Out.WriteInt32(1);
                f8Out.WriteTag((uint)((3 << 3) | 0));
                f8Out.WriteInt32(3);
                
                using var f8f4Ms = new MemoryStream();
                var f8f4Out = new CodedOutputStream(f8f4Ms);
                f8f4Out.WriteTag((uint)((3987636 << 3) | 2));
                f8f4Out.WriteBytes(ByteString.CopyFrom(sig2));
                f8f4Out.Flush();
                f8Out.WriteTag((uint)((4 << 3) | 2));
                f8Out.WriteBytes(ByteString.CopyFrom(f8f4Ms.ToArray()));
                
                f8Out.WriteTag((uint)((5 << 3) | 2));
                f8Out.WriteBytes(ByteString.CopyFrom(new byte[] { 0x5b, 0xe4, 0x10 }));
                f8Out.WriteTag((uint)((8 << 3) | 2));
                f8Out.WriteString("4");
                f8Out.Flush();

                f1Out.WriteTag((uint)((8 << 3) | 2));
                f1Out.WriteBytes(ByteString.CopyFrom(f8Ms.ToArray()));

                f1Out.WriteTag((uint)((14 << 3) | 0));
                f1Out.WriteInt32(22);

                f1Out.WriteTag((uint)((16 << 3) | 2));
                f1Out.WriteString(guid);

                f1Out.Flush();
                output.WriteTag((uint)((1 << 3) | 2));
                output.WriteBytes(ByteString.CopyFrom(f1Ms.ToArray()));
            }

            output.Flush();
            return NetworkEnvelope.BuildGameNodePacket("type.ankama.com/lwb", ms.ToArray());
        }

        public static byte[] BuildLuqMessage()
        {
            using var ms = new MemoryStream();
            var output = new CodedOutputStream(ms);

            string guid = "47cefdf0-00d3-493c-b532-80d3f1d5125b";
            byte[] sig2 = new byte[] { 0xa4, 0xe1, 0xb9, 0x19, 0x92, 0xa6, 0xc8, 0x20, 0x88, 0xca, 0xa0, 0x28, 0xf5, 0xb7, 0xcb, 0x34 };

            using var f2Ms = new MemoryStream();
            var f2Out = new CodedOutputStream(f2Ms);
            f2Out.WriteTag((uint)((1 << 3) | 0));
            f2Out.WriteInt32(1);
            f2Out.WriteTag((uint)((3 << 3) | 0));
            f2Out.WriteInt32(3);

            using var f4Ms = new MemoryStream();
            var f4Out = new CodedOutputStream(f4Ms);
            f4Out.WriteTag((uint)((3987636 << 3) | 2));
            f4Out.WriteBytes(ByteString.CopyFrom(sig2));
            f4Out.Flush();
            f2Out.WriteTag((uint)((4 << 3) | 2));
            f2Out.WriteBytes(ByteString.CopyFrom(f4Ms.ToArray()));

            f2Out.WriteTag((uint)((5 << 3) | 2));
            f2Out.WriteBytes(ByteString.CopyFrom(new byte[] { 0x5b, 0xe4, 0x10 }));
            f2Out.WriteTag((uint)((8 << 3) | 2));
            f2Out.WriteString("4");
            f2Out.Flush();

            output.WriteTag((uint)((2 << 3) | 2));
            output.WriteBytes(ByteString.CopyFrom(f2Ms.ToArray()));

            output.WriteTag((uint)((3 << 3) | 2));
            output.WriteString(guid);

            output.Flush();
            return NetworkEnvelope.BuildGameNodePacket("type.ankama.com/luq", ms.ToArray());
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

            return NetworkEnvelope.BuildGameNodePacket("type.ankama.com/lpe", ms.ToArray());
        }

        public static byte[][] BuildKqmList(int subAreaId)
        {
            int?[][] kqmData = new int?[][] {
                new int?[] { subAreaId, 1 }
            };

            byte[][] list = new byte[kqmData.Length][];
            for (int i = 0; i < kqmData.Length; i++)
            {
                using var ms = new MemoryStream();
                var output = new CodedOutputStream(ms);

                var data = kqmData[i];
                if (data[0].HasValue)
                {
                    output.WriteTag((uint)((1 << 3) | 0));
                    output.WriteInt32(data[0].Value);
                }
                if (data[1].HasValue)
                {
                    output.WriteTag((uint)((2 << 3) | 0));
                    output.WriteInt32(data[1].Value);
                }
                output.Flush();
                list[i] = NetworkEnvelope.BuildGameNodePacket("type.ankama.com/kqm", ms.ToArray());
            }
            return list;
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

        public static byte[] BuildHmdMessage()
        {
            byte[] payload = new byte[] {
                0x08, 0x01, 0x1A, 0x04, 0x18, 0x02, 0x20, 0x01, 0x1A, 0x08, 0x08, 0xBB, 0xFD, 0x01, 0x18, 0x01, 0x20, 0x01, 0x1A, 0x08, 0x08, 0xAA, 0xFD, 0x01, 0x18, 0x01, 0x20, 0x01, 0x1A, 0x08, 0x08, 0xB3, 0xFD, 0x01, 0x18, 0x01, 0x20, 0x01, 0x1A, 0x08, 0x08, 0xC7, 0xFD, 0x01, 0x18, 0x01, 0x20, 0x01
            };
            return NetworkEnvelope.BuildGameNodePacket("type.ankama.com/hmd", payload);
        }

        public static byte[][] BuildItpList()
        {
            byte[] itp1 = new byte[] {
                0x08, 0x01, 0x12, 0x02, 0x22, 0x00, 0x12, 0x08, 0x18, 0x01, 0x22, 0x04, 0x08, 0xAA, 0xFD, 0x01, 0x12, 0x08, 0x18, 0x02, 0x22, 0x04, 0x08, 0xB3, 0xFD, 0x01, 0x12, 0x08, 0x18, 0x03, 0x22, 0x04, 0x08, 0xBB, 0xFD, 0x01, 0x12, 0x08, 0x18, 0x04, 0x22, 0x04, 0x08, 0xC7, 0xFD, 0x01
            };
            return new byte[][] {
                NetworkEnvelope.BuildGameNodePacket("type.ankama.com/itp", itp1),
                NetworkEnvelope.BuildGameNodePacket("type.ankama.com/itp", itp1),
                NetworkEnvelope.BuildGameNodePacket("type.ankama.com/itp", Array.Empty<byte>())
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
