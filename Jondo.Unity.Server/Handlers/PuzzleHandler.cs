using System;
using System.Globalization;
using System.Net.Sockets;
using System.Threading.Tasks;
using Jondo.Unity.Server.Managers;
using Jondo.Unity.Server.Network;
using Jondo.Unity.Protocol;

namespace Jondo.Unity.Server.Handlers
{
    /// <summary>
    /// Plays a map puzzle's answers (<see cref="PuzzleCase"/>): the cases of an element that says
    /// something (InteractiveMessages) and the cells of <see cref="FloorRiddles"/>.
    /// </summary>
    public static class PuzzleHandler
    {
        /// <summary>
        /// The first of <paramref name="cases"/> that holds for the session's character. <paramref name="key"/>
        /// names the element or cell, for the cases that count their uses (<see cref="PuzzleCase.Uses"/>).
        /// </summary>
        public static PuzzleCase? Pick(PuzzleCase[] cases, string key = "")
        {
            long character = SessionContext.State.CharacterId;
            var now = DateTimeOffset.Now;
            return PuzzleCase.First(cases, ItemCriteria.Holds, mark => PuzzleMarks.Has(character, mark, now),
                                    () => key.Length > 0 && PuzzleUses.Locked(character, key, now));
        }

        /// <summary>
        /// The case played: its messages, the items it takes («Has perdido», lqn) and gives, each one
        /// only while none is carried («Has conseguido») unless the case stacks, its marks, its use
        /// counted against <paramref name="key"/>, and the move last.
        /// </summary>
        public static async Task PlayAsync(NetworkStream stream, PuzzleCase said, string where, string key = "")
        {
            long character = SessionContext.State.CharacterId;
            var now = DateTimeOffset.Now;
            foreach (int message in said.Messages)
                await WriteAsync(stream, ConnectionProtocol.Push(Op.Lqn,
                    ConnectionProtocol.BuildInfoMessage(InfoMessages.Info, message, Array.Empty<string>())));
            foreach (var (item, count) in said.Takes)
            {
                int taken = said.TakeNow(Equipment.HowMany(item), count);
                if (taken <= 0 || !await Equipment.TakeAsync(stream, item, taken)) continue;
                await WriteAsync(stream, ConnectionProtocol.Push(Op.Lqn,
                    ConnectionProtocol.BuildSystemMessage(InfoMessages.ItemLost,
                        taken.ToString(CultureInfo.InvariantCulture), item.ToString(CultureInfo.InvariantCulture))));
            }
            foreach (var (item, count) in said.Gives)
            {
                int given = said.GiveNow(Equipment.HowMany(item), count);
                if (given <= 0 || !await Equipment.GiveAsync(stream, item, given)) continue;
                await WriteAsync(stream, ConnectionProtocol.Push(Op.Lqn,
                    ConnectionProtocol.BuildSystemMessage(InfoMessages.ItemGained,
                        given.ToString(CultureInfo.InvariantCulture), item.ToString(CultureInfo.InvariantCulture))));
            }
            foreach (string mark in said.Unmark) PuzzleMarks.Clear(character, mark);
            foreach (string mark in said.Mark) PuzzleMarks.Set(character, mark, said.For, now);
            if (said.Uses > 0 && key.Length > 0 && PuzzleUses.Use(character, key, said.Uses, said.LockFor, now))
                Console.WriteLine($"[Puzzle] {where}: locked for {said.LockFor} s after {said.Uses} use(s).");
            Console.WriteLine($"[Puzzle] {where}: messages [{string.Join(",", said.Messages)}], marks +[{string.Join(",", said.Mark)}] " +
                              $"-[{string.Join(",", said.Unmark)}]{(said.ToMap != 0 ? $", to {said.ToMap} cell {said.ToCell}" : "")}.");
            if (said.ToMap != 0) await TeleportHandler.ToMapAsync(stream, said.ToMap, said.ToCell);
        }

        /// <summary>
        /// A cell stopped on: a spot's case, or a riddle tile -- the next island on the right digit,
        /// the riddle's wrong case on any other. True when the cell was one of them.
        /// </summary>
        public static async Task<bool> SteppedAsync(NetworkStream stream, long mapId, int cellId)
        {
            var spot = FloorRiddles.SpotOf(mapId, cellId);
            if (spot != null)
            {
                string key = $"{mapId}@{cellId}";
                var said = Pick(spot.Cases, key);
                if (said == null) return false;
                await PlayAsync(stream, said, $"cell {cellId} of {mapId}", key);
                return true;
            }

            var tile = FloorRiddles.TileOf(mapId, cellId);
            if (tile == null) return false;
            var (riddle, step, digit) = tile.Value;
            var readable = Readables.Of(riddle.ReadableMap, riddle.ReadableElement);
            int document = readable?.DocumentFor(SessionContext.State.CharacterId) ?? 0;
            string answer = riddle.Answers.TryGetValue(document, out var a) ? a : "";
            if (FloorRiddles.Right(answer, step, digit))
            {
                Console.WriteLine($"[Puzzle] Tile {cellId} of {mapId}: {digit}, step {step + 1} of {answer.Length}.");
                await TeleportHandler.ToMapAsync(stream, mapId, riddle.Steps[step].ToCell);
            }
            else
            {
                await PlayAsync(stream, riddle.Wrong, $"tile {cellId} of {mapId} ({digit}, wrong at step {step + 1})");
            }
            return true;
        }

        private static Task WriteAsync(NetworkStream stream, byte[] frame)
            => Jondo.Protocol.NetworkMessage.WriteFrameAsync(stream, frame);
    }
}
