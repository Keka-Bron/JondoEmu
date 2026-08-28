using System;

namespace Jondo.Unity.Server.Diagnostics
{
    /// <summary>
    /// Takes the session tokens out of the traffic log without taking anything else out.
    /// </summary>
    /// <remarks>
    /// The log carries every frame in both directions, and among them is the token the client got
    /// from HAAPI and hands over on connect. It is a live credential: whoever reads the file can
    /// resume that session as that account, and the file sits on disk in the clear for as long as
    /// the machine has room for it.
    ///
    /// Blanking whole frames was the obvious fix and the wrong one -- this log is the tool the
    /// protocol gets read with, and a log you cannot read is a log nobody keeps. So the redaction is
    /// exact instead of broad: <c>HaapiServer.GameTokenResponse</c> and <c>ControlApi</c> both mint
    /// tokens with <c>Guid.NewGuid().ToString("N")</c>, which is 32 hex characters and nothing else,
    /// and that shape does not occur anywhere else in this traffic. Sixteen of the 256 byte values
    /// are ASCII hex, so a run of 32 arriving by chance is one in 16^32; what shows up in practice
    /// is a token, every time.
    ///
    /// It happens on the <b>bytes</b>, before either line is rendered, and that is the part worth
    /// insisting on. Masking only the <c>Str:</c> line would have looked finished while leaving the
    /// token written out one ASCII byte at a time on the <c>Hex:</c> line right above it.
    ///
    /// Frame lengths, field numbers, wire types and every other byte come through untouched, so the
    /// log still answers the question it exists to answer.
    /// </remarks>
    public static class TrafficRedaction
    {
        /// <summary>Length of a <c>Guid.ToString("N")</c>, which is what a token is.</summary>
        public const int TokenLength = 32;

        /// <summary>What replaces each masked byte. ASCII, so it survives both renderings.</summary>
        public const byte Mask = (byte)'x';

        private static bool IsHex(byte b)
            => (b >= '0' && b <= '9') || (b >= 'a' && b <= 'f') || (b >= 'A' && b <= 'F');

        /// <summary>
        /// A copy of the first <paramref name="length"/> bytes with every token-shaped run masked.
        /// </summary>
        /// <remarks>
        /// Returns the original array when there is nothing to mask, which is almost every frame:
        /// this runs twice per frame on the hot path and there is no reason to copy a combat packet
        /// so it can come out identical.
        /// </remarks>
        public static byte[] Scrub(byte[] data, int length)
        {
            if (data == null || length <= 0) return Array.Empty<byte>();
            if (length > data.Length) length = data.Length;

            byte[]? clean = null;
            int run = 0;

            for (int i = 0; i <= length; i++)
            {
                bool hex = i < length && IsHex(data[i]);
                if (hex) { run++; continue; }

                if (run >= TokenLength)
                {
                    // The run ends at i, so it started at i - run. Copy lazily: most frames never
                    // get here and should not pay for an allocation.
                    clean ??= (byte[])data.Clone();
                    for (int j = i - run; j < i; j++) clean[j] = Mask;
                }

                run = 0;
            }

            return clean ?? data;
        }
    }
}
