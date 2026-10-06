using System.Collections.Generic;

namespace Jondo.Unity.Server.Managers
{
    /// <summary>
    /// The guild shop's five oracles: what they cost and what alteration they set.
    ///
    /// The shop (jkh) sends them by their number from 1 to 5 with the price ALREADY MULTIPLIED by the
    /// guild's active accounts: in the capture's four-account guild they come out 80, 80, 800, 200 and 80,
    /// and in the one-account guild, 20, 20, 200, 50 and 20. Hence the prices here.
    ///
    /// Which alteration each one sets is measured on ONE: number 1 was bought, activated, and what arrived
    /// was alteration 859, «Oráculo de saber». The other four are placed by their price, which only leaves
    /// open the order of the three that cost 20:
    ///
    ///   859 knowledge  20    ←  measured
    ///   860 fortune    20    ←  inference: it is one of the three at 20
    ///   862 divine    200    ←  the only one at 200
    ///   861 gatherer   50    ←  the only one at 50
    ///   863 gladiator  20    ←  inference, like fortune's
    ///
    /// The game's sheet prices -- knowledge, fortune and gladiator at 20, gatherer at 50, divine at 200 --
    /// fit the capture's five, so the only unproven thing is whether fortune goes in 2 and gladiator in 5
    /// or the other way round.
    /// </summary>
    public static class GuildOracles
    {
        public sealed class Oracle
        {
            public int Id { get; init; }

            /// <summary>What it costs PER active account of the guild, in guild kamas.</summary>
            public int PricePerAccount { get; init; }

            /// <summary>The alteration it sets on being activated.</summary>
            public int Alteration { get; init; }
        }

        private static readonly Dictionary<int, Oracle> Catalogo = new()
        {
            [1] = new Oracle { Id = 1, PricePerAccount = 20,  Alteration = 859 },
            [2] = new Oracle { Id = 2, PricePerAccount = 20,  Alteration = 860 },
            [3] = new Oracle { Id = 3, PricePerAccount = 200, Alteration = 862 },
            [4] = new Oracle { Id = 4, PricePerAccount = 50,  Alteration = 861 },
            [5] = new Oracle { Id = 5, PricePerAccount = 20,  Alteration = 863 },
        };

        /// <summary>The five, in the order they travel in the shop.</summary>
        public static IReadOnlyList<Oracle> All => new List<Oracle>
        {
            Catalogo[1], Catalogo[2], Catalogo[3], Catalogo[4], Catalogo[5],
        };

        public static Oracle Of(int id) => Catalogo.TryGetValue(id, out var oracle) ? oracle : null;

        /// <summary>What it really costs: the price per account times the accounts there are.</summary>
        public static int PriceFor(int id, int accounts)
        {
            var oracle = Of(id);
            return oracle == null ? 0 : oracle.PricePerAccount * (accounts < 1 ? 1 : accounts);
        }

        /// <summary>
        /// How long an oracle lasts: two hours. Its own description says so -- «durante dos horas» -- and the
        /// capture confirms it, where the lzs goes from 1788304392333 to 1788311580000.
        /// </summary>
        public const int HoursActive = 2;

        /// <summary>
        /// How long one has to activate it from buying it: a day. In the capture the deadline travelling in the
        /// jkv falls 24 hours minus a few seconds after the purchase.
        /// </summary>
        public const int HoursToActivate = 24;
    }
}
