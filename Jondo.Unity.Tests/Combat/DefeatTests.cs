using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using Jondo.Unity.Server;
using Jondo.Unity.Server.Handlers;
using Jondo.Unity.Server.Managers;
using Jondo.Unity.Server.Network;
using Jondo.Unity.World.Fights;
using Microsoft.Data.Sqlite;
using Xunit;

namespace Jondo.Unity.Tests.Combat
{
    /// <summary>
    /// What losing a fight against monsters costs, against the four defeats of the captures: the
    /// energy, half the life, the message, and the way back beside the save point's zaap.
    /// </summary>
    // Asks the dream's maps (a defeat in a dream costs nothing), which the dream tests share.
    [Collection("MapManager")]
    public class DefeatTests
    {
        private static byte[] Hex(string hex) => Convert.FromHexString(hex);

        // ─── The numbers ─────────────────────────────────────────────────────────────────────

        /// <summary>
        /// The level 354 -- a 200 with Omega 154 -- loses 2,000 each time (10 × 200), and the
        /// last 1,000 lose 999: the gauge stops at one.
        /// </summary>
        [Theory]
        [InlineData(354, 10000, 2000)]   // ruta muy larga zonas pandala-sidimote: 10000 -> 8000
        [InlineData(354, 5000, 2000)]    // entrar a combate-cerrar juego: 5000 -> 3000
        [InlineData(354, 3000, 2000)]    // submarino steamer-...-dopeul-perder: 3000 -> 1000
        [InlineData(354, 1000, 999)]     // vestigio de zaap-...-abandonar: 1000 -> 1
        [InlineData(354, 1, 0)]
        [InlineData(50, 10000, 500)]     // "10 veces su nivel", Translations 1156704
        [InlineData(1, 10000, 10)]
        public void The_energy_a_defeat_costs(int level, int energy, int lost)
        {
            Assert.Equal(lost, DefeatPenalty.EnergyLost(level, energy));
        }

        /// <summary>Half the maximum missing: 576 of 1,153 in four defeats, 3,096 of 6,192 in Pandala.</summary>
        [Theory]
        [InlineData(1153, 576)]
        [InlineData(6192, 3096)]
        public void Half_the_life_missing(int maxLife, int missing)
        {
            Assert.Equal(missing, DefeatPenalty.MissingLifeAfter(maxLife));
        }

        /// <summary>"Has perdido 2000 puntos de energía." and "... 999 ...", byte for byte.</summary>
        [Fact]
        public void The_message_is_the_capture_s()
        {
            // Pandala 8829, the Dopeul 2496, "entrar a combate-cerrar juego" 293.
            Assert.Equal(Hex("1022220432303030"),
                         ConnectionProtocol.BuildSystemMessage(InfoMessages.EnergyLost, "2000"));
            // The anomaly, 767.
            Assert.Equal(Hex("10222203393939"),
                         ConnectionProtocol.BuildSystemMessage(InfoMessages.EnergyLost, "999"));
        }

        // ─── Who pays ────────────────────────────────────────────────────────────────────────

        [Fact]
        public void Only_a_fight_against_monsters_costs()
        {
            Assert.True(FightRules.ContraMonstruos.DefeatCosts);
            Assert.False(FightRules.Desafio.DefeatCosts);
            Assert.False(FightRules.Koliseo.DefeatCosts);
            Assert.False(FightRules.Entrenamiento.DefeatCosts);

            Assert.True(FightHandler.DefeatCostsIn(new FightInstance(1, 120195585)));
            Assert.False(FightHandler.DefeatCostsIn(new FightInstance(2, 120195585) { Reglas = FightRules.Desafio }));

            // A dream's room: its capture loses with the energy at 10000 and the life whole.
            Assert.False(FightHandler.DefeatCostsIn(new FightInstance(3, Dreams.MapaDeEntrada)));
        }

        // ─── What it does to the character ───────────────────────────────────────────────────

        private const long Loser = 8_950_000_001;

        private static void ForgetEnergy()
        {
            Energy.Forget(Loser);
            using var connection = new SqliteConnection(DatabaseManager.WorldConnectionString);
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = "CREATE TABLE IF NOT EXISTS CharacterEnergy (CharacterId INTEGER PRIMARY KEY, Energy INTEGER NOT NULL);" +
                                  "DELETE FROM CharacterEnergy WHERE CharacterId = $c;";
            command.Parameters.AddWithValue("$c", Loser);
            command.ExecuteNonQuery();
        }

        /// <summary>
        /// The capture's character, 1,153 of life (50 + 5 × 200 + 103 of vitality): the defeat
        /// leaves 576 missing and the energy 2,000 lower, stored, and sends him to the save point.
        /// </summary>
        [Fact]
        public void A_defeat_takes_the_energy_and_half_the_life()
        {
            ForgetEnergy();
            var session = GameSession.SinSocket();
            try
            {
                using (SessionContext.Push(session))
                {
                    session.State.CharacterId = Loser;
                    session.State.CharacterLevel = 200;
                    session.State.StatVitality = 3;
                    session.State.ScrolledVitality = 100;
                    Assert.Equal(1153, StatsHandler.GetPlayerMaxHp());
                    Assert.Equal(DefeatPenalty.MaxEnergy, Energy.Of(Loser));

                    var defeat = FightHandler.ApplyDefeat();

                    Assert.Equal(2000, defeat.EnergyLost);
                    Assert.Equal(8000, defeat.EnergyLeft);
                    Assert.Equal(576, defeat.MissingLife);
                    Assert.Equal(CharacterCreationHandler.StartingMap, defeat.SavePointMap);

                    // Stored: read again from the base.
                    Energy.Forget(Loser);
                    Assert.Equal(8000, Energy.Of(Loser));

                    // And the sheet says so: 29 the energy, 47 the gauge, 97 the life missing.
                    session.State.RegenerationStartedUtc = DateTime.UtcNow;
                    Assert.Equal(8000, ConnectionProtocol.ValueOf(ConnectionProtocol.Stat.EnergyPoints, 200));
                    Assert.Equal(10000, ConnectionProtocol.ValueOf(ConnectionProtocol.Stat.MaxEnergyPoints, 200));
                    Assert.Equal(-576, ConnectionProtocol.ValueOf(ConnectionProtocol.Stat.HitPointLoss, 200));

                    // The next fight takes the life he has.
                    Assert.Equal(577, FightHandler.BuildPlayerFighter(new FightInstance(9, 120195585)).CurrentHP);
                }
            }
            finally
            {
                RestingLife.Clear(Loser);
                ForgetEnergy();
            }
        }

        // ─── The life comes back ─────────────────────────────────────────────────────────────

        /// <summary>
        /// "entrar a combate-cerrar juego": the collector's defeat left 576 missing, and the fight
        /// entered nine and a half minutes later reads 1,153 of 1,153 after 1,138 ticks in its kuq.
        /// </summary>
        [Fact]
        public void The_life_comes_back_with_the_regeneration()
        {
            const long someone = 8_950_000_002;
            var wounded = new DateTime(2026, 8, 20, 0, 0, 0, DateTimeKind.Utc);
            try
            {
                RestingLife.Set(someone, 576, wounded);

                Assert.Equal(576, RestingLife.MissingAt(someone, wounded));
                Assert.Equal(577, RestingLife.LifeAt(someone, 1153, wounded));
                // A hundred seconds are two hundred ticks of half a second.
                Assert.Equal(1153 - 376, RestingLife.LifeAt(someone, 1153, wounded.AddSeconds(100)));
                Assert.Equal(1153, RestingLife.LifeAt(someone, 1153, wounded.AddMilliseconds(1138 * ConnectionProtocol.RegenerationTickMs)));

                RestingLife.Clear(someone);
                Assert.Equal(0, RestingLife.MissingAt(someone, wounded));
            }
            finally
            {
                RestingLife.Clear(someone);
            }
        }

        // ─── Where he comes back ─────────────────────────────────────────────────────────────

        /// <summary>
        /// Beside the zaap, on the side it lands its travellers: the defeats on the saved zaap
        /// 73400320 come back on 299 by its 286; the zaap arrivals of the captures land on 272 by
        /// the Castillo's 259, on 286 by 272, and on 315 by Bonta's 300.
        /// </summary>
        [Collection("MapManager")]
        public class Arrival
        {
            [Theory]
            [InlineData(73400320, 299)]
            [InlineData(68552706, 272)]
            [InlineData(54172969, 286)]
            [InlineData(212600323, 315)]
            public void Beside_the_zaap(long map, int cell)
            {
                Interactives.Initialize();
                LoadWalkable(map);

                Assert.Equal(cell, FightHandler.ZaapArrivalCell(map));
            }

            /// <summary>The walkable cells of one map, the ones MapManager.Initialize would load for it.</summary>
            private static void LoadWalkable(long map)
            {
                using var doc = JsonDocument.Parse(File.ReadAllText(Jondo.Unity.Launcher.Paths.WalkableCellsJson));
                var cells = doc.RootElement.GetProperty(map.ToString()).EnumerateArray().Select(e => e.GetInt32()).ToList();
                lock (MapManager.WalkableCells) MapManager.WalkableCells[map] = cells;
            }
        }
    }
}
