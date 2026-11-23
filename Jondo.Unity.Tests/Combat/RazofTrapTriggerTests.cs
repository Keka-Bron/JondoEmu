using System.Linq;
using System.Threading.Tasks;
using Jondo.Unity.Server.Handlers;
using Jondo.Unity.Server.Managers;
using Jondo.Unity.World.Fights;
using Xunit;

namespace Jondo.Unity.Tests.Combat
{
    /// <summary>
    /// Count Razof's camp: a trap laid by one of his hunters (8508, a 400 of 8518) and the
    /// Bobalicor's Busca (8504), whose second grade is a 1025, "Activa las trampas", on C3. It
    /// was never read, and the traps round the one it hunted stayed where they were.
    /// </summary>
    [Collection("MapManager")]
    public class RazofTrapTriggerTests
    {
        private static Fighter Monster(long id, int cell) => new()
        {
            Id = id, Name = "M", IsMonster = true, Level = 190, MaxHP = 9000, CurrentHP = 9000, TeamId = 1,
            MaxAP = 10, CurrentAP = 10, MaxMP = 4, CurrentMP = 4, MonsterId = 4803, CellId = cell,
        };

        private static async Task<(FightInstance Fight, Fighter Player, Fighter Bobalicor)> TrapUnder(int trapCell)
        {
            var player = new Fighter
            {
                Id = 9_978_001, Name = "P", CellId = 300, Level = 200, MaxHP = 5000, CurrentHP = 5000,
                MaxAP = 12, CurrentAP = 12, MaxMP = 6, CurrentMP = 6, Breed = 8,
            };
            var hunter = Monster(-1, 100);
            var bobalicor = Monster(-2, 120);
            var fight = new FightInstance(4803, 990_004_804);
            fight.AddPlayer(player); fight.AddMonster(hunter); fight.AddMonster(bobalicor);
            player.CellId = 300; hunter.CellId = 100; bobalicor.CellId = 120;
            fight.StartFight();
            await FightHandler.AplicarEfectosAsync(null, fight, hunter, 8508, 1, null, EffectEngine.AlLanzar, trapCell);
            Assert.Single(fight.Glifos, g => g.Tipo == EffectEngine.ColocaUnaTrampa);
            return (fight, player, bobalicor);
        }

        [Fact]
        public async Task Busca_sets_off_the_trap_next_to_the_one_it_hunts()
        {
            if (!SpellEffects.De(8504, 2).Any(row => row.EffectId == EffectEngine.ActivaLasTrampas)) return;   // no spell data
            var (fight, player, bobalicor) = await TrapUnder(300);
            await FightHandler.AplicarEfectosAsync(null, fight, bobalicor, 8504, 2, player, EffectEngine.AlLanzar, player.CellId);
            Assert.DoesNotContain(fight.Glifos, g => g.Tipo == EffectEngine.ColocaUnaTrampa);
            Assert.True(player.Buffs.TieneEstado(522));
        }

        [Fact]
        public async Task A_trap_out_of_its_zone_stays()
        {
            if (!SpellEffects.De(8504, 2).Any(row => row.EffectId == EffectEngine.ActivaLasTrampas)) return;   // no spell data
            var (fight, player, bobalicor) = await TrapUnder(500);
            await FightHandler.AplicarEfectosAsync(null, fight, bobalicor, 8504, 2, player, EffectEngine.AlLanzar, player.CellId);
            Assert.Single(fight.Glifos, g => g.Tipo == EffectEngine.ColocaUnaTrampa);
            Assert.False(player.Buffs.TieneEstado(522));
        }
    }
}
