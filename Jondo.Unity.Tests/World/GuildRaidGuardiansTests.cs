using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Jondo.Unity.Server;
using Jondo.Unity.Server.Handlers;
using Jondo.Unity.Server.Managers;
using Jondo.Unity.World.Content;
using Jondo.Unity.World.Fights;
using Microsoft.Data.Sqlite;
using Xunit;

namespace Jondo.Unity.Tests.World
{
    /// <summary>
    /// The Santuario's guardians: their groups, the four-player cap of their fights, and the
    /// Centinela's colour, which its obelisks and spells follow by the raid's.
    /// </summary>
    [Collection("guild raids")]
    public class GuildRaidGuardiansTests : IDisposable
    {
        private readonly string _file;

        /// <summary>A blow of any damage: the freed guardians' "taken x%" rows are under "D".</summary>
        private static readonly string[] AnyBlow = { "D" };

        public GuildRaidGuardiansTests()
        {
            _file = Path.Combine(Path.GetTempPath(), $"jondo-raidguard-{Guid.NewGuid():N}.db");
            GuildStore.ConnectionStringOverride = $"Data Source={_file}";
            GuildRaidBoard.Forget();
            GuildRaidManager.Forget();
        }

        public void Dispose()
        {
            GuildRaidManager.Forget();
            GuildRaidBoard.Forget();
            GuildStore.ConnectionStringOverride = null;
            SqliteConnection.ClearAllPools();
            try { File.Delete(_file); } catch (IOException) { }
        }

        [Fact]
        public void Each_guardian_comes_with_one_of_each_kind()
        {
            Assert.Equal(new[] { 8318, 8285, 8286, 8287, 8288 }, GuildRaidGuardians.GroupOf(8318));
            Assert.Equal(9, GuildRaidGuardians.GroupOf(GuildRaidGuardians.Centinela).Count);     // and the four obelisks
            Assert.Contains(8320, GuildRaidGuardians.GroupOf(GuildRaidGuardians.Centinela));
        }

        /// <summary>The colour versions by their admin names; any other spell is none.</summary>
        [Fact]
        public void The_colour_versions_name_their_colour()
        {
            Assert.Equal(1, GuildRaidGuardians.ColourOf(32043));      // Check color == 1
            Assert.Equal(4, GuildRaidGuardians.ColourOf(32055));      // color == 4 / terre
            Assert.Equal(3, GuildRaidGuardians.ColourOf(32059));      // Cromago, color == 3 / eau
            Assert.Equal(0, GuildRaidGuardians.ColourOf(32231));
        }

        [Fact]
        public void A_guardian_s_fight_takes_four_a_side()
        {
            var fight = new FightInstance(1, 1);
            fight.AddMonster(new Fighter { Id = -1, TeamId = 1, MaxHP = 100, CurrentHP = 100, IsMonster = true, MonsterId = 8319 });
            GuildRaidGuardians.OnFightCreated(fight);
            Assert.Equal(4, FightHandler.CapOf(fight));

            var ordinary = new FightInstance(2, 1);
            ordinary.AddMonster(new Fighter { Id = -1, TeamId = 1, MaxHP = 100, CurrentHP = 100, IsMonster = true, MonsterId = 8285 });
            GuildRaidGuardians.OnFightCreated(ordinary);
            Assert.Equal(FightHandler.MaxPeoplePerTeam, FightHandler.CapOf(ordinary));
        }

        /// <summary>The scene's letters: Def and Atq are the sides, and with them "a" and "A" add nobody.</summary>
        [Fact]
        public void The_scene_s_masks_name_the_sides()
        {
            var vigilante = new Fighter { Id = -1, TeamId = 1, IsMonster = true, MonsterId = GuildRaidGuardians.Vigilante };
            var companion = new Fighter { Id = -2, TeamId = 1, IsMonster = true, MonsterId = 8285 };
            var player = new Fighter { Id = 7001, TeamId = 0 };

            Assert.True(EffectEngine.CumpleLaMascara(vigilante, companion, "Def,A,F8285"));
            Assert.False(EffectEngine.CumpleLaMascara(vigilante, player, "Def,A,F8285"));
            Assert.True(EffectEngine.CumpleLaMascara(vigilante, player, "Atq,A"));
            Assert.False(EffectEngine.CumpleLaMascara(vigilante, companion, "Atq,A"));
            Assert.True(EffectEngine.CumpleLaMascara(vigilante, companion, "a,A,Atq,Def"));
            Assert.True(EffectEngine.CumpleLaMascara(vigilante, player, "a,A,Atq,Def"));
        }

        /// <summary>
        /// Any companion and any glyph is the right one: the "== N" and "&lt; 5" checks go through,
        /// once a fight each; the "!= N" and "&gt; 4" ones never, so the fight is never lost to them.
        /// </summary>
        [Fact]
        public void Any_companion_and_any_glyph_is_the_right_one()
        {
            Assert.True(GuildRaidGuardians.CheckVerdict(32101));      // mob == 2
            Assert.False(GuildRaidGuardians.CheckVerdict(32105));     // mob != 2
            Assert.True(GuildRaidGuardians.CheckVerdict(32082));      // o_hint_8 < 5
            Assert.False(GuildRaidGuardians.CheckVerdict(32089));     // o_hint_8 > 4
            Assert.Null(GuildRaidGuardians.CheckVerdict(32043));      // a colour check

            var fight = new FightInstance(31, 1);
            Assert.True(ChosenCasts.Allows(fight, 32066, 32067));
            Assert.False(ChosenCasts.Allows(fight, 32066, 32067));    // the same glyph, a second attacker
            Assert.False(ChosenCasts.Allows(fight, 32066, 32068));
            Assert.True(ChosenCasts.Allows(fight, 32099, 32100));
            Assert.False(ChosenCasts.Allows(fight, 32099, 32104));
            GuildRaidGuardians.Forget(fight.FightId);
            Assert.True(ChosenCasts.Allows(fight, 32066, 32067));
            GuildRaidGuardians.Forget(fight.FightId);
        }

        /// <summary>
        /// The Vigilante's own Check Mob: the first companion to fall frees it -- no longer
        /// invulnerable, 20 % more damage taken and its companions 10 % more -- and takes the
        /// check away, so a second fall changes nothing.
        /// </summary>
        [Fact]
        public async Task The_first_companion_to_fall_frees_the_vigilante()
        {
            var fight = new FightInstance(21, 1);
            fight.AddPlayer(new Fighter { Id = 7001, TeamId = 0, CellId = 200, MaxHP = 5000, CurrentHP = 5000 });
            var vigilante = new Fighter { Id = -1, TeamId = 1, CellId = 300, MaxHP = 20000, CurrentHP = 20000, IsMonster = true,
                                          MonsterId = GuildRaidGuardians.Vigilante };
            vigilante.Buffs.PonerEstado(GuildRaidGuardians.Invulnerable);
            var fallen = new Fighter { Id = -2, TeamId = 1, CellId = 301, MaxHP = 6000, CurrentHP = 6000, IsMonster = true, MonsterId = 8287 };
            var other = new Fighter { Id = -3, TeamId = 1, CellId = 302, MaxHP = 6000, CurrentHP = 6000, IsMonster = true, MonsterId = 8285 };
            fight.AddMonster(vigilante);
            fight.AddMonster(fallen);
            fight.AddMonster(other);
            GuildRaidGuardians.OnFightCreated(fight);
            Assert.Same(vigilante, fight.SceneStandIn);
            Assert.Contains(GuildRaidGuardians.CheckMob, vigilante.Buffs.Actitudes);

            try
            {
                fallen.CurrentHP = 0;
                await FightHandler.CaenSusInvocadosAsync(null, fight, fallen);
                Assert.False(vigilante.Buffs.TieneEstado(GuildRaidGuardians.Invulnerable));
                Assert.Equal(120, vigilante.Buffs.Multiplicador(1163, 1, AnyBlow));
                Assert.Equal(110, other.Buffs.Multiplicador(1163, 1, AnyBlow));
                Assert.DoesNotContain(GuildRaidGuardians.CheckMob, vigilante.Buffs.Actitudes);

                other.CurrentHP = 0;
                await FightHandler.CaenSusInvocadosAsync(null, fight, other);
                Assert.Equal(120, vigilante.Buffs.Multiplicador(1163, 1, AnyBlow));
            }
            finally
            {
                GuildRaidGuardians.Forget(fight.FightId);
            }
        }

        /// <summary>
        /// The Guardián lays its eight glyphs; a step on one is one step of its counter and that
        /// glyph gone, however many players there are; the fourth frees it and takes the rest away.
        /// </summary>
        [Fact]
        public async Task The_fourth_glyph_frees_the_guardian()
        {
            var fight = new FightInstance(22, 1);
            var first = new Fighter { Id = 7001, TeamId = 0, CellId = 200, MaxHP = 5000, CurrentHP = 5000 };
            fight.AddPlayer(first);
            fight.AddPlayer(new Fighter { Id = 7002, TeamId = 0, CellId = 201, MaxHP = 5000, CurrentHP = 5000 });
            var guardian = new Fighter { Id = -1, TeamId = 1, CellId = 300, MaxHP = 20000, CurrentHP = 20000, IsMonster = true,
                                         MonsterId = GuildRaidGuardians.Guardian };
            guardian.Buffs.PonerEstado(GuildRaidGuardians.Invulnerable);
            fight.AddMonster(guardian);
            GuildRaidGuardians.OnFightCreated(fight);
            Assert.Same(guardian, fight.SceneStandIn);

            try
            {
                int[] cells = { 100, 104, 108, 112, 160, 164, 168, 172 };
                for (int i = 0; i < cells.Length; i++)
                    await FightHandler.CastAtAsync(null, fight, guardian, GuildRaidGuardians.GlyphSpells[i], 1, cells[i]);
                Assert.Equal(8, fight.Glifos.Count(g => g.Dueno == guardian.Id));

                int[] counter = { 6679, 6680, 6681, 6682 };
                for (int step = 0; step < 4; step++)
                {
                    var glyph = fight.Glifos.First(g => g.Dueno == guardian.Id);
                    first.CellId = glyph.Casillas.First();
                    await FightHandler.AplicarEfectosAsync(null, fight, guardian, glyph.Hechizo, glyph.Grado, first,
                                                           EffectEngine.AlLanzar, first.CellId);
                    Assert.True(guardian.Buffs.TieneEstado(counter[step]), "step " + (step + 1));
                    Assert.DoesNotContain(glyph, fight.Glifos);
                    if (step < 3) Assert.True(guardian.Buffs.TieneEstado(GuildRaidGuardians.Invulnerable));
                }
                Assert.False(guardian.Buffs.TieneEstado(GuildRaidGuardians.Invulnerable));
                Assert.DoesNotContain(fight.Glifos, g => g.Dueno == guardian.Id);
            }
            finally
            {
                GuildRaidGuardians.Forget(fight.FightId);
            }
        }

        [Fact]
        public void The_glyphs_spread_over_free_cells()
        {
            var walkable = Enumerable.Range(0, 560).ToList();
            var taken = new System.Collections.Generic.HashSet<int> { 0, 1, 2, 300 };
            var cells = GuildRaidGuardians.GlyphCells(walkable, taken, 8, new Random(5));
            Assert.Equal(8, cells.Distinct().Count());
            Assert.DoesNotContain(cells, taken.Contains);
            foreach (int a in cells)
                foreach (int b in cells.Where(b => b != a))
                    Assert.True(Jondo.Unity.World.Maps.MapGeometry.Distance(a, b) >= GuildRaidGuardians.GlyphSpacing);
        }

        /// <summary>
        /// The raid's colour picks the versions: the Centinela's Caleidohueso hits water at colour 3,
        /// through the engine, and only the obelisk check of colour 3 goes through.
        /// </summary>
        [Fact]
        public async Task The_raid_s_colour_picks_the_versions()
        {
            var guild = GuildStore.Create(7001, "Jondo", 165, 8, 16744448, 9476018);
            GuildStore.SpendGuildKamas(guild.Id, -1000);
            var raid = await GuildRaidManager.LaunchAsync(GuildRaidBoard.Purchase(7001, Raids.EternalGardens).Raid);
            raid.Add(7001);
            Assert.InRange(raid.Get(GuildRaidGuardians.ColourVariable), 1, 4);
            raid.Set(GuildRaidGuardians.ColourVariable, 3);

            var fight = new FightInstance(9, 1);
            fight.AddPlayer(new Fighter { Id = 7001, TeamId = 0, MaxHP = 100, CurrentHP = 100 });
            var centinela = new Fighter { Id = -1, TeamId = 1, CellId = 300, MaxHP = 15000, CurrentHP = 15000, IsMonster = true,
                                          MonsterId = GuildRaidGuardians.Centinela };
            fight.AddMonster(centinela);

            Assert.True(ChosenCasts.Allows(fight, 32040, 32045));
            Assert.False(ChosenCasts.Allows(fight, 32040, 32043));

            var outcomes = EffectEngine.Resolver(fight, centinela, 32051, 1, centinela, EffectEngine.AlLanzar, 1,
                                                 celdaApuntada: centinela.CellId);
            var versions = outcomes.Where(o => GuildRaidGuardians.ColourOf(o.HechizoEncadenado) != 0).ToList();
            Assert.Equal(32054, Assert.Single(versions).HechizoEncadenado);
        }
    }
}
