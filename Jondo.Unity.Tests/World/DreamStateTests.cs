using System;
using System.Collections.Generic;
using System.Linq;
using Jondo.Unity.Server.Managers;
using Jondo.Unity.Server.Network;
using Xunit;

namespace Jondo.Unity.Tests.World
{
    /// <summary>
    /// The izg of a dream against the 57 of the captures: the dream points, the bonus, the path
    /// behind, the room's flags and one graph per band.
    /// </summary>
    /// <remarks>
    /// What the dream's panel shows -- the score, the dream points and the bonuses -- comes from
    /// here, and the panel did not appear: the dream points were never sent, the f8 that the
    /// client paints as a percentage was used for them, and the path was the room one stood in.
    /// </remarks>
    [Collection("MapManager")]
    public class DreamStateTests
    {
        private static Dreams.Sueno New(int difficulty = 9, int breed = 0)
        {
            Interactives.Initialize();
            Dreams.OlvidarTodo();
            var dream = Dreams.Crear(1, "Prueba", 200, difficulty, 100, 200, breed);
            Dreams.Enter(dream, 0, out _);
            return dream;
        }

        private static List<ProtoField> Fields(byte[] message) => ProtoMessage.Parse(message).Fields.ToList();

        private static int? Var(List<ProtoField> fields, int number)
            => (int?)fields.FirstOrDefault(f => f.FieldNumber == number && f.WireType == 0)?.VarIntValue;

        private static Dreams.Sala FirstFight(Dreams.Sueno dream)
            => dream.Salas.First(r => r.Miembros.Count > 0 && dream.Buscar(0)!.Salidas.Contains(r.Id));

        /// <summary>Ten in Sueño, five in Paradoja, none in Pesadilla: the f11 of every entrance of the captures.</summary>
        [Theory]
        [InlineData(1, 10)]
        [InlineData(3, 10)]
        [InlineData(4, 5)]
        [InlineData(7, 5)]
        [InlineData(8, 0)]
        [InlineData(10, 0)]
        public void A_dream_starts_with_the_measured_dream_points(int difficulty, int points)
        {
            var dream = New(difficulty);

            Assert.Equal(points, dream.DreamPoints);
            Assert.Equal(points == 0 ? (int?)null : points, Var(Fields(DreamProtocol.BuildDreamState(dream)), 11));
        }

        /// <summary>The Draconiros arena is an f17 in the Sueño dreams and nowhere else.</summary>
        [Theory]
        [InlineData(1, 1)]
        [InlineData(3, 1)]
        [InlineData(4, null)]
        [InlineData(10, null)]
        public void Only_a_sueno_brings_an_arena(int difficulty, int? arena)
            => Assert.Equal(arena, Var(Fields(DreamProtocol.BuildDreamState(New(difficulty))), 17));

        /// <summary>
        /// A room pays on entering, once: its dream points into f11 and its bonus into f15, the
        /// way the captures show them grown in the izg of the room and not after the fight.
        /// </summary>
        private static Dreams.Reward RewardOf(int id) => Dreams.RoomRewards.First(r => r.Id == id);

        [Fact]
        public void A_room_pays_once_on_entering()
        {
            var dream = New(difficulty: 4);
            var room = FirstFight(dream);
            room.Reward = RewardOf(14798);

            Dreams.Enter(dream, room.Id, out var gained);

            Assert.Equal(5 + room.DreamPoints, dream.DreamPoints);
            Assert.Same(room.Regalo, gained);
            Assert.Single(dream.Ganados);

            // Coming back to it -- continuing the dream -- pays nothing more.
            Dreams.Enter(dream, room.Id, out gained);
            Assert.Null(gained);
            Assert.Equal(5 + room.DreamPoints, dream.DreamPoints);
            Assert.Single(dream.Ganados);
        }

        /// <summary>
        /// A room of reward 14931 gives dream points and a storm and no bonus: the Paradoja II one
        /// takes f11 from 5 to 15, its own five and these five, and f7 from 1 to 2.
        /// </summary>
        [Fact]
        public void A_points_room_pays_its_points()
        {
            var dream = New(difficulty: 5);
            var room = FirstFight(dream);
            room.Reward = RewardOf(14931);
            room.DreamPoints = 5;

            Dreams.Enter(dream, room.Id, out var gained);

            Assert.Equal(15, dream.DreamPoints);
            Assert.Equal(2, dream.Tormentas);
            Assert.Null(gained);
            Assert.Empty(dream.Ganados);
        }

        /// <summary>
        /// The f8 is the percentage under the dream's name, and a room of palier I that is not
        /// marked leaves it at the difficulty's: it used to grow with every room won, 220%
        /// becoming 275% in three. What does move it is <see cref="Dreams.LootBonusOf"/>.
        /// </summary>
        [Fact]
        public void Rooms_leave_the_bonus_alone()
        {
            var dream = New(difficulty: 8);
            var room = FirstFight(dream);
            Dreams.Enter(dream, room.Id, out _);
            room.Hecha = true;

            var izg = Fields(DreamProtocol.BuildDreamState(dream));
            Assert.Equal(220, Var(izg, 8));
            Assert.Equal(220, Var(izg, 22));
        }

        /// <summary>
        /// The f3 of the graph is the rooms behind: "0" at the entrance, still "0" in the first
        /// fight room, and that room too once in the next one -- the long capture's "0", "2", "4".
        /// </summary>
        [Fact]
        public void The_path_is_the_rooms_behind()
        {
            var dream = New();
            Assert.Equal(new[] { 0 }, dream.Visited);

            var first = FirstFight(dream);
            Dreams.Enter(dream, first.Id, out _);
            Assert.Equal(new[] { 0 }, dream.Visited);

            int next = first.Salidas[0];
            Dreams.Enter(dream, next, out _);
            Assert.Equal(new[] { first.Id, 0 }, dream.Visited);

            // And in the izg, as strings, in every graph.
            var graph = Fields(Fields(DreamProtocol.BuildDreamState(dream)).First(f => f.FieldNumber == 16).BytesValue!);
            var path = graph.Where(f => f.FieldNumber == 3).Select(f => System.Text.Encoding.UTF8.GetString(f.BytesValue!));
            Assert.Equal(new[] { first.Id.ToString(), "0" }, path);
        }

        /// <summary>
        /// f18 while the room's fight is to be won, f19 when the room is clear, both at the
        /// fountain: what the 57 izg say, where f19 used to be always 1 and f18 never there.
        /// </summary>
        [Fact]
        public void The_room_says_whether_its_fight_is_won()
        {
            var dream = New();

            var entrance = Fields(DreamProtocol.BuildDreamState(dream));
            Assert.Null(Var(entrance, 18));
            Assert.Equal(1, Var(entrance, 19));

            var room = FirstFight(dream);
            Dreams.Enter(dream, room.Id, out _);
            var fighting = Fields(DreamProtocol.BuildDreamState(dream));
            Assert.Equal(1, Var(fighting, 18));
            Assert.Null(Var(fighting, 19));

            room.Hecha = true;
            var won = Fields(DreamProtocol.BuildDreamState(dream));
            Assert.Null(Var(won, 18));
            Assert.Equal(1, Var(won, 19));

            var fountain = dream.Salas.First(r => r.EsFuente);
            Dreams.Enter(dream, fountain.Id, out _);
            var atTheFountain = Fields(DreamProtocol.BuildDreamState(dream));
            Assert.Equal(1, Var(atTheFountain, 18));
            Assert.Equal(1, Var(atTheFountain, 19));
        }

        /// <summary>
        /// One f16 per band, as in the long capture past its first fountain: the second with an
        /// f2 of 1, the fountain in both, its edges only in the band it opens. And the band in f12.
        /// </summary>
        [Fact]
        public void Each_band_has_its_graph()
        {
            var dream = New();
            var fountain = dream.Salas.First(r => r.EsFuente);
            Dreams.AnadirFranja(dream);
            Dreams.Enter(dream, fountain.Id, out _);

            var izg = Fields(DreamProtocol.BuildDreamState(dream));
            Assert.Equal(1, Var(izg, 12));

            var graphs = izg.Where(f => f.FieldNumber == 16).Select(f => Fields(f.BytesValue!)).ToList();
            Assert.Equal(2, graphs.Count);
            Assert.Null(Var(graphs[0], 2));
            Assert.Equal(1, Var(graphs[1], 2));

            static IEnumerable<string> Ids(List<ProtoField> graph, int field)
                => graph.Where(f => f.FieldNumber == field)
                        .Select(f => System.Text.Encoding.UTF8.GetString(Fields(f.BytesValue!).First(g => g.FieldNumber == 1).BytesValue!));

            string id = fountain.Id.ToString();
            Assert.Contains(id, Ids(graphs[0], 1));
            Assert.Contains(id, Ids(graphs[1], 1));
            Assert.DoesNotContain(id, Ids(graphs[0], 4));
            Assert.Contains(id, Ids(graphs[1], 4));
            Assert.DoesNotContain("0", Ids(graphs[1], 1));
        }

        /// <summary>The portrait carries the dreamer's breed; it was an 11, a Sacrier, for everyone.</summary>
        [Fact]
        public void The_portrait_is_of_the_dreamer_s_breed()
        {
            var izg = Fields(DreamProtocol.BuildDreamState(New(breed: 9)));
            Assert.Equal(9, Var(Fields(izg.First(f => f.FieldNumber == 1).BytesValue!), 4));
        }

        /// <summary>
        /// The shapes of a room, against rooms "1", "2", "0" and "9" of the Paradoja III capture,
        /// byte for byte: a bonus room, a dream-points room, the entrance and the fountain.
        /// </summary>
        [Fact]
        public void A_room_is_the_bytes_of_the_capture()
        {
            var bonus = new Dreams.Sala { Id = 1, Fila = 1, Score = 16, DreamPoints = 5, Reward = RewardOf(14798) };
            Assert.Equal("08101805221c080010001a090a052014589c16100120002800400048ce7350775800280130013800",
                         Convert.ToHexString(DreamProtocol.BuildRoom(bonus)).ToLowerInvariant());

            var points = new Dreams.Sala { Id = 2, Fila = 1, Score = 15, DreamPoints = 5, Reward = RewardOf(14931) };
            Assert.Equal("080f1805221308011000200028053801400048d37450765800280130013800",
                         Convert.ToHexString(DreamProtocol.BuildRoom(points)).ToLowerInvariant());

            Assert.Equal("3800", Convert.ToHexString(DreamProtocol.BuildRoom(new Dreams.Sala { Id = 0, Fila = 0 })).ToLowerInvariant());
            Assert.Equal("280330043800", Convert.ToHexString(DreamProtocol.BuildRoom(
                new Dreams.Sala { Id = 9, Fila = 4, EsFuente = true })).ToLowerInvariant());
        }
    }
}
