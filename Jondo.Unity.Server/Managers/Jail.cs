using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;
using Jondo.Unity.Protocol;
using Jondo.Unity.Server.Handlers;
using Jondo.Unity.Server.Network;
using Jondo.Unity.World.Maps;
using Microsoft.Data.Sqlite;

namespace Jondo.Unity.Server.Managers
{
    /// <summary>
    /// The jail an administrator sends a player to: ten minutes in a cell of the game's own
    /// prison, with no way out and a muzzle on.
    /// </summary>
    /// <remarks>
    /// The prison is Ankama's: subarea 751, "Prisión de los GM", the jail the real game keeps for
    /// its game masters. Its map 105121026 is a corridor of 75 cells with four cells of 13 to 17
    /// walkable squares in its corners, each behind its own door and none joined to the corridor
    /// on foot -- measured on map_walkable_cells.json, diagonals counted -- and no edge leading
    /// to another map. The prisoner goes in one of the four; the administrator who sent him, to
    /// the corridor beside its bars, free.
    ///
    /// While the sentence lasts the prisoner
    ///
    ///   cannot leave the map by any road    every teleport asks <see cref="KeepsInAsync"/>
    ///   speaks only on the general channel  and in private messages (GameNodeProxy, ktm)
    ///   cannot use any command              (CommandHandler.TryHandleAsync)
    ///   comes back to his cell from a fight even one lost, instead of going to his save point
    ///
    /// and when it ends -- the ten minutes, or an administrator letting him out -- he goes back
    /// where he was taken from. The time runs whether he is connected or not, and the sentence is
    /// kept in the Jail table, so neither leaving the game nor a server restart lets him out early.
    /// </remarks>
    public static class Jail
    {
        /// <summary>The GM prison's map: see the remarks.</summary>
        public const long MapId = 105121026;

        /// <summary>How long a sentence is.</summary>
        public static readonly TimeSpan Sentence = TimeSpan.FromMinutes(10);

        /// <summary>Who is in, until when, and where he goes back to.</summary>
        public sealed record Prisoner(long CharacterId, string Name, DateTime UntilUtc,
                                      long ReturnMapId, int ReturnCellId, long JailedBy);

        private static readonly ConcurrentDictionary<long, Prisoner> _prisoners = new();

        /// <summary>
        /// Set while the jail itself moves somebody -- in, or out -- so that its own teleports are
        /// not refused by the very rule they put in place.
        /// </summary>
        private static readonly AsyncLocal<bool> _escorting = new();

        /// <summary>The sentences' clock. Held here so the timer outlives the method that starts it.</summary>
        private static Timer? _clock;

        /// <summary>How long to wait for a session's turn before giving up on it this time.</summary>
        private static readonly TimeSpan TurnWait = TimeSpan.FromSeconds(5);

        // ─── The sentences ──────────────────────────────────────────────────────────

        /// <summary>Reads the sentences still running and starts the clock that ends them.</summary>
        public static void Initialize()
        {
            try
            {
                using var connection = new SqliteConnection(DatabaseManager.WorldConnectionString);
                connection.Open();
                var create = connection.CreateCommand();
                create.CommandText = @"
                    CREATE TABLE IF NOT EXISTS Jail (
                        CharacterId INTEGER PRIMARY KEY,
                        Name TEXT NOT NULL,
                        UntilMs INTEGER NOT NULL,
                        ReturnMapId INTEGER NOT NULL,
                        ReturnCellId INTEGER NOT NULL,
                        JailedBy INTEGER NOT NULL
                    );";
                create.ExecuteNonQuery();

                _prisoners.Clear();
                var read = connection.CreateCommand();
                read.CommandText = "SELECT CharacterId, Name, UntilMs, ReturnMapId, ReturnCellId, JailedBy FROM Jail;";
                using var reader = read.ExecuteReader();
                while (reader.Read())
                {
                    var prisoner = new Prisoner(reader.GetInt64(0), reader.GetString(1),
                        DateTimeOffset.FromUnixTimeMilliseconds(reader.GetInt64(2)).UtcDateTime,
                        reader.GetInt64(3), reader.GetInt32(4), reader.GetInt64(5));
                    _prisoners[prisoner.CharacterId] = prisoner;
                }
                Console.WriteLine($"[Cárcel] {_prisoners.Count} preso(s) con condena en curso.");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[Cárcel] No se ha podido leer la tabla Jail: {ex.Message}");
            }

            _clock ??= new Timer(_ =>
            {
                try { TickAsync().GetAwaiter().GetResult(); }
                catch (Exception ex) { Console.WriteLine($"[Cárcel] El reloj ha fallado: {ex.Message}"); }
            }, null, TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(5));
        }

        /// <summary>Whether this character is serving a sentence right now.</summary>
        public static bool IsJailed(long characterId)
            => characterId > 0 && _prisoners.TryGetValue(characterId, out var prisoner)
               && prisoner.UntilUtc > DateTime.UtcNow;

        /// <summary>Everybody serving a sentence, the soonest out first.</summary>
        public static IReadOnlyList<Prisoner> Prisoners
            => _prisoners.Values.OrderBy(p => p.UntilUtc).ToList();

        /// <summary>How long this character has left inside; zero when he is not.</summary>
        public static TimeSpan Left(long characterId)
            => _prisoners.TryGetValue(characterId, out var prisoner) && prisoner.UntilUtc > DateTime.UtcNow
                ? prisoner.UntilUtc - DateTime.UtcNow
                : TimeSpan.Zero;

        /// <summary>
        /// Whether the current session is kept where it is: jailed, and not being moved by the jail
        /// itself. Says so to the player when it is, with the time left.
        /// </summary>
        /// <remarks>
        /// Every road off the map asks this first -- the teleport they all end in, and each of the
        /// requests that would spend something before it (a dungeon key, a zaap's kamas) -- so a
        /// prisoner loses nothing by trying.
        /// </remarks>
        public static async Task<bool> KeepsInAsync(NetworkStream? stream)
        {
            if (_escorting.Value) return false;
            long characterId = SessionContext.State.CharacterId;
            if (!IsJailed(characterId)) return false;

            if (stream != null)
                await TellAsync(stream, CommandTexts.Get("jail.held", Minutes(Left(characterId))));
            return true;
        }

        /// <summary>Whether a line on this chat channel is allowed: a prisoner has the general one only.</summary>
        /// <remarks>Private messages travel by their own message (ktb) and are always allowed.</remarks>
        public static bool MaySpeakOn(long characterId, int channel)
            => channel == GeneralChannel || !IsJailed(characterId);

        /// <summary>The chat's general channel, the map's: channel 0 of the ktm.</summary>
        public const int GeneralChannel = 0;

        // ─── The prison's layout ────────────────────────────────────────────────────

        /// <summary>The prison's floor: its corridor, and its cells, each one walled off from it.</summary>
        public sealed record Layout(IReadOnlyList<int> Corridor, IReadOnlyList<IReadOnlyList<int>> Cells);

        /// <summary>
        /// The prison's floor read from its walkable cells: the pieces that cannot be walked to
        /// from one another, the biggest of them the corridor and the rest the cells.
        /// </summary>
        public static Layout? LayoutOf(IEnumerable<int> walkable)
        {
            var pieces = Pieces(walkable);
            if (pieces.Count < 2) return null;
            var ordered = pieces.OrderByDescending(p => p.Count).ToList();
            return new Layout(ordered[0], ordered.Skip(1).ToList());
        }

        /// <summary>
        /// The cells a character can walk between, grouped: two touch when they are neighbours,
        /// diagonals included, which is how a character walks outside a fight.
        /// </summary>
        private static List<List<int>> Pieces(IEnumerable<int> walkable)
        {
            var left = new HashSet<int>(walkable);
            var pieces = new List<List<int>>();
            while (left.Count > 0)
            {
                int first = left.Min();
                left.Remove(first);
                var piece = new List<int> { first };
                var pending = new Stack<int>();
                pending.Push(first);
                while (pending.Count > 0)
                {
                    var (x, y) = MapGeometry.CellToPoint(pending.Pop());
                    for (int dx = -1; dx <= 1; dx++)
                    for (int dy = -1; dy <= 1; dy++)
                    {
                        if (dx == 0 && dy == 0) continue;
                        int next = MapGeometry.PointToCell(x + dx, y + dy);
                        if (next < 0 || !left.Remove(next)) continue;
                        piece.Add(next);
                        pending.Push(next);
                    }
                }
                piece.Sort();
                pieces.Add(piece);
            }
            return pieces;
        }

        /// <summary>
        /// Where a new prisoner and his warden stand: the cell with fewest people in it, on its
        /// middle square that is free; and the corridor's free square nearest to that cell.
        /// </summary>
        public static (int Inside, int Outside) PlacesFor(Layout layout, ICollection<int> occupied)
        {
            var cell = layout.Cells
                .OrderBy(c => c.Count(occupied.Contains))
                .ThenByDescending(c => c.Count)
                .First();

            double cx = cell.Average(c => MapGeometry.CellToPoint(c).X);
            double cy = cell.Average(c => MapGeometry.CellToPoint(c).Y);
            int inside = cell
                .OrderBy(c => occupied.Contains(c))
                .ThenBy(c => Math.Abs(MapGeometry.CellToPoint(c).X - cx) + Math.Abs(MapGeometry.CellToPoint(c).Y - cy))
                .First();

            int outside = layout.Corridor
                .OrderBy(c => occupied.Contains(c))
                .ThenBy(c => cell.Min(i => MapGeometry.Distance(i, c)))
                .First();
            return (inside, outside);
        }

        // ─── In and out ─────────────────────────────────────────────────────────────

        /// <summary>Why a prisoner could not be taken in or let out.</summary>
        public enum Refusal { None, NotConnected, InFight, Busy, AlreadyIn, NotIn, Self, NoPrison }

        /// <summary>
        /// Takes this player to a cell of the prison for <see cref="Sentence"/>, and the
        /// administrator who sends him to the corridor beside it.
        /// </summary>
        public static async Task<(Refusal Refusal, Prisoner? Prisoner)> LockUpAsync(GameSession prisoner, GameSession? warden)
        {
            if (prisoner?.Stream == null || !prisoner.IsInWorld || !prisoner.HasCharacter) return (Refusal.NotConnected, null);
            if (warden != null && warden.CharacterId == prisoner.CharacterId) return (Refusal.Self, null);
            if (IsJailed(prisoner.CharacterId)) return (Refusal.AlreadyIn, null);
            if (prisoner.State.IsInFight || (warden?.State.IsInFight ?? false)) return (Refusal.InFight, null);

            var layout = MapManager.WalkableCells.TryGetValue(MapId, out var floor) ? LayoutOf(floor) : null;
            if (layout == null) return (Refusal.NoPrison, null);

            var occupied = new HashSet<int>(SessionRegistry.OnMap(MapId).Select(s => s.State.CellId));
            var (inside, outside) = PlacesFor(layout, occupied);

            Prisoner? sentence = null;
            var refused = await InTurnAsync(prisoner, async () =>
            {
                var state = prisoner.State;
                if (state.IsInFight) return Refusal.InFight;
                sentence = new Prisoner(state.CharacterId, state.CharacterName ?? "",
                                        DateTime.UtcNow + Sentence, state.MapId, state.CellId,
                                        warden?.CharacterId ?? 0);
                _prisoners[sentence.CharacterId] = sentence;
                Save(sentence);
                await EscortAsync(prisoner.Stream!, MapId, inside);
                await TellAsync(prisoner.Stream!, CommandTexts.Get("jail.in", Minutes(Sentence)));
                return Refusal.None;
            });
            if (refused != Refusal.None) return (refused, null);

            if (warden?.Stream != null)
            {
                await InTurnAsync(warden, async () =>
                {
                    await EscortAsync(warden.Stream!, MapId, outside);
                    return Refusal.None;
                });
            }

            Console.WriteLine($"[Cárcel] {sentence!.Name} entra en la celda {inside} hasta las " +
                              $"{sentence.UntilUtc.ToLocalTime():HH:mm:ss}" +
                              (warden != null ? $"; {warden.State.CharacterName} se queda en el pasillo, casilla {outside}." : "."));
            return (Refusal.None, sentence);
        }

        /// <summary>
        /// Lets a prisoner out before his time, or because it is up: back where he was taken from,
        /// now if he is connected and on his next login's map if not.
        /// </summary>
        public static async Task<Refusal> ReleaseAsync(long characterId, string why)
        {
            if (!_prisoners.TryGetValue(characterId, out var sentence)) return Refusal.NotIn;

            var session = SessionRegistry.FindByCharacter(characterId);
            if (session?.Stream != null && session.IsInWorld)
            {
                var refused = await InTurnAsync(session, async () =>
                {
                    if (session.State.IsInFight) return Refusal.InFight;
                    Forget(characterId);
                    await EscortAsync(session.Stream!, sentence.ReturnMapId, sentence.ReturnCellId);
                    await TellAsync(session.Stream!, CommandTexts.Get("jail.out"));
                    return Refusal.None;
                });
                if (refused != Refusal.None) return refused;
            }
            else
            {
                Forget(characterId);
                MoveOffline(characterId, sentence.ReturnMapId, sentence.ReturnCellId);
            }

            Console.WriteLine($"[Cárcel] {sentence.Name} sale de la cárcel ({why}).");
            return Refusal.None;
        }

        /// <summary>
        /// An administrator going to see the prison: to the corridor, on the square nearest the
        /// cells that nobody stands on.
        /// </summary>
        public static async Task<Refusal> VisitAsync(GameSession visitor)
        {
            if (visitor?.Stream == null || !visitor.IsInWorld) return Refusal.NotConnected;
            if (visitor.State.IsInFight) return Refusal.InFight;
            if (IsJailed(visitor.CharacterId)) return Refusal.AlreadyIn;
            var layout = MapManager.WalkableCells.TryGetValue(MapId, out var floor) ? LayoutOf(floor) : null;
            if (layout == null) return Refusal.NoPrison;

            var occupied = new HashSet<int>(SessionRegistry.OnMap(MapId).Select(s => s.State.CellId));
            var (_, outside) = PlacesFor(layout, occupied);
            return await InTurnAsync(visitor, async () =>
            {
                await EscortAsync(visitor.Stream!, MapId, outside);
                return Refusal.None;
            });
        }

        /// <summary>Every few seconds: whoever's time is up goes out.</summary>
        private static async Task TickAsync()
        {
            foreach (var prisoner in _prisoners.Values.ToList())
            {
                if (prisoner.UntilUtc > DateTime.UtcNow) continue;
                // A prisoner in a fight, or whose session is busy, goes out on a later tick.
                await ReleaseAsync(prisoner.CharacterId, "condena cumplida");
            }
        }

        /// <summary>The jail's own move, which its rule does not stop.</summary>
        private static async Task EscortAsync(NetworkStream stream, long mapId, int cell)
        {
            bool was = _escorting.Value;
            _escorting.Value = true;
            try { await TeleportHandler.ToMapAsync(stream, mapId, cell); }
            finally { _escorting.Value = was; }
        }

        /// <summary>
        /// Runs this on the session's own turn, so it does not cross a message the client is
        /// being answered; and in its context, so that the teleport moves this character.
        /// </summary>
        private static async Task<Refusal> InTurnAsync(GameSession session, Func<Task<Refusal>> act)
        {
            if (!await session.UnoCadaVez.WaitAsync(TurnWait)) return Refusal.Busy;
            try
            {
                using (SessionContext.Push(session)) return await act();
            }
            finally
            {
                session.UnoCadaVez.Release();
            }
        }

        private static async Task TellAsync(NetworkStream stream, string text)
            => await Jondo.Protocol.NetworkMessage.WriteFrameAsync(stream,
                ConnectionProtocol.Push(Op.Lqn, ConnectionProtocol.BuildNotice(text)));

        /// <summary>Minutes, rounded up: "1" for the last thirty seconds rather than "0".</summary>
        private static int Minutes(TimeSpan time) => Math.Max(1, (int)Math.Ceiling(time.TotalMinutes));

        // ─── The table ──────────────────────────────────────────────────────────────

        private static void Save(Prisoner prisoner)
        {
            try
            {
                using var connection = new SqliteConnection(DatabaseManager.WorldConnectionString);
                connection.Open();
                var command = connection.CreateCommand();
                command.CommandText =
                    "INSERT OR REPLACE INTO Jail (CharacterId, Name, UntilMs, ReturnMapId, ReturnCellId, JailedBy) " +
                    "VALUES ($id, $name, $until, $map, $cell, $by);";
                command.Parameters.AddWithValue("$id", prisoner.CharacterId);
                command.Parameters.AddWithValue("$name", prisoner.Name);
                command.Parameters.AddWithValue("$until", new DateTimeOffset(prisoner.UntilUtc).ToUnixTimeMilliseconds());
                command.Parameters.AddWithValue("$map", prisoner.ReturnMapId);
                command.Parameters.AddWithValue("$cell", prisoner.ReturnCellId);
                command.Parameters.AddWithValue("$by", prisoner.JailedBy);
                command.ExecuteNonQuery();
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[Cárcel] No se ha podido guardar la condena de {prisoner.Name}: {ex.Message}");
            }
        }

        private static void Forget(long characterId)
        {
            _prisoners.TryRemove(characterId, out _);
            try
            {
                using var connection = new SqliteConnection(DatabaseManager.WorldConnectionString);
                connection.Open();
                var command = connection.CreateCommand();
                command.CommandText = "DELETE FROM Jail WHERE CharacterId = $id;";
                command.Parameters.AddWithValue("$id", characterId);
                command.ExecuteNonQuery();
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[Cárcel] No se ha podido borrar la condena de {characterId}: {ex.Message}");
            }
        }

        /// <summary>Where a disconnected character will appear at his next login.</summary>
        private static void MoveOffline(long characterId, long mapId, int cellId)
        {
            try
            {
                using var connection = new SqliteConnection(DatabaseManager.WorldConnectionString);
                connection.Open();
                var command = connection.CreateCommand();
                command.CommandText = "UPDATE Characters SET MapId = $map, CellId = $cell WHERE Id = $id;";
                command.Parameters.AddWithValue("$map", mapId);
                command.Parameters.AddWithValue("$cell", cellId);
                command.Parameters.AddWithValue("$id", characterId);
                command.ExecuteNonQuery();
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[Cárcel] No se ha podido devolver a {characterId} a su sitio: {ex.Message}");
            }
        }

        /// <summary>For tests: a sentence put by hand, with nobody moved.</summary>
        internal static void PutForTests(Prisoner prisoner) => _prisoners[prisoner.CharacterId] = prisoner;

        /// <summary>For tests: the sentence gone, with nobody moved.</summary>
        internal static void ForgetForTests(long characterId) => _prisoners.TryRemove(characterId, out _);
    }
}
