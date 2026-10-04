using System;
using System.Collections.Generic;
using System.Linq;
using Jondo.Unity.Server.Managers;
using Jondo.Unity.Server.Network;
using Xunit;

namespace Jondo.Unity.Tests.World
{
    /// <summary>
    /// The dream favours, the guide's Faveur Onirique: rooms of kind 2 behind the "bonus" portal,
    /// one NPC and three free choices, the purse of ten dream points last, and no way on until one
    /// is chosen.
    /// </summary>
    /// <remarks>
    /// Measured in the invitation capture's five-band graph: rooms "15", "29" and "41", on rows 7,
    /// 12 and 17, each { f5: 2, f6: the row, f7: 0 } and one exit. The door's look is read in the
    /// client (eft::xhh and the maps' staging sequences); the choices come from the guide and the
    /// client's reward 143.
    /// </remarks>
    [Collection("MapManager")]
    public class DreamFavorTests
    {
        private static Dreams.Sueno Whole(long id = 1, int difficulty = 4)
        {
            Interactives.Initialize();
            var dream = Dreams.Crear(id, "Prueba", 200, difficulty, 100, 200);
            for (int band = 2; band <= Dreams.Bands; band++) Dreams.AnadirFranja(dream);
            Dreams.Enter(dream, 0, out _);
            return dream;
        }

        /// <summary>A dream with a favour in it, whole: three bands in four have one, so a few tries find one.</summary>
        private static (Dreams.Sueno Dream, Dreams.Sala Favor) WithAFavor(int difficulty = 4)
        {
            Dreams.OlvidarTodo();
            for (int i = 1; i <= 50; i++)
            {
                var dream = Whole(i, difficulty);
                var favor = dream.Salas.FirstOrDefault(r => r.EsFavor);
                if (favor != null) return (dream, favor);
            }
            throw new InvalidOperationException("fifty dreams without a favour");
        }

        private static List<ProtoField> Fields(byte[] message) => ProtoMessage.Parse(message).Fields.ToList();

        private static int? Var(List<ProtoField> fields, int number)
            => (int?)fields.FirstOrDefault(f => f.FieldNumber == number && f.WireType == 0)?.VarIntValue;

        /// <summary>
        /// Bands II to IV, never I or V; one at most in a band; no fight, no reward, no dream points;
        /// one way on, as "15 -> 19", "29 -> 32" and "41 -> 44"; and within everybody's reach.
        /// </summary>
        [Fact]
        public void Favours_are_one_a_band_in_bands_II_to_IV()
        {
            Dreams.OlvidarTodo();
            int favours = 0;
            for (int i = 1; i <= 30; i++)
            {
                var dream = Whole(i);
                foreach (var band in dream.Salas.Where(r => r.EsFavor).GroupBy(r => r.Franja))
                {
                    Assert.InRange(band.Key, Dreams.FirstFavorBand, Dreams.LastFavorBand);
                    var favor = Assert.Single(band);
                    Assert.Empty(favor.Miembros);
                    Assert.Null(favor.Reward);
                    Assert.Equal(0, favor.DreamPoints);
                    Assert.False(favor.EsFuente || favor.Senalada || favor.EsFinal);
                    Assert.Single(favor.Salidas);
                    Assert.Contains(dream.Salas, r => r.Salidas.Contains(favor.Id));
                    favours++;
                }
                Assert.All(dream.Salas.Where(r => r.Id != 0), r => Assert.Contains(dream.Salas, p => p.Salidas.Contains(r.Id)));
            }
            // Three bands in four over thirty dreams: ninety bands, some seventy favours.
            Assert.InRange(favours, 40, 90);
        }

        /// <summary>The three favours of the invitation capture, byte for byte.</summary>
        [Theory]
        [InlineData(15, 7, "280230073800")]
        [InlineData(29, 12, "2802300c3800")]
        [InlineData(41, 17, "280230113800")]
        public void A_favour_is_the_bytes_of_the_capture(int id, int row, string hex)
            => Assert.Equal(hex, Convert.ToHexString(DreamProtocol.BuildRoom(new Dreams.Sala { Id = id, Fila = row, EsFavor = true })).ToLowerInvariant());

        /// <summary>
        /// A door's f5 is the look of its portal, by what is behind it: the client's staging
        /// sequences shop, bonus, combatFacile, combatDifficile and boss. The captures show 3 on
        /// the 96 doors to a fight, 4 on the 4 to a marked one and 1 on the 5 to a fountain.
        /// </summary>
        [Fact]
        public void The_portal_says_what_is_behind_the_door()
        {
            Assert.Equal(1, DreamProtocol.PortalOf(new Dreams.Sala { EsFuente = true }));
            Assert.Equal(2, DreamProtocol.PortalOf(new Dreams.Sala { EsFavor = true }));
            Assert.Equal(3, DreamProtocol.PortalOf(new Dreams.Sala { Fila = 5 }));
            Assert.Equal(4, DreamProtocol.PortalOf(new Dreams.Sala { Fila = 5, Senalada = true }));
            Assert.Equal(5, DreamProtocol.PortalOf(new Dreams.Sala { EsFinal = true, Senalada = true }));
        }

        /// <summary>The door to a favour is drawn green: 2 in the izg of the room before it.</summary>
        [Fact]
        public void The_door_to_a_favour_is_its_portal()
        {
            var (dream, favor) = WithAFavor();
            var before = dream.Salas.First(r => r.Salidas.Contains(favor.Id));
            dream.Actual = before.Id;
            before.Hecha = true;

            var doors = Fields(DreamProtocol.BuildDreamState(dream)).Where(f => f.FieldNumber == 4)
                                                                   .Select(f => Fields(f.BytesValue!)).ToList();
            var toFavor = doors.Single(d => d.Any(f => f.FieldNumber == 1
                                                       && System.Text.Encoding.UTF8.GetString(f.BytesValue!) == favor.Id.ToString()));
            Assert.Equal(2, Var(toFavor, 5));
        }

        /// <summary>
        /// On entering, three free choices: two different bonuses and the purse of ten dream
        /// points, always last -- in the izg's f6, the list the shop window reads -- and the
        /// room is not clear: f18 alone.
        /// </summary>
        [Fact]
        public void A_favour_offers_three_choices_the_purse_last()
        {
            var (dream, favor) = WithAFavor();
            int points = dream.DreamPoints;

            Dreams.Enter(dream, favor.Id, out var gained);

            Assert.Null(gained);
            Assert.Equal(points, dream.DreamPoints);
            Assert.NotNull(favor.Offers);
            Assert.Equal(3, favor.Offers!.Count);
            Assert.Equal(143, favor.Offers[2].Tag);
            Assert.Equal(10, favor.Offers[2].Points);
            Assert.NotEqual(favor.Offers[0].Tag, favor.Offers[1].Tag);
            Assert.All(favor.Offers.Take(2), o => Assert.Contains(o, Dreams.FavorBonuses));
            Assert.All(favor.Offers, o => Assert.Equal(0, o.Price));

            var izg = Fields(DreamProtocol.BuildDreamState(dream));
            Assert.Equal(3, izg.Count(f => f.FieldNumber == 6));
            Assert.Equal(1, Var(izg, 18));
            Assert.Null(Var(izg, 19));
        }

        /// <summary>
        /// No way on without choosing: the doors stay shut until one of the three is taken, and
        /// after it the room is clear, the choices gone, and nothing more can be taken.
        /// </summary>
        [Fact]
        public void A_favour_cannot_be_skipped()
        {
            var (dream, favor) = WithAFavor();
            Dreams.Enter(dream, favor.Id, out _);

            Assert.False(Dreams.CanLeave(favor));

            var first = favor.Offers![0];
            var chosen = Dreams.Buy(dream, first.Tag, out string refusal);
            Assert.Same(first, chosen);
            Assert.Equal("", refusal);
            Assert.True(favor.FavorChosen);
            Assert.True(Dreams.CanLeave(favor));
            Assert.Null(favor.Offers);
            Assert.All(first.Bonuses, b => Assert.Contains(dream.Ganados, g => g.Efecto == b.Efecto));

            var izg = Fields(DreamProtocol.BuildDreamState(dream));
            Assert.Equal(0, izg.Count(f => f.FieldNumber == 6));
            Assert.Null(Var(izg, 18));
            Assert.Equal(1, Var(izg, 19));

            Assert.Null(Dreams.Buy(dream, Dreams.FavorPurse.Tag, out refusal));
            Assert.Contains("chosen", refusal);

            // Entering again -- a dream continued -- offers nothing more.
            Dreams.Enter(dream, favor.Id, out _);
            Assert.Null(favor.Offers);
        }

        /// <summary>The purse is ten dream points, into the f11.</summary>
        [Fact]
        public void The_purse_pays_ten_dream_points()
        {
            var (dream, favor) = WithAFavor();
            Dreams.Enter(dream, favor.Id, out _);
            int points = dream.DreamPoints;

            Assert.NotNull(Dreams.Buy(dream, Dreams.FavorPurse.Tag, out _));
            Assert.Equal(points + 10, dream.DreamPoints);
            Assert.Equal(points + 10, Var(Fields(DreamProtocol.BuildDreamState(dream)), 11));
        }

        /// <summary>
        /// An astral storm draws the favour's bonuses again, the purse still last; a favour chosen
        /// has nothing left to draw.
        /// </summary>
        [Fact]
        public void A_storm_draws_the_favour_again()
        {
            var (dream, favor) = WithAFavor();
            Dreams.Enter(dream, favor.Id, out _);

            Assert.True(Dreams.RerollFavor(favor));
            Assert.Equal(3, favor.Offers!.Count);
            Assert.Equal(Dreams.FavorPurse.Tag, favor.Offers[2].Tag);

            Dreams.Buy(dream, favor.Offers[0].Tag, out _);
            Assert.False(Dreams.RerollFavor(favor));
        }

        /// <summary>The purse is the client's reward 143: ten dream-point actions.</summary>
        [Fact]
        public void The_purse_is_the_client_s_reward_143()
        {
            var row = DreamData.RewardOf(Dreams.FavorPurse.Tag);
            Assert.NotNull(row);
            Assert.All(row!.Actions, a => Assert.Equal(DreamData.DreamPointAction, a));
            Assert.Equal(Dreams.FavorPurse.Points, row.Actions.Count);
        }

        /// <summary>
        /// A favour stands on the one map of the dream's with its three doors and a centrepiece
        /// that is neither door nor fountain: 237787188. The centrepiece is not declared a door.
        /// </summary>
        [Fact]
        public void The_favour_map_is_the_one_with_a_centrepiece()
        {
            Interactives.Initialize();
            Assert.Equal(new[] { 237787188L }, Dreams.MapasDeFavor());
            Assert.Equal(3, Dreams.DoorsOf(237787188).Count);
            Assert.True(Dreams.IsDreamMap(237787188));
            Assert.False(Dreams.IsDoorOrFountain(306053));

            var (_, favor) = WithAFavor();
            Assert.Equal(237787188, favor.MapaDeLaSala);
        }

        /// <summary>
        /// The Dispensador de favores speaks his template: 59655 with "Acepto el favor." -- the
        /// reply that opens the choices -- and "No, gracias.", and 59657 once it is given.
        /// </summary>
        [Fact]
        public void The_dispensador_speaks_his_template()
        {
            Npcs.Initialize();

            var talk = NpcDialogues.For(Dreams.FavorNpc, 0);
            Assert.NotNull(talk);
            Assert.Equal(Dreams.FavorOfferMessage, talk!.Opening);

            var offer = talk.Line(Dreams.FavorOfferMessage)!;
            Assert.True(offer.Choice(Dreams.FavorAcceptReply)!.DreamFavor);
            Assert.False(offer.Choice(81585)!.DreamFavor);
            Assert.NotNull(talk.Line(Dreams.FavorGivenMessage));

            var template = Npcs.TemplateOf(Dreams.FavorNpc);
            Assert.NotNull(template);
            Assert.Contains((long)Dreams.FavorAcceptReply, template!.Replies);
        }
    }
}
