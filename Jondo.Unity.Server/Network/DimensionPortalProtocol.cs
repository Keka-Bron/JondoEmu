using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Jondo.Unity.Server.Managers;

namespace Jondo.Unity.Server.Network
{
    /// <summary>
    /// The divine dimensions' old portals on the wire (DimensionPortals), as the 3.6.10.11 client
    /// reads them: no capture shows one.
    /// </summary>
    /// <remarks>
    /// <code>
    ///   actor (ljx)  f1 { f1 cell, f2 facing }   f2 { f1 { f3 { f2 the portal, f3 the dimension's area } }, f3 look }   f3 actor id
    ///   ioc          f2 the portal's type, f4 map, f5 actor id
    ///   ios          the portal NPC's sentence, its reply, and as parameters the crossings left and
    ///                the closing time in unix seconds
    ///   hhu (client) f3 the portal
    /// </code>
    /// </remarks>
    public static class DimensionPortalProtocol
    {
        /// <summary>The dialogue with the portal opened (ioc): its type makes the client show its NPC.</summary>
        public static byte[] BuildDialog(DimensionPortals.Portal portal)
            => Pb.New().Var(2, portal.Type).Var(4, portal.MapId).Var(5, portal.ContextId).Build();

        /// <summary>
        /// The portal's sentence (ios): its crossings never run out («∞»), and its closing a century
        /// off -- the client reads #2 as a moment in unix seconds, and writes «0» for anything else.
        /// </summary>
        public static byte[] BuildQuestion(long messageId, IEnumerable<long> replies, DateTimeOffset now)
            => BankProtocol.BuildQuestion(messageId, replies, null, new[]
            {
                DimensionPortals.UsesLeft,
                now.AddYears(DimensionPortals.ClosesInYears).ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture),
            });

        /// <summary>The portal a crossing asks for (hhu f3).</summary>
        public static int ReadUse(byte[] hhu)
            => (int)(ProtoMessage.Parse(hhu).Fields.FirstOrDefault(f => f.FieldNumber == 3 && f.WireType == 0)?.VarIntValue ?? 0);
    }
}
