using Jondo.Unity.Server.Handlers;
using Jondo.Unity.World.Fights;
using Xunit;

namespace Jondo.Unity.Tests.Combat
{
    /// <summary>
    /// The Riveteuse's turn-end glyph (3196) makes whoever ends his turn on it cast a one-turn
    /// state on himself: 124 the first time, then 125 if he ends the next turn on it with 124 still
    /// on, and 125 is what lets him lift the Hyperscampe's invulnerability (3197). Those rows are
    /// his, put after his turn is over: they live through his next turn. Counted as put inside it,
    /// 124 fell at his next turn start and 125 could never be reached.
    /// </summary>
    [Collection("MapManager")]
    public class TurnEndGlyphRowTests
    {
        private const int FirstStep = 124;

        [Fact]
        public void A_state_the_turn_end_glyph_gives_him_lives_through_his_next_turn()
        {
            var player = new Fighter
            {
                Id = 10, TeamId = 0, CellId = 300, MaxHP = 2000, CurrentHP = 2000, Level = 200,
                MaxAP = 7, CurrentAP = 7, MaxMP = 3, CurrentMP = 3,
            };
            var boss = new Fighter
            {
                Id = -1, TeamId = 1, CellId = 400, IsMonster = true, MaxHP = 2000, CurrentHP = 2000, Level = 200,
                MaxAP = 7, CurrentAP = 7, MaxMP = 3, CurrentMP = 3,
            };
            var fight = new FightInstance(1, 1);
            fight.AddPlayer(player);
            fight.AddOpponent(boss);
            fight.StartFight();
            while (fight.CurrentFighter != player) fight.NextTurn();

            // His turn ends on the glyph: it is closed, and then the glyph makes him cast 124.
            fight.CloseTheTurn();
            player.Buffs.PonerEstado(FirstStep);
            player.Buffs.Poner(new Buff
            {
                EffectId = 950, Estado = FirstStep, Quien = player.Id, HechizoOrigen = 3198, NivelOrigen = 1,
                CaducaEnRonda = fight.RoundNumber + 1,
            }, fight.SiguienteEmbrujo);
            fight.NextTurn();
            while (fight.CurrentFighter != player) fight.NextTurn();

            // His next turn starts with the state on...
            FightHandler.AguantaSuTurno(fight, player, player);
            Assert.Empty(player.Buffs.Barrer(fight.RoundNumber, row => FightHandler.LeTocaCaer(fight, player, row)));
            Assert.True(player.Buffs.TieneEstado(FirstStep));

            // ...and it falls once that turn is over.
            fight.NextTurn();
            FightHandler.AguantaSuTurno(fight, fight.CurrentFighter, player);
            Assert.Single(player.Buffs.Barrer(fight.RoundNumber, row => FightHandler.LeTocaCaer(fight, fight.CurrentFighter, row)));
            Assert.False(player.Buffs.TieneEstado(FirstStep));
        }
    }
}
