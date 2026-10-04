using System.Linq;
using System.Text;
using Jondo.Unity.Server;
using Jondo.Unity.Server.Handlers;
using Jondo.Unity.Server.Managers;
using Jondo.Unity.Server.Network;
using Jondo.Unity.World.Fights;
using Xunit;

namespace Jondo.Unity.Tests.World
{
    /// <summary>
    /// A dream whole: its five bands, 26 rows deep as the invitation capture measures it, and the
    /// Fin du rêve at the end, with its waves and what winning or losing it does to the dream.
    /// </summary>
    [Collection("MapManager")]
    public class DreamFinalTests
    {
        /// <summary>No character has this id: what the handler writes to the base is cleaned up after.</summary>
        private const long Dreamer = 900_000_000_001;

        private static Dreams.Sueno Whole(int difficulty = 9)
        {
            Interactives.Initialize();
            Dreams.OlvidarTodo();
            var dream = Dreams.Crear(Dreamer, "Prueba", 200, difficulty, 100, 200);
            for (int band = 2; band <= Dreams.Bands; band++) Dreams.AnadirFranja(dream);
            return dream;
        }

        /// <summary>Rows 0 to 26, fountains at 4, 10, 16 and 25, band IV closed by a lone fight room.</summary>
        [Fact]
        public void Five_bands_are_26_rows_deep()
        {
            var dream = Whole();

            Assert.Equal(Dreams.Bands, dream.Franja);
            Assert.Equal(26, dream.Salas.Max(r => r.Fila));
            Assert.Equal(new[] { 4, 10, 16, 25 }, dream.Salas.Where(r => r.EsFuente).Select(r => r.Fila).OrderBy(f => f));

            var lone = Assert.Single(dream.Salas, r => r.Fila == 22);
            Assert.True(lone.Cierre);
            Assert.False(lone.EsFuente);
            Assert.NotEmpty(lone.Miembros);

            var end = Assert.Single(dream.Salas, r => r.Fila == 26);
            Assert.True(end.EsFinal);
            Assert.NotEmpty(end.Miembros);
            Assert.Empty(end.Salidas);

            // Nobody left out of reach, and a sixth band never comes.
            foreach (var room in dream.Salas.Where(r => r.Id != 0))
                Assert.Contains(dream.Salas, r => r.Salidas.Contains(room.Id));
            int rooms = dream.Salas.Count;
            Dreams.AnadirFranja(dream);
            Assert.Equal(rooms, dream.Salas.Count);
        }

        /// <summary>
        /// Band widths as measured: two to four rooms a row; 13 to 17 over the five rows of bands
        /// II to IV; band V's two rows of fights -- the first behind a lone room of three doors --
        /// each paying 10 dream points, row 22's too.
        /// </summary>
        [Fact]
        public void The_bands_have_their_measured_widths()
        {
            var dream = Whole();
            int Width(int row) => dream.Salas.Count(r => r.Fila == row);

            foreach (int start in new[] { 5, 11, 17 })
            {
                int total = Enumerable.Range(start, 5).Sum(Width);
                Assert.InRange(total, 13, 17);
                Assert.All(Enumerable.Range(start, 5), row => Assert.InRange(Width(row), 2, 4));
            }
            Assert.InRange(Width(23), 2, 3);
            Assert.InRange(Width(24), 2, 4);

            foreach (var room in dream.Salas.Where(r => r.Fila >= 22 && !r.EsFuente && !r.EsFinal && !r.Senalada))
                Assert.Equal(10, room.DreamPoints);
        }

        /// <summary>What closes a band opens the next: fountains I to III and row 22, never band V's fountain.</summary>
        [Fact]
        public void The_rooms_that_close_a_band()
        {
            var dream = Whole();

            var closing = dream.Salas.Where(Dreams.Closes).Select(r => r.Fila).OrderBy(f => f);
            Assert.Equal(new[] { 4, 10, 16, 22 }, closing);
            Assert.All(dream.Salas.Where(r => r.Fila < 26 && r.Id != 0), r => Assert.NotEmpty(r.Salidas));
        }

        /// <summary>The end in the band V graph: type 4, row 26, marked -- "{f1 score, f5 4, f6 26, f7 1}".</summary>
        [Fact]
        public void The_end_goes_to_the_client_as_type_4()
        {
            var dream = Whole();
            var end = dream.Salas.Single(r => r.EsFinal);

            var graphs = ProtoMessage.Parse(DreamProtocol.BuildDreamState(dream)).Fields.Where(f => f.FieldNumber == 16).ToList();
            Assert.Equal(Dreams.Bands, graphs.Count);

            var rooms = ProtoMessage.Parse(graphs[^1].BytesValue).Fields.Where(f => f.FieldNumber == 1);
            var entry = rooms.Select(r => ProtoMessage.Parse(r.BytesValue).Fields)
                             .Single(r => Encoding.UTF8.GetString(r.Single(f => f.FieldNumber == 1).BytesValue) == end.Id.ToString());
            var body = ProtoMessage.Parse(entry.Single(f => f.FieldNumber == 2).BytesValue).Fields;

            Assert.Equal(4, (int)body.Single(f => f.FieldNumber == 5).VarIntValue);
            Assert.Equal(26, (int)body.Single(f => f.FieldNumber == 6).VarIntValue);
            Assert.Equal(1, (int)body.Single(f => f.FieldNumber == 7).VarIntValue);
            Assert.Equal(end.Score, (int)body.Single(f => f.FieldNumber == 1).VarIntValue);
        }

        /// <summary>The guide's figures for the Fin du rêve, by difficulty.</summary>
        [Theory]
        [InlineData(1, 250, 5, 1, 5)]
        [InlineData(3, 250, 5, 1, 5)]
        [InlineData(4, 275, 10, 3, 15)]
        [InlineData(7, 275, 10, 3, 15)]
        [InlineData(8, 300, 15, 3, 0)]
        [InlineData(10, 300, 15, 3, 0)]
        public void The_end_s_rules_by_difficulty(int difficulty, int level, int step, int min, int max)
            => Assert.Equal(new Dreams.FinalRules(level, step, min, max), Dreams.FinalRulesOf(difficulty));

        /// <summary>Four fighters in the first wave, one more every two, eight at most.</summary>
        [Fact]
        public void Waves_grow()
        {
            Interactives.Initialize();
            MobSpawnManager.EnsureMonsterData();
            Dreams.OlvidarTodo();
            Dreams.Crear(Dreamer, "Prueba", 200, 1, 100, 200);

            Assert.Equal(4, Dreams.FinalWave(1).Count);
            Assert.Equal(5, Dreams.FinalWave(3).Count);
            Assert.Equal(8, Dreams.FinalWave(9).Count);
            Assert.Equal(8, Dreams.FinalWave(30).Count);
        }

        /// <summary>Brought to a level, a monster's life and characteristics grow with it; never down.</summary>
        [Fact]
        public void A_monster_brought_to_a_level()
        {
            var monster = new Fighter { Level = 100, MaxHP = 1000, CurrentHP = 400, Strength = 200, Initiative = 300, CurrentAP = 8 };

            Dreams.ScaleTo(monster, 250);

            Assert.Equal(250, monster.Level);
            Assert.Equal(2500, monster.MaxHP);
            Assert.Equal(2500, monster.CurrentHP);
            Assert.Equal(500, monster.Strength);
            Assert.Equal(750, monster.Initiative);
            Assert.Equal(8, monster.CurrentAP);

            Dreams.ScaleTo(monster, 120);
            Assert.Equal(250, monster.Level);
            Assert.Equal(2500, monster.MaxHP);
        }

        /// <summary>The end fight in the dream's last room: its wave at its level, and one fighting the end.</summary>
        private static FightInstance EndFight(Dreams.Sueno dream, long fightId)
        {
            var end = dream.Salas.Single(r => r.EsFinal);
            dream.Actual = end.Id;
            var fight = new FightInstance(fightId, end.MapaDeLaSala);
            fight.AddMonster(new Fighter { Id = -1, Level = 200, MaxHP = 1000, CurrentHP = 1000, IsMonster = true });
            return fight;
        }

        private static void AsTheDreamer(System.Action body)
        {
            var session = GameSession.SinSocket();
            session.State.CharacterId = Dreamer;
            try
            {
                using (SessionContext.Push(session)) body();
            }
            finally
            {
                DatabaseManager.DeleteDream(Dreamer);
            }
        }

        /// <summary>
        /// A Rêve's end: the first wave at 250, then 255, 260, 265 and 270, and no sixth. Won,
        /// the dream is over: forgotten, and the player out where he came from.
        /// </summary>
        [Fact]
        public void A_reve_s_end_has_five_waves_and_ends_the_dream() => AsTheDreamer(() =>
        {
            var dream = Whole(difficulty: 2);
            var fight = EndFight(dream, 7_000_001);

            DreamHandler.OnFightCreated(fight);
            Assert.Equal(250, fight.Rojo[0].Level);
            Assert.Equal(1250, fight.Rojo[0].MaxHP);

            foreach (int level in new[] { 255, 260, 265, 270 })
            {
                var next = DreamHandler.NextWave(fight);
                Assert.NotNull(next);
                Assert.Equal(level, next!.Value.Level);
                Assert.NotEmpty(next.Value.Members);
            }
            Assert.Null(DreamHandler.NextWave(fight));

            var (map, cell, notice, ended) = DreamHandler.AfterTheFight(fight, won: true);
            Assert.True(ended);
            Assert.Equal(100, map);
            Assert.Equal(200, cell);
            Assert.Contains("5 oleada", notice);
            Assert.Contains("250 retazo", notice);   // 50 dream fragments a wave in a Rêve II
            Assert.Null(Dreams.De(Dreamer));
        });

        /// <summary>A Cauchemar's end never runs out of waves: it ends when the player falls, won from the third.</summary>
        [Fact]
        public void A_cauchemar_s_end_counts_the_waves_that_fell() => AsTheDreamer(() =>
        {
            var dream = Whole(difficulty: 9);
            var fight = EndFight(dream, 7_000_002);

            DreamHandler.OnFightCreated(fight);
            for (int wave = 2; wave <= 20; wave++)
                Assert.Equal(300 + 15 * (wave - 1), DreamHandler.NextWave(fight)!.Value.Level);

            var (_, _, notice, ended) = DreamHandler.AfterTheFight(fight, won: false);
            Assert.True(ended);
            Assert.Contains("completado", notice);
        });

        /// <summary>Falling before the third wave of a Paradoxe, with no arena left, loses the dream.</summary>
        [Fact]
        public void Falling_too_soon_loses_the_dream() => AsTheDreamer(() =>
        {
            var dream = Whole(difficulty: 5);
            dream.Arena = 0;
            var fight = EndFight(dream, 7_000_003);

            DreamHandler.OnFightCreated(fight);
            DreamHandler.NextWave(fight);

            var (map, _, notice, ended) = DreamHandler.AfterTheFight(fight, won: false);
            Assert.True(ended);
            Assert.Equal(100, map);
            Assert.DoesNotContain("completado", notice);
            Assert.Null(Dreams.De(Dreamer));
        });

        /// <summary>A Draconiros arena is a second go: spent on a loss, the dream and the room stay.</summary>
        [Fact]
        public void An_arena_is_a_second_go() => AsTheDreamer(() =>
        {
            var dream = Whole(difficulty: 1);
            Assert.Equal(1, dream.Arena);
            var room = dream.Salas.First(r => r.Fila == 1);
            dream.Actual = room.Id;
            var fight = new FightInstance(7_000_004, room.MapaDeLaSala);

            var (map, _, notice, ended) = DreamHandler.AfterTheFight(fight, won: false);
            Assert.False(ended);
            Assert.Equal(0, map);
            Assert.NotNull(notice);
            Assert.Equal(0, dream.Arena);
            Assert.Same(dream, Dreams.De(Dreamer));

            // The second loss is the last.
            (_, _, _, ended) = DreamHandler.AfterTheFight(fight, won: false);
            Assert.True(ended);
        });

        /// <summary>A room won is not the end of anything: no move, no notice, the dream as it was.</summary>
        [Fact]
        public void A_room_won_changes_nothing_here() => AsTheDreamer(() =>
        {
            var dream = Whole(difficulty: 1);
            var room = dream.Salas.First(r => r.Fila == 1);
            dream.Actual = room.Id;

            var result = DreamHandler.AfterTheFight(new FightInstance(7_000_005, room.MapaDeLaSala), won: true);

            Assert.Equal((0L, 0, (string?)null, false), result);
            Assert.Same(dream, Dreams.De(Dreamer));
        });
    }
}
