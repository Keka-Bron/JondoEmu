using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Jondo.Unity.Launcher;
using Jondo.Unity.Server;
using Jondo.Unity.Server.Handlers;
using Jondo.Unity.Server.Managers;
using Jondo.Unity.Server.Network;
using Jondo.Unity.Tests.Economy;
using Xunit;

namespace Jondo.Unity.Tests.World
{
    /// <summary>
    /// The perlequines: their flavour goes to the boost food's slot for sixty minutes, «Expiración: #1»
    /// as the moment it ends, and the heartbeat takes it away when that moment comes.
    /// </summary>
    public sealed class PerlequinTests
    {
        private const int CitrusPerlequin = 21078, CitrusFlavour = 21061;

        [Fact]
        public void The_minutes_become_the_moment_it_ends_and_travel_as_a_date()
        {
            var now = new DateTime(2026, 10, 22, 12, 0, 0, DateTimeKind.Utc);
            var placed = BonusSlots.Fresh(new[]
            {
                new Equipment.ItemEffect(124, 100, 0, 0),
                new Equipment.ItemEffect(AlterationCatalogue.Expiry, 0, 0, 60),
            }, now);
            long end = new DateTimeOffset(now).ToUnixTimeSeconds() + 3600;
            Assert.Equal(end, placed.Single(e => e.Effect == AlterationCatalogue.Expiry).Value);
            Assert.Equal(100, placed.Single(e => e.Effect == 124).Value);
            Assert.Equal(end, BonusSlots.EndsAt(placed));

            Assert.Equal((EffectFields.AsDate, end, 0L, 0L), EffectFields.Shape(AlterationCatalogue.Expiry, end, 0, 0));
            Assert.NotEqual(EffectFields.AsDate, EffectFields.Shape(AlterationCatalogue.Expiry, 60, 0, 0).Field);
        }

        [Fact]
        public async Task A_perlequin_s_flavour_lasts_its_hour()
        {
            if (!File.Exists(Paths.WorldDb)) return;
            const long account = 8_900_000_301;
            long character = DatabaseManager.CreateCharacter(account, 1, "Golosa-Uno", 4, 0, 1,
                new long[] { -1, -1, -1, -1, -1 }, 191105026, 100, 0, 0, Array.Empty<(int, int)>());
            Assert.True(character > 0);
            try
            {
                await using var pipe = await ClientPipe.OpenAsync(account, character, 990_100_161, "Golosa-Uno");
                using (SessionContext.Push(pipe.Session))
                {
                    Assert.True(await Equipment.GiveAsync(pipe.ToClient, CitrusPerlequin, 1));
                    var perlequin = Equipment.All.Single(i => i.Template == CitrusPerlequin);
                    Assert.True(await BonusItemHandler.TryUseAsync(pipe.ToClient, perlequin));

                    var flavour = Equipment.All.Single(i => i.Template == CitrusFlavour);
                    Assert.Equal(BonusSlots.BoostFood, flavour.Position);
                    long now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
                    Assert.InRange(BonusSlots.EndsAt(flavour.Effects), now + 3590, now + 3610);

                    // Still there before its end...
                    await AlterationHandler.TickAsync(pipe.Session);
                    Assert.Contains(Equipment.All, i => i.Template == CitrusFlavour);

                    // ...and gone once it has come.
                    var ended = flavour.Effects.Select(e => e.Effect == AlterationCatalogue.Expiry
                        ? new Equipment.ItemEffect(e.Effect, now - 1, 0, 0) : e).ToList();
                    Assert.True(Equipment.Rewrite(flavour, flavour.Quantity, ended));
                    await AlterationHandler.TickAsync(pipe.Session);
                    Assert.DoesNotContain(Equipment.All, i => i.Template == CitrusFlavour);
                }
            }
            finally
            {
                DatabaseManager.DeleteCharacter(character, account);
            }
        }
    }
}
