using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Jondo.Unity.Server.Managers;
using Jondo.Unity.World.Content;

namespace Jondo.Unity.Server.Network
{
    /// <summary>
    /// The guild raid frames (Com.Ankama.Dofus.Server.Game.Protocol.Raid.*), read from the client:
    /// its raid frame's handlers, the wrappers they build for the Raids tab and the tab's own code.
    /// No capture carries a raid; the real names come from the obfuscator's names tables in
    /// global-metadata.dat, and every field below is the one the client reads.
    /// </summary>
    public static class GuildRaidProtocol
    {
        /// <summary>A participant's connection state as the client's status enum has it (lcf).</summary>
        public const int Offline = 0;
        public const int Online = 1;

        /// <summary>The ISO date the client parses with DateTime.Parse (the note's date, a deadline).</summary>
        public static string IsoDate(DateTimeOffset when)
            => when.UtcDateTime.ToString("yyyy-MM-ddTHH:mm:ss.fffffffZ", CultureInfo.InvariantCulture);

        /// <summary>
        /// How the client tells raid participants apart: a string, the card's f6. It matches the
        /// connection updates (iav f2), the running raid's map keys and its captain (ibj f4) with
        /// it. The character's id, as text.
        /// </summary>
        public static string KeyOf(long characterId) => characterId.ToString(CultureInfo.InvariantCulture);

        /// <summary>
        /// A player's card (hys), what a raid's member list shows: f1 level, f2 sex, f3 the
        /// character's id, f5 breed, f6 the participant's key (<see cref="KeyOf"/>), f7 the
        /// connection state, f8 the name the lists show.
        /// </summary>
        public static Pb PlayerCard(long characterId)
        {
            var ficha = DatabaseManager.GetCharacterById(characterId);
            bool online = SessionRegistry.FindByCharacter(characterId) != null;
            return Pb.New()
                .VarIfNotZero(1, ficha?.Level ?? 0)
                .VarIfNotZero(2, ficha?.Sex ?? 0)
                .Var(3, characterId)
                .VarIfNotZero(5, ficha?.Breed ?? 0)
                .Str(6, KeyOf(characterId))
                .Msg(7, Status(online))
                .StrIfNotEmpty(8, ficha?.Name);
        }

        /// <summary>A connection state (lcf): f1 0 offline, 1 online, 3 away, 4 private, 5 solo.</summary>
        public static Pb Status(bool online) => Pb.New().VarIfNotZero(1, online ? Online : Offline);

        /// <summary>A player as an OptionalPlayerInformation (hxd): its oneof case 1, the card.</summary>
        public static Pb OptionalPlayer(long characterId) => Pb.New().Msg(1, PlayerCard(characterId));

        /// <summary>
        /// A participant (RaidParticipant, hwr): f3 its group, f4 whether it is the captain, f6 the
        /// player as an OptionalPlayerInformation.
        /// </summary>
        public static Pb ParticipantBlock(GuildRaidBoard.Participant participant)
            => Pb.New()
                .VarIfNotZero(3, participant.Group)
                .VarIfNotZero(4, participant.IsCaptain ? 1 : 0)
                .Msg(6, OptionalPlayer(participant.CharacterId));

        /// <summary>
        /// The raid's note (RaidNote, hyr): f1 its author {f1 id, f2 name}, f2 the date written
        /// (ISO), f3 the text.
        /// </summary>
        public static Pb NoteBlock(GuildRaidBoard.Note note)
            => Pb.New()
                .Msg(1, Pb.New().VarIfNotZero(1, note?.AuthorId ?? 0).StrIfNotEmpty(2, note?.AuthorName))
                .Str(2, IsoDate(note?.When ?? DateTimeOffset.UtcNow))
                .StrIfNotEmpty(3, note?.Text);

        /// <summary>
        /// A raid in constitution (ibg): f1 its note, f2 its participants, f3 the raid's id in the
        /// client's data, f4 the block list as player cards. The tab tags it "abierta", or
        /// "completa" once it has its raid's maximum of players.
        /// </summary>
        public static Pb ConstitutionBlock(GuildRaidBoard.Raid raid)
        {
            var block = Pb.New().Msg(1, NoteBlock(raid.Note));
            foreach (var participant in raid.Participants) block.Msg(2, ParticipantBlock(participant));
            block.Var(3, raid.RaidId);
            foreach (long blocked in raid.Blocked) block.Msg(4, PlayerCard(blocked));
            return block;
        }

        /// <summary>
        /// A raid starting (hyx): f1 who has accepted, by id as text; f2 the deadline (ISO); f3 the
        /// participants; f4 the raid's id; f5 its note. The tab tags it "abierta" still and marks
        /// who has accepted.
        /// </summary>
        public static Pb StartingBlock(GuildRaidBoard.Raid raid)
        {
            var block = Pb.New();
            foreach (long accepted in raid.Accepted) block.Str(1, accepted.ToString(CultureInfo.InvariantCulture));
            block.Str(2, IsoDate(raid.StartDeadline));
            foreach (var participant in raid.Participants) block.Msg(3, ParticipantBlock(participant));
            return block.Var(4, raid.RaidId).Msg(5, NoteBlock(raid.Note));
        }

        /// <summary>
        /// A raid running (hwl): f1 the raid's id, f2 the participants, f3 its note, f4 the running
        /// raid's id -- its uuid, which is what the running raid's panel asks for. Tagged "en curso".
        /// </summary>
        public static Pb RunningBlock(GuildRaidBoard.Raid raid)
        {
            var block = Pb.New().Var(1, raid.RaidId);
            foreach (var participant in raid.Participants) block.Msg(2, ParticipantBlock(participant));
            return block.Msg(3, NoteBlock(raid.Note)).Str(4, raid.Uuid);
        }

        /// <summary>
        /// A raid over (iak): f1 the running raid's id, f2 the participants, f3 the raid's id, f4 how
        /// long it ran in seconds, f5 the goals it met, f6 its score, f7 its note. Tagged
        /// "terminada"; its final score card reads f4, f5 and f6. f8 and f9 belong to the rewards.
        /// </summary>
        public static Pb FinishedBlock(GuildRaidBoard.Raid raid)
        {
            var block = Pb.New().Str(1, raid.Uuid);
            foreach (var participant in raid.Participants) block.Msg(2, ParticipantBlock(participant));
            block.Var(3, raid.RaidId).VarIfNotZero(4, raid.DurationSeconds);
            foreach (int goal in raid.ValidatedGoals) block.Var(5, goal);
            return block.VarIfNotZero(6, raid.Score).Msg(7, NoteBlock(raid.Note));
        }

        /// <summary>A map&lt;string, message&gt; entry: f1 the key, f2 the value.</summary>
        private static Pb Entry(string key, Pb value) => Pb.New().Str(1, key).Msg(2, value);

        /// <summary>
        /// The Raids tab (GuildRaidShowResponse, ice), answer to hzc: f1 the success case, which maps
        /// each raid by its uuid in the map of its state -- f1 in constitution, f4 starting, f2
        /// running, f5 over. A guild with none answers "0a00", as the captures of a new guild do.
        /// </summary>
        public static byte[] BuildGuildRaidShow(IEnumerable<GuildRaidBoard.Raid> raids)
        {
            var success = Pb.New();
            foreach (var raid in raids)
            {
                int map = raid.State switch
                {
                    GuildRaidBoard.State.Starting => 4,
                    GuildRaidBoard.State.Running => 2,
                    GuildRaidBoard.State.Finished => 5,
                    _ => 1,
                };
                success.Msg(map, Entry(raid.Uuid, StateBlock(raid).Block));
            }
            return Pb.New().Msg(1, success).Build();
        }

        // The oneof cases of a raid's update (iai), one per state.
        private const int RunningCase = 1;
        private const int StartingCase = 3;
        private const int FinishedCase = 4;
        private const int ConstitutionCase = 5;

        /// <summary>A raid as its state shows it, with the update's oneof case for that state.</summary>
        private static (int Case, Pb Block) StateBlock(GuildRaidBoard.Raid raid) => raid.State switch
        {
            GuildRaidBoard.State.Starting => (StartingCase, StartingBlock(raid)),
            GuildRaidBoard.State.Running => (RunningCase, RunningBlock(raid)),
            GuildRaidBoard.State.Finished => (FinishedCase, FinishedBlock(raid)),
            _ => (ConstitutionCase, ConstitutionBlock(raid)),
        };

        /// <summary>
        /// Buying a raid, answered (RaidPurchaseResponse, hwe): f1 the result. Success makes the
        /// client print its "raid bought" line; an error, its own text for it.
        /// </summary>
        public static byte[] BuildPurchaseResult(GuildRaidBoard.PurchaseResult result)
            => Pb.New().VarIfNotZero(1, (int)result).Build();

        /// <summary>
        /// A raid was bought (RaidPurchasedEvent, hya): f2 the raid in constitution, f3 its uuid.
        /// The client adds it to the tab's list and selects it.
        /// </summary>
        public static byte[] BuildPurchased(GuildRaidBoard.Raid raid)
            => Pb.New().Msg(2, ConstitutionBlock(raid)).Str(3, raid.Uuid).Build();

        /// <summary>
        /// A raid changed (iai): f2 its uuid, then the raid in the oneof case of its state -- 5 in
        /// constitution, 3 starting, 1 running, 4 over. The tab finds it by uuid and redraws it.
        /// </summary>
        public static byte[] BuildRaidUpdated(GuildRaidBoard.Raid raid)
        {
            var (field, block) = StateBlock(raid);
            return Pb.New().Str(2, raid.Uuid).Msg(field, block).Build();
        }

        // ─── Starting ───────────────────────────────────────────────────────────

        /// <summary>
        /// The captain's start, answered (RaidStartResponse, hxt), a oneof: f2 success {f1 the raid's
        /// uuid}, which the client takes quietly; f3 the participants not connected {f1 each one},
        /// which it lists; f4 an error code, which it translates.
        /// </summary>
        public static byte[] BuildStartResponse(GuildRaidBoard.StartError error, string uuid, IReadOnlyList<long> disconnected)
        {
            if (disconnected != null && disconnected.Count > 0)
            {
                var missing = Pb.New();
                foreach (long id in disconnected) missing.Msg(1, OptionalPlayer(id));
                return Pb.New().Msg(3, missing).Build();
            }
            if (error != GuildRaidBoard.StartError.Done) return Pb.New().Var(4, (int)error).Build();
            return Pb.New().Msg(2, Pb.New().StrIfNotEmpty(1, uuid)).Build();
        }

        /// <summary>
        /// The start asked of a participant (idf): f1 the raid's id, f2 the deadline (ISO). The
        /// client opens its "raid starting" popup, counts down to the deadline and answers with
        /// accept or refuse.
        /// </summary>
        public static byte[] BuildStartPrompt(GuildRaidBoard.Raid raid)
            => Pb.New().Var(1, raid.RaidId).Str(2, IsoDate(raid.StartDeadline)).Build();

        /// <summary>
        /// A participant's answer, answered (RaidStartAnswerResponse, hxx): f2 0 closes the popup,
        /// 1 is "occupied, cannot accept" -- someone fighting cannot be taken in.
        /// </summary>
        public const int OccupiedCannotAccept = 1;

        public static byte[] BuildStartAnswerResult(int result) => Pb.New().VarIfNotZero(2, result).Build();

        /// <summary>
        /// The start called off because someone refused (RaidStartCanceledEvent, icz, case 3): {f1
        /// who}. The client closes the popup and says who refused.
        /// </summary>
        public static byte[] BuildStartRefused(long characterId)
            => Pb.New().Msg(3, Pb.New().Msg(1, OptionalPlayer(characterId))).Build();

        /// <summary>
        /// The start called off because time ran out (icz, case 4): {f1 each one who had not
        /// accepted, f2 how many}. The client closes the popup and names them.
        /// </summary>
        public static byte[] BuildStartTimedOut(IReadOnlyList<long> missing)
        {
            var block = Pb.New();
            foreach (long id in missing) block.Msg(1, OptionalPlayer(id));
            return Pb.New().Msg(4, block.VarIfNotZero(2, missing.Count)).Build();
        }

        /// <summary>
        /// A participant's connection changed (iav): f1 the raid's uuid, f2 the participant's id as
        /// text, f3 his state.
        /// </summary>
        public static byte[] BuildParticipantStatus(string uuid, long characterId, bool online)
            => Pb.New()
                .Str(1, uuid)
                .Str(2, characterId.ToString(CultureInfo.InvariantCulture))
                .Msg(3, Status(online))
                .Build();

        // ─── The running raid (its tracking panel, RaidTrackingUI) ──────────────

        /// <summary>A participant's state in the running raid (hvp), as the panel draws it.</summary>
        public enum RunningState { Left = 0, Offline = 1, Fighting = 2, Present = 3 }

        /// <summary>Where a participant is, for his line in the panel.</summary>
        public static RunningState StateOf(RaidInstance raid, long characterId)
        {
            if (raid.HasLeft(characterId)) return RunningState.Left;
            var session = SessionRegistry.FindByCharacter(characterId);
            if (session == null) return RunningState.Offline;
            return session.State.IsInFight ? RunningState.Fighting : RunningState.Present;
        }

        /// <summary>
        /// A participant of the running raid (ibh): f1 his group, f4 his card, f5 his state. f2, his
        /// health, only when the raid's data gives players health; neither raid does.
        /// </summary>
        public static Pb RunningParticipantBlock(RaidInstance raid, GuildRaidBoard.Participant participant)
            => Pb.New()
                .VarIfNotZero(1, participant.Group)
                .Msg(4, PlayerCard(participant.CharacterId))
                .Var(5, (int)StateOf(raid, participant.CharacterId));

        /// <summary>
        /// A goal's state (ias), a oneof: case 1 met (an empty message), case 2 under way {f1 how
        /// far}. The panel writes the second as "name (n/value)" and ticks the first.
        /// </summary>
        private static Pb GoalBlock(GuildRaidCatalogue.Goal goal, int value)
            => value >= Math.Max(1, goal.Value)
                ? Pb.New().Msg(1, Pb.New())
                : Pb.New().Msg(2, Pb.New().VarIfNotZero(1, value));

        /// <summary>
        /// The running raid's state (RunningRaidState, iau): f1 its score, f2 its goals by id, f3 its
        /// health for a raid that has it (the panel draws hearts only when f3 is there), f4 its
        /// variables by id.
        /// </summary>
        public static Pb RunningStateBlock(RaidInstance raid, IReadOnlyDictionary<int, int> variables)
        {
            var block = Pb.New().VarIfNotZero(1, raid.Score);
            if (raid.Health is int health) block.Var(3, health);
            foreach (var goal in GuildRaidCatalogue.GoalsOf(raid.RaidId))
            {
                if (!raid.Goals.TryGetValue(goal.Id, out int value)) continue;
                block.Msg(2, Pb.New().Var(1, goal.Id).Msg(2, GoalBlock(goal, value)));
            }
            foreach (var (id, value) in variables)
                block.Msg(4, Pb.New().Var(1, id).VarIfNotZero(2, value));
            return block;
        }

        /// <summary>
        /// The running raid (RunningRaid, ibj): f1 its participants by key, f2 the raid's id, f3 when
        /// it ends (ISO; the panel counts down to it, "overtime" past it), f4 the captain's key, f5
        /// its state.
        /// </summary>
        public static Pb RunningRaidBlock(GuildRaidBoard.Raid board, RaidInstance raid, IReadOnlyDictionary<int, int> variables)
        {
            var block = Pb.New();
            foreach (var participant in board.Participants)
                block.Msg(1, Pb.New().Str(1, KeyOf(participant.CharacterId)).Msg(2, RunningParticipantBlock(raid, participant)));
            return block
                .Var(2, raid.RaidId)
                .Str(3, IsoDate(raid.EndsUtc))
                .Str(4, KeyOf(raid.CaptainId))
                .Msg(5, RunningStateBlock(raid, variables));
        }

        /// <summary>
        /// The running raid one takes part in (hvt): f1 the raid, f2 its uuid. The client keeps it as
        /// its own and opens the tracking panel.
        /// </summary>
        public static byte[] BuildRunningRaid(GuildRaidBoard.Raid board, RaidInstance raid, IReadOnlyDictionary<int, int> variables)
            => Pb.New().Msg(1, RunningRaidBlock(board, raid, variables)).Str(2, board.Uuid).Build();

        /// <summary>
        /// A running raid looked at from the guild's tab (ShowAndListenRunningRaidResponse, icj): f1
        /// {f1 the raid}, or f2 an error (which the client takes as "nothing to show").
        /// </summary>
        public static byte[] BuildShowRunningRaid(GuildRaidBoard.Raid board, RaidInstance raid, IReadOnlyDictionary<int, int> variables)
            => board == null || raid == null
                ? Pb.New().Var(2, 2).Build()
                : Pb.New().Msg(1, Pb.New().Msg(1, RunningRaidBlock(board, raid, variables))).Build();

        /// <summary>The running raid's state changed (hzg): f1 the state, f3 the raid's uuid.</summary>
        public static byte[] BuildRunningStateUpdated(RaidInstance raid, IReadOnlyDictionary<int, int> variables)
            => Pb.New().Msg(1, RunningStateBlock(raid, variables)).Str(3, raid.Uuid).Build();

        /// <summary>A participant's state changed (hwo): f1 his key, f2 the raid's uuid, f3 his state.</summary>
        public static byte[] BuildRunningParticipantState(string uuid, long characterId, RunningState state)
            => Pb.New().Str(1, KeyOf(characterId)).Str(2, uuid).Var(3, (int)state).Build();

        /// <summary>The running raid has a new captain (hzh): f1 his key, f2 the raid's uuid.</summary>
        public static byte[] BuildRunningCaptain(string uuid, long characterId)
            => Pb.New().Str(1, KeyOf(characterId)).Str(2, uuid).Build();

        /// <summary>
        /// The raid started over (ibv): f1 its uuid, f2 the raid as it is now. The panel redraws it.
        /// </summary>
        public static byte[] BuildRunningRestarted(GuildRaidBoard.Raid board, RaidInstance raid, IReadOnlyDictionary<int, int> variables)
            => Pb.New().Str(1, board.Uuid).Msg(2, RunningRaidBlock(board, raid, variables)).Build();

        /// <summary>
        /// The raid is over (hyg), to who took part: f1 how long it ran in seconds, f2 its score, f5
        /// the goals it met, f6 whether it is the guild's best of the week, f7 the frieze steps it
        /// unlocked for this player, each with state 1 -- the only state the final score screen
        /// draws, as "rewards to claim". The client closes the panel and opens the final score.
        /// </summary>
        public static byte[] BuildRunningFinished(long durationSeconds, long score, IEnumerable<int> validatedGoals,
                                                  bool newHighScore, IEnumerable<int> unlockedRewards = null)
        {
            var frame = Pb.New().VarIfNotZero(1, durationSeconds).VarIfNotZero(2, score);
            foreach (int goal in validatedGoals ?? Enumerable.Empty<int>()) frame.Var(5, goal);
            frame.VarIfNotZero(6, newHighScore ? 1 : 0);
            foreach (int reward in unlockedRewards ?? Enumerable.Empty<int>())
                frame.Msg(7, Pb.New().Var(1, reward).Var(2, RewardToClaim));
            return frame.Build();
        }

        /// <summary>A frieze step's state in the final score (hyd): 1, to claim.</summary>
        public const int RewardToClaim = 1;

        // ─── The player's raids and the weekly frieze ───────────────────────────

        /// <summary>
        /// A raid's guild as the raid screens show it (hzx): f1 its id as text, f2 its emblem, f3 its
        /// name. It is how a player sees the raid of a guild that is not his.
        /// </summary>
        public static Pb GuildInfoBlock(long guildId)
        {
            var guild = GuildStore.ById(guildId);
            if (guild == null) return Pb.New().Str(1, guildId.ToString(CultureInfo.InvariantCulture));
            return Pb.New()
                .Str(1, guild.Id.ToString(CultureInfo.InvariantCulture))
                .Msg(2, GuildProtocol.Emblem(guild))
                .StrIfNotEmpty(3, guild.Name);
        }

        /// <summary>
        /// The raid a player is in, of his guild or another (icl): f1 its guild, f5 its uuid, and the
        /// raid in the oneof case of its state -- 2 running (hwl), 3 in constitution (ibg), 4
        /// starting (hyx).
        /// </summary>
        public static Pb PlayerRaidBlock(GuildRaidBoard.Raid raid)
        {
            var block = Pb.New().Msg(1, GuildInfoBlock(raid.GuildId)).Str(5, raid.Uuid);
            return raid.State switch
            {
                GuildRaidBoard.State.Running => block.Msg(2, RunningBlock(raid)),
                GuildRaidBoard.State.Starting => block.Msg(4, StartingBlock(raid)),
                _ => block.Msg(3, ConstitutionBlock(raid)),
            };
        }

        /// <summary>
        /// A player's raids (PlayerRaidShowAndListenResponse, hxm), answer to hvx: f1 the raids he
        /// finished, by uuid {f2 the guild, f4 the raid over}; f3 per raid id {f1 the frieze steps
        /// unlocked, f2 the week's best score}; f4 the raid he is in; f5 what he claimed this week
        /// {f2 the steps, f3 the raid}. The rewards screen and the Raids tab read it.
        /// </summary>
        public static byte[] BuildPlayerRaids(IEnumerable<GuildRaidBoard.Raid> finished, IReadOnlyDictionary<int, long> bestScores,
                                              GuildRaidBoard.Raid current, (int RaidId, List<int> Rewards)? claimed)
        {
            var frame = Pb.New();
            foreach (var raid in finished)
                frame.Msg(1, Entry(raid.Uuid, Pb.New().Msg(2, GuildInfoBlock(raid.GuildId)).Msg(4, FinishedBlock(raid))));
            foreach (var (raidId, best) in bestScores)
            {
                var result = Pb.New();
                foreach (int reward in GuildRaidRewards.UnlockedBy(raidId, best)) result.Var(1, reward);
                frame.Msg(3, Pb.New().Var(1, raidId).Msg(2, result.VarIfNotZero(2, best)));
            }
            if (current != null) frame.Msg(4, PlayerRaidBlock(current));
            if (claimed is { } c)
            {
                var block = Pb.New();
                foreach (int reward in c.Rewards) block.Var(2, reward);
                frame.Msg(5, block.Var(3, c.RaidId));
            }
            return frame.Build();
        }

        /// <summary>
        /// Rewards waiting to be claimed (RaidPendingRewardEvent, iac): f2 the raids they come from,
        /// f4 the frieze steps. The client says so in the chat, or with a popup and "see my rewards"
        /// when the weekly reset is less than a day away; an empty f4 says nothing.
        /// </summary>
        public static byte[] BuildPendingRewards(IEnumerable<int> raidIds, IEnumerable<int> rewards)
        {
            var frame = Pb.New();
            foreach (int raid in raidIds) frame.Var(2, raid);
            foreach (int reward in rewards) frame.Var(4, reward);
            return frame.Build();
        }

        // ─── Invitations ─────────────────────────────────────────────────────────

        /// <summary>
        /// An invitation to a raid, to the invited player (ibo): f2 the raid's guild, f3 the raid's
        /// id, f4 who invites. The client opens its "guild raid" popup naming the guild and the
        /// raid, and answers accept or refuse.
        /// </summary>
        public static byte[] BuildInvitation(GuildRaidBoard.Raid raid, long inviterId)
            => Pb.New()
                .Msg(2, GuildInfoBlock(raid.GuildId))
                .Var(3, raid.RaidId)
                .Msg(4, PlayerCard(inviterId))
                .Build();

        /// <summary>
        /// The invitation's answer, answered (RaidInvitationAnswerResponse, hzb): f1 the result, and
        /// on success f3 the raid the player is now in, which the client takes as his.
        /// </summary>
        public static byte[] BuildInvitationAnswerResult(GuildRaidBoard.InvitationAnswerResult result, GuildRaidBoard.Raid raid)
        {
            var frame = Pb.New().VarIfNotZero(1, (int)result);
            if (result == GuildRaidBoard.InvitationAnswerResult.Done && raid != null) frame.Msg(3, PlayerRaidBlock(raid));
            return frame.Build();
        }

        // ─── The weekly ladder ───────────────────────────────────────────────────

        /// <summary>
        /// A guild's line in a raid's ladder (hzr): f1 its place, f2 its score, f4 how long its best
        /// raid took in milliseconds (the tab's duration column formats it as a time), f6 the guild.
        /// </summary>
        public static Pb LadderLineBlock(GuildStore.LadderRow row)
            => Pb.New()
                .Var(1, row.Place)
                .VarIfNotZero(2, row.Score)
                .VarIfNotZero(4, row.DurationMs)
                .Msg(6, GuildInfoBlock(row.GuildId));

        /// <summary>A raid's ladder of a week (hzp): f1 how many guilds scored, f2 the lines.</summary>
        public static Pb LadderBlock(int scored, IEnumerable<GuildStore.LadderRow> rows)
        {
            var block = Pb.New().VarIfNotZero(1, scored);
            foreach (var row in rows) block.Msg(2, LadderLineBlock(row));
            return block;
        }

        /// <summary>How many lines of each ladder the tab is sent.</summary>
        public const int LadderLines = 100;

        /// <summary>
        /// The raid ladders (ShowAndListenRaidLadderResponse, hwu), answer to hzq: per raid id, f1 this
        /// week's ladder and f4 the player's guild's line in it; f2 last week's and f3 its line.
        /// </summary>
        public static byte[] BuildRaidLadder(long guildId, DateTimeOffset now)
        {
            var frame = Pb.New();
            var lastWeek = now.AddDays(-7);
            foreach (var raid in GuildRaidCatalogue.Raids)
            {
                frame.Msg(1, Pb.New().Var(1, raid.Id).Msg(2, LadderBlock(GuildStore.LadderCount(raid.Id, now),
                                                                           GuildStore.Ladder(raid.Id, now, LadderLines))));
                frame.Msg(2, Pb.New().Var(1, raid.Id).Msg(2, LadderBlock(GuildStore.LadderCount(raid.Id, lastWeek),
                                                                           GuildStore.Ladder(raid.Id, lastWeek, LadderLines))));
                var mineLast = guildId == 0 ? null : GuildStore.LadderRowOf(guildId, raid.Id, lastWeek);
                if (mineLast != null) frame.Msg(3, Pb.New().Var(1, raid.Id).Msg(2, LadderLineBlock(mineLast)));
                var mine = guildId == 0 ? null : GuildStore.LadderRowOf(guildId, raid.Id, now);
                if (mine != null) frame.Msg(4, Pb.New().Var(1, raid.Id).Msg(2, LadderLineBlock(mine)));
            }
            return frame.Build();
        }

        /// <summary>
        /// Ladder rewards obtained (ibs): f1 one entry per raid {f4 the raid, f3 its ornaments, f5 its
        /// titles}. The client writes "obtained the ladder rewards of" the raid in the chat; it reads
        /// only f4.
        /// </summary>
        public static byte[] BuildLadderRewardsObtained(IEnumerable<GuildRaidLadder.Paid> paid)
        {
            var frame = Pb.New();
            foreach (var p in paid)
            {
                var entry = Pb.New();
                foreach (int ornament in p.Reward.Ornaments) entry.Var(3, ornament);
                entry.Var(4, p.RaidId);
                foreach (int title in p.Reward.Titles) entry.Var(5, title);
                frame.Msg(1, entry);
            }
            return frame.Build();
        }

        /// <summary>
        /// A claim, answered (RaidClaimRewardResponse, hyj): f1 0 done, 1 noRewardToClaim,
        /// 2 rewardsAlreadyClaim, 3 invalidRaid, 4 inventoryFull, 5 subscriptionRequired; f2 the
        /// frieze steps paid.
        /// </summary>
        public static byte[] BuildClaimResult(GuildRaidRewards.ClaimResult result, IEnumerable<int> rewards)
        {
            var frame = Pb.New().VarIfNotZero(1, (int)result);
            foreach (int reward in rewards ?? Enumerable.Empty<int>()) frame.Var(2, reward);
            return frame.Build();
        }

        /// <summary>Result codes of the running raid's requests, each in its own field.</summary>
        public enum RunningResult
        {
            Done = 0, NotInRaid = 1, ImpossibleForThisRaid = 2, NotCaptain = 3, FailedRestart = 4,
        }

        /// <summary>Finishing it, answered (hzn): f1 1 notInRaid, 2 impossibleForThisRaid, 3 notCaptain.</summary>
        public static byte[] BuildFinishResult(RunningResult result) => Pb.New().VarIfNotZero(1, (int)result).Build();

        /// <summary>Restarting it, answered (hzv): f2 1 notInRaid, 2 impossibleForThisRaid, 3 notCaptain, 4 failed.</summary>
        public static byte[] BuildRestartResult(RunningResult result) => Pb.New().VarIfNotZero(2, (int)result).Build();

        /// <summary>Leaving it, answered (ide): f2 0 gone (the client closes the panel), 1 notInRaid, 2 occupied.</summary>
        public const int LeaveNotInRaid = 1;
        public const int LeaveOccupied = 2;

        public static byte[] BuildRunningLeaveResult(int result) => Pb.New().VarIfNotZero(2, result).Build();

        /// <summary>
        /// Naming another captain while it runs, answered (hyw): f1 1 notInRaid, 2 notCaptain, 3 the
        /// target is not connected, 4 he has left the raid, 5 an invalid target.
        /// </summary>
        public enum RunningCaptainResult { Done = 0, NotInRaid = 1, NotCaptain = 2, TargetDisconnected = 3, TargetHasLeft = 4, InvalidTarget = 5 }

        public static byte[] BuildRunningCaptainResult(RunningCaptainResult result) => Pb.New().VarIfNotZero(1, (int)result).Build();

        /// <summary>A response that is just its result code, f1 (join, leave, captain, removal).</summary>
        public static byte[] BuildResult(int result) => Pb.New().VarIfNotZero(1, result).Build();
    }
}
