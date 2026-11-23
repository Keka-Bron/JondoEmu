using System.Linq;
using System.Threading.Tasks;
using Jondo.Unity.Server.Handlers;
using Jondo.Unity.Server.Managers;
using Jondo.Unity.World.Fights;
using Xunit;

namespace Jondo.Unity.Tests.Combat
{
    /// <summary>
    /// Qu'Tan (1195) is invulnerable (state 56 from 1178). At the end of his turn he lays 3941
    /// under himself: an aura glyph (1091) whose grade 2 gives him state 168 while he stands in
    /// it. A portal (3502, behaviour 3949) that falls next to him casts 3950 (a cross of 1) ->
    /// 3948, which lifts state 56 only from a Qu'Tan in state 168. The aura was given only on
    /// stepping in or starting a turn in it, so Qu'Tan, already standing there, never had 168
    /// and could never be hurt.
    /// </summary>
    [Collection("MapManager")]
    public class QuTanAuraTests
    {
        private const int QuTan = 1195, Portal = 3502, Glyphs = 3941, PortalBehaviour = 3949;

        private static Fighter Monster(long id, int monsterId, int cell, int life) => new()
        {
            Id = id, Name = "M", IsMonster = true, CellId = cell, Level = 186, MaxHP = life,
            CurrentHP = life, TeamId = 1, MaxAP = 11, CurrentAP = 11, MaxMP = 4, CurrentMP = 4,
            MonsterId = monsterId,
        };

        [Fact]
        public async Task A_portal_falling_while_he_stands_on_his_aura_lifts_his_invulnerability()
        {
            if (!SpellEffects.De(Glyphs, 1).Any(row => row.EffectId == 1091)) return;   // no spell data
            var player = new Fighter
            {
                Id = 9_970_921, Name = "P", CellId = 400, Level = 200, MaxHP = 5000, CurrentHP = 5000,
                MaxAP = 12, CurrentAP = 12, MaxMP = 6, CurrentMP = 6, Breed = 8,
            };
            var quTan = Monster(-1, QuTan, 300, 15000);
            var portal = Monster(-2, Portal, 314, 2400);
            var fight = new FightInstance(QuTan, 990_001_195);
            fight.AddPlayer(player);
            fight.AddMonster(quTan);
            fight.AddMonster(portal);
            player.CellId = 400; quTan.CellId = 300; portal.CellId = 314;
            fight.StartFight();

            // His invulnerability, and the portal's behaviour, as the fight gives them.
            await FightHandler.AplicarEfectosAsync(null, fight, quTan, 1178, 1, quTan,
                                                   EffectEngine.AlLanzar, quTan.CellId);
            portal.Conducta = (PortalBehaviour, 2);
            await FightHandler.LanzarLaConductaAsync(null, fight, portal);
            Assert.True(quTan.Buffs.TieneEstado(56));
            Assert.Contains(portal.Buffs.ActiveSpells, s => s.Hechizo == PortalBehaviour);

            // His turn-end glyphs, laid under him.
            await FightHandler.AplicarEfectosAsync(null, fight, quTan, Glyphs, 1, quTan,
                                                   EffectEngine.AlLanzar, quTan.CellId);
            Assert.True(quTan.Buffs.TieneEstado(168));

            await FightHandler.TestKillAsync(null, fight, portal, player);
            Assert.False(portal.IsAlive);
            Assert.False(quTan.Buffs.TieneEstado(56));
        }
    }
}
