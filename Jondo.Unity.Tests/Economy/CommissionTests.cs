using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Jondo.Unity.Server.Handlers;
using Jondo.Unity.Server.Network;
using Xunit;
using Effect = Jondo.Unity.Server.Managers.Equipment.ItemEffect;

namespace Jondo.Unity.Tests.Economy
{
    /// <summary>
    /// A magus maging somebody else's item: the messages against the two captured sessions
    /// (Oficios/"envio invitacion a maguear ...") and the customer's side up to accepting
    /// ("recibo invitacion para que me magueen joya ..."), and who pays whom at the end.
    /// </summary>
    [Collection("forgemagic")]
    public class CommissionTests
    {
        private static byte[] Hex(string hex) => Convert.FromHexString(hex);
        private const long Magus = 302677754146, Customer = 293213045026;

        [Fact]
        public void An_invitation_waits_with_both_roles()
        {
            // The magus invites (kbl f3 = 10) and gets his kgu: the other, his role, himself.
            Assert.Equal(Hex("08a282f0a6c408100a18a28280c8e708"),
                         WorkshopProtocol.BuildCommissionRequested(Customer, WorkshopProtocol.CrafterRole, Magus));
            // The one invited to have a jewel maged gets role 11, with the magus as sender twice.
            Assert.Equal(Hex("08a282f0a6c408100b18a282f0a6c408"),
                         WorkshopProtocol.BuildCommissionRequested(Customer, WorkshopProtocol.CustomerRole, Customer));
        }

        [Fact]
        public void Accepting_opens_both_windows()
        {
            Assert.Equal(Hex("10a601"), WorkshopProtocol.BuildCrafterOpened(166));
            // The customer is told the magus' job: level 15 joyeromago, 2,307 experience.
            Assert.Equal(Hex("0a0d083f10e012180f20b41028831210a282f0a6c408"),
                         WorkshopProtocol.BuildOtherJob(63, 2400, 15, 2100, 2307, Customer));
            Assert.Equal(Hex("080f10a801"), WorkshopProtocol.BuildCustomerOpened(15, 168));
        }

        [Fact]
        public void The_customers_offer_readiness_and_payment()
        {
            var hat = new List<Effect> { new Effect(125, 18, 0, 0), new Effect(422, 1, 0, 0) };
            Assert.Equal(Hex("1a1c083f2a1808b74012042012587d1205200158a60318012090c9fafc012001"),
                         WorkshopProtocol.BuildOffered(8247, hat, 1, 530490512, remote: true));
            Assert.Equal(Hex("1a1c083f2a1808b74012042012587d1205200158a60318012090c9fafc01"),
                         WorkshopProtocol.BuildOffered(8247, hat, 1, 530490512, remote: false));
            Assert.Equal(Hex("0890c9fafc01"), WorkshopProtocol.BuildUnoffered(530490512));
            Assert.Equal(Hex("180120a282f0a6c408"), WorkshopProtocol.BuildReady(true, Customer));
            Assert.Equal(Hex("20a282f0a6c408"), WorkshopProtocol.BuildReady(false, Customer));
            Assert.Equal(Hex("08904e"), WorkshopProtocol.BuildPayment(10000));
            Assert.Empty(WorkshopProtocol.BuildPayment(0));
            Assert.Equal(Hex("0803"), WorkshopProtocol.BuildCommissionImpossible(WorkshopProtocol.TooFar));
        }

        [Fact]
        public void The_payment_is_announced_the_way_the_magus_saw_it()
        {
            Assert.Equal(Hex("184022012b22053130303030"), WorkshopProtocol.BuildPaymentLine(10000));
            Assert.Equal(Hex("10d20422012b22053130303030"), WorkshopProtocol.BuildPaymentInfo(10000));
            Assert.Equal(Hex("184022012b220435303030"), WorkshopProtocol.BuildPaymentLine(5000));
        }

        [Fact]
        public void The_other_ones_doing_is_flagged()
            => Assert.Equal(Hex("0890c9fafc011001"), WorkshopProtocol.BuildRemoved(530490512, remote: true));

        // ─── Who pays ───────────────────────────────────────────────────────────────────────

        private static (GameSession Crafter, GameSession Customer) Two(long crafterKamas, long customerKamas)
        {
            // Ending a commission saves both characters, and a world.db fresh out of
            // datos/world.zip -- the CI's -- still has the Characters table from before the
            // scroll columns, which the server adds when it starts. Brought up to date first, as
            // the server would.
            using (var connection = new Microsoft.Data.Sqlite.SqliteConnection(Jondo.Unity.Server.DatabaseManager.WorldConnectionString))
            {
                connection.Open();
                Jondo.Unity.Server.DatabaseManager.MoveScrollsOutOfTheBase(connection);
            }

            var crafter = GameSession.SinSocket();
            var customer = GameSession.SinSocket();
            crafter.State.CharacterId = 9_000_000_001;
            customer.State.CharacterId = 9_000_000_002;
            crafter.State.Kamas = crafterKamas;
            customer.State.Kamas = customerKamas;
            Assert.True(SessionRegistry.Register(crafter));
            Assert.True(SessionRegistry.Register(customer));
            return (crafter, customer);
        }

        private static Commission Between(GameSession crafter, GameSession customer)
        {
            var commission = new Commission
            {
                CrafterId = crafter.CharacterId, CustomerId = customer.CharacterId,
                InviterId = crafter.CharacterId, SkillId = 166, Accepted = true,
            };
            crafter.State.Commission = commission;
            customer.State.Commission = commission;
            crafter.State.Workshop = new WorkshopHandler.Bench { SkillId = 166, Magus = true, Commission = commission };
            return commission;
        }

        [Fact]
        public async Task A_ready_customer_pays_when_a_rune_went_on_their_item()
        {
            var (crafter, customer) = Two(66_240_774, 20_000);
            try
            {
                var commission = Between(crafter, customer);
                commission.Payment = 5000;
                commission.CustomerReady = true;
                commission.Worked = true;

                await CommissionHandler.EndAsync(commission);

                Assert.Equal(66_245_774, crafter.State.Kamas);
                Assert.Equal(15_000, customer.State.Kamas);
                Assert.Null(crafter.State.Commission);
                Assert.Null(customer.State.Commission);
                Assert.Null(crafter.State.Workshop);
            }
            finally
            {
                SessionRegistry.Unregister(crafter);
                SessionRegistry.Unregister(customer);
            }
        }

        [Fact]
        public async Task Nothing_is_paid_for_nothing_done_or_for_a_customer_not_ready()
        {
            var (crafter, customer) = Two(100, 20_000);
            try
            {
                var idle = Between(crafter, customer);
                idle.Payment = 5000;
                idle.CustomerReady = true;
                await CommissionHandler.EndAsync(idle);
                Assert.Equal(100, crafter.State.Kamas);

                var unready = Between(crafter, customer);
                unready.Payment = 5000;
                unready.Worked = true;
                await CommissionHandler.EndAsync(unready);
                Assert.Equal(100, crafter.State.Kamas);
                Assert.Equal(20_000, customer.State.Kamas);
            }
            finally
            {
                SessionRegistry.Unregister(crafter);
                SessionRegistry.Unregister(customer);
            }
        }
    }
}
