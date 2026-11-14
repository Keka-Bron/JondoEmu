using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Jondo.Unity.Protocol;
using Jondo.Unity.Server;
using Jondo.Unity.Server.Handlers;
using Jondo.Unity.Server.Managers;
using Jondo.Unity.Server.Network;
using Jondo.Unity.World.Fights;
using Microsoft.Data.Sqlite;
using Xunit;

namespace Jondo.Unity.Tests.World
{
    /// <summary>
    /// The conquests broadcast to the whole world (ifa, iew): they run alone, or another test's
    /// pipe would read a prism it never asked about.
    /// </summary>
    [CollectionDefinition("conquests", DisableParallelization = true)]
    public class ConquestsCollection { }

    /// <summary>
    /// A prism beaten: weakened, vulnerable, its zone's King of the Hill, and the winner's
    /// priority (Translations 354323, 1149316, 1149317).
    /// </summary>
    [Collection("conquests")]
    public class ConquestTests : IDisposable
    {
        private readonly string _file;

        /// <summary>Three maps of subarea 838, and the prism stands on the first.</summary>
        private const long PrismMap = 144441344, OtherMap = 144441346, ThirdMap = 144442368;
        private const int Zone = 838;

        private const long CharacterA = 8_940_100_001, AccountA = 8_940_000_001;
        private const long CharacterB = 8_940_100_002, AccountB = 8_940_000_002;
        private const long CharacterC = 8_940_100_003, AccountC = 8_940_000_003;

        public ConquestTests()
        {
            _file = Path.Combine(Path.GetTempPath(), $"jondo-conquista-{Guid.NewGuid():N}.db");
            AllianceStore.ConnectionStringOverride = $"Data Source={_file}";
            PrismStore.ConnectionStringOverride = $"Data Source={_file}";
            Conquests.Clear();
        }

        public void Dispose()
        {
            using (var world = new SqliteConnection(DatabaseManager.WorldConnectionString))
            {
                world.Open();
                var clean = world.CreateCommand();
                clean.CommandText = "DELETE FROM CharacterItems WHERE CharacterId IN ($a, $b, $c);";
                clean.Parameters.AddWithValue("$a", CharacterA);
                clean.Parameters.AddWithValue("$b", CharacterB);
                clean.Parameters.AddWithValue("$c", CharacterC);
                clean.ExecuteNonQuery();
            }
            KothRoles.Forget();
            AllianceStore.SetTakesPartInAva(CharacterA, false);
            AllianceStore.SetTakesPartInAva(CharacterB, false);
            AllianceStore.SetTakesPartInAva(CharacterC, false);
            ConquestHandler.Forget(CharacterA);
            ConquestHandler.Forget(CharacterB);
            ConquestHandler.Forget(CharacterC);
            Conquests.Clear();
            PrismStore.ConnectionStringOverride = null;
            AllianceStore.ConnectionStringOverride = null;
            TestDatabases.ReleasePool(_file);
            try { File.Delete(_file); } catch (IOException) { }
        }

        private static Conquests.Presence At(long alliance, long map, long character, int role = 0) => new(alliance, map, character, role);

        private static (long, int)[] Of(long alliance, int role = KothRolesDefault) => new[] { (alliance, role) };
        private const int KothRolesDefault = 8;

        // ─── The rules ──────────────────────────────────────────────────────────

        /// <summary>Alone in the zone, an alliance dominates every minute and wins at the twentieth.</summary>
        [Fact]
        public void An_alliance_alone_wins_at_twenty_minutes()
        {
            var war = Conquests.Start(9001, defenderId: 1, nowMs: 0, endsUtcMs: 7_200_000);
            var present = new[] { At(2, PrismMap, 20), At(2, OtherMap, 21) };
            for (int minute = 1; minute < Conquests.DominationToWin; minute++)
                Assert.Equal(0, Conquests.Tick(war, present, minute * Conquests.TickMs));
            Assert.Equal(2, Conquests.Tick(war, present, 20 * Conquests.TickMs));

            var attacker = war.SideOf(2);
            Assert.Equal(20, attacker.Domination);
            Assert.Equal(2, attacker.Present);
            Assert.Equal(40, attacker.Scores[Conquests.ScoreOfMaps]);   // two maps a minute
            Assert.Equal(0, war.SideOf(1).Domination);                 // the defence was never there
        }

        /// <summary>A map's point to whoever outnumbers the rest on it; a tie on a map gives it to nobody.</summary>
        [Fact]
        public void The_maps_go_to_the_most_numerous()
        {
            var war = Conquests.Start(9002, 1, 0, 7_200_000);
            Conquests.Tick(war, new[]
            {
                At(1, PrismMap, 10), At(2, PrismMap, 20), At(2, PrismMap, 21),   // two against one
                At(1, OtherMap, 11), At(2, OtherMap, 22),                         // one against one
                At(1, ThirdMap, 12),                                              // alone
            }, Conquests.TickMs);

            Assert.Equal(1, war.SideOf(1).Scores[Conquests.ScoreOfMaps]);
            Assert.Equal(1, war.SideOf(2).Scores[Conquests.ScoreOfMaps]);
            Assert.Equal(3, war.SideOf(1).Present);
            Assert.Equal(3, war.SideOf(2).Present);

            // A tie in the score: each tied alliance gets its minute.
            Assert.Equal(1, war.SideOf(1).Domination);
            Assert.Equal(1, war.SideOf(2).Domination);
        }

        /// <summary>Reaching the twenty together, the defence keeps its prism.</summary>
        [Fact]
        public void The_defence_wins_a_tie_at_twenty()
        {
            var war = Conquests.Start(9003, 1, 0, 7_200_000);
            var present = new[] { At(1, PrismMap, 10), At(2, OtherMap, 20) };
            long winner = 0;
            for (int minute = 1; minute <= Conquests.DominationToWin && winner == 0; minute++)
                winner = Conquests.Tick(war, present, minute * Conquests.TickMs);
            Assert.Equal(1, winner);
        }

        /// <summary>Without a member in the zone there is no minute of domination, whatever the score.</summary>
        [Fact]
        public void An_absent_alliance_does_not_dominate()
        {
            var war = Conquests.Start(9004, 1, 0, 7_200_000);
            Conquests.FightOver(war, Of(2), Of(1));
            Conquests.Tick(war, new[] { At(1, PrismMap, 10) }, Conquests.TickMs);

            Assert.Equal(5, war.SideOf(2).Total);
            Assert.Equal(0, war.SideOf(2).Domination);
            Assert.Equal(1, war.SideOf(1).Domination);
        }

        /// <summary>An aggression won gives its points, and lost takes them away.</summary>
        [Fact]
        public void Aggressions_give_and_take_points()
        {
            var war = Conquests.Start(9005, 1, 0, 7_200_000);
            Conquests.FightOver(war, Of(2), Of(1));
            Conquests.FightOver(war, Of(2), Of(1));
            Conquests.FightOver(war, Of(1), Of(2));

            Assert.Equal(2 * Conquests.PointsPerFighter - Conquests.PointsPerFighter, war.SideOf(2).Scores[Conquests.ScoreOfFights]);
            Assert.Equal(Conquests.PointsPerFighter - 2 * Conquests.PointsPerFighter, war.SideOf(1).Scores[Conquests.ScoreOfFights]);

            // A champion's weigh double, won and lost.
            Conquests.FightOver(war, Of(3, KothRoles.Champion), Of(4, KothRoles.Champion));
            Assert.Equal(Conquests.PointsPerFighter * Conquests.ChampionFactor, war.SideOf(3).Scores[Conquests.ScoreOfFights]);
            Assert.Equal(-Conquests.PointsPerFighter * Conquests.ChampionFactor, war.SideOf(4).Scores[Conquests.ScoreOfFights]);
        }

        /// <summary>
        /// The roles' limits per alliance and conquest -- one champion, ten seers... -- and the
        /// window's roles: each one present, by role.
        /// </summary>
        [Fact]
        public void Each_alliance_brings_its_roles_up_to_their_limits()
        {
            var war = Conquests.Start(9008, 1, 0, 7_200_000);
            Assert.True(Conquests.Admit(war, 10, 1, KothRoles.Champion));
            Assert.True(Conquests.Admit(war, 10, 1, KothRoles.Champion));          // the same one, again
            Assert.False(Conquests.Admit(war, 11, 1, KothRoles.Champion));         // a second champion
            Assert.True(Conquests.Admit(war, 20, 2, KothRoles.Champion));          // another alliance's
            for (long seer = 100; seer < 110; seer++) Assert.True(Conquests.Admit(war, seer, 1, KothRoles.Seer));
            Assert.False(Conquests.Admit(war, 110, 1, KothRoles.Seer));
            for (long fodder = 200; fodder < 400; fodder++) Assert.True(Conquests.Admit(war, fodder, 1, KothRolesDefault));

            Conquests.Release(war, 10);
            Assert.True(Conquests.Admit(war, 11, 1, KothRoles.Champion));

            Conquests.Tick(war, new[] { At(1, PrismMap, 11, KothRoles.Champion), At(1, OtherMap, 100, KothRoles.Seer),
                                        At(1, OtherMap, 101, KothRoles.Seer), At(1, OtherMap, 200, KothRolesDefault) },
                           Conquests.TickMs);
            var side = war.SideOf(1);
            Assert.Equal(1, side.Roles[KothRoles.Champion]);
            Assert.Equal(2, side.Roles[KothRoles.Seer]);
            Assert.Equal(1, side.Roles[KothRolesDefault]);

            var alliance = new AllianceStore.Alliance { Uuid = "u", Name = "N", Tag = "T" };
            var roles = KothProtocol.Line(side, alliance, KothRolesDefault).Build();
            var lines = ProtoMessage.Parse(roles).Fields.Where(f => f.FieldNumber == 2)
                                    .Select(f => ProtoMessage.Parse(f.BytesValue).Fields).ToList();
            Assert.Equal(3, lines.Count);
            Assert.Contains(lines, l => l.Single(f => f.FieldNumber == 2).VarIntValue == KothRoles.Seer
                                        && l.Single(f => f.FieldNumber == 1).VarIntValue == 2);
        }

        /// <summary>
        /// A scout gathers a point a minute off the prism's map, up to his cap, none on it, and his
        /// potion hands them in as his alliance's prism points.
        /// </summary>
        [Fact]
        public void A_scout_gathers_and_hands_in()
        {
            var war = Conquests.Start(9009, 1, 0, 7_200_000, prismMapId: PrismMap);
            for (int minute = 1; minute <= Conquests.ScoutCap + 3; minute++)
                Conquests.Tick(war, new[] { At(2, OtherMap, 30, KothRoles.Scout), At(2, PrismMap, 31, KothRoles.Scout) },
                               minute * Conquests.TickMs);
            Assert.Equal(Conquests.ScoutCap, war.ScoutCarried[30]);
            Assert.False(war.ScoutCarried.ContainsKey(31));

            Assert.Equal(Conquests.ScoutCap, Conquests.HandIn(war, 30, 2));
            Assert.Equal(Conquests.ScoutCap, war.SideOf(2).Scores[Conquests.ScoreOfPrism]);
            Assert.Equal(0, Conquests.HandIn(war, 30, 2));
        }

        /// <summary>The hon carries the role while aggressable; the hoh's line is { role, status, character }.</summary>
        [Fact]
        public void The_role_goes_over_the_fighter()
        {
            var hon = Fields(AllianceProtocol.BuildAggressableStatus(AllianceProtocol.AvaAggressable, KothRoles.Scout));
            Assert.False(hon.ContainsKey(1));
            Assert.Equal(1_800_000_000_000, (long)Fields(AllianceProtocol.BuildAggressableStatus(
                AllianceProtocol.AvaPrequalified, -1, 1_800_000_000_000))[1].VarIntValue);
            Assert.Equal(KothRoles.Scout, (int)hon[4].VarIntValue);
            Assert.Equal(AllianceProtocol.AvaAggressable, (int)hon[5].VarIntValue);
            Assert.False(Fields(AllianceProtocol.BuildAggressableStatus(AllianceProtocol.AvaNotAggressable, KothRoles.Scout)).ContainsKey(4));

            var hof = Fields(AllianceProtocol.StatusOf(77, AllianceProtocol.AvaAggressable, -1).Build());
            Assert.Equal(-1, (int)(long)hof[1].VarIntValue);
            Assert.Equal(77, hof[3].VarIntValue);
        }

        /// <summary>Each role's certificate comes from the client's own items, «Certificado de explorador» for the scout.</summary>
        [Fact]
        public void The_roles_have_their_certificates()
        {
            if (AllianceData.KothRoles.Count == 0) return;   // no alliance data
            Assert.Equal(27659, KothRoles.CertificateOf(KothRoles.Scout));
            Assert.Equal(27936, KothRoles.CertificateOf(AllianceData.DefaultKothRole));
            Assert.Equal(KothRoles.Seer, KothRoles.RoleOfCertificate(27658));
            Assert.Equal(0, KothRoles.RoleOfCertificate(0));
        }

        /// <summary>
        /// Hal Ylanza's conversation: «Elegir tu rol» leads to the roles, and «Quiero ser explorador»
        /// takes the scout's role, or says «aún no estás listo».
        /// </summary>
        [Fact]
        public void Hal_Ylanza_hands_the_roles_over()
        {
            var tree = NpcDialogues.For(2142, 115083777);
            Assert.NotNull(tree);
            Assert.Empty(Jondo.Unity.World.Content.NpcDialogueContent.Complaints(tree));
            var choose = tree.Line(tree.Opening)?.Choice(18584);
            Assert.NotNull(choose);
            var scout = tree.Line(choose.Next)?.Choice(70781);
            Assert.NotNull(scout);
            Assert.Equal(KothRoles.Scout, scout.KothRole);
            Assert.Equal(52764, scout.Next);
            Assert.Equal(52765, scout.RefusedNext);
        }

        /// <summary>Taking a role hands its certificate over and takes the others; changing again so soon is refused.</summary>
        [Fact]
        public async Task A_role_is_its_certificate()
        {
            if (AllianceData.KothRoles.Count == 0) return;
            await using var a = await Economy.ClientPipe.OpenAsync(AccountA, CharacterA, PrismMap);
            using (SessionContext.Push(a.Session))
            {
                Assert.Equal(AllianceData.DefaultKothRole, KothRoles.RoleOfCurrent());
                Assert.Equal(KothRoles.None, KothRoles.HeldRoleOf(CharacterA));
                Assert.True(await KothRoles.BecomeAsync(a.ToClient, KothRoles.Scout, 1_000_000));
                Assert.Equal(KothRoles.Scout, KothRoles.RoleOfCurrent());
                Assert.Equal(KothRoles.Scout, KothRoles.HeldRoleOf(CharacterA));
                Assert.Equal(1, Equipment.HowMany(27659));
                Assert.False(await KothRoles.BecomeAsync(a.ToClient, KothRoles.Seer, 1_000_000 + 60_000));
                Assert.Equal(KothRoles.Scout, KothRoles.RoleOfCurrent());
                Assert.True(await KothRoles.BecomeAsync(a.ToClient, KothRoles.Seer, 1_000_000 + KothRoles.ChangeCooldownMs));
                Assert.Equal(KothRoles.Seer, KothRoles.RoleOfCurrent());
                Assert.Equal(KothRoles.Seer, KothRoles.HeldRoleOf(CharacterA));
                Assert.Equal(0, Equipment.HowMany(27659));

                // Cannon fodder's certificate is a role the members tab shows too, not «---».
                Assert.True(await KothRoles.BecomeAsync(a.ToClient, AllianceData.DefaultKothRole, 1_000_000 + 2 * KothRoles.ChangeCooldownMs));
                Assert.Equal(AllianceData.DefaultKothRole, KothRoles.HeldRoleOf(CharacterA));
            }
        }

        /// <summary>
        /// Fighting where one's alliance holds the prism pays the client's 25 % more: only its
        /// members, only in its subarea.
        /// </summary>
        [Fact]
        public void The_own_prism_raises_the_experience()
        {
            if (MapManager.GetMapInfo(PrismMap) == null) MapManager.Initialize();
            if (MapManager.GetMapInfo(PrismMap)?.SubAreaId != Zone) return;

            var mine = AllianceStore.Create(CharacterA, "Mia", "MIA", 1, 1, 1, 1, 1);
            AllianceStore.Create(CharacterB, "Otra", "OTR", 1, 1, 1, 1, 1);
            Assert.False(CombatExperience.FightsUnderOwnPrism(CharacterA, OtherMap));

            PrismStore.Place(mine.Id, Zone, PrismMap, 300, 1);
            Assert.True(CombatExperience.FightsUnderOwnPrism(CharacterA, OtherMap));
            Assert.False(CombatExperience.FightsUnderOwnPrism(CharacterB, OtherMap));
            Assert.False(CombatExperience.FightsUnderOwnPrism(CharacterC, OtherMap));
            Assert.Equal(1250, CombatExperience.WithCharacterBonus(1000, CombatExperience.AlliancePrismBonusPercent));
        }

        /// <summary>
        /// Its own alliance's ley: f1 the crystals left of four, f6 the crystal it holds; nobody
        /// else's carries either.
        /// </summary>
        [Fact]
        public void The_own_view_carries_the_crystals()
        {
            var alliance = new AllianceStore.Alliance { Uuid = "u", Name = "N", Tag = "T" };
            var prism = new PrismStore.Prism { Id = 5, PlacedUtcMs = 1_784_886_505_835, CrystalsUsed = 1 };

            var theirs = Fields(PrismProtocol.State(prism, alliance).Build());
            Assert.False(theirs.ContainsKey(1));
            Assert.False(theirs.ContainsKey(6));

            var ours = Fields(PrismProtocol.State(prism, alliance, own: true).Build());
            Assert.Equal(PrismProtocol.MaxCrystals - 1, ours[1].VarIntValue);
            // No crystal in it: still a lec, with an empty item (f5), or the client throws.
            var empty = Fields(ours[6].BytesValue);
            Assert.False(empty.ContainsKey(3));
            Assert.Empty(Fields(empty[2].BytesValue)[5].BytesValue);

            prism.CrystalGid = 27632;
            prism.State = PrismStore.Pacified;
            var lno = Fields(Fields(PrismProtocol.State(prism, alliance, own: true).Build())[6].BytesValue);
            Assert.Equal(1, lno[3].VarIntValue);
            Assert.True(lno.ContainsKey(2));
        }

        /// <summary>
        /// «Añadir un cristal» (iey) on one's prism opens the crystal window (kci, kind 18); a
        /// pacifying crystal dropped in it is spent, the prism pacified for its twelve hours with one
        /// crystal of four gone, and no teleport goes to it; the window closes as a storage (khd).
        /// </summary>
        [Fact]
        public async Task A_crystal_pacifies_its_prism()
        {
            if (PrismHandler.CrystalHours(27632) == 0) return;   // no item data
            var defence = AllianceStore.Create(CharacterA, "Defensa", "DEF", 1, 1, 1, 1, 1);
            var prism = PrismStore.Place(defence.Id, Zone, PrismMap, 300, 1);
            var crystal = DatabaseManager.AddPieceToInventory(CharacterA, 27632, "[]");
            Assert.NotNull(crystal);

            await using var a = await Economy.ClientPipe.OpenAsync(AccountA, CharacterA, PrismMap);
            using (SessionContext.Push(a.Session))
            {
                Equipment.Add(crystal.Uid, 27632, 1, Equipment.Bag, null);
                await PrismHandler.CrystalWindowAsync(a.ToClient);
                var view = await a.Next(Op.Ifa);
                Assert.Equal(PrismHandler.ChangedFor(prism, defence, CharacterA), ConnectionProtocol.Push(Op.Ifa, view));
                Assert.Equal(StorageProtocol.BuildOpened(1, PrismProtocol.CrystalWindowKind), await a.Next(Op.Kci));

                long before = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
                Assert.True(await PrismHandler.MoveCrystalAsync(a.ToClient,
                    ConnectionProtocol.Push(Op.Kcr, Pb.New().Var(1, 1).Var(2, crystal.Uid).Build())));
                Assert.Equal(ConnectionProtocol.BuildItemGone(crystal.Uid), await a.Next(Op.Ium));
                Assert.Equal(PrismStore.Pacified, prism.State);
                Assert.Equal(27632, prism.CrystalGid);
                Assert.Equal(1, prism.CrystalsUsed);
                Assert.InRange(prism.NextChangeUtcMs - before, 12 * 3_600_000L - 5_000, 12 * 3_600_000L + 5_000);
                Assert.True(prism.HasActiveCrystal);
                Assert.False(PrismHandler.IsDestination(CharacterA, PrismMap));

                Assert.True(await PrismHandler.CloseCrystalWindowAsync(a.ToClient));
                Assert.False(await PrismHandler.CloseCrystalWindowAsync(a.ToClient));
            }

            // Its hours over, the crystal is gone from its slot and the prism normal again.
            await ConquestHandler.TickAsync(prism.NextChangeUtcMs + 1);
            Assert.Equal(PrismStore.Normal, prism.State);
            Assert.Equal(0, prism.CrystalGid);
            Assert.Equal(1, prism.CrystalsUsed);
        }

        /// <summary>The winner's priority lasts its thirty minutes; a conquered prism's invulnerability shrinks with the prisms held.</summary>
        [Fact]
        public void The_priority_and_the_invulnerability()
        {
            Conquests.GivePriority(9006, 7, 30 * 60_000);
            Assert.Equal(7, Conquests.PriorityIn(9006, 29 * 60_000)?.AllianceId);
            Assert.Null(Conquests.PriorityIn(9006, 30 * 60_000));

            Assert.Equal(24 * 3_600_000L, Conquests.InvulnerabilityMs(24, 1));
            Assert.Equal(6 * 3_600_000L, Conquests.InvulnerabilityMs(24, 4));
            Assert.Equal(0, Conquests.InvulnerabilityMs(0, 4));
        }

        // ─── The store and the frames ───────────────────────────────────────────

        /// <summary>A table of before the states gets its columns, and a state set is there after a reload.</summary>
        [Fact]
        public void The_state_is_kept_and_an_old_table_gets_it()
        {
            using (var old = new SqliteConnection($"Data Source={_file}"))
            {
                old.Open();
                var create = old.CreateCommand();
                create.CommandText = @"
                    CREATE TABLE Prisms (Id INTEGER PRIMARY KEY AUTOINCREMENT, AllianceId INTEGER NOT NULL,
                        SubAreaId INTEGER NOT NULL, MapId INTEGER NOT NULL, CellId INTEGER NOT NULL DEFAULT 0,
                        Direction INTEGER NOT NULL DEFAULT 1, PlacedUtcMs INTEGER NOT NULL DEFAULT 0);
                    INSERT INTO Prisms (AllianceId, SubAreaId, MapId, CellId, Direction, PlacedUtcMs)
                    VALUES (3, 838, 144441344, 300, 1, 1000);";
                create.ExecuteNonQuery();
            }
            PrismStore.ConnectionStringOverride = $"Data Source={_file}";

            var prism = PrismStore.InSubArea(Zone);
            Assert.NotNull(prism);
            Assert.Equal(PrismStore.Normal, prism.State);

            PrismStore.SetState(prism, PrismStore.Weakened, 123_456);
            PrismStore.ConnectionStringOverride = $"Data Source={_file}";
            var again = PrismStore.InSubArea(Zone);
            Assert.NotSame(prism, again);
            Assert.Equal(PrismStore.Weakened, again.State);
            Assert.Equal(123_456, again.NextChangeUtcMs);
        }

        /// <summary>
        /// The ley: normal, as the captures' -- f2 the placing, no f3 --; weakened, f2 when it turns
        /// vulnerable, the time the conquests window counts down, and f3 its state.
        /// </summary>
        [Fact]
        public void The_state_goes_in_the_ley()
        {
            var alliance = new AllianceStore.Alliance { Uuid = "u", Name = "N", Tag = "T" };
            var prism = new PrismStore.Prism { PlacedUtcMs = 1_784_886_505_835 };

            var normal = Fields(PrismProtocol.State(prism, alliance).Build());
            Assert.Equal(1_784_886_505_835, normal[2].VarIntValue);
            Assert.False(normal.ContainsKey(3));
            Assert.Equal(1_784_886_505, normal[4].VarIntValue);

            prism.State = PrismStore.Weakened;
            prism.NextChangeUtcMs = 1_784_972_905_835;
            var weakened = Fields(PrismProtocol.State(prism, alliance).Build());
            Assert.Equal(1_784_972_905_835, weakened[2].VarIntValue);
            Assert.Equal(PrismStore.Weakened, (int)weakened[3].VarIntValue);
            Assert.Equal(1_784_886_505, weakened[4].VarIntValue);
        }

        /// <summary>The score: each alliance's line -- its roles, minutes, three kinds of points, members -- and the conquest's clocks.</summary>
        [Fact]
        public void The_score_frame()
        {
            var war = Conquests.Start(9007, 1, 1_784_886_000_000, 1_784_893_200_000);
            Conquests.Tick(war, new[] { At(1, PrismMap, 10), At(1, OtherMap, 11) }, 1_784_886_060_000);
            var alliance = new AllianceStore.Alliance { Uuid = "uuid-1", Name = "Defensa", Tag = "DEF" };

            var mep = ProtoMessage.Parse(KothProtocol.BuildUpdate(war, id => id == 1 ? alliance : null, 8)).Fields;
            var line = ProtoMessage.Parse(mep.Single(f => f.FieldNumber == 1).BytesValue).Fields;
            Assert.Equal(1_784_886_120_000, mep.Single(f => f.FieldNumber == 2).VarIntValue);
            string began = System.Text.Encoding.UTF8.GetString(mep.Single(f => f.FieldNumber == 3).BytesValue);
            Assert.Equal(1_784_886_000_000,
                new DateTimeOffset(DateTime.Parse(began, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind)).ToUnixTimeMilliseconds());

            Assert.Equal(AllianceProtocol.Block(alliance).Build(), line.Single(f => f.FieldNumber == 1).BytesValue);
            var role = ProtoMessage.Parse(line.Single(f => f.FieldNumber == 2).BytesValue).Fields;
            Assert.Equal(2, role.Single(f => f.FieldNumber == 1).VarIntValue);
            Assert.Equal(8, role.Single(f => f.FieldNumber == 2).VarIntValue);
            Assert.Equal(1, line.Single(f => f.FieldNumber == 3).VarIntValue);
            var points = line.Where(f => f.FieldNumber == 5).Select(f => ProtoMessage.Parse(f.BytesValue).Fields).ToList();
            Assert.Equal(3, points.Count);
            Assert.Equal(2, points[0].Single(f => f.FieldNumber == 1).VarIntValue);   // the maps
            Assert.DoesNotContain(points[0], f => f.FieldNumber == 4);                 // kind 0
            Assert.Equal(1, points[1].Single(f => f.FieldNumber == 4).VarIntValue);
            Assert.Equal(2, points[2].Single(f => f.FieldNumber == 4).VarIntValue);
            Assert.Equal(2, line.Single(f => f.FieldNumber == 6).VarIntValue);
        }

        private static Dictionary<int, ProtoField> Fields(byte[] bytes)
            => ProtoMessage.Parse(bytes).Fields.ToDictionary(f => f.FieldNumber);

        // ─── The whole of it ────────────────────────────────────────────────────

        /// <summary>
        /// «Defensa»'s prism beaten by «Asalto»: weakened (95 and 94), vulnerable at its hour (86,
        /// 89, both fighters aggressable), refused to a new attack (406), and conquered by «Asalto»
        /// alone in the zone for twenty minutes: the score each minute, the end (mdz), the prism
        /// gone (iew), «Asalto»'s priority (1567) and its fighter back to not aggressable.
        /// </summary>
        [Fact]
        public async Task A_prism_is_weakened_turns_vulnerable_and_is_conquered()
        {
            if (MapManager.GetMapInfo(PrismMap) == null) MapManager.Initialize();
            if (MapManager.GetMapInfo(PrismMap)?.SubAreaId != Zone) return;   // no map data

            var defence = AllianceStore.Create(CharacterA, "Defensa", "DEF", 1, 1, 1, 1, 1);
            var assault = AllianceStore.Create(CharacterB, "Asalto", "ASA", 1, 1, 1, 1, 1);
            AllianceStore.SetTakesPartInAva(CharacterA, true);
            AllianceStore.SetTakesPartInAva(CharacterB, true);
            var prism = PrismStore.Place(defence.Id, Zone, PrismMap, 300, 1);

            await using var a = await Economy.ClientPipe.OpenAsync(AccountA, CharacterA, PrismMap);
            await using var b = await Economy.ClientPipe.OpenAsync(AccountB, CharacterB, PrismMap);

            // Beaten.
            long now = 1_784_886_000_000;
            await ConquestHandler.WeakenAsync(prism, new[] { assault.Id }, now);
            long hours = Math.Max(0, ServerSettings.Current.PrismWeakenedHours);
            Assert.Equal(PrismStore.Weakened, prism.State);
            Assert.Equal(now + hours * 3_600_000L, prism.NextChangeUtcMs);
            Assert.Equal(new[] { "ifa", "jsn", "lqn" }, (await a.Take(3)).Select(f => f.Opcode));
            var bSaw = await b.Take(3);
            Assert.Equal(new[] { "ifa", "jsn", "lqn" }, bSaw.Select(f => f.Opcode));
            Assert.Equal(ConnectionProtocol.BuildInfoMessage(2, 94, Zone.ToString()), bSaw[2].Payload);

            // Weakened, it is not fought.
            using (SessionContext.Push(b.Session))
                await PrismHandler.AttackAsync(b.ToClient);
            Assert.Equal(ConnectionProtocol.BuildInfoMessage(InfoMessages.Warning, 406), await b.Next(Op.Lqn));

            // Its hour come: vulnerable, the conquest open, both aggressable.
            now += hours * 3_600_000L;
            await ConquestHandler.TickAsync(now);
            Assert.Equal(PrismStore.Vulnerable, prism.State);
            Assert.NotNull(Conquests.In(Zone));
            var aSaw = await a.Take(5);
            Assert.Equal(new[] { "ifa", "jsn", "lqn", "hon", "hoh" }, aSaw.Select(f => f.Opcode));
            Assert.Equal(ConnectionProtocol.BuildInfoMessage(2, 86, Zone.ToString()), aSaw[2].Payload);
            Assert.Equal(AllianceProtocol.BuildAggressableStatus(AllianceProtocol.AvaAggressable, KothRolesDefault), aSaw[3].Payload);
            // «Asalto»'s fighter, his role hidden from one who is no seer.
            Assert.Equal(Pb.New().Msg(1, AllianceProtocol.StatusOf(CharacterB, AllianceProtocol.AvaAggressable, -1)).Build(), aSaw[4].Payload);
            bSaw = await b.Take(5);
            Assert.Equal(ConnectionProtocol.BuildInfoMessage(2, 89, Zone.ToString()), bSaw[2].Payload);
            Assert.Equal(AllianceProtocol.AvaAggressable, AllianceProtocol.AggressableStatus(CharacterB));

            // «Defensa» leaves; «Asalto» holds the zone twenty minutes.
            await a.DisposeAsync();
            for (int minute = 1; minute <= Conquests.DominationToWin; minute++)
            {
                await ConquestHandler.TickAsync(now + minute * Conquests.TickMs);
                var score = ProtoMessage.Parse(await b.Next(Op.Mep)).Fields;
                Assert.Equal(2, score.Count(f => f.FieldNumber == 1));
            }

            Assert.Equal(AllianceProtocol.Block(assault).Build(),
                         ProtoMessage.Parse(await b.Next(Op.Mdz)).Fields.Single(f => f.FieldNumber == 1).BytesValue);
            await b.Next(Op.Kmu);
            Assert.Equal(PrismProtocol.BuildGone(Zone), await b.Next(Op.Iew));
            Assert.Equal(ConnectionProtocol.BuildSystemMessage(1567, Zone.ToString(), "30"), await b.Next(Op.Lqn));
            Assert.Equal(AllianceProtocol.BuildAggressableStatus(AllianceProtocol.AvaNotAggressable), await b.Next(Op.Hon));
            Assert.Null(PrismStore.InSubArea(Zone));
            Assert.Null(Conquests.In(Zone));

            // «Asalto» alone may place there now, and its prism is born pacified.
            var (state, until) = ConquestHandler.BirthOf(defence.Id, Zone, now + 21 * Conquests.TickMs);
            Assert.Equal(PrismStore.Normal, state);
            (state, until) = ConquestHandler.BirthOf(assault.Id, Zone, now + 21 * Conquests.TickMs);
            Assert.Equal(PrismStore.Pacified, state);
            Assert.Equal(now + 21 * Conquests.TickMs
                         + Conquests.InvulnerabilityMs(ServerSettings.Current.ConquestInvulnerabilityHours, 1), until);
            Assert.Null(Conquests.PriorityIn(Zone, now + 21 * Conquests.TickMs));
        }

        /// <summary>
        /// «Agredir» (an hph without its f2) outside a conquest is refused (421); an aggression
        /// of a conquest's zone over gives its points.
        /// </summary>
        [Fact]
        public async Task Aggressions_need_a_conquest_and_count_in_it()
        {
            if (MapManager.GetMapInfo(PrismMap) == null) MapManager.Initialize();
            if (MapManager.GetMapInfo(PrismMap)?.SubAreaId != Zone) return;

            var defence = AllianceStore.Create(CharacterA, "Defensa", "DEF", 1, 1, 1, 1, 1);
            var assault = AllianceStore.Create(CharacterB, "Asalto", "ASA", 1, 1, 1, 1, 1);
            AllianceStore.SetTakesPartInAva(CharacterA, true);
            AllianceStore.SetTakesPartInAva(CharacterB, true);
            PrismStore.Place(defence.Id, Zone, PrismMap, 300, 1);

            await using var a = await Economy.ClientPipe.OpenAsync(AccountA, CharacterA, PrismMap);
            await using var b = await Economy.ClientPipe.OpenAsync(AccountB, CharacterB, PrismMap);
            using (SessionContext.Push(b.Session))
                await ChallengeDuelHandler.OfferAsync(b.ToClient, ConnectionProtocol.Push(Op.Hph, Pb.New().Var(1, CharacterA).Build()));
            Assert.Equal(ConnectionProtocol.BuildInfoMessage(InfoMessages.Warning, 421), await b.Next(Op.Lqn));
            await a.Quiet();

            var war = Conquests.Start(Zone, defence.Id, 0, 7_200_000);
            var fight = new FightInstance(1, PrismMap) { Reglas = FightRules.Agresion };
            fight.Azul.Add(new Fighter { Id = CharacterB });
            fight.Rojo.Add(new Fighter { Id = CharacterA });
            await ConquestHandler.AggressionEndedAsync(fight, attackersWon: true);
            Assert.Equal(Conquests.PointsPerFighter, war.SideOf(assault.Id).Scores[Conquests.ScoreOfFights]);
            Assert.Equal(-Conquests.PointsPerFighter, war.SideOf(defence.Id).Scores[Conquests.ScoreOfFights]);
        }

        /// <summary>
        /// The fallen of a conquest: the loser of an aggression stays a tomb (kup 1), out of the
        /// count and not aggressable; an allied resurrector's potion raises him (kup 0); fallen
        /// again, his «Resucitar» on the death popup (kug) frees his soul, out of this conquest for
        /// good. A champion is never a tomb.
        /// </summary>
        [Fact]
        public async Task The_fallen_are_tombs_until_raised_or_freed()
        {
            if (MapManager.GetMapInfo(PrismMap) == null) MapManager.Initialize();
            if (MapManager.GetMapInfo(PrismMap)?.SubAreaId != Zone || AllianceData.KothRoles.Count == 0) return;

            var defence = AllianceStore.Create(CharacterA, "Defensa", "DEF", 1, 1, 1, 1, 1);
            var assault = AllianceStore.Create(CharacterB, "Asalto", "ASA", 1, 1, 1, 1, 1);
            AllianceStore.Join(CharacterC, assault.Id);
            foreach (long id in new[] { CharacterA, CharacterB, CharacterC }) AllianceStore.SetTakesPartInAva(id, true);
            var prism = PrismStore.Place(defence.Id, Zone, PrismMap, 300, 1);
            PrismStore.SetState(prism, PrismStore.Vulnerable, 7_200_000);
            var war = Conquests.Start(Zone, defence.Id, 0, 7_200_000, PrismMap);
            var potion = DatabaseManager.AddPieceToInventory(CharacterC, 14443, "[]");

            await using var a = await Economy.ClientPipe.OpenAsync(AccountA, CharacterA, PrismMap);
            await using var b = await Economy.ClientPipe.OpenAsync(AccountB, CharacterB, PrismMap);
            await using var c = await Economy.ClientPipe.OpenAsync(AccountC, CharacterC, PrismMap);
            using (SessionContext.Push(c.Session))
            {
                Equipment.Add(9_400_001, KothRoles.CertificateOf(KothRoles.Resurrector), 1, Equipment.Bag, null);
                Equipment.Add(potion.Uid, 14443, 1, Equipment.Bag, null);
            }
            Assert.True(ConquestHandler.FightsInAWar(CharacterA));
            Assert.True(ConquestHandler.FightsInAWar(CharacterB));
            Assert.True(ConquestHandler.FightsInAWar(CharacterC));
            Assert.Equal(KothRoles.Resurrector, war.Admitted[CharacterC].Role);

            FightInstance Lost()
            {
                var fight = new FightInstance(1, PrismMap) { Reglas = FightRules.Agresion };
                fight.Azul.Add(new Fighter { Id = CharacterA });
                fight.Rojo.Add(new Fighter { Id = CharacterB });
                return fight;
            }

            // «Asalto»'s fighter loses: a tomb.
            Assert.True(ConquestHandler.StaysAsTomb(Lost(), CharacterB));
            await ConquestHandler.AggressionEndedAsync(Lost(), attackersWon: true);
            var (savePoint, _) = FightHandler.SavePointOf(CharacterB);
            Assert.Equal(KothProtocol.BuildLifeState(KothProtocol.Tomb, savePoint), await Economy.RecyclingTests.Until(b, Op.Kup));
            Assert.Equal(AllianceProtocol.BuildAggressableStatus(AllianceProtocol.AvaNotAggressable), await b.Next(Op.Hon));
            Assert.False(ConquestHandler.FightsInAWar(CharacterB));
            Assert.Equal(AllianceProtocol.AvaNotAggressable, AllianceProtocol.AggressableStatus(CharacterB));
            Assert.DoesNotContain(ConquestHandler.PresentIn(Zone), p => p.CharacterId == CharacterB);

            // His ally the resurrector raises him.
            using (SessionContext.Push(c.Session))
                Assert.True(await ConquestHandler.UseRolePotionAsync(c.ToClient, Equipment.ByUid(potion.Uid)));
            Assert.Equal(KothProtocol.BuildLifeState(KothProtocol.Alive), await Economy.RecyclingTests.Until(b, Op.Kup));
            Assert.Empty(war.Fallen);
            Assert.True(ConquestHandler.FightsInAWar(CharacterB));
            using (SessionContext.Push(c.Session)) Assert.Equal(0, Equipment.HowMany(14443));

            // Fallen again, he frees his soul himself: out of this conquest for good.
            await ConquestHandler.AggressionEndedAsync(Lost(), attackersWon: true);
            Assert.True(war.Fallen.ContainsKey(CharacterB));
            using (SessionContext.Push(b.Session))
                await ConquestHandler.FreeSoulAsync(b.ToClient);
            Assert.True(war.Freed.ContainsKey(CharacterB));
            Assert.False(war.Admitted.ContainsKey(CharacterB));
            Assert.False(Conquests.Admit(war, CharacterB, assault.Id, AllianceData.DefaultKothRole));

            // A champion is no tomb: his soul goes at once.
            Assert.True(Conquests.Admit(war, 77, defence.Id, KothRoles.Champion));
            var champion = new FightInstance(2, PrismMap) { Reglas = FightRules.Agresion };
            Assert.False(ConquestHandler.StaysAsTomb(champion, 77));
            Assert.False(ConquestHandler.StaysAsTomb(new FightInstance(3, PrismMap), CharacterA));   // no aggression
        }

        /// <summary>The aggression's rules: the kam's type 1, the usual clock, nothing to win, a defeat that costs.</summary>
        [Fact]
        public void The_aggression_rules()
        {
            var rules = FightRules.Agresion;
            Assert.Equal(1, rules.TipoDelKam);
            Assert.Equal(450, rules.RelojDeColocacion);
            Assert.False(rules.HayRetos);
            Assert.False(rules.ReparteBotin);
            Assert.False(rules.EnfrenteHayMonstruos);
            Assert.True(rules.DefeatCosts);
        }
    }
}
