using System;
using System.Linq;
using System.Threading.Tasks;
using Jondo.Unity.Server;
using Jondo.Unity.Server.Handlers;
using Jondo.Unity.Server.Network;
using Jondo.Unity.Tests.Economy;
using Xunit;

namespace Jondo.Unity.Tests.Social
{
    /// <summary>«Informaciones» on a player's menu: lqw answered lqq and lqe, as the friends capture's frames 59-61.</summary>
    public sealed class WhoisTests
    {
        private const long AskerAccount = 8_900_000_073, TargetAccount = 8_900_000_074;

        private static string Hex(byte[] bytes) => Convert.ToHexString(bytes).ToLowerInvariant();

        [Fact]
        public void The_date_is_the_game_s_calendar_as_the_capture_s()
            => Assert.Equal("1090051807200a", Hex(WhoisHandler.BuildDate(new DateTime(2026, 8, 10, 10, 50, 5))));

        [Fact]
        public async Task Asking_by_name_gets_the_date_and_who_he_is()
        {
            long target = DatabaseManager.CreateCharacter(TargetAccount, 1, "Quien-Soy", 3, 0, 1,
                new long[] { -1, -1, -1, -1, -1 }, 191105026, 50, 0, 0, Array.Empty<(int, int)>());
            Assert.True(target > 0);
            try
            {
                // A map of our own: parallel map-transition tests broadcast kmu on the real map.
                await using var asker = await ClientPipe.OpenAsync(AskerAccount, 8_900_100_073, 990_100_173, "Pregunta-Uno");
                byte[] ask = ConnectionProtocol.Push("lqw", Pb.New()
                    .Msg(2, Pb.New().Msg(4, Pb.New().Str(1, "quien-soy"))).Var(3, 1).Build());

                // Not playing: «disconnected», no area.
                using (SessionContext.Push(asker.Session))
                    await WhoisHandler.AskAsync(asker.ToClient, ask);
                await asker.Next("lqq");
                var lqe = ProtoMessage.Parse(await asker.Next("lqe")).Fields;
                Assert.Equal(target, lqe.Single(f => f.FieldNumber == 7).VarIntValue);
                Assert.Equal(TargetAccount, lqe.Single(f => f.FieldNumber == 9).VarIntValue);
                Assert.Equal("Quien-Soy", System.Text.Encoding.UTF8.GetString(lqe.Single(f => f.FieldNumber == 14).BytesValue));
                Assert.DoesNotContain(lqe, f => f.FieldNumber == 3);
                Assert.Equal(-1, (long)lqe.Single(f => f.FieldNumber == 13).VarIntValue);
                Assert.Equal(WhoisHandler.PlayerRole, (int)lqe.Single(f => f.FieldNumber == 10).VarIntValue);

                // Playing: connected.
                await using var playing = await ClientPipe.OpenAsync(TargetAccount, target, 191105026, "Quien-Soy");
                using (SessionContext.Push(asker.Session))
                    await WhoisHandler.AskAsync(asker.ToClient, ask);
                await asker.Next("lqq");
                lqe = ProtoMessage.Parse(await asker.Next("lqe")).Fields;
                Assert.Equal(1, lqe.Single(f => f.FieldNumber == 3).VarIntValue);
            }
            finally
            {
                DatabaseManager.DeleteCharacter(target, TargetAccount);
            }
        }
    }
}
