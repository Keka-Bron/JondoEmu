using System;
using System.Collections.Generic;
using Jondo.Unity.Server.Managers;

namespace Jondo.Unity.Server.Network
{
    /// <summary>
    /// The guild frames, each measured on the 12 captures of Gremio/.
    ///
    /// What goes byte by byte against the capture -the guild one belongs to (jgw), the header
    /// of its window (jhh) and the four default ranks (jco)- has its test. The member
    /// list (jgu) is built with the fields that are understood and a few constants that are
    /// copied from the founder's capture without knowing what they mean; they are marked where appropriate.
    /// </summary>
    public static class GuildProtocol
    {
        /// <summary>
        /// The guild block shared by the jgw, the jhe and the map actor: the emblem, the
        /// id, the name and the level. In the capture of creating «Jondo»: f1{f3 emblem}, f2 42043,
        /// f3 «Jondo», f4 1. In the actor it goes as option f5 { f4: this block }, and it is what puts
        /// the guild's name under the character's.
        /// </summary>
        public static Pb GuildBlock(GuildStore.Guild guild)
            => Pb.New()
                .Msg(1, Pb.New().Msg(3, Emblem(guild)))
                .Var(2, guild.Id)
                .Str(3, guild.Name)
                .Var(4, guild.Level);

        /// <summary>The emblem, the four numbers of the creation jjg in its fields 1, 2, 3 and 5.</summary>
        internal static Pb Emblem(GuildStore.Guild guild)
            => Pb.New()
                .Var(1, guild.EmblemSymbol)
                .Var(2, guild.EmblemSymbolColor)
                .Var(3, guild.EmblemBackground)
                .Var(5, guild.EmblemSymbolRgb);

        /// <summary>
        /// «You belong to this guild» (jgw): f2 the character's position, f3 the guild block.
        /// Byte by byte against the creation of «Jondo».
        /// </summary>
        public static byte[] BuildGuildJoined(GuildStore.Guild guild, int rank)
            => Pb.New()
                .Var(2, rank)
                .Msg(3, GuildBlock(guild))
                .Build();

        /// <summary>
        /// Belonging to a guild, said silently (jhe): f1 the guild's block, f2 the rank, f3 the
        /// member's contribution. What world entry sends; the jgw is joining, and its handler
        /// prints "you have just joined" and opens a popup every time it comes.
        /// </summary>
        /// <remarks>
        /// Against the captures of «Jondo»: f3 is 10 and then 20 around "contribuir en el gremio
        /// 10 puntos", the {20, 20} of the member's own jgu row. The f4 (1791 for that member,
        /// 4568 for another) is not known and does not go; a member who has just joined has it at
        /// zero, since the jgw does not carry it.
        /// </remarks>
        public static byte[] BuildMembership(GuildStore.Guild guild, int rank, long contribution)
            => Pb.New()
                .Msg(1, GuildBlock(guild))
                .Var(2, rank)
                .VarIfNotZero(3, contribution)
                .Build();

        /// <summary>
        /// The next weekly reset (jez), as an ISO string: Tuesday at 05:00 UTC. The five captures
        /// that ask for it agree -- asked on Sunday the 9th it is the 11th, on Wednesday the 12th
        /// and Saturday the 15th the 18th, on Saturday the 29th 1 September, and on Tuesday the
        /// 1st at 23:12, the reset of that morning already past, the 8th.
        /// </summary>
        public static byte[] BuildWeeklyReset(DateTime utcNow)
            => Pb.New().Str(1, NextWeeklyReset(utcNow).ToString("yyyy-MM-dd'T'HH:mm:ss'Z'",
                                                                  System.Globalization.CultureInfo.InvariantCulture)).Build();

        internal static DateTime NextWeeklyReset(DateTime utcNow)
        {
            int ahead = ((int)DayOfWeek.Tuesday - (int)utcNow.DayOfWeek + 7) % 7;
            var reset = DateTime.SpecifyKind(utcNow.Date.AddDays(ahead).AddHours(5), DateTimeKind.Utc);
            return reset > utcNow ? reset : reset.AddDays(7);
        }

        /// <summary>
        /// A guild tab's answer for a guild that has none of what it lists -- no perks, no raids,
        /// nothing in the paged list -- as the captures of a new guild answer it: the empty
        /// message ("0a00" or "1a00") in the one field they carry, or nothing at all.
        /// </summary>
        public static byte[] BuildEmptyTab(int field)
            => field == 0 ? Array.Empty<byte>() : Pb.New().Bytes(field, Array.Empty<byte>()).Build();

        /// <summary>
        /// The missions' ranks and categories (jgq): f3 { f1 the preferences, jgs { f1 the super
        /// categories, f2 the ranks } } -- the oneof's third case, the one fft.beie reads.
        /// </summary>
        public static byte[] BuildMissionPreferences(byte[] jgs)
            => Pb.New().Msg(3, Pb.New().Bytes(1, jgs ?? Array.Empty<byte>())).Build();

        /// <summary>
        /// The missions' ranks and categories saved (jgl): f2 { f1 the preferences } -- fft.beif keeps
        /// them for the window, which redraws on its bux.
        /// </summary>
        public static byte[] BuildMissionConfigurationSaved(byte[] jgs)
            => Pb.New().Msg(2, Pb.New().Bytes(1, jgs ?? Array.Empty<byte>())).Build();

        /// <summary>The same, refused (jgl f1 {}): the window only redraws what it had.</summary>
        public static byte[] BuildMissionConfigurationRefused() => BuildEmptyTab(1);

        /// <summary>
        /// A member's weekly rewards (jfz): f1 { f1 his guild acknowledgments waiting, f2 { f1 item,
        /// f2 quantity } each other reward } -- ix.nnb's reading, the first tile being the krj's
        /// acknowledgment item with its 100 warning.
        /// </summary>
        public static byte[] BuildWeeklyRewards(int acknowledgments, IEnumerable<(int Item, int Quantity)> others = null)
        {
            var jgb = Pb.New().VarIfNotZero(1, acknowledgments);
            foreach (var (item, quantity) in others ?? Array.Empty<(int, int)>())
                jgb.Msg(2, Pb.New().Var(1, item).Var(2, quantity));
            return Pb.New().Msg(1, jgb).Build();
        }

        /// <summary>
        /// The guild window's header (jhh): f1 the founding date, f3 the level, f5 the experience
        /// the level starts at, f6 the guild's experience, f7 its guild kamas, f9 the experience
        /// the next level starts at and f10 how many members it has.
        /// </summary>
        /// <remarks>
        /// Read off the client (fft::wm builds the header's hook btm from f5, f6, f9 and f3, which
        /// GuildUI.OnGuildInformationGeneral puts in m_expLevelFloor, m_experience,
        /// m_expNextLevelFloor and m_level; f7 goes to the guild service's kamas). f9 was taken
        /// for the member maximum: a level 1 guild's 50 is level 2's floor. The captures: a level 1
        /// guild "f3 1, f7 10, f9 50, f10 2", 20 kamas after a contribution; a level 4 one "f5 150,
        /// f6 181, f9 210". Its f2 (7, then 6) is not read by that handler and does not go.
        /// </remarks>
        public static byte[] BuildGuildInfo(GuildStore.Guild guild, int memberCount)
            => Pb.New()
                .Str(1, guild.FoundedUtc)
                .Var(3, guild.Level)
                .VarIfNotZero(5, GuildLevels.FloorOf(guild.Level))
                .VarIfNotZero(6, guild.Experience)
                .VarIfNotZero(7, guild.GuildKamas)
                .Var(9, GuildLevels.NextFloorOf(guild.Level))
                .Var(10, memberCount)
                .Build();

        /// <summary>
        /// The four default ranks of a freshly created guild (jco). They are a fixed mould,
        /// measured once in the creation of «Jondo»: the names are translation keys
        /// (guild.rank.N.name), and each rank's permissions go as a string of bytes that
        /// this emulator does not yet interpret -they are copied as is, which is what a new
        /// guild does-. The f4{f2} is the rank's icon and f5 its number.
        /// </summary>
        public static byte[] BuildDefaultRanks() => BuildRanks(GuildStore.DefaultRanks(0));

        /// <summary>A notice as the client reads it (leg): { f1 the text, f2 when, f3 the author's name, f4 the author }.</summary>
        private static Pb Notice(GuildStore.Notice notice)
            => Pb.New().Str(1, notice.Text).Var(2, notice.WhenMs).Str(3, notice.AuthorName).Var(4, notice.AuthorId);

        /// <summary>
        /// The message of the day (jhk): { f2 the notice } -- fft.behj keeps it for the window's tooltip
        /// and, the option on, prints «Mensaje del día del gremio: …» in the chat.
        /// </summary>
        public static byte[] BuildMessageOfTheDay(GuildStore.Notice notice) => Pb.New().Msg(2, Notice(notice)).Build();

        /// <summary>The guild's hall changed (jjv): { f2 the hall }, as the capture of choosing Amakna's.</summary>
        public static byte[] BuildHallChanged(int hallId) => Pb.New().Var(2, hallId).Build();

        /// <summary>The guild hall's look (jqf): { f1 the theme }.</summary>
        public static byte[] BuildHallTheme(int theme) => Pb.New().Var(1, theme).Build();

        /// <summary>
        /// A hall room's jss f4 (jry): the guild's own hall, f1 { f1 { f1 its look, f2 the looks it may
        /// take } }, which fft's map frame raises as PlayerGuildHallEntered; or anybody else's, f3 { f1
        /// whether he may choose it }. Measured: "0a070a050801120101" once Amakna's was chosen at
        /// level 1, "1a020801" before, by the leader.
        /// </summary>
        public static byte[] BuildHallVisit(bool own, int theme, IEnumerable<int> themes, bool mayChoose)
        {
            if (own)
                return Pb.New().Msg(1, Pb.New().Msg(1, Pb.New().Var(1, theme).Packed(2, themes.Select(t => (long)t)))).Build();
            return Pb.New().Msg(3, Pb.New().VarIfNotZero(1, mayChoose ? 1 : 0)).Build();
        }

        /// <summary>A member's connection (jhd): { f1 the character, f2 on line }.</summary>
        public static byte[] BuildMemberConnection(long characterId, bool online)
            => Pb.New().Var(1, characterId).VarIfNotZero(2, online ? 1 : 0).Build();

        /// <summary>The logbook's bulletin (jjf): { f1 the notice } -- fft.behl, shown by GuildLogBook.</summary>
        public static byte[] BuildBulletin(GuildStore.Notice notice) => Pb.New().Msg(1, Notice(notice)).Build();

        /// <summary>
        /// A guild's ranks (jco), one per f2 { f2 name, f3 permissions, f4 { f2 icon,
        /// f3 order }, f5 id }. The permissions are { f1 flag, f3 list } just as they arrived.
        /// </summary>
        /// <remarks>
        /// Measured twice: the mould of a new guild, and the same guild after renaming
        /// rank 1, touching rank 2's permissions and creating a fifth -jct, jck and jcv-, which the server
        /// answers with this same whole jco, with the new one in its place and 4 shifted one position.
        /// </remarks>
        public static byte[] BuildRanks(IReadOnlyList<GuildStore.Rank> ranks)
        {
            var jco = Pb.New();
            foreach (var rank in ranks)
            {
                jco.Msg(2, Pb.New()
                    .Str(2, rank.Name)
                    .Msg(3, Pb.New().VarIfNotZero(1, rank.Flag ? 1 : 0).BytesIfNotEmpty(3, rank.Rights))
                    .Msg(4, Pb.New().Var(2, rank.Icon).VarIfNotZero(3, rank.Order))
                    .Var(5, rank.Id));
            }
            return jco.Build();
        }

        /// <summary>
        /// The guild as seen from a map actor (jhe): the guild block, the position, the
        /// member's experience and a constant f4 (1791 in all the «Jondo» captures, with no
        /// reconstructed meaning). It is what the client draws on hovering over.
        /// </summary>
        public const int ActorGuildTrailer = 1791;

        public static byte[] BuildActorGuild(GuildStore.Guild guild, int rank, long memberExperience)
            => Pb.New()
                .Msg(1, GuildBlock(guild))
                .Var(2, rank)
                .VarIfNotZero(3, memberExperience)
                .Var(4, ActorGuildTrailer)
                .Build();

        /// <summary>
        /// A member for the window's list (jgu).
        /// </summary>
        /// <remarks>
        /// Measured over the seven members of two guilds in the captures: f3 the level (with the
        /// omegas inside: 354, 352, 369), f5.f2 the position, f5.f4 when he joined, f5.f6 THE CLASS -12
        /// the pandawa, 10 the sadida, 11 the sacrier; by copying the founder's 11 every member
        /// came out drawn as a sacrier-, f5.f7.f1 the achievement points -8094 the founder, 2466,
        /// 732 and 55 the rest, which is what the «Logros» column shows-, f5.f8 whether he is connected and
        /// f5.f10 the account. f5.f3 (1 for the founder, absent for the rest) and f5.f7.f3 (3 for
        /// the founder, 2 for the rest) are still without meaning and go as for the founder.
        /// </remarks>
        public static byte[] BuildMember(GuildStore.Member member, string name, int level, long accountId,
                                         int breed, int achievementPoints, int contributed = 0, bool online = true)
            => BuildMembers(new[] { new MemberRow(member, name, level, accountId, breed, achievementPoints, contributed, online) });

        /// <summary>One member's row of the list, with what the entry needs besides the membership.</summary>
        public readonly record struct MemberRow(GuildStore.Member Member, string Name, int Level, long AccountId,
                                                int Breed, int AchievementPoints, int Contributed, bool Online);

        /// <summary>
        /// The member list (jgu): EVERY member in one message, an f1 each.
        /// </summary>
        /// <remarks>
        /// The client takes a jgu as the whole list and replaces the one it had (fft::begz sets
        /// the guild service's list from it), so a jgu per member left only the last one. The
        /// captures agree: sixteen members in one jgu ("salir de mi gremio", "recibir
        /// invitacion"), two in one ("muchas acciones"), never one each.
        /// </remarks>
        public static byte[] BuildMembers(IEnumerable<MemberRow> rows)
        {
            var list = Pb.New();
            foreach (var r in rows)
                list.Msg(1, MemberEntry(r.Member, r.Name, r.Level, r.AccountId, r.Breed, r.AchievementPoints,
                                        r.Contributed, r.Online));
            return list.Build();
        }

        /// <summary>
        /// A member brought up to date (jgz): the same entry as the jgu, in f2. It is what
        /// the server answers to the note (jjj) and what it sends after a contribution.
        /// </summary>
        public static byte[] BuildMemberUpdated(GuildStore.Member member, string name, int level, long accountId,
                                                int breed, int achievementPoints, int contributed = 0, bool online = true)
            => Pb.New().Msg(2, MemberEntry(member, name, level, accountId, breed, achievementPoints, contributed, online)).Build();

        /// <summary>
        /// A member's entry, the one that goes inside the jgu and the jgz.
        /// </summary>
        /// <remarks>
        /// From the jgz of «hola» and the jgu after contributing: f7.f2 are the guild coins,
        /// the same number twice -{10, 10} after one contribution, {20, 20} after two- and empty
        /// before any; f7.f8 is the note, { f1 text, f2 when }, and empty without a note. The
        /// f8 { f1: 1 } means he is connected; when offline the server sends f8 empty and an
        /// f9 = 1 behind it, which is how the four disconnected members of «Guild-A» come out.
        /// </remarks>
        private static Pb MemberEntry(GuildStore.Member member, string name, int level, long accountId,
                                      int breed, int achievementPoints, int contributed, bool online)
        {
            var status = online ? Handlers.ChatStatusHandler.Online(member.CharacterId) : Pb.New();
            var details = Pb.New()
                .Var(2, member.Rank)
                .Var(3, 1)                                   // measured constant, meaning unknown
                .Var(4, member.JoinedUtcMs)
                .Var(6, breed)
                .Msg(7, Pb.New()
                    .VarIfNotZero(1, achievementPoints)
                    .Msg(2, Pb.New().VarIfNotZero(1, contributed).VarIfNotZero(2, contributed))
                    .Var(3, 3)
                    .VarIfNotZero(5, SharesHavenBagWithGuild(member.CharacterId) ? 1 : 0)
                    .Msg(8, Pb.New().StrIfNotEmpty(1, member.Note).VarIfNotZero(2, member.NoteMs)))
                .Msg(8, status);
            if (!online) details.Var(9, 1);
            details.Var(10, accountId);

            return Pb.New()
                .Msg(1, Pb.New().Str(2, name).Var(3, level).Msg(5, details))
                .Var(2, member.CharacterId);
        }

        /// <summary>
        /// Whether the member's haven bag is open to his guild: f7 f5, the «Visitar» button of the
        /// members' list (GuildMembersUI.BindMembersItem, cf +0x68). In «comprar una raid de gremio y
        /// activarlo» the player's own line carries it while he stands in the world.
        /// </summary>
        private static bool SharesHavenBagWithGuild(long characterId)
            => (Managers.HavenBagStore.SharedOf(characterId) & Managers.HavenBagStore.SharedWithGuild) != 0;

        /// <summary>
        /// A guild's directory sheet (jci), or the empty one of a freshly founded guild.
        /// </summary>
        /// <remarks>
        /// Both measured: the empty one is f2 { f3: 1, f5: 2, f6: 0x01, f10: guild }, and the written one
        /// -«Hola!», levels 20 to 100, nine tags, title «Dragon Ball»- returns what
        /// the jcc sent with the f1 (when) and the f8 (the leader) the server puts in.
        /// </remarks>
        public static byte[] BuildProfile(GuildStore.Guild guild, GuildStore.Profile profile, string leaderName)
            => Pb.New().Msg(2, ProfileBody(guild, profile, leaderName)).Build();

        private static Pb ProfileBody(GuildStore.Guild guild, GuildStore.Profile profile, string leaderName)
        {
            if (profile == null || profile.WhenMs == 0)
            {
                return Pb.New().Var(3, 1).Var(5, 2).Bytes(6, new byte[] { 0x01 }).Var(10, guild.Id);
            }

            return Pb.New()
                .Var(1, profile.WhenMs)
                .StrIfNotEmpty(2, profile.Description)
                .VarIfNotZero(3, profile.MinLevel)
                .BytesIfNotEmpty(4, profile.Tags)
                .VarIfNotZero(5, profile.F5)
                .BytesIfNotEmpty(6, profile.F6)
                .StrIfNotEmpty(8, leaderName)
                .VarIfNotZero(9, profile.MaxLevel)
                .Var(10, guild.Id)
                .StrIfNotEmpty(13, profile.Title);
        }

        /// <summary>A directory guild, with its sheet, its leader, how many they are and its emblem.</summary>
        public sealed class DirectoryEntry
        {
            public GuildStore.Guild Guild { get; init; }
            public GuildStore.Profile Profile { get; init; }
            public long LeaderId { get; init; }
            public string LeaderName { get; init; } = "";
            public int Members { get; init; }
        }

        /// <summary>
        /// The directory (jiv): one f1 per guild, { f1 { f1 { f1 leader, f6 sheet, f7 members },
        /// f3 emblem }, f2 id, f3 name, f4 level }.
        /// </summary>
        /// <remarks>
        /// From the 6,754-byte jiv that answers the directory search: the leader by his id, the
        /// sheet with the same shape as the jci, how many members it has (91, 339), the emblem with
        /// the jjg's four numbers, and the guild with its id, its name and its level.
        /// </remarks>
        /// <remarks>
        /// f3 and f6 too, or the client shows «0/0 Guildes» and «Aucune guilde ne correspond à votre
        /// recherche» whatever the f1s hold. Read off the 3.6.10.11 client (Cpp2IL): fft.behg hands the
        /// list, f5, f6 and f3 to btj(list, f5, f6, f3), and GuildDirectory.OnGuildList stores each guild
        /// at f5 + i (the page's first index), writes «f3/max Guildes» with f3 the guilds found, and sizes
        /// its list from f3 only when f6 is not zero -- left at zero, the list stays empty.
        /// </remarks>
        public static byte[] BuildDirectory(IReadOnlyList<DirectoryEntry> entries)
        {
            var jiv = Pb.New();
            foreach (var entry in entries) jiv.Msg(1, DirectoryGuild(entry));
            // f5, the first index, stays 0: every guild is sent at once.
            return jiv.VarIfNotZero(3, entries.Count).VarIfNotZero(6, entries.Count).Build();
        }

        /// <summary>A guild as the directory lists it (les): the leader, the sheet, how many they are, the emblem.</summary>
        private static Pb DirectoryGuild(DirectoryEntry entry)
            => Pb.New()
                .Msg(1, Pb.New()
                    .Msg(1, Pb.New()
                        .Var(1, entry.LeaderId)
                        .Msg(6, ProfileBody(entry.Guild, entry.Profile, entry.LeaderName))
                        .Var(7, entry.Members))
                    .Msg(3, Emblem(entry.Guild)))
                .Var(2, entry.Guild.Id)
                .Str(3, entry.Guild.Name)
                .Var(4, entry.Guild.Level);

        /// <summary>
        /// A guild's card (jie): f1 its members, each as the jgu lists them, f2 its founding in
        /// seconds, f3 the guild as the directory lists it. Read off fft.behh: the card's leader is
        /// the directory's f1 looked up among the members, its member count the directory's f7, and
        /// its creation date the f2.
        /// </summary>
        public static byte[] BuildGuildCard(DirectoryEntry entry, IEnumerable<MemberRow> members, long foundedSeconds)
        {
            var jie = Pb.New();
            foreach (var r in members)
                jie.Msg(1, MemberEntry(r.Member, r.Name, r.Level, r.AccountId, r.Breed, r.AchievementPoints,
                                       r.Contributed, r.Online));
            return jie.VarIfNotZero(2, foundedSeconds).Msg(3, DirectoryGuild(entry)).Build();
        }

        /// <summary>
        /// The contributions left this week (jla): 5, 4, 3 in the contributing capture,
        /// one less for each one, and empty in a guild that has none.
        /// </summary>
        public static byte[] BuildContributionsLeft(int left) => Pb.New().VarIfNotZero(1, left).Build();

        /// <summary>
        /// The guild's week (jfl): f1 its tier, f2 its activity points, f3 the member's token cap,
        /// f4 his tokens of the week and f5 the tier waiting for next week.
        /// </summary>
        /// <remarks>
        /// Read off the client: jn(jfl) keeps f1, f5, f2, f4 and f3; the missions tab shows the tier
        /// by f1 and the member's tokens as "min(f4, f3) / f3", and the tier window takes f5 for
        /// its next tier (GuildMissionTierOption.m_nextGuildMissionActivityData). The captures:
        /// "f1 1, f3 250" right after choosing tier 1, "f1 1, f2 100, f3 250, f4 10" after one
        /// contribution and "200 / 20" after two. f5 is optional, so it only goes when it says
        /// something.
        /// </remarks>
        public static Pb WeekBlock(GuildActivity.Week week, int tokenCap, int tokens)
            => Pb.New()
                .Var(1, week.ActivityId)
                .VarIfNotZero(2, week.Points)
                .Var(3, tokenCap)
                .VarIfNotZero(4, tokens)
                .VarIfNotZero(5, week.PendingActivityId);

        /// <summary>
        /// The guild's week as the jfp asks for it (jff): f2 { f1 the week } with a tier, f3 empty
        /// without one -- what a freshly founded guild gets, and what makes the client ask for a tier
        /// before contributing.
        /// </summary>
        public static byte[] BuildWeek(GuildActivity.Week week, int tokenCap, int tokens)
            => week == null || !week.HasActivity
                ? Pb.New().Str(3, "").Build()
                : Pb.New().Msg(2, Pb.New().Msg(1, WeekBlock(week, tokenCap, tokens))).Build();

        /// <summary>
        /// The answer to choosing a tier (jdb): f2 { f1 the mission preferences, f2 the week, f3 the
        /// missions }. The capture of choosing tier 1: "f2 { f1 {}, f2 { f1 1, f3 250 }, f3 { twelve
        /// missions } }". The client reads f3 without asking whether it is there, so it always goes,
        /// empty until missions are drawn.
        /// </summary>
        public static byte[] BuildTierChosen(GuildActivity.Week week, int tokenCap, int tokens,
                                             IEnumerable<GuildMissions.Drawn> missions = null,
                                             IReadOnlyDictionary<int, GuildMissions.Standing> standings = null,
                                             IReadOnlyDictionary<int, List<Subscriber>> subscribers = null)
            => Pb.New().Msg(2, Pb.New()
                    .Bytes(1, week.Preferences ?? Array.Empty<byte>())
                    .Msg(2, WeekBlock(week, tokenCap, tokens))
                    .Msg(3, MissionsBlock(missions, standings, subscribers)))
                .Build();

        /// <summary>The guild's week changed (jex): f1 the week, as the contributing capture sends it.</summary>
        public static byte[] BuildWeekChanged(GuildActivity.Week week, int tokenCap, int tokens)
            => Pb.New().Msg(1, WeekBlock(week, tokenCap, tokens)).Build();

        /// <summary>
        /// The week's missions (jdf): f2 each mission. The capture's: twelve in f2 and the eight
        /// of the live events in f4, which this server has none of.
        /// </summary>
        public static Pb MissionsBlock(IEnumerable<GuildMissions.Drawn> missions,
                                       IReadOnlyDictionary<int, GuildMissions.Standing> standings = null,
                                       IReadOnlyDictionary<int, List<Subscriber>> subscribers = null)
        {
            var jdf = Pb.New();
            foreach (var mission in missions ?? Array.Empty<GuildMissions.Drawn>())
            {
                var standing = standings != null && standings.TryGetValue(mission.Slot, out var s) ? s : GuildMissions.Standing.None;
                var interested = subscribers != null && subscribers.TryGetValue(mission.Slot, out var l) ? l : null;
                jdf.Msg(2, Mission(mission, standing, interested));
            }
            return jdf;
        }

        /// <summary>
        /// A member interested in a mission (jdv), as the subscribers window draws him
        /// (GuildMissionSubscribersUi.BindItem): f1 connected, f2 name, f3 breed and f7 sex for the
        /// class icon, f5 level, f6 whether he has done it, f8 the character. f4 is not read.
        /// </summary>
        public sealed record Subscriber(long CharacterId, string Name, int Breed, int Sex, int Level,
                                        bool Connected, bool Finished);

        public static Pb SubscriberBlock(Subscriber subscriber)
            => Pb.New()
                .VarIfNotZero(1, subscriber.Connected ? 1 : 0)
                .Str(2, subscriber.Name)
                .Var(3, subscriber.Breed)
                .Var(5, subscriber.Level)
                .VarIfNotZero(6, subscriber.Finished ? 1 : 0)
                .VarIfNotZero(7, subscriber.Sex)
                .Var(8, subscriber.CharacterId);

        /// <summary>
        /// One mission (jdx): f1 its uuid, f3 the mission, f4 its objective's state (jdu) -- a oneof
        /// the client reads by its case (iz.ctor): f1 {} not started, f3 { f1 count } on the way,
        /// f2 {} done -- and f4 the objective. The capture's twenty all go { {}, objective }:
        /// «ad92a545-…», 65, { {}, 65 }. The client shows the count as «Progresión: n/target» when
        /// the target is above one, and marks the card done when its objective is (jk.npb).
        /// </summary>
        public static Pb Mission(GuildMissions.Drawn mission, GuildMissions.Standing standing = default,
                                 IEnumerable<Subscriber> subscribers = null)
        {
            var objective = Pb.New();
            if (standing.Done) objective.Msg(2, Pb.New());
            else if (standing.Count > 0) objective.Msg(3, Pb.New().Var(1, standing.Count));
            else objective.Msg(1, Pb.New());
            var jdx = Pb.New().Str(1, mission.Uuid);
            foreach (var subscriber in subscribers ?? Array.Empty<Subscriber>()) jdx.Msg(2, SubscriberBlock(subscriber));
            return jdx
                .Var(3, mission.MissionId)
                .Msg(4, objective.Var(4, mission.ObjectiveId));
        }

        /// <summary>One mission changed (jdc): f1 the mission as it now stands for the member.</summary>
        public static byte[] BuildMissionChanged(GuildMissions.Drawn mission, GuildMissions.Standing standing,
                                                 IEnumerable<Subscriber> subscribers = null)
            => Pb.New().Msg(1, Mission(mission, standing, subscribers)).Build();

        /// <summary>
        /// The answer to marking a mission interesting (jdk): f2 { f2 the mission as it now stands },
        /// which the client puts on its card (fft.beic).
        /// </summary>
        public static byte[] BuildInterestAnswer(GuildMissions.Drawn mission, GuildMissions.Standing standing,
                                                 IEnumerable<Subscriber> subscribers)
            => Pb.New().Msg(2, Pb.New().Msg(2, Mission(mission, standing, subscribers))).Build();

        /// <summary>A mission rerolled (jed): f2 { f1 the old one's uuid, f3 the new one }.</summary>
        public static byte[] BuildRerolled(string oldUuid, GuildMissions.Drawn fresh)
            => Pb.New().Msg(2, Pb.New().Str(1, oldUuid).Msg(3, Mission(fresh))).Build();

        /// <summary>No mission left to reroll into (jed): f3 {}.</summary>
        public static byte[] BuildRerollRefused() => Pb.New().Msg(3, Pb.New()).Build();

        /// <summary>A mission done (jen): f1 the mission, for the client's «¡ENCARGO TERMINADO!».</summary>
        public static byte[] BuildMissionDone(int missionId) => Pb.New().Var(1, missionId).Build();

        /// <summary>
        /// The missions tab (jei, the answer to jeu): f1 { f1 the week's missions }. A guild with no
        /// tier gets the empty f3 the tab had before.
        /// </summary>
        public static byte[] BuildMissions(IReadOnlyCollection<GuildMissions.Drawn> missions,
                                           IReadOnlyDictionary<int, GuildMissions.Standing> standings = null,
                                           IReadOnlyDictionary<int, List<Subscriber>> subscribers = null)
            => missions == null || missions.Count == 0
                ? BuildEmptyTab(3)
                : Pb.New().Msg(1, Pb.New().Msg(1, MissionsBlock(missions, standings, subscribers))).Build();

        /// <summary>
        /// The guild's message (jci), empty: what the real server answers to the jiy {f2: 4} of
        /// a freshly founded guild, byte for byte except the id. Measured twice in the capture of
        /// founding «Jondo»: f2 { f3: 1, f5: 2, f6: 0x01, f10: the guild }.
        /// </summary>
        public static byte[] BuildEmptyGuildMessage(long guildId)
            => Pb.New()
                .Msg(2, Pb.New()
                    .Var(3, 1)
                    .Var(5, 2)
                    .Bytes(6, new byte[] { 0x01 })
                    .Var(10, guildId))
                .Build();

        /// <summary>
        /// The notice of no longer being in the guild (khj), and that of having just joined (khi):
        /// both carry f1 = 97, measured on founding «Jondo» and on leaving it. It is neither the position nor the
        /// guild -it is the same number in both directions-, and it goes as is.
        /// </summary>
        public const int GuildNotice = 97;

        public static byte[] BuildGoneNotice() => Pb.New().Var(1, GuildNotice).Build();

        // ─── The guild shop ─────────────────────────────────────────────────────

        /// <summary>
        /// The shop (jkh): f2 { f1 the active accounts, and one f2 per article with its number and its
        /// price already multiplied }. Each article's empty f2{f2{f3}} goes as is, which is what
        /// the real server sends in the two guilds measured -one with one account and another with
        /// four-.
        /// </summary>
        public static byte[] BuildShop(int accounts)
        {
            var jkh = Pb.New();
            var cuerpo = Pb.New().Var(1, accounts);
            foreach (var oracle in GuildOracles.All)
            {
                cuerpo.Msg(2, Pb.New()
                    .Var(1, oracle.Id)
                    .Msg(2, Pb.New()
                        .Var(1, GuildOracles.PriceFor(oracle.Id, accounts))
                        .Msg(2, Pb.New().Str(3, ""))));
            }
            return jkh.Msg(2, cuerpo).Build();
        }

        /// <summary>Bought (jkj with the article). The refusal is the same message EMPTY.</summary>
        public static byte[] BuildShopBought(int oracle) => Pb.New().Var(1, oracle).Build();
        public static byte[] BuildShopRefused() => System.Array.Empty<byte>();

        /// <summary>The guild kamas left (jia).</summary>
        public static byte[] BuildGuildKamas(long kamas) => Pb.New().Var(1, kamas).Build();

        /// <summary>
        /// What has been bought and is still to be activated (jkv): f1 { f2 { f1 …, f3 the deadline } }, f2 the
        /// article. The inner f1 was 5 in the only measured purchase and what it is is not known -it
        /// is not the article, which was 1, nor the accounts, which were four-, so it goes as
        /// it was.
        /// </summary>
        public const int PendingOracleUnknown = 5;

        public static byte[] BuildPendingOracle(int oracle, string deadlineUtc)
            => Pb.New()
                .Msg(1, Pb.New().Msg(2, Pb.New().Var(1, PendingOracleUnknown).Str(3, deadlineUtc)))
                .Var(2, oracle)
                .Build();

        /// <summary>Activated (jkx): f1 the article.</summary>
        public static byte[] BuildOracleActivated(int oracle) => Pb.New().Var(1, oracle).Build();

        // ─── Contributing ───────────────────────────────────────────────────────

        /// <summary>The contribution made (jle): f1 the kamas given, f2 the ones left.</summary>
        public static byte[] BuildContribution(long kamas, int left)
            => Pb.New().Var(1, kamas).Var(2, left).Build();

        // ─── Applications and invitations ───────────────────────────────────────

        /// <summary>
        /// An application's block, the same inside the jmf and the jly: when it was sent, with
        /// what text, and who sends it -his account, his name, his tag, his character, the
        /// account's nickname and his level-.
        /// </summary>
        private static Pb ApplicationBlock(GuildStore.Application application, string name, int level,
                                           long accountId, string accountNick, string tag)
            => Pb.New()
                .Var(1, application.WhenMs)
                .Str(3, application.Message)
                .Msg(4, Pb.New()
                    .Var(1, 10)                       // measured on the one application; not understood
                    .Var(3, 1)                        // idem
                    .Var(4, accountId)
                    .Str(5, name)
                    .Str(6, tag)
                    .Var(7, application.CharacterId)
                    .Str(8, accountNick)
                    .Msg(9, Handlers.ChatStatusHandler.Online(application.CharacterId))
                    .Var(10, level));

        /// <summary>
        /// The applications tab (jmf): f2 the tab asked for, one f3 per application and
        /// f4 how many there are. With none only the tab goes, which is what arrives in the guilds without
        /// applications in the captures.
        /// </summary>
        public static byte[] BuildApplications(int tab, IReadOnlyList<(GuildStore.Application Application,
                                                                       string Name, int Level, long AccountId,
                                                                       string Nick, string Tag)> applications)
        {
            var jmf = Pb.New().Var(2, tab);
            foreach (var (application, name, level, accountId, nick, tag) in applications)
            {
                jmf.Msg(3, ApplicationBlock(application, name, level, accountId, nick, tag));
            }
            if (applications.Count > 0) jmf.Var(4, applications.Count);
            return jmf.Build();
        }

        /// <summary>
        /// An application leaves the list (jly): f1 1, f2 the application, f3 who dealt with it.
        /// Measured in «comprar gremialogema-crear gremio-valorar candidatura-rechazarla»: the leader
        /// refuses with the jlt and the real server answers at once with this, naming the leader.
        /// </summary>
        public static byte[] BuildApplicationGone(GuildStore.Application application, string name, int level,
                                                  long accountId, string nick, string tag, long dealtWithBy)
            => BuildApplicationMoved(ApplicationGone, application, name, level, accountId, nick, tag, dealtWithBy);

        /// <summary>The jly with any of its three moves: an application that comes, goes or is rewritten.</summary>
        public static byte[] BuildApplicationMoved(int move, GuildStore.Application application, string name, int level,
                                                   long accountId, string nick, string tag, long by)
            => Pb.New()
                .VarIfNotZero(1, move)
                .Msg(2, ApplicationBlock(application, name, level, accountId, nick, tag))
                .Var(3, by)
                .Build();

        /// <summary>
        /// The jly's f1: the client's enum has three values, and 1 is the one measured, on a refusal.
        /// 0 for a new one and 2 for a rewritten one are INFERRED from the alliance's twin, the mbb,
        /// whose three are measured in that order.
        /// </summary>
        public const int ApplicationNew = 0;
        public const int ApplicationGone = 1;
        public const int ApplicationRewritten = 2;

        /// <summary>
        /// One's own pending application (jme), what the guild directory shows under "my pending
        /// application": f1 { f1 the guild's block, f2 the application }. Empty when there is none,
        /// which is the jme every capture answers the directory's jlu with. The client's handler
        /// reads only f1 (fft.begr).
        /// </summary>
        public static byte[] BuildOwnApplication(GuildStore.Guild guild, GuildStore.Application application, string name,
                                                 int level, long accountId, string nick, string tag)
            => Pb.New()
                .Msg(1, Pb.New()
                    .Msg(1, GuildBlock(guild))
                    .Msg(2, ApplicationBlock(application, name, level, accountId, nick, tag)))
                .Build();

        /// <summary>
        /// One's application answered (jmp): f1 the guild's block, f2 whether it was accepted. The
        /// client says "ui.guild.applyAccepted" or "applyRejected" with a link to the guild (its id
        /// and its name, f2 and f3 of the block) and takes the pending application away.
        /// </summary>
        public static byte[] BuildApplicationAnswered(GuildStore.Guild guild, bool accepted)
            => Pb.New().Msg(1, GuildBlock(guild)).VarIfNotZero(2, accepted ? 1 : 0).Build();

        /// <summary>
        /// One's own application withdrawn (jmi): f2 whether it was. The client's handler reads only
        /// f2 and, when it is false, warns that an application cannot be withdrawn so soon
        /// (ui.guild.applyDeleteDelay).
        /// </summary>
        public static byte[] BuildApplicationWithdrawn(bool withdrawn)
            => Pb.New().VarIfNotZero(2, withdrawn ? 1 : 0).Build();

        /// <summary>«Someone has sent an application» (jma): f1 his character, f2 his name.</summary>
        public static byte[] BuildApplicationArrived(long characterId, string name)
            => Pb.New().Var(1, characterId).Str(2, name).Build();

        /// <summary>
        /// How an invitation is going (jin): f1 the invited one's name, f2 a number that has not been
        /// reconstructed, f3 the state. Measured in «...-invitar a gremio»: the jjn, a jin at 1 at
        /// once, and when the other accepts the lqn 1423 and a jin at 3, the member's joining stamp
        /// falling exactly in the second.
        /// </summary>
        public const int StatusInvited = 1;
        public const int StatusJoined = 3;

        public static byte[] BuildApplicationStatus(string name, long number, int status)
            => Pb.New().Str(1, name).Var(2, number).Var(3, status).Build();

        /// <summary>
        /// «You are invited to a guild» (jiq): f1 the guild block -the same as the jgw's- and f2 who
        /// invites. Measured byte by byte in the capture of receiving an invitation.
        /// </summary>
        public static byte[] BuildInvitation(GuildStore.Guild guild, string inviterName)
            => Pb.New()
                .Msg(1, GuildBlock(guild))
                .Str(2, inviterName)
                .Build();

        /// <summary>What closes joining the guild (jij). It was 3 on accepting the invitation.</summary>
        public static byte[] BuildJoinDone() => Pb.New().Var(1, StatusJoined).Build();
    }
}
