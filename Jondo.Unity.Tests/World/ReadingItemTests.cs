using System;
using System.Linq;
using System.Threading.Tasks;
using Jondo.Unity.Protocol;
using Jondo.Unity.Server;
using Jondo.Unity.Server.Handlers;
using Jondo.Unity.Server.Network;
using Xunit;
using Wire = Jondo.Unity.Tests.Combat.PortalTests.Wire;

namespace Jondo.Unity.Tests.World
{
    /// <summary>The books of the bag, against «abrir libro el escudo de feca y leerlo».</summary>
    public class ReadingItemTests
    {
        private static DatabaseManager.ItemEffectData Effect(int id, int dice = 0)
            => new() { EffectId = id, DiceNum = dice };

        /// <summary>A book opens its document, bound or not; one that also starts a quest is not this one's.</summary>
        [Fact]
        public void A_book_opens_its_document()
        {
            Assert.Equal(217, ReadingItemHandler.DocumentOf(new[] { Effect(620, 217) }.ToList()));
            Assert.Equal(17, ReadingItemHandler.DocumentOf(new[] { Effect(620, 17), Effect(981) }.ToList()));

            // Effect 30 validates an objective (327 of 327 items carry an objective id in it): the
            // recipe of Incarnam's cake opens document 437 and does 9805. It no longer stops a book.
            Assert.Equal(40, ReadingItemHandler.DocumentOf(new[] { Effect(30, 1), Effect(620, 40) }.ToList()));
            Assert.Equal(0, ReadingItemHandler.DocumentOf(new[] { Effect(110, 30) }.ToList()));
        }

        /// <summary>
        /// The Escudo de Feca opened and closed as the capture does: kkt { f2: 217 }, and to the close
        /// button's kla an empty kld; a second kla is no longer the document's.
        /// </summary>
        [Fact]
        public async Task A_document_opens_and_closes_as_the_capture()
        {
            await using var wire = await Wire.Open(7601);
            using (SessionContext.Push(wire.Session))
            {
                await ReadingItemHandler.OpenAsync(wire.Session.Stream!, 217);
                var opened = await wire.Drain();
                Assert.Equal("10d901", Convert.ToHexString(Assert.Single(opened, f => f.Op == Op.Kkt).Payload).ToLowerInvariant());

                Assert.True(await ReadingItemHandler.CloseAsync(wire.Session.Stream!));
                var closed = await wire.Drain();
                Assert.Empty(Assert.Single(closed, f => f.Op == Op.Kld).Payload ?? Array.Empty<byte>());

                Assert.False(await ReadingItemHandler.CloseAsync(wire.Session.Stream!));
            }
        }
    }
}
