using System;
using System.Linq;
using Jondo.Unity.Protocol.Game;
using Jondo.Unity.Protocol.Game.Wire;
using Jondo.Unity.Server;
using Jondo.Unity.Server.Handlers;
using Jondo.Unity.Server.Managers;
using Jondo.Unity.Server.Network;
using Jondo.Unity.World.Fights;
using Xunit;

namespace Jondo.Unity.Tests.World
{
    /// <summary>
    /// A dream's code, its "souvenir": the same code makes the same dream, a dream started from
    /// one pays nothing, its training never spends a sand; and the result window that hands the
    /// code out at the end.
    /// </summary>
    [Collection("MapManager")]
    public class DreamSouvenirTests
    {
        /// <summary>No character has these ids: what the handler writes to the base is cleaned up after.</summary>
        private const long Dreamer = 900_000_000_021;

        private static Dreams.Sueno Whole(long who, string? seed, bool training = false, int difficulty = 5)
        {
            Interactives.Initialize();
            var dream = Dreams.Crear(who, "Prueba", 200, difficulty, 100, 200, 0, seed, training);
            for (int band = 2; band <= Dreams.Bands; band++) Dreams.AnadirFranja(dream);
            return dream;
        }

        /// <summary>Everything a dream draws: its graph, groups, rewards, maps, favours and Rey Gobs.</summary>
        private static string ShapeOf(Dreams.Sueno dream)
            => string.Join("|", dream.Salas.OrderBy(s => s.Id).Select(s =>
                $"{s.Id}:{s.Fila}:{string.Join(",", s.Salidas)}:{s.Grupo}:{s.Reward?.Tag}:{s.MapaDeLaSala}:" +
                $"{s.EsFavor}:{s.EsFuente}:{s.HasReyGob}:{string.Join(",", s.Miembros)}"));

        private static void AsTheDreamer(Action<GameSession> body)
        {
            var session = GameSession.SinSocket();
            session.State.CharacterId = Dreamer;
            try
            {
                body(session);
            }
            finally
            {
                DatabaseManager.DeleteDream(Dreamer);
            }
        }

        /// <summary>
        /// The same code, typed in either case, makes the same dream for anybody -- "les salles de
        /// songes et les bonus proposés seront alors exactement les mêmes" -- its fountain's offers
        /// included; another code, another dream.
        /// </summary>
        [Fact]
        public void A_code_makes_the_same_dream()
        {
            Dreams.OlvidarTodo();
            var one = Whole(900_000_000_031, "ABCD1234");
            var other = Whole(900_000_000_032, "abcd1234");
            Assert.Equal("ABCD1234", one.Seed);
            Assert.True(one.FromSouvenir);
            Assert.Equal(ShapeOf(one), ShapeOf(other));
            Assert.NotEqual(ShapeOf(one), ShapeOf(Whole(900_000_000_033, "ZZZZ9999")));

            var fountain = one.Salas.First(r => r.EsFuente);
            Dreams.Enter(one, fountain.Id, out _);
            Dreams.Enter(other, fountain.Id, out _);
            Assert.Equal(fountain.Offers!.Select(o => o.Tag), other.Buscar(fountain.Id)!.Offers!.Select(o => o.Tag));
            Dreams.OlvidarTodo();
        }

        /// <summary>A dream started without one draws its own code, of the client's alphabet, and pays as any.</summary>
        [Fact]
        public void A_new_dream_draws_its_own_code()
        {
            var dream = Dreams.Crear(900_000_000_034, "Prueba", 200, 3, 100, 200);
            Assert.True(Dreams.IsSeed(dream.Seed));
            Assert.Equal(dream.Seed, dream.Seed.ToUpperInvariant());
            Assert.False(dream.FromSouvenir);
            Assert.False(dream.Training);
            Dreams.Olvidar(900_000_000_034);
        }

        /// <summary>A code is eight of A-Z and 0-9, either case; anything else is none.</summary>
        [Theory]
        [InlineData("ABCD1234", true)]
        [InlineData("fonsocac", true)]
        [InlineData("ABC", false)]
        [InlineData("ABCD-123", false)]
        [InlineData("ABCDÉ123", false)]
        [InlineData(null, false)]
        public void What_a_code_is(string? text, bool isOne) => Assert.Equal(isOne, Dreams.IsSeed(text));

        /// <summary>
        /// Training is a code's: with one it starts with the sands of SOFTCORE and never spends
        /// them -- the room is tried again, the dream goes on --; without one there is none.
        /// </summary>
        [Fact]
        public void Training_never_spends_its_sands() => AsTheDreamer(session =>
        {
            Assert.False(Dreams.Crear(Dreamer, "Prueba", 200, 2, 100, 200, 0, null, training: true).Training);

            var dream = Whole(Dreamer, "TRAIN123", training: true, difficulty: 2);
            Assert.True(dream.Training);
            Assert.Equal(Dreams.TrainingSands, dream.Arena);
            var room = dream.Salas.First(r => r.Fila == 1);
            dream.Actual = room.Id;

            var (_, _, notice, ended, result) = DreamHandler.AfterTheFight(session, new FightInstance(7_000_011, room.MapaDeLaSala), won: false);
            Assert.False(ended);
            Assert.Null(result);
            Assert.NotNull(notice);
            Assert.Equal(Dreams.TrainingSands, dream.Arena);
        });

        /// <summary>A dream from a code pays nothing: "sans aucune récompense (que ce soit xp ou drop)".</summary>
        [Fact]
        public void A_souvenir_pays_nothing() => AsTheDreamer(_ =>
        {
            var dream = Whole(Dreamer, "NOLOOT12", difficulty: 2);
            var room = dream.Salas.First(r => r.Fila == 1);
            dream.Actual = room.Id;
            var fight = new FightInstance(7_000_012, room.MapaDeLaSala);
            fight.AddPlayer(new Fighter { Id = Dreamer, MaxHP = 10, CurrentHP = 10 });

            var pay = DreamHandler.PayOf(fight, won: true);
            Assert.NotNull(pay);
            Assert.True(pay!.Souvenir);
            Assert.Equal(0, pay.Fragments);
            Dreams.Olvidar(Dreamer);
        });

        /// <summary>
        /// The result of a dream won: its code, its difficulty, its bonuses and graphs, its owner,
        /// and the winning players; a loss identifies the last room and the dream stage.
        /// </summary>
        [Fact]
        public void The_result_window_carries_the_code()
        {
            var dream = Whole(900_000_000_035, "RESULT12", difficulty: 4);
            dream.Ganados.Add(new Dreams.Bono(111, 1));
            dream.FightMs = (long)int.MaxValue + 3000;
            dream.DreamPoints = 43;
            dream.DreamerLevels = 2;
            var players = new[] { new DreamProtocol.ResultPlayer(7, "Guest", 0, 2, 3),
                new DreamProtocol.ResultPlayer(dream.CharacterId, "Prueba", 1, 11, 1) };
            var codec = new GameCodec();
            InfiniteDreamEndEvent RoundTrip(bool won)
            {
                var bytes = codec.Encode(new(DreamProtocol.BuildResult(dream, won, players, 5, 40)));
                var envelope = codec.ReadEnvelope(bytes);
                Assert.Equal(GameMessageKind.Event, envelope.Kind);
                Assert.Equal(GameCodec.JondoVersion == "3.8.1.2" ? "izx" : "iya", envelope.Alias);
                return Assert.IsType<InfiniteDreamEndEvent>(codec.Decode(bytes).Content);
            }

            var won = RoundTrip(true);
            Assert.Equal("RESULT12", won.Seed);
            Assert.Equal(4, won.Intensity);
            Assert.Equal(5, won.KilledWaveNumber);
            Assert.Equal(40, won.KilledWaveGlobalTurn);
            Assert.Equal(dream.FightMs, won.CumulatedDuration);
            Assert.Equal(43, won.DreamPoints);
            Assert.Equal(2, won.BonusLevels);
            Assert.Equal(dream.Arena, won.Life);
            Assert.Equal(dream.Tormentas, won.Rerolls);
            Assert.Equal(Dreams.DreamerLevel(dream), won.ScaledLevel);
            Assert.Equal(Enum.GetValues<InfiniteDreamStage>(), won.Maps.Select(m => m.Stage));
            Assert.Equal(111, Assert.Single(won.Bonuses).Effect!.Action);
            Assert.Equal(new InfiniteDreamCharacter("Prueba", Gender.Female, dream.CharacterId, dream.Breed), won.Owner);
            var rows = Assert.IsType<InfiniteDreamEndEventCaseWin>(won.Outcome).Value.Players;
            Assert.Collection(rows,
                guest => Assert.Equal(new InfiniteDreamWinPlayer(new("Guest", Gender.Male, 7, 2), 3), guest),
                owner => Assert.Equal(new InfiniteDreamWinPlayer(new("Prueba", Gender.Female, dream.CharacterId, 11), 1), owner));

            var room = dream.Salas.First(r => r.Franja == Dreams.Bands && r.Miembros.Count > 0);
            dream.Actual = room.Id;
            var lost = Assert.IsType<InfiniteDreamEndEventCaseLoss>(RoundTrip(false).Outcome);
            Assert.Equal(new InfiniteDreamLossResult(room.Id.ToString(), InfiniteDreamStage.Stage5), lost.Value);
            Dreams.Olvidar(900_000_000_035);
        }

        /// <summary>The end of a dream hands its result out, won at its end or lost with no sand left.</summary>
        [Fact]
        public void The_end_hands_the_result_out() => AsTheDreamer(session =>
        {
            var dream = Whole(Dreamer, null, difficulty: 2);
            dream.Arena = 0;
            var room = dream.Salas.First(r => r.Fila == 1);
            dream.Actual = room.Id;
            var fight = new FightInstance(7_000_013, room.MapaDeLaSala);
            fight.AddPlayer(new Fighter { Id = Dreamer, Name = "Prueba", MaxHP = 10, CurrentHP = 10 });

            var (_, _, _, ended, result) = DreamHandler.AfterTheFight(session, fight, won: false);
            Assert.True(ended);
            Assert.NotNull(result);
            Assert.IsType<InfiniteDreamEndEventCaseLoss>(result!.Outcome);
            Assert.Equal(dream.Seed, result.Seed);
        });
    }
}
