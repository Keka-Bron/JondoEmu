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
        private static Pb Emblem(GuildStore.Guild guild)
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
        /// The guild window's header (jhh): f1 the founding date, f3 the level, f9
        /// the member maximum and f10 how many there are. A freshly created level 1 guild sends
        /// exactly those four fields -without the experience bars f5/f6/f7 that only appear
        /// in guilds with experience-, and that is how it is reproduced.
        /// </summary>
        public static byte[] BuildGuildInfo(GuildStore.Guild guild, int memberCount)
            => Pb.New()
                .Str(1, guild.FoundedUtc)
                .Var(3, guild.Level)
                .Var(9, GuildStore.MaxMembers(guild.Level))
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
            => Pb.New().Msg(1, MemberEntry(member, name, level, accountId, breed, achievementPoints, contributed, online)).Build();

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
        /// f9 = 1 behind it, which is how the four disconnected members of «Hezbola» come out.
        /// </remarks>
        private static Pb MemberEntry(GuildStore.Member member, string name, int level, long accountId,
                                      int breed, int achievementPoints, int contributed, bool online)
        {
            var status = online ? Pb.New().Var(1, 1) : Pb.New();
            var details = Pb.New()
                .Var(2, member.Rank)
                .Var(3, 1)                                   // measured constant, meaning unknown
                .Var(4, member.JoinedUtcMs)
                .Var(6, breed)
                .Msg(7, Pb.New()
                    .VarIfNotZero(1, achievementPoints)
                    .Msg(2, Pb.New().VarIfNotZero(1, contributed).VarIfNotZero(2, contributed))
                    .Var(3, 3)
                    .Msg(8, Pb.New().StrIfNotEmpty(1, member.Note).VarIfNotZero(2, member.NoteMs)))
                .Msg(8, status);
            if (!online) details.Var(9, 1);
            details.Var(10, accountId);

            return Pb.New()
                .Msg(1, Pb.New().Str(2, name).Var(3, level).Msg(5, details))
                .Var(2, member.CharacterId);
        }

        /// <summary>
        /// The journal (jil): one line per f1 { f11 guild, f17 '' if it is the founding, f19 when,
        /// f20 { f2 character, f3 name, f4 2 in the type that has not been understood } }.
        /// </summary>
        /// <remarks>
        /// Measured in «muchas acciones»: three lines, the founding of «Jondo» two milliseconds
        /// after being created, and two of Hiierbita-Xx: one with f4 = 2 and, half a minute later, that of his
        /// joining, whose f19 is exactly the JoinedUtcMs of his jgu.
        /// </remarks>
        public static byte[] BuildLog(IReadOnlyList<GuildStore.LogEntry> entries)
        {
            var jil = Pb.New();
            foreach (var entry in entries)
            {
                var line = Pb.New().Var(11, entry.GuildId);
                if (entry.Kind == GuildStore.LogFounded) line.Str(17, "");
                line.Var(19, entry.WhenMs);
                if (entry.Kind != GuildStore.LogFounded)
                {
                    line.Msg(20, Pb.New()
                        .Var(2, entry.CharacterId)
                        .Str(3, entry.Name)
                        .VarIfNotZero(4, entry.Kind == GuildStore.LogJoined ? 0 : entry.Kind));
                }
                jil.Msg(1, line);
            }
            return jil.Build();
        }

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
        public static byte[] BuildDirectory(IReadOnlyList<DirectoryEntry> entries)
        {
            var jiv = Pb.New();
            foreach (var entry in entries)
            {
                jiv.Msg(1, Pb.New()
                    .Msg(1, Pb.New()
                        .Msg(1, Pb.New()
                            .Var(1, entry.LeaderId)
                            .Msg(6, ProfileBody(entry.Guild, entry.Profile, entry.LeaderName))
                            .Var(7, entry.Members))
                        .Msg(3, Emblem(entry.Guild)))
                    .Var(2, entry.Guild.Id)
                    .Str(3, entry.Guild.Name)
                    .Var(4, entry.Guild.Level));
            }
            return jiv.Build();
        }

        /// <summary>
        /// The contributions left this week (jla): 5, 4, 3 in the contributing capture,
        /// one less for each one, and empty in a guild that has none.
        /// </summary>
        public static byte[] BuildContributionsLeft(int left) => Pb.New().VarIfNotZero(1, left).Build();

        /// <summary>The jff of a freshly founded guild: f3 empty. What fills a guild with a history is not understood.</summary>
        public static byte[] BuildNoBenefits() => Pb.New().Str(3, "").Build();

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

        /// <summary>
        /// An alteration set (lzs): f1 { f1 from, f2 which, f4 2, f5 until } in milliseconds.
        /// </summary>
        /// <remarks>
        /// The capture's «Oráculo de saber» one also carries two f3 blocks with the detail of
        /// what it does -effects 2833 and 2867-. They are not reproduced: of the five oracles only
        /// that one is measured, and copying its effects onto the other four would be inventing them. The client
        /// draws the icon and the time with what goes here.
        /// </remarks>
        public static byte[] BuildAlteration(int alteration, long fromMs, long toMs)
            => Pb.New()
                .Msg(1, Pb.New()
                    .Var(1, fromMs)
                    .Var(2, alteration)
                    .Var(4, 2)
                    .Var(5, toMs))
                .Build();

        // ─── Contribuir ─────────────────────────────────────────────────────────

        /// <summary>The contribution made (jle): f1 the kamas given, f2 the ones left.</summary>
        public static byte[] BuildContribution(long kamas, int left)
            => Pb.New().Var(1, kamas).Var(2, left).Build();

        // ─── Candidaturas e invitaciones ────────────────────────────────────────

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
                    .Msg(9, Pb.New().Var(1, 1))
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

        /// <summary>A single application (jly): f1 1, f2 the application, f3 whom it is shown to.</summary>
        public static byte[] BuildApplication(GuildStore.Application application, string name, int level,
                                              long accountId, string nick, string tag, long toCharacter)
            => Pb.New()
                .Var(1, 1)
                .Msg(2, ApplicationBlock(application, name, level, accountId, nick, tag))
                .Var(3, toCharacter)
                .Build();

        /// <summary>«Someone has sent an application» (jma): f1 his character, f2 his name.</summary>
        public static byte[] BuildApplicationArrived(long characterId, string name)
            => Pb.New().Var(1, characterId).Str(2, name).Build();

        /// <summary>
        /// How an application or an invitation is going (jin): f1 the name, f2 a number that has not
        /// been reconstructed, f3 the state. Measured: 1 on accepting it and 3 when the other joins, with the
        /// member's joining stamp falling exactly in the second jin.
        /// </summary>
        public const int StatusAccepted = 1;
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
