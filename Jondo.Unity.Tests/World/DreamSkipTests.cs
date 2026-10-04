using System.Linq;
using Jondo.Unity.Server.Managers;
using Xunit;

namespace Jondo.Unity.Tests.World
{
    /// <summary>
    /// ".sueno": a dream carried forward down its own graph, the rooms on the way won as if fought.
    /// </summary>
    [Collection("MapManager")]
    public class DreamSkipTests
    {
        private static Dreams.Sueno New(int difficulty = 9)
        {
            Interactives.Initialize();
            Dreams.OlvidarTodo();
            var dream = Dreams.Crear(1, "Prueba", 200, difficulty, 100, 200);
            Dreams.Enter(dream, 0, out _);
            return dream;
        }

        /// <summary>
        /// From the entrance to the end: all five bands open, one room of each row 0 to 25 on the
        /// path, each an exit of the one before, every fight among them won and paid -- 21 of them,
        /// rows 1-3, 5-9, 11-15, 17-22 and 23-24, less the dream favours the path goes through.
        /// The end itself is left for the handler to enter.
        /// </summary>
        [Fact]
        public void To_the_end_from_the_entrance()
        {
            var dream = New();
            int pointsBefore = dream.DreamPoints;

            var (outcome, end, skipped) = Dreams.SkipTo(dream, null);

            Assert.Equal(Dreams.SkipOutcome.Done, outcome);
            Assert.True(end!.EsFinal);
            Assert.Equal(Dreams.Bands, dream.Franja);
            Assert.False(end.Cobrada);
            Assert.NotEqual(end.Id, dream.Actual);

            var path = dream.Visited.Select(id => dream.Buscar(id)!).OrderBy(r => r.Fila).ToList();
            Assert.Equal(21 - path.Count(r => r.EsFavor), skipped);
            Assert.Equal(Enumerable.Range(0, 26), path.Select(r => r.Fila));
            for (int i = 1; i < path.Count; i++) Assert.Contains(path[i].Id, path[i - 1].Salidas);
            Assert.Contains(end.Id, path[^1].Salidas);

            Assert.All(path.Where(r => r.Miembros.Count > 0), r => Assert.True(r.Hecha && r.Cobrada));
            Assert.True(dream.DreamPoints > pointsBefore);
        }

        /// <summary>
        /// To a row: the nearest room of it, and only the bands it takes. Eight fights, rows 1-3
        /// and 5-9, less the dream favour of band II when the way goes through it.
        /// </summary>
        [Fact]
        public void To_a_row()
        {
            var dream = New();

            var (outcome, room, skipped) = Dreams.SkipTo(dream, 10);

            Assert.Equal(Dreams.SkipOutcome.Done, outcome);
            Assert.Equal(10, room!.Fila);
            Assert.True(room.EsFuente);
            Assert.Equal(2, dream.Franja);
            Assert.Equal(8 - dream.Visited.Count(id => dream.Buscar(id)!.EsFavor), skipped);
            Assert.Equal(9, dream.SalaActual!.Fila);
        }

        /// <summary>The fight one stands in, not won yet, counts as won when one skips out of it.</summary>
        [Fact]
        public void The_room_stood_in_counts_as_won()
        {
            var dream = New();
            var first = dream.Buscar(dream.Buscar(0)!.Salidas[0])!;
            Dreams.Enter(dream, first.Id, out _);

            var (outcome, room, skipped) = Dreams.SkipTo(dream, 2);

            Assert.Equal(Dreams.SkipOutcome.Done, outcome);
            Assert.Contains(room!.Id, first.Salidas);
            Assert.Equal(1, skipped);
            Assert.True(first.Hecha);
        }

        /// <summary>A dream only goes forward: not to the row one is on, not back, not past the end.</summary>
        [Fact]
        public void Only_forward_and_only_as_deep_as_the_dream()
        {
            var dream = New();
            var first = dream.Buscar(dream.Buscar(0)!.Salidas[0])!;
            Dreams.Enter(dream, first.Id, out _);

            Assert.Equal(Dreams.SkipOutcome.Behind, Dreams.SkipTo(dream, 1).Outcome);
            Assert.Equal(Dreams.SkipOutcome.Behind, Dreams.SkipTo(dream, 0).Outcome);
            Assert.False(first.Hecha);

            Assert.Equal(Dreams.SkipOutcome.Past, Dreams.SkipTo(dream, 40).Outcome);
            Assert.Equal(Dreams.Bands, dream.Franja);
            Assert.Equal(first.Id, dream.Actual);

            var end = dream.Salas.Single(r => r.EsFinal);
            dream.Actual = end.Id;
            Assert.Equal(Dreams.SkipOutcome.Behind, Dreams.SkipTo(dream, null).Outcome);
        }
    }
}
