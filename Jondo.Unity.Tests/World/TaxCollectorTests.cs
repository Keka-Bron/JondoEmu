using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
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
    /// The alliances' tax collectors: their frames against the captures that have one -- the map
    /// actor of «Alliance2» in the party search capture, and the talk and the attack of «atacar a
    /// recaudador y perder» -- and the store and the rules underneath.
    /// </summary>
    [Collection("alliances")]
    public class TaxCollectorTests : IDisposable
    {
        private readonly string _file;

        public TaxCollectorTests()
        {
            _file = Path.Combine(Path.GetTempPath(), $"jondo-recaudador-{Guid.NewGuid():N}.db");
            AllianceStore.ConnectionStringOverride = $"Data Source={_file}";
            TaxCollectorStore.ConnectionStringOverride = $"Data Source={_file}";
        }

        public void Dispose()
        {
            TaxCollectorStore.ConnectionStringOverride = null;
            AllianceStore.ConnectionStringOverride = null;
            TestDatabases.ReleasePool(_file);
            try { File.Delete(_file); } catch (IOException) { }
        }

        private static byte[] Hex(string hex) => Convert.FromHexString(hex);

        /// <summary>«Alliance2», whose tax collector stands in the party search capture.</summary>
        private static readonly AllianceStore.Alliance Alliance2 = new()
        {
            Uuid = "00000000-0000-4000-8000-000000000005",
            Name = "Alliance2",
            Tag = "BLOOD",
            EmblemSymbol = 104,
            EmblemSymbolColor = 8,
            EmblemSymbolRgb = 11862276,
        };

        /// <summary>«Alliance-3», whose tax collector is talked to and attacked in the other capture.</summary>
        private static readonly AllianceStore.Alliance AllianceThree = new()
        {
            Uuid = "00000000-0000-4000-8000-000000000006",
            Name = "Alliance-3",
            Tag = "AL3",
            EmblemSymbol = 136,
            EmblemSymbolColor = 3,
            EmblemBackground = 1002575,
            EmblemSymbolRgb = 16630094,
        };

        // ─── The frames, byte for byte against the captures ──────────────────────

        /// <summary>
        /// The map actor: kind 6 with the alliance, the names, the uuid and who placed it, and the
        /// look in the alliance's colours with its symbol as a skin. The capture's 74 is its last
        /// name and its 154 its first: the client reads f2 as the last name and f5 as the first.
        /// </summary>
        [Fact]
        public void The_map_actor_matches_the_capture()
        {
            var tc = new TaxCollectorStore.TaxCollector
            {
                Id = TaxCollectorStore.ContextBase + 20006,
                Uuid = "00000000-0000-4000-8000-000000000007",
                OwnerId = 240000000014,
                CellId = 352,
                Direction = 1,
                FirstNameId = 154,
                LastNameId = 74,
            };
            Assert.Equal(-20006, tc.ContextId);
            Assert.Equal(Hex("0a0508e00210011295010a7b32790a770a43120908681008288482d4051a09416c6c69616e636532222430303030" +
                             "303030302d303030302d343030302d383030302d3030303030303030303030352a05424c4f4f44104a1a24303030" +
                             "30303030302d303030302d343030302d383030302d303030303030303030303037208ec0f588fe06289a011a160a" +
                             "088482d4458080803810031890392a01733202ef0c18dae3feffffffffffff01"),
                         TaxCollectorProtocol.Actor(tc, Alliance2).Build());
        }

        /// <summary>
        /// The actor's f5 is looked up among the client's first names and its f2 among its last
        /// names. A last name past the 154 first names -- 241 -- in f5 left the tax collector with
        /// no tooltip and no menu.
        /// </summary>
        [Fact]
        public void The_actor_names_are_where_the_client_looks_them_up()
        {
            var tc = new TaxCollectorStore.TaxCollector { Id = 1, Uuid = "u", FirstNameId = 78, LastNameId = 241 };

            var block = Field(Field(Field(Field(TaxCollectorProtocol.Actor(tc, Alliance2).Build(), 2), 1), 6), 1);
            Assert.Equal(241, Number(block, 2));
            Assert.Equal(78, Number(block, 5));

            if (AllianceData.TaxCollectorFirstNames.Count == 0) return;   // no client data
            Assert.Contains(AllianceData.TaxCollectorFirstNames, n => n.Id == Number(block, 5));
            Assert.Contains(AllianceData.TaxCollectorNames, n => n.Id == Number(block, 2));
            Assert.DoesNotContain(AllianceData.TaxCollectorFirstNames, n => n.Id == 241);
        }

        private static byte[] Field(byte[] message, int number)
            => ProtoMessage.Parse(message).Fields.First(f => f.FieldNumber == number && f.WireType == 2).BytesValue;

        private static long Number(byte[] message, int number)
            => ProtoMessage.Parse(message).Fields.First(f => f.FieldNumber == number && f.WireType == 0).VarIntValue;

        /// <summary>
        /// Talking to it (ion): 429 pods of 1000, «Alliance-3», 20 tax collectors, 200 of
        /// prospecting and 23836 kamas' worth.
        /// </summary>
        [Fact]
        public void The_dialog_matches_the_capture()
        {
            Assert.Equal(Hex("08ad0310e8071a47120e088801100318cf983d28ce82f7071a0a416c6c69616e63652d33222430303030303030" +
                             "302d303030302d343030302d383030302d3030303030303030303030362a03414c33201428c801389cba01"),
                         TaxCollectorProtocol.BuildDialog(AllianceThree, 429, 20, 23836));
        }

        /// <summary>The tax collector's side on the map (kae): kind 3, led by its actor, it by its names.</summary>
        [Fact]
        public void Its_side_matches_the_capture()
        {
            var side = FightJoinProtocol.Team(FightInstance.Rojos, -20074, FightJoinProtocol.TaxCollectorSide,
                                              new[] { FightJoinProtocol.TaxCollectorOnTeam(-1, 35, 109) });
            Assert.Equal(Hex("0a2610031896e3feffffffffffff0120012a130a1110ffffffffffffffffff011a040823106d300110c401"),
                         FightJoinProtocol.BuildTeamUpdate(side, 196));
        }

        /// <summary>The fight's announcement (kam): type 5, the tax collector's actor, no monster list.</summary>
        [Fact]
        public void The_announcement_matches_the_capture()
        {
            Assert.Equal(Hex("10051896e3feffffffffffff0128c4013081f092cbdd08"),
                         FightProtocol.BuildFightAnnounced(FightRules.ContraRecaudador.TipoDelKam, -20074,
                                                           new List<long>(), 196, 300000000001));
            Assert.Equal(Hex("1801200128bb033005"),
                         FightProtocol.BuildFightSummary(FightRules.ContraRecaudador.TipoDelKam, 443));
        }

        /// <summary>
        /// Its identity as a fighter: the other case of the monsters' oneof, its names and the
        /// monster «Recaudador», as in the jxg of the attack ({35, 109, 3859}).
        /// </summary>
        [Fact]
        public void Its_identity_as_a_fighter_matches_the_capture()
        {
            Assert.Equal(Hex("0a070823106d18931e"),
                         FightProtocol.TaxCollectorIdentity(35, 109, TaxCollectorProtocol.Monster).Build());
        }

        /// <summary>Its look in a fight is the one of the jxg: the fight's bones, the same colours and skin.</summary>
        [Fact]
        public void Its_look_in_a_fight_matches_the_capture()
        {
            Assert.Equal("{7313|1762|8=16630094,7=1002575|115}",
                         TaxCollectorProtocol.LookString(AllianceThree, TaxCollectorProtocol.FightBones));
            Assert.Equal(Hex("0a08ce82f747cf98bd3810031891392a01733202e20d"),
                         TaxCollectorProtocol.Look(AllianceThree, TaxCollectorProtocol.FightBones).Build());
        }

        /// <summary>The fight's rules: the kam's 5, the usual clock, no challenges and no loot table.</summary>
        [Fact]
        public void The_rules_are_those_of_the_capture()
        {
            var rules = FightRules.ContraRecaudador;
            Assert.Equal(5, rules.TipoDelKam);
            Assert.Equal(450, rules.RelojDeColocacion);
            Assert.False(rules.HayRetos);
            Assert.False(rules.ReparteBotin);
            Assert.False(rules.BorraElGrupoAlGanar);
            Assert.True(rules.EnfrenteHayMonstruos);
        }

        /// <summary>
        /// A defended side takes people although it has a monster: its alliance's defenders. Any other
        /// monster side does not.
        /// </summary>
        [Fact]
        public void Its_side_takes_defenders()
        {
            var fight = new FightInstance(1, 1, 1);
            fight.RedPlacementCells.AddRange(new[] { 10, 11, 12 });
            fight.AddMonster(new Fighter { Id = -1, IsMonster = true, MonsterId = TaxCollectorProtocol.Monster, CellId = 10 });
            Assert.Equal(FightInstance.JoinRefusal.MonsterTeam, fight.CanJoin(FightInstance.Rojos, 8));

            fight.DefendingAllianceId = 7;
            Assert.Equal(FightInstance.JoinRefusal.None, fight.CanJoin(FightInstance.Rojos, 8));
        }

        // ─── The store ───────────────────────────────────────────────────────────

        /// <summary>One per map, found by its map, its uuid and its actor; gone with what it carried.</summary>
        [Fact]
        public void One_stands_on_a_map()
        {
            var tc = TaxCollectorStore.Place(7, 100, "Owner", 5000, 12, 300, 3, 35, 109);
            Assert.NotNull(tc);
            Assert.Null(TaxCollectorStore.Place(8, 101, "Other", 5000, 12, 301, 1, 1, 1));

            Assert.Same(tc, TaxCollectorStore.OnMap(5000));
            Assert.Same(tc, TaxCollectorStore.ByUuid(tc.Uuid));
            Assert.Same(tc, TaxCollectorStore.ByContextId(tc.ContextId));
            Assert.Single(TaxCollectorStore.OfAlliance(7));
            Assert.True(tc.ContextId <= TaxCollectorStore.ContextBase);

            TaxCollectorStore.AddItem(tc, 287, 2);
            TaxCollectorStore.AddItem(tc, 287, 1);
            TaxCollectorStore.AddItem(tc, 6903, 1);
            Assert.Equal(new[] { (287, 3), (6903, 1) }, TaxCollectorStore.Items(tc));

            TaxCollectorStore.Remove(tc);
            Assert.Null(TaxCollectorStore.OnMap(5000));
            Assert.Empty(TaxCollectorStore.Items(tc));
            Assert.NotNull(TaxCollectorStore.Place(8, 101, "Other", 5000, 12, 301, 1, 1, 1));
        }

        /// <summary>
        /// The potions' criterion: the alliance right to place one, and the subarea's level inside
        /// the potion's band.
        /// </summary>
        [Fact]
        public void The_potion_asks_for_the_right_and_the_level()
        {
            var alliance = AllianceStore.Create(1, "Recaudadores", "REC", 1, 1, 1, 1, 1);
            AllianceStore.Join(2, alliance.Id, AllianceStore.RankNewcomer);
            const string criterion = "Ox=10&(SL>40&SL<61)";

            Assert.True(TaxCollectorHandler.CriterionHolds(criterion, 1, 50));
            Assert.False(TaxCollectorHandler.CriterionHolds(criterion, 1, 61));
            Assert.False(TaxCollectorHandler.CriterionHolds(criterion, 1, 40));
            Assert.False(TaxCollectorHandler.CriterionHolds(criterion, 2, 50));
            Assert.False(TaxCollectorHandler.CriterionHolds(criterion, 3, 50));
            Assert.True(TaxCollectorHandler.CriterionHolds("", 3, 1));
        }
    
        // ─── What it is besides its place ────────────────────────────────────────

        /// <summary>
        /// Its lea as kx.nxi reads it: the first name in f4, the last in f10, the subarea in f11, the
        /// pods and worth in f2's case 4, its spells in f7 { f2 level, f3 slot }.
        /// </summary>
        [Fact]
        public void Its_lea_carries_what_the_client_reads()
        {
            var tc = new TaxCollectorStore.TaxCollector
            {
                Id = 1, Uuid = "u", OwnerId = 9, OwnerName = "Owner", MapId = 5000, SubAreaId = 956,
                FirstNameId = 84, LastNameId = 67, PlacedUtcS = 1786345052,
            };
            var details = new TaxCollectorProtocol.Details
            {
                Pods = 160, Worth = 622691,
                Spells = new Dictionary<int, int> { [0] = 64128, [1] = 64239 },
            };
            var fields = Fields(TaxCollectorProtocol.Information(tc, Alliance2, 0, details).Build());

            Assert.Equal(84, fields.Numbers[4]);
            Assert.Equal(67, fields.Numbers[10]);
            Assert.Equal(956, fields.Numbers[11]);
            Assert.Equal(1786345052, fields.Numbers[8]);
            Assert.Equal(Hex("220708a00110e38026"), fields.Blocks[2][0]);
            Assert.Equal(new[] { "1080f503", "10eff5031801" },
                         fields.Blocks[7].Select(b => Convert.ToHexString(b).ToLowerInvariant()).ToArray());
        }

        /// <summary>A spell set where another was, out of the slot it had; two slots swapped.</summary>
        [Fact]
        public void Spells_are_set_removed_and_swapped()
        {
            var spells = new Dictionary<int, int> { [0] = 64128, [1] = 64239, [2] = 63924 };

            var set = TaxCollectorHandler.WithSpell(spells, 4, 64239);
            Assert.Equal(new Dictionary<int, int> { [0] = 64128, [2] = 63924, [4] = 64239 }, set);

            var swapped = TaxCollectorHandler.Swapped(spells, 0, 2);
            Assert.Equal(new Dictionary<int, int> { [0] = 63924, [1] = 64239, [2] = 64128 }, swapped);

            var moved = TaxCollectorHandler.Swapped(spells, 1, 5);
            Assert.Equal(new Dictionary<int, int> { [0] = 64128, [2] = 63924, [5] = 64239 }, moved);

            Assert.Equal(spells, TaxCollectorHandler.Swapped(spells, 1, 6));
        }

        /// <summary>Its six spells and the alliance's preset, stored by slot.</summary>
        [Fact]
        public void Spells_and_the_preset_are_stored()
        {
            var tc = TaxCollectorStore.Place(7, 100, "Owner", 6000, 12, 300, 3, 35, 109);
            TaxCollectorStore.SetSpells(tc, new Dictionary<int, int> { [0] = 64128, [5] = 64136, [9] = 1 });
            Assert.Equal(new Dictionary<int, int> { [0] = 64128, [5] = 64136 }, TaxCollectorStore.SpellsOf(tc));

            TaxCollectorStore.SetPreset(7, new Dictionary<int, int> { [1] = 64239 });
            Assert.Equal(new Dictionary<int, int> { [1] = 64239 }, TaxCollectorStore.PresetOf(7));
            Assert.Empty(TaxCollectorStore.PresetOf(8));

            TaxCollectorStore.Remove(tc);
            Assert.Empty(TaxCollectorStore.SpellsOf(tc));
        }

        /// <summary>What happened while away is kept for the owner, and given once.</summary>
        [Fact]
        public void What_happened_while_away_is_told_once()
        {
            TaxCollectorStore.RecordAbsence(55, new TaxCollectorStore.Absence(
                TaxCollectorStore.AbsenceCollected, 35, 109, 88081167, 25, 77, "Hero-Example"));
            TaxCollectorStore.RecordAbsence(55, new TaxCollectorStore.Absence(
                TaxCollectorStore.AbsenceDefeated, 74, 154, 88081168, 25, 0, ""));

            var absences = TaxCollectorStore.TakeAbsences(55);
            Assert.Equal(2, absences.Count);
            Assert.Equal("Hero-Example", absences[0].WhoName);
            Assert.Empty(TaxCollectorStore.TakeAbsences(55));

            var hkx = Fields(TaxCollectorProtocol.BuildAbsences(absences));
            var first = Fields(hkx.Blocks[1][0]);
            Assert.Equal(77, first.Numbers[1]);
            Assert.Equal(109, first.Numbers[2]);
            Assert.Equal(35, first.Numbers[4]);
            Assert.Equal(2, first.Numbers[6]);
            Assert.Equal(3, Fields(hkx.Blocks[1][1]).Numbers[6]);
        }

        /// <summary>
        /// The frames of the window: hkb and kds carry the lea in f2 and f1, hkh the change with the
        /// uuid in f5, hki the spells and the uuid in f3, hlb the preset { sheet, spells, id }.
        /// </summary>
        [Fact]
        public void The_window_s_frames_have_the_client_s_shape()
        {
            var lea = Pb.New().Var(1, 1);
            Assert.Equal(Hex("12020801"), TaxCollectorProtocol.BuildConsult(lea));
            Assert.Equal(Hex("0a020801"), TaxCollectorProtocol.BuildModificationStarted(lea));

            var hkh = Fields(TaxCollectorProtocol.BuildEquipmentChanged("u", true, Pb.New().Var(1, 5), null));
            Assert.Equal(1, hkh.Numbers[3]);
            Assert.Equal("u", System.Text.Encoding.UTF8.GetString(hkh.Blocks[5][0]));
            Assert.False(Fields(TaxCollectorProtocol.BuildEquipmentChanged("u", false, Pb.New(), null)).Numbers.ContainsKey(3));

            var spells = new Dictionary<int, int> { [0] = 64128 };
            Assert.Equal(Hex("0a041080f5031a0175"), TaxCollectorProtocol.BuildSpells("u", spells));
            Assert.Equal(Hex("0a0175120410" + "80f503"), TaxCollectorProtocol.BuildPresetSpells("u", spells));
            Assert.Equal(Hex("0a09120410" + "80f5031a0175"), TaxCollectorProtocol.BuildPresets("u", spells, null));
        }

        /// <summary>The spell requests read back: hle's lli, the slot of each.</summary>
        [Fact]
        public void The_spell_requests_are_read()
        {
            var hle = Pb.New().Str(1, "u").Msg(2, TaxCollectorProtocol.Spell(3, 64136)).Build();
            var (_, texts, blocks) = TaxCollectorProtocol.Read(hle);
            Assert.Equal("u", texts[1]);
            Assert.Equal((64136, 3), TaxCollectorProtocol.ReadSpell(blocks[2]));
        }

        /// <summary>The kinds of item it wears, by the slot they go in: the capture's chests in 5, saddlebags in 4.</summary>
        [Fact]
        public void Its_items_go_in_the_slots_of_their_type()
        {
            Assert.Equal(7, TaxCollectorProtocol.SlotOfType.Count);
            Assert.Equal(5, TaxCollectorProtocol.SlotOfType[279]);
            Assert.Equal(4, TaxCollectorProtocol.SlotOfType[280]);
            Assert.Equal(7, TaxCollectorProtocol.SlotOfType.Values.Distinct().Count());
        }

        /// <summary>
        /// A tax collector's piece is not the character's: its superType shares the character's slot
        /// numbers, so the client drags it onto one (iuk), and it goes back to the bag (ivq 63).
        /// </summary>
        [Fact]
        public async System.Threading.Tasks.Task Its_pieces_are_not_worn_by_the_character()
        {
            if (Forgemagic.TemplateOf(28204) == null) return;   // no item data
            Assert.True(Equipment.IsTaxCollectorPiece(28204));   // Alforjas GaPM, type 280
            Assert.False(Equipment.IsTaxCollectorPiece(10800));  // a cape

            const long character = 990000041;
            var piece = DatabaseManager.AddPieceToInventory(character, 28204, "[]");
            try
            {
                await using var pipe = await Economy.ClientPipe.OpenAsync(990000040, character, 1);
                using (SessionContext.Push(pipe.Session))
                {
                    Equipment.Add(piece.Uid, 28204, 1, Equipment.Bag, null);
                    await EquipmentHandler.MoveAsync(pipe.ToClient,
                        ConnectionProtocol.Push(Op.Iuk, Pb.New().Var(2, piece.Uid).Var(3, 4).Build()));
                    Assert.Equal(Pb.New().Var(1, piece.Uid).Var(2, Equipment.Bag).Build(), await pipe.Next(Op.Ivq));
                    Assert.Equal(Equipment.Bag, Equipment.ByUid(piece.Uid).Position);
                }
            }
            finally
            {
                using var connection = new SqliteConnection(DatabaseManager.WorldConnectionString);
                connection.Open();
                var clean = connection.CreateCommand();
                clean.CommandText = "DELETE FROM CharacterItems WHERE CharacterId = $c;";
                clean.Parameters.AddWithValue("$c", character);
                clean.ExecuteNonQuery();
            }
        }

        /// <summary>
        /// The spells it may be given: the forty of type 25 -- Sobretasa's 64128, Franqueo's 64136 --
        /// and no other. Read from world.db's SpellTemplates.
        /// </summary>
        [Fact]
        public void Only_tax_collector_spells_are_given()
        {
            Assert.True(TaxCollectorHandler.IsTaxCollectorSpell(64128));
            Assert.True(TaxCollectorHandler.IsTaxCollectorSpell(64136));
            Assert.False(TaxCollectorHandler.IsTaxCollectorSpell(1));
            Assert.Equal((25282, 1), TaxCollectorHandler.SpellOfLevel(64128));
        }

        /// <summary>Equipment bonuses reach the fighter in the fields the engine reads.</summary>
        [Fact]
        public void Equipment_bonuses_reach_the_fighter()
        {
            var fighter = new Fighter { MaxAP = 10, CurrentAP = 10, MaxHP = 3000, CurrentHP = 3000 };
            TaxCollectorHandler.AddTo(fighter, 1, 1);
            TaxCollectorHandler.AddTo(fighter, 11, 200);
            TaxCollectorHandler.AddTo(fighter, 10, 50);
            TaxCollectorHandler.AddTo(fighter, 78, 20);
            Assert.Equal(11, fighter.MaxAP);
            Assert.Equal(3200, fighter.MaxHP);
            Assert.Equal(50, fighter.Strength);
            Assert.Equal(20, fighter.Otra(78));
        }

        // ─── The alliance window and the end of the fight ───────────────────────

        /// <summary>
        /// A person of the alliance fights' list is the fighter of the person's oneof, its f4, with
        /// the look in its f6: gk.mwi reads that one, and the f6 (a rank) left «Attacks» empty.
        /// </summary>
        [Fact]
        public void A_fighter_of_the_list_is_the_fighter_variant()
        {
            byte[] look = { 0x0a, 0x01, 0x02 };
            var (numbers, blocks) = Fields(TaxCollectorProtocol.Fighter(-40001, "78,241", 200, look, TaxCollectorProtocol.FighterListMarker).Build());
            Assert.Equal(-40001, numbers[2]);
            var (_, entry) = Fields(Assert.Single(blocks[1]));
            Assert.False(entry.ContainsKey(6));
            var (breed, fighter) = Fields(Assert.Single(entry[4]));
            Assert.Equal(look, Assert.Single(fighter[6]));
            Assert.Equal(TaxCollectorProtocol.FighterListMarker, breed[7]);
        }

        /// <summary>
        /// A tax collector's name in the list is its two name ids in base 36 joined by a comma: the
        /// client (ffc.bdjh) read «35,220» as 113 and 2664, names that do not exist, and drew no line.
        /// </summary>
        [Fact]
        public void A_tax_collector_s_list_name_is_its_two_ids_in_base_36()
        {
            Assert.Equal("z,64", TaxCollectorProtocol.NameIds(35, 220));
            Assert.Equal("10,0", TaxCollectorProtocol.NameIds(36, 0));
            string name = TaxCollectorProtocol.NameIds(78, 241);
            Assert.Equal(new long[] { 78, 241 }, name.Split(",").Select(p => Convert.ToInt64(FromBase36(p))));
        }

        private static long FromBase36(string text)
            => text.Aggregate(0L, (n, c) => n * 36 + (char.IsDigit(c) ? c - '0' : c - 'a' + 10));

        /// <summary>
        /// A tax collector's end-of-fight entry is a monster's: no alliance in the f4 of the
        /// fighter's oneof (lmk). With it, fkv.bgdw made it a «tax collector» line, and the end
        /// screen drew an empty head, «?», among the enemies (TaxCollectorEndScreenTests).
        /// </summary>
        [Fact]
        public void A_tax_collector_ends_the_fight_as_a_monster()
        {
            var jyg = FightProtocol.BuildFightResults(new[]
            {
                new FightProtocol.FightResult { Fighter = -1 },
            }, 1000);

            var (_, first) = Fields(Assert.Single(Fields(jyg).Blocks[2]));
            Assert.False(Fields(Assert.Single(first[3])).Blocks.ContainsKey(4));
        }

        /// <summary>
        /// A fight gathers for the tax collectors of its territory (subarea), not only of its map:
        /// the client's guide, 1149321. One in another subarea gathers nothing.
        /// </summary>
        [Fact]
        public void A_fight_gathers_for_its_territory()
        {
            var here = TaxCollectorStore.Place(17, 100, "Owner", 9_100_001, 9101, 300, 3, 35, 109);
            var sameTerritory = TaxCollectorStore.Place(18, 101, "Other", 9_100_002, 9101, 300, 3, 35, 109);
            var elsewhere = TaxCollectorStore.Place(19, 102, "Far", 9_100_003, 9102, 300, 3, 35, 109);
            try
            {
                var gathering = TaxCollectorStore.GatheringFor(9_100_009, 9101);
                Assert.Contains(here, gathering);
                Assert.Contains(sameTerritory, gathering);
                Assert.DoesNotContain(elsewhere, gathering);
            }
            finally
            {
                foreach (var tc in new[] { here, sameTerritory, elsewhere })
                    if (tc != null) TaxCollectorStore.Remove(tc);
            }
        }

        /// <summary>
        /// Where one may stand (Translations 1149321): outdoors or in a dungeon's room -- whose rooms
        /// are indoors --, not in any other indoor map; and one per group of territories, a
        /// dungeon's rooms going with the territory of its entrance.
        /// </summary>
        [Fact]
        public void Dungeons_take_them_one_per_group_of_territories()
        {
            if (!DungeonManager.IsLoaded) DungeonManager.Initialize();
            if (MapManager.GetMapInfo(120063489) == null) MapManager.Initialize();
            var dungeon = DungeonManager.Get(1);   // Corte del Jalató Real
            var room = dungeon == null ? null : MapManager.GetMapInfo(dungeon.FirstRoom);
            var entrance = dungeon == null ? null : MapManager.GetMapInfo(dungeon.EntranceMapId);
            if (room == null || entrance == null) return;   // no world data

            var alliance = AllianceStore.Create(990000051, "Mazmorras", "MAZ", 1, 1, 1, 1, 1);
            Assert.False(room.Outdoor);
            Assert.Equal(-1, TaxCollectorHandler.WhyNotHere(alliance, 990000051, room, 0));
            var indoor = new MapInfo { MapId = 1, SubAreaId = entrance.SubAreaId, Outdoor = false };
            Assert.Equal(TaxCollectorProtocol.ErrorCantHireHere, TaxCollectorHandler.WhyNotHere(alliance, 990000051, indoor, 0));

            Assert.Equal(entrance.SubAreaId, TaxCollectorHandler.GroupOf(room.MapId, room.SubAreaId));
            Assert.Equal(entrance.SubAreaId, TaxCollectorHandler.GroupOf(entrance.MapId, entrance.SubAreaId));
            Assert.Equal(5, TaxCollectorHandler.FightPeopleCap);
        }

        /// <summary>
        /// The Temple of Alliances' board: «Consultar» marks it in use (iwn) and sends the tops (hks)
        /// -- the ten richest outside the dungeons in f1, by the worth of what they carry, and those
        /// in a dungeon in f3, each a whole lea.
        /// </summary>
        [Fact]
        public async System.Threading.Tasks.Task The_board_shows_the_richest_ten()
        {
            if (!DungeonManager.IsLoaded) DungeonManager.Initialize();
            long room = DungeonManager.All.Values.Select(d => d.FirstRoom).FirstOrDefault(r => r != 0);
            var alliance = AllianceStore.Create(990000031, "Tops", "TOP", 1, 1, 1, 1, 1);
            var placed = new List<TaxCollectorStore.TaxCollector>();
            try
            {
                for (int i = 1; i <= 12; i++)
                {
                    var tc = TaxCollectorStore.Place(alliance.Id, 100 + i, "Owner", 9_200_000 + i, 9201, 300, 3, 35, 109);
                    TaxCollectorStore.AddItem(tc, 287, i);
                    placed.Add(tc);
                }
                TaxCollectorStore.TaxCollector inDungeon = null;
                if (room != 0 && TaxCollectorStore.OnMap(room) == null)
                {
                    inDungeon = TaxCollectorStore.Place(alliance.Id, 200, "Deep", room, 9202, 300, 3, 35, 109);
                    TaxCollectorStore.AddItem(inDungeon, 287, 50);
                    placed.Add(inDungeon);
                }

                // The richest ten, richest first; the two poorest left out.
                var top = TaxCollectorHandler.TopOf(placed.Take(12));
                Assert.Equal(placed.Take(12).Reverse().Take(TaxCollectorHandler.TopSize), top);

                await using var pipe = await Economy.ClientPipe.OpenAsync(990000030, 990000031, TaxCollectorHandler.TopsMap);
                using (SessionContext.Push(pipe.Session))
                    await TaxCollectorHandler.ShowTopsAsync(pipe.ToClient, TaxCollectorHandler.TopsBoard, TaxCollectorHandler.TopsSkill);
                Assert.Equal(ConnectionProtocol.BuildElementInUse(TaxCollectorHandler.TopsBoard, TaxCollectorHandler.TopsSkill, 990000031),
                             await pipe.Next(Op.Iwn));
                var (_, blocks) = Fields(await pipe.Next(Op.Hks));
                string[] world = blocks[1].Select(b => Fields(b).Blocks[12][0]).Select(System.Text.Encoding.UTF8.GetString).ToArray();
                Assert.Equal(top.Select(t => t.Uuid), world.Where(u => placed.Any(t => t.Uuid == u)));
                Assert.False(blocks.ContainsKey(2));
                Assert.True(room == 0 || inDungeon != null);
                if (inDungeon != null)
                {
                    Assert.DoesNotContain(inDungeon.Uuid, world);
                    Assert.Contains(blocks[3], b => System.Text.Encoding.UTF8.GetString(Fields(b).Blocks[12][0]) == inDungeon.Uuid);
                }
            }
            finally
            {
                foreach (var tc in placed) TaxCollectorStore.Remove(tc);
            }
        }

        /// <summary>«Leave» on a tax collector's dialogue closes it with the conversation's kld (reason 1).</summary>
        [Fact]
        public async System.Threading.Tasks.Task Leave_closes_the_talk()
        {
            await using var pipe = await Economy.ClientPipe.OpenAsync(990000020, 990000021, 1);
            using (SessionContext.Push(pipe.Session))
            {
                Assert.False(await TaxCollectorHandler.CloseTalkAsync(pipe.ToClient));
                pipe.Session.State.OpenActorDialogue = -40001;
                Assert.True(await TaxCollectorHandler.CloseTalkAsync(pipe.ToClient));
                Assert.Equal(ConnectionProtocol.BuildDialogClosed(ConnectionProtocol.NpcDialogCloseReason),
                             await pipe.Next(Op.Kld));
                Assert.Equal(0, pipe.Session.State.OpenActorDialogue);
            }
        }

        private static (Dictionary<int, long> Numbers, Dictionary<int, List<byte[]>> Blocks) Fields(byte[] message)
        {
            var numbers = new Dictionary<int, long>();
            var blocks = new Dictionary<int, List<byte[]>>();
            foreach (var field in ProtoMessage.Parse(message).Fields)
            {
                if (field.WireType == 0) numbers[field.FieldNumber] = field.VarIntValue;
                else if (field.WireType == 2)
                {
                    if (!blocks.TryGetValue(field.FieldNumber, out var list)) blocks[field.FieldNumber] = list = new List<byte[]>();
                    list.Add(field.BytesValue);
                }
            }
            return (numbers, blocks);
        }
    }
}
