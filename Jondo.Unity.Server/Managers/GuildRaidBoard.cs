using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using Jondo.Unity.Server.Network;
using Microsoft.Data.Sqlite;

namespace Jondo.Unity.Server.Managers
{
    /// <summary>
    /// The guild raids bought and not over: what the guild window's Raids tab lists, who is in each,
    /// who leads it and what its note says. Every rule here answers with the result codes the
    /// client's own handlers translate (read from the client; no capture has a raid).
    /// </summary>
    /// <remarks>
    /// A raid is bought from the guild shop with the right "Administrar las raids" and guild kamas.
    /// Whoever buys it is its captain; any guild member may join, outsiders by invitation. The
    /// captain starts it once enough are in, everyone accepts, and it runs. Each raid is known by a
    /// uuid, as the client keys them.
    /// </remarks>
    public static class GuildRaidBoard
    {
        public enum State { Constitution = 0, Starting = 1, Running = 2, Finished = 3 }

        public sealed class Note
        {
            public string Text { get; set; } = "";
            public long AuthorId { get; set; }
            public string AuthorName { get; set; } = "";
            public DateTimeOffset When { get; set; }
        }

        public sealed class Participant
        {
            public long CharacterId { get; set; }
            public int Group { get; set; }
            public bool IsCaptain { get; set; }
        }

        public sealed class Raid
        {
            public string Uuid { get; init; } = "";
            public long GuildId { get; init; }
            public int RaidId { get; init; }
            public long BuyerId { get; init; }
            public DateTimeOffset BoughtUtc { get; init; }
            public State State { get; set; }
            public Note Note { get; set; } = new();
            public List<Participant> Participants { get; } = new();
            public HashSet<long> Blocked { get; } = new();

            /// <summary>Who has accepted the start, while it is starting.</summary>
            public HashSet<long> Accepted { get; } = new();

            public DateTimeOffset StartDeadline { get; set; }
            public long Score { get; set; }
            public DateTimeOffset FinishedUtc { get; set; }

            /// <summary>Once over: how long it ran, in seconds, and the goals it met.</summary>
            public long DurationSeconds { get; set; }
            public List<int> ValidatedGoals { get; } = new();

            public Participant Captain => Participants.FirstOrDefault(p => p.IsCaptain);
            public Participant ParticipantOf(long characterId) => Participants.FirstOrDefault(p => p.CharacterId == characterId);
            public bool Has(long characterId) => ParticipantOf(characterId) != null;
            public bool Active => State != State.Finished;
        }

        // ─── Result codes, field 1 of each response; 0 is success ──────────────────

        public enum PurchaseResult
        {
            Done = 0, UnknownRaid = 1, MissingRight = 2, NotEnoughMoney = 3, CaptainHasActiveRaid = 4,
            InvalidGroup = 5, GuildRequired = 6, UnknownError = 7, RaidDeactivated = 8,
        }

        public enum JoinResult
        {
            Done = 0, GuildRequired = 1, NoRaidInConstitution = 2, InvalidGroup = 3, RaidOrGroupFull = 4,
            IsInBlockList = 5, AlreadyInRaid = 6, IsInAnotherRaid = 7, UnknownError = 8, RaidIsStarting = 9,
            SubscriptionRequired = 10,
        }

        public enum LeaveResult { Done = 0, RequireRaid = 1, ImpossibleWhileCaptain = 2 }

        public enum CaptainResult
        {
            Done = 0, GuildRequired = 1, RaidNotFound = 2, NotCaptainAndMissingRight = 3, ExternCannotBeCaptain = 4,
            TargetNotInRaid = 5, TargetIsAlreadyCaptain = 6, RaidIsStarting = 7,
        }

        public enum RemoveResult
        {
            Done = 0, RequireRaid = 1, RaidInProgress = 2, NotCaptain = 3, TargetNotInRaid = 4, RaidIsStarting = 5,
            UnknownError = 6,
        }

        private static readonly ConcurrentDictionary<string, Raid> _raids = new();
        private static readonly object _lock = new();
        private static bool _loaded;

        private static void EnsureLoaded()
        {
            if (_loaded) return;
            lock (_lock)
            {
                if (_loaded) return;
                foreach (var raid in GuildRaidStore.LoadAll()) _raids[raid.Uuid] = raid;
                _loaded = true;
            }
        }

        /// <summary>Forgets what is in memory, so the next use reloads it: for the tests.</summary>
        internal static void Forget()
        {
            lock (_lock)
            {
                _raids.Clear();
                _invitations.Clear();
                _loaded = false;
            }
        }

        public static Raid Find(string uuid)
        {
            EnsureLoaded();
            return uuid != null && _raids.TryGetValue(uuid, out var raid) ? raid : null;
        }

        /// <summary>The raids a guild's members see in their Raids tab: the guild's own, not over.</summary>
        public static List<Raid> OfGuild(long guildId)
        {
            EnsureLoaded();
            return _raids.Values.Where(r => r.GuildId == guildId && r.Active).OrderBy(r => r.BoughtUtc).ToList();
        }

        /// <summary>The raid, not over, a character takes part in -- of his guild or of another.</summary>
        public static Raid OfParticipant(long characterId)
        {
            EnsureLoaded();
            return _raids.Values.FirstOrDefault(r => r.Active && r.Has(characterId));
        }

        /// <summary>The raids a character took part in that finished this week, newest first.</summary>
        public static List<Raid> FinishedOf(long characterId, DateTimeOffset now)
        {
            EnsureLoaded();
            string week = GuildRaidRewards.WeekOf(now);
            return _raids.Values.Where(r => r.State == State.Finished && r.Has(characterId) &&
                                            GuildRaidRewards.WeekOf(r.FinishedUtc) == week)
                         .OrderByDescending(r => r.FinishedUtc).ToList();
        }

        /// <summary>What a character sees: his guild's raids, and the one he is in if it is another guild's.</summary>
        public static List<Raid> VisibleTo(long characterId)
        {
            var guild = GuildStore.GuildOf(characterId);
            var raids = guild != null ? OfGuild(guild.Id) : new List<Raid>();
            var own = OfParticipant(characterId);
            if (own != null && !raids.Contains(own)) raids.Add(own);
            return raids;
        }

        // ─── Buying ─────────────────────────────────────────────────────────────

        /// <summary>
        /// Buys a raid from the guild shop. The buyer needs the right "Administrar las raids", and
        /// may not already be in a raid not over -- he becomes this one's captain.
        /// </summary>
        /// <param name="group">The group the buyer goes into, or 0 for the raid's first.</param>
        public static (PurchaseResult Result, Raid Raid) Purchase(long characterId, int raidId, int group = 0)
        {
            EnsureLoaded();
            var kind = GuildRaidCatalogue.Of(raidId);
            if (kind == null) return (PurchaseResult.UnknownRaid, null);
            var guild = GuildStore.GuildOf(characterId);
            if (guild == null) return (PurchaseResult.GuildRequired, null);
            if (!GuildStore.HasRight(characterId, GuildStore.ManageRaidsRight)) return (PurchaseResult.MissingRight, null);
            if (OfParticipant(characterId) != null) return (PurchaseResult.CaptainHasActiveRaid, null);
            if (group == 0) group = kind.Groups.FirstOrDefault();
            if (!kind.Groups.Contains(group)) return (PurchaseResult.InvalidGroup, null);
            if (!GuildStore.SpendGuildKamas(guild.Id, kind.Price)) return (PurchaseResult.NotEnoughMoney, null);

            var now = DateTimeOffset.UtcNow;
            var raid = new Raid
            {
                Uuid = Guid.NewGuid().ToString(),
                GuildId = guild.Id,
                RaidId = raidId,
                BuyerId = characterId,
                BoughtUtc = now,
                State = State.Constitution,
                Note = new Note { AuthorId = characterId, AuthorName = NameOf(characterId), When = now },
            };
            raid.Participants.Add(new Participant { CharacterId = characterId, Group = group, IsCaptain = true });
            _raids[raid.Uuid] = raid;
            GuildRaidStore.Save(raid);
            return (PurchaseResult.Done, raid);
        }

        // ─── Joining and leaving ────────────────────────────────────────────────

        /// <summary>
        /// Joins a raid in constitution. A guild member may; an outsider only through an invitation,
        /// which goes through <see cref="AnswerInvitation"/>.
        /// </summary>
        public static JoinResult Join(long characterId, string uuid, int group = 0)
        {
            var raid = Find(uuid);
            if (raid == null || !raid.Active) return JoinResult.NoRaidInConstitution;
            if (raid.State == State.Starting) return JoinResult.RaidIsStarting;
            if (raid.State != State.Constitution) return JoinResult.NoRaidInConstitution;
            var guild = GuildStore.GuildOf(characterId);
            if (guild == null || guild.Id != raid.GuildId) return JoinResult.GuildRequired;
            return Add(raid, characterId, group);
        }

        /// <summary>Puts someone in a raid, once whatever let him in has been checked.</summary>
        internal static JoinResult Add(Raid raid, long characterId, int group)
        {
            var kind = GuildRaidCatalogue.Of(raid.RaidId);
            if (kind == null) return JoinResult.UnknownError;
            if (raid.Blocked.Contains(characterId)) return JoinResult.IsInBlockList;
            if (raid.Has(characterId)) return JoinResult.AlreadyInRaid;
            if (OfParticipant(characterId) != null) return JoinResult.IsInAnotherRaid;
            if (group == 0) group = kind.Groups.FirstOrDefault();
            if (!kind.Groups.Contains(group)) return JoinResult.InvalidGroup;
            int groupMax = GuildRaidCatalogue.GroupOf(group)?.MaxPlayers ?? kind.MaxPlayers;
            if (raid.Participants.Count >= kind.MaxPlayers ||
                raid.Participants.Count(p => p.Group == group) >= groupMax) return JoinResult.RaidOrGroupFull;

            raid.Participants.Add(new Participant { CharacterId = characterId, Group = group });
            GuildRaidStore.Save(raid);
            return JoinResult.Done;
        }

        /// <summary>
        /// Leaves the raid in constitution one is in. The captain may not: he names another captain
        /// first, as the game's guides tell the buyer who does not mean to go (the client has the
        /// error for it, "impossibleWhileCaptain").
        /// </summary>
        public static (LeaveResult Result, Raid Raid) Leave(long characterId)
        {
            var raid = OfParticipant(characterId);
            if (raid == null || raid.State != State.Constitution) return (LeaveResult.RequireRaid, null);
            var me = raid.ParticipantOf(characterId);
            if (me.IsCaptain) return (LeaveResult.ImpossibleWhileCaptain, raid);
            raid.Participants.Remove(me);
            GuildRaidStore.Save(raid);
            return (LeaveResult.Done, raid);
        }

        // ─── Invitations ─────────────────────────────────────────────────────────

        /// <summary>The result of inviting someone (RaidInvitationResponse f1), as the client translates it.</summary>
        public enum InviteResult
        {
            Done = 0, RequireRaid = 1, RaidFull = 2, TargetNotFound = 3, TargetIsOccupied = 4, TargetHasRaid = 5,
            InvitationImpossibleForExtern = 6, NoRaidInConstitution = 7, TargetIsInYourRaid = 8, TargetIsBlocked = 9,
            TargetIsDisconnected = 10, TargetSubscriptionRequired = 11,
        }

        /// <summary>The result of answering one (RaidInvitationAnswerResponse f1).</summary>
        public enum InvitationAnswerResult
        {
            Done = 0, NoPendingInvitation = 1, RaidFull = 2, AlreadyInRaid = 3, NoRaidInConstitution = 4,
            IsInBlockList = 5, UnknownError = 6, RaidIsStarting = 7, SubscriptionRequired = 8,
        }

        /// <summary>
        /// How long an invitation waits for its answer. The client's popup has no countdown, so it
        /// is not measured; five minutes.
        /// </summary>
        public static readonly TimeSpan InvitationWindow = TimeSpan.FromMinutes(5);

        private sealed record Invitation(string Uuid, long InviterId, DateTimeOffset Expires);

        /// <summary>The invitation each player has pending, by the invited player.</summary>
        private static readonly ConcurrentDictionary<long, Invitation> _invitations = new();

        /// <summary>
        /// Invites a player into one's raid in constitution: the way an outsider -- a player of another
        /// guild, or of none -- comes into a guild's raid. Only the raid's own guild invites.
        /// </summary>
        public static (InviteResult Result, Raid Raid) Invite(long inviterId, long targetId, DateTimeOffset now)
        {
            var raid = OfParticipant(inviterId);
            if (raid == null) return (InviteResult.RequireRaid, null);
            if (raid.State != State.Constitution) return (InviteResult.NoRaidInConstitution, raid);
            if (GuildStore.GuildOf(inviterId)?.Id != raid.GuildId) return (InviteResult.InvitationImpossibleForExtern, raid);
            if (targetId == inviterId || raid.Has(targetId)) return (InviteResult.TargetIsInYourRaid, raid);
            var session = SessionRegistry.FindByCharacter(targetId);
            if (session == null)
                return (DatabaseManager.GetCharacterById(targetId) == null ? InviteResult.TargetNotFound : InviteResult.TargetIsDisconnected, raid);
            if (session.State.IsInFight) return (InviteResult.TargetIsOccupied, raid);
            if (OfParticipant(targetId) != null) return (InviteResult.TargetHasRaid, raid);
            if (raid.Blocked.Contains(targetId)) return (InviteResult.TargetIsBlocked, raid);
            var kind = GuildRaidCatalogue.Of(raid.RaidId);
            if (kind == null || raid.Participants.Count >= kind.MaxPlayers) return (InviteResult.RaidFull, raid);

            _invitations[targetId] = new Invitation(raid.Uuid, inviterId, now + InvitationWindow);
            return (InviteResult.Done, raid);
        }

        /// <summary>
        /// The invited player answers. Accepting puts him in the raid's first group, whatever his
        /// guild; refusing only drops the invitation.
        /// </summary>
        public static (InvitationAnswerResult Result, Raid Raid) AnswerInvitation(long characterId, bool accept, DateTimeOffset now)
        {
            if (!_invitations.TryRemove(characterId, out var invitation) || invitation.Expires < now)
                return (InvitationAnswerResult.NoPendingInvitation, null);
            var raid = Find(invitation.Uuid);
            if (!accept) return (InvitationAnswerResult.Done, null);
            if (raid == null || !raid.Active) return (InvitationAnswerResult.NoRaidInConstitution, null);
            if (raid.State == State.Starting) return (InvitationAnswerResult.RaidIsStarting, raid);
            if (raid.State != State.Constitution) return (InvitationAnswerResult.NoRaidInConstitution, raid);
            return Add(raid, characterId, 0) switch
            {
                JoinResult.Done => (InvitationAnswerResult.Done, raid),
                JoinResult.RaidOrGroupFull => (InvitationAnswerResult.RaidFull, raid),
                JoinResult.IsInBlockList => (InvitationAnswerResult.IsInBlockList, raid),
                JoinResult.AlreadyInRaid or JoinResult.IsInAnotherRaid => (InvitationAnswerResult.AlreadyInRaid, raid),
                _ => (InvitationAnswerResult.UnknownError, raid),
            };
        }

        // ─── The captain, the note and the participants ────────────────────────

        /// <summary>
        /// Hands the captaincy to another participant. The captain may, and so may anyone of the
        /// raid's guild with the right "Administrar las raids". An outsider cannot be captain.
        /// </summary>
        public static CaptainResult UpdateCaptain(long characterId, string uuid, long targetId)
        {
            var raid = Find(uuid);
            if (raid == null || !raid.Active) return CaptainResult.RaidNotFound;
            var guild = GuildStore.GuildOf(characterId);
            bool isCaptain = raid.Captain?.CharacterId == characterId;
            bool hasRight = guild != null && guild.Id == raid.GuildId &&
                            GuildStore.HasRight(characterId, GuildStore.ManageRaidsRight);
            if (!isCaptain && guild == null) return CaptainResult.GuildRequired;
            if (!isCaptain && !hasRight) return CaptainResult.NotCaptainAndMissingRight;
            if (raid.State == State.Starting) return CaptainResult.RaidIsStarting;
            var target = raid.ParticipantOf(targetId);
            if (target == null) return CaptainResult.TargetNotInRaid;
            if (target.IsCaptain) return CaptainResult.TargetIsAlreadyCaptain;
            if (GuildStore.GuildOf(targetId)?.Id != raid.GuildId) return CaptainResult.ExternCannotBeCaptain;

            foreach (var p in raid.Participants) p.IsCaptain = p.CharacterId == targetId;
            GuildRaidStore.Save(raid);
            return CaptainResult.Done;
        }

        /// <summary>
        /// Writes the raid's note, which its tab shows under its name. The captain may, and so may
        /// whoever of its guild has the right. Returns whether it was written.
        /// </summary>
        public static bool UpdateNote(long characterId, string uuid, string text)
        {
            var raid = Find(uuid);
            if (raid == null || !raid.Active) return false;
            bool isCaptain = raid.Captain?.CharacterId == characterId;
            bool hasRight = GuildStore.GuildOf(characterId)?.Id == raid.GuildId &&
                            GuildStore.HasRight(characterId, GuildStore.ManageRaidsRight);
            if (!isCaptain && !hasRight) return false;
            raid.Note = new Note
            {
                Text = text ?? "",
                AuthorId = characterId,
                AuthorName = NameOf(characterId),
                When = DateTimeOffset.UtcNow,
            };
            GuildRaidStore.Save(raid);
            return true;
        }

        /// <summary>
        /// The captain takes someone out of his raid in constitution, and may bar him from coming
        /// back (the block list, which the raid's sheet keeps).
        /// </summary>
        public static RemoveResult RemoveParticipant(long characterId, string uuid, long targetId, bool block)
        {
            var raid = Find(uuid);
            if (raid == null || !raid.Active) return RemoveResult.RequireRaid;
            if (raid.State == State.Starting) return RemoveResult.RaidIsStarting;
            if (raid.State != State.Constitution) return RemoveResult.RaidInProgress;
            if (raid.Captain?.CharacterId != characterId) return RemoveResult.NotCaptain;
            var target = raid.ParticipantOf(targetId);
            if (target == null || target.IsCaptain) return RemoveResult.TargetNotInRaid;
            raid.Participants.Remove(target);
            if (block) raid.Blocked.Add(targetId);
            GuildRaidStore.Save(raid);
            return RemoveResult.Done;
        }

        /// <summary>Lets someone the captain had barred come back.</summary>
        public static bool Unblock(long characterId, string uuid, long targetId)
        {
            var raid = Find(uuid);
            if (raid == null || raid.Captain?.CharacterId != characterId) return false;
            if (!raid.Blocked.Remove(targetId)) return false;
            GuildRaidStore.Save(raid);
            return true;
        }

        /// <summary>A participant changes group, if there is room in it.</summary>
        public static bool MoveGroup(long characterId, int group)
        {
            var raid = OfParticipant(characterId);
            var kind = raid != null ? GuildRaidCatalogue.Of(raid.RaidId) : null;
            if (kind == null || raid.State != State.Constitution || !kind.Groups.Contains(group)) return false;
            int groupMax = GuildRaidCatalogue.GroupOf(group)?.MaxPlayers ?? kind.MaxPlayers;
            if (raid.Participants.Count(p => p.Group == group) >= groupMax) return false;
            raid.ParticipantOf(characterId).Group = group;
            GuildRaidStore.Save(raid);
            return true;
        }

        // ─── Starting ───────────────────────────────────────────────────────────

        /// <summary>The error case of RaidStartResponse (its field 4), as the client's handler reads it.</summary>
        public enum StartError
        {
            Done = 0, NoRaidInConstitution = 1, MissingCaptain = 2, MalformedRaid = 3, NotCaptain = 4,
            RaidDeactivated = 5, NotMember = 6, SubscriptionsMissing = 7,
        }

        /// <summary>
        /// How long the participants have to accept the start: the client counts it down on its
        /// popup from the deadline the server sends. Not measured; a minute.
        /// </summary>
        public static readonly TimeSpan StartWindow = TimeSpan.FromMinutes(1);

        /// <summary>
        /// The fewest players a raid starts with: the client's data minimum (eight in both raids),
        /// unless the server's settings set another -- never above the raid's maximum --, or
        /// JONDO_RAID_MIN_PLAYERS lowers it for a test.
        /// </summary>
        public static int MinPlayersOf(GuildRaidCatalogue.Raid kind)
        {
            string set = Environment.GetEnvironmentVariable("JONDO_RAID_MIN_PLAYERS");
            if (int.TryParse(set, out int min) && min > 0) return Math.Min(min, kind.MinPlayers);
            int chosen = ServerSettings.Current.RaidMinPlayers;
            return chosen > 0 ? Math.Clamp(chosen, 1, Math.Max(1, kind.MaxPlayers)) : kind.MinPlayers;
        }

        /// <summary>
        /// The captain starts his raid. It needs its minimum of players, all of them connected;
        /// those who are not are returned, for the client's "disconnected participants". Then the
        /// raid is starting: the captain has accepted, and everyone else is asked.
        /// </summary>
        public static (StartError Error, Raid Raid, List<long> Disconnected) Start(long characterId, DateTimeOffset now)
        {
            var raid = OfParticipant(characterId);
            if (raid == null || raid.State != State.Constitution) return (StartError.NoRaidInConstitution, raid, null);
            if (raid.Captain == null) return (StartError.MissingCaptain, raid, null);
            if (raid.Captain.CharacterId != characterId) return (StartError.NotCaptain, raid, null);
            var kind = GuildRaidCatalogue.Of(raid.RaidId);
            if (kind == null) return (StartError.RaidDeactivated, raid, null);
            if (raid.Participants.Count < MinPlayersOf(kind)) return (StartError.MalformedRaid, raid, null);

            var offline = raid.Participants.Select(p => p.CharacterId)
                              .Where(id => SessionRegistry.FindByCharacter(id) == null).ToList();
            if (offline.Count > 0) return (StartError.Done, raid, offline);

            raid.State = State.Starting;
            raid.StartDeadline = now + StartWindow;
            raid.Accepted.Clear();
            raid.Accepted.Add(characterId);
            GuildRaidStore.Save(raid);
            return (StartError.Done, raid, null);
        }

        /// <summary>
        /// Whether the server's minimum is below the client's: then the client never lights the start
        /// button -- it compares the participants with its own data's minimum
        /// (GuildRaids.UpdateRaidInConstitution: btn_start enabled when participants >= minPlayers) --
        /// and the server starts the raid itself (<see cref="AutoStart"/>).
        /// </summary>
        public static bool StartsByItself(GuildRaidCatalogue.Raid kind)
            => kind != null && MinPlayersOf(kind) < kind.MinPlayers;

        /// <summary>
        /// The start the server makes when the settings lower the minimum below the client's (his
        /// choice for testing alone, 2026-10-10): once the raid has the server's minimum, all of them
        /// connected, it is starting, and NOBODY has accepted yet -- the captain is asked like the
        /// rest, so nobody is taken in without saying yes. False when it does not apply.
        /// </summary>
        public static bool AutoStart(Raid raid, DateTimeOffset now)
        {
            if (raid == null) return false;
            lock (raid)
            {
                var kind = GuildRaidCatalogue.Of(raid.RaidId);
                if (!StartsByItself(kind) || raid.State != State.Constitution || raid.Captain == null) return false;
                if (raid.Participants.Count < MinPlayersOf(kind)) return false;
                if (raid.Participants.Any(p => SessionRegistry.FindByCharacter(p.CharacterId) == null)) return false;

                raid.State = State.Starting;
                raid.StartDeadline = now + StartWindow;
                raid.Accepted.Clear();
                GuildRaidStore.Save(raid);
                return true;
            }
        }

        /// <summary>What a participant's answer to the start left the raid at.</summary>
        public enum StartOutcome { NotStarting, Waiting, Everyone, Refused }

        /// <summary>
        /// A participant accepts or refuses the start of his raid. The last one to accept makes it
        /// run there and then, so that two answers crossing cannot launch it twice.
        /// </summary>
        public static (StartOutcome Outcome, Raid Raid) Answer(long characterId, bool accept)
        {
            var raid = OfParticipant(characterId);
            if (raid == null) return (StartOutcome.NotStarting, null);
            lock (raid)
            {
                if (raid.State != State.Starting) return (StartOutcome.NotStarting, raid);
                if (!accept)
                {
                    CancelStart(raid);
                    return (StartOutcome.Refused, raid);
                }
                raid.Accepted.Add(characterId);
                if (!raid.Participants.All(p => raid.Accepted.Contains(p.CharacterId))) return (StartOutcome.Waiting, raid);
                MarkRunning(raid);
                return (StartOutcome.Everyone, raid);
            }
        }

        /// <summary>Who has not accepted yet.</summary>
        public static List<long> NotAccepted(Raid raid)
            => raid.Participants.Select(p => p.CharacterId).Where(id => !raid.Accepted.Contains(id)).ToList();

        /// <summary>
        /// The start ran out of time: the raid goes back to constitution, and who had not accepted is
        /// returned. Null when the start had already gone one way or the other.
        /// </summary>
        public static List<long> ExpireStart(Raid raid, DateTimeOffset deadline)
        {
            lock (raid)
            {
                if (raid.State != State.Starting || raid.StartDeadline != deadline) return null;
                var missing = NotAccepted(raid);
                CancelStart(raid);
                return missing;
            }
        }

        /// <summary>The start is off: the raid is back in constitution, as it was.</summary>
        public static void CancelStart(Raid raid)
        {
            if (raid == null || raid.State != State.Starting) return;
            raid.State = State.Constitution;
            raid.Accepted.Clear();
            GuildRaidStore.Save(raid);
        }

        /// <summary>The raid has gone in: it runs.</summary>
        public static void MarkRunning(Raid raid)
        {
            if (raid.State == State.Running) return;
            raid.State = State.Running;
            raid.Accepted.Clear();
            GuildRaidStore.Save(raid);
        }

        /// <summary>The raid is over, with the score it reached, how long it took and the goals it met.</summary>
        public static void MarkFinished(Raid raid, long score, DateTimeOffset when, long durationSeconds = 0,
                                        IEnumerable<int> validatedGoals = null)
        {
            raid.State = State.Finished;
            raid.Score = score;
            raid.FinishedUtc = when;
            raid.DurationSeconds = durationSeconds;
            raid.ValidatedGoals.Clear();
            if (validatedGoals != null) raid.ValidatedGoals.AddRange(validatedGoals);
            GuildRaidStore.Save(raid);
        }

        /// <summary>The captaincy of a running raid changes hands.</summary>
        public static void HandCaptaincy(Raid raid, long targetId)
        {
            foreach (var p in raid.Participants) p.IsCaptain = p.CharacterId == targetId;
            GuildRaidStore.Save(raid);
        }

        /// <summary>Records the raid's new state and saves it.</summary>
        internal static void Save(Raid raid) => GuildRaidStore.Save(raid);

        internal static string NameOf(long characterId)
            => DatabaseManager.GetCharacterById(characterId)?.Name ?? "";
    }

    /// <summary>The board's tables in world.db: the raids, their participants and their block lists.</summary>
    internal static class GuildRaidStore
    {
        private static SqliteConnection Open()
        {
            var connection = new SqliteConnection(GuildStore.ConnectionStringOverride ?? DatabaseManager.WorldConnectionString);
            connection.Open();
            var create = connection.CreateCommand();
            create.CommandText = @"
                CREATE TABLE IF NOT EXISTS GuildRaids (
                    Uuid TEXT PRIMARY KEY,
                    GuildId INTEGER NOT NULL,
                    RaidId INTEGER NOT NULL,
                    BuyerId INTEGER NOT NULL,
                    BoughtUtcMs INTEGER NOT NULL,
                    State INTEGER NOT NULL,
                    NoteText TEXT NOT NULL DEFAULT '',
                    NoteAuthorId INTEGER NOT NULL DEFAULT 0,
                    NoteAuthorName TEXT NOT NULL DEFAULT '',
                    NoteUtcMs INTEGER NOT NULL DEFAULT 0,
                    Score INTEGER NOT NULL DEFAULT 0,
                    FinishedUtcMs INTEGER NOT NULL DEFAULT 0
                );
                CREATE TABLE IF NOT EXISTS GuildRaidParticipants (
                    Uuid TEXT NOT NULL,
                    CharacterId INTEGER NOT NULL,
                    GroupId INTEGER NOT NULL,
                    IsCaptain INTEGER NOT NULL DEFAULT 0,
                    Ord INTEGER NOT NULL DEFAULT 0,
                    PRIMARY KEY (Uuid, CharacterId)
                );
                CREATE TABLE IF NOT EXISTS GuildRaidBlocked (
                    Uuid TEXT NOT NULL,
                    CharacterId INTEGER NOT NULL,
                    PRIMARY KEY (Uuid, CharacterId)
                );";
            create.ExecuteNonQuery();
            return connection;
        }

        /// <summary>
        /// Every raid not over. One left starting when the server went down is back in constitution;
        /// one left running is over, since its instance went with the server.
        /// </summary>
        public static List<GuildRaidBoard.Raid> LoadAll()
        {
            using var connection = Open();
            var raids = new Dictionary<string, GuildRaidBoard.Raid>();
            var query = connection.CreateCommand();
            query.CommandText = @"SELECT Uuid, GuildId, RaidId, BuyerId, BoughtUtcMs, State, NoteText, NoteAuthorId,
                                         NoteAuthorName, NoteUtcMs, Score, FinishedUtcMs
                                  FROM GuildRaids WHERE State <> $finished;";
            query.Parameters.AddWithValue("$finished", (int)GuildRaidBoard.State.Finished);
            using (var reader = query.ExecuteReader())
            {
                while (reader.Read())
                {
                    var state = (GuildRaidBoard.State)reader.GetInt32(5);
                    var raid = new GuildRaidBoard.Raid
                    {
                        Uuid = reader.GetString(0),
                        GuildId = reader.GetInt64(1),
                        RaidId = reader.GetInt32(2),
                        BuyerId = reader.GetInt64(3),
                        BoughtUtc = DateTimeOffset.FromUnixTimeMilliseconds(reader.GetInt64(4)),
                        State = state == GuildRaidBoard.State.Starting ? GuildRaidBoard.State.Constitution
                              : state == GuildRaidBoard.State.Running ? GuildRaidBoard.State.Finished
                              : state,
                        Note = new GuildRaidBoard.Note
                        {
                            Text = reader.GetString(6),
                            AuthorId = reader.GetInt64(7),
                            AuthorName = reader.GetString(8),
                            When = DateTimeOffset.FromUnixTimeMilliseconds(reader.GetInt64(9)),
                        },
                        Score = reader.GetInt64(10),
                        FinishedUtc = DateTimeOffset.FromUnixTimeMilliseconds(reader.GetInt64(11)),
                    };
                    raids[raid.Uuid] = raid;
                }
            }

            var members = connection.CreateCommand();
            members.CommandText = "SELECT Uuid, CharacterId, GroupId, IsCaptain FROM GuildRaidParticipants ORDER BY Uuid, Ord;";
            using (var reader = members.ExecuteReader())
            {
                while (reader.Read())
                {
                    if (!raids.TryGetValue(reader.GetString(0), out var raid)) continue;
                    raid.Participants.Add(new GuildRaidBoard.Participant
                    {
                        CharacterId = reader.GetInt64(1),
                        Group = reader.GetInt32(2),
                        IsCaptain = reader.GetInt64(3) != 0,
                    });
                }
            }

            var blocked = connection.CreateCommand();
            blocked.CommandText = "SELECT Uuid, CharacterId FROM GuildRaidBlocked;";
            using (var reader = blocked.ExecuteReader())
            {
                while (reader.Read())
                {
                    if (raids.TryGetValue(reader.GetString(0), out var raid)) raid.Blocked.Add(reader.GetInt64(1));
                }
            }
            return raids.Values.Where(r => r.Active).ToList();
        }

        /// <summary>Writes the raid whole: its row, its participants and its block list.</summary>
        public static void Save(GuildRaidBoard.Raid raid)
        {
            using var connection = Open();
            using var transaction = connection.BeginTransaction();

            var upsert = connection.CreateCommand();
            upsert.Transaction = transaction;
            upsert.CommandText = @"
                INSERT INTO GuildRaids (Uuid, GuildId, RaidId, BuyerId, BoughtUtcMs, State, NoteText, NoteAuthorId,
                                        NoteAuthorName, NoteUtcMs, Score, FinishedUtcMs)
                VALUES ($u, $g, $r, $b, $bought, $s, $nt, $na, $nn, $nw, $score, $fin)
                ON CONFLICT(Uuid) DO UPDATE SET
                    State = $s, NoteText = $nt, NoteAuthorId = $na, NoteAuthorName = $nn, NoteUtcMs = $nw,
                    Score = $score, FinishedUtcMs = $fin;";
            upsert.Parameters.AddWithValue("$u", raid.Uuid);
            upsert.Parameters.AddWithValue("$g", raid.GuildId);
            upsert.Parameters.AddWithValue("$r", raid.RaidId);
            upsert.Parameters.AddWithValue("$b", raid.BuyerId);
            upsert.Parameters.AddWithValue("$bought", raid.BoughtUtc.ToUnixTimeMilliseconds());
            upsert.Parameters.AddWithValue("$s", (int)raid.State);
            upsert.Parameters.AddWithValue("$nt", raid.Note?.Text ?? "");
            upsert.Parameters.AddWithValue("$na", raid.Note?.AuthorId ?? 0);
            upsert.Parameters.AddWithValue("$nn", raid.Note?.AuthorName ?? "");
            upsert.Parameters.AddWithValue("$nw", raid.Note?.When.ToUnixTimeMilliseconds() ?? 0);
            upsert.Parameters.AddWithValue("$score", raid.Score);
            upsert.Parameters.AddWithValue("$fin", raid.FinishedUtc.ToUnixTimeMilliseconds());
            upsert.ExecuteNonQuery();

            var clear = connection.CreateCommand();
            clear.Transaction = transaction;
            clear.CommandText = "DELETE FROM GuildRaidParticipants WHERE Uuid = $u; DELETE FROM GuildRaidBlocked WHERE Uuid = $u;";
            clear.Parameters.AddWithValue("$u", raid.Uuid);
            clear.ExecuteNonQuery();

            for (int i = 0; i < raid.Participants.Count; i++)
            {
                var p = raid.Participants[i];
                var insert = connection.CreateCommand();
                insert.Transaction = transaction;
                insert.CommandText = "INSERT INTO GuildRaidParticipants (Uuid, CharacterId, GroupId, IsCaptain, Ord) VALUES ($u, $c, $g, $cap, $o);";
                insert.Parameters.AddWithValue("$u", raid.Uuid);
                insert.Parameters.AddWithValue("$c", p.CharacterId);
                insert.Parameters.AddWithValue("$g", p.Group);
                insert.Parameters.AddWithValue("$cap", p.IsCaptain ? 1 : 0);
                insert.Parameters.AddWithValue("$o", i);
                insert.ExecuteNonQuery();
            }
            foreach (long blocked in raid.Blocked)
            {
                var insert = connection.CreateCommand();
                insert.Transaction = transaction;
                insert.CommandText = "INSERT INTO GuildRaidBlocked (Uuid, CharacterId) VALUES ($u, $c);";
                insert.Parameters.AddWithValue("$u", raid.Uuid);
                insert.Parameters.AddWithValue("$c", blocked);
                insert.ExecuteNonQuery();
            }
            transaction.Commit();
        }
    }
}
