using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Jondo.Unity.Launcher;
using Jondo.Unity.Server.Managers;
using Jondo.Unity.Server.Network;

namespace Jondo.Unity.Server.Handlers
{
    /// <summary>
    /// Who hears a chat line (kti), by its channel -- the client's ChatChannel: 0 general, 1 team,
    /// 2 guild, 3 alliance, 4 party, 5 trade, 6 recruitment, 14 community. Whoever said it gets it
    /// back on his own, before.
    /// </summary>
    /// <remarks>
    /// Only the general, guild and alliance channels reached anybody else: a line on trade,
    /// recruitment, the party or the team went back to whoever wrote it alone, and two characters
    /// side by side each read only their own.
    /// </remarks>
    public static class ChatChannels
    {
        public const int General = 0, Team = 1, Guild = 2, Alliance = 3, Party = 4,
                         Trade = 5, Recruitment = 6, Community = 14;

        /// <summary>
        /// One line on trade or recruitment every two minutes, as on Dofus Retro's: the official
        /// one varies -- players quote «Podrás enviar un nuevo mensaje dentro de 334 segundos» --
        /// and is the client's information 115 when one writes too soon.
        /// </summary>
        public static readonly TimeSpan RestrictedDelay = TimeSpan.FromMinutes(2);

        /// <summary>The channels held to <see cref="RestrictedDelay"/>.</summary>
        public static bool IsRestricted(int channel) => channel is Trade or Recruitment;

        private static readonly ConcurrentDictionary<(long Character, int Channel), DateTime> _lastSaid = new();

        /// <summary>
        /// The seconds he has still to wait before saying something on this channel, rounded up; zero
        /// when he may, and then the line counts from <paramref name="now"/>. The staff, from
        /// moderator up, are not held.
        /// </summary>
        public static int WaitLeft(long characterId, int channel, int role, DateTime now)
        {
            if (!IsRestricted(channel) || Roles.AlMenos(role, Roles.Moderador)) return 0;
            var key = (characterId, channel);
            if (_lastSaid.TryGetValue(key, out var last) && now - last < RestrictedDelay)
                return (int)Math.Ceiling((RestrictedDelay - (now - last)).TotalSeconds);
            _lastSaid[key] = now;
            return 0;
        }

        /// <summary>Hands a line out to whoever hears its channel, the speaker aside. Returns how many heard it.</summary>
        public static async Task<int> SayAsync(GameSession speaker, int channel, byte[] line)
        {
            long me = speaker.CharacterId;
            switch (channel)
            {
                case General:
                    // The map's, not the server's.
                    return await SessionRegistry.BroadcastToMapAsync(speaker.State.MapId, line, speaker.Id);
                case Guild:
                    return await GuildHandler.SayAsync(me, line);
                case Alliance:
                    return await AllianceHandler.SayAsync(me, line);
                case Trade:
                case Recruitment:
                case Community:
                    // The whole server's.
                    return await ToAsync(SessionRegistry.InWorld().Where(s => s.CharacterId != me), line);
                case Party:
                {
                    var party = Parties.Of(me);
                    if (party == null) return 0;
                    var members = Parties.MembersOf(party);
                    return await ToAsync(members.Where(id => id != me).Select(SessionRegistry.FindByCharacter), line);
                }
                case Team:
                {
                    // His side of the fight he is in.
                    var fight = FightHandler.FightOf(me);
                    if (fight == null) return 0;
                    var side = fight.Azul.Any(f => f.Id == me) ? fight.Azul : fight.Rojo;
                    return await ToAsync(side.Where(f => f.Id != me && !f.IsMonster)
                                             .Select(f => SessionRegistry.FindByCharacter(f.Id)), line);
                }
                default:
                    return 0;
            }
        }

        private static async Task<int> ToAsync(IEnumerable<GameSession?> sessions, byte[] line)
        {
            int heard = 0;
            foreach (var session in sessions.Where(s => s != null).Distinct())
            {
                await session!.SendAsync(line);
                heard++;
            }
            return heard;
        }
    }
}
