using System;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;
using Jondo.Unity.Protocol;
using Jondo.Unity.Server.Managers;
using Jondo.Unity.Server.Network;

namespace Jondo.Unity.Server.Handlers
{
    /// <summary>
    /// Osavora's seasons on the wire (OsavoraSeasons): the season's staging on loading one of the
    /// dimension's maps, at its end (hpm 100); and when the season turns, played from its start to
    /// everybody standing in the dimension.
    /// </summary>
    public static class OsavoraSeasonHandler
    {
        private static Timer? _clock;
        private static int _ticking;
        private static OsavoraSeasons.Season _season = OsavoraSeasons.At(DateTimeOffset.UtcNow);

        /// <summary>The clock that turns the seasons, looked at every minute. At the server's start.</summary>
        public static void Start()
        {
            _season = OsavoraSeasons.At(DateTimeOffset.UtcNow);
            Console.WriteLine($"[Osavora] It is the season of {_season}, until {OsavoraSeasons.NextChange(DateTimeOffset.UtcNow):yyyy-MM-dd HH:mm zzz}.");
            _clock ??= new Timer(_ =>
            {
                if (Interlocked.Exchange(ref _ticking, 1) != 0) return;
                try { TickAsync().GetAwaiter().GetResult(); }
                catch (Exception ex) { Console.WriteLine($"[Osavora] The seasons' clock failed: {ex.Message}"); }
                finally { Interlocked.Exchange(ref _ticking, 0); }
            }, null, TimeSpan.FromMinutes(1), TimeSpan.FromMinutes(1));
        }

        /// <summary>A season over: the next one played to whoever is in the dimension.</summary>
        public static async Task TickAsync()
        {
            var now = OsavoraSeasons.At(DateTimeOffset.UtcNow);
            if (now == _season) return;
            _season = now;
            Console.WriteLine($"[Osavora] The season turns: {now}.");
            foreach (var session in SessionRegistry.InWorld())
            {
                if (session.State.IsInFight) continue;
                int staging = OsavoraSeasons.StagingOn(session.State.MapId, now);
                if (staging == 0) continue;
                try
                {
                    await session.SendAsync(ConnectionProtocol.Push(Op.Hpm, GuildRaidStagings.BuildPlay(staging, GuildRaidStagings.FromStart)));
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"[Osavora] The season could not reach {session.State.CharacterId}: {ex.Message}");
                }
            }
        }

        /// <summary>One of the dimension's maps loaded: its look for the season, as it stands.</summary>
        public static async Task OnMapLoadedAsync(GameSession session)
        {
            int staging = OsavoraSeasons.StagingOn(session.MapId, OsavoraSeasons.At(DateTimeOffset.UtcNow));
            if (staging == 0) return;
            await session.SendAsync(GuildRaidStagings.StandingEvent(staging));
        }
    }
}
