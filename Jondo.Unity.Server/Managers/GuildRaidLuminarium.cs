using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Net.Sockets;
using System.Threading.Tasks;
using Jondo.Unity.Protocol;
using Jondo.Unity.Server.Network;
using Jondo.Unity.World.Content;

namespace Jondo.Unity.Server.Managers
{
    /// <summary>
    /// The Luminarium, the Gigalodón's floor -3 enigma: a 4x4 board of lantern fish where clicking a
    /// fish switches it and its neighbours, and lighting them all blows the wall to floor -4 (the
    /// guides; the goal says "Hacer explotar el muro del luminarium para acceder a la planta -4").
    /// </summary>
    /// <remarks>
    /// The board is read off the client's map: the sixteen elements of graphic 10041 on one map of
    /// the raid's floors, in rows by their cell (289, 303, 318, 332) and columns by their id. Each raid
    /// has its own board, shuffled from all lit by random presses so that it can always be solved.
    ///
    /// NOT measured: which of the client's two raid "Utilizar" skills the fish carry, and which of
    /// the graphic's states is lit -- no capture shows the grotto. Lit is taken as state 1.
    /// </remarks>
    public static class GuildRaidLuminarium
    {
        /// <summary>The lantern fish's graphic, in the client's maps.</summary>
        public const int FishGfx = 10041;

        /// <summary>The board's side: four by four.</summary>
        public const int Side = 4;

        /// <summary>The fish's type: none, as for the raid's other unnamed elements.</summary>
        public const int FishType = -1;

        /// <summary>"Utilizar", one of the raid's own skills in the client's data (1187822).</summary>
        public const int FishSkill = 471;

        /// <summary>A fish's states. NOT measured: lit is taken as 1, out as 0 (the map's own).</summary>
        public const int Lit = 1;
        public const int Out = 0;

        /// <summary>How many random presses shuffle a board.</summary>
        private const int ShufflePresses = 7;

        /// <summary>Each running raid's board: which fish are lit, by the raid instance's id.</summary>
        private static readonly ConcurrentDictionary<long, bool[]> _boards = new();

        private static readonly Random _random = new();

        /// <summary>The raid's board: its map and its fish in reading order, or null when its floors have none.</summary>
        public static (long MapId, int Floor, List<Interactives.Element> Fish)? BoardOf(RaidKind kind)
        {
            for (int floor = 1; floor <= kind.Floors.Count; floor++)
            {
                foreach (long map in DatabaseManager.MapsOfSubArea(kind.Floors[floor - 1]))
                {
                    var fish = Interactives.ElementsOf(map).Where(e => e.Gfx == FishGfx).ToList();
                    if (fish.Count != Side * Side) continue;
                    return (map, floor, fish.OrderBy(e => e.Cell).ThenBy(e => e.Id).ToList());
                }
            }
            return null;
        }

        /// <summary>Every board of every raid, for the interactive registry.</summary>
        public static IEnumerable<(long MapId, List<Interactives.Element> Fish)> Boards()
        {
            foreach (var kind in Raids.All)
            {
                if (BoardOf(kind) is { } board) yield return (board.MapId, board.Fish);
            }
        }

        /// <summary>Switches a fish and its neighbours, up, down, left and right.</summary>
        public static void Press(bool[] lit, int index)
        {
            int row = index / Side, column = index % Side;
            foreach (var (r, c) in new[] { (row, column), (row - 1, column), (row + 1, column), (row, column - 1), (row, column + 1) })
            {
                if (r < 0 || r >= Side || c < 0 || c >= Side) continue;
                lit[r * Side + c] = !lit[r * Side + c];
            }
        }

        /// <summary>A board shuffled from all lit, so that it can always be solved, and never already solved.</summary>
        public static bool[] Shuffled(Random random)
        {
            var lit = Enumerable.Repeat(true, Side * Side).ToArray();
            do
            {
                for (int i = 0; i < ShufflePresses; i++) Press(lit, random.Next(Side * Side));
            } while (lit.All(l => l));
            return lit;
        }

        /// <summary>Gives a starting raid its board.</summary>
        public static void Shuffle(RaidInstance raid)
        {
            if (BoardOf(Raids.Of(raid.RaidId)) == null) return;
            lock (_random) _boards[raid.Id] = Shuffled(_random);
        }

        /// <summary>For the tests: a raid's board as given.</summary>
        internal static void Set(RaidInstance raid, bool[] lit) => _boards[raid.Id] = lit;

        /// <summary>A raid's board, or null.</summary>
        public static bool[] Of(RaidInstance raid) => raid != null && _boards.TryGetValue(raid.Id, out var lit) ? lit : null;

        /// <summary>A fish's state for whoever looks at it: lit or out on his raid's board, the map's own otherwise.</summary>
        public static int StateFor(long characterId, long mapId, int elementId)
            => StateFor(GuildRaidManager.RaidOf(characterId), mapId, elementId);

        /// <summary>A fish's state on a raid's board, the map's own when there is none.</summary>
        public static int StateFor(RaidInstance raid, long mapId, int elementId)
        {
            var board = raid == null ? null : BoardOf(Raids.Of(raid.RaidId));
            var lit = Of(raid);
            if (board == null || lit == null || board.Value.MapId != mapId) return Out;
            int index = board.Value.Fish.FindIndex(f => f.Id == elementId);
            return index >= 0 && lit[index] ? Lit : Out;
        }

        /// <summary>
        /// A raid member clicks a fish: it and its neighbours switch, for everyone of his raid on the
        /// map; all lit, the goal that opens the next floor is met. Returns whether it was a fish.
        /// </summary>
        public static Task<bool> PressAsync(NetworkStream stream, long characterId, long mapId, int elementId, int skillId)
            => PressAsync(stream, GuildRaidManager.RaidOf(characterId), mapId, elementId, skillId);

        /// <summary>A fish clicked on a raid's board (see the overload by character).</summary>
        public static async Task<bool> PressAsync(NetworkStream stream, RaidInstance raid, long mapId, int elementId, int skillId)
        {
            var kind = raid == null ? null : Raids.Of(raid.RaidId);
            var board = kind == null ? null : BoardOf(kind);
            var lit = Of(raid);
            if (board == null || lit == null || board.Value.MapId != mapId) return false;
            int index = board.Value.Fish.FindIndex(f => f.Id == elementId);
            if (index < 0) return false;

            if (stream != null)
            {
                await Jondo.Protocol.NetworkMessage.WriteFrameAsync(stream,
                    ConnectionProtocol.Push(Op.Iwi, ConnectionProtocol.BuildInteractiveUseEnded(elementId, skillId)));
            }

            var opener = GuildRaidManager.OpenerOf(raid.RaidId, board.Value.Floor + 1);
            if (opener != null && raid.GoalAt(opener.Id) >= 1) return true;      // already solved

            bool[] before;
            lock (lit)
            {
                before = (bool[])lit.Clone();
                Press(lit, index);
            }
            for (int i = 0; i < lit.Length; i++)
            {
                if (lit[i] == before[i]) continue;
                var fish = board.Value.Fish[i];
                await TellRaidOnMapAsync(raid, mapId, ConnectionProtocol.Push(Op.Iwf,
                    ConnectionProtocol.BuildElementState(fish.Cell, fish.Id, lit[i] ? Lit : Out)));
            }

            if (lit.All(l => l) && opener != null)
            {
                await GuildRaidManager.AdvanceGoalAsync(raid, opener.Id, Math.Max(1, opener.Value));
                await TellRaidOnMapAsync(raid, mapId, ConnectionProtocol.Push(Op.Lqn,
                    ConnectionProtocol.BuildNotice(Handlers.CommandTexts.Get("raid.goal.met", (object)opener.Name))));
                Console.WriteLine($"[Raid] {raid.Uuid}: the Luminarium is lit; floor {board.Value.Floor + 1} opens.");
            }
            return true;
        }

        /// <summary>A frame to the raid's members standing on a map.</summary>
        private static async Task TellRaidOnMapAsync(RaidInstance raid, long mapId, byte[] frame)
        {
            foreach (long member in raid.Members.ToList())
            {
                var session = SessionRegistry.FindByCharacter(member);
                if (session != null && session.State.MapId == mapId) await session.SendAsync(frame);
            }
        }

        /// <summary>A finished raid's board goes.</summary>
        public static void Forget(RaidInstance raid) => _boards.TryRemove(raid.Id, out _);
    }
}
