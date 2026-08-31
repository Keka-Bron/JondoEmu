using System.Linq;
using Jondo.Unity.World.Fights;
using Xunit;

namespace Jondo.Unity.Tests.Combat
{
    /// <summary>
    /// The number that says which portrait the carousel should highlight.
    /// </summary>
    /// <remarks>
    /// It rides in f7 of the turn-start frame and it is an index into the turn order, so it is only
    /// right while that list is. When a summon ran out of rounds the server set its life to zero
    /// and left it in the list, and from then on everybody behind it was announced one slot too
    /// high — the client lit up the wrong face for the rest of the fight.
    ///
    /// Measured in "Combate/combate contra 4 poutchs nivel 25 usando varios hechizos e invos.pcapng",
    /// where the real server recomputes it in BOTH directions:
    ///
    /// <code>
    ///   round 1   player(0)  -1(1) -2(2) -3(3) -4(4)
    ///   round 2   player(0)  -7(1)  then -1(2) -2(3) -3(4) -4(5)   a summon arrives
    ///   round 6   player(0)  -9(1)  ...                            the earlier two are gone,
    ///                                                              and the list closed up
    /// </code>
    ///
    /// Only for summons. What the real server does with a dead MONSTER's slot is in no capture
    /// here, so that is left alone rather than guessed at.
    /// </remarks>
    public class CarouselIndexTests
    {
        private static FightInstance FightWith(int monsters)
        {
            var fight = new FightInstance(1, 1);
            fight.AddPlayer(new Fighter { Id = 100, MaxHP = 500, CurrentHP = 500, Initiative = 900 });

            for (int i = 1; i <= monsters; i++)
            {
                fight.AddMonster(new Fighter
                {
                    Id = -i,
                    IsMonster = true,
                    MaxHP = 100,
                    CurrentHP = 100,
                    Initiative = 100 - i,
                });
            }

            fight.StartFight();
            return fight;
        }

        private static Fighter Summon(FightInstance fight, long id, Fighter owner)
        {
            var summon = new Fighter
            {
                Id = id,
                MaxHP = 50,
                CurrentHP = 50,
                JuegaTurno = true,
                MuereEnRonda = -1,
            };

            fight.Invocar(summon, owner);
            return summon;
        }

        [Fact]
        public void A_summon_takes_the_slot_right_behind_its_owner()
        {
            // Round 2 of the capture: the summon lands at 1 and the four monsters move from 1..4
            // to 2..5. It is not appended at the end.
            var fight = FightWith(4);
            var player = fight.Azul[0];

            Summon(fight, -7, player);

            Assert.Equal(0, fight.TurnOrder.IndexOf(player));
            Assert.Equal(1, fight.TurnOrder.FindIndex(f => f.Id == -7));
            Assert.Equal(2, fight.TurnOrder.FindIndex(f => f.Id == -1));
        }

        [Fact]
        public void And_when_it_goes_the_list_closes_up_again()
        {
            // The bug. Without the rebuild the summon keeps its slot and every monster behind it
            // is announced one too high for the rest of the fight.
            var fight = FightWith(4);
            var player = fight.Azul[0];
            var summon = Summon(fight, -7, player);

            summon.CurrentHP = 0;
            fight.RebuildTurnOrderKeepingCurrent();

            Assert.DoesNotContain(fight.TurnOrder, f => f.Id == -7);
            Assert.Equal(1, fight.TurnOrder.FindIndex(f => f.Id == -1));
            Assert.Equal(4, fight.TurnOrder.FindIndex(f => f.Id == -4));
        }

        [Fact]
        public void Whoever_is_playing_keeps_playing_across_the_rebuild()
        {
            // The half that is easy to break while fixing the other one: closing the list up moves
            // everybody's index, and if CurrentTurnIndex is not repointed the turn jumps to the
            // next fighter in the middle of somebody's action.
            var fight = FightWith(4);
            var player = fight.Azul[0];
            var summon = Summon(fight, -7, player);

            // Hand the turn to a monster standing behind the summon.
            while (fight.CurrentFighter != null && fight.CurrentFighter.Id != -2) fight.NextTurn();
            Assert.Equal(-2, fight.CurrentFighter!.Id);

            summon.CurrentHP = 0;
            fight.RebuildTurnOrderKeepingCurrent();

            Assert.Equal(-2, fight.CurrentFighter!.Id);
            Assert.Equal(fight.CurrentTurnIndex, fight.TurnOrder.FindIndex(f => f.Id == -2));
        }

        [Fact]
        public void A_dead_monster_is_not_compacted_away_by_this()
        {
            // Deliberate. The fix is for summons because that is what the capture shows; doing it
            // for every dead fighter was the mistake in the change this replaces. Rebuilding is
            // only called from the summon path, so a dead monster keeps its slot until something
            // else rebuilds the list.
            var fight = FightWith(4);
            int before = fight.TurnOrder.Count;

            fight.Rojo[0].CurrentHP = 0;

            Assert.Equal(before, fight.TurnOrder.Count);
            Assert.Contains(fight.TurnOrder, f => f.Id == -1);
        }

        [Fact]
        public void The_index_never_points_past_the_end_of_the_list()
        {
            // The crash this could turn into. If the fighter whose turn it was is the one that
            // left, the old index can be beyond the shortened list, and CurrentFighter indexes it
            // without checking.
            var fight = FightWith(1);
            var player = fight.Azul[0];
            var summon = Summon(fight, -7, player);

            while (fight.CurrentFighter != null && fight.CurrentFighter.Id != -7) fight.NextTurn();

            summon.CurrentHP = 0;
            fight.RebuildTurnOrderKeepingCurrent();

            Assert.True(fight.CurrentTurnIndex < fight.TurnOrder.Count);
            Assert.NotNull(fight.CurrentFighter);
        }
    }
}
