using System.Linq;
using Jondo.Unity.Server;
using Jondo.Unity.Server.Managers;
using Jondo.Unity.Server.Network;
using Jondo.Unity.World.Content;
using Xunit;

namespace Jondo.Unity.Tests.Economy
{
    /// <summary>
    /// Gladis Hadora (NPC 1511), the koliseo's token seller: her whole stock for Kolichas, not only
    /// the four pebbles, and the conditions of sale that ask for a koliseo league («KL») or a level.
    /// </summary>
    [Collection("forgemagic")]
    public sealed class KolizetonShopTests
    {
        private const int Gladis = 1511;
        private const int Kolichas = 12736;

        private const int CrimsonPebble = 12737;      // KL>18|PL>49
        private const int GleamingPebble = 12738;     // KL>30|PL>99
        private const int RadiantPebble = 29444;      // KL>36|PL>149
        private const int GladiatorShield = 20138;    // no condition
        private const int ReinforkedShelled = 25981;  // KL>18 only
        private const int Karmeleon = 13182;          // KL>40 only
        private const int BigWave = 20384;            // the account-linked scroll

        private const int SilverIII = 18, PlatinumIII = 38, PlatinumV = 40, DiamondI = 42;

        private static PlayerCriteria.Facts FactsOf(int level, int league)
        {
            var session = GameSession.SinSocket();
            session.State.CharacterLevel = level;
            return new PlayerCriteria.Facts(session.State, null, null, 0, _ => 0, KoliseoLeague: () => league);
        }

        private static bool Met(string criterion, PlayerCriteria.Facts facts)
            => Criterion.Met(criterion, condition => PlayerCriteria.Resolve(condition, facts));

        [Fact]
        public void A_league_condition_compares_the_highest_league_by_rank()
        {
            var platinum = FactsOf(1, PlatinumIII);
            Assert.True(Met("KL>" + SilverIII, platinum));
            Assert.False(Met("KL>" + PlatinumIII, platinum));
            Assert.True(Met("KL=" + PlatinumIII, platinum));

            var unplaced = FactsOf(1, KoliseoLeagues.None);
            Assert.False(Met("KL>" + SilverIII, unplaced));

            // 19 is no league: the condition is not understood, so it does not open the door.
            Assert.False(Met("KL>19", platinum));
        }

        [Fact]
        public void Gladis_sells_her_whole_stock_for_kolichas()
        {
            NpcShops.Initialize();
            var stock = NpcShops.CatalogueOf(Gladis);
            Assert.Equal(130, stock.Count);
            Assert.Equal(stock.Count, stock.Distinct().Count());
            foreach (int gid in new[] { CrimsonPebble, GleamingPebble, RadiantPebble, GladiatorShield,
                                        ReinforkedShelled, Karmeleon, BigWave })
                Assert.Contains(gid, stock);
            Assert.DoesNotContain(14872, stock);   // the other Big Wave scroll

            var shop = TokenShops.Of(Gladis);
            Assert.NotNull(shop);
            Assert.Equal(Kolichas, shop!.TokenGid);
            Assert.Equal(10, TokenShops.PriceOf(shop, CrimsonPebble));
            Assert.Equal(20000, TokenShops.PriceOf(shop, Karmeleon));
        }

        [Fact]
        public void Her_conditions_of_sale_ask_for_a_level_or_a_league()
        {
            NpcShops.Initialize();
            var levelOnly = NpcShops.CatalogueFor(Gladis, c => Met(c, FactsOf(120, KoliseoLeagues.None)));
            Assert.Contains(CrimsonPebble, levelOnly);
            Assert.Contains(GleamingPebble, levelOnly);
            Assert.Contains(GladiatorShield, levelOnly);
            Assert.DoesNotContain(RadiantPebble, levelOnly);
            Assert.DoesNotContain(ReinforkedShelled, levelOnly);
            Assert.DoesNotContain(Karmeleon, levelOnly);

            var platinum = NpcShops.CatalogueFor(Gladis, c => Met(c, FactsOf(1, PlatinumV)));
            Assert.Contains(RadiantPebble, platinum);
            Assert.Contains(ReinforkedShelled, platinum);
            Assert.DoesNotContain(Karmeleon, platinum);

            var diamond = NpcShops.CatalogueFor(Gladis, c => Met(c, FactsOf(1, DiamondI)));
            Assert.Contains(Karmeleon, diamond);
        }
    }
}
