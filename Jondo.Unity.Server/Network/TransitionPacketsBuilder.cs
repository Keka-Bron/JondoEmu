using System;
using System.IO;
using Google.Protobuf;
using Jondo.Unity.Protocol;

namespace Jondo.Unity.Server.Network
{
    /// <summary>Compatibility builders for the remaining legacy transition branches.</summary>
    public static class TransitionPacketsBuilder
    {
        public static byte[] BuildKltMessage() => BuildEmptyMessage("type.ankama.com/klt");
        public static byte[] BuildKlpMessage() => BuildEmptyMessage(Op.Uri(Op.Klp));

        private static byte[] BuildEmptyMessage(string typeUrl)
            => NetworkEnvelope.BuildGameNodePacket(typeUrl, Array.Empty<byte>());

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

        public static byte[] BuildLpeMessage()
        {
            using var ms = new MemoryStream();
            var output = new CodedOutputStream(ms);

            using var f2Ms = new MemoryStream();
            var f2Out = new CodedOutputStream(f2Ms);
            f2Out.WriteTag((uint)((1 << 3) | 2));
            f2Out.WriteString("00000000-0000-4000-8000-00000000000b");
            f2Out.Flush();

            output.WriteTag((uint)((2 << 3) | 2));
            output.WriteBytes(ByteString.CopyFrom(f2Ms.ToArray()));
            output.Flush();

            return NetworkEnvelope.BuildGameNodePacket(Op.Uri(Op.Lpe), ms.ToArray());
        }

        public static byte[] BuildIthMessage()
        {
            // Return the pre-recorded complete ith packet containing the official world prisms database
            // to populate the client's cartography prism registry and prevent null reference crashes
            return TransitionPayloads.ith;
        }
    }
}
