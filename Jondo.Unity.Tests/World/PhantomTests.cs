using Jondo.Unity.Server.Managers;
using Xunit;

namespace Jondo.Unity.Tests.World
{
    /// <summary>
    /// «El cementerio primitivo» (485): Warko Linch's brew turns the character into a soul in the
    /// primitive cemetery, and its phoenix brings him back (Managers.Phantoms).
    /// </summary>
    public class PhantomTests
    {
        private const int Warko = 1219;
        private const long Hut = 73402891, Cemetery = 64488707, Phoenix = 64489222;

        [Fact]
        public void A_soul_is_one_until_he_rises()
        {
            const long id = 990_485_001;
            Assert.False(Phantoms.Is(id));
            Phantoms.Become(id, Phoenix);
            Assert.True(Phantoms.Is(id));
            Assert.Equal(Phoenix, Phantoms.PhoenixOf(id));
            Assert.True(Phantoms.Rise(id));
            Assert.False(Phantoms.Is(id));
            Assert.False(Phantoms.Rise(id));
            Assert.Equal(0, Phantoms.PhoenixOf(id));
        }

        [Fact]
        public void A_soul_wears_the_souls_body()
        {
            Assert.StartsWith("{3|322,", Phantoms.LookOf(0));
            Assert.StartsWith("{3|323,", Phantoms.LookOf(1));
        }

        [Fact]
        public void Warko_takes_the_flowers_and_his_brew_leaves_a_soul_by_the_phoenix()
        {
            var tree = NpcDialogues.For(Warko, Hut);
            Assert.NotNull(tree);
            Assert.Empty(Jondo.Unity.World.Content.NpcDialogueContent.Complaints(tree));

            var flowers = tree.Line(5446)?.Choice(7477);
            Assert.NotNull(flowers);
            Assert.Equal(3463, flowers.Completes);
            Assert.Equal(7904, flowers.Holds);
            Assert.Equal(5, flowers.HoldsCount);
            Assert.Equal(5442, flowers.Next);

            var drink = tree.Line(5442)?.Choice(7475);
            Assert.NotNull(drink);
            Assert.Equal(3464, drink.Completes);
            Assert.Equal(Cemetery, drink.TeleportsTo);
            Assert.Equal(Phoenix, drink.PhantomPhoenix);
        }
    }
}
