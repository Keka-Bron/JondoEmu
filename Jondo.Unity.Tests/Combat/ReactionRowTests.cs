using System.Linq;
using Jondo.Unity.Server.Handlers;
using Jondo.Unity.World.Fights;
using Xunit;

namespace Jondo.Unity.Tests.Combat
{
    /// <summary>
    /// A row its caster puts outside his own turn lives through his next one, and falls once it is
    /// over. The Kralamoure's Kraken gives him the state of the element he is hit with, for one
    /// turn, and on his next turn that state lets him summon the matching tentacle; it was swept
    /// at that turn's start.
    /// </summary>
    public class ReactionRowTests
    {
        private const int WaterState = 36;

        private static Fighter Person(long id, int team, int cell) => new()
        {
            Id = id, TeamId = team, CellId = cell, MaxHP = 2000, CurrentHP = 2000, Level = 200,
            MaxAP = 7, CurrentAP = 7, MaxMP = 3, CurrentMP = 3,
        };

        private static Buff State(Fighter caster, int round) => new()
        {
            EffectId = 950, Estado = WaterState, Quien = caster.Id, HechizoOrigen = 1103, NivelOrigen = 1,
            CaducaEnRonda = round,
        };

        [Fact]
        public void A_reaction_after_his_turn_lives_through_his_next_one()
        {
            var fight = new FightInstance(1, 1);
            var kralamoure = Person(-1, 1, 344);
            var player = Person(10, 0, 438);
            fight.AddPlayer(player); fight.AddOpponent(kralamoure);

            // His turn is over; then the player hits him in water and Kraken answers.
            kralamoure.UltimoEmbrujoDeSuTurno = fight.UltimoEmbrujo;
            kralamoure.Buffs.PonerEstado(WaterState);
            kralamoure.Buffs.Poner(State(kralamoure, fight.RoundNumber), fight.SiguienteEmbrujo);

            // His next turn starts: the state is still his...
            FightHandler.AguantaSuTurno(fight, kralamoure, kralamoure);
            Assert.Empty(kralamoure.Buffs.Barrer(fight.RoundNumber, row => FightHandler.LeTocaCaer(fight, kralamoure, row)));
            Assert.True(kralamoure.Buffs.TieneEstado(WaterState));

            // ...and falls once it is over, at the next turn start, not at his turn after.
            FightHandler.AguantaSuTurno(fight, player, kralamoure);
            Assert.Single(kralamoure.Buffs.Barrer(fight.RoundNumber, row => FightHandler.LeTocaCaer(fight, player, row)));
            Assert.False(kralamoure.Buffs.TieneEstado(WaterState));
        }

        [Fact]
        public void A_row_put_in_his_own_turn_still_falls_at_his_next()
        {
            var fight = new FightInstance(1, 1);
            var kralamoure = Person(-1, 1, 344);
            var player = Person(10, 0, 438);
            fight.AddPlayer(player); fight.AddOpponent(kralamoure);

            kralamoure.Buffs.Poner(State(kralamoure, fight.RoundNumber), fight.SiguienteEmbrujo);
            kralamoure.UltimoEmbrujoDeSuTurno = fight.UltimoEmbrujo;   // put, then his turn ended

            FightHandler.AguantaSuTurno(fight, kralamoure, kralamoure);
            Assert.Single(kralamoure.Buffs.Barrer(fight.RoundNumber, row => FightHandler.LeTocaCaer(fight, kralamoure, row)));
        }
    }
}
