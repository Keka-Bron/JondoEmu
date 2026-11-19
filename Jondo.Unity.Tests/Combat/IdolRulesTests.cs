using System.Linq;
using Jondo.Unity.Launcher;
using Jondo.Unity.Server.Managers;
using Jondo.Unity.World.Fights;
using Jondo.Unity.World.Maps;
using Xunit;

namespace Jondo.Unity.Tests.Combat
{
    /// <summary>
    /// The quest idols' fight rules (content/fights/idol_rules.json), run through the real engine on
    /// the client's old idol spells: each sentence of an idol's description, as it happens.
    /// </summary>
    [Collection("MapManager")]
    public class IdolRulesTests
    {
        private static Fighter Character(long id, int cell) => new()
        {
            Id = id, TeamId = 0, CellId = cell, MaxHP = 4000, CurrentHP = 4000, Level = 200,
            MaxAP = 12, CurrentAP = 12, MaxMP = 6, CurrentMP = 6,
        };

        private static Fighter Monster(long id, int cell) => new()
        {
            Id = id, TeamId = 1, CellId = cell, MaxHP = 3000, CurrentHP = 3000, Level = 150,
            MaxAP = 8, CurrentAP = 8, MaxMP = 4, CurrentMP = 4, IsMonster = true, MonsterId = 494,
        };

        private static int At(int cell, int dx, int dy)
        {
            var (x, y) = MapGeometry.CellToPoint(cell);
            return MapGeometry.PointToCell(x + dx, y + dy);
        }

        private static int Distance(int a, int b)
        {
            var (ax, ay) = MapGeometry.CellToPoint(a);
            var (bx, by) = MapGeometry.CellToPoint(b);
            return System.Math.Abs(ax - bx) + System.Math.Abs(ay - by);
        }

        private static IdolRules.Rule Rule(int alteration)
        {
            IdolRules.Forget();
            return Assert.IsType<IdolRules.Rule>(IdolRules.Of(alteration));
        }

        /// <summary>The ten idols of 1660-1662 have a rule, and every spell it names has rows.</summary>
        [Fact]
        public void Every_quest_idol_has_its_rule_of_spells_with_rows()
        {
            Assert.True(System.IO.File.Exists(Paths.ContentFile(IdolRules.File)));
            IdolRules.Forget();
            for (int alteration = 238; alteration <= 247; alteration++)
            {
                var rule = Assert.IsType<IdolRules.Rule>(IdolRules.Of(alteration));
                Assert.NotEmpty(rule.Enemies.Concat(rule.Allies));
                foreach (var attitude in rule.Enemies.Concat(rule.Allies))
                    Assert.True(SpellEffects.De(attitude.Spell, attitude.Grade).Count > 0,
                                $"alteration {alteration}: spell {attitude.Spell} grade {attitude.Grade} has no rows");
            }
        }

        /// <summary>The idol's own spell carries the marker in the client; the file's rows replace it.</summary>
        [Fact]
        public void The_written_rows_replace_the_idol_spell_marker()
        {
            Rule(238);
            var rows = SpellEffects.De(28438, 1);
            var row = Assert.Single(rows);
            Assert.Equal(2807, row.EffectId);
            Assert.Equal(40, row.DiceNum);
            Assert.DoesNotContain(rows, r => r.EffectId == 666);
        }

        /// <summary>Only a fight with a character holding the alteration gets the rules, on each side.</summary>
        [Fact]
        public void The_rules_go_on_the_monsters_and_the_characters_of_a_held_idol_only()
        {
            var rule = Rule(240);
            var fight = new FightInstance(1, 1);
            var character = Character(10, 300);
            var monster = Monster(-1, 330);
            fight.AddPlayer(character); fight.AddMonster(monster);

            Assert.Equal(0, IdolRules.PutOn(fight, f => f.IsMonster, (_, _) => false));
            Assert.Empty(monster.Buffs.Actitudes);

            int put = IdolRules.PutOn(fight, f => f.IsMonster, (who, alteration) => who == 10 && alteration == 240);
            Assert.Equal(rule.Enemies.Count + rule.Allies.Count, put);
            Assert.Equal(rule.Enemies.Select(a => a.Spell).OrderBy(s => s), monster.Buffs.Actitudes.OrderBy(s => s));
            Assert.Equal(rule.Allies.Select(a => a.Spell).OrderBy(s => s), character.Buffs.Actitudes.OrderBy(s => s));
        }

        /// <summary>«Los enemigos reducen los daños de los ataques a distancia un 40%»: +40% ranged resistance.</summary>
        [Fact]
        public void Viti_chamuscado_gives_the_monsters_forty_percent_ranged_resistance()
        {
            Rule(238);
            var fight = new FightInstance(1, 1);
            var monster = Monster(-1, 330);
            fight.AddPlayer(Character(10, 300)); fight.AddMonster(monster);

            EffectEngine.Resolver(fight, monster, 28438, 1, monster, EffectEngine.AlLanzar, 1, celdaApuntada: monster.CellId);

            Assert.Contains(monster.Buffs.Puestos, b => b.EffectId == 2807 && b.Cuanto == -40);
        }

        /// <summary>«Los adversarios ganan 1 PM cada vez que son alcanzados» (Genkyatto).</summary>
        [Fact]
        public void Genkyatto_gives_a_monster_a_movement_point_when_hit()
        {
            var fight = new FightInstance(1, 1);
            var character = Character(10, 300);
            var monster = Monster(-1, 330);
            fight.AddPlayer(character); fight.AddMonster(monster);

            fight.TriggeringAttacker = character;
            EffectEngine.Resolver(fight, monster, 5884, 1, monster, EffectEngine.CuandoMePegan, 1, celdaApuntada: monster.CellId);
            fight.TriggeringAttacker = null;

            Assert.Contains(monster.Buffs.Puestos, b => b.EffectId == 128 && b.Cuanto == 1);
        }

        /// <summary>
        /// «Al final de su turno, los enemigos atraen 1 casilla a todos los aliados situados a más de
        /// 4 AL» (Trespeó grade 3): the far one comes a cell nearer, the near one stays.
        /// </summary>
        [Fact]
        public void Trespeo_pulls_the_far_characters_at_the_monsters_turn_end()
        {
            var fight = new FightInstance(1, 1);
            var monster = Monster(-1, 300);
            var far = Character(10, At(300, 6, 0));
            var near = Character(11, At(300, 0, 2));
            fight.AddMonster(monster); fight.AddPlayer(far); fight.AddPlayer(near);
            int farBefore = far.CellId, nearBefore = near.CellId;

            EffectEngine.Resolver(fight, monster, 5918, 3, monster, EffectEngine.AlAcabarElTurno, 1, celdaApuntada: monster.CellId);

            Assert.Equal(Distance(farBefore, monster.CellId) - 1, Distance(far.CellId, monster.CellId));
            Assert.Equal(nearBefore, near.CellId);
        }

        /// <summary>
        /// «Si se está alineado con un enemigo o un aliado al final de turno, se pierde un 50% de
        /// los PdV durante 2 turnos» (Hóskar): in line, the vitality halves; out of line, nothing.
        /// </summary>
        [Fact]
        public void Hoskar_halves_the_vitality_of_who_ends_his_turn_in_line()
        {
            var fight = new FightInstance(1, 1);
            var inLine = Character(10, 300);
            var monster = Monster(-1, At(300, 3, 0));
            fight.AddPlayer(inLine); fight.AddMonster(monster);

            EffectEngine.Resolver(fight, inLine, 5893, 1, inLine, EffectEngine.AlAcabarElTurno, 1, celdaApuntada: inLine.CellId);
            Assert.True(inLine.MaxHP < 4000, $"max life {inLine.MaxHP}");

            var fight2 = new FightInstance(1, 1);
            var apart = Character(10, 300);
            fight2.AddPlayer(apart); fight2.AddMonster(Monster(-1, At(300, 3, 2)));
            EffectEngine.Resolver(fight2, apart, 5893, 1, apart, EffectEngine.AlAcabarElTurno, 1, celdaApuntada: apart.CellId);
            Assert.Equal(4000, apart.MaxHP);
        }

        /// <summary>
        /// «Si se permanece en una casilla adyacente a la de un enemigo al final de turno, se pierde
        /// un 50% de los PdV durante 2 turnos» (Ugah).
        /// </summary>
        [Fact]
        public void Ugah_halves_the_vitality_of_who_ends_his_turn_next_to_an_enemy()
        {
            var fight = new FightInstance(1, 1);
            var beside = Character(10, 300);
            fight.AddPlayer(beside); fight.AddMonster(Monster(-1, At(300, 1, 0)));
            EffectEngine.Resolver(fight, beside, 5890, 1, beside, EffectEngine.AlAcabarElTurno, 1, celdaApuntada: beside.CellId);
            Assert.True(beside.MaxHP < 4000, $"max life {beside.MaxHP}");

            var fight2 = new FightInstance(1, 1);
            var away = Character(10, 300);
            fight2.AddPlayer(away); fight2.AddMonster(Monster(-1, At(300, 3, 0)));
            EffectEngine.Resolver(fight2, away, 5890, 1, away, EffectEngine.AlAcabarElTurno, 1, celdaApuntada: away.CellId);
            Assert.Equal(4000, away.MaxHP);
        }

        /// <summary>
        /// «Si no se permanece en una casilla adyacente a la de un enemigo al final de turno, se
        /// pierde un 50% de los PdV durante 2 turnos» (Muta): the reverse of Ugah.
        /// </summary>
        [Fact]
        public void Muta_halves_the_vitality_of_who_ends_his_turn_away_from_the_enemies()
        {
            var fight = new FightInstance(1, 1);
            var away = Character(10, 300);
            fight.AddPlayer(away); fight.AddMonster(Monster(-1, At(300, 3, 0)));
            EffectEngine.Resolver(fight, away, 5891, 1, away, EffectEngine.AlAcabarElTurno, 1, celdaApuntada: away.CellId);
            Assert.True(away.MaxHP < 4000, $"max life {away.MaxHP}");

            var fight2 = new FightInstance(1, 1);
            var beside = Character(10, 300);
            fight2.AddPlayer(beside); fight2.AddMonster(Monster(-1, At(300, 1, 0)));
            EffectEngine.Resolver(fight2, beside, 5891, 1, beside, EffectEngine.AlAcabarElTurno, 1, celdaApuntada: beside.CellId);
            Assert.Equal(4000, beside.MaxHP);
        }

        /// <summary>
        /// «Si se termina el turno a 2 casillas o menos de un aliado, se da 2 PM a los enemigos»
        /// (Foluk Dorado, 28440's written row): another ally within 2 cells, not the character himself.
        /// </summary>
        [Fact]
        public void Foluk_dorado_gives_the_monsters_two_movement_points_for_allies_side_by_side()
        {
            Rule(245);
            var fight = new FightInstance(1, 1);
            var character = Character(10, 300);
            var friend = Character(11, At(300, 2, 0));
            var monster = Monster(-1, At(300, 0, 6));
            fight.AddPlayer(character); fight.AddPlayer(friend); fight.AddMonster(monster);

            EffectEngine.Resolver(fight, character, 28440, 1, character, EffectEngine.AlAcabarElTurno, 1, celdaApuntada: character.CellId);
            Assert.Contains(monster.Buffs.Puestos, b => b.EffectId == 128 && b.Cuanto == 2);

            var fight2 = new FightInstance(1, 1);
            var alone = Character(10, 300);
            var other = Monster(-1, At(300, 0, 6));
            fight2.AddPlayer(alone); fight2.AddPlayer(Character(11, At(300, 5, 0))); fight2.AddMonster(other);
            EffectEngine.Resolver(fight2, alone, 28440, 1, alone, EffectEngine.AlAcabarElTurno, 1, celdaApuntada: alone.CellId);
            Assert.DoesNotContain(other.Buffs.Puestos, b => b.EffectId == 128);
        }

        /// <summary>«Los enemigos devuelven un 50% de los daños recibidos»: half the blow back on its dealer.</summary>
        [Fact]
        public void Thomahon_nauseabundo_monsters_return_half_the_blow()
        {
            Rule(241);
            var fight = new FightInstance(1, 1);
            var character = Character(10, 300);
            var monster = Monster(-1, At(300, 3, 0));
            fight.AddPlayer(character); fight.AddMonster(monster);

            fight.TriggeringAttacker = character;
            fight.DanoDelDisparo = 200;
            fight.ElementoDelDisparo = 1;
            var outcomes = EffectEngine.Resolver(fight, monster, 28439, 1, monster, EffectEngine.CuandoMePegan, 1,
                                                 celdaApuntada: monster.CellId);
            fight.TriggeringAttacker = null;

            var returned = Assert.Single(outcomes, o => o.NestedDamage);
            Assert.Equal(character, returned.Sobre);
            Assert.Equal(100, EffectEngine.BaseDelModo(returned.Efecto, returned.Efecto.DiceNum, monster, character, fight));
        }

        /// <summary>
        /// «Cuando se ataca a un enemigo, los aliados alrededor del atacante en una zona de 3 casillas
        /// reciben un 100% de los daños ocasionados» (Pikmi grade 2): around the attacker, not around
        /// the monster hit.
        /// </summary>
        [Fact]
        public void Pikmi_shares_the_blow_out_around_the_attacker()
        {
            var fight = new FightInstance(1, 1);
            var attacker = Character(10, 300);
            var beside = Character(11, At(300, 1, 0));
            var far = Character(12, At(300, 0, 8));
            var monster = Monster(-1, At(300, 5, 0));
            fight.AddPlayer(attacker); fight.AddPlayer(beside); fight.AddPlayer(far); fight.AddMonster(monster);

            fight.TriggeringAttacker = attacker;
            fight.DanoDelDisparo = 120;
            fight.ElementoDelDisparo = 1;
            var outcomes = EffectEngine.Resolver(fight, monster, 5921, 2, monster, EffectEngine.CuandoMePegan, 1,
                                                 celdaApuntada: monster.CellId);
            fight.TriggeringAttacker = null;

            var shared = outcomes.Where(o => o.NestedDamage).Select(o => o.Sobre).ToList();
            Assert.Contains(beside, shared);
            Assert.DoesNotContain(far, shared);
            Assert.DoesNotContain(monster, shared);
        }

        /// <summary>«No es posible ni robar vida a los enemigos ni curarse» (Penitente): Incurable.</summary>
        [Fact]
        public void Foluk_aranado_leaves_the_characters_incurable()
        {
            var fight = new FightInstance(1, 1);
            var character = Character(10, 300);
            fight.AddPlayer(character); fight.AddMonster(Monster(-1, At(300, 3, 0)));

            EffectEngine.Resolver(fight, character, 5886, 1, character, EffectEngine.AlLanzar, 1, celdaApuntada: character.CellId);

            Assert.True(character.Buffs.TieneEstado(76));
        }

        /// <summary>«Los monstruos son invulnerables mientras no se desplacen» (Djim): the state Djim.</summary>
        [Fact]
        public void Foluk_feerico_monsters_are_invulnerable_until_moved()
        {
            var fight = new FightInstance(1, 1);
            var monster = Monster(-1, 330);
            fight.AddPlayer(Character(10, 300)); fight.AddMonster(monster);

            EffectEngine.Resolver(fight, monster, 5896, 1, monster, EffectEngine.AlLanzar, 1, celdaApuntada: monster.CellId);

            Assert.True(monster.Buffs.TieneEstado(298));
        }
    }
}
