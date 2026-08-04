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

        public void UpdateTurnOrder()
        {
            TurnOrder = BuildAlternatingTurnOrder();
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
            var team0Sorted = Team0.Where(f => f.IsAlive).OrderByDescending(f => f.Initiative).ToList();
            var team1Sorted = Team1.Where(f => f.IsAlive).OrderByDescending(f => f.Initiative).ToList();

            var result = new List<Fighter>();
            int maxCount = Math.Max(team0Sorted.Count, team1Sorted.Count);

            int team0BestInit = team0Sorted.FirstOrDefault()?.Initiative ?? 0;
            int team1BestInit = team1Sorted.FirstOrDefault()?.Initiative ?? 0;
            bool team0First = team0BestInit >= team1BestInit;

            for (int i = 0; i < maxCount; i++)
            {
                if (team0First)
                {
                    if (i < team0Sorted.Count) result.Add(team0Sorted[i]);
                    if (i < team1Sorted.Count) result.Add(team1Sorted[i]);
                }
                else
                {
                    if (i < team1Sorted.Count) result.Add(team1Sorted[i]);
                    if (i < team0Sorted.Count) result.Add(team0Sorted[i]);
                }
            }
            return result;
        }

        public int RoundNumber { get; private set; } = 1;
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
            bool team0Alive = Team0.Any(f => f.IsAlive);
            bool team1Alive = Team1.Any(f => f.IsAlive);

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
