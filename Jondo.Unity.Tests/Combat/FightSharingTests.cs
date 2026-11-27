using Jondo.Unity.Server.Handlers;
using Jondo.Unity.Server.Managers;
using Jondo.Unity.World.Fights;
using Xunit;

namespace Jondo.Unity.Tests.Combat
{
    /// <summary>
    /// A fight of several players: what it pays each of them, and a side kept to its party.
    /// </summary>
    public class FightSharingTests
    {
        // ─── The shares ─────────────────────────────────────────────────────────────────

        /// <summary>Solo also applies the level mismatch and individual cap.</summary>
        [Fact]
        public void Solo_XP_is_reduced_against_a_low_level_monster_but_kamas_are_not_shared()
        {
            Assert.Equal(250, FightHandler.XpShare(10000, 354, new[] { 354 }, 20, 20));
            Assert.Equal(65, FightHandler.KamasShare(65, 130, new[] { 130 }));
        }

        /// <summary>
        /// The individual cap reduces total distributed XP; the denominator keeps full combat levels.
        /// </summary>
        [Fact]
        public void Two_players_share_the_group_bonus_by_level_up_to_the_cap()
        {
            int[] levels = { 447, 354 };
            Assert.Equal(3437, FightHandler.XpShare(10000, 447, levels, 50, 400));
            Assert.Equal(3437, FightHandler.XpShare(10000, 354, levels, 50, 400));

            // Against a monster strong enough, the levels count whole.
            int[] mixed = { 200, 100 };
            Assert.Equal(733, FightHandler.XpShare(1000, 200, mixed, 200, 300));
            Assert.Equal(366, FightHandler.XpShare(1000, 100, mixed, 200, 300));
        }

        /// <summary>Someone under a third of the highest level does not count for the group bonus.</summary>
        [Fact]
        public void A_low_level_does_not_raise_the_group_bonus()
        {
            int[] levels = { 200, 50 };
            Assert.Equal(800, FightHandler.XpShare(1000, 200, levels, 200, 250));
            Assert.Equal(200, FightHandler.XpShare(1000, 50, levels, 200, 250));
        }

        /// <summary>The kamas go by prospecting: 47 between 300 and 170 is 30 and 17.</summary>
        [Fact]
        public void The_kamas_go_by_prospecting()
        {
            int[] prospectings = { 300, 170 };
            Assert.Equal(30, FightHandler.KamasShare(47, 300, prospectings));
            Assert.Equal(17, FightHandler.KamasShare(47, 170, prospectings));
        }

        // ─── The options ────────────────────────────────────────────────────────────────

        /// <summary>A side kept to its party on the map: { f4: 1 } in its options block.</summary>
        [Fact]
        public void A_side_kept_to_its_party_shows_on_the_map()
        {
            var fight = Fight(9_968_000_021);
            fight.SetOption(FightInstance.Azules, FightInstance.OptionPartyOnly, true);
            var open = FightHandler.MapSnapshotOf(fight);
            Assert.True(open.TeamsOptions[0].IsRestrictedToParty);
            Assert.False(open.TeamsOptions[1].IsRestrictedToParty);
        }

        private static FightInstance Fight(long player)
        {
            var fight = new FightInstance(9_000_001, 191105026);
            fight.AddPlayer(new Fighter { Id = player, TeamId = FightInstance.Azules, MaxHP = 10, CurrentHP = 10 });
            return fight;
        }

        /// <summary>
        /// A side kept to its party takes the party's members and nobody else; a closed side
        /// nobody at all.
        /// </summary>
        [Fact]
        public void A_side_kept_to_its_party_takes_only_the_party()
        {
            const long leader = 9_100_000_001, member = 9_100_000_002, stranger = 9_100_000_003;
            var fight = Fight(leader);
            var party = Parties.Create(leader);
            try
            {
                Assert.True(Parties.Invite(party, member, leader));
                Assert.True(Parties.Accept(party, member));

                Assert.True(FightHandler.MayJoinSide(fight, FightInstance.Azules, stranger));
                Assert.True(FightHandler.RestrictToPartyOnOpening(fight, FightInstance.Azules));
                Assert.True(fight.OptionOn(FightInstance.Azules, FightInstance.OptionPartyOnly));

                Assert.True(FightHandler.MayJoinSide(fight, FightInstance.Azules, member));
                Assert.False(FightHandler.MayJoinSide(fight, FightInstance.Azules, stranger));

                fight.SetOption(FightInstance.Azules, FightInstance.OptionClosed, true);
                Assert.Equal(FightInstance.JoinRefusal.Closed, fight.CanJoin(FightInstance.Azules, 8));
            }
            finally { Parties.Dissolve(party); }
        }

        /// <summary>A side with nobody in a party opens as it always did.</summary>
        [Fact]
        public void A_side_without_a_party_is_not_restricted()
        {
            var fight = Fight(9_100_000_004);
            Assert.False(FightHandler.RestrictToPartyOnOpening(fight, FightInstance.Azules));
            Assert.False(fight.OptionOn(FightInstance.Azules, FightInstance.OptionPartyOnly));
        }
    }
}
