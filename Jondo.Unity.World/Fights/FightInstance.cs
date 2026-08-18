using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace Jondo.Unity.World.Fights
{
    public enum FightState
    {
        Placement,
        Ongoing,
        Ended
    }

    public class FightInstance
    {
        public long FightId { get; set; }
        public long MapId { get; set; }
        public bool HasLoadedMap { get; set; } = false;
        public FightState State { get; private set; } = FightState.Placement;

        public List<Fighter> Team0 { get; } = new List<Fighter>(); // Players
        public List<Fighter> Team1 { get; } = new List<Fighter>(); // Monsters

        public List<int> BluePlacementCells { get; } = new List<int>(); // Players placement
        public List<int> RedPlacementCells { get; } = new List<int>();  // Monsters placement

        public long ChallengerLeaderId => Team0.FirstOrDefault()?.Id ?? 0;
        public long DefenderLeaderId { get; set; } = -20000;

        public List<Fighter> TurnOrder { get; private set; } = new List<Fighter>();
        public int CurrentTurnIndex { get; private set; } = 0;
        public Fighter CurrentFighter => TurnOrder.Count > 0 ? TurnOrder[CurrentTurnIndex] : null;

        public int WinnerTeamId { get; private set; } = -1;

        public long RoleplayMapId { get; set; }
        public long ArenaMapId { get; set; }

        /// <summary>
        /// Cuándo empezó a pelearse de verdad, para saber lo que ha durado: la pantalla de fin de
        /// combate lo enseña arriba a la derecha y sin esto salía 00:00.
        /// </summary>
        public DateTime StartedAt { get; set; } = DateTime.UtcNow;

        /// <summary>
        /// El número que le toca al siguiente embrujo. Es del COMBATE, no de cada luchador: en la
        /// captura los del jugador y los del monstruo van en la misma serie, y es el número con el
        /// que luego se quita cada uno.
        /// </summary>
        private int _ultimoEmbrujo;
        public int SiguienteEmbrujo() => ++_ultimoEmbrujo;

        public CancellationTokenSource PlacementTimerCts { get; set; }
        public CancellationTokenSource TurnTimerCts { get; set; }

        public FightInstance(long fightId, long mapId, long arenaMapId = 0)
        {
            FightId = fightId;
            RoleplayMapId = mapId;
            ArenaMapId = arenaMapId != 0 ? arenaMapId : mapId;
            MapId = ArenaMapId;
        }

        public void CancelPlacementTimer()
        {
            try
            {
                PlacementTimerCts?.Cancel();
                PlacementTimerCts?.Dispose();
            }
            catch { }
            finally
            {
                PlacementTimerCts = null;
            }
        }

        public void CancelTurnTimer()
        {
            try
            {
                TurnTimerCts?.Cancel();
                TurnTimerCts?.Dispose();
            }
            catch { }
            finally
            {
                TurnTimerCts = null;
            }
        }

        public void GeneratePlacementCells(List<int> walkableCells)
        {
            BluePlacementCells.Clear();
            RedPlacementCells.Clear();

            if (walkableCells == null || walkableCells.Count == 0)
            {
                BluePlacementCells.AddRange(new[] { 286, 298, 326, 271, 285, 299, 312, 313 });
                RedPlacementCells.AddRange(new[] { 411, 424, 439, 397, 410, 426, 438, 453 });
                return;
            }

            var defaultBlue = new[] { 286, 298, 326, 271, 285, 299, 312, 313 };
            var defaultRed = new[] { 411, 424, 439, 397, 410, 426, 438, 453 };

            if (defaultBlue.All(c => walkableCells.Contains(c)) && defaultRed.All(c => walkableCells.Contains(c)))
            {
                BluePlacementCells.AddRange(defaultBlue);
                RedPlacementCells.AddRange(defaultRed);
                return;
            }

            var sorted = walkableCells.OrderBy(c => c).ToList();
            var team0Candidates = sorted.Take(sorted.Count / 2).ToList();
            var team1Candidates = sorted.Skip(sorted.Count / 2).ToList();

            BluePlacementCells.AddRange(team0Candidates.Take(8));
            RedPlacementCells.AddRange(team1Candidates.Take(8));
        }

        public void AddPlayer(Fighter player)
        {
            player.TeamId = 0;
            if (BluePlacementCells.Count > 0)
                player.CellId = BluePlacementCells[Team0.Count % BluePlacementCells.Count];
            Team0.Add(player);
            UpdateTurnOrder();
        }

        public void AddMonster(Fighter monster)
        {
            monster.TeamId = 1;
            if (RedPlacementCells.Count > 0)
                monster.CellId = RedPlacementCells[Team1.Count % RedPlacementCells.Count];
            Team1.Add(monster);
            UpdateTurnOrder();
        }

        /// <summary>
        /// El siguiente identificador libre para un invocado.
        ///
        /// Los combatientes que no son jugadores llevan número negativo y se reparten de uno en
        /// uno: en las capturas del Ocra los pious son -1 y -2 y la primera baliza sale con el -3,
        /// la segunda con el -4, y así. Se mira lo que ya hay para no pisar a nadie.
        /// </summary>
        public long SiguienteIdDeInvocado()
        {
            long menor = 0;
            foreach (var f in Team0) if (f.Id < menor) menor = f.Id;
            foreach (var f in Team1) if (f.Id < menor) menor = f.Id;
            return menor - 1;
        }

        /// <summary>
        /// Mete un invocado en el combate, en el bando del que lo invoca, y rehace el orden de
        /// turnos para que le toque jugar.
        /// </summary>
        public void Invocar(Fighter invocado, Fighter dueno)
        {
            invocado.Invocador = dueno.Id;
            invocado.TeamId = dueno.TeamId;
            (dueno.TeamId == 0 ? Team0 : Team1).Add(invocado);

            // El que está jugando ahora mismo sigue jugando: se rehace la lista pero se conserva
            // a quién le toca, que si no el turno se le va al de al lado en mitad de una acción.
            var jugando = CurrentFighter;
            TurnOrder = BuildAlternatingTurnOrder();
            if (jugando != null && TurnOrder.Contains(jugando))
            {
                CurrentTurnIndex = TurnOrder.IndexOf(jugando);
            }
        }

        /// <summary>
        /// Los invocados a los que se les ha acabado el tiempo en esta ronda. El efecto 141 les
        /// cuelga la cuenta atrás al nacer y aquí se cobra.
        /// </summary>
        public List<Fighter> InvocadosQueSeDeshacen(int ronda)
        {
            var fuera = new List<Fighter>();
            foreach (var f in Team0) if (SeDeshace(f, ronda)) fuera.Add(f);
            foreach (var f in Team1) if (SeDeshace(f, ronda)) fuera.Add(f);
            return fuera;
        }

        private static bool SeDeshace(Fighter f, int ronda)
            => f.EsInvocado && f.IsAlive && f.MuereEnRonda >= 0 && ronda >= f.MuereEnRonda;

        public void UpdateTurnOrder()
        {
            TurnOrder = BuildAlternatingTurnOrder();
        }

        /// <summary>
        /// Un bando en orden de juego: los de siempre por iniciativa, y detrás de cada uno los que
        /// haya invocado, en el orden en que los sacó.
        ///
        /// Los invocados que no juegan turno se quedan fuera de la lista, pero siguen estando en
        /// el combate: se les puede pegar y cuentan para el tablero.
        /// </summary>
        private static List<List<Fighter>> Agrupar(List<Fighter> bando)
        {
            var salida = new List<List<Fighter>>();
            foreach (var quien in bando.Where(f => f.IsAlive && !f.EsInvocado)
                                       .OrderByDescending(f => f.Initiative))
            {
                var grupo = new List<Fighter> { quien };
                foreach (var suyo in bando)
                {
                    if (suyo.IsAlive && suyo.EsInvocado && suyo.JuegaTurno && suyo.Invocador == quien.Id)
                    {
                        grupo.Add(suyo);
                    }
                }
                salida.Add(grupo);
            }
            return salida;
        }

        public bool SetFighterReady(long fighterId)
        {
            var f = Team0.FirstOrDefault(p => p.Id == fighterId);
            if (f != null) f.IsReady = true;

            if (Team0.All(p => p.IsReady))
            {
                CancelPlacementTimer();
                StartFight();
                return true;
            }
            return false;
        }

        public void ChangePlacementCell(long fighterId, int newCellId)
        {
            if (State != FightState.Placement) return;
            var f = Team0.FirstOrDefault(p => p.Id == fighterId);
            if (f != null && BluePlacementCells.Contains(newCellId))
            {
                f.CellId = newCellId;
            }
        }

        public void StartFight()
        {
            CancelPlacementTimer();
            State = FightState.Ongoing;
            StartedAt = DateTime.UtcNow;
            if (TurnOrder.Count == 0)
            {
                TurnOrder = BuildAlternatingTurnOrder();
            }
            CurrentTurnIndex = 0;

            if (CurrentFighter != null)
            {
                CurrentFighter.StartTurn();
            }
        }

        public List<Fighter> BuildAlternatingTurnOrder()
        {
            // Los invocados NO se ordenan por iniciativa: van pegados a quien los puso, y sólo los
            // que tengan algo que hacer al empezar su turno. Es lo que se ve en las capturas, con
            // la Baliza de Supervivencia jugando siempre justo detrás de su Ocra.
            // Se intercalan GRUPOS, no combatientes sueltos: cada grupo es uno de los de siempre
            // con sus invocados detrás. Intercalando de uno en uno, la baliza se separaba de su
            // Ocra y jugaba después del monstruo, cuando en la captura va inmediatamente detrás.
            var team0Sorted = Agrupar(Team0);
            var team1Sorted = Agrupar(Team1);

            var result = new List<Fighter>();
            int maxCount = Math.Max(team0Sorted.Count, team1Sorted.Count);

            int team0BestInit = team0Sorted.FirstOrDefault()?[0].Initiative ?? 0;
            int team1BestInit = team1Sorted.FirstOrDefault()?[0].Initiative ?? 0;
            bool team0First = team0BestInit >= team1BestInit;

            for (int i = 0; i < maxCount; i++)
            {
                if (team0First)
                {
                    if (i < team0Sorted.Count) result.AddRange(team0Sorted[i]);
                    if (i < team1Sorted.Count) result.AddRange(team1Sorted[i]);
                }
                else
                {
                    if (i < team1Sorted.Count) result.AddRange(team1Sorted[i]);
                    if (i < team0Sorted.Count) result.AddRange(team0Sorted[i]);
                }
            }
            return result;
        }

        /// <summary>
        /// La ronda en la que va ESTE combate.
        ///
        /// Vivía como un entero estático del manejador, uno para todo el servidor, así que dos
        /// jugadores peleando a la vez compartían el contador: al pasar de ronda uno, el otro veía
        /// caducar sus embrujos. Cada combate lleva el suyo.
        /// </summary>
        public int RoundNumber { get; private set; } = 1;

        /// <summary>
        /// El número de acción, que es lo que el cliente acusa al cerrar cada secuencia. También
        /// era único para todo el servidor, y el cliente de un jugador acusaba números que había
        /// gastado el combate de otro.
        /// </summary>
        private int _ultimaAccion;
        public int SiguienteAccion() => System.Threading.Interlocked.Increment(ref _ultimaAccion);

        /// <summary>De dónde salió cada jugador al entrar en combate, para devolverlo ahí.</summary>
        public Dictionary<long, (long Mapa, int Casilla)> DeDondeVenian { get; }
            = new Dictionary<long, (long, int)>();
        public bool StartsNewRound { get; private set; } = false;

        public Fighter NextTurn()
        {
            CancelTurnTimer();
            StartsNewRound = false;
            CheckFightEnd();
            if (State == FightState.Ended) return null;

            int attempts = 0;
            do
            {
                CurrentTurnIndex++;
                if (CurrentTurnIndex >= TurnOrder.Count)
                {
                    CurrentTurnIndex = 0;
                    RoundNumber++;
                    StartsNewRound = true;
                }
                attempts++;
            } while (!CurrentFighter.IsAlive && attempts < TurnOrder.Count);

            if (!CurrentFighter.IsAlive)
            {
                CheckFightEnd();
                return null;
            }

            CurrentFighter.StartTurn();
            return CurrentFighter;
        }

        public bool RebuildTurnOrderOnFighterDeath()
        {
            var oldOrder = TurnOrder.ToList();
            var currentFighter = CurrentFighter;
            TurnOrder = BuildAlternatingTurnOrder();
            if (currentFighter != null && TurnOrder.Contains(currentFighter))
            {
                CurrentTurnIndex = TurnOrder.IndexOf(currentFighter);
            }
            else if (TurnOrder.Count > 0)
            {
                CurrentTurnIndex = CurrentTurnIndex % TurnOrder.Count;
            }

            return !oldOrder.SequenceEqual(TurnOrder);
        }

        public void CheckFightEnd()
        {
            // Los invocados NO cuentan para saber si un bando sigue en pie: matar la baliza del
            // rival no gana un combate. Cuando el que las puso se muere se le caen todas en el
            // acto, así que en la práctica esto es un cinturón además de los tirantes.
            bool team0Alive = Team0.Any(f => f.IsAlive && !f.EsInvocado);
            bool team1Alive = Team1.Any(f => f.IsAlive && !f.EsInvocado);

            if (!team1Alive)
            {
                CancelPlacementTimer();
                CancelTurnTimer();
                State = FightState.Ended;
                WinnerTeamId = 0; // Players won!
            }
            else if (!team0Alive)
            {
                CancelPlacementTimer();
                CancelTurnTimer();
                State = FightState.Ended;
                WinnerTeamId = 1; // Monsters won!
            }
        }
    }
}
