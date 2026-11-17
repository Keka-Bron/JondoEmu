using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Text;
using Jondo.Unity.Protocol;

namespace Jondo.Unity.Server.Managers
{
    /// <summary>
    /// Keeps the client from learning about the invisible items (type 203, «Objeto invisible»:
    /// Ankama's quest progress flags, like «[!] Quête 2453 Objet 001»).
    /// </summary>
    /// <remarks>
    /// The server stores, counts, tests and removes them like any other item; only the wire is
    /// filtered, because the client has no filter of its own and would show them in the inventory
    /// and in the chat (« Vous avez obtenu 1 [Quête 2453 Objet 001] »).
    ///
    /// Item messages are built in more than a hundred places, so the one thing every one of them
    /// goes through, the frame writer, asks <see cref="Hides"/> whether a frame would tell the
    /// client about one: an item arriving (iua, itd), its quantity changing (ivj), it leaving (ium,
    /// itc), or the «you got» and «you lost» notices (lqn, <see cref="InfoMessages.ItemGained"/>,
    /// <see cref="InfoMessages.ItemLost"/>). Lists (the
    /// inventory on entering the world, the contents of a chest) leave them out where they are built,
    /// through <see cref="Conceal"/>.
    ///
    /// A quantity or a removal only names the uid, so the uids the client was never told about are
    /// remembered: an invisible item that arrives and goes sends neither the arrival, nor the
    /// quantity, nor the removal. Uids are unique across the whole server, so one set serves everybody.
    /// </remarks>
    public static class InvisibleItems
    {
        private static readonly ConcurrentDictionary<long, byte> Uids = new();

        /// <summary>Notes that the client must not hear about this stack.</summary>
        public static void Hide(long uid) => Uids[uid] = 0;

        /// <summary>Whether this stack was kept from the client.</summary>
        public static bool IsHidden(long uid) => Uids.ContainsKey(uid);

        /// <summary>
        /// For the lists that are built stack by stack: true, and the uid remembered, when the item is
        /// invisible and so must stay out of the list.
        /// </summary>
        public static bool Conceal(int gid, long uid)
        {
            if (!Equipment.IsInvisible(gid)) return false;
            Hide(uid);
            return true;
        }

        // ─── The frames ─────────────────────────────────────────────────────────────────────

        // As bytes: every frame the server writes goes through Hides, and comparing the url's bytes
        // allocates nothing for the movements and fights that make up most of them.
        private static readonly byte[] Iua = Url(Op.Iua), Itd = Url(Op.Itd), Ivj = Url(Op.Ivj),
                                       Ium = Url(Op.Ium), Itc = Url(Op.Itc), Lqn = Url(Op.Lqn);

        private static byte[] Url(string op) => Encoding.ASCII.GetBytes(Op.Uri(op));

        /// <summary>
        /// Whether a frame about to be written tells the client about an invisible item and must
        /// not go. The frame is a push: f1 or f3 { f1 { f1: the type url, f2: the message } }.
        /// </summary>
        public static bool Hides(ReadOnlySpan<byte> frame)
        {
            try
            {
                if (!TryReadAny(frame, out var url, out var message)) return false;
                if (url.SequenceEqual(Iua) || url.SequenceEqual(Itd)) return Arrival(message);
                if (url.SequenceEqual(Ivj)) return Quantity(message);
                if (url.SequenceEqual(Ium) || url.SequenceEqual(Itc)) return Varint(message, 1) is long gone && IsHidden(gone);
                if (url.SequenceEqual(Lqn)) return Gained(message);
            }
            catch (Exception)
            {
                // Whatever is not understood goes through as it is.
            }
            return false;
        }

        /// <summary>iua / itd: { f1 or f3 { f1: where, f5 { f1: template, f4: uid } } }, as BuildItemArrived writes it.</summary>
        private static bool Arrival(ReadOnlySpan<byte> message)
        {
            foreach (int field in new[] { 1, 3 })
            {
                if (!Message(message, field, out var place) || !Message(place, 5, out var item)) continue;
                if (Varint(item, 1) is not long gid || !Equipment.IsInvisible((int)gid)) continue;
                if (Varint(item, 4) is long uid) Hide(uid);
                return true;
            }
            return false;
        }

        /// <summary>ivj: { f3 { f2: uid, f3: the new total } }, as BuildItemQuantity writes it.</summary>
        private static bool Quantity(ReadOnlySpan<byte> message)
            => Message(message, 3, out var stack) && Varint(stack, 2) is long uid && IsHidden(uid);

        /// <summary>
        /// lqn: { f1: type (0 when absent), f2: 21 or 22, f4: quantity, f4: template }, «Vous avez obtenu» and
        /// «Vous avez perdu» (<see cref="InfoMessages.ItemLost"/>, what taking a quest flag back says).
        /// </summary>
        private static bool Gained(ReadOnlySpan<byte> message)
        {
            if ((Varint(message, 1) ?? 0) != 0) return false;
            long id = Varint(message, 2) ?? 0;
            if (id != InfoMessages.ItemGained && id != InfoMessages.ItemLost) return false;
            var parameters = Strings(message, 4);
            return parameters.Count >= 2 && int.TryParse(parameters[1], out int gid) && Equipment.IsInvisible(gid);
        }

        // ─── A small reader: only what the checks above need ──────────────────────────────────

        private static bool TryReadAny(ReadOnlySpan<byte> frame, out ReadOnlySpan<byte> url, out ReadOnlySpan<byte> message)
        {
            url = default;
            message = default;
            ReadOnlySpan<byte> envelope = default;
            bool found = false;
            int pos = 0;
            while (!found && Next(frame, ref pos, out int field, out int wire, out _, out var bytes))
                if ((field == 1 || field == 3) && wire == 2) { envelope = bytes; found = true; }
            if (!found || !Message(envelope, 1, out var any)) return false;

            pos = 0;
            bool hasUrl = false;
            while (Next(any, ref pos, out int field, out int wire, out _, out var bytes))
            {
                if (wire != 2) continue;
                if (field == 1) { url = bytes; hasUrl = true; }
                else if (field == 2) message = bytes;
            }
            return hasUrl;
        }

        private static bool Message(ReadOnlySpan<byte> from, int field, out ReadOnlySpan<byte> message)
        {
            int pos = 0;
            while (Next(from, ref pos, out int f, out int wire, out _, out var bytes))
            {
                if (f == field && wire == 2) { message = bytes; return true; }
            }
            message = default;
            return false;
        }

        private static long? Varint(ReadOnlySpan<byte> from, int field)
        {
            int pos = 0;
            while (Next(from, ref pos, out int f, out int wire, out long value, out _))
                if (f == field && wire == 0) return value;
            return null;
        }

        private static List<string> Strings(ReadOnlySpan<byte> from, int field)
        {
            var all = new List<string>();
            int pos = 0;
            while (Next(from, ref pos, out int f, out int wire, out _, out var bytes))
                if (f == field && wire == 2) all.Add(Encoding.UTF8.GetString(bytes));
            return all;
        }

        /// <summary>The next field: its number, wire type, and its value (a varint) or bytes (a block).</summary>
        private static bool Next(ReadOnlySpan<byte> data, scoped ref int pos, out int field, out int wire,
                                 out long varint, out ReadOnlySpan<byte> bytes)
        {
            field = wire = 0;
            varint = 0;
            bytes = default;
            if (pos >= data.Length) return false;

            ulong tag = ReadVarint(data, ref pos);
            field = (int)(tag >> 3);
            wire = (int)(tag & 7);
            switch (wire)
            {
                case 0: varint = (long)ReadVarint(data, ref pos); return true;
                case 1: pos += 8; return pos <= data.Length;
                case 5: pos += 4; return pos <= data.Length;
                case 2:
                    int length = (int)ReadVarint(data, ref pos);
                    if (length < 0 || length > data.Length - pos) return false;
                    bytes = data.Slice(pos, length);
                    pos += length;
                    return true;
                default: return false;
            }
        }

        private static ulong ReadVarint(ReadOnlySpan<byte> data, ref int pos)
        {
            ulong value = 0;
            for (int shift = 0; pos < data.Length && shift < 64; shift += 7)
            {
                byte b = data[pos++];
                value |= (ulong)(b & 0x7F) << shift;
                if ((b & 0x80) == 0) return value;
            }
            throw new FormatException("Truncated varint");
        }
    }
}
