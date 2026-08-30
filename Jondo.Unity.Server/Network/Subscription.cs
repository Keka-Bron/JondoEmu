using System;
using Jondo.Unity.Server.Managers;

namespace Jondo.Unity.Server.Network
{
    /// <summary>
    /// When an account subscription runs out, in the one shape the client can read.
    /// </summary>
    /// <remarks>
    /// It comes from the Accounts table now. It used to be computed here for everybody at once,
    /// which amounts to writing it into the source: every account carried the same date, none could
    /// expire, and nothing could ever say otherwise. A column costs one migration and makes the
    /// answer a fact about an account rather than a property of the build.
    ///
    /// The client is told twice over two different channels, and for a while only one was right:
    ///
    /// <list type="number">
    /// <item>The game protocol, field 5 of the accepted-authentication message. This draws the date
    /// in the header, and it has been wrong twice: the value once ended in Z where the real server
    /// sends a numeric offset, and the year was 2099, which is 4,070,901,600 seconds and does not
    /// fit in a signed 32-bit count. Every date in the captures sits about eight days ahead, or is
    /// the 1970 sentinel of an account without one.</item>
    /// <item>The launcher, over Thrift, in the JSON that userInfo_get answers with -- called on
    /// every single launch, before the client has even asked for its game token. It read
    /// <c>"endOfSubscribe":"2035-01-01T00:00:00Z"</c>: the same trailing Z already measured as
    /// unreadable, in the same file where addedDate beside it uses +02:00.</item>
    /// </list>
    ///
    /// Both read <see cref="EndDateFor"/> now, which is the point of the file: two answers to one
    /// question had no business being written in two places.
    ///
    /// One thing this does NOT explain, and the record should be straight about it. The subscribed
    /// header and the create-character button were chased through this file for two rounds -- first
    /// the year, then the distance, on the theory that a client showing "Hasta el {0}" (1094137)
    /// instead of "Abonado hasta el {0}" (1092350) was refusing to believe the date. It was not.
    /// What told the client the account had reached its character limit was field 1 of mgq, three
    /// frames further down the welcome burst, and CharacterLimitFrameTests has it. Nothing measured
    /// here has been shown to change what the client believes; what is measured is only the shape
    /// the value has to have.
    /// </remarks>
    public static class Subscription
    {
        /// <summary>ISO 8601 with a numeric offset. No Z: the client has never been seen to read one.</summary>
        public const string Format = "yyyy-MM-ddTHH:mm:sszzz";

        /// <summary>How long a new account is subscribed for.</summary>
        /// <remarks>
        /// A year, which is what a subscription in this game tops out at. It was thirty days for
        /// one build, chosen while the distance was still suspected of darkening the create button.
        /// That suspicion is dead, so the number went back to what it should be.
        /// </remarks>
        public static readonly TimeSpan Length = TimeSpan.FromDays(365);

        /// <summary>The date a fresh account starts with, in the format that travels.</summary>
        public static string DefaultEndDate()
            => DateTimeOffset.Now.Add(Length).ToString(Format);

        /// <summary>
        /// The stored date for an account, or a fresh default when there is nothing to read.
        /// </summary>
        /// <remarks>
        /// The fallback is not a courtesy. This is asked for on the authentication path and on
        /// every launch, and an account whose row cannot be read still has to get a usable date:
        /// an empty string reaches the client as a subscription that ended at the epoch, which is
        /// a worse failure than a date that is merely generous.
        /// </remarks>
        public static string EndDateFor(long accountId)
        {
            string stored = DatabaseManager.GetSubscriptionEnd(accountId);
            return stored.Length > 0 ? stored : DefaultEndDate();
        }
    }
}
