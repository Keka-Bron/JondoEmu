using Jondo.Unity.Server.Managers;
using Xunit;

namespace Jondo.Unity.Tests.Security
{
    /// <summary>
    /// Passwords are hashed, and the ones stored before still get in.
    /// </summary>
    /// <remarks>
    /// They used to be stored exactly as typed and compared by SQL itself. The migration matters as
    /// much as the hashing: a password still sitting in the clear has to work ONCE, and say so, or
    /// every account created before the change is locked out.
    /// </remarks>
    public class PasswordTests
    {
        [Fact]
        public void The_hash_does_not_contain_the_password()
        {
            Assert.DoesNotContain("perro verde", Claves.Cifrar("perro verde"));
        }

        [Fact]
        public void A_freshly_hashed_password_is_recognised_and_needs_no_rewrite()
        {
            string hashed = Claves.Cifrar("perro verde");

            Assert.True(Claves.Comprueba("perro verde", hashed, out bool rewrite));
            Assert.False(rewrite);
        }

        [Fact]
        public void A_wrong_password_is_refused()
        {
            string hashed = Claves.Cifrar("perro verde");

            Assert.False(Claves.Comprueba("perro rojo", hashed, out _));
        }

        [Fact]
        public void The_same_password_hashes_differently_every_time()
        {
            // Without salt, one rainbow table lifts every account at once.
            Assert.NotEqual(Claves.Cifrar("perro verde"), Claves.Cifrar("perro verde"));
        }

        [Fact]
        public void A_password_still_in_the_clear_works_once_and_asks_to_be_rewritten()
        {
            Assert.True(Claves.Comprueba("test", "test", out bool convert));
            Assert.True(convert);
        }

        [Theory]
        [InlineData("")]
        [InlineData(" ")]
        [InlineData("ñÁ€ 汉字")]
        [InlineData("a very long one 0123456789012345678901234567890123456789012345678901234567890123456789")]
        public void Odd_passwords_survive_a_round_trip(string password)
        {
            // Non-ASCII especially: the launcher takes whatever the keyboard gives it, and an
            // encoding mismatch between hashing and checking locks the account silently.
            string hashed = Claves.Cifrar(password);

            Assert.True(Claves.Comprueba(password, hashed, out _));
        }
    }
}
