using System.Linq;
using System.Threading.Tasks;
using Jondo.Unity.Server.Handlers;
using Jondo.Unity.Server.Managers;
using Jondo.Unity.World.Fights;
using Xunit;

namespace Jondo.Unity.Tests.Combat
{
    /// <summary>
    /// The Blops' spells as the enclave's rooms play them, run through the real engine on the real
    /// data (Dofus pour les noobs, «Clos des Blops»).
    /// </summary>
    [Collection("MapManager")]
    public class BlopSpellsTests
    {
        private const int BlopCoco = 273, BlopIndigo = 274, Blopiseta = 1182;
        private const int Blifo = 476, Bloproyeccion = 7067, BlifoGlyphSpell = 7176;

        private static Fighter Blop(long id, int monster, int cell) => new()
        {
            Id = id, TeamId = 1, CellId = cell, MaxHP = 720, CurrentHP = 720, Level = 54,
            IsMonster = true, MonsterId = monster, MaxAP = 6, CurrentAP = 6, MaxMP = 4, CurrentMP = 4,
        };

        private static Fighter Person(long id, int cell) => new()
        {
            Id = id, TeamId = 0, CellId = cell, MaxHP = 5000, CurrentHP = 5000, Level = 150,
            MaxAP = 12, CurrentAP = 12, MaxMP = 6, CurrentMP = 6,
        };

        /// <summary>
        /// Bloproyección's "4 de daños de tierra por 1 PM utilizado" waits on the end of its target's
        /// turn with no duration of its own. Cast by the last of the round on a player who has
        /// already played, it is his NEXT turn it waits on: armed to the next round alone, it was
        /// gone before he ended it, and his steps never cost him anything.
        /// </summary>
        [Fact]
        public void The_projection_s_poison_waits_on_the_next_turn_of_one_who_has_played()
        {
            if (SpellEffects.De(Bloproyeccion, 1).Count == 0) return;   // no spell data
            var fight = new FightInstance(1, 1);
            var me = Person(10, 262);
            var blop = Blop(-4, Blopiseta, 290);                         // two cells off: on the ring
            fight.AddPlayer(me); fight.AddMonster(blop);
            fight.StartFight();
            while (fight.CurrentFighter != blop) fight.NextTurn();
            Assert.True(fight.TurnOrder.IndexOf(me) < fight.CurrentTurnIndex);
            int round = fight.RoundNumber;

            var outcomes = EffectEngine.Resolver(fight, blop, Bloproyeccion, 1, blop, EffectEngine.AlLanzar, round,
                                                 celdaApuntada: blop.CellId);
            Assert.Contains(outcomes, o => o.FilaArmada && o.Sobre == me && o.Efecto.EffectId == 1140);
            FightHandler.EngancharLoPendiente(outcomes, Bloproyeccion, 1, blop.Id, round);

            var hook = Assert.Single(me.Buffs.ActiveSpells, h => h.Hechizo == Bloproyeccion && h.Filas != null);
            Assert.True(hook.Vivo(round + 1));      // the end of his next turn
            Assert.False(hook.Vivo(round + 2));     // and no other
        }

        /// <summary>
        /// "Por cada PM utilizado" counts from what his turn started with: the MP a Blifo took off
        /// him before it is not one he used. Two steps of three are two, not the three short of his four.
        /// </summary>
        [Fact]
        public void A_point_taken_before_the_turn_is_not_one_used()
        {
            var me = Person(10, 262);
            me.MaxMP = 4; me.MPAtTurnStart = 3; me.CurrentMP = 1;
            var poison = new SpellEffect { EffectId = 1140, DiceNum = 1, DiceSide = 4 };
            Assert.Equal(8, EffectEngine.BaseDelModo(poison, 0, new Fighter(), me, new FightInstance(1, 1)));

            me.MPAtTurnStart = null;                 // before his first turn: from the maximum
            Assert.Equal(12, EffectEngine.BaseDelModo(poison, 0, new Fighter(), me, new FightInstance(1, 1)));
        }

        /// <summary>The same row on one still to play this round goes off on that turn, and on no later one.</summary>
        [Fact]
        public void The_projection_s_poison_on_one_still_to_play_is_for_this_round()
        {
            if (SpellEffects.De(Bloproyeccion, 1).Count == 0) return;
            var fight = new FightInstance(1, 1);
            var me = Person(10, 262);
            var blop = Blop(-4, Blopiseta, 290);
            blop.Initiative = 5000;                                       // the Blop opens the round
            fight.AddPlayer(me); fight.AddMonster(blop);
            fight.StartFight();
            Assert.Equal(blop, fight.CurrentFighter);
            int round = fight.RoundNumber;

            var outcomes = EffectEngine.Resolver(fight, blop, Bloproyeccion, 1, blop, EffectEngine.AlLanzar, round,
                                                 celdaApuntada: blop.CellId);
            FightHandler.EngancharLoPendiente(outcomes, Bloproyeccion, 1, blop.Id, round);

            var hook = Assert.Single(me.Buffs.ActiveSpells, h => h.Hechizo == Bloproyeccion && h.Filas != null);
            Assert.True(hook.Vivo(round));
            Assert.False(hook.Vivo(round + 1));
        }

        /// <summary>
        /// Blifo's glyph burns whoever ends his turn ON it, and him alone: «Si un personnage termine
        /// son tour dedans, il subit 60 de dégâts». Its spell, 7176, draws the glyph's own cross; cast
        /// round a Blop ending its turn on one arm, that cross burnt the player standing next to the
        /// Blop, off the glyph.
        /// </summary>
        [Fact]
        public async Task Blifo_s_glyph_burns_only_who_ends_his_turn_on_it()
        {
            if (SpellEffects.De(Blifo, 1).Count == 0) return;
            var fight = new FightInstance(1, 1);
            var coco = Blop(-1, BlopCoco, 300);
            var indigo = Blop(-2, BlopIndigo, 248);      // on an arm of the cross
            var me = Person(10, 262);                    // next to him, off the glyph
            fight.AddPlayer(me); fight.AddMonster(coco); fight.AddMonster(indigo);

            await FightHandler.AplicarEfectosAsync(null, fight, coco, Blifo, 1, null, EffectEngine.AlLanzar, 235);
            var glyph = Assert.Single(fight.Glifos, g => g.Dueno == coco.Id);
            Assert.Equal(BlifoGlyphSpell, glyph.Hechizo);
            Assert.True(glyph.Cubre(248));
            Assert.False(glyph.Cubre(262));
            Assert.True(FightHandler.EsGlifoDeTurnoConSuForma(glyph));

            // The Blop ends his turn on it: nobody burns, he is no enemy of the glyph's.
            await FightHandler.TurnEndGlyphsAsync(null, fight, indigo);
            Assert.Equal(5000, me.CurrentHP);
            Assert.Equal(720, indigo.CurrentHP);

            // The player ends his turn on it: he burns.
            me.CellId = 249;
            await FightHandler.TurnEndGlyphsAsync(null, fight, me);
            Assert.True(me.CurrentHP < 5000);
            Assert.Equal(720, indigo.CurrentHP);
        }

        /// <summary>The few monster turn glyphs whose spell draws something else keep their zone round the one caught.</summary>
        [Fact]
        public void A_turn_glyph_whose_spell_draws_another_shape_is_not_taken_for_its_own()
        {
            const int FlaqueducGlyphSpell = 4536;       // a ring of two, laid as a circle of two
            if (SpellEffects.De(FlaqueducGlyphSpell, 1).Count == 0) return;
            var glyph = new Glifo(-1, new[] { 300 }, FlaqueducGlyphSpell, 1, 0, 3, "a,A", Disparo.AlAcabarElTurno)
            {
                Tipo = 402, ZoneShape = 'C', ZoneSize = 2,
            };
            Assert.False(FightHandler.EsGlifoDeTurnoConSuForma(glyph));
        }
    }
}
