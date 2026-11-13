using System.Linq;
using Jondo.Unity.Server;
using Jondo.Unity.Server.Handlers;
using Jondo.Unity.Server.Managers;
using Jondo.Unity.World.Fights;
using Jondo.Unity.World.Maps;
using Xunit;

namespace Jondo.Unity.Tests.Combat
{
    /// <summary>
    /// The Giant Kralamoure hit in water summons the Quaternary Tentacle on his next turn
    /// (spell 1110, «HS=36», four to twelve cells away). The cells around him are not walkable,
    /// and a summon with no rows was only ever aimed at its caster's neighbours: he never summoned.
    /// </summary>
    [Collection("MapManager")]
    public class KralamoureTentacleTests
    {
        private const long BossRoom = 26740736;
        private const int WaterState = 36, Quaternary = 1110, Kracheau = 1105;

        [Fact]
        public void Hit_in_water_he_summons_the_quaternary_tentacle_four_cells_away_or_more()
        {
            if (MapManager.Maps.Count == 0) MapManager.Initialize();
            if (!MapManager.WalkableCells.ContainsKey(BossRoom)) return;   // no world data

            var fight = new FightInstance(9_999_777, BossRoom, BossRoom);
            var kralamoure = new Fighter
            {
                Id = -1, TeamId = 1, CellId = 344, IsMonster = true, MonsterId = 423, Level = 200,
                MaxHP = 5800, CurrentHP = 5800, MaxAP = 18, CurrentAP = 18,
            };
            foreach (int spell in new[] { 1105, 1279, 1108, 1107, 1110, 1109, 1106, 1103 })
            {
                kralamoure.SpellIds.Add(spell);
                kralamoure.SpellGrades[spell] = 1;
            }
            var player = new Fighter
            {
                Id = 10, TeamId = 0, CellId = 479, MaxHP = 5000, CurrentHP = 5000, Level = 200,
                MaxAP = 12, CurrentAP = 12, MaxMP = 6, CurrentMP = 6,
            };
            fight.AddPlayer(player); fight.AddOpponent(kralamoure);
            kralamoure.Buffs.PonerEstado(WaterState);

            var usable = FightHandler.TacticsOf(kralamoure).Where(s => SpellCriteria.Allows(kralamoure, s.Id, s.Grade)).ToList();
            if (usable.All(s => s.Id != Quaternary)) return;   // no spell data
            var options = MonsterTactics.Options(FightHandler.BoardOf(fight), kralamoure, usable);

            var summons = options.Where(o => o.Spell.Id == Quaternary).ToList();
            Assert.NotEmpty(summons);
            Assert.All(summons, o => Assert.InRange(MapGeometry.Distance(344, o.TargetCell), 4, 12));
            // And before his Kracheau: the tentacle is the point of the turn.
            Assert.True(summons.Max(o => o.Score) > options.Where(o => o.Spell.Id == Kracheau).Max(o => o.Score));
        }

        /// <summary>
        /// The Primary Tentacle with its five AP eats the summon at its contact: Kraken Primario,
        /// 900 of air steal and a kill, which passes the relay on to the Secondary. Its damage is a
        /// steal (93), which the spell data left at the default 5 to 10: it preferred its own
        /// +1 AP every turn and never struck.
        /// </summary>
        [Fact]
        public void The_primary_tentacle_eats_the_summon_at_its_contact()
        {
            if (MapManager.Maps.Count == 0) MapManager.Initialize();
            if (!MapManager.WalkableCells.ContainsKey(BossRoom)) return;   // no world data

            var fight = new FightInstance(9_999_778, BossRoom, BossRoom);
            var player = new Fighter { Id = 10, TeamId = 0, CellId = 460, MaxHP = 5000, CurrentHP = 5000, Level = 200, MaxAP = 12, CurrentAP = 12, MaxMP = 6, CurrentMP = 6 };
            var turret = new Fighter { Id = -6, TeamId = 0, CellId = 486, MaxHP = 1890, CurrentHP = 1890, Level = 200, IsMonster = true, MonsterId = 5833, Invocador = 10 };
            var primary = new Fighter
            {
                Id = -5, TeamId = 1, CellId = 499, MaxHP = 230, CurrentHP = 230, Level = 100, IsMonster = true,
                MonsterId = 424, MaxAP = 4, CurrentAP = 5, MaxMP = 2, CurrentMP = 2, Invocador = -1,
            };
            foreach (int spell in new[] { MotivacionNatural, KrakenPrimario })
            {
                primary.SpellIds.Add(spell);
                primary.SpellGrades[spell] = 5;
            }
            fight.AddPlayer(player); fight.AddPlayer(turret); fight.AddOpponent(primary);

            var spells = FightHandler.TacticsOf(primary);
            if (spells.All(s => s.Id != KrakenPrimario)) return;   // no spell data
            var next = MonsterTactics.Next(FightHandler.BoardOf(fight), primary, spells);

            Assert.NotNull(next);
            Assert.Equal(KrakenPrimario, next!.Spell.Id);
            Assert.Equal(turret.Id, next.Target.Id);
        }

        private const int MotivacionNatural = 261, KrakenPrimario = 1096;
    }
}
