using Jondo.Unity.Server.Managers;
using Jondo.Unity.World.Fights;
using Xunit;

namespace Jondo.Unity.Tests.Combat
{
    /// <summary>Regressions of the mechanic measured in the Ouginak captures.</summary>
    public class OuginakRageTests
    {
        private const int RageManager = 13745;
        private const int BestialFormEnd = 13747;
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
        public void Molosse_still_grants_rage_when_the_damage_killed_its_target()
        {
            var (fight, ouginak) = Combat();
            // FightHandler applies the damage before it resolves the spell's other rows, so this
            // target stands for the monster Molosse has already killed.
            var target = new Fighter { Id = 2, TeamId = 1, CellId = 101, CurrentHP = 0 };
            fight.Rojo.Add(target);

            EffectEngine.Resolver(fight, ouginak, Molosse, 1, target,
                                  EffectEngine.AlLanzar, 0, celdaApuntada: target.CellId);

            Assert.True(ouginak.Buffs.TieneEstado(RageOne));
            Assert.True(ouginak.Buffs.TieneEstado(RagePresent));
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
        public void Bestial_form_cleanup_waits_until_the_end_of_the_following_turn()
        {
            var (fight, ouginak) = Combat();

            Gain(fight, ouginak, round: 2);
            Gain(fight, ouginak, round: 2);
            Gain(fight, ouginak, round: 2);

            Assert.True(ouginak.Buffs.TieneEstado(BestialForm));
            Assert.Equal(BestialAppearance, ouginak.Buffs.AparienciaEn(2));

            var waiting = Assert.Single(ouginak.Buffs.Puestos,
                buff => buff.Pendiente && buff.Dado == BestialFormEnd);
            Assert.Equal(3, waiting.EmpiezaEnRonda);
            Assert.Empty(EffectEngine.ActivateDuePending(fight, 2));

            var due = Assert.Single(EffectEngine.ActivateDuePending(fight, 3),
                activation => activation.Casts && activation.Waiting.Dado == BestialFormEnd);
            Assert.Equal(ouginak, due.Target);

            // The delayed spell only arms its TE row at the start of round 3. The form remains
            // throughout that turn, then this row removes Bestialidad when the turn ends.
            EffectEngine.Resolver(fight, ouginak, BestialFormEnd, 1, ouginak,
                                  EffectEngine.AlLanzar, 3);
            Assert.True(ouginak.Buffs.TieneEstado(BestialForm));

            EffectEngine.Resolver(fight, ouginak, BestialFormEnd, 1, ouginak,
                                  EffectEngine.AlAcabarElTurno, 3);
            Assert.False(ouginak.Buffs.TieneEstado(BestialForm));
            Assert.Equal(0, ouginak.Buffs.AparienciaEn(3));
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

            // Apaisement goes through effect 1160 towards 13782, which removes the effects of the
            // transformation spell with effect 406.
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
            // The catalogue loads itself on the first question asked of it.
            Assert.Equal(9025, Cosmetics.AppearanceBones(BestialAppearance));
        }
    }
}
