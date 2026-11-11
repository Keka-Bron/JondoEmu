using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Jondo.Unity.Server;
using Jondo.Unity.Server.Handlers;
using Jondo.Unity.Server.Managers;
using Jondo.Unity.Server.Network;
using Microsoft.Data.Sqlite;
using Xunit;

namespace Jondo.Unity.Tests.World
{
    /// <summary>
    /// Running an alliance from its window: ranks and rights, the message and the bulletin,
    /// applications, the recruitment card and the warning when a member connects.
    /// </summary>
    /// <remarks>
    /// No capture shows any of it; the requests are read off the client's own code (fgu's action
    /// handlers and the windows that send them, decompiled in Ghidra), so these tests pin the
    /// rules this server keeps and that what it writes reads back the way the client reads it.
    /// </remarks>
    [Collection("alliances")]
    public class AllianceManagementTests : IDisposable
    {
        private readonly string _file;

        public AllianceManagementTests()
        {
            _file = Path.Combine(Path.GetTempPath(), $"jondo-alianza-gestion-{Guid.NewGuid():N}.db");
            AllianceStore.ConnectionStringOverride = $"Data Source={_file}";
        }

        public void Dispose()
        {
            AllianceStore.ConnectionStringOverride = null;
            TestDatabases.ReleasePool(_file);
            try { File.Delete(_file); } catch (IOException) { }
        }

        // ─── Ranks ───────────────────────────────────────────────────────────────

        /// <summary>
        /// A rank created after another goes right under it; the ones below move down one, and the
        /// newcomers' stays last.
        /// </summary>
        [Fact]
        public void A_new_rank_goes_under_the_one_it_was_created_from()
        {
            var alliance = AllianceStore.Create(9101, "Jondo Unidos", "JON", 1, 2, 3, 0, 4);
            var created = AllianceStore.CreateRank(alliance.Id, "Tesorero", 120, afterRankId: 2);

            Assert.Equal(5, created.Id);
            Assert.True(created.Flag);
            Assert.Empty(created.Rights);
            var ranks = AllianceStore.Ranks(alliance.Id);
            Assert.Equal(new[] { 1, 2, 5, 3, 4 }, ranks.Select(r => r.Id));
            Assert.Equal(new[] { 0, 1, 2, 3, 4 }, ranks.Select(r => r.Order));
        }

        /// <summary>
        /// Removing a rank sends its members to the one chosen and closes the gap; the leader's and
        /// the newcomers' cannot go.
        /// </summary>
        [Fact]
        public void Removing_a_rank_moves_its_members()
        {
            var alliance = AllianceStore.Create(9201, "Jondo Unidos", "JON", 1, 2, 3, 0, 4);
            AllianceStore.Join(9202, alliance.Id, rank: 3);

            Assert.False(AllianceStore.RemoveRank(alliance.Id, AllianceStore.RankLeader, 2));
            Assert.False(AllianceStore.RemoveRank(alliance.Id, AllianceStore.RankNewcomer, 2));
            Assert.True(AllianceStore.RemoveRank(alliance.Id, 3, 2));

            Assert.Equal(2, AllianceStore.RankOf(9202));
            var ranks = AllianceStore.Ranks(alliance.Id);
            Assert.Equal(new[] { 1, 2, 4 }, ranks.Select(r => r.Id));
            Assert.Equal(new[] { 0, 1, 2 }, ranks.Select(r => r.Order));
        }

        /// <summary>
        /// «Gestionar los rangos y derechos inferiores al mío»: the leader manages any rank; a member
        /// with the right only the ones below his own; one without it none.
        /// </summary>
        [Fact]
        public void Ranks_are_managed_from_above()
        {
            var alliance = AllianceStore.Create(9301, "Jondo Unidos", "JON", 1, 2, 3, 0, 4);
            AllianceStore.Join(9302, alliance.Id, rank: 2);
            AllianceStore.Join(9303, alliance.Id, rank: 3);
            var ranks = AllianceStore.Ranks(alliance.Id);
            var lieutenant = ranks.Single(r => r.Id == 2);
            lieutenant.Rights = new List<int> { AllianceData.ManageRanks };
            AllianceStore.SaveRank(lieutenant);
            ranks = AllianceStore.Ranks(alliance.Id);

            Assert.True(AllianceHandler.MayManageRank(9301, ranks.Single(r => r.Id == 2)));
            Assert.True(AllianceHandler.MayManageRank(9302, ranks.Single(r => r.Id == 3)));
            Assert.False(AllianceHandler.MayManageRank(9302, ranks.Single(r => r.Id == 2)));
            Assert.False(AllianceHandler.MayManageRank(9302, ranks.Single(r => r.Id == 1)));
            Assert.False(AllianceHandler.MayManageRank(9303, ranks.Single(r => r.Id == 4)));
        }

        /// <summary>A stored rank supplies the semantic management snapshot.</summary>
        [Fact]
        public void A_rank_snapshot_uses_the_stored_management_values()
        {
            var rank = new AllianceStore.Rank
            {
                Id = 6, Name = "Tesorero", Flag = true, Icon = 118, Order = 2, Rights = new() { 3, 18, 9 },
            };
            var sent = Assert.Single(AllianceRankMessages.Snapshot(new[] { rank }).Ranks);

            Assert.Equal(6, sent.Id);
            Assert.Equal("Tesorero", sent.Name);
            Assert.True(sent.PrivateInformation!.Modifiable);
            Assert.Equal(118, sent.OrderAndGfx!.GfxId);
            Assert.Equal(2, sent.OrderAndGfx.Order);
            Assert.Equal(new[] { 3, 18, 9 }, sent.PrivateInformation.Rights);
        }

        /// <summary>A rank's name cannot be empty, too long or a translation key in disguise.</summary>
        [Theory]
        [InlineData("Tesorero", true)]
        [InlineData("", false)]
        [InlineData("alliance.rank.1.name", false)]
        [InlineData("Un nombre de rango demasiado largo para caber", false)]
        public void A_rank_name_is_what_the_editor_allows(string name, bool valid)
            => Assert.Equal(valid, AllianceHandler.IsValidRankName(name));

        // ─── The message and the bulletin ────────────────────────────────────────

        /// <summary>
        /// A notice never written carries the alliance's founding date, as joining «E X A M P L E»
        /// did; one written carries its text, when and who, and goes out with all four.
        /// </summary>
        [Fact]
        public void Notices_keep_who_wrote_them()
        {
            var alliance = AllianceStore.Create(9401, "Jondo Unidos", "JON", 1, 2, 3, 0, 4);
            var fresh = AllianceStore.NoticeOf(alliance, AllianceStore.NoticeMessage);
            Assert.Equal("", fresh.Text);
            Assert.Equal(alliance.CreatedUtcS, fresh.WhenS);

            AllianceStore.SetNotice(alliance.Id, AllianceStore.NoticeBulletin, new AllianceStore.Notice("Hola", 1800000000, "Member-Three", 9401));
            var bulletin = AllianceStore.NoticeOf(alliance, AllianceStore.NoticeBulletin);
            Assert.Equal(new AllianceStore.Notice("Hola", 1800000000, "Member-Three", 9401), bulletin);
            Assert.Equal(alliance.CreatedUtcS, AllianceStore.NoticeOf(alliance, AllianceStore.NoticeMessage).WhenS);

            var snapshot = Assert.IsType<Jondo.Unity.Protocol.Game.AllianceBulletinEvent>(
                AllianceNoticeMessages.Snapshot(AllianceStore.NoticeBulletin, bulletin));
            Assert.Equal(new Jondo.Unity.Protocol.Game.SocialNoticeInformation("Hola", 1800000000, "Member-Three", 9401), snapshot.BulletinInfo);
        }

        // ─── Applications ────────────────────────────────────────────────────────

        /// <summary>
        /// One application per character: applying elsewhere replaces it. An alliance lists its own
        /// oldest first, and a dissolved alliance takes them with it.
        /// </summary>
        [Fact]
        public void One_application_at_a_time()
        {
            var first = AllianceStore.Create(9501, "Jondo Unidos", "JON", 1, 2, 3, 0, 4);
            var second = AllianceStore.Create(9502, "Los Del Templo", "TEM", 1, 2, 3, 0, 4);

            AllianceStore.Apply(9510, first.Id, "Hola");
            AllianceStore.Apply(9511, first.Id, "Buenas");
            AllianceStore.Apply(9510, second.Id, "Mejor aquí");

            Assert.Equal(second.Id, AllianceStore.ApplicationOf(9510).AllianceId);
            Assert.Equal(new long[] { 9511 }, AllianceStore.Applications(first.Id).Select(a => a.CharacterId));
            Assert.Equal("Mejor aquí", Assert.Single(AllianceStore.Applications(second.Id)).Message);

            AllianceStore.Leave(9502);
            Assert.Null(AllianceStore.ApplicationOf(9510));
        }

        /// <summary>
        /// An application (lpo) carries when, the message and the applicant's sheet in the fields
        /// ih and ig read: f1, f3, and f4 { f1 class, f3 sex, f4 account, f5 name, f7 character,
        /// f8 nickname, f9 status, f10 level }.
        /// </summary>
        [Fact]
        public void An_application_has_the_shape_the_client_reads()
        {
            var row = new AllianceProtocol.ApplicationRow(
                new AllianceStore.Application(9601, 1, "Hola", 1786000000000), "Member-Three", 200, 7777, 2, 1, true);
            var mbb = ProtoMessage.Parse(AllianceProtocol.BuildApplicationsMoved(AllianceProtocol.ApplicationGone, row, 9001));

            Assert.Equal(1, mbb.Fields.Single(f => f.FieldNumber == 2).VarIntValue);
            Assert.Equal(9001, mbb.Fields.Single(f => f.FieldNumber == 4).VarIntValue);
            var lpo = ProtoMessage.Parse(mbb.Fields.Single(f => f.FieldNumber == 3).BytesValue);
            Assert.Equal(1786000000000, lpo.Fields.Single(f => f.FieldNumber == 1).VarIntValue);
            var lgm = ProtoMessage.Parse(lpo.Fields.Single(f => f.FieldNumber == 4).BytesValue);
            Assert.Equal(new[] { 1, 3, 4, 5, 7, 8, 9, 10 }, lgm.Fields.Select(f => f.FieldNumber));
            Assert.Equal(9601, lgm.Fields.Single(f => f.FieldNumber == 7).VarIntValue);
            Assert.Equal(200, lgm.Fields.Single(f => f.FieldNumber == 10).VarIntValue);

            // The page: f1 how many are pending -- the window's counter --, then the page.
            var maz = ProtoMessage.Parse(AllianceProtocol.BuildApplications(3, new[] { row }, 8, 0));
            Assert.Equal(3, maz.Fields.Single(f => f.FieldNumber == 1).VarIntValue);
            Assert.Single(maz.Fields, f => f.FieldNumber == 2);
        }

        // ─── The recruitment card ────────────────────────────────────────────────

        /// <summary>The card written with the recruitment block reads back into a profile, as lzy brings it.</summary>
        [Fact]
        public void The_recruitment_card_reads_back_as_written()
        {
            var alliance = new AllianceStore.Alliance { Uuid = Guid.NewGuid().ToString(), Name = "Jondo Unidos", Tag = "JON" };
            var written = new AllianceStore.Profile
            {
                Recruitment = 2, MinLevel = 120, Description = "Gente maja", Title = "Buscamos",
                Tags = new() { 4, 17, 26 }, Languages = new() { 0, 3 },
            };
            var read = new AllianceStore.Profile();
            AllianceProtocol.ReadRecruitment(AllianceProtocol.Recruitment(alliance, written, "Member-Three").Build(), read);

            Assert.Equal(2, read.Recruitment);
            Assert.Equal(120, read.MinLevel);
            Assert.Equal("Gente maja", read.Description);
            Assert.Equal("Buscamos", read.Title);
            Assert.Equal(new[] { 4, 17, 26 }, read.Tags);
            Assert.Equal(new[] { 0, 3 }, read.Languages);
        }

        // ─── The member list, seen by the others ─────────────────────────────────

        /// <summary>A member gone (mby) is his character in f1, which is all fgv reads of it.</summary>
        [Fact]
        public void A_member_gone_is_his_character()
            => Assert.Equal(new byte[] { 0x08, 0x84, 0xf8, 0xa9, 0x80, 0xa9, 0x19 }, AllianceProtocol.BuildMemberGone(870000000004));

        // ─── Invitations, name and emblem ────────────────────────────────────────

        /// <summary>
        /// The invitation as fvl reads it: the invited sees «X te invita» with the alliance (mbs);
        /// whoever invites waits (mbq 1) until it is refused (2) or accepted (3).
        /// </summary>
        [Fact]
        public void Invitations_have_the_shape_the_client_reads()
        {
            var alliance = new AllianceStore.Alliance { Uuid = Guid.NewGuid().ToString(), Name = "Jondo Unidos", Tag = "JON" };
            var mbs = ProtoMessage.Parse(AllianceProtocol.BuildInvited("Member-Three", alliance));
            Assert.Equal("Member-Three", System.Text.Encoding.UTF8.GetString(mbs.Fields.Single(f => f.FieldNumber == 1).BytesValue));
            Assert.Equal(AllianceProtocol.Block(alliance).Build(), mbs.Fields.Single(f => f.FieldNumber == 2).BytesValue);

            var mbq = ProtoMessage.Parse(AllianceProtocol.BuildInvitationForInviter("Member-One", "Member-Three",
                                                                                  AllianceProtocol.InvitationSent));
            Assert.Equal(1, mbq.Fields.Single(f => f.FieldNumber == 3).VarIntValue);
            Assert.Equal(new byte[] { 0x08, 0x03 }, AllianceProtocol.BuildInvitationForInvited(AllianceProtocol.InvitationAccepted));
        }

        /// <summary>
        /// The editor's mode travels as (f1 emblem, f2 name): fvl opens the name alone with f2 (its
        /// mode 1) and the emblem alone with f1 (its mode 2).
        /// </summary>
        [Fact]
        public void The_modification_opens_the_editor_in_its_mode()
        {
            Assert.Equal(new byte[] { 0x10, 0x01 }, AllianceProtocol.BuildModificationStarted(name: true, emblem: false));
            Assert.Equal(new byte[] { 0x08, 0x01 }, AllianceProtocol.BuildModificationStarted(name: false, emblem: true));
            Assert.Empty(AllianceProtocol.BuildModificationResult(AllianceProtocol.CreationOk));
        }

        /// <summary>A potion's change stays: the new name and tag, or the new emblem.</summary>
        [Fact]
        public void Name_and_emblem_change()
        {
            var alliance = AllianceStore.Create(9801, "Jondo Unidos", "JON", 1, 2, 3, 0, 4);
            var renamed = AllianceStore.Rename(alliance.Id, "Jondo Reunidos", "JOR");
            Assert.Equal("Jondo Reunidos", renamed.Name);
            Assert.Equal("JOR", renamed.Tag);
            Assert.Equal(alliance.Uuid, renamed.Uuid);

            var redrawn = AllianceStore.SetEmblem(alliance.Id, 408, 27, 3116544, 0, 15859463);
            Assert.Equal((408, 27, 3116544, 15859463),
                         (redrawn.EmblemSymbol, redrawn.EmblemSymbolColor, redrawn.EmblemBackground, redrawn.EmblemSymbolRgb));
        }

        /// <summary>The option to be warned when a member connects is kept per character.</summary>
        [Fact]
        public void The_connection_warning_is_kept()
        {
            Assert.False(AllianceStore.WarnsOnConnection(9701));
            AllianceStore.SetWarnsOnConnection(9701, true);
            AllianceStore.SetWarnsOnConnection(9701, true);
            Assert.True(AllianceStore.WarnsOnConnection(9701));
            AllianceStore.SetWarnsOnConnection(9701, false);
            Assert.False(AllianceStore.WarnsOnConnection(9701));
        }

        /// <summary>
        /// The creation's results are the ones the client's mch handler words (fgv.bevj in Ghidra):
        /// 6 requirementUnmet, 8 invalidTag, 9 alreadyUsedTag.
        /// </summary>
        [Fact]
        public void The_creation_results_are_the_clients()
        {
            Assert.Equal(1, AllianceProtocol.CreationInvalidName);
            Assert.Equal(2, AllianceProtocol.CreationAlreadyInAlliance);
            Assert.Equal(3, AllianceProtocol.CreationNameUsed);
            Assert.Equal(6, AllianceProtocol.CreationRequirementUnmet);
            Assert.Equal(8, AllianceProtocol.CreationInvalidTag);
            Assert.Equal(9, AllianceProtocol.CreationTagUsed);
        }
    }
}
