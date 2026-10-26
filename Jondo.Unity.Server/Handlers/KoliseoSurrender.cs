using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;
using Jondo.Unity.Protocol;
using Jondo.Unity.Server.Network;
using Jondo.Unity.World.Fights;
using static Jondo.Protocol.NetworkMessage;

namespace Jondo.Unity.Server.Handlers
{
    /// <summary>
    /// Giving up a Koliseo fight of people. The leave button opens the surrender window (ltf, answered
    /// with luw), whose «Abandonar» leaves alone -- the kme AbandonAsync answers -- and whose «Votar para
    /// rendirse» opens the team's vote (lsp). Every yes is counted (lto); with all of them the team falls,
    /// its people told they gave up (ltn true) and the other team that it did (lss); one no, or the
    /// minute run out, and it is over without (ltn false).
    /// </summary>
    /// <remarks>
    /// Read off the client: the surrender service (flw) and its network side (flu sends, flv handles).
    /// Of all of it the 2v2 koliseo capture holds only the ltm that opens it; nobody gave up there.
    /// The vote's minute is the group search's (Matchmaking.VoteSeconds), an inference as that one is:
    /// the client counts no time down for it.
    /// </remarks>
    public static partial class FightHandler
    {
        /// <summary>A team's give-up vote: who votes, and who has said yes.</summary>
        private sealed class SurrenderVote
        {
            public HashSet<long> Voters { get; init; } = new();
            public HashSet<long> Yes { get; } = new();
            public CancellationTokenSource Clock { get; } = new();
        }

        /// <summary>The open votes, by fight and team.</summary>
        private static readonly ConcurrentDictionary<(long Fight, int Team), SurrenderVote> _surrenderVotes = new();

        /// <summary>
        /// Whether a fight is a Koliseo one of people giving up opens in: every Koliseo fight but
        /// the one against JondoBots, which leaves at once (<see cref="CanGiveUpAtOnce"/>).
        /// </summary>
        internal static bool OpensSurrender(FightInstance fight, long characterId)
            => fight != null && fight.KoliseoMode >= 0 && !CanGiveUpAtOnce(fight, characterId);

        /// <summary>
        /// The leave button of a Koliseo fight, unlocked (ltm): leaving at once against JondoBots,
        /// the surrender window in a fight of people. Nothing in any other fight.
        /// </summary>
        internal static async Task SendSurrenderStateAsync(NetworkStream stream, FightInstance fight, long me)
        {
            if (CanGiveUpAtOnce(fight, me))
            {
                await WriteFrameAsync(stream, ConnectionProtocol.Push(Op.Ltm,
                    Network.FightProtocol.BuildSurrenderUnlocked(true)));
                Program.LogDebug($"[Koliseo] {me} may give up fight #{fight.FightId}.");
            }
            else if (OpensSurrender(fight, me))
            {
                await WriteFrameAsync(stream, ConnectionProtocol.Push(Op.Ltm,
                    Network.FightProtocol.BuildSurrenderOpen()));
                Program.LogDebug($"[Koliseo] {me} may leave or vote to give up fight #{fight.FightId}.");
            }
        }

        /// <summary>The surrender window asked for (ltf): leaving and voting, both open.</summary>
        private static async Task SurrenderOptionsAsync(NetworkStream stream, FightInstance fight)
        {
            if (fight == null || fight.KoliseoMode < 0 || fight.State != FightState.Ongoing) return;
            await WriteFrameAsync(stream, ConnectionProtocol.Push(Op.Luw,
                Network.FightProtocol.BuildSurrenderOptions()));
        }

        /// <summary>
        /// A ballot (lsp). The first yes of a team opens its vote, which counts the yes; a no
        /// closes it.
        /// </summary>
        private static async Task SurrenderBallotAsync(NetworkStream stream, FightInstance fight, byte[] payload)
        {
            if (fight == null || fight.KoliseoMode < 0 || fight.State != FightState.Ongoing) return;
            long me = GameState.CharacterId;
            int team = fight.EquipoDe(me);
            if (team < 0 || fight.Gone.Contains(me)) return;

            bool yes = Network.FightProtocol.ReadSurrenderBallot(payload);
            var key = (fight.FightId, team);

            if (!_surrenderVotes.TryGetValue(key, out var vote))
            {
                if (!yes) return;
                vote = new SurrenderVote { Voters = VotersOf(fight, team) };
                vote.Voters.Add(me);
                vote.Yes.Add(me);
                _surrenderVotes[key] = vote;
                ExpireSurrenderVote(fight, key, vote);
                Program.LogDebug($"[Koliseo] {me} opens the give-up vote of fight #{fight.FightId}, " +
                                 $"team {team}: {vote.Voters.Count} voter(s).");
                await ToEachAsync(vote.Voters, ConnectionProtocol.Push(Op.Lsq,
                    Network.FightProtocol.BuildSurrenderVote(vote.Voters.Count, vote.Yes.Count)));
            }
            else
            {
                if (!vote.Voters.Contains(me) || vote.Yes.Contains(me)) return;
                if (!yes)
                {
                    await CloseSurrenderVoteAsync(fight, key, vote, gaveUp: false);
                    Program.LogDebug($"[Koliseo] {me} votes no: team {team} of fight #{fight.FightId} fights on.");
                    return;
                }
                vote.Yes.Add(me);
                await ToEachAsync(vote.Voters, ConnectionProtocol.Push(Op.Lto, Array.Empty<byte>()));
                Program.LogDebug($"[Koliseo] {me} votes to give up: {vote.Yes.Count} of {vote.Voters.Count}.");
            }

            if (vote.Yes.Count < vote.Voters.Count) return;

            await CloseSurrenderVoteAsync(fight, key, vote, gaveUp: true);
            await ToEachAsync(VotersOf(fight, FightInstance.Contrario(team)),
                ConnectionProtocol.Push(Op.Lss, Array.Empty<byte>()));
            Program.LogDebug($"[Koliseo] Team {team} gives up fight #{fight.FightId}.");
            await FallTogetherAsync(stream, fight, fight.Bando(team), "giving up");
        }

        /// <summary>The vote over, either way: its people told, its clock stopped.</summary>
        private static async Task CloseSurrenderVoteAsync(FightInstance fight, (long Fight, int Team) key,
                                                          SurrenderVote vote, bool gaveUp)
        {
            _surrenderVotes.TryRemove(key, out _);
            vote.Clock.Cancel();
            await ToEachAsync(vote.Voters, ConnectionProtocol.Push(Op.Ltn,
                Network.FightProtocol.BuildSurrenderResult(gaveUp)));
        }

        /// <summary>The minute of a vote: still open when it runs out, it is over without giving up.</summary>
        private static void ExpireSurrenderVote(FightInstance fight, (long Fight, int Team) key, SurrenderVote vote)
        {
            var token = vote.Clock.Token;
            _ = Task.Run(async () =>
            {
                try
                {
                    await Task.Delay(TimeSpan.FromSeconds(Managers.Matchmaking.VoteSeconds), token);
                    await InTurnAsync(fight, async () =>
                    {
                        if (!_surrenderVotes.TryGetValue(key, out var open) || open != vote) return;
                        await CloseSurrenderVoteAsync(fight, key, vote, gaveUp: false);
                        Program.LogDebug($"[Koliseo] The give-up vote of fight #{fight.FightId}, team {key.Team}, runs out.");
                    });
                }
                catch (OperationCanceledException)
                {
                }
                catch (Exception ex)
                {
                    Program.LogDebug($"[Koliseo] The give-up vote's clock choked: {ex.Message}");
                }
            });
        }

        /// <summary>The people of a team still in the fight and connected: who votes.</summary>
        private static HashSet<long> VotersOf(FightInstance fight, int team)
            => fight.Bando(team)
                    .Where(f => f != null && !f.IsMonster && !f.EsInvocado && !f.EsIlusion && !f.IsBot
                                && !fight.Gone.Contains(f.Id) && SessionRegistry.FindByCharacter(f.Id) != null)
                    .Select(f => f.Id)
                    .ToHashSet();

        /// <summary>The same frame to each of these people.</summary>
        private static async Task ToEachAsync(IEnumerable<long> people, byte[] frame)
        {
            foreach (long who in people.ToList())
            {
                var session = SessionRegistry.FindByCharacter(who);
                if (session == null) continue;
                try
                {
                    await session.SendAsync(frame);
                }
                catch (Exception ex)
                {
                    Program.LogDebug($"[Koliseo] Could not write to {session.Id}: {ex.Message}");
                }
            }
        }

        /// <summary>The fight is over: its votes go.</summary>
        private static void ForgetSurrenderVotes(FightInstance fight)
        {
            foreach (var key in _surrenderVotes.Keys.Where(k => k.Fight == fight.FightId).ToList())
            {
                if (_surrenderVotes.TryRemove(key, out var vote)) vote.Clock.Cancel();
            }
        }
    }
}
