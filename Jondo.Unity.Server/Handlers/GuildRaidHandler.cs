using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Net.Sockets;
using System.Threading.Tasks;
using Jondo.Unity.Protocol;
using Jondo.Unity.Server.Managers;
using Jondo.Unity.Server.Network;
using Jondo.Unity.World.Content;

namespace Jondo.Unity.Server.Handlers
{
    /// <summary>
    /// The guild window's Raids tab and the guild shop's raids, answered the way the client's raid
    /// frame expects: each request with its result (on the request's id), and every change told to
    /// whoever sees the raid -- its guild's members connected and its participants.
    /// </summary>
    public static class GuildRaidHandler
    {
        private static async Task WriteAsync(NetworkStream stream, byte[] frame)
            => await Jondo.Protocol.NetworkMessage.WriteFrameAsync(stream, frame);

        private static List<ProtoField> Fields(byte[] frame, string opcode)
        {
            byte[] payload = ConnectionProtocol.ReadPayload(frame, opcode);
            return payload == null ? new List<ProtoField>() : ProtoMessage.Parse(payload).Fields.ToList();
        }

        private static long Number(List<ProtoField> fields, int field)
            => fields.FirstOrDefault(f => f.FieldNumber == field && f.WireType == 0)?.VarIntValue ?? 0;

        private static List<string> Texts(List<ProtoField> fields)
            => fields.Where(f => f.WireType == 2)
                     .Select(f => System.Text.Encoding.UTF8.GetString(f.BytesValue))
                     .ToList();

        /// <summary>
        /// The two texts of a request that names a raid and something else -- a target, a note.
        /// Which comes first is not certain from the client, so the raid is the one that IS a raid.
        /// </summary>
        private static (GuildRaidBoard.Raid Raid, string Other) RaidAndOther(List<string> texts)
        {
            foreach (string text in texts)
            {
                var raid = GuildRaidBoard.Find(text);
                if (raid != null) return (raid, texts.FirstOrDefault(t => t != text) ?? "");
            }
            return (null, texts.FirstOrDefault() ?? "");
        }

        private static long CharacterIdOf(string text)
            => long.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out long id) ? id : 0;

        /// <summary>Everybody who sees a raid: its guild's members connected, and its participants.</summary>
        private static List<GameSession> Watchers(GuildRaidBoard.Raid raid)
        {
            var ids = new HashSet<long>(GuildStore.Members(raid.GuildId).Select(m => m.CharacterId));
            foreach (var p in raid.Participants) ids.Add(p.CharacterId);
            return ids.Select(SessionRegistry.FindByCharacter).Where(s => s != null).ToList();
        }

        /// <summary>The raid, as it is now, to whoever sees it.</summary>
        internal static async Task TellUpdatedAsync(GuildRaidBoard.Raid raid, IEnumerable<long> alsoTo = null)
        {
            byte[] frame = ConnectionProtocol.Push(Op.Iai, GuildRaidProtocol.BuildRaidUpdated(raid));
            var sessions = Watchers(raid);
            foreach (long extra in alsoTo ?? Enumerable.Empty<long>())
            {
                var session = SessionRegistry.FindByCharacter(extra);
                if (session != null && !sessions.Contains(session)) sessions.Add(session);
            }
            foreach (var session in sessions) await session.SendAsync(frame);
        }

        /// <summary>
        /// The Raids tab (hzc → ice): the raids the player sees, by their uuid. A captain opening it
        /// may start his raid, when the server does the starting (<see cref="AutoStartAsync"/>).
        /// </summary>
        public static async Task ShowAsync(NetworkStream stream, byte[] frame)
        {
            await WriteAsync(stream, ConnectionProtocol.Answer(Op.Ice,
                GuildRaidProtocol.BuildGuildRaidShow(GuildRaidBoard.VisibleTo(GameState.CharacterId)),
                ConnectionProtocol.RequestId(frame)));
            var mine = GuildRaidBoard.OfParticipant(GameState.CharacterId);
            if (mine?.Captain?.CharacterId == GameState.CharacterId) await AutoStartAsync(mine);
        }

        /// <summary>
        /// The server starts a raid itself when its settings lower the minimum of players below the
        /// client's, whose start button stays off until the client's own minimum (eight): bought,
        /// joined or looked at by its captain, a raid with the server's minimum is starting and
        /// every participant -- the captain too -- gets the usual prompt to accept. A refusal or the
        /// minute running out takes it back to constitution, and the captain's next look at the
        /// tab starts it again. With the setting at the game's minimum nothing of this happens.
        /// </summary>
        private static async Task AutoStartAsync(GuildRaidBoard.Raid raid)
        {
            if (!GuildRaidBoard.AutoStart(raid, DateTimeOffset.UtcNow)) return;
            Program.LogDebug($"[Raids] Raid {raid.RaidId} ({raid.Uuid}) starts by itself: the server's minimum is below the client's.");
            await TellUpdatedAsync(raid);
            byte[] prompt = ConnectionProtocol.Push(Op.Idf, GuildRaidProtocol.BuildStartPrompt(raid));
            foreach (long id in GuildRaidBoard.NotAccepted(raid))
            {
                var session = SessionRegistry.FindByCharacter(id);
                if (session != null) await session.SendAsync(prompt);
            }
            WatchDeadline(raid, raid.StartDeadline);
        }

        /// <summary>
        /// Buying a raid from the guild shop (idm → hwe): f1 the raid, f2 the group if one was
        /// chosen. Bought, the raid appears in everyone's tab (hya), and the buyer is told the guild
        /// kamas left (jia), as the capture of buying one has it.
        /// </summary>
        public static async Task PurchaseAsync(NetworkStream stream, byte[] frame)
        {
            var fields = Fields(frame, Op.Idm);
            var (result, raid) = GuildRaidBoard.Purchase(GameState.CharacterId, (int)Number(fields, 1), (int)Number(fields, 2));
            await WriteAsync(stream, ConnectionProtocol.Answer(Op.Hwe, GuildRaidProtocol.BuildPurchaseResult(result),
                                                               ConnectionProtocol.RequestId(frame)));
            if (raid == null) return;

            byte[] bought = ConnectionProtocol.Push(Op.Hya, GuildRaidProtocol.BuildPurchased(raid));
            foreach (var session in Watchers(raid)) await session.SendAsync(bought);
            var guild = GuildStore.GuildOf(GameState.CharacterId);
            if (guild != null)
                await WriteAsync(stream, ConnectionProtocol.Push(Op.Jia, GuildProtocol.BuildGuildKamas(guild.GuildKamas)));
            await AutoStartAsync(raid);
            Program.LogDebug($"[Raids] {GameState.CharacterId} bought raid {raid.RaidId} ({raid.Uuid}).");
        }

        /// <summary>Joining a raid (hzi → hwk): f3 the raid's uuid, and the group if one was chosen.</summary>
        public static async Task JoinAsync(NetworkStream stream, byte[] frame)
        {
            var fields = Fields(frame, Op.Hzi);
            string uuid = Texts(fields).FirstOrDefault() ?? "";
            int group = (int)(Number(fields, 1) != 0 ? Number(fields, 1) : Number(fields, 2));
            var result = GuildRaidBoard.Join(GameState.CharacterId, uuid, group);
            await WriteAsync(stream, ConnectionProtocol.Answer(Op.Hwk, GuildRaidProtocol.BuildResult((int)result),
                                                               ConnectionProtocol.RequestId(frame)));
            if (result != GuildRaidBoard.JoinResult.Done) return;
            var raid = GuildRaidBoard.Find(uuid);
            await TellUpdatedAsync(raid);
            await AutoStartAsync(raid);
        }

        /// <summary>Leaving one's raid in constitution (hym → hvs).</summary>
        public static async Task LeaveAsync(NetworkStream stream, byte[] frame)
        {
            var (result, raid) = GuildRaidBoard.Leave(GameState.CharacterId);
            await WriteAsync(stream, ConnectionProtocol.Answer(Op.Hvs, GuildRaidProtocol.BuildResult((int)result),
                                                               ConnectionProtocol.RequestId(frame)));
            if (result == GuildRaidBoard.LeaveResult.Done)
                await TellUpdatedAsync(raid, new[] { GameState.CharacterId });
        }

        /// <summary>Naming another captain (hxu → idi): the raid's uuid and the new captain's id.</summary>
        public static async Task UpdateCaptainAsync(NetworkStream stream, byte[] frame)
        {
            var (raid, target) = RaidAndOther(Texts(Fields(frame, Op.Hxu)));
            var result = raid == null
                ? GuildRaidBoard.CaptainResult.RaidNotFound
                : GuildRaidBoard.UpdateCaptain(GameState.CharacterId, raid.Uuid, CharacterIdOf(target));
            await WriteAsync(stream, ConnectionProtocol.Answer(Op.Idi, GuildRaidProtocol.BuildResult((int)result),
                                                               ConnectionProtocol.RequestId(frame)));
            if (result == GuildRaidBoard.CaptainResult.Done) await TellUpdatedAsync(raid);
        }

        /// <summary>
        /// The raid's note (hzk): the raid's uuid and the text. The client does not handle an answer
        /// to it; it redraws the raid from the update everyone gets.
        /// </summary>
        public static async Task UpdateDescriptionAsync(NetworkStream stream, byte[] frame)
        {
            var (raid, text) = RaidAndOther(Texts(Fields(frame, Op.Hzk)));
            if (raid != null && GuildRaidBoard.UpdateNote(GameState.CharacterId, raid.Uuid, text))
                await TellUpdatedAsync(raid);
        }

        /// <summary>
        /// The captain takes someone out (hyb → hwa): the raid's uuid, the participant's id and f3
        /// whether he may not come back.
        /// </summary>
        public static async Task RemoveParticipantAsync(NetworkStream stream, byte[] frame)
        {
            var fields = Fields(frame, Op.Hyb);
            var (raid, target) = RaidAndOther(Texts(fields));
            var result = raid == null
                ? GuildRaidBoard.RemoveResult.RequireRaid
                : GuildRaidBoard.RemoveParticipant(GameState.CharacterId, raid.Uuid, CharacterIdOf(target), Number(fields, 3) != 0);
            await WriteAsync(stream, ConnectionProtocol.Answer(Op.Hwa, GuildRaidProtocol.BuildResult((int)result),
                                                               ConnectionProtocol.RequestId(frame)));
            if (result == GuildRaidBoard.RemoveResult.Done) await TellUpdatedAsync(raid, new[] { CharacterIdOf(target) });
        }

        /// <summary>The captain lets someone he had barred come back (ida): the raid and the player.</summary>
        public static async Task UnblockAsync(NetworkStream stream, byte[] frame)
        {
            var (raid, target) = RaidAndOther(Texts(Fields(frame, Op.Ida)));
            if (raid != null && GuildRaidBoard.Unblock(GameState.CharacterId, raid.Uuid, CharacterIdOf(target)))
                await TellUpdatedAsync(raid);
        }

        /// <summary>
        /// The captain starts his raid (ibr, sent empty → hxt). With its minimum of players, all of
        /// them connected, the raid is starting: everyone who has not accepted yet is asked (idf)
        /// and has until the deadline. With nobody left to ask, it goes in at once.
        /// </summary>
        public static async Task StartAsync(NetworkStream stream, byte[] frame)
        {
            long captain = GameState.CharacterId;
            var (error, raid, offline) = GuildRaidBoard.Start(captain, DateTimeOffset.UtcNow);
            await WriteAsync(stream, ConnectionProtocol.Answer(Op.Hxt,
                GuildRaidProtocol.BuildStartResponse(error, raid?.Uuid, offline), ConnectionProtocol.RequestId(frame)));
            if (error != GuildRaidBoard.StartError.Done || offline != null || raid?.State != GuildRaidBoard.State.Starting)
                return;

            Program.LogDebug($"[Raids] {captain} starts raid {raid.RaidId} ({raid.Uuid}), until {raid.StartDeadline:HH:mm:ss}.");
            var pending = GuildRaidBoard.NotAccepted(raid);
            if (pending.Count == 0)
            {
                var (outcome, _) = GuildRaidBoard.Answer(captain, true);
                if (outcome == GuildRaidBoard.StartOutcome.Everyone) await LaunchAsync(raid);
                return;
            }

            await TellUpdatedAsync(raid);
            byte[] prompt = ConnectionProtocol.Push(Op.Idf, GuildRaidProtocol.BuildStartPrompt(raid));
            foreach (long id in pending)
            {
                var session = SessionRegistry.FindByCharacter(id);
                if (session != null) await session.SendAsync(prompt);
            }
            WatchDeadline(raid, raid.StartDeadline);
        }

        /// <summary>
        /// A participant answers the start (hzd → hxx): f1 accept. Someone fighting cannot accept.
        /// A refusal calls it off for everyone; the last acceptance takes the raid in.
        /// </summary>
        public static async Task AnswerAsync(NetworkStream stream, byte[] frame)
        {
            long who = GameState.CharacterId;
            bool accept = Number(Fields(frame, Op.Hzd), 1) != 0;
            if (accept && GameState.IsInFight)
            {
                await WriteAsync(stream, ConnectionProtocol.Answer(Op.Hxx,
                    GuildRaidProtocol.BuildStartAnswerResult(GuildRaidProtocol.OccupiedCannotAccept),
                    ConnectionProtocol.RequestId(frame)));
                return;
            }

            var (outcome, raid) = GuildRaidBoard.Answer(who, accept);
            await WriteAsync(stream, ConnectionProtocol.Answer(Op.Hxx, GuildRaidProtocol.BuildStartAnswerResult(0),
                                                               ConnectionProtocol.RequestId(frame)));
            switch (outcome)
            {
                case GuildRaidBoard.StartOutcome.Refused:
                    await TellParticipantsAsync(raid, ConnectionProtocol.Push(Op.Icz, GuildRaidProtocol.BuildStartRefused(who)));
                    await TellUpdatedAsync(raid);
                    break;
                case GuildRaidBoard.StartOutcome.Waiting:
                    await TellUpdatedAsync(raid);
                    break;
                case GuildRaidBoard.StartOutcome.Everyone:
                    await LaunchAsync(raid);
                    break;
            }
        }

        /// <summary>
        /// Everyone has accepted: the raid runs, they are taken in, each gets the running raid, which
        /// opens its tracking panel, and then the tab says it runs.
        /// </summary>
        /// <remarks>
        /// In that order. The client opens the panel from the running raid (hvt, its handler ffz::beky
        /// raising the hook bwh) unless the raid is the one its Raids tab is looking at -- the guild
        /// service's watched uuid -- and the tab starts looking at it as soon as it hears the raid
        /// runs. Told the tab first, the panel never opened for whoever had the raid on screen.
        /// </remarks>
        private static async Task LaunchAsync(GuildRaidBoard.Raid raid)
        {
            var running = await GuildRaidManager.LaunchAsync(raid);
            await TellRunningRaidAsync(raid, running, raid.Participants.Select(p => p.CharacterId));
            await TellUpdatedAsync(raid);
        }

        // ─── The running raid ───────────────────────────────────────────────────

        /// <summary>Who looks at a running raid from the guild's tab without being in it, and which.</summary>
        private static readonly ConcurrentDictionary<long, string> _listening = new();

        /// <summary>Everybody the running raid's news goes to: its participants and who looks at it.</summary>
        private static List<GameSession> RunningWatchers(string uuid)
        {
            var ids = new HashSet<long>(GuildRaidBoard.Find(uuid)?.Participants.Select(p => p.CharacterId) ?? Enumerable.Empty<long>());
            foreach (var (id, listened) in _listening)
                if (listened == uuid) ids.Add(id);
            return ids.Select(SessionRegistry.FindByCharacter).Where(s => s != null).ToList();
        }

        private static async Task TellRunningWatchersAsync(string uuid, byte[] frame)
        {
            foreach (var session in RunningWatchers(uuid)) await session.SendAsync(frame);
        }

        /// <summary>The running raid, whole, to these participants: it opens (or redraws) their panel.</summary>
        internal static async Task TellRunningRaidAsync(GuildRaidBoard.Raid board, RaidInstance raid, IEnumerable<long> to)
        {
            if (board == null || raid == null) return;
            byte[] frame = ConnectionProtocol.Push(Op.Hvt,
                GuildRaidProtocol.BuildRunningRaid(board, raid, GuildRaidManager.VariablesOf(raid)));
            foreach (long id in to.ToList())
            {
                var session = SessionRegistry.FindByCharacter(id);
                if (session != null && !raid.HasLeft(id)) await session.SendAsync(frame);
            }
        }

        /// <summary>The running raid's score, goals and variables, to whoever follows it.</summary>
        internal static async Task TellRunningStateAsync(RaidInstance raid)
        {
            if (raid == null || raid.Uuid.Length == 0) return;
            await TellRunningWatchersAsync(raid.Uuid, ConnectionProtocol.Push(Op.Hzg,
                GuildRaidProtocol.BuildRunningStateUpdated(raid, GuildRaidManager.VariablesOf(raid))));
        }

        /// <summary>The running raid's state, for the raid this character is in: after salt or treasure.</summary>
        internal static Task TellRunningStateAsync(long characterId) => TellRunningStateAsync(GuildRaidManager.RunningOf(characterId));

        /// <summary>A participant's line in the panel changed: he came, went, fights or left.</summary>
        internal static async Task TellParticipantStateAsync(RaidInstance raid, long characterId)
        {
            if (raid == null || raid.Uuid.Length == 0) return;
            await TellRunningWatchersAsync(raid.Uuid, ConnectionProtocol.Push(Op.Hwo,
                GuildRaidProtocol.BuildRunningParticipantState(raid.Uuid, characterId, GuildRaidProtocol.StateOf(raid, characterId))));
        }

        /// <summary>The running raid has a new captain.</summary>
        internal static async Task TellRunningCaptainAsync(GuildRaidBoard.Raid board, RaidInstance raid)
        {
            await TellRunningWatchersAsync(board.Uuid, ConnectionProtocol.Push(Op.Hzh,
                GuildRaidProtocol.BuildRunningCaptain(board.Uuid, raid.CaptainId)));
            await TellUpdatedAsync(board);
        }

        /// <summary>The raid started over: everyone's panel redraws it.</summary>
        internal static async Task TellRestartedAsync(GuildRaidBoard.Raid board, RaidInstance raid)
            => await TellRunningWatchersAsync(board.Uuid, ConnectionProtocol.Push(Op.Ibv,
                GuildRaidProtocol.BuildRunningRestarted(board, raid, GuildRaidManager.VariablesOf(raid))));

        /// <summary>
        /// The raid is over: its participants who had not left get the final score (hyg), which
        /// closes their panel and opens the score screen. Who only looked at it stops looking.
        /// </summary>
        internal static async Task TellFinishedAsync(GuildRaidBoard.Raid board, RaidInstance raid, long seconds,
                                                     IReadOnlyList<int> goals, bool newHighScore)
        {
            var now = DateTimeOffset.UtcNow;
            foreach (var participant in board.Participants.ToList())
            {
                long id = participant.CharacterId;
                if (raid.HasLeft(id)) continue;

                // His week's frieze moves on whether he is connected or not.
                var unlocked = GuildRaidRewards.RecordRun(id, raid.RaidId, raid.Score, now);
                var session = SessionRegistry.FindByCharacter(id);
                if (session == null) continue;
                await session.SendAsync(ConnectionProtocol.Push(Op.Hyg,
                    GuildRaidProtocol.BuildRunningFinished(seconds, raid.Score, goals, newHighScore, unlocked)));
                await session.SendAsync(ConnectionProtocol.Push(Op.Hxm, PlayerRaidsOf(id, now)));
                await TellPendingAsync(session, id, now);
            }
            foreach (var (id, listened) in _listening.ToList())
                if (listened == board.Uuid) _listening.TryRemove(id, out _);
        }

        /// <summary>
        /// Looking at a running raid from the guild's tab (iaz → icj): f1 its uuid. Its guild's
        /// members and its participants may; from then on they hear of it until they stop.
        /// </summary>
        public static async Task ShowRunningAsync(NetworkStream stream, byte[] frame)
        {
            long who = GameState.CharacterId;
            string uuid = Texts(Fields(frame, Op.Iaz)).FirstOrDefault() ?? "";
            var board = GuildRaidBoard.Find(uuid);
            var raid = GuildRaidManager.RunningByUuid(uuid);
            bool may = board != null && (board.Has(who) || GuildStore.GuildOf(who)?.Id == board.GuildId);
            if (!may || raid == null) { board = null; raid = null; }
            else _listening[who] = uuid;
            await WriteAsync(stream, ConnectionProtocol.Answer(Op.Icj,
                GuildRaidProtocol.BuildShowRunningRaid(board, raid, raid == null ? null : GuildRaidManager.VariablesOf(raid)),
                ConnectionProtocol.RequestId(frame)));
        }

        /// <summary>Stops looking at a running raid (hvw). The client expects no answer.</summary>
        public static Task StopListeningAsync(NetworkStream stream, byte[] frame)
        {
            _listening.TryRemove(GameState.CharacterId, out _);
            return Task.CompletedTask;
        }

        /// <summary>The captain ends the running raid (icm → hzn).</summary>
        public static async Task FinishAsync(NetworkStream stream, byte[] frame)
        {
            var result = await GuildRaidManager.CloseAsync(GameState.CharacterId);
            await WriteAsync(stream, ConnectionProtocol.Answer(Op.Hzn, GuildRaidProtocol.BuildFinishResult(result),
                                                               ConnectionProtocol.RequestId(frame)));
        }

        /// <summary>The captain starts the running raid over (ibz → hzv).</summary>
        public static async Task RestartAsync(NetworkStream stream, byte[] frame)
        {
            var result = await GuildRaidManager.RestartAsync(GameState.CharacterId);
            await WriteAsync(stream, ConnectionProtocol.Answer(Op.Hzv, GuildRaidProtocol.BuildRestartResult(result),
                                                               ConnectionProtocol.RequestId(frame)));
        }

        /// <summary>A participant leaves the running raid (ibf → ide); its f1, the reason, changes nothing here.</summary>
        public static async Task RunningLeaveAsync(NetworkStream stream, byte[] frame)
        {
            int result = await GuildRaidManager.LeaveAsync(GameState.CharacterId);
            await WriteAsync(stream, ConnectionProtocol.Answer(Op.Ide, GuildRaidProtocol.BuildRunningLeaveResult(result),
                                                               ConnectionProtocol.RequestId(frame)));
        }

        /// <summary>The captain hands the running raid on (iaj → hyw): f1 the new captain's key.</summary>
        public static async Task RunningCaptainAsync(NetworkStream stream, byte[] frame)
        {
            long target = CharacterIdOf(Texts(Fields(frame, Op.Iaj)).FirstOrDefault() ?? "");
            var result = await GuildRaidManager.HandCaptaincyAsync(GameState.CharacterId, target);
            await WriteAsync(stream, ConnectionProtocol.Answer(Op.Hyw, GuildRaidProtocol.BuildRunningCaptainResult(result),
                                                               ConnectionProtocol.RequestId(frame)));
        }

        // ─── The player's raids and the frieze ──────────────────────────────────

        /// <summary>A character's raids, frieze and claims, as hxm carries them.</summary>
        private static byte[] PlayerRaidsOf(long characterId, DateTimeOffset now)
            => GuildRaidProtocol.BuildPlayerRaids(GuildRaidBoard.FinishedOf(characterId, now),
                                                  GuildRaidRewards.BestScores(characterId, now),
                                                  GuildRaidBoard.OfParticipant(characterId),
                                                  GuildRaidRewards.Claimed(characterId, now));

        /// <summary>The frieze steps waiting for him, if any (iac): the client reminds him to claim.</summary>
        private static async Task TellPendingAsync(GameSession session, long characterId, DateTimeOffset now)
        {
            var pending = GuildRaidRewards.Pending(characterId, now);
            if (pending.Count == 0) return;
            var raids = GuildRaidCatalogue.Raids.Where(r => GuildRaidCatalogue.RewardsOf(r.Id).Any(s => pending.Contains(s.Id)))
                                                .Select(r => r.Id);
            await session.SendAsync(ConnectionProtocol.Push(Op.Iac, GuildRaidProtocol.BuildPendingRewards(raids, pending)));
        }

        /// <summary>
        /// A player's raids (PlayerRaidShowAndListenRequest hvx → hxm): sent at world entry and when
        /// the raid screens open. The rewards screen and the Raids tab read the answer.
        /// </summary>
        public static async Task PlayerRaidsAsync(NetworkStream stream, byte[] frame)
            => await WriteAsync(stream, ConnectionProtocol.Answer(Op.Hxm, PlayerRaidsOf(GameState.CharacterId, DateTimeOffset.UtcNow),
                                                                  ConnectionProtocol.RequestId(frame)));

        /// <summary>Stops listening to his raids (icn). The client expects no answer.</summary>
        public static Task StopListeningPlayerAsync(NetworkStream stream, byte[] frame) => Task.CompletedTask;

        /// <summary>
        /// Claims a raid's frieze steps (RaidClaimRewardsRequest ibw → hyj): f1 the raid. Paid, the
        /// rewards screen gets his raids again.
        /// </summary>
        public static async Task ClaimRewardsAsync(NetworkStream stream, byte[] frame)
        {
            long who = GameState.CharacterId;
            var now = DateTimeOffset.UtcNow;
            int raidId = (int)Number(Fields(frame, Op.Ibw), 1);
            var (result, rewards) = await GuildRaidRewards.ClaimAsync(stream, who, raidId, now);
            await WriteAsync(stream, ConnectionProtocol.Answer(Op.Hyj, GuildRaidProtocol.BuildClaimResult(result, rewards),
                                                               ConnectionProtocol.RequestId(frame)));
            if (result == GuildRaidRewards.ClaimResult.Done)
                await WriteAsync(stream, ConnectionProtocol.Push(Op.Hxm, PlayerRaidsOf(who, now)));
        }

        // ─── Invitations ─────────────────────────────────────────────────────────

        /// <summary>
        /// Inviting a player into one's raid (RaidInvitationRequest hwg → ibc): f1 his character's
        /// id. Invited, he gets the popup (ibo).
        /// </summary>
        public static async Task InviteAsync(NetworkStream stream, byte[] frame)
        {
            long who = GameState.CharacterId;
            long target = Number(Fields(frame, Op.Hwg), 1);
            var (result, raid) = GuildRaidBoard.Invite(who, target, DateTimeOffset.UtcNow);
            await WriteAsync(stream, ConnectionProtocol.Answer(Op.Ibc, GuildRaidProtocol.BuildResult((int)result),
                                                               ConnectionProtocol.RequestId(frame)));
            if (result != GuildRaidBoard.InviteResult.Done) return;
            var session = SessionRegistry.FindByCharacter(target);
            if (session != null)
                await session.SendAsync(ConnectionProtocol.Push(Op.Ibo, GuildRaidProtocol.BuildInvitation(raid, who)));
            Program.LogDebug($"[Raids] {who} invites {target} into {raid.Uuid}.");
        }

        /// <summary>
        /// The invited player answers (RaidInvitationAnswerRequest iag → hzb): f1 accept. In, the raid
        /// is his to see, and everyone who sees it hears of him.
        /// </summary>
        public static async Task InvitationAnswerAsync(NetworkStream stream, byte[] frame)
        {
            long who = GameState.CharacterId;
            bool accept = Number(Fields(frame, Op.Iag), 1) != 0;
            var (result, raid) = GuildRaidBoard.AnswerInvitation(who, accept, DateTimeOffset.UtcNow);
            await WriteAsync(stream, ConnectionProtocol.Answer(Op.Hzb,
                GuildRaidProtocol.BuildInvitationAnswerResult(result, accept ? raid : null), ConnectionProtocol.RequestId(frame)));
            if (accept && result == GuildRaidBoard.InvitationAnswerResult.Done) await TellUpdatedAsync(raid);
        }

        // ─── The weekly ladder ───────────────────────────────────────────────────

        /// <summary>
        /// The raids tab of the ladder window (ShowAndListenRaidLadderRequest hzq → hwu): every raid's
        /// ladder this week and last, with the player's guild's line.
        /// </summary>
        public static async Task LadderAsync(NetworkStream stream, byte[] frame)
            => await WriteAsync(stream, ConnectionProtocol.Answer(Op.Hwu,
                GuildRaidProtocol.BuildRaidLadder(GuildStore.GuildOf(GameState.CharacterId)?.Id ?? 0, DateTimeOffset.UtcNow),
                ConnectionProtocol.RequestId(frame)));

        /// <summary>Stops listening to the ladder (ibl). The client expects no answer.</summary>
        public static Task StopLadderAsync(NetworkStream stream, byte[] frame) => Task.CompletedTask;

        // ─── Coming and going ───────────────────────────────────────────────────

        /// <summary>
        /// A character came into the world: he is reminded of frieze steps waiting for him, his
        /// raid's tab shows him connected, and if his raid is running he gets it back, with its
        /// panel, and the others see him there again.
        /// </summary>
        public static async Task OnEnterWorldAsync(long characterId)
        {
            var me = SessionRegistry.FindByCharacter(characterId);
            if (me != null)
            {
                var now = DateTimeOffset.UtcNow;
                await TellPendingAsync(me, characterId, now);
                var paid = await GuildRaidLadder.DeliverAsync(me.Stream, characterId, now);
                if (paid.Count > 0)
                    await me.SendAsync(ConnectionProtocol.Push(Op.Ibs, GuildRaidProtocol.BuildLadderRewardsObtained(paid)));
            }
            var raid = GuildRaidBoard.OfParticipant(characterId);
            if (raid == null) return;
            await TellConnectionAsync(raid, characterId, true);
            var running = GuildRaidManager.RunningByUuid(raid.Uuid);
            if (running == null || running.HasLeft(characterId)) return;
            await TellRunningRaidAsync(raid, running, new[] { characterId });
            await TellParticipantStateAsync(running, characterId);
        }

        /// <summary>A character left the world: his raid sees him disconnected.</summary>
        public static async Task OnLeaveWorldAsync(long characterId)
        {
            _listening.TryRemove(characterId, out _);
            var raid = GuildRaidBoard.OfParticipant(characterId);
            if (raid == null) return;
            await TellConnectionAsync(raid, characterId, false);
            var running = GuildRaidManager.RunningByUuid(raid.Uuid);
            if (running != null && !running.HasLeft(characterId)) await TellParticipantStateAsync(running, characterId);
        }

        /// <summary>A participant's connection, to whoever sees his raid in the tab (iav).</summary>
        private static async Task TellConnectionAsync(GuildRaidBoard.Raid raid, long characterId, bool online)
        {
            byte[] frame = ConnectionProtocol.Push(Op.Iav, GuildRaidProtocol.BuildParticipantStatus(raid.Uuid, characterId, online));
            foreach (var session in Watchers(raid))
                if (session.CharacterId != characterId) await session.SendAsync(frame);
        }

        /// <summary>When the deadline comes and the raid is still starting, it is called off.</summary>
        private static void WatchDeadline(GuildRaidBoard.Raid raid, DateTimeOffset deadline)
        {
            _ = Task.Run(async () =>
            {
                try
                {
                    var wait = deadline - DateTimeOffset.UtcNow;
                    if (wait > TimeSpan.Zero) await Task.Delay(wait);
                    var missing = GuildRaidBoard.ExpireStart(raid, deadline);
                    if (missing == null) return;
                    await TellParticipantsAsync(raid, ConnectionProtocol.Push(Op.Icz, GuildRaidProtocol.BuildStartTimedOut(missing)));
                    await TellUpdatedAsync(raid);
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"[Raids] The start of {raid.Uuid} could not be called off: {ex.Message}");
                }
            });
        }

        /// <summary>A frame to every participant of the raid connected.</summary>
        private static async Task TellParticipantsAsync(GuildRaidBoard.Raid raid, byte[] frame)
        {
            foreach (var participant in raid.Participants.ToList())
            {
                var session = SessionRegistry.FindByCharacter(participant.CharacterId);
                if (session != null) await session.SendAsync(frame);
            }
        }

        /// <summary>A participant moves to another group of his raid (hyt): f1 the group.</summary>
        public static async Task MoveGroupAsync(NetworkStream stream, byte[] frame)
        {
            int group = (int)Number(Fields(frame, Op.Hyt), 1);
            if (GuildRaidBoard.MoveGroup(GameState.CharacterId, group))
                await TellUpdatedAsync(GuildRaidBoard.OfParticipant(GameState.CharacterId));
        }
    }
}
