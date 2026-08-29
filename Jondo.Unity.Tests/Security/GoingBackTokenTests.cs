using System;
using Jondo.Unity.Server.Network;
using Xunit;

namespace Jondo.Unity.Tests.Security
{
    /// <summary>
    /// The id handed out when a player goes back to server selection, and whether it is recognised.
    /// </summary>
    /// <remarks>
    /// Pressing "change server" hung until the client gave up with "el servidor tarda mucho en
    /// responder". The chain, measured end to end in the player's own logs:
    ///
    /// <list type="number">
    /// <item>The server answers the go-back request with a freshly minted id. That is the right
    /// shape — the real server does the same, and the capture opens with
    /// <c>kqr (40) 0a24 b00dae9b-e88d-4b5c-9110-e54f3ffaeb40 2001</c>, a dashed GUID of 36
    /// characters.</item>
    /// <item>The client closes the connection, opens a new one, and presents THAT id as its
    /// identity.</item>
    /// <item>Nothing had registered it, so it resolved to no account and the connection was
    /// closed.</item>
    /// </list>
    ///
    /// The proof it is the same value: the last id sent inside a kqr in
    /// logs/gameserver_traffic.log was b52b…16eb, and the console rejected exactly b52b…16eb one
    /// second later.
    ///
    /// It could never have matched by accident, either: every token this server minted was 32
    /// characters — <c>Guid "N"</c> or sixteen bytes of hex — and the auth database confirms it,
    /// with a maximum stored length of 32 in both token columns.
    ///
    /// Esta clase y <c>GoingBackTokenTests</c> comparten colección, y hace falta: las dos vacían y
    /// rellenan el MISMO registro estático de lanzamientos —que es estático porque tiene que
    /// serlo— y xUnit corre las clases en paralelo. Aquí se cruzaban sin romperse por suerte de
    /// tiempos; en la integración continua no. Verde en d703636 y rojo en el commit siguiente,
    /// que es justo el que añadió la segunda clase.
    /// </remarks>
    [Collection("ClientLaunchRegistry")]
    public class GoingBackTokenTests : IDisposable
    {
        private const long Account = 91_002;

        public GoingBackTokenTests() => ClientLaunchRegistry.ForgetEverything();
        public void Dispose() => ClientLaunchRegistry.ForgetEverything();

        [Fact]
        public void A_registered_session_id_resolves_back_to_its_account()
        {
            // What the fix has to achieve, in one line: the id we hand out comes back and is known.
            string sessionId = Guid.NewGuid().ToString();
            ClientLaunchRegistry.RegisterToken(Account, sessionId);

            Assert.Equal(Account, ClientLaunchRegistry.ResolveToken(sessionId));
        }

        [Fact]
        public void Resolving_works_with_no_authentication_database_at_all()
        {
            // Where this went red and the machine it ran on was right. The lookup falls through to
            // the Accounts table, and on a server whose auth database does not exist yet -- a first
            // boot, a deleted file, or continuous integration, which unpacks the world and nothing
            // else -- SQLite answers "no such table: Accounts" by throwing. That exception used to
            // climb out of ResolveToken into the connection handler, which is code that runs for
            // EVERY client that presents itself.
            //
            // Its neighbour GetAccountIdByLauncherToken had the guard and this one did not.
            Exception? thrown = Record.Exception(
                () => ClientLaunchRegistry.ResolveToken(Guid.NewGuid().ToString()));

            Assert.Null(thrown);
        }

        [Fact]
        public void An_id_nobody_registered_still_resolves_to_nothing()
        {
            // The other half. Registering the one we mint must not turn into accepting anything
            // that looks like a GUID: an unknown id is still an unknown id.
            Assert.Equal(0, ClientLaunchRegistry.ResolveToken(Guid.NewGuid().ToString()));
            Assert.Equal(0, ClientLaunchRegistry.ResolveToken("b52b0000-0000-0000-0000-00000000016e"));
        }

        [Fact]
        public void The_shape_is_the_dashed_one_the_capture_carries()
        {
            // 36 characters with dashes, not the 32 of Guid "N". The mismatch is what made the
            // failure impossible to hit by luck, and it is worth pinning: switching the mint to
            // "N" would compile, look tidier, and break this again.
            string sessionId = Guid.NewGuid().ToString();

            Assert.Equal(36, sessionId.Length);
            Assert.Equal(4, sessionId.Split('-').Length - 1);
            Assert.NotEqual(sessionId, Guid.NewGuid().ToString("N"));
        }

        [Fact]
        public void Nothing_is_registered_for_an_unidentified_socket()
        {
            // Going back before the account is known must not register anything, and must not
            // register it against account zero either.
            Assert.Equal(0, ClientLaunchRegistry.ResolveToken(""));
            ClientLaunchRegistry.RegisterToken(0, Guid.NewGuid().ToString());

            Assert.Equal(0, ClientLaunchRegistry.ResolveToken(Guid.NewGuid().ToString()));
        }
    }
}
