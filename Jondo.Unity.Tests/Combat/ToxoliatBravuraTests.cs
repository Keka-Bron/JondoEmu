using System.Linq;
using System.Threading.Tasks;
using Jondo.Unity.Server.Handlers;
using Jondo.Unity.Server.Managers;
using Jondo.Unity.World.Fights;
using Xunit;

namespace Jondo.Unity.Tests.Combat
{
    /// <summary>
    /// Toxoliat's Bravura (23880) holds "793 on A under TB": «À chaque début de tour, les personnages
    /// posent un glyphe en zone croix de taille 1 autour d'eux. Ce glyphe OS en début et en fin de
    /// tour» (Dofus pour les noobs). Each player lays it at the start of his own turn and has the
    /// turn to walk out; laid at the boss's turn start, it killed every player at his next one.
    /// </summary>
    [Collection("MapManager")]
    public class ToxoliatBravuraTests
    {
        private const int Toxoliat = 3752, Bravura = 23880;

        private static (FightInstance Fight, Fighter Player, Fighter Boss) Build()
        {
            var player = new Fighter
            {
                Id = 9_973_752, Name = "P", CellId = 250, Level = 200, MaxHP = 5000, CurrentHP = 5000,
                MaxAP = 12, CurrentAP = 12, MaxMP = 6, CurrentMP = 6, Breed = 8,
            };
            var boss = new Fighter
            {
                Id = -1, Name = "T", IsMonster = true, Level = 180, MaxHP = 8000, CurrentHP = 8000, TeamId = 1,
                MaxAP = 10, CurrentAP = 10, MaxMP = 4, CurrentMP = 4, MonsterId = Toxoliat,
            };
            var fight = new FightInstance(Toxoliat, 990_013_752);
            fight.AddPlayer(player);
            fight.AddMonster(boss);
            player.CellId = 250; boss.CellId = 400;
            fight.StartFight();
            boss.Buffs.PonerActitud(Bravura);
            return (fight, player, boss);
        }

        [Fact]
        public async Task The_player_lays_the_glyph_at_his_own_turn_start_and_survives_it()
        {
            if (!SpellEffects.De(Bravura, 1).Any(row => row.EffectId == 793)) return;   // no spell data
            var (fight, player, boss) = Build();

            await FightHandler.DispararAsync(null, fight, boss, EffectEngine.AlEmpezarElTurno);
            Assert.DoesNotContain(fight.Glifos, g => g.Hechizo == Bravura);

            await FightHandler.DispararLosGlifosAsync(null, fight, player, alPisar: false);
            await FightHandler.DispararAsync(null, fight, player, EffectEngine.AlEmpezarElTurno);
            Assert.True(player.IsAlive);
            var glyphs = fight.Glifos.Where(g => g.Hechizo == Bravura).ToList();
            Assert.NotEmpty(glyphs);
            Assert.All(glyphs, g => Assert.Equal(player.Id, g.Dueno));
            Assert.All(glyphs, g => Assert.Contains(250, g.Casillas));

            // He walks out of it before his turn ends.
            player.CellId = 100;
            await FightHandler.TurnEndGlyphsAsync(null, fight, player);
            Assert.True(player.IsAlive);
        }

        [Fact]
        public async Task Ending_the_turn_inside_his_own_glyph_kills_him()
        {
            if (!SpellEffects.De(Bravura, 1).Any(row => row.EffectId == 793)) return;   // no spell data
            var (fight, player, _) = Build();

            await FightHandler.DispararAsync(null, fight, player, EffectEngine.AlEmpezarElTurno);
            Assert.True(player.IsAlive);
            await FightHandler.TurnEndGlyphsAsync(null, fight, player);
            Assert.False(player.IsAlive);
        }
    }
}
