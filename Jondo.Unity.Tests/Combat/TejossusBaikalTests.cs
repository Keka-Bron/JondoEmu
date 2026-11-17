using System.Linq;
using System.Threading.Tasks;
using Jondo.Unity.Server.Handlers;
using Jondo.Unity.Server.Managers;
using Jondo.Unity.World.Fights;
using Jondo.Unity.World.Maps;
using Xunit;

namespace Jondo.Unity.Tests.Combat
{
    /// <summary>
    /// Tejossus (2986) casts Baikal (2479) on himself: one row, "+10% final damage (1171) under DM
    /// on C, duration 1" -- every melee blow he takes until his next turn gives him +10%. Armed to
    /// the round after the cast and read by rounds, the hook was gone as that round began, before
    /// the players who play ahead of him could hit him. A row of duration N lives, as any row of
    /// that duration does, until its caster's turn in the Nth round after the cast.
    /// </summary>
    public class TejossusBaikalTests
    {
        private const int Tejossus = 2986, Baikal = 2479, FinalDamage = 1171;

        private static bool HasData => SpellEffects.De(Baikal, 1).Any(row => row.EffectId == FinalDamage
                                                                          && row.Duration == 1);

        /// <summary>A character, and Tejossus next to him, who plays after him in every round.</summary>
        private static (FightInstance Fight, Fighter Boss, Fighter Character) Board()
        {
            var fight = new FightInstance(1, 1);
            var character = new Fighter
            {
                Id = 10, CellId = 300, MaxHP = 5000, CurrentHP = 5000, Level = 200, Initiative = 1000,
                MaxAP = 10, CurrentAP = 10, MaxMP = 5, CurrentMP = 5,
            };
            fight.AddPlayer(character);
            var boss = new Fighter
            {
                Id = -1, CellId = MapGeometry.GetNeighbors(300).First(), MaxHP = 20000, CurrentHP = 20000,
                Level = 200, IsMonster = true, MonsterId = Tejossus, MaxAP = 10, CurrentAP = 10, MaxMP = 4, CurrentMP = 4,
            };
            fight.AddMonster(boss);
            fight.StartFight();
            return (fight, boss, character);
        }

        /// <summary>Plays the turns up to <paramref name="fighter"/>'s next one, sweeping as each turn starts.</summary>
        private static void UpToTheTurnOf(FightInstance fight, Fighter fighter)
        {
            do
            {
                var next = fight.NextTurn();
                FightHandler.TurnStartSweep(fight, next);
            }
            while (fight.CurrentFighter != fighter);
        }

        private static Task MeleeBlowAsync(FightInstance fight, Fighter character, Fighter boss)
            => FightHandler.HurtAsync(null, fight, character, 1, 1, boss, boss.CellId,
                   tirada: new[] { new SpellEffect { EffectId = 97, EffectUid = 900101, DiceNum = 50, DiceSide = 50,
                                                     Element = 1, TargetMask = "A" } });

        private static int Boosts(Fighter boss)
            => boss.Buffs.Puestos.Count(row => row.HechizoOrigen == Baikal && row.EffectId == FinalDamage);

        [Fact]
        public async Task His_hook_answers_the_blows_of_the_players_who_play_before_his_next_turn()
        {
            if (!HasData) return;
            var (fight, boss, character) = Board();
            Assert.Same(character, fight.CurrentFighter);

            // His turn of round 1, the last of it: Baikal.
            UpToTheTurnOf(fight, boss);
            int castIn = fight.RoundNumber;
            await FightHandler.AplicarEfectosAsync(null, fight, boss, Baikal, 1, boss, EffectEngine.AlLanzar, boss.CellId);

            // Round 2, the character's turn, ahead of his: a melee blow sets the hook off.
            UpToTheTurnOf(fight, character);
            Assert.Equal(castIn + 1, fight.RoundNumber);
            await MeleeBlowAsync(fight, character, boss);

            Assert.Equal(1, Boosts(boss));
        }

        [Fact]
        public async Task His_hook_is_over_once_his_next_turn_has_come()
        {
            if (!HasData) return;
            var (fight, boss, character) = Board();
            UpToTheTurnOf(fight, boss);
            await FightHandler.AplicarEfectosAsync(null, fight, boss, Baikal, 1, boss, EffectEngine.AlLanzar, boss.CellId);

            // His own next turn starts: the hook falls with it.
            UpToTheTurnOf(fight, boss);
            Assert.DoesNotContain(boss.Buffs.ActiveSpells, hook => hook.Hechizo == Baikal);

            // And the character's next blow gives him nothing more.
            UpToTheTurnOf(fight, character);
            int before = Boosts(boss);
            await MeleeBlowAsync(fight, character, boss);
            Assert.Equal(before, Boosts(boss));
        }
    }
}
