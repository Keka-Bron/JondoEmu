using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Jondo.Unity.Server.Handlers;
using Jondo.Unity.Server.Managers;
using Jondo.Unity.World.Fights;
using Xunit;

namespace Jondo.Unity.Tests.Combat
{
    /// <summary>
    /// The Zurcarák's poker, hand by hand, as the class sheet lists them: four cards drawn, the
    /// winning hand the client's own spells make of them -- the best one only --, and what playing
    /// it leaves: the hand's bonus on the table, the cards on the table, the hand empty.
    /// </summary>
    /// <remarks>
    /// "Pica: aire, Trébol: agua, Corazón: fuego, Diamantes: tierra; Sota: 2 PA, Reina: 3 PA, Rey: 4
    /// PA, As: 5 PA". Each card is a draw spell of «Mano de Zurcarák» and a state; each winning
    /// hand a state of 29703's and a spell of Póquer de Zurcarák's (29683).
    /// </remarks>
    public class ZurcarakPokerHandsTests
    {
        private const int PlayTheHand = 29683;

        // (rank, suit) -> (draw spell, card state). Ranks J Q K A, suits S C H D.
        private static readonly Dictionary<string, (int Draw, int State)> Cards = new()
        {
            ["JS"] = (29675, 5440), ["QS"] = (29686, 5442), ["KS"] = (29687, 5443), ["AS"] = (29688, 5457),
            ["JC"] = (29676, 5444), ["QC"] = (29691, 5446), ["KC"] = (29693, 5447), ["AC"] = (29694, 5458),
            ["JH"] = (29678, 5448), ["QH"] = (29696, 5450), ["KH"] = (29697, 5451), ["AH"] = (29698, 5459),
            ["JD"] = (29679, 5452), ["QD"] = (29700, 5454), ["KD"] = (29701, 5455), ["AD"] = (29702, 5460),
        };

        /// <summary>Every winning hand's state and the spell its bonus is.</summary>
        private static readonly Dictionary<int, int> Bonus = new()
        {
            [5488] = 29818, [5675] = 30054, [5706] = 30088, [5487] = 29817, [5676] = 30055, [5480] = 29810,
            [5481] = 29811, [5482] = 29812, [5483] = 29813, [5484] = 29814, [5532] = 29978, [5677] = 30056,
            [5678] = 30057, [5707] = 30089, [5486] = 29816, [5679] = 30058, [5708] = 30090, [5680] = 30059,
            [5485] = 29815, [5709] = 30091, [5533] = 29979,
        };

        private static Fighter Zurcarak(FightInstance fight)
        {
            var me = new Fighter { Id = 1, Breed = 6, CellId = 300, MaxHP = 4000, CurrentHP = 4000, Level = 200, MaxAP = 12, CurrentAP = 12, MaxMP = 6, CurrentMP = 6 };
            var foe = new Fighter { Id = 2, Breed = 8, CellId = 330, MaxHP = 4000, CurrentHP = 4000, Level = 200, MaxAP = 12, CurrentAP = 12, MaxMP = 6, CurrentMP = 6 };
            fight.AddPlayer(me); fight.AddOpponent(foe);
            me.CellId = 300; foe.CellId = 330;
            return me;
        }

        public static IEnumerable<object[]> Hands() => new[]
        {
            new object[] { "Póquer de Ases", "AS AC AH AD", 5488 },
            new object[] { "Reyes y Ases", "KS KC AH AD", 5675 },
            new object[] { "Trío de Ases", "AS AC AH JD", 5706 },
            new object[] { "Póquer de Reyes", "KS KC KH KD", 5487 },
            new object[] { "Reinas y Ases", "QS QC AH AD", 5676 },
            new object[] { "Serie Real de Colores", "JS QC KH AD", 5480 },
            new object[] { "Serie Real de Picas", "JS QS KS AS", 5481 },
            new object[] { "Serie Real de Tréboles", "JC QC KC AC", 5482 },
            new object[] { "Serie Real de Corazones", "JH QH KH AH", 5483 },
            new object[] { "Serie Real de Diamantes", "JD QD KD AD", 5484 },
            new object[] { "Serie Real", "JS QS KC AH", 5532 },
            new object[] { "Reinas y Reyes", "QS QC KH KD", 5677 },
            new object[] { "Sotas y Ases", "JS JC AH AD", 5678 },
            new object[] { "Trío de Reyes", "KS KC KH JD", 5707 },
            new object[] { "Póquer de Damas", "QS QC QH QD", 5486 },
            new object[] { "Sotas y Reyes", "JS JC KH KD", 5679 },
            new object[] { "Trío de Reinas", "QS QC QH JD", 5708 },
            new object[] { "Sotas y Reinas", "JS JC QH QD", 5680 },
            new object[] { "Póquer de Sotas", "JS JC JH JD", 5485 },
            new object[] { "Trío de Sotas", "JS JC JH QD", 5709 },
            new object[] { "Póquer de Colores", "JS JC QH KD", 5533 },
            new object[] { "no winning hand", "JS QS QC KS", 0 },
        };

        [Theory]
        [MemberData(nameof(Hands))]
        public async Task Four_cards_make_their_best_hand_and_playing_it_lays_its_bonus(string name, string hand, int winning)
        {
            var fight = new FightInstance(1, 1);
            var me = Zurcarak(fight);
            var cards = hand.Split(' ');
            foreach (var card in cards)
                await FightHandler.AplicarEfectosAsync(null, fight, me, Cards[card].Draw, 1, me, EffectEngine.AlLanzar, me.CellId);

            foreach (var card in cards)
                Assert.True(me.Buffs.TieneEstado(Cards[card].State), $"{name}: {card} not in the hand");
            var held = Bonus.Keys.Where(me.Buffs.TieneEstado).ToList();
            Assert.True(winning == 0 ? held.Count == 0 : held.SequenceEqual(new[] { winning }),
                        $"{name}: the hand reads as {string.Join(", ", held)}, expected {(winning == 0 ? "none" : winning.ToString())}");

            await FightHandler.AplicarEfectosAsync(null, fight, me, PlayTheHand, 1, me, EffectEngine.AlLanzar, me.CellId);

            // The hand is empty, its cards on the table (each card's own spell), its bonus too.
            foreach (var card in cards)
                Assert.False(me.Buffs.TieneEstado(Cards[card].State), $"{name}: {card} still in the hand after playing it");
            var origins = me.Buffs.Puestos.Select(r => r.HechizoOrigen).ToHashSet();
            if (winning != 0)
                Assert.True(origins.Contains(Bonus[winning]), $"{name}: no row of its bonus {Bonus[winning]} on the table ({string.Join(", ", origins)})");
            foreach (var other in Bonus.Where(b => b.Key != winning))
                Assert.False(origins.Contains(other.Value), $"{name}: the bonus of another hand ({other.Value}) is on the table");
        }
    }
}
