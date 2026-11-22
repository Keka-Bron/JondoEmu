using System.Linq;
using Jondo.Unity.Server;
using Jondo.Unity.Server.Managers;
using Xunit;

namespace Jondo.Unity.Tests.World
{
    /// <summary>
    /// Elmer Zenario's expeditions: each of his groups of replies matched to its dungeon by name.
    /// </summary>
    public class ExpeditionsTests
    {
        private static readonly (int, string, bool)[] Catalogue =
        {
            (143, "Expedición - Tofullinero Real", true),
            (144, "Expedición - Sala de Minotot", false),
            (167, "Expedición de Audacia - Cueva de Bworker", true),
            (187, "Expedición de Bravura - Cueva de Bworker", true),
            (205, "Expedición de Bravura - Sala de Minotot", true),
            (12, "Cueva de Bworker", true),
        };

        // Three groups in id order, as Elmer declares them; the fourth names a dungeon without rooms.
        private static readonly (long, string)[] Replies =
        {
            (81885, "Sí, quiero volver a esta sala."),
            (81886, "Retomar la expedición donde la dejaste."),
            (81887, "Comenzar la expedición."),
            (81888, "Seleccionar otra expedición."),
            (81889, "Seleccionar la expedición del Tofullinero Real."),
            (81890, "Sí, quiero volver a esta sala."),
            (81891, "Retomar la expedición donde la dejaste."),
            (81892, "Comenzar la expedición."),
            (81893, "Seleccionar otra expedición."),
            (81894, "Seleccionar la expedición de la Sala de Minotot."),
            (81986, "Sí, quiero volver a esta sala."),
            (81987, "Retomar la expedición donde la dejaste."),
            (81988, "Comenzar la expedición."),
            (81989, "Seleccionar otra expedición."),
            (81990, "Seleccionar la expedición de audacia de la Cueva de Bworker."),
            (82086, "Sí, quiero volver a esta sala."),
            (82087, "Retomar la expedición donde la dejaste."),
            (82088, "Comenzar la expedición."),
            (82089, "Seleccionar otra expedición."),
            (82090, "Seleccionar la expedición de bravura de la Cueva de Bworker."),
            (82091, "Sí, quiero volver a esta sala."),
            (82092, "Retomar la expedición donde la dejaste."),
            (82093, "Comenzar la expedición."),
            (82094, "Seleccionar otra expedición."),
            (82095, "Seleccionar la expedición de bravura de la Sala del Trono de Minotot."),
            (81985, "¿Qué expediciones puede hacer mi gremio?"),
        };

        private static readonly (long, string)[] Messages =
        {
            (59745, "Hola."),
            (59796, "¡Muy bien! ¿Qué expedición debes realizar?"),
            (59797, "¿Seguro que quieres entrar en el Tofullinero Real?"),
            (59798, "¿Quieres volver a: $map#1?"),
            (59799, "¿Seguro que quieres entrar en la Sala de Minotot?"),
            (59800, "¿Quieres volver a: $map#1?"),
            (59801, "¿Seguro que quieres entrar en la Cueva de Bworker?"),
            (59802, "¿Quieres volver a: $map#1?"),
            (59803, "¿Seguro que quieres entrar en la Cueva de Bworker?"),
            (59804, "¿Quieres volver a: $map#1?"),
        };

        private static System.Collections.Generic.List<Expeditions.Offer> Parse(out System.Collections.Generic.List<string> skipped)
            => Expeditions.Parse(Replies.Select(r => r.Item1).ToArray(), Replies.Select(r => r.Item2).ToArray(),
                                 Messages.Select(m => m.Item1).ToArray(), Messages.Select(m => m.Item2).ToArray(),
                                 Catalogue, out skipped);

        [Fact]
        public void Each_group_opens_the_dungeon_it_names()
        {
            var offers = Parse(out _);

            Assert.Equal(new[] { 143, 167, 187, 205 }, offers.Select(o => o.DungeonId));
            var bravura = offers.Single(o => o.DungeonId == 187);
            Assert.Equal(82090, bravura.Select);
            Assert.Equal(82088, bravura.Begin);
            Assert.Equal(82087, bravura.Resume);
            Assert.Equal(82089, bravura.Other);
            Assert.Equal(82086, bravura.ReturnYes);
        }

        [Fact]
        public void A_name_worded_longer_than_the_catalogue_still_finds_its_dungeon()
        {
            // «Sala del Trono de Minotot» in Elmer's line, «Sala de Minotot» in the catalogue.
            Assert.Equal(82095, Parse(out _).Single(o => o.DungeonId == 205).Select);
        }

        [Fact]
        public void The_k_th_confirmation_belongs_to_the_k_th_group()
        {
            var offers = Parse(out _);

            Assert.Equal(59797, offers.Single(o => o.DungeonId == 143).Confirm);
            Assert.Equal(59801, offers.Single(o => o.DungeonId == 167).Confirm);
            Assert.Equal(59803, offers.Single(o => o.DungeonId == 187).Confirm);
        }

        [Fact]
        public void A_dungeon_with_no_rooms_in_this_client_is_left_out()
        {
            Parse(out var skipped);

            Assert.Equal(new[] { "|sala de minotot" }, skipped);
        }

        [Theory]
        [InlineData("Seleccionar la expedición del Tofullinero Real.", "|tofullinero real")]
        [InlineData("Seleccionar la expedición de los Bosques.", "|bosques")]
        [InlineData("Seleccionar la expedición de audacia de la Cueva de Bworker.", "audacia|cueva de bworker")]
        [InlineData("Seleccionar la expedición de bravura del Tofullinero Real.", "bravura|tofullinero real")]
        [InlineData("Seleccionar la exploración de audacia de la Cervecería del Rey Dazahk.", "audacia|cervecería del rey dazahk")]
        public void The_selection_names_family_and_dungeon(string text, string key)
            => Assert.Equal(key, Expeditions.SelectionKey(text));

        [Theory]
        [InlineData("Expedición - Tofullinero Real", "|tofullinero real")]
        [InlineData("Expedición de Audacia - Cueva de Bworker", "audacia|cueva de bworker")]
        [InlineData("Expedición de Bravura - Cueva de Bworker", "bravura|cueva de bworker")]
        [InlineData("Cueva de Bworker", null)]
        [InlineData("Dimensión - Ecaflipus", null)]
        public void Only_expeditions_have_a_catalogue_key(string name, string? key)
            => Assert.Equal(key, Expeditions.CatalogueKey(name));

        [Fact]
        public void An_expedition_takes_the_monster_of_its_rooms_named_like_the_base_boss()
        {
            var names = new System.Collections.Generic.Dictionary<int, string>
            {
                [854] = "Cocabulia", [7906] = "Cocabulia", [7901] = "Coco Moreno", [3] = "Otro",
            };
            string? NameOf(int m) => names.TryGetValue(m, out var n) ? n : null;

            Assert.Equal(new[] { 7906 }, DungeonManager.BossesOfTheExpedition(new[] { 854 }, new[] { 7901, 7906 }, NameOf));
            Assert.Equal(new[] { 854 }, DungeonManager.BossesOfTheExpedition(new[] { 854 }, new[] { 854, 7901 }, NameOf));
            Assert.Empty(DungeonManager.BossesOfTheExpedition(new[] { 854 }, new[] { 7901, 3 }, NameOf));
        }
    }

    /// <summary>Elmer's groups against the real catalogue, and where he stands.</summary>
    [Collection("MapManager")]
    public class ExpeditionsWorldTests
    {
        [Fact]
        public void Elmer_offers_the_expeditions_that_have_rooms()
        {
            if (MapManager.Maps.Count == 0) MapManager.Initialize();
            DungeonManager.Initialize();
            Npcs.Initialize();
            Expeditions.Initialize();
            if (!DungeonManager.IsLoaded || Npcs.TemplateOf(Expeditions.Npc) == null) return;   // no world base

            var ids = Expeditions.Offers.Select(o => o.DungeonId).ToHashSet();
            Assert.Contains(167, ids);
            Assert.Contains(205, ids);
            Assert.Contains(184, ids);   // «Seleccionar la exploración ...» in the client
            Assert.True(ids.Count >= 50, $"only {ids.Count} expeditions matched");
            Assert.Equal(ids.Count, Expeditions.Offers.Count);
            Assert.DoesNotContain(144, ids);
            Assert.DoesNotContain(157, ids);
            Assert.All(ids, id => Assert.NotEmpty(DungeonManager.Get(id)!.Rooms));
            Assert.All(Expeditions.Offers, o => Assert.True(o.Begin != 0 && o.Confirm != 0));

            var talk = Expeditions.Lines!;
            Assert.Equal(59745, talk.Opening);
            Assert.Equal(59796, talk.Which);
            Assert.Equal(81985, talk.WhichExpeditions);
            Assert.Equal(new long[] { 60064, 60065, 60066, 60067 }, talk.Explanation);
        }

        [Fact]
        public void Each_expedition_has_the_boss_of_its_base_dungeon_in_its_last_room()
        {
            if (MapManager.Maps.Count == 0) MapManager.Initialize();
            DungeonManager.Initialize();
            if (!DungeonManager.IsLoaded || DungeonManager.Get(147) is not { } cocabulia) return;   // no world base

            Assert.Equal(new[] { 7906 }, cocabulia.Bosses);
            Assert.Equal(new[] { 7852 }, DungeonManager.Get(143)!.Bosses);
            Assert.Equal(new[] { 478 }, DungeonManager.Get(186)!.Bosses);
            Assert.True(DungeonManager.IsBoss(7906));
        }

        [Theory]
        [InlineData(91889155L, 361)]
        [InlineData(207627266L, 423)]
        [InlineData(54526212L, 385)]
        [InlineData(99093249L, 354)]
        [InlineData(83889162L, 191)]
        public void Elmer_stands_on_a_walkable_cell_of_each_hall(long map, int cell)
        {
            if (MapManager.Maps.Count == 0) MapManager.Initialize();
            if (!MapManager.WalkableCells.ContainsKey(map)) return;   // no map data
            if (Npcs.OnMap(map).Count == 0) Npcs.Initialize();

            Assert.True(MapManager.IsCellWalkable(map, cell));
            Assert.Contains(Npcs.OnMap(map), s => s.NpcId == Expeditions.Npc && s.Cell == cell);
        }
    }
}
