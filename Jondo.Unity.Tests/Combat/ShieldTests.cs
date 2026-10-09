using System.Collections.Generic;
using System.Linq;
using Jondo.Unity.Server;
using Jondo.Unity.World.Fights;
using Jondo.Unity.Server.Handlers;
using Jondo.Unity.Server.Managers;
using Jondo.Unity.Server.Network;
using Jondo.Unity.Protocol;
using Microsoft.Data.Sqlite;
using System.Threading.Tasks;
using Xunit;

namespace Jondo.Unity.Tests.Combat
{
    /// <summary>
    /// The shield: effects 1020 and 1039, 401 spells between the two.
    /// </summary>
    /// <remarks>
    /// 1020 gives a percentage of the caster's LEVEL and 1039 one of his LIFE. The
    /// percentage goes in the die, not in the value: Caparazón carries diceNum 150 and Soldagüino 200;
    /// Bendición Maravillosa 10 and Coraza de Dopeul 20. The value is zero in the six read.
    ///
    /// What these tests pin is that the shield is NOT life: it is not healed, it does not count for
    /// death and it drops on its own. Putting it in CurrentHP would have been shorter and would have left a
    /// shielded character healing up to the shield's cap.
    /// </remarks>
    public class ShieldTests
    {
        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public void Damage_packet_carries_shield_loss_for_the_purple_number(bool push)
        {
            byte[] packet = push
                ? FightProtocol.BuildPushDamage(-1, 13825566, 31, erosion: 3, shieldLoss: 437)
                : FightProtocol.BuildDamage(-1, 100, 13825566, 31, elemento: 0,
                                            erosion: 3, shieldLoss: 437);

            var action = ProtoMessage.Parse(packet);
            var damage = ProtoMessage.Parse(action.Fields.Single(f => f.FieldNumber == 40).BytesValue);

            Assert.Equal(437, damage.Fields.Single(f => f.FieldNumber == 1).VarIntValue);
            Assert.Equal(13825566, damage.Fields.Single(f => f.FieldNumber == 2).VarIntValue);
            Assert.Equal(31, damage.Fields.Single(f => f.FieldNumber == 3).VarIntValue);
        }

        [Theory]
        [InlineData(1, 480)]
        [InlineData(2, 720)]
        [InlineData(3, 960)]
        public void Pelage_protecteur_gives_the_shield_at_every_grade(int grade, int expected)
        {
            var fight = new FightInstance(1, 1);
            var ouginak = Uno();
            ouginak.CellId = 300;
            fight.AddPlayer(ouginak);

            EffectEngine.Resolver(fight, ouginak, 13772, grade, ouginak,
                                  EffectEngine.AlLanzar, 0, celdaApuntada: ouginak.CellId);

            Assert.Equal(expected, ouginak.PuntosDeEscudo);
        }

        [Fact]
        public void Every_catalogue_shield_row_works_at_every_spell_grade()
        {
            var ranks = new List<(int Spell, int Grade)>();
            using (var connection = new SqliteConnection(DatabaseManager.WorldConnectionString))
            {
                connection.Open();
                using var command = connection.CreateCommand();
                command.CommandText = "SELECT SpellId, Grade FROM SpellLevels ORDER BY SpellId, Grade;";
                using var reader = command.ExecuteReader();
                while (reader.Read()) ranks.Add((reader.GetInt32(0), reader.GetInt32(1)));
            }

            int checkedRows = 0;
            foreach (var rank in ranks)
            foreach (bool critical in new[] { false, true })
            {
                var rows = critical ? SpellEffects.Criticos(rank.Spell, rank.Grade)
                                    : SpellEffects.De(rank.Spell, rank.Grade);
                foreach (var source in rows.Where(e => e.EffectId is 1020 or 1039))
                {
                    int percent = source.DiceNum != 0 ? source.DiceNum : source.Value;
                    if (percent <= 0) continue;

                    var fight = new FightInstance(1, 1);
                    var caster = Uno();
                    caster.CellId = 300;
                    fight.AddPlayer(caster);
                    var row = new SpellEffect
                    {
                        EffectId = source.EffectId,
                        EffectUid = source.EffectUid,
                        DiceNum = source.DiceNum,
                        Value = source.Value,
                        Duration = source.Duration,
                        Dispellable = source.Dispellable,
                        MaxStack = source.MaxStack,
                        Flags = source.Flags,
                        Triggers = "I",
                        TargetMask = "C",
                    };

                    EffectEngine.ResolveEffects(fight, caster, rank.Spell, rank.Grade, caster,
                        EffectEngine.AlLanzar, 0, new[] { row }, aimedCell: caster.CellId);

                    int basis = source.EffectId == 1020 ? caster.StatLevel : caster.MaxHP;
                    int expected = basis * percent / 100;
                    Assert.True(caster.PuntosDeEscudo == expected,
                        $"Spell {rank.Spell}, grade {rank.Grade}, critical={critical}, " +
                        $"effect {source.EffectUid}: expected {expected}, got {caster.PuntosDeEscudo}.");
                    Assert.Equal(17, caster.PasarPorElEscudo(expected + 17));
                    Assert.Equal(0, caster.PuntosDeEscudo);
                    checkedRows++;
                }
            }

            Assert.True(checkedRows > 0, "The catalogue must contain shield effects to validate.");
        }

        [Theory]
        [InlineData(200, 1000, 760)]
        [InlineData(1130, 830, 0)]
        public async Task Ouginak_shield_absorbs_enemy_spell_damage(int hit, int hp, int shield)
        {
            var fight = new FightInstance(1, 1);
            var ouginak = Uno();
            ouginak.CellId = 300;
            var bot = new Fighter { Id = -1, TeamId = 1, CellId = 330,
                Level = 200, CurrentHP = 1000, MaxHP = 1000, IsMonster = true };
            fight.AddPlayer(ouginak);
            fight.AddOpponent(bot);
            // Pelage protecteur applies two shields: 2 x 240% of level 200 gives 960 points.
            EffectEngine.Resolver(fight, ouginak, 13772, 3, ouginak, EffectEngine.AlLanzar, 0);
            Assert.Equal(960, ouginak.PuntosDeEscudo);

            await FightHandler.HurtAsync(null, fight, bot, 1, 1, ouginak, ouginak.CellId,
                tirada: new[] { new SpellEffect { EffectId = 97, DiceNum = hit, DiceSide = hit,
                    Element = 1, TargetMask = "a,A" } });

            Assert.Equal(hp, ouginak.CurrentHP);
            Assert.Equal(shield, ouginak.PuntosDeEscudo);
        }

        [Theory]
        [InlineData(200, 100, 100, 100)]
        [InlineData(350, 100, 50, 0)]
        [InlineData(450, 1000, 850, 0)]
        public async Task Collision_consumes_shield_before_hp_and_death(int hit, int initialHp, int hp, int shield)
        {
            var fight = new FightInstance(1, 1);
            var target = Uno(initialHp);
            var enemy = new Fighter { Id = -1, TeamId = 1, CurrentHP = 1000, MaxHP = 1000, IsMonster = true };
            fight.AddPlayer(target);
            fight.AddOpponent(enemy);
            target.Escudar(300, 3);

            await FightHandler.UnEstampadoAsync(null, fight, enemy, target, hit);

            Assert.Equal(hp, target.CurrentHP);
            Assert.Equal(shield, target.PuntosDeEscudo);
            Assert.True(target.IsAlive);
        }

        private static Fighter Uno(int vida = 1000) => new Fighter
        {
            Id = 1, Level = 200, MaxHP = vida, CurrentHP = vida,
        };

        [Fact]
        public void El_escudo_se_come_el_golpe_antes_que_la_vida()
        {
            var quien = Uno();
            quien.Escudar(300, caducaEnRonda: 3);

            // A hit smaller than the shield does not touch life.
            Assert.Equal(0, quien.PasarPorElEscudo(200));
            Assert.Equal(100, quien.PuntosDeEscudo);

            // And a bigger one lets through only what is left over.
            Assert.Equal(150, quien.PasarPorElEscudo(250));
            Assert.Equal(0, quien.PuntosDeEscudo);
        }

        [Fact]
        public void Sin_escudo_el_golpe_pasa_entero()
        {
            var quien = Uno();
            Assert.Equal(500, quien.PasarPorElEscudo(500));
        }

        [Fact]
        public void Se_suma_y_se_queda_la_caducidad_mas_lejana()
        {
            var quien = Uno();
            quien.Escudar(100, caducaEnRonda: 3);
            quien.Escudar(200, caducaEnRonda: 6);

            Assert.Equal(300, quien.PuntosDeEscudo);

            // In round 3 it still holds, because the second lasts until round 6.
            quien.CaducarElEscudo(3);
            Assert.Equal(300, quien.PuntosDeEscudo);

            quien.CaducarElEscudo(6);
            Assert.Equal(0, quien.PuntosDeEscudo);
        }

        [Fact]
        public void El_escudo_no_es_vida()
        {
            // It neither counts for death nor is healed: they are two different sacks.
            var quien = Uno(vida: 100);
            quien.Escudar(500, caducaEnRonda: 9);

            Assert.Equal(100, quien.CurrentHP);
            Assert.Equal(100, quien.MaxHP);
            Assert.True(quien.IsAlive);

            quien.TakeDamage(quien.PasarPorElEscudo(600));
            Assert.False(quien.IsAlive);
        }
    }
}
