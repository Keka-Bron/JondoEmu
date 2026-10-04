using System;
using System.Linq;
using Jondo.Unity.Server;
using Jondo.Unity.Server.Managers;
using Jondo.Unity.Server.Network;
using Jondo.Unity.World.Fights;
using Xunit;

namespace Jondo.Unity.Tests.Combat
{
    /// <summary>
    /// The steals -- of a characteristic, of range, of points -- as the real server sends them:
    /// two rows with the steal's uid, the characteristic's malus on the target and its bonus on
    /// the caster. Read off the Sram's Estafa capture and the Hipermago's Desecación.
    /// </summary>
    [Collection("removal dice")]
    public class StealTests
    {
        private const int Estafa = 12911;          // 268, "robo de agilidad", uid 371888
        private const int Desecacion = 13705;      // 320, "roba alcance", uid 213583
        private const int Agility = 14;
        private const int Range = 19;

        private static Fighter Player(long id, int team, int cell) => new()
        {
            Id = id, TeamId = team, CellId = cell, MaxHP = 2000, CurrentHP = 2000, Level = 200,
            MaxAP = 12, CurrentAP = 12, MaxMP = 6, CurrentMP = 6,
        };

        private static Fighter Monster(long id, int cell) => new()
        {
            Id = id, TeamId = 1, CellId = cell, MaxHP = 900, CurrentHP = 900, Level = 50,
            MaxAP = 6, CurrentAP = 6, MaxMP = 3, CurrentMP = 3, IsMonster = true, MonsterId = 494,
        };

        private static FightInstance AtRound(int round)
        {
            var fight = new FightInstance(1, 1);
            typeof(FightInstance).GetProperty(nameof(FightInstance.RoundNumber))!.SetValue(fight, round);
            return fight;
        }

        /// <summary>
        /// The catalogue names every steal's pair: the four characteristics of the Sram and the
        /// Hipermago, range, and the points.
        /// </summary>
        [Fact]
        public void The_catalogue_names_each_steals_two_rows()
        {
            Assert.Equal(new Steals.Steal(14, 154, 119), Steals.Of(268));
            Assert.Equal(new Steals.Steal(13, 152, 123), Steals.Of(266));
            Assert.Equal(new Steals.Steal(15, 155, 126), Steals.Of(269));
            Assert.Equal(new Steals.Steal(10, 157, 118), Steals.Of(271));
            Assert.Equal(new Steals.Steal(19, 116, 117), Steals.Of(320));
            Assert.Equal(new Steals.Steal(23, 169, 128), Steals.Of(77));
            Assert.Equal(new Steals.Steal(1, 168, 111), Steals.Of(84));
            Assert.Null(Steals.Of(91));   // a life steal is a blow
            Assert.Null(Steals.Of(98));
        }

        /// <summary>
        /// Estafa at grade 3, cast in round 5: "jxm 154 dice 100" on the enemy and "jxm 119 dice
        /// 100" on the Sram, uid 371888, until round 8 -- frames 14 and 20 of "sram-estafa",
        /// byte for byte but for the row's number. The enemy loses the hundred and the Sram
        /// gains it; it used to go on the enemy as a plus.
        /// </summary>
        [Fact]
        public void Estafa_takes_agility_off_the_enemy_and_gives_it_to_the_Sram()
        {
            var fight = AtRound(5);
            var sram = Player(53720907875, 0, 300);
            var enemy = Monster(-2, 301);
            fight.AddPlayer(sram); fight.AddOpponent(enemy);

            var outcomes = EffectEngine.Resolver(fight, sram, Estafa, 3, enemy, EffectEngine.AlLanzar, 5,
                                                 celdaApuntada: 301);

            var malus = Assert.Single(outcomes, o => o.Sobre == enemy && o.Buff != null);
            var bonus = Assert.Single(outcomes, o => o.Sobre == sram && o.Buff != null);
            Assert.Equal(-100, enemy.Buffs.De(Agility, 5));
            Assert.Equal(100, sram.Buffs.De(Agility, 5));

            Assert.Equal(Hex("0a440a38086410feffffffffffffffff01189d012003320210083a014940b0d916621610ffffffffffffffffff0118ffffffffffffffffff0170ef6410e3809090c801189a01"),
                         AsItGoesOut(malus, sram, number: 157));
            Assert.Equal(Hex("0a3f0a34086410e3809090c801189e012003320210083a014940b0d916621610ffffffffffffffffff0118ffffffffffffffffff0170ef6410e3809090c8011877"),
                         AsItGoesOut(bonus, sram, number: 158));
        }

        /// <summary>
        /// Desecación's 320 in the Hipermago capture: 116 "-3 alcance" on the enemy (frame 5819)
        /// and 117 "+3 alcance" on the Hipermago (5823), uid 213583, a turn long.
        /// </summary>
        [Fact]
        public void Desecacion_steals_range()
        {
            var fight = AtRound(3);
            var hipermago = Player(879988113698, 0, 300);
            var enemy = Monster(-2, 301);
            fight.AddPlayer(hipermago); fight.AddOpponent(enemy);

            var steal = SpellEffects.De(Desecacion, 1).Single(e => e.EffectId == 320);
            var outcomes = EffectEngine.ResolveEffects(fight, hipermago, Desecacion, 1, enemy, EffectEngine.AlLanzar, 3,
                                                       new[] { steal }, aimedCell: 301);

            var malus = Assert.Single(outcomes, o => o.Sobre == enemy);
            var bonus = Assert.Single(outcomes, o => o.Sobre == hipermago);
            Assert.Equal((116, 3, 213583), (malus.Efecto.EffectId, malus.Efecto.DiceNum, malus.Efecto.EffectUid));
            Assert.Equal((117, 3, 213583), (bonus.Efecto.EffectId, bonus.Efecto.DiceNum, bonus.Efecto.EffectUid));
            Assert.Equal(-3, enemy.Buffs.De(Range, 3));
            Assert.Equal(3, hipermago.Buffs.De(Range, 3));
            Assert.Equal(4, malus.Buff.CaducaEnRonda);   // f6{f2=4}
        }

        /// <summary>
        /// A steal of points is dodged like a removal: what is dodged goes out as its jwe and
        /// nothing is handed over; what lands becomes a "-1 PM" on the target and a "+1 PM" in
        /// the caster's hand. The two rows are an inference from the other steals.
        /// </summary>
        [Fact]
        public void A_steal_of_MP_is_dodged_or_handed_over()
        {
            var fight = AtRound(1);
            var ocra = Player(10, 0, 300);
            var enemy = Monster(-1, 301);
            fight.AddPlayer(ocra); fight.AddOpponent(enemy);
            var row = new SpellEffect { EffectId = 77, EffectUid = 420294, DiceNum = 1, Duration = 1, TargetMask = "a,A", Triggers = "I" };

            try
            {
                EffectEngine.DadoDeRetirada = () => 0.0;   // every point lands
                var landed = EffectEngine.ResolveEffects(fight, ocra, 32436, 1, enemy, EffectEngine.AlLanzar, 1, new[] { row }, aimedCell: 301);
                Assert.Equal(169, landed.Single(o => o.Sobre == enemy).Efecto.EffectId);
                Assert.Equal(128, landed.Single(o => o.Sobre == ocra).Efecto.EffectId);
                Assert.Equal(1, ocra.Buffs.De(Fighter.CaracteristicaDePuntosDeMovimiento, 1));

                enemy.Buffs.Vaciar(); ocra.Buffs.Vaciar();
                EffectEngine.DadoDeRetirada = () => 0.99;  // every point dodged
                var dodged = EffectEngine.ResolveEffects(fight, ocra, 32436, 1, enemy, EffectEngine.AlLanzar, 1, new[] { row }, aimedCell: 301);
                var only = Assert.Single(dodged);
                Assert.Equal(1, only.PuntosEsquivados);
                Assert.Null(only.Buff);
                Assert.Empty(ocra.Buffs.Puestos);
            }
            finally
            {
                EffectEngine.DadoDeRetiradaPorDefecto();
            }
        }

        /// <summary>The jxm the fight sends for a row, the way AplicarEfectosAsync builds it.</summary>
        internal static byte[] AsItGoesOut(Outcome c, Fighter caster, int number)
        {
            var (category, boost) = DatabaseManager.EffectFamily(c.Efecto.EffectId);
            int family = FightProtocol.FamiliaDelEmbrujo(c.Efecto.EffectId, category, boost);
            return FightProtocol.BuildBuff(c.Sobre.Id, caster.Id, number, c.Efecto.EffectId, c.Efecto.EffectUid,
                                           c.Efecto.Value, c.Efecto.DiceNum, c.Efecto.DiceSide, c.HechizoOrigen,
                                           EffectEngine.AlLanzar, c.Buff.CaducaEnRonda, c.Efecto.Dispellable, family,
                                           c.NivelOrigen, c.Critico);
        }

        internal static byte[] Hex(string hex) => Convert.FromHexString(hex);

        /// <summary>
        /// The same top-level fields with the same contents, in whatever order: the jwe builder
        /// writes its detail before f14 while the real server writes the fields by number, which
        /// no protobuf reader tells apart.
        /// </summary>
        internal static void SameFields(byte[] expected, byte[] actual)
        {
            static string[] Of(byte[] frame) => ProtoMessage.Parse(frame).Fields
                .Select(f => $"{f.FieldNumber}/{f.WireType}/{f.VarIntValue}/{Convert.ToHexString(f.BytesValue)}")
                .OrderBy(s => s, StringComparer.Ordinal).ToArray();
            Assert.Equal(Of(expected), Of(actual));
        }
    }
}
