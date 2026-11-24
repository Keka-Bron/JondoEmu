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

        /// <summary>
        /// The Gorgouille's glyph takes «up to 4 AP» from whoever starts his turn on it, for that
        /// turn. The row is hers and is put in the player's turn, ahead of hers in the round: it
        /// falls once her turn is over, and the player starts his next turn with his points.
        /// Counted from the next round, it was still on at that start and the glyph took 4 more.
        /// </summary>
        [Fact]
        public void A_glyph_row_put_before_its_casters_turn_falls_once_that_turn_is_over()
        {
            var fight = new FightInstance(1, 1);
            var player = Person(10, 0, 438);
            var gorgouille = Person(-3, 1, 384);
            fight.AddPlayer(player); fight.AddOpponent(gorgouille);

            // The player starts his turn on her glyph: -4 AP, for one turn, put by her.
            var minus = new Buff
            {
                EffectId = 1079, Cuanto = -4, Quien = gorgouille.Id, HechizoOrigen = 2936, NivelOrigen = 1,
                CaducaEnRonda = fight.RoundNumber + 1, EmpiezaEnRonda = fight.RoundNumber,
            };
            player.Buffs.Poner(minus, fight.SiguienteEmbrujo);
            player.UltimoEmbrujoDeSuTurno = fight.UltimoEmbrujo;   // his turn is over

            // Her turn starts: the row stays through it...
            fight.LastBuffBeforeThisTurn = fight.UltimoEmbrujo;
            FightHandler.AguantaSuTurno(fight, gorgouille, player);
            Assert.Empty(player.Buffs.Barrer(fight.RoundNumber, row => FightHandler.LeTocaCaer(fight, gorgouille, row)));

            // ...and falls at the next start, the player's.
            FightHandler.AguantaSuTurno(fight, player, player);
            Assert.Single(player.Buffs.Barrer(fight.RoundNumber, row => FightHandler.LeTocaCaer(fight, player, row)));
        }

        /// <summary>
        /// A reaction put on himself in the round, before his first turn, still lives through
        /// that turn: the state the Kraken gives the Kralamoure when the player, ahead of him,
        /// hits him, lets him summon the tentacle on his turn.
        /// </summary>
        [Fact]
        public void A_reaction_before_his_first_turn_lives_through_it()
        {
            var fight = new FightInstance(1, 1);
            var player = Person(10, 0, 438);
            var kralamoure = Person(-1, 1, 344);
            fight.AddPlayer(player); fight.AddOpponent(kralamoure);

            kralamoure.Buffs.PonerEstado(WaterState);
            var row = State(kralamoure, fight.RoundNumber + 1);
            row.EmpiezaEnRonda = fight.RoundNumber;
            kralamoure.Buffs.Poner(row, fight.SiguienteEmbrujo);

            fight.LastBuffBeforeThisTurn = fight.UltimoEmbrujo;   // his turn starts
            FightHandler.AguantaSuTurno(fight, kralamoure, kralamoure);
            Assert.Empty(kralamoure.Buffs.Barrer(fight.RoundNumber, r => FightHandler.LeTocaCaer(fight, kralamoure, r)));
            Assert.True(kralamoure.Buffs.TieneEstado(WaterState));

            FightHandler.AguantaSuTurno(fight, player, kralamoure);
            Assert.Single(kralamoure.Buffs.Barrer(fight.RoundNumber, r => FightHandler.LeTocaCaer(fight, player, r)));
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

        /// <summary>
        /// A row a summon puts on itself in its summoner's turn counts from its own next turn, not
        /// from the one right after the summoner's: Agony's Radiation IV reaches her Malakite
        /// Crystals at her turn start and they switch their invulnerability off for one turn
        /// (952). Counted from their turn right after hers, it was back before the players played.
        /// </summary>
        [Fact]
        public void A_row_a_summon_puts_in_its_summoners_turn_outlives_its_own_turn_after()
        {
            var fight = new FightInstance(1, 1);
            var player = Person(10, 0, 438);
            var agony = Person(-1, 1, 259);
            var crystal = Person(-2, 1, 260);
            crystal.Invocador = agony.Id;
            crystal.JuegaTurno = true;
            fight.AddPlayer(player); fight.AddOpponent(agony); fight.AddOpponent(crystal);

            // Her turn: the crystal switches its invulnerability off for one turn.
            agony.PrimerEmbrujoDeSuTurno = fight.UltimoEmbrujo;
            var off = new Buff
            {
                EffectId = 952, Estado = 56, Quien = crystal.Id, HechizoOrigen = 12126, NivelOrigen = 1,
                CaducaEnRonda = fight.RoundNumber + 1, EmpiezaEnRonda = fight.RoundNumber,
            };
            crystal.Buffs.Poner(off, fight.SiguienteEmbrujo);
            agony.UltimoEmbrujoDeSuTurno = fight.UltimoEmbrujo;

            // The crystal's own turn, right after hers, and then the player's: the row stays.
            fight.LastBuffBeforeThisTurn = fight.UltimoEmbrujo;
            FightHandler.AguantaSuTurno(fight, crystal, crystal);
            Assert.Empty(crystal.Buffs.Barrer(fight.RoundNumber, row => FightHandler.LeTocaCaer(fight, crystal, row)));
            crystal.UltimoEmbrujoDeSuTurno = fight.UltimoEmbrujo;

            FightHandler.AguantaSuTurno(fight, player, crystal);
            Assert.Empty(crystal.Buffs.Barrer(fight.RoundNumber, row => FightHandler.LeTocaCaer(fight, player, row)));
            Assert.Single(crystal.Buffs.Puestos.Where(b => b.EffectId == 952));
        }

        /// <summary>
        /// A row put by a summon that never plays counts in its owner's turns: a Dathura Bud
        /// hatching at a player's turn start makes Dathura invulnerable for one turn, and the
        /// row lives through her turn and falls once it is over, before the player plays again.
        /// </summary>
        [Fact]
        public void A_row_of_a_summon_that_never_plays_lives_through_its_owners_turn()
        {
            var fight = new FightInstance(1, 1);
            var player = Person(10, 0, 438);
            var dathura = Person(-1, 1, 259);
            var bud = Person(-2, 1, 260);
            bud.Invocador = dathura.Id;
            bud.JuegaTurno = false;
            fight.AddPlayer(player); fight.AddOpponent(dathura); fight.AddOpponent(bud);

            // The player's turn starts next to the bud: it hatches and Dathura is invulnerable.
            dathura.Buffs.PonerEstado(56);
            var invulnerable = new Buff
            {
                EffectId = 950, Estado = 56, Quien = bud.Id, HechizoOrigen = 12006, NivelOrigen = 2,
                CaducaEnRonda = fight.RoundNumber + 1, EmpiezaEnRonda = fight.RoundNumber,
            };
            dathura.Buffs.Poner(invulnerable, fight.SiguienteEmbrujo);
            player.UltimoEmbrujoDeSuTurno = fight.UltimoEmbrujo;   // his turn is over

            // Her turn starts: the row stays through it...
            fight.LastBuffBeforeThisTurn = fight.UltimoEmbrujo;
            FightHandler.AguantaSuTurno(fight, dathura, dathura);
            Assert.Empty(dathura.Buffs.Barrer(fight.RoundNumber, row => FightHandler.LeTocaCaer(fight, dathura, row)));
            Assert.True(dathura.Buffs.TieneEstado(56));

            // ...and falls at the next start, the player's.
            FightHandler.AguantaSuTurno(fight, player, dathura);
            Assert.Single(dathura.Buffs.Barrer(fight.RoundNumber, row => FightHandler.LeTocaCaer(fight, player, row)));
            Assert.False(dathura.Buffs.TieneEstado(56));
        }
    }
}
