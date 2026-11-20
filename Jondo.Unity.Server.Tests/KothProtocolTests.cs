using Jondo.Unity.Protocol.Game;
using Jondo.Unity.Server.Managers;
using Jondo.Unity.Server.Network;
using Xunit;

namespace Jondo.Unity.Server.Tests;

public sealed class KothProtocolTests
{
    [Fact]
    public void Score_snapshot_preserves_tallies_and_columns_and_rejects_overflow()
    {
        var war = new Conquests.War { DefenderId = 1, StartedUtcMs = 1_800_000_000_123, NextTickUtcMs = 1_800_000_060_123 };
        var side = war.SideOf(1);
        side.Present = 5;
        side.Domination = 2;
        side.Roles = new Dictionary<int, int> { [10] = 1, [5] = 2 };
        side.Scores[Conquests.ScoreOfMaps] = 31;
        side.Scores[Conquests.ScoreOfFights] = -5;
        side.Scores[Conquests.ScoreOfPrism] = int.MaxValue;
        war.SideOf(2);
        war.SideOf(3); // Missing alliance, excluded from the event.
        var own = new AllianceStore.Alliance { Uuid = "own" };
        var other = new AllianceStore.Alliance { Uuid = "other" };
        AllianceStore.Alliance? Resolve(long id) => id switch { 1 => own, 2 => other, _ => null };
        var snapshot = KothProtocol.Update(war, Resolve, 8);
        side.Roles = new Dictionary<int, int>();
        side.Scores[Conquests.ScoreOfPrism]++;
        Assert.Throws<OverflowException>(() => KothProtocol.Update(war, Resolve, 8));

        Assert.Equal(war.NextTickUtcMs, snapshot.NextTickTimeMs);
        Assert.Equal("2027-01-15T08:00:00.1230000Z", snapshot.StartingAvaTimestamp);
        Assert.Collection(snapshot.Alliances,
            first =>
            {
                Assert.Equal("own", first.Alliance!.Uid);
                Assert.Equal(2, first.DominationScore);
                Assert.Equal(5L, first.MemberCount);
                Assert.Equal(new[] { new KothAllianceRoleNumber(2, 5), new KothAllianceRoleNumber(1, 10), new KothAllianceRoleNumber(2, 8) }, first.KothRoles);
                Assert.Equal(new[] { new KothScore(31, KothScoreType.Fight), new KothScore(-5, KothScoreType.Domination),
                    new KothScore(int.MaxValue, KothScoreType.Prism) }, first.Scores);
            },
            second =>
            {
                Assert.Equal("other", second.Alliance!.Uid);
                Assert.Empty(second.KothRoles);
                Assert.Equal(3, second.Scores.Count);
                Assert.All(second.Scores, score => Assert.Equal(0, score.CumulativePoints));
            });
    }
}
