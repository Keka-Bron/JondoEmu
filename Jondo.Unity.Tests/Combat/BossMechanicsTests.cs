using System.Collections.Generic;
using System.Linq;
using Jondo.Unity.Server.Handlers;
using Jondo.Unity.Server.Managers;
using Jondo.Unity.World.Combat;
using Jondo.Unity.World.Fights;
using Jondo.Unity.World.Maps;
using Xunit;

namespace Jondo.Unity.Tests.Combat
{
    /// <summary>
    /// What a boss is made of, run through the real engine on the real data: Conde Kontatrás,
    /// whose clock holds every piece -- the behaviour spell, the sub-casts that wait, the telefrag
    /// and the 952 that lifts his invulnerability.
    /// </summary>
    /// <remarks>
    /// The guide (dofuspourlesnoobs, Comte Harebourg): invulnerable the whole fight; on odd turns a
    /// hit on him throws the ATTACKER to the other side of him; on even turns it throws HIM to the
    /// other side of the attacker, and if that cell is taken the two swap and he loses the
    /// invulnerability; swapping with a character gives that character +100 damage.
    /// </remarks>
    [Collection("MapManager")]
    public class BossMechanicsTests
    {
        private const int Kontatras = 3416;
        private const int Carrillon = 3642;      // his behaviour spell
        private const int Alternancia = 3647;    // odd turns
        private const int MedioTiempo = 3648;    // even turns
        private const int RelojDeBolsillo = 3657; // what a hit on an even turn casts
        private const int Contratiempo = 3653;
        private const int Jaquemart = 3655;      // HS=56
        private const int Multicuenta = 3654;    // HS!56
        private const int Invulnerable = 56;

        private static Fighter Person(long id, int team, int cell) => new()
        {
            Id = id, TeamId = team, CellId = cell, MaxHP = 5000, CurrentHP = 5000, Level = 200,
            MaxAP = 12, CurrentAP = 12, MaxMP = 6, CurrentMP = 6,
        };

        private static Fighter Count(int cell) => new()
        {
            Id = -1, TeamId = 1, CellId = cell, MaxHP = 20000, CurrentHP = 20000, Level = 200,
            IsMonster = true, MonsterId = Kontatras, MaxAP = 12, CurrentAP = 12, MaxMP = 5, CurrentMP = 5,
        };

        /// <summary>The conditions on the caster's states: HS=, HS!, &amp;, | and brackets.</summary>
        [Theory]
        [InlineData("HS=56", new[] { 56 }, true)]
        [InlineData("HS=56", new int[0], false)]
        [InlineData("HS!56", new[] { 56 }, false)]
        [InlineData("HS=87&HS!86", new[] { 87 }, true)]
        [InlineData("HS=87&HS!86", new[] { 87, 86 }, false)]
        [InlineData("HS=98|HS=100", new[] { 100 }, true)]
        [InlineData("(HS=3360|HS=3589)&HS!7", new[] { 3589 }, true)]
        [InlineData("(HS=3360|HS=3589)&HS!7", new[] { 3589, 7 }, false)]
        [InlineData("HS=3|(HS=3531&HS!534&HS!661&HS!8)", new[] { 3531 }, true)]
        [InlineData("HS=3|(HS=3531&HS!534&HS!661&HS!8)", new[] { 3531, 8 }, false)]
        [InlineData("", new int[0], true)]
        public void A_spell_level_asks_for_its_caster_s_states(string criterion, int[] states, bool holds)
            => Assert.Equal(holds, SpellCriteria.Holds(criterion, states.Contains));

        /// <summary>Jaquemart while he is invulnerable, Multicuenta once he is not -- off the base.</summary>
        [Fact]
        public void His_attacks_follow_his_invulnerability()
        {
            var count = Count(300);
            count.Buffs.PonerEstado(Invulnerable);
            Assert.True(SpellCriteria.Allows(count, Jaquemart, 1));
            Assert.False(SpellCriteria.Allows(count, Multicuenta, 1));

            count.Buffs.QuitarEstado(Invulnerable);
            Assert.False(SpellCriteria.Allows(count, Jaquemart, 1));
            Assert.True(SpellCriteria.Allows(count, Multicuenta, 1));
        }

        /// <summary>A 952 switches a state off while its row lives, and the state is back when it falls.</summary>
        [Fact]
        public void A_disabled_state_does_not_count_while_its_row_lives()
        {
            var who = Person(10, 0, 300);
            who.Buffs.PonerEstado(Invulnerable);
            who.Buffs.Poner(new Buff { EffectId = EffectSupport.DisableState, Estado = Invulnerable, CaducaEnRonda = 3 }, () => 1);

            Assert.False(who.Buffs.TieneEstado(Invulnerable));
            Assert.DoesNotContain(Invulnerable, who.Buffs.Estados);
            Assert.False(SpellStates.ShieldsFromBlow(who, 3));

            who.Buffs.Barrer(3);

            Assert.True(who.Buffs.TieneEstado(Invulnerable));
            Assert.True(SpellStates.ShieldsFromBlow(who, 3));
        }

        /// <summary>
        /// His behaviour spell at the start: Invulnerable for good, the odd-turn state, and the
        /// even turn waiting for the next round -- not cast at once, which is what made the two
        /// halves of his clock call each other in the same instant.
        /// </summary>
        [Fact]
        public void The_clock_starts_invulnerable_and_waits_a_round_for_the_even_turn()
        {
            var fight = new FightInstance(1, 1);
            var count = Count(300);
            var player = Person(10, 0, 330);
            fight.AddMonster(count); fight.AddPlayer(player);

            EffectEngine.Resolver(fight, count, Carrillon, 1, count, EffectEngine.AlLanzar, fight.RoundNumber, celdaApuntada: count.CellId);

            Assert.True(count.Buffs.TieneEstado(Invulnerable));
            Assert.True(count.Buffs.TieneEstado(7152));
            var waiting = Assert.Single(count.Buffs.Puestos, b => b.Pendiente);
            Assert.Equal(MedioTiempo, waiting.Dado);
            Assert.Equal(fight.RoundNumber + 1, waiting.EmpiezaEnRonda);

            var due = EffectEngine.ActivateDuePending(fight, fight.RoundNumber + 1);
            var cast = Assert.Single(due);
            Assert.True(cast.Casts);
            Assert.Null(cast.Live);
        }

        /// <summary>
        /// An even-turn hit with somebody on the mirror cell: the Count and that one swap -- a
        /// telefrag -- the Count's invulnerability is switched off, and the one swapped gets +100
        /// damage. Nobody dies: nobody swapped with a summon.
        /// </summary>
        [Fact]
        public void An_even_turn_hit_that_swaps_him_lifts_his_invulnerability()
        {
            var fight = new FightInstance(1, 1);
            var count = Count(300);
            var attacker = Person(10, 0, 302);
            int mirror = MapGeometry.Reflejar(count.CellId, attacker.CellId);
            Assert.True(mirror >= 0);
            var friend = Person(11, 0, mirror);
            fight.AddMonster(count); fight.AddPlayer(attacker); fight.AddPlayer(friend);
            count.Buffs.PonerEstado(Invulnerable);

            fight.TriggeringAttacker = attacker;
            var outcomes = EffectEngine.Resolver(fight, attacker, RelojDeBolsillo, 1, count, EffectEngine.AlLanzar,
                                                 fight.RoundNumber, celdaApuntada: count.CellId);
            fight.TriggeringAttacker = null;

            Assert.Equal(mirror, count.CellId);
            Assert.Equal(300, friend.CellId);
            Assert.False(count.Buffs.TieneEstado(Invulnerable));
            Assert.Contains(outcomes, o => o.Sobre == friend && o.Efecto.EffectId == 112);
            Assert.True(attacker.IsAlive && friend.IsAlive);
        }

        /// <summary>With the mirror cell free he is only thrown there, and stays invulnerable.</summary>
        [Fact]
        public void An_even_turn_hit_on_a_free_cell_only_moves_him()
        {
            var fight = new FightInstance(1, 1);
            var count = Count(300);
            var attacker = Person(10, 0, 302);
            int mirror = MapGeometry.Reflejar(count.CellId, attacker.CellId);
            fight.AddMonster(count); fight.AddPlayer(attacker);
            count.Buffs.PonerEstado(Invulnerable);

            fight.TriggeringAttacker = attacker;
            EffectEngine.Resolver(fight, attacker, RelojDeBolsillo, 1, count, EffectEngine.AlLanzar,
                                  fight.RoundNumber, celdaApuntada: count.CellId);
            fight.TriggeringAttacker = null;

            Assert.Equal(mirror, count.CellId);
            Assert.True(count.Buffs.TieneEstado(Invulnerable));
        }

        /// <summary>Contratiempo sends its target back to its turn-start cell once he is vulnerable.</summary>
        [Fact]
        public void Contratiempo_sends_back_to_the_turn_start_cell_only_when_he_is_vulnerable()
        {
            var fight = new FightInstance(1, 1);
            var count = Count(300);
            var player = Person(10, 0, 330);
            fight.AddMonster(count); fight.AddPlayer(player);
            player.CasillaAlEmpezarTurno = 360;

            count.Buffs.PonerEstado(Invulnerable);
            EffectEngine.Resolver(fight, count, Contratiempo, 1, player, EffectEngine.AlLanzar, fight.RoundNumber,
                                  celdaApuntada: player.CellId);
            Assert.Equal(330, player.CellId);

            count.Buffs.QuitarEstado(Invulnerable);
            EffectEngine.Resolver(fight, count, Contratiempo, 1, player, EffectEngine.AlLanzar, fight.RoundNumber,
                                  celdaApuntada: player.CellId);
            Assert.Equal(360, player.CellId);
        }

        /// <summary>Nobody pinned in place is teleported, nor swapped out of his cell.</summary>
        [Fact]
        public void The_indesplazable_are_neither_thrown_nor_swapped()
        {
            var fight = new FightInstance(1, 1);
            var count = Count(300);
            var attacker = Person(10, 0, 302);
            int mirror = MapGeometry.Reflejar(count.CellId, attacker.CellId);
            var friend = Person(11, 0, mirror);
            fight.AddMonster(count); fight.AddPlayer(attacker); fight.AddPlayer(friend);
            count.Buffs.PonerEstado(Invulnerable);
            friend.Buffs.PonerEstado(97);

            fight.TriggeringAttacker = attacker;
            EffectEngine.Resolver(fight, attacker, RelojDeBolsillo, 1, count, EffectEngine.AlLanzar,
                                  fight.RoundNumber, celdaApuntada: count.CellId);
            fight.TriggeringAttacker = null;

            Assert.Equal(300, count.CellId);
            Assert.Equal(mirror, friend.CellId);
            Assert.True(count.Buffs.TieneEstado(Invulnerable));
        }

        /// <summary>The 793, 1018 and 1019 are in the sub-cast table, each casting as its reading says.</summary>
        [Theory]
        [InlineData(793, true, "AlCandidato")]
        [InlineData(1018, false, "AlCandidato")]
        [InlineData(1019, false, "AlOrigen")]
        public void The_monster_sub_casts_are_in_the_table(int effect, bool candidateCasts, string aims)
        {
            Assert.True(EffectEngine.EsDeLaFamiliaDeSublanzar(effect));
            Assert.Equal((candidateCasts, aims), EffectEngine.ComoSublanza(effect));
        }

        /// <summary>A monster carries its behaviour spell from the moment it is built, to cast at the start.</summary>
        [Fact]
        public void A_monster_carries_its_behaviour_spell()
        {
            MobSpawnManager.EnsureMonsterData();
            var group = MobSpawnManager.ComposeOffMap(new[] { (Kontatras, 4) })!;

            var built = FightHandler.BuildMonsterFighter(group.Members[0], -1, 300);

            Assert.Equal(Carrillon, built.Conducta.Spell);
            Assert.DoesNotContain(Carrillon, built.Buffs.Actitudes);
        }

        /// <summary>
        /// Cast at the start, his behaviour spell ARMS its triggered rows on whoever they name: the
        /// turn-start confusion on every player, the turn-start glyph on himself, the melee one on
        /// his side -- each to go off on its own bearer.
        /// </summary>
        [Fact]
        public void The_clock_arms_its_rows_on_the_fighters_they_name()
        {
            var fight = new FightInstance(1, 1);
            var count = Count(300);
            var ally = new Fighter { Id = -2, TeamId = 1, CellId = 330, MaxHP = 1000, CurrentHP = 1000, IsMonster = true, MonsterId = 3417 };
            var p1 = Person(10, 0, 250);
            var p2 = Person(11, 0, 360);
            fight.AddMonster(count); fight.AddMonster(ally); fight.AddPlayer(p1); fight.AddPlayer(p2);

            var outcomes = EffectEngine.Resolver(fight, count, Carrillon, 1, count, EffectEngine.AlLanzar,
                                                 fight.RoundNumber, celdaApuntada: count.CellId);
            var armed = outcomes.Where(o => o.FilaArmada).ToList();

            // 793 TB on the players (mask A), for each at his own turn start.
            Assert.Contains(armed, o => o.Sobre == p1 && o.Efecto.Triggers == "TB" && o.Efecto.DiceNum == 3646);
            Assert.Contains(armed, o => o.Sobre == p2 && o.Efecto.Triggers == "TB" && o.Efecto.DiceNum == 3646);
            Assert.DoesNotContain(armed, o => o.Sobre == count && o.Efecto.DiceNum == 3646);
            // The glyph on himself (mask c).
            Assert.Contains(armed, o => o.Sobre == count && o.Efecto.DiceNum == 3644);
            // In melee, on his own side (mask a).
            Assert.Contains(armed, o => o.Sobre == ally && o.Efecto.Triggers == "DM");
            Assert.All(armed, o => Assert.Same(count, o.Caster));
        }

        /// <summary>The class spells and what they reach are the players'; a boss's are not.</summary>
        [Fact]
        public void The_players_spells_are_told_apart()
        {
            Assert.True(PlayerSpells.Contains(13024));   // Anutrof, Excursión
            Assert.True(PlayerSpells.Contains(13036));   // and the glyph it lays
            Assert.False(PlayerSpells.Contains(Carrillon));
            Assert.False(PlayerSpells.Contains(RelojDeBolsillo));
        }

        /// <summary>
        /// A teleport with nowhere to land is the W of the masks: with the mirror cell off the board,
        /// Reloj de Bolsillo's Época kills the Count's enemies -- "if a required symmetric cell does
        /// not exist, the fight ends with all characters dead".
        /// </summary>
        [Fact]
        public void A_teleport_with_nowhere_to_land_is_W()
        {
            // The attacker on the board's edge, the Count two cells in from him: the Count's mirror
            // about the attacker falls off the board.
            int attackerCell = 1, countCell = -1;
            for (int cell = 0; cell < 560 && countCell < 0; cell++)
            {
                if (MapGeometry.Distance(cell, attackerCell) == 2 && MapGeometry.Reflejar(cell, attackerCell) < 0)
                    countCell = cell;
            }
            Assert.True(countCell >= 0);

            var fight = new FightInstance(1, 1);
            var count = Count(countCell);
            var attacker = Person(10, 0, attackerCell);
            var friend = Person(11, 0, 400);
            fight.AddMonster(count); fight.AddPlayer(attacker); fight.AddPlayer(friend);
            count.Buffs.PonerEstado(Invulnerable);

            fight.TriggeringAttacker = attacker;
            var outcomes = EffectEngine.Resolver(fight, attacker, RelojDeBolsillo, 1, count, EffectEngine.AlLanzar,
                                                 fight.RoundNumber, celdaApuntada: count.CellId);
            fight.TriggeringAttacker = null;

            // The kills the fight then deals: both players, wherever they stand.
            Assert.True(outcomes.Any(o => o.Fulmina && o.Sobre == attacker),
                string.Join(" | ", outcomes.Select(o => $"{o.HechizoOrigen}:{o.Efecto?.EffectId}->{o.Sobre?.Id} mueve={o.Mueve} fin={o.CasillaHasta}")) +
                $" count@{count.CellId} attacker@{attacker.CellId}");
            Assert.Contains(outcomes, o => o.Fulmina && o.Sobre == friend);
            Assert.True(count.Buffs.TieneEstado(Invulnerable));
        }
    }
}
