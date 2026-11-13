using System.Linq;
using System.Threading.Tasks;
using Jondo.Unity.Server;
using Jondo.Unity.Server.Handlers;
using Jondo.Unity.Server.Managers;
using Jondo.Unity.World.Fights;
using Jondo.Unity.World.Maps;
using Xunit;

namespace Jondo.Unity.Tests.Combat
{
    /// <summary>
    /// Dominio de Invocaciones: on from the fight's start (its initial spell 21976), it hands the
    /// owner's «maîtrisable» summons to him; cast, it lets them play on their own; cast again, it
    /// takes them back. Every summon was its owner's whatever the spell said.
    /// </summary>
    [Collection("MapManager")]
    public class SummonMasteryTests
    {
        private const int Initial = 21976, Mastery = 18646, Bouftou = 31116;

        [Fact]
        public async Task The_spell_switches_who_plays_the_bouftou()
        {
            if (MapManager.Maps.Count == 0) MapManager.Initialize();
            MobSpawnManager.EnsureMonsterData();
            var fight = new FightInstance(9_999_802, 0);
            var osamodas = new Fighter
            {
                Id = 7501, Breed = 2, TeamId = 0, CellId = 300, MaxHP = 2000, CurrentHP = 2000, Level = 200,
                MaxAP = 12, CurrentAP = 12, MaxMP = 6, CurrentMP = 6, MastersSummons = true,
            };
            var foe = new Fighter { Id = -1, TeamId = 1, CellId = 400, MaxHP = 2000, CurrentHP = 2000, Level = 200, IsMonster = true };
            fight.AddPlayer(osamodas);
            fight.AddMonster(foe);
            fight.StartFight();

            // Before the first turn, as the attitudes are.
            osamodas.Buffs.PonerActitud(Initial);
            await FightHandler.AplicarEfectosAsync(null, fight, osamodas, Initial, 1, osamodas,
                                                   EffectEngine.AlLanzar, osamodas.CellId, armar: false);

            int cell = MapGeometry.GetNeighbors(osamodas.CellId).First();
            await Cast(fight, osamodas, Bouftou, 3, cell);
            var bouftou = fight.Azul.Concat(fight.Rojo).Single(f => f != null && f.EsInvocado && f.IsAlive);
            Assert.True(bouftou.Dominable);
            Assert.False(bouftou.PlaysOnItsOwn, $"not his after the summon; states {string.Join(",", bouftou.Buffs.Estados)}");

            // Handed over after its birth: its bar goes to him before its turn, or he cannot play it.
            await using (var wire = await PortalTests.Wire.Open(osamodas.Id))
            {
                await FightHandler.HandOverWhatBecameHisAsync(fight);
                Assert.Contains(await wire.Drain(), f => f.Op == "jyy");
            }

            await Cast(fight, osamodas, Mastery, 1, osamodas.CellId);
            Assert.True(bouftou.PlaysOnItsOwn, $"still his after switching it off; owner states {string.Join(",", osamodas.Buffs.Estados)}");

            await Cast(fight, osamodas, Mastery, 1, osamodas.CellId);
            Assert.False(bouftou.PlaysOnItsOwn, $"not his again after switching it on; states {string.Join(",", bouftou.Buffs.Estados)}");
        }

        private static Task Cast(FightInstance fight, Fighter caster, int spell, int grade, int cell)
        {
            var target = fight.Azul.Concat(fight.Rojo).FirstOrDefault(f => f != null && f.IsAlive && f.CellId == cell);
            return FightHandler.CastEffectsAsync(null, fight, caster, spell, grade, target, cell, false,
                                                 EffectEngine.EfectosSorteados(spell, grade, false));
        }
    }
}
