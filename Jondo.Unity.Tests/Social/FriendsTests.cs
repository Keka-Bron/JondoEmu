using System.Linq;
using System.Threading.Tasks;
using Jondo.Unity.Server;
using Jondo.Unity.Server.Handlers;
using Jondo.Unity.Server.Managers;
using Jondo.Unity.Server.Network;
using Jondo.Unity.Tests.Economy;
using Xunit;

namespace Jondo.Unity.Tests.Social
{
    /// <summary>
    /// The friends window's in-game lists between two players who are playing: a contact is
    /// one-way until both have added each other, a mutual friend is seen playing, and the blocked
    /// and ignored are kept away from whispers and the map's chat.
    /// </summary>
    [Collection("friends")]
    public sealed class FriendsTests
    {
        private const long AccountA = 8_870_000_001, AccountB = 8_870_000_002, AccountC = 8_870_000_003;
        private const long Map = 8_870_555_001;

        private static void Forget()
        {
            long[] all = { AccountA, AccountB, AccountC };
            foreach (long a in all)
            {
                Contacts.ForgetSession(a);
                foreach (long b in all)
                {
                    Contacts.Remove(a, b, Contacts.Contact);
                    Contacts.Remove(a, b, Contacts.Blocked);
                }
            }
        }

        private static byte[] ByName(string op, int field, string name)
            => ConnectionProtocol.Push(op, Pb.New().Msg(field, Pb.New().Msg(4, Pb.New().Str(1, name))).Build());

        private static byte[] ById(string op, int field, long account)
            => ConnectionProtocol.Push(op, Pb.New().Var(field, account).Build());

        /// <summary>
        /// «Avertir quand mes amis se connectent» (koe { f1 enable }): kept per account, and a
        /// character entering the world is announced -- info 143, «Pseudo (Personnage) est en
        /// ligne» -- to his mutual friends playing who asked for it; not to one who did not, nor to
        /// one who listed him without being listed back. koe went unanswered, and nobody heard.
        /// </summary>
        [Fact]
        public async Task A_friend_coming_in_is_announced_to_who_asked()
        {
            Forget();
            try
            {
                await using var b = await ClientPipe.OpenAsync(AccountB, 8_870_100_002, Map, "Amigo-Dos");
                await using var c = await ClientPipe.OpenAsync(AccountC, 8_870_100_003, Map, "Amigo-Tres");
                Contacts.Add(AccountA, AccountB, Contacts.Contact);
                Contacts.Add(AccountB, AccountA, Contacts.Contact);
                Contacts.Add(AccountC, AccountA, Contacts.Contact);   // A never listed C: one-way

                using (SessionContext.Push(b.Session))
                    FriendsHandler.SetConnectionWarnings(ConnectionProtocol.Push("koe", Pb.New().Var(1, 1).Build()));
                using (SessionContext.Push(c.Session))
                    FriendsHandler.SetConnectionWarnings(ConnectionProtocol.Push("koe", Pb.New().Var(1, 1).Build()));
                Assert.True(Contacts.WarnsOnConnection(AccountB));

                await using var a = await ClientPipe.OpenAsync(AccountA, 8_870_100_001, Map, "Amigo-Uno");
                await FriendsHandler.OnWorldEntryAsync(a.Session);
                Assert.Equal(ConnectionProtocol.BuildSystemMessage(FriendsHandler.FriendOnline,
                                 HouseHandler.AccountTagOf(AccountA).Name, "Amigo-Uno", "8870100001"),
                             await b.Next(Jondo.Unity.Protocol.Op.Lqn));
                await c.Quiet();

                // Switched off, B hears nothing the next time.
                using (SessionContext.Push(b.Session))
                    FriendsHandler.SetConnectionWarnings(ConnectionProtocol.Push("koe", Pb.New().Build()));
                Assert.False(Contacts.WarnsOnConnection(AccountB));
                await FriendsHandler.OnWorldEntryAsync(a.Session);
                await b.Quiet();
            }
            finally
            {
                Forget();
                Contacts.SetWarnsOnConnection(AccountB, false);
                Contacts.SetWarnsOnConnection(AccountC, false);
            }
        }

        private static ProtoMessage Parse(byte[] payload) => ProtoMessage.Parse(payload);

        private static ProtoMessage Sub(ProtoMessage message, int field)
            => ProtoMessage.Parse(message.Fields.First(f => f.FieldNumber == field).BytesValue);

        private static long Var(ProtoMessage message, int field)
            => message.Fields.Single(f => f.FieldNumber == field).VarIntValue;

        private static long[] Accounts(byte[] list, int listField, int accountField)
            => Parse(list).Fields.Where(f => f.FieldNumber == listField)
                .Select(f => Var(ProtoMessage.Parse(f.BytesValue), accountField)).ToArray();

        [Fact]
        public async Task A_contact_is_one_way_until_both_add_each_other()
        {
            Forget();
            await using var a = await ClientPipe.OpenAsync(AccountA, 8_870_100_001, Map, "Amigo-Uno");
            await using var b = await ClientPipe.OpenAsync(AccountB, 8_870_100_002, Map, "Amigo-Dos");
            b.Session.State.CharacterLevel = 57;
            b.Session.State.Breed = 8;

            using (SessionContext.Push(a.Session))
            {
                await FriendsHandler.AddContactAsync(a.ToClient, ByName("koq", 2, "amigo-dos"));
                var kpr = Sub(Parse(await a.Next("koh")), 1);
                Assert.Equal(AccountB, Var(kpr, 2));
                Assert.Equal(3, Var(kpr, 5));

                await FriendsHandler.SendMutualAsync(a.ToClient);
                Assert.Empty(Accounts(await a.Next("kqg"), 1, 1));
                await FriendsHandler.SendContactsAsync(a.ToClient);
                Assert.Equal(new[] { AccountB }, Accounts(await a.Next("koj"), 2, 2));

                // Twice is refused: already in the list.
                await FriendsHandler.AddContactAsync(a.ToClient, ByName("koq", 2, "Amigo-Dos"));
                Assert.Equal(FriendsHandler.AlreadyInList, Var(Parse(await a.Next("kqc")), 2));
            }

            using (SessionContext.Push(b.Session))
            {
                await FriendsHandler.AddContactAsync(b.ToClient, ByName("koq", 2, "Amigo-Uno"));
                Assert.Equal(new[] { AccountA }, Accounts(await b.Next("kqg"), 1, 1));
                Assert.Empty(Accounts(await b.Next("koj"), 2, 2));
            }

            // A sees B playing: his character, level and class.
            var kqg = await a.Next("kqg");
            var friend = Sub(Parse(kqg), 1);
            Assert.Equal(AccountB, Var(friend, 1));
            Assert.Equal(1, Var(friend, 6));
            var playing = Sub(friend, 5);
            Assert.Equal(57, Var(playing, 5));
            Assert.Equal(8, Var(playing, 9));
            Assert.Equal(8_870_100_002, Var(playing, 11));
            Assert.Empty(Accounts(await a.Next("koj"), 2, 2));

            // A takes B away: B keeps his own contact, one-way again.
            using (SessionContext.Push(a.Session))
            {
                await FriendsHandler.RemoveContactAsync(a.ToClient, ById("kog", 2, AccountB));
                var kpi = Parse(await a.Next("kpi"));
                Assert.Equal(1, Var(kpi, 1));
            }
            Assert.Empty(Accounts(await b.Next("kqg"), 1, 1));
            Assert.Equal(new[] { AccountA }, Accounts(await b.Next("koj"), 2, 2));
            Forget();
        }

        [Fact]
        public async Task Blocking_is_refused_twice_on_oneself_and_on_nobody()
        {
            Forget();
            await using var a = await ClientPipe.OpenAsync(AccountA, 8_870_100_011, Map, "Bloquea-Uno");
            await using var c = await ClientPipe.OpenAsync(AccountC, 8_870_100_013, Map, "Bloquea-Tres");

            using (SessionContext.Push(a.Session))
            {
                await FriendsHandler.BlockAsync(a.ToClient, ByName("kok", 1, "Bloquea-Tres"));
                var kph = Sub(Sub(Parse(await a.Next("knv")), 2), 1);
                Assert.Equal(AccountC, Var(kph, 4));

                await FriendsHandler.BlockAsync(a.ToClient, ByName("kok", 1, "Bloquea-Tres"));
                Assert.Equal(FriendsHandler.AlreadyInList, Var(Sub(Parse(await a.Next("knv")), 1), 3));
                await FriendsHandler.BlockAsync(a.ToClient, ByName("kok", 1, "Bloquea-Uno"));
                Assert.Equal(FriendsHandler.Egocentric, Var(Sub(Parse(await a.Next("knv")), 1), 3));
                await FriendsHandler.BlockAsync(a.ToClient, ByName("kok", 1, "Nadie-Se-Llama-Asi"));
                Assert.Equal(FriendsHandler.NotFound, Var(Sub(Parse(await a.Next("knv")), 1), 3));

                // A blocked player cannot be a contact as well.
                await FriendsHandler.AddContactAsync(a.ToClient, ByName("koq", 2, "Bloquea-Tres"));
                Assert.Equal(FriendsHandler.Conflicting, Var(Parse(await a.Next("kqc")), 2));

                await FriendsHandler.SendBlockedAsync(a.ToClient);
                Assert.Equal(new[] { AccountC }, Accounts(await a.Next("koi"), 2, 4));

                await FriendsHandler.UnblockAsync(a.ToClient, ById("kqd", 1, AccountC));
                Assert.Contains(Parse(await a.Next("knk")).Fields, f => f.FieldNumber == 3);
                await FriendsHandler.SendBlockedAsync(a.ToClient);
                Assert.Empty(Accounts(await a.Next("koi"), 2, 4));
            }
            Forget();
        }

        [Fact]
        public async Task The_ignored_and_the_blocked_are_not_heard()
        {
            Forget();
            await using var a = await ClientPipe.OpenAsync(AccountA, 8_870_100_021, Map, "Oye-Uno");
            await using var b = await ClientPipe.OpenAsync(AccountB, 8_870_100_022, Map, "Oye-Dos");

            using (SessionContext.Push(b.Session))
            {
                await FriendsHandler.IgnoreAsync(b.ToClient, ByName("kpa", 1, "Oye-Uno"));
                var kph = Sub(Sub(Parse(await b.Next("kpy")), 1), 1);
                Assert.Equal(AccountA, Var(kph, 4));
            }

            // A whispers to B, who ignores him: refused, and B gets nothing.
            using (SessionContext.Push(a.Session))
            {
                await PrivateMessageHandler.WhisperAsync(a.ToClient,
                    ConnectionProtocol.Push("ktb", Pb.New().Str(1, "hola").Str(5, "Oye-Dos").Build()));
                await a.Next("ktl");
            }
            using (SessionContext.Push(b.Session))
            {
                // The first thing B gets is his own answer: the whisper never reached him.
                await FriendsHandler.UnignoreAsync(b.ToClient, ById("kpl", 1, AccountA));
                Assert.Contains(Parse(await b.Next("kox")).Fields, f => f.FieldNumber == 1);
                Assert.False(Contacts.IsIgnored(AccountB, AccountA));

                await FriendsHandler.BlockAsync(b.ToClient, ByName("kok", 1, "Oye-Uno"));
                await b.Next("knv");
            }

            // Blocked: the map's chat does not reach him either.
            Assert.Equal(0, await SessionRegistry.BroadcastToMapAsync(Map,
                ConnectionProtocol.Push("kti", new byte[0]), a.Session.Id, null, AccountA));
            Forget();
        }
    }
}
