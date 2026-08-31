using Jondo.Unity.Server.Managers;
using Jondo.Unity.World.Fights;
using Xunit;

namespace Jondo.Unity.Tests.Combat
{
    /// <summary>Régressions de la mécanique mesurée dans les captures Ouginak.</summary>
    public class OuginakRageTests
    {
        private const int RageManager = 13745;
        private const int Molosse = 13756;
        private const int Apaisement = 13769;
        private const int RageOne = 513;
        private const int RageTwo = 514;
        private const int RagePresent = 515;
        private const int BestialForm = 517;
        private const int FinalDamage = 107;
        private const int BestialAppearance = 1260;

        private static (FightInstance Fight, Fighter Ouginak) Combat()
        {
            var fight = new FightInstance(1, 1);
            var ouginak = new Fighter { Id = 1, TeamId = 0, CellId = 100, CurrentHP = 1000 };
            fight.Azul.Add(ouginak);
            return (fight, ouginak);
        }

        private static void Gain(FightInstance fight, Fighter ouginak, int round = 0)
            => EffectEngine.Resolver(fight, ouginak, RageManager, 1, ouginak,
                                     EffectEngine.AlLanzar, round);

        private static void Lose(FightInstance fight, Fighter ouginak, int round = 0)
            => EffectEngine.Resolver(fight, ouginak, RageManager, 2, ouginak,
                                     EffectEngine.AlLanzar, round);

        [Fact]
        public void One_gain_crosses_exactly_one_rage_threshold()
        {
            var (fight, ouginak) = Combat();

            Gain(fight, ouginak);

            Assert.True(ouginak.Buffs.TieneEstado(RageOne));
            Assert.True(ouginak.Buffs.TieneEstado(RagePresent));
            Assert.False(ouginak.Buffs.TieneEstado(RageTwo));
            Assert.False(ouginak.Buffs.TieneEstado(BestialForm));
        }

        [Fact]
        public void Molosse_reaches_the_rage_manager_through_effect_1160()
        {
            var (fight, ouginak) = Combat();
            var target = new Fighter { Id = 2, TeamId = 1, CellId = 101, CurrentHP = 1000 };
            fight.Rojo.Add(target);

            EffectEngine.Resolver(fight, ouginak, Molosse, 1, target,
                                  EffectEngine.AlLanzar, 0);

            Assert.True(ouginak.Buffs.TieneEstado(RageOne));
            Assert.False(ouginak.Buffs.TieneEstado(RageTwo));
        }

        [Fact]
        public void Third_gain_transforms_and_grants_twenty_percent_final_damage()
        {
            var (fight, ouginak) = Combat();

            Gain(fight, ouginak);
            Gain(fight, ouginak);
            var consequences = EffectEngine.Resolver(fight, ouginak, RageManager, 1, ouginak,
                                                     EffectEngine.AlLanzar, 0);

            Assert.True(ouginak.Buffs.TieneEstado(BestialForm));
            Assert.False(ouginak.Buffs.TieneEstado(RageOne));
            Assert.False(ouginak.Buffs.TieneEstado(RageTwo));
            Assert.False(ouginak.Buffs.TieneEstado(RagePresent));
            Assert.Equal(20, ouginak.Buffs.De(FinalDamage, 0));
            Assert.Equal(BestialAppearance, ouginak.Buffs.AparienciaEn(0));
            Assert.Contains(consequences, c => c.Apariencia == BestialAppearance);
        }

        [Fact]
        public void Rage_can_go_down_from_two_to_one_and_then_to_zero()
        {
            var (fight, ouginak) = Combat();
            Gain(fight, ouginak);
            Gain(fight, ouginak);

            Lose(fight, ouginak);
            Assert.True(ouginak.Buffs.TieneEstado(RageOne));
            Assert.False(ouginak.Buffs.TieneEstado(RageTwo));

            Lose(fight, ouginak);
            Assert.False(ouginak.Buffs.TieneEstado(RageOne));
            Assert.False(ouginak.Buffs.TieneEstado(RageTwo));
            Assert.False(ouginak.Buffs.TieneEstado(RagePresent));
        }

        [Fact]
        public void Removing_bestial_form_removes_its_state_and_final_damage()
        {
            var (fight, ouginak) = Combat();
            Gain(fight, ouginak);
            Gain(fight, ouginak);
            Gain(fight, ouginak);

            // Apaisement passe par l'effet 1160 vers 13782, qui retire les effets du sort de
            // transformation avec l'effet 406.
            EffectEngine.Resolver(fight, ouginak, Apaisement, 1, ouginak,
                                  EffectEngine.AlLanzar, 0);

            Assert.False(ouginak.Buffs.TieneEstado(BestialForm));
            Assert.Equal(0, ouginak.Buffs.De(FinalDamage, 0));
            Assert.Equal(0, ouginak.Buffs.AparienciaEn(0));
        }

        [Fact]
        public void Expiring_bestial_buffs_also_expires_the_state()
        {
            var (fight, ouginak) = Combat();
            Gain(fight, ouginak);
            Gain(fight, ouginak);
            Gain(fight, ouginak);

            ouginak.Buffs.Barrer(2);

            Assert.False(ouginak.Buffs.TieneEstado(BestialForm));
            Assert.Equal(0, ouginak.Buffs.De(FinalDamage, 2));
            Assert.Equal(0, ouginak.Buffs.AparienciaEn(2));
        }

        [Fact]
        public void Look_change_packet_matches_the_captured_combat_shape()
        {
            byte[] normal = Jondo.Unity.Server.Network.Pb.New()
                .Var(2, 3).Var(3, 1).Build();
            byte[] bestial = Jondo.Unity.Server.Network.FightProtocol.WithRootBones(normal, 9025);
            byte[] packet = Jondo.Unity.Server.Network.FightProtocol.BuildLookChanged(1, bestial);

            Assert.Equal("1801709501d2010908011a05100318c146",
                         Convert.ToHexString(packet).ToLowerInvariant());
        }

        [Fact]
        public void Bestial_appearance_resolves_to_the_captured_skeleton()
        {
            Cosmetics.Initialize();

            Assert.Equal(9025, Cosmetics.AppearanceBones(BestialAppearance));
        }
    }
}
