using System;
using System.Linq;
using Jondo.Unity.Server.Handlers;
using Jondo.Unity.Server.Managers;
using Jondo.Unity.Server.Network;
using Jondo.Unity.World.Fights;
using Jondo.Unity.World.Maps;
using Xunit;

namespace Jondo.Unity.Tests.Combat
{
    /// <summary>
    /// The Koliseo's JondoBots: the fourth card of the window, the JondoBot's sheet as asked for,
    /// and a turn thought out with its class's own spells for every class.
    /// </summary>
    [Collection("koliseo")]
    public class KoliseoBotTests
    {
        private const int Centre = 300;

        private static int CellAt(int from, int distance)
        {
            foreach (int cell in Enumerable.Range(0, 560))
                if (MapGeometry.Distance(from, cell) == distance) return cell;
            return -1;
        }

        [Fact]
        public void The_fourth_card_is_an_open_1v1_against_JondoBots()
        {
            var mode = Assert.Single(KoliseoHandler.Modes, m => m.Index == KoliseoHandler.JondoBotMode);
            Assert.Equal((1, true, false), (mode.TeamSize, mode.Open, mode.Inner));

            // Its entry: mode 3, open, not a default mode, a 1v1, running for the season.
            var ltd = ProtoMessage.Parse(KoliseoHandler.BuildModes(KoliseoHandler.Modes));
            var entry = ltd.Fields.Where(f => f.FieldNumber == 1).Select(f => ProtoMessage.Parse(f.BytesValue)).Last();
            Assert.Equal(3, entry.Fields.Single(f => f.FieldNumber == 1).VarIntValue);
            Assert.Equal(1, entry.Fields.Single(f => f.FieldNumber == 3).VarIntValue);
            var settings = ProtoMessage.Parse(entry.Fields.Single(f => f.FieldNumber == 2).BytesValue);
            Assert.DoesNotContain(settings.Fields, f => f.FieldNumber == 1);
            Assert.Equal(1, settings.Fields.Single(f => f.FieldNumber == 4).VarIntValue);
            var season = KoliseoLadder.Current();
            string start = System.Text.Encoding.UTF8.GetString(settings.Fields.Single(f => f.FieldNumber == 2).BytesValue);
            Assert.Equal(season.StartUtc, DateTime.Parse(start, null, System.Globalization.DateTimeStyles.AdjustToUniversal));
            Assert.Single(settings.Fields, f => f.FieldNumber == 3);
        }

        [Fact]
        public void A_JondoBot_is_level_200_with_12_ap_6_mp_1500_everywhere_and_6666_life()
        {
            var spec = KoliseoBots.Create(8);
            try
            {
                Assert.True(KoliseoBots.IsBot(spec.Id));
                Assert.Equal("JondoBot Yopuka", spec.Name);
                var bot = KoliseoBots.BuildFighter(spec);
                Assert.True(bot.IsBot);
                Assert.False(bot.IsMonster);
                Assert.True(bot.IsReady);
                Assert.Equal((200, 12, 6, 6666, 6666), (bot.Level, bot.MaxAP, bot.MaxMP, bot.MaxHP, bot.CurrentHP));
                Assert.Equal((1500, 1500, 1500, 1500), (bot.Strength, bot.Intelligence, bot.Chance, bot.Agility));
                Assert.NotEmpty(bot.BotLook!);
                // A level-200 Yopuka's spells, one of each pair, every one at its highest grade.
                Assert.True(bot.SpellIds.Count >= 20);
                foreach (int spell in bot.SpellIds)
                    Assert.Equal(SpellTable.GradeFor(spell, 200), bot.SpellGrades[spell]);
                var pairs = SpellTable.PairsOf(8);
                Assert.All(pairs, pair => Assert.True(bot.SpellIds.Count(s => pair.Holds(s)) <= 1));
            }
            finally { KoliseoBots.Forget(spec.Id); }
        }

        /// <summary>
        /// A JondoBot wears the look of a notable NPC of its class when there is one: placed in the
        /// world, with dialogue, dressed; its body skin is its class's.
        /// </summary>
        [Fact]
        public void A_JondoBot_looks_like_a_notable_npc_of_its_class()
        {
            Npcs.Initialize();
            var withLooks = SpellTable.ClassBreeds.Where(b => KoliseoBots.NpcLooksOf(b).Count > 0).ToList();
            Assert.True(withLooks.Count >= 10, $"only {withLooks.Count} classes have a notable NPC");
            foreach (int breed in withLooks)
            {
                var spec = KoliseoBots.Create(breed);
                try
                {
                    Assert.NotNull(spec.LooksLike);
                    var npc = spec.LooksLike!;
                    Assert.Contains(npc.Skins[0], new[] { BreedLookTable.Get(breed, 0)!.Skins[0], BreedLookTable.Get(breed, 1)!.Skins[0] });
                    var body = BodyOf(KoliseoBots.BuildFighter(spec).BotLook!);
                    Assert.Equal(npc.Skins[0], Packed(body, 6)[0]);
                }
                finally { KoliseoBots.Forget(spec.Id); }
            }
        }

        private static long[] Packed(ProtoMessage look, int field)
        {
            var raw = look.Fields.FirstOrDefault(f => f.FieldNumber == field);
            if (raw == null) return Array.Empty<long>();
            if (raw.WireType == 0) return new[] { raw.VarIntValue };
            var values = new System.Collections.Generic.List<long>();
            var input = new Google.Protobuf.CodedInputStream(raw.BytesValue);
            while (!input.IsAtEnd) values.Add((long)input.ReadUInt64());
            return values.ToArray();
        }

        /// <summary>The body of a look: the rider inside the mount when it rides, else the root.</summary>
        private static ProtoMessage BodyOf(byte[] look)
        {
            var root = ProtoMessage.Parse(look);
            var rider = root.Fields.FirstOrDefault(f => f.FieldNumber == 7);
            if (rider == null) return root;
            return ProtoMessage.Parse(ProtoMessage.Parse(rider.BytesValue).Fields.First(f => f.FieldNumber == 1).BytesValue);
        }

        /// <summary>
        /// Its outfit is one set's: level 100 or more, two visible pieces or more, a hat, a cape or
        /// a shield each, every one with its skin measured.
        /// </summary>
        [Fact]
        public void A_JondoBots_outfit_is_one_whole_epic_set()
        {
            var outfits = KoliseoBots.Outfits;
            Assert.True(outfits.Count >= 20, $"only {outfits.Count} sets to wear");
            Assert.All(outfits, o =>
            {
                Assert.True(o.Level >= KoliseoBots.OutfitLevel);
                Assert.True(o.Pieces.Count >= 2);
                Assert.Equal(o.Pieces.Count, o.Pieces.Select(p => p.Type).Distinct().Count());
                Assert.True(ItemSets.TryGetItems(o.SetId, out var items));
                Assert.All(o.Pieces, p => Assert.Contains(p.Item, items));
                Assert.All(o.Pieces, p => Assert.Equal(p.Skin, EquipmentSkins.SkinOf(p.Item)));
            });
        }

        /// <summary>
        /// Dressed and mounted: the mount is the root, half again as big; the rider on the rider's
        /// bones, half again as big, wearing the set's pieces -- the set's hat in place of the hat
        /// the NPC had, not on top of it.
        /// </summary>
        [Fact]
        public void A_JondoBot_wears_its_set_rides_its_mount_and_is_half_again_as_big()
        {
            var outfit = KoliseoBots.Outfits.First(o => o.Pieces.Any(p => p.Type == 16));
            int setHat = outfit.Pieces.First(p => p.Type == 16).Skin;
            int oldHat = EquipmentSkins.All.Values.First(s => s != setHat && EquipmentSkins.TypeOfSkin(s) == 16);
            var spec = new KoliseoBots.Spec
            {
                Id = KoliseoBots.FirstId - 1, Breed = 8, Sex = 0, Name = "JondoBot Yopuka",
                LooksLike = new Npcs.Spawn { Bones = 1, Skins = new long[] { 10, 2020, oldHat }, Colors = new long[] { 16777216 }, Scales = new long[] { 100 } },
                Wears = outfit,
                Rides = new Mounts.Look { Bones = 639, Scale = 120, Colors = new long[] { 33549261 } },
            };

            var root = ProtoMessage.Parse(KoliseoBots.LookOf(spec));
            Assert.Equal(639, root.Fields.First(f => f.FieldNumber == 3).VarIntValue);
            Assert.Equal(new long[] { 180 }, Packed(root, 5));

            var body = BodyOf(KoliseoBots.LookOf(spec));
            Assert.Equal(Mounts.RiderBones, body.Fields.First(f => f.FieldNumber == 3).VarIntValue);
            Assert.Equal(new long[] { 150 }, Packed(body, 5));
            var skins = Packed(body, 6);
            Assert.Equal(new long[] { 10, 2020 }, skins.Take(2).ToArray());
            Assert.DoesNotContain(oldHat, skins);
            Assert.All(outfit.Pieces, p => Assert.Contains((long)p.Skin, skins));
        }

        /// <summary>On foot the body is the root, and still half again as big.</summary>
        [Fact]
        public void A_JondoBot_on_foot_is_half_again_as_big()
        {
            var spec = new KoliseoBots.Spec
            {
                Id = KoliseoBots.FirstId - 2, Breed = 8, Sex = 0, Name = "JondoBot Yopuka",
                LooksLike = new Npcs.Spawn { Bones = 1, Skins = new long[] { 10, 2020 }, Colors = new long[] { 16777216 }, Scales = new long[] { 100 } },
            };
            var root = ProtoMessage.Parse(KoliseoBots.LookOf(spec));
            Assert.Equal(1, root.Fields.First(f => f.FieldNumber == 3).VarIntValue);
            Assert.Equal(new long[] { 150 }, Packed(root, 5));
            Assert.DoesNotContain(root.Fields, f => f.FieldNumber == 7);
        }

        /// <summary>For every class, the JondoBot finds something worth doing to an enemy in reach.</summary>
        [Fact]
        public void Every_class_of_JondoBot_plays_its_turn_with_its_spells()
        {
            foreach (int breed in SpellTable.ClassBreeds)
            {
                var spec = KoliseoBots.Create(breed);
                try
                {
                    var bot = KoliseoBots.BuildFighter(spec);
                    bot.TeamId = 1;
                    bot.CellId = Centre;
                    var enemy = new Fighter
                    {
                        Id = 1, TeamId = 0, CellId = CellAt(Centre, 3), MaxHP = 3000, CurrentHP = 3000,
                        CurrentAP = 11, CurrentMP = 6, Level = 200,
                    };
                    var spells = FightHandler.TacticsOf(bot);
                    Assert.True(spells.Count > 0, $"class {breed} has no spell the tactics can weigh");
                    var action = MonsterTactics.Next(new MonsterTactics.Board { Fighters = new[] { bot, enemy } }, bot, spells);
                    Assert.True(action != null, $"a JondoBot of class {breed} does nothing");
                }
                finally { KoliseoBots.Forget(spec.Id); }
            }
        }
    }
}
