using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Jondo.Unity.Protocol;
using Jondo.Unity.Server;
using Jondo.Unity.Server.Handlers;
using Jondo.Unity.Server.Managers;
using Jondo.Unity.Server.Network;
using Microsoft.Data.Sqlite;
using Xunit;

namespace Jondo.Unity.Tests.World
{
    /// <summary>
    /// What the guild window asks for and receives, byte for byte against the «Jondo» captures:
    /// a member's entry with his class, his points, his guild coins and his note; the ranks
    /// after editing them; the journal; and the directory sheet.
    /// </summary>
    [Collection("guild raids")]
    public class GuildWindowTests : IDisposable
    {
        private readonly string _file;

        public GuildWindowTests()
        {
            _file = Path.Combine(Path.GetTempPath(), $"jondo-ventana-{Guid.NewGuid():N}.db");
            GuildStore.ConnectionStringOverride = $"Data Source={_file}";
        }

        public void Dispose()
        {
            GuildStore.ConnectionStringOverride = null;
            TestDatabases.ReleasePool(_file);
            try { File.Delete(_file); } catch (IOException) { }
        }

        private static string Hex(byte[] bytes) => string.Concat(bytes.Select(b => b.ToString("x2")));

        private static readonly GuildStore.Guild Jondo = new()
        {
            Id = 42043, Name = "Jondo", Level = 1,
            EmblemSymbol = 165, EmblemSymbolColor = 8, EmblemBackground = 16744448, EmblemSymbolRgb = 9476018,
        };

        /// <summary>The founder as he comes out in the founding jgu: sacrier, 8,094 points, no guild coins nor note.</summary>
        private static GuildStore.Member Recorder(string note = "", long noteMs = 0) => new()
        {
            CharacterId = 300000000001, GuildId = 42043, Rank = 1, JoinedUtcMs = 1786567982202, Note = note, NoteMs = noteMs,
        };

        [Fact]
        public void The_founders_row_is_the_capture()
        {
            Assert.Equal("0a3d0a34120c4865726f2d4578616d706c6518e2022a211001180120faf8ffbdff33300b3a09089e3f1200180342004202080150818ece1c1081f092cbdd08",
                         Hex(GuildProtocol.BuildMember(Recorder(), "Hero-Example", 354, 60000001, breed: 11, achievementPoints: 8094)));
        }

        /// <summary>
        /// The list goes whole, every member an f1 of ONE jgu: the client replaces its list with
        /// each jgu, and one per member left it holding only the last.
        /// </summary>
        [Fact]
        public void The_member_list_goes_in_one_message()
        {
            var second = new GuildStore.Member { CharacterId = 300000000002, GuildId = 42043, Rank = 4, JoinedUtcMs = 1786567999999 };
            byte[] list = GuildProtocol.BuildMembers(new[]
            {
                new GuildProtocol.MemberRow(Recorder(), "Hero-Example", 354, 60000001, 11, 8094, 0, true),
                new GuildProtocol.MemberRow(second, "Player-Alpha", 200, 60000002, 10, 55, 0, false),
            });
            var entries = ProtoMessage.Parse(list).Fields.Where(f => f.FieldNumber == 1).ToList();
            Assert.Equal(2, entries.Count);
            Assert.StartsWith(Hex(GuildProtocol.BuildMember(Recorder(), "Hero-Example", 354, 60000001, breed: 11,
                                                            achievementPoints: 8094)), Hex(list));
        }

        /// <summary>
        /// And the same, brought up to date after a contribution and the note «hola»: the jgz of «muchas
        /// acciones», with the guild coins {10, 10} and the note with its time.
        /// </summary>
        [Fact]
        public void The_updated_row_carries_the_note_and_the_contributions()
        {
            Assert.Equal("124e0a45120c4865726f2d4578616d706c6518e2022a321001180120faf8ffbdff33300b3a1a089e3f1204080a100a1803420d0a04686f6c6110808b88bfff334202080150818ece1c1081f092cbdd08",
                         Hex(GuildProtocol.BuildMemberUpdated(Recorder("hola", 1786570212736), "Hero-Example", 354, 60000001,
                                                              breed: 11, achievementPoints: 8094, contributed: 10)));
        }

        /// <summary>
        /// The ranks after renaming 1, touching 2's permissions and creating a fifth in the
        /// third position: the captured policy, with 4 shifted to the fourth. The independent
        /// 192-byte compatibility fixture lives in the protocol suite.
        /// </summary>
        [Fact]
        public void The_edited_ranks_use_persisted_names_rights_and_display_order()
        {
            var guild = GuildStore.Create(7001, "Jondo", 165, 8, 16744448, 9476018);
            var ranks = GuildStore.Ranks(guild.Id);
            Assert.Equal(4, ranks.Count);
            Assert.Equal(new[] { 1, 2, 3, 4 }, GuildRankMessages.Snapshot(ranks).Ranks.Select(rank => rank.Id));

            // jct: «Tesorero», with f4 empty, which leaves the icon as it was.
            var first = ranks.Single(r => r.Id == 1);
            first.Name = "Tesorero";
            GuildStore.SaveRank(first);

            // jct: «Test rango»; then jck with rank 2's new list.
            var second = ranks.Single(r => r.Id == 2);
            second.Name = "Test rango";
            second.Rights = new byte[] { 0x01, 0x05, 0x06, 0x26, 0x07, 0x27, 0x08, 0x28, 0x29, 0x0d, 0x0e, 0x0f, 0x17, 0x18, 0x19 };
            GuildStore.SaveRank(second);

            // jcv: «Rango personalizado», icon 102, in position 3.
            var created = GuildStore.CreateRank(guild.Id, "Rango personalizado", 102, 3);
            Assert.Equal(5, created.Id);

            var snapshot = GuildRankService.Live.Snapshot(guild.Id).Ranks;
            Assert.Equal(new[] { 1, 2, 3, 5, 4 }, snapshot.Select(rank => rank.Id));
            Assert.Equal(new[] { "Tesorero", "Test rango", "guild.rank.3.name", "Rango personalizado", "guild.rank.4.name" },
                snapshot.Select(rank => rank.Name));
            Assert.Equal(new[] { 116, 115, 114, 102, 117 }, snapshot.Select(rank => rank.OrderAndGfx!.GfxId));
            Assert.Equal(new[] { 0, 1, 2, 3, 4 }, snapshot.Select(rank => rank.OrderAndGfx!.Order));
            Assert.Equal(new[] { 1, 5, 6, 38, 7, 39, 8, 40, 41, 13, 14, 15, 23, 24, 25 }, snapshot[1].PrivateInformation!.Rights);
            Assert.True(snapshot[3].PrivateInformation!.Modifiable);
            Assert.Empty(snapshot[3].PrivateInformation!.Rights);
        }

        /// <summary>The «Jondo» journal: the founding and Player-Alpha's two lines.</summary>
        [Fact]
        public void The_log_is_the_capture()
        {
            // The founding is the guild's first chest tab; then an application turned down and a joining.
            var entries = new List<GuildStore.LogEntry>
            {
                new() { GuildId = 42043, WhenMs = 1786567982200, Field = GuildLogProtocol.ChestTabUnlocked },
                new()
                {
                    GuildId = 42043, WhenMs = 1786568036573, Field = GuildLogProtocol.PlayerFlow,
                    Details = GuildLogProtocol.PlayerFlowLine(180000000005, "Player-Alpha", GuildLogProtocol.Flow.ApplicationRefused),
                },
                new()
                {
                    GuildId = 42043, WhenMs = 1786568106484, Field = GuildLogProtocol.PlayerFlow,
                    Details = GuildLogProtocol.PlayerFlowLine(180000000005, "Player-Alpha", GuildLogProtocol.Flow.Joined),
                },
            };
            Assert.Equal("0a0f58bbc8028a01009801f8f8ffbdff330a2658bbc8029801dda183beff33a20117108590d8c69e051a0c506c617965722d416c70686120020a2458bbc8029801f4c387beff33a20115108590d8c69e051a0c506c617965722d416c706861",
                         Hex(GuildLogProtocol.BuildLog(entries)));

            // And the store writes it on its own: founding is one line, joining is another.
            var guild = GuildStore.Create(7001, "Jondo", 165, 8, 16744448, 9476018);
            GuildStore.Join(7002, guild.Id);
            var log = GuildStore.LogOf(guild.Id);
            Assert.Equal(2, log.Count);
            Assert.Equal(GuildLogProtocol.ChestTabUnlocked, log[0].Field);
            Assert.Equal(GuildLogProtocol.PlayerFlow, log[1].Field);
            Assert.Equal(7002, log[1].CharacterId);
        }

        /// <summary>
        /// Each line goes in its logbook tab -- jil f1 general, f2 missions, f3 raids, f5 chest --
        /// with the fields the client reads, numbered as its classes number them.
        /// </summary>
        [Fact]
        public void The_journal_s_lines_go_in_their_tabs()
        {
            var entries = new List<GuildStore.LogEntry>
            {
                Line(GuildLogProtocol.LevelUp, GuildLogProtocol.LevelUpLine(3), 1),
                Line(GuildLogProtocol.MissionComplete, GuildLogProtocol.MissionCompleteLine(7, "Uno", 417), 2),
                Line(GuildLogProtocol.RaidPurchase, GuildLogProtocol.RaidPurchaseLine(7, "Uno", 2, "u-1"), 3),
                Line(GuildLogProtocol.Chest, GuildLogProtocol.ChestLine(7, "Uno", GuildLogProtocol.ChestMove.Withdraw, 2,
                     ConnectionProtocol.ItemBody(289, Array.Empty<Equipment.ItemEffect>(), 2, 99), 1, 0), 4),
            };
            var tabs = ProtoMessage.Parse(GuildLogProtocol.BuildLog(entries)).Fields.Select(f => f.FieldNumber).ToList();
            Assert.Equal(new[] { 1, 2, 3, 5 }, tabs);

            // lkh { f2 3 }; lks { f1 7, f2 "Uno", f3 417 }; lkc's withdrawal: f2 2, f3 the item, f4 tab 1, f7, f8, f9 1.
            Assert.Equal("1003", Hex(GuildLogProtocol.LevelUpLine(3)));
            Assert.Equal("0807" + "1203556e6f" + "18a103", Hex(GuildLogProtocol.MissionCompleteLine(7, "Uno", 417)));
            var chest = ProtoMessage.Parse(GuildLogProtocol.ChestLine(7, "Uno", GuildLogProtocol.ChestMove.Withdraw, 2,
                ConnectionProtocol.ItemBody(289, Array.Empty<Equipment.ItemEffect>(), 2, 99), 1, 0)).Fields;
            Assert.Equal(new[] { 2, 3, 4, 7, 8, 9 }, chest.Select(f => f.FieldNumber));
            Assert.Equal(1, chest.Single(f => f.FieldNumber == 9).VarIntValue);

            // lkn: the rank { f2 name, f5 id }, who, by whom, and both names.
            var rank = ProtoMessage.Parse(GuildLogProtocol.PlayerRankLine(3, "Tesorero", 8, "Dos", 7, "Uno")).Fields;
            Assert.Equal(new[] { 1, 2, 3, 4, 5 }, rank.Select(f => f.FieldNumber));
            Assert.Equal(8, rank.Single(f => f.FieldNumber == 2).VarIntValue);
        }

        private static GuildStore.LogEntry Line(int field, byte[] details, long when)
            => new() { GuildId = 1, WhenMs = when, Field = field, Details = details, Tab = GuildLogProtocol.TabOf(field) };

        /// <summary>
        /// The store keeps what happens: a level reached (and the chest tab it brings), a member
        /// leaving, and a line written before the journal had its cases, read back as one.
        /// </summary>
        [Fact]
        public void The_store_keeps_the_guild_s_doings()
        {
            var guild = GuildStore.Create(7101, "Bitácora", 165, 8, 16744448, 9476018);
            GuildStore.Join(7102, guild.Id);
            GuildStore.AddGuildExperience(guild.Id, 50_000_000);
            GuildStore.Leave(7102);

            var log = GuildStore.LogOf(guild.Id);
            Assert.Contains(log, e => e.Field == GuildLogProtocol.LevelUp);
            Assert.True(log.Count(e => e.Field == GuildLogProtocol.ChestTabUnlocked) >= 2);   // the founding's and a level's
            var left = log.Last(e => e.Field == GuildLogProtocol.PlayerFlow);
            Assert.Equal(7102, ProtoMessage.Parse(left.Details).Fields.Single(f => f.FieldNumber == 2).VarIntValue);
            Assert.Equal(1, ProtoMessage.Parse(left.Details).Fields.Single(f => f.FieldNumber == 4).VarIntValue);

            // An old row: Kind 1, no case -- a joining.
            using (var connection = new SqliteConnection(GuildStore.ConnectionStringOverride))
            {
                connection.Open();
                var insert = connection.CreateCommand();
                insert.CommandText = "INSERT INTO GuildLog (GuildId, WhenMs, Kind, CharacterId, Name) VALUES ($g, 1, 1, 7103, 'Viejo');";
                insert.Parameters.AddWithValue("$g", guild.Id);
                insert.ExecuteNonQuery();
            }
            var old = GuildStore.LogOf(guild.Id).First();
            Assert.Equal(GuildLogProtocol.PlayerFlow, old.Field);
            Assert.Equal(GuildStore.LogTab.General, old.Tab);
            Assert.Equal(7103, ProtoMessage.Parse(old.Details).Fields.Single(f => f.FieldNumber == 2).VarIntValue);
        }

        /// <summary>
        /// Deleting a rank (jcn): its members go to the rank chosen and the ranks below go up one;
        /// the leader's, and the usual fourth without the removable flag, stay.
        /// </summary>
        [Fact]
        public void A_deleted_rank_hands_its_members_on()
        {
            var guild = GuildStore.Create(7201, "Rangos", 165, 8, 16744448, 9476018);
            GuildStore.Join(7202, guild.Id, rank: 2);
            var custom = GuildStore.CreateRank(guild.Id, "Temporal", 102, 2);

            Assert.Null(GuildStore.DeleteRank(guild.Id, GuildStore.RankLeader, 2));
            Assert.Null(GuildStore.DeleteRank(guild.Id, 4, 2));
            Assert.Equal(new long[] { 7202 }, GuildStore.DeleteRank(guild.Id, 2, 3));
            Assert.Equal(3, GuildStore.MemberOf(7202).Rank);

            var ranks = GuildStore.Ranks(guild.Id);
            Assert.DoesNotContain(ranks, r => r.Id == 2);
            Assert.Equal(Enumerable.Range(0, ranks.Count), ranks.Select(r => r.Order));
            Assert.Contains(ranks, r => r.Id == custom.Id);
        }

        /// <summary>
        /// The message of the day and the logbook's bulletin: kept per guild, and sent as the
        /// client reads them -- jhk { f2 notice }, jjf { f1 notice }, a notice { f1 text, f2 when,
        /// f3 author's name, f4 author }.
        /// </summary>
        [Fact]
        public void The_notices_are_kept_and_sent()
        {
            var guild = GuildStore.Create(7301, "Avisos", 165, 8, 16744448, 9476018);
            Assert.Null(GuildStore.NoticeOf(guild.Id, GuildStore.NoticeKind.MessageOfTheDay));
            GuildStore.SetNotice(guild.Id, GuildStore.NoticeKind.MessageOfTheDay, "Hola", 7301, "Jefe");
            var motd = GuildStore.NoticeOf(guild.Id, GuildStore.NoticeKind.MessageOfTheDay);
            Assert.Equal("Hola", motd.Text);
            Assert.Null(GuildStore.NoticeOf(guild.Id, GuildStore.NoticeKind.Bulletin));

            var sent = new GuildStore.Notice("Hola", 5, 7, "Jefe");
            Assert.Equal("1210" + "0a04486f6c61" + "1005" + "1a044a656665" + "2007", Hex(GuildProtocol.BuildMessageOfTheDay(sent)));
            Assert.Equal("0a10" + "0a04486f6c61" + "1005" + "1a044a656665" + "2007", Hex(GuildProtocol.BuildBulletin(sent)));
        }

        /// <summary>The directory sheet, empty and written, both from the capture of founding «Jondo».</summary>
        [Fact]
        public void The_profile_is_the_capture()
        {
            Assert.Equal(Hex(GuildProtocol.BuildEmptyGuildMessage(42043)), Hex(GuildProtocol.BuildProfile(Jondo, null, "Hero-Example")));
            Assert.Equal("120b1801280232010150bbc802", Hex(GuildProtocol.BuildProfile(Jondo, null, "")));

            var written = new GuildStore.Profile
            {
                GuildId = 42043, WhenMs = 1786568020893, Description = "Hola!", MinLevel = 20, MaxLevel = 100,
                Tags = new byte[] { 0x1d, 0x13, 0x08, 0x0e, 0x1a, 0x12, 0x0b, 0x09, 0x10 },
                F5 = 2, F6 = new byte[] { 0x03 }, Title = "Dragon Ball",
            };
            Assert.Equal("1241089da782beff331205486f6c6121181422091d13080e1a120b09102802320103420c4865726f2d4578616d706c65486450bbc8026a0b447261676f6e2042616c6c",
                         Hex(GuildProtocol.BuildProfile(Jondo, written, "Hero-Example")));

            // And the store keeps it whole.
            GuildStore.SaveProfile(written);
            var back = GuildStore.ProfileOf(42043);
            Assert.Equal("Dragon Ball", back.Title);
            Assert.Equal(written.Tags, back.Tags);
            Assert.Equal(20, back.MinLevel);
        }

        /// <summary>The contributions left and a new guild's jff, as in the opening.</summary>
        [Fact]
        public void The_window_opening_frames_are_the_capture()
        {
            Assert.Equal("0805", Hex(GuildProtocol.BuildContributionsLeft(5)));
            Assert.Equal("", Hex(GuildProtocol.BuildContributionsLeft(0)));
            Assert.Equal("1a00", Hex(GuildProtocol.BuildWeek(null, 250, 0)));
        }

        /// <summary>
        /// Belonging, as world entry says it (jhe): the capture's frame of «Jondo» after two
        /// contributions ("entrar a combate-desconectarse-reconectar", frame 180), all but its
        /// unknown f4.
        /// </summary>
        [Fact]
        public void Belonging_is_the_world_entrys_jhe_and_not_the_jgw_of_joining()
        {
            var membership = GuildStateMessages.Membership(Jondo, rankId: 1, contributed: 20);
            Assert.Equal(42043, membership.GuildInformation!.Id);
            Assert.Equal("Jondo", membership.GuildInformation.Name);
            Assert.Equal(1, membership.RankId);
            Assert.Equal(20, membership.GuildTokens);
            Assert.Equal(0, membership.UnknownUsedContextValue);
        }

        /// <summary>The next weekly reset (jez), against the five captures that ask for it.</summary>
        [Theory]
        [InlineData("2026-08-09T16:56:00Z", "2026-08-11T05:00:00Z")]
        [InlineData("2026-08-12T21:50:00Z", "2026-08-18T05:00:00Z")]
        [InlineData("2026-08-15T18:24:00Z", "2026-08-18T05:00:00Z")]
        [InlineData("2026-08-29T21:03:00Z", "2026-09-01T05:00:00Z")]
        [InlineData("2026-09-01T23:12:00Z", "2026-09-08T05:00:00Z")]
        public void The_week_starts_again_on_tuesday_at_five(string asked, string reset)
        {
            var now = DateTime.Parse(asked, null, System.Globalization.DateTimeStyles.AdjustToUniversal);
            Assert.Equal(reset, GuildProtocol.BuildWeeklyReset(now).ResetDate);
        }

        /// <summary>The tabs of a guild with nothing in them, as the captures answer them.</summary>
        [Fact]
        public void An_empty_tab_is_the_captures()
        {
            Assert.Equal("0a00", Hex(GuildProtocol.BuildEmptyTab(1)));   // jfz, jgq, ice
            Assert.Equal("1a00", Hex(GuildProtocol.BuildEmptyTab(3)));   // jei
            Assert.Equal("", Hex(GuildProtocol.BuildEmptyTab(0)));       // jfs, jfr, hxm
        }

        /// <summary>
        /// Opening the window answers the chest's tabs, the bare jiy the header and the
        /// contributions left, as the captures pair them, and the members only when the jml asks
        /// for them -- never the jgw of joining, which printed "acabas de unirte al gremio" at
        /// every tab.
        /// </summary>
        [Fact]
        public async Task Opening_the_window_never_says_you_have_just_joined()
        {
            GuildStore.Create(7003, "Jondo", 165, 8, 16744448, 9476018);
            await using var wire = await global::Jondo.Unity.Tests.Combat.PortalTests.Wire.Open(7003);
            using (SessionContext.Push(wire.Session))
            {
                await GuildHandler.OpenWindowAsync(wire.Session.Stream!);
                await GuildHandler.TabAsync(wire.Session.Stream!, ConnectionProtocol.Push(Op.Jiy, Array.Empty<byte>()));
                await GuildHandler.MembersAsync(wire.Session.Stream!, ConnectionProtocol.Push(Op.Jml, Array.Empty<byte>()));
            }

            var ops = (await wire.Drain()).Select(f => f.Op).ToList();
            Assert.Equal(new[] { Op.Ivl, Op.Jhh, Op.Jla }, ops);
        }

        /// <summary>
        /// A guild's name clicked (jhp) opens its card (jie): the guild as the directory lists it
        /// in f3 and its founding, in seconds, in f2.
        /// </summary>
        [Fact]
        public async Task A_guild_s_card_is_the_directory_s_guild_and_its_founding()
        {
            var guild = GuildStore.Create(7003, "Jondo", 165, 8, 16744448, 9476018);
            await using var wire = await global::Jondo.Unity.Tests.Combat.PortalTests.Wire.Open(7004);
            using (SessionContext.Push(wire.Session))
            {
                await GuildHandler.ShowGuildAsync(wire.Session.Stream!,
                    ConnectionProtocol.Push(Op.Jhp, Pb.New().Var(1, guild.Id).Build()));
                await GuildHandler.ShowGuildAsync(wire.Session.Stream!,
                    ConnectionProtocol.Push(Op.Jhp, Pb.New().Var(1, guild.Id + 1000).Build()));
            }

            var frames = await wire.Drain();
            Assert.Equal(new[] { Op.Jie }, frames.Select(f => f.Op));
            var jie = ProtoMessage.Parse(frames[0].Payload);
            long founded = jie.Fields.Single(f => f.FieldNumber == 2).VarIntValue;
            Assert.InRange(founded, DateTimeOffset.UtcNow.AddMinutes(-5).ToUnixTimeSeconds(), DateTimeOffset.UtcNow.ToUnixTimeSeconds() + 1);
            var les = ProtoMessage.Parse(jie.Fields.Single(f => f.FieldNumber == 3).BytesValue);
            Assert.Equal(guild.Id, les.Fields.Single(f => f.FieldNumber == 2).VarIntValue);
            Assert.Equal("Jondo", System.Text.Encoding.UTF8.GetString(les.Fields.Single(f => f.FieldNumber == 3).BytesValue));
            var leo = ProtoMessage.Parse(ProtoMessage.Parse(les.Fields.Single(f => f.FieldNumber == 1).BytesValue)
                                             .Fields.Single(f => f.FieldNumber == 1).BytesValue);
            Assert.Equal(7003, leo.Fields.Single(f => f.FieldNumber == 1).VarIntValue);   // the leader
            Assert.Equal(1, leo.Fields.Single(f => f.FieldNumber == 7).VarIntValue);      // one member

            // Each member carries his rank (f6) with its f4, never the details (f5): gl.mwk reads
            // the rank without a null check, and the details made the client throw.
            foreach (var member in jie.Fields.Where(f => f.FieldNumber == 1))
            {
                var lpe = ProtoMessage.Parse(ProtoMessage.Parse(member.BytesValue).Fields.Single(f => f.FieldNumber == 1).BytesValue);
                Assert.DoesNotContain(lpe.Fields, f => f.FieldNumber == 5);
                var ldt = ProtoMessage.Parse(lpe.Fields.Single(f => f.FieldNumber == 6).BytesValue);
                Assert.Contains(ldt.Fields, f => f.FieldNumber == 4);
            }
        }

        /// <summary>
        /// The card's members carry their rank, the f4 included even for a rank the guild does not
        /// have: the client reads both without looking.
        /// </summary>
        [Fact]
        public void A_guild_card_member_carries_his_rank()
        {
            var guild = new GuildStore.Guild { Id = 1, Name = "Jondo", Level = 1 };
            var member = new GuildStore.Member { CharacterId = 42, GuildId = 1, Rank = 1 };
            var row = new GuildProtocol.MemberRow(member, "Sley", 200, 1, 8, 0, 0, true);
            var rank = GuildStore.DefaultRanks(1).First();
            var entry = new GuildProtocol.DirectoryEntry { Guild = guild, LeaderId = 42, LeaderName = "Sley", Members = 1 };

            foreach (var given in new[] { rank, null })
            {
                var jie = ProtoMessage.Parse(GuildProtocol.BuildGuildCard(entry, new[] { (row, given) }, 0));
                var lpg = ProtoMessage.Parse(jie.Fields.Single(f => f.FieldNumber == 1).BytesValue);
                Assert.Equal(42, lpg.Fields.Single(f => f.FieldNumber == 2).VarIntValue);
                var lpe = ProtoMessage.Parse(lpg.Fields.Single(f => f.FieldNumber == 1).BytesValue);
                Assert.Equal("Sley", System.Text.Encoding.UTF8.GetString(lpe.Fields.Single(f => f.FieldNumber == 2).BytesValue));
                Assert.Equal(200, lpe.Fields.Single(f => f.FieldNumber == 3).VarIntValue);
                var ldt = ProtoMessage.Parse(lpe.Fields.Single(f => f.FieldNumber == 6).BytesValue);
                Assert.Contains(ldt.Fields, f => f.FieldNumber == 4);
            }
        }

        /// <summary>
        /// A member coming on line is shown on line in the others' lists (jgz), and those who asked
        /// to be warned (jha) are told (jhd: him, on line); after jhf, only the list.
        /// </summary>
        [Fact]
        public async Task A_member_coming_on_line_warns_who_asked()
        {
            const long account = 8_900_000_077;
            long comer = DatabaseManager.CreateCharacter(account, 1, "Avisado-Uno", 8, 0, 1, new long[] { -1, -1, -1, -1, -1 },
                                                         191105026, 1, 0, 0, Array.Empty<(int, int)>());
            long watcher = DatabaseManager.CreateCharacter(account, 1, "Avisado-Dos", 8, 0, 1, new long[] { -1, -1, -1, -1, -1 },
                                                           191105026, 1, 0, 0, Array.Empty<(int, int)>());
            try
            {
                var guild = GuildStore.Create(watcher, "Avisos", 165, 8, 16744448, 9476018);
                GuildStore.Join(comer, guild.Id);
                await using var wire = await global::Jondo.Unity.Tests.Combat.PortalTests.Wire.Open(watcher);
                using (SessionContext.Push(wire.Session))
                    await GuildHandler.WarnConnectionsAsync(true);
                await GuildHandler.TellConnectionAsync(comer, online: true);
                using (SessionContext.Push(wire.Session))
                    await GuildHandler.WarnConnectionsAsync(false);
                await GuildHandler.TellConnectionAsync(comer, online: true);

                var frames = await wire.Drain();
                Assert.Equal(new[] { Op.Jgz, Op.Jhd, Op.Jgz }, frames.Select(f => f.Op));
                Assert.Equal(Pb.New().Var(1, comer).Var(2, 1).Build(), frames[1].Payload);
            }
            finally
            {
                DatabaseManager.DeleteCharacter(comer, account);
                DatabaseManager.DeleteCharacter(watcher, account);
            }
        }

        /// <summary>
        /// «Unirse al gremio» (jhn f4) lets one in at once when the sheet recruits automatically (f5
        /// 1), and not when it takes applications.
        /// </summary>
        [Fact]
        public async Task A_guild_of_automatic_recruitment_lets_one_join_at_once()
        {
            var open = GuildStore.Create(7003, "Abierto", 165, 8, 16744448, 9476018);
            GuildStore.SaveProfile(new GuildStore.Profile { GuildId = open.Id, WhenMs = 1, F5 = GuildHandler.AutomaticRecruitment });
            var closed = GuildStore.Create(7005, "Cerrado", 165, 8, 16744448, 9476018);
            GuildStore.SaveProfile(new GuildStore.Profile { GuildId = closed.Id, WhenMs = 1, F5 = 2 });

            await using var wire = await global::Jondo.Unity.Tests.Combat.PortalTests.Wire.Open(7004);
            using (SessionContext.Push(wire.Session))
            {
                await GuildHandler.JoinDirectlyAsync(wire.Session.Stream!, ConnectionProtocol.Push(Op.Jhn, Pb.New().Var(4, closed.Id).Build()));
                Assert.Null(GuildStore.GuildOf(7004));
                await GuildHandler.JoinDirectlyAsync(wire.Session.Stream!, ConnectionProtocol.Push(Op.Jhn, Pb.New().Var(4, open.Id).Build()));
            }
            Assert.Equal(open.Id, GuildStore.GuildOf(7004)?.Id);
            Assert.Contains(Op.Jij, (await wire.Drain()).Select(f => f.Op));
        }

        /// <summary>The guild coins come from the contributions: ten for each one, in total.</summary>
        [Fact]
        public void Contributions_become_gremichas()
        {
            var guild = GuildStore.Create(7001, "Jondo", 165, 8, 16744448, 9476018);
            Assert.Equal(0, GuildStore.ContributedBy(7001));
            GuildStore.Contribute(7001, guild.Id);
            Assert.Equal(10, GuildStore.ContributedBy(7001));
            GuildStore.Contribute(7001, guild.Id);
            Assert.Equal(20, GuildStore.ContributedBy(7001));
        }
    }
}
