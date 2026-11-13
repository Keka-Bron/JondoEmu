using System.Linq;
using System.Threading.Tasks;
using Jondo.Unity.Server.Handlers;
using Jondo.Unity.Server.Network;
using Jondo.Unity.World.Fights;
using Xunit;

namespace Jondo.Unity.Tests.Combat
{
    /// <summary>
    /// A glyph whose time is up goes at its owner's turn start, its jwe 310 inside an action
    /// sequence. Sent loose, between the expired buffs' jwi and the next jto, the 3.6 client
    /// swallowed it: a Feca's glyphs stayed painted on the board after they had dropped.
    /// </summary>
    public class GlyphExpiryTests
    {
        private const long Feca = 54000000031;

        [Fact]
        public async Task An_expired_glyph_goes_inside_an_action_sequence()
        {
            var feca = new Fighter { Id = Feca, Breed = 1, CellId = 300, Level = 60, CurrentHP = 500, MaxHP = 500, MaxAP = 6, CurrentAP = 6, MaxMP = 3, CurrentMP = 3 };
            var fight = new FightInstance(98766, 0);
            fight.AddPlayer(feca);
            fight.Glifos.Add(new Glifo(Feca, new[] { 314, 315 }, 12996, 1, 0xCA1330, fight.RoundNumber, "A",
                                       Disparo.AlEmpezarElTurno) { Id = 1 });

            await using var wire = await PortalTests.Wire.Open(Feca);
            await FightHandler.QuitarLosGlifosCaducadosAsync(fight, feca);
            var frames = (await wire.Drain()).Select(f => f.Op).ToList();

            Assert.Empty(fight.Glifos);
            int gone = frames.IndexOf("jwe");
            Assert.True(gone > 0, string.Join(" ", frames));
            Assert.Equal("jto", frames[gone - 1]);
            Assert.Equal("jwi", frames[gone + 1]);
        }
    }
}
