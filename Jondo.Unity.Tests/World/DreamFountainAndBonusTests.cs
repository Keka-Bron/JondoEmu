using System;
using System.Collections.Generic;
using System.Linq;
using Jondo.Unity.Server.Handlers;
using Jondo.Unity.Server.Managers;
using Jondo.Unity.Server.Network;
using Jondo.Unity.World.Fights;
using Xunit;

namespace Jondo.Unity.Tests.World
{
    /// <summary>
    /// The dream's bonuses in a fight, the fountains on their own maps, the loot table and the
    /// positions against the capture's bytes, and the Rey Gob with a face.
    /// </summary>
    [Collection("MapManager")]
    public class DreamFountainAndBonusTests
    {
        private static Dreams.Sueno New(int difficulty = 4)
        {
            Interactives.Initialize();
            Dreams.OlvidarTodo();
            var dream = Dreams.Crear(1, "Prueba", 200, difficulty, 100, 200);
            Dreams.Enter(dream, 0, out _);
            return dream;
        }

        /// <summary>
        /// The same effect twice is one line with the sum -- 1 MP and 1 MP are 2 MP, as the
        /// captures' 3 AP and 3 MP are -- but 792, two different spells, stays two.
        /// </summary>
        [Fact]
        public void The_same_bonus_adds_up()
        {
            var dream = New();
            Dreams.Gain(dream, new Dreams.Bono(128, 1));
            Dreams.Gain(dream, new Dreams.Bono(128, 1));
            Dreams.Gain(dream, new Dreams.Bono(281, 1, true));
            Dreams.Gain(dream, new Dreams.Bono(281, 1, true));
            Dreams.Gain(dream, new Dreams.Bono(792, 0, true));
            Dreams.Gain(dream, new Dreams.Bono(792, 0, true));

            Assert.Equal(2, dream.Ganados.Single(b => b.Efecto == 128).Valor);
            Assert.Equal(2, dream.Ganados.Single(b => b.Efecto == 281).Valor);
            Assert.Equal(2, dream.Ganados.Count(b => b.Efecto == 792));
        }

        /// <summary>Every bonus the rooms give reaches the fighter, the percentage of damage included.</summary>
        [Fact]
        public void The_bonuses_are_the_fighter_s()
        {
            var dream = New();
            foreach (var bonus in new[]
                     {
                         new Dreams.Bono(111, 1), new Dreams.Bono(128, 2), new Dreams.Bono(117, 2),
                         new Dreams.Bono(281, 1, true), new Dreams.Bono(2844, 20), new Dreams.Bono(4041, 5),
                         new Dreams.Bono(286, 1, true), new Dreams.Bono(291, 1, true), new Dreams.Bono(115, 25),
                     })
                Dreams.Gain(dream, bonus);

            var fighter = new Fighter { MaxAP = 7, CurrentAP = 7, MaxMP = 3, CurrentMP = 3, MaxHP = 1000, CurrentHP = 1000 };
            var applied = Dreams.ApplyTo(fighter, dream);

            Assert.Equal(9, applied.Count);
            Assert.Equal(8, fighter.MaxAP);
            Assert.Equal(8, fighter.CurrentAP);
            Assert.Equal(5, fighter.MaxMP);
            Assert.Equal(5, fighter.CurrentMP);
            Assert.Equal(3, fighter.Range);
            Assert.Equal(1200, fighter.MaxHP);
            Assert.Equal(1200, fighter.CurrentHP);
            Assert.Equal(105, fighter.DamageDealtPercent);
            Assert.Equal(1, fighter.CooldownReduction);
            Assert.Equal(1, fighter.ExtraCastsPerTarget);
            Assert.Equal(25, fighter.CriticalBonus);
        }

        /// <summary>
        /// A fountain stands on one of the five maps with the Fontaine onirique, its three doors
        /// apart from it; a fight room on a map of doors and nothing else.
        /// </summary>
        [Fact]
        public void A_fountain_has_its_fountain()
        {
            var dream = New();
            var fountain = dream.Salas.Single(r => r.EsFuente);

            int element = Dreams.FountainOf(fountain);
            Assert.NotEqual(0, element);
            var doors = Dreams.DoorsOf(fountain.MapaDeLaSala);
            Assert.Equal(3, doors.Count);
            Assert.DoesNotContain(doors, d => d.Id == element);

            foreach (var room in dream.Salas.Where(r => !r.EsFuente))
                Assert.Equal(0, Dreams.FountainOf(room));
        }

        /// <summary>The first line of the capture's loot table, byte for byte.</summary>
        [Fact]
        public void A_loot_line_is_the_bytes_of_the_capture()
        {
            float percent = BitConverter.ToSingle(Convert.FromHexString("a69bc43b"));
            byte[] izo = DreamProtocol.BuildDropTable(new[] { ("(Wi>3&Wp>2)", 20658, 1, (double)percent) });
            Assert.Equal("12180a0b2857693e332657703e322910b2a10118012da69bc43b", Convert.ToHexString(izo).ToLowerInvariant());
        }

        /// <summary>The positions of the Pesadilla III capture's room, byte for byte.</summary>
        [Fact]
        public void The_positions_are_the_bytes_of_the_capture()
        {
            var attackers = new[] { 171, 174, 184, 188, 198, 203, 217, 272, 286, 303, 317, 318, 331, 403, 416, 430 };
            var defenders = new[] { 268, 283, 300, 314, 368, 381, 382, 384, 396, 399, 413 };
            Assert.Equal("088898b871108088b0711a3a0a20ab01ae01b801bc01c601cb01d90190029e02af02bd02be02cb029303a003ae03" +
                         "12168c029b02ac02ba02f002fd02fe0280038c038f039d03",
                         Convert.ToHexString(DreamProtocol.BuildPositions(237898760, 237765632, attackers, defenders)).ToLowerInvariant());
        }

        /// <summary>
        /// The room's loot table opens with the coin every monster pays, at 100%, and goes on with
        /// the dream's own: the reflections, x12 at a Paradoja I's 120, and the other 60 lines.
        /// </summary>
        [Fact]
        public void The_loot_table_is_the_fight_s()
        {
            MobSpawnManager.EnsureMonsterData();
            var dream = New();
            var room = dream.Salas.First(r => r.Miembros.Count > 0);

            var drops = DreamHandler.DropsOf(room, 120);
            Assert.NotEmpty(drops);
            Assert.Equal(JondoCoin.TemplateId, drops[0].Item);
            Assert.Equal(100.0, drops[0].Percent);
            Assert.True(drops[0].Quantity > 0);
            Assert.Equal(12, drops.Single(d => d.Item == Dreams.ReflectionItem).Quantity);
            Assert.Equal(62, drops.Count);
            Assert.Empty(DreamHandler.DropsOf(dream.Buscar(0)));
        }

        /// <summary>
        /// The Rey Gob is placed with his look cut up: bone 6243 and skin 1665, as the real one.
        /// He came out as the question mark of a look the client cannot find.
        /// </summary>
        [Fact]
        public void The_rey_gob_has_his_face()
        {
            Npcs.Initialize();
            const long map = 237783053;
            Npcs.PonerDelSueno(map, Dreams.ReyGob, Dreams.CasillaDelReyGob, Dreams.OrientacionDelReyGob);

            var gob = Npcs.Of(map).Single(s => s.NpcId == Dreams.ReyGob);
            Assert.Equal(6243, gob.Bones);
            Assert.Equal(6243, gob.BoneId);
            Assert.Contains(1665L, gob.Skins);
        }
    }
}
