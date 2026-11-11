using System.Globalization;
using Jondo.Unity.Protocol.Game;
using Jondo.Unity.Server.Managers;

namespace Jondo.Unity.Server.Network;

/// <summary>The selected character's arena standings and current penalty, independent of capture bytes.</summary>
/// <remarks>
/// In "koliseo completo" the recorded player lost the 2v2: one season/day fight,
/// zero victories and four placement fights left. Keep these separate counters;
/// the old season-date and current/best-league interpretations were incorrect.
/// </remarks>
internal static class ArenaPlayerInformationMessages
{
    internal static ArenaUpdatePlayerInformationEvent Snapshot(IEnumerable<KoliseoLadder.Standing> standings,
        DateTime? banEndUtc, DateTime nowUtc)
    {
        // The client parses this string even without a penalty. A past, valid ISO date
        // means no active ban; an empty string fails its invariant DateTime parser.
        // The old builder supplied the captured season-start date here by mistake.
        var expiry = banEndUtc is { } end && end > nowUtc ? end : DateTime.UnixEpoch;
        return new(expiry.ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'", CultureInfo.InvariantCulture),
            standings.Select(standing => new ArenaRank(
                ArenaType: standing.Mode switch
                {
                    0 => ArenaType.OneVsOne,
                    1 => ArenaType.TwoVsTwo,
                    2 => ArenaType.ThreeVsThree,
                    3 => ArenaType.Event,
                    _ => throw new ArgumentOutOfRangeException(nameof(standings), standing.Mode, "Unknown arena mode."),
                },
                // Historical best rating and ladder position are not stored in this snapshot.
                // Retain their existing zero values rather than inventing historical/ranking data.
                BestRating: 0,
                BestLeagueId: standing.BestLeague,
                FightNeededForLadder: standing.PlacementLeft,
                SeasonVictoryCount: standing.SeasonWins,
                SeasonFightCount: standing.SeasonFights,
                DailyVictoryCount: standing.DayWins,
                LeagueRanking: new(standing.League, standing.Rating, LadderPosition: 0),
                DailyFightCount: standing.DayFights)).ToArray());
    }
}
