using System;
using System.Collections.Generic;
using System.Linq;
using Jondo.Unity.Protocol.Game;
using Jondo.Unity.Server.Handlers;
using Jondo.Unity.Server.Managers;
using Jondo.Unity.Server.Network;
using Xunit;

namespace Jondo.Unity.Tests.World
{
    /// <summary>
    /// The fountain's shop (the f6 of the izg) and the room's bestiary (the f3), against the
    /// bytes of the long capture's fountain and of the Paradoja III capture's first room.
    /// </summary>
    [Collection("MapManager")]
    public class DreamShopAndBestiaryTests
    {
        private static Dreams.Sueno New(int difficulty = 4)
        {
            Interactives.Initialize();
            Dreams.OlvidarTodo();
            var dream = Dreams.Crear(1, "Prueba", 200, difficulty, 100, 200);
            Dreams.Enter(dream, 0, out _);
            return dream;
        }

        private static List<ProtoField> Fields(byte[] message) => ProtoMessage.Parse(message).Fields.ToList();

        /// <summary>A reward of the catalogue at its fountain price.</summary>
        private static Dreams.Reward Offer(int reward)
            => DreamCatalogue.RewardOf(reward, DreamCatalogue.Fountain.Single(f => f.Reward == reward).Price)!;

        /// <summary>A dream at its first fountain, showing the capture's five offers.</summary>
        private static (Dreams.Sueno Dream, Dreams.Sala Fountain) AtTheCapturedFountain()
        {
            var dream = New();
            var fountain = dream.Salas.First(r => r.EsFuente);
            Dreams.Enter(dream, fountain.Id, out _);
            fountain.Offers = new[] { 65, 149, 103, 89, 124 }.Select(Offer).ToList();
            return (dream, fountain);
        }

        /// <summary>
        /// The five offers of the long capture's fountain, byte for byte and in their order, as
        /// the catalogue makes them: its rewards 65, 149, 103, 89 and 124 at their prices.
        /// </summary>
        [Fact]
        public void The_shop_is_the_bytes_of_the_capture()
        {
            string[] measured =
            {
                "080010001a090a052014589b1710011a090a052028589c16100120002800400f48ce7350415800",
                "080010001a0b0a0720ef990558cd1a1001200028003802400f489d785095015800",
                "080010001a090a05206458a21610011a090a05206458a416100120002800400f48cf7350675800",
                "080010001a0b0a0720e58d0558cd1a1001200028003802400f48967850595800",
                "080010001a080a04201958731001200028003802400f48dd73507c5800",
            };
            Assert.Equal(measured, new[] { 65, 149, 103, 89, 124 }.Select(r =>
                Convert.ToHexString(DreamProtocol.BuildReward(Offer(r))).ToLowerInvariant()));
        }

        /// <summary>
        /// At a fountain the izg carries what the shop shows, one f6 each; buying takes the
        /// price off the dream points, adds the bonuses, and another offer takes its place.
        /// </summary>
        [Fact]
        public void The_fountain_sells_what_it_has_for_dream_points()
        {
            var (dream, fountain) = AtTheCapturedFountain();
            Assert.Equal(5, DreamProtocol.BuildDreamState(dream).Rewards.Count);

            // Five dream points for a price of fifteen: nothing.
            dream.DreamPoints = 5;
            Assert.Null(Dreams.Buy(dream, 14813, out string refusal));
            Assert.NotEmpty(refusal);

            // Twenty: the critical offer, by its reward id.
            dream.DreamPoints = 20;
            var bought = Dreams.Buy(dream, 14813, out _);
            Assert.NotNull(bought);
            Assert.Equal(5, dream.DreamPoints);
            Assert.Contains(dream.Ganados, b => b.Efecto == 115 && b.Valor == 25);
            Assert.Equal(5, DreamProtocol.BuildDreamState(dream).Rewards.Count);
            Assert.DoesNotContain(fountain.Offers!, o => o.Tag == 124);

            // And by its place in the shop: the first one, with its two bonuses.
            dream.DreamPoints = 15;
            Assert.Equal(14798, Dreams.Buy(dream, 0, out _)!.Id);
            Assert.Equal(0, dream.DreamPoints);
            Assert.Equal(5, fountain.Offers!.Count);
            Assert.Equal(new[] { 124, 65 }, dream.RewardsTaken);
        }

        /// <summary>
        /// The client buys by the offer's f10: clicking "Psst Psst" sent iym { f1: 149 }, the f10
        /// of reward 15389, and nothing was bought while the server looked for a reward id.
        /// </summary>
        [Fact]
        public void The_client_buys_by_the_f10()
        {
            var (dream, _) = AtTheCapturedFountain();
            dream.DreamPoints = 35;

            var bought = Dreams.Buy(dream, 149, out _);
            Assert.NotNull(bought);
            Assert.Equal(15389, bought!.Id);
            Assert.Equal(20, dream.DreamPoints);
            Assert.Contains(dream.Ganados, b => b.Efecto == 3405 && b.Valor == 85231);
        }

        /// <summary>Anywhere else there is no shop: no f6, and nothing to buy.</summary>
        [Fact]
        public void Only_a_fountain_sells()
        {
            var dream = New();
            dream.DreamPoints = 100;
            Assert.Empty(DreamProtocol.BuildDreamState(dream).Rewards);
            Assert.Null(Dreams.Buy(dream, 0, out _));
        }

        /// <summary>
        /// A monster of the bestiary, against the first of the Paradoja III capture's room 1:
        /// monster 1153 on cell 283, level 200, and its thirteen characteristics.
        /// </summary>
        [Fact]
        public void The_snapshot_encodes_the_reviewed_beast_fields_and_omits_the_unmapped_level()
        {
            var dream = New();
            Dreams.Enter(dream, dream.Salas.First(r => r.Miembros.Count > 0).Id, out _);
            var beast = new InfiniteDreamMonster(283, 1153, InfiniteDreamMonsterKind.Normal, new Dictionary<int, int>
            {
                [0] = 5450, [1] = 10, [33] = 17, [34] = 10, [35] = 41, [36] = -33, [37] = 15,
                [44] = 3263, [78] = 88, [79] = 88, [23] = 4, [27] = 69, [28] = 69,
            });
            var boss = new InfiniteDreamMonster(205, 8099, InfiniteDreamMonsterKind.Boss, new Dictionary<int, int>());
            var rows = Fields(DreamProtocol.BuildDreamState(dream, [beast, boss]).Payload()).Where(f => f.FieldNumber == 3)
                .Select(f => Convert.ToHexString(f.BytesValue!).ToLowerInvariant()).ToArray();
            // The codec elides the map entry's default key (0 = life points); the row and its value remain.
            Assert.Equal("089b021081092a0310ca2a2a040801100a2a04082110112a040822100a2a04082310292a0d0824" +
                         "10dfffffffffffffffff012a040825100f2a05082c10bf192a04084e10582a04084f10582a04081710042a04" +
                         "081b10452a04081c1045",
                          rows[0]);

            // A boss carries f4, as monster 8099 does after the storm of the same capture.
            Assert.Equal("08cd0110a33f2001", rows[1]);
        }

        /// <summary>The value-4 bit of m_flags: set on 8099 and 173, which the bestiary marks, not on 209.</summary>
        [Fact]
        public void The_boss_flag_is_the_template_s()
        {
            Assert.True(Dreams.IsBoss(8099));
            Assert.True(Dreams.IsBoss(173));
            Assert.False(Dreams.IsBoss(209));
        }

        /// <summary>
        /// The bestiary of a room is its group as the fight builds it, on the cells the fight
        /// places it on, and it goes in the izg only while the fight is to be won.
        /// </summary>
        [Fact]
        public void The_bestiary_is_the_fight_to_come()
        {
            MobSpawnManager.EnsureMonsterData();
            var dream = New();
            var room = dream.Salas.First(r => r.Miembros.Count > 0 && dream.Buscar(0)!.Salidas.Contains(r.Id));
            Dreams.Enter(dream, room.Id, out _);

            var beasts = DreamHandler.BestiaryOf(room);
            Assert.Equal(room.Miembros.Count, beasts.Count);
            var cells = FightHandler.DefenderPlacement(room.MapaDeLaSala);
            for (int i = 0; i < beasts.Count; i++)
            {
                Assert.Equal(i < cells.Count ? cells[i] : cells[0], beasts[i].CellId);
                Assert.Equal(room.Miembros[i].Monstruo, beasts[i].MonsterId);
                Assert.True(beasts[i].Characteristics[0] > 0);
            }

            Assert.Equal(beasts.Count, DreamProtocol.BuildDreamState(dream, beasts).Monsters.Count);

            room.Hecha = true;
            Assert.Empty(DreamHandler.BestiaryOf(room));
            Assert.Empty(DreamProtocol.BuildDreamState(dream, beasts).Monsters);
        }
    }
}
