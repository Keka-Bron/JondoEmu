using System.Linq;
using Jondo.Unity.Server.Managers;
using Jondo.Unity.World.Fights;
using Xunit;
using Xunit.Abstractions;

namespace Jondo.Unity.Tests.Combat
{
    /// <summary>
    /// The Xelor's syncro (3958) and its Fin de los Tiempos (13270). In the game's own tooltip:
    /// "-200% de daños finales" from the start, and "aumenta los daños finales y cura del todo a la
    /// syncro cuando un xelor genera un telefrag. Los efectos solo se aplican una vez por turno y
    /// por hechizo de generación de telefrag."
    ///
    /// The once-per-spell rule is written in states: the Xelor's passive Syncro (24941) gives him
    /// one per telefrag spell (3712-3731, 6115-6118), the syncro's grade 1 hands grade 2 to him
    /// (1017) and grade 2 copies onto the syncro those he holds ("950 on a,F3958,*E3712"); each
    /// telefrag spell's child then looks for a syncro still holding its state (Teletransportación's
    /// 13272: "2792 on a,F3958,e306,E3712").
    /// </summary>
    public class SyncroTests
    {
        private const int FinDeLosTiempos = 13270, Pasivo = 24941, SyncroTemplate = 3958;
        private readonly ITestOutputHelper _out;

        public SyncroTests(ITestOutputHelper output) => _out = output;

        private static Fighter Xelor(FightInstance fight)
        {
            var xelor = new Fighter { Id = 10, TeamId = 0, CellId = 300, MaxHP = 1000, CurrentHP = 1000, MaxAP = 7, CurrentAP = 7, Breed = 5 };
            fight.AddPlayer(xelor);
            return xelor;
        }

        private static Fighter Syncro(FightInstance fight, Fighter owner)
        {
            var syncro = new Fighter
            {
                Id = -2, TeamId = owner.TeamId, CellId = 302, MaxHP = 1260, CurrentHP = 1260,
                IsMonster = true, MonsterId = SyncroTemplate, GradeIndex = 1, Invocador = owner.Id, SummonCost = 0,
            };
            fight.AddPlayer(syncro);
            return syncro;
        }

        [Fact]
        public void A_syncro_born_takes_the_telefrag_states_its_xelor_holds()
        {
            var fight = new FightInstance(1, 1);
            var xelor = Xelor(fight);
            fight.AddMonster(new Fighter { Id = -50, TeamId = 1, CellId = 200, MaxHP = 500, CurrentHP = 500 });

            // The passive, as the fight's start casts it.
            var passive = EffectEngine.Resolver(fight, xelor, Pasivo, 5, xelor, EffectEngine.AlLanzar, fight.RoundNumber);
            Assert.True(xelor.Buffs.TieneEstado(3712), "the passive did not give the Xelor 3712");

            var syncro = Syncro(fight, xelor);
            // As the fight casts a summon's own spell at its birth (FightHandler.InvocarAsync): not armed.
            var birth = EffectEngine.Resolver(fight, syncro, FinDeLosTiempos, 1, syncro, EffectEngine.AlLanzar, fight.RoundNumber,
                                              celdaApuntada: syncro.CellId, armar: false);
            foreach (var o in birth)
                _out.WriteLine($"{o.HechizoOrigen}/{o.NivelOrigen} effect {o.Efecto.EffectId} on {o.Sobre?.Id} by {o.Caster?.Id} state {o.Buff?.Estado}");

            Assert.True(syncro.Buffs.TieneEstado(3712), "the syncro was not given its Xelor's telefrag states");
        }

        [Fact]
        public void A_telefrag_of_teletransportacion_grows_the_syncro_once()
        {
            var fight = new FightInstance(1, 1);
            var xelor = Xelor(fight);
            var foe = new Fighter { Id = -50, TeamId = 1, CellId = 200, MaxHP = 500, CurrentHP = 500 };
            fight.AddMonster(foe);
            EffectEngine.Resolver(fight, xelor, Pasivo, 5, xelor, EffectEngine.AlLanzar, fight.RoundNumber);
            var syncro = Syncro(fight, xelor);
            EffectEngine.Resolver(fight, syncro, FinDeLosTiempos, 1, syncro, EffectEngine.AlLanzar, fight.RoundNumber,
                                  celdaApuntada: syncro.CellId, armar: false);
            int before = syncro.Buffs.De(107, fight.RoundNumber);

            // Teletransportación's telefrag child, cast by the Xelor on the one swapped (its 2160 on T).
            var grown = EffectEngine.Resolver(fight, xelor, 13272, 1, foe, EffectEngine.AlLanzar, fight.RoundNumber,
                                              celdaApuntada: foe.CellId);
            foreach (var o in grown)
                _out.WriteLine($"{o.HechizoOrigen}/{o.NivelOrigen} effect {o.Efecto.EffectId} on {o.Sobre?.Id} by {o.Caster?.Id} state {o.Buff?.Estado} value {o.Buff?.Cuanto}");
            _out.WriteLine($"final damage {before} -> {syncro.Buffs.De(107, fight.RoundNumber)}; 306: {syncro.Buffs.TieneEstado(306)}; 3712: {syncro.Buffs.TieneEstado(3712)}");
            Assert.Equal(before + 200, syncro.Buffs.De(107, fight.RoundNumber));
        }
    }
}
