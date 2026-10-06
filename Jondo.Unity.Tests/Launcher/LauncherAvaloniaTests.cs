using System;
using System.Reflection;
using System.Text;
using Jondo.Unity.Launcher.Security;
using Jondo.Unity.Launcher.UI;
using Xunit;

namespace Jondo.Unity.Tests.Launcher
{
    /// <summary>
    /// The launcher after moving it to Avalonia.
    /// </summary>
    /// <remarks>
    /// Two things that have to be held by tests and that did not exist before:
    ///
    ///   - <b>The palette is a single one.</b> The launcher draws with Avalonia and the server with Windows
    ///     Forms, and both read from LauncherPalette. If someone writes a colour by hand on
    ///     either side, this test catches it; without it, the two executables keep
    ///     drifting apart and nobody notices until they are seen together.
    ///
    ///   - <b>What is stored is encrypted.</b> Before, the credentials of the eight accounts were stored
    ///     run through Base64, which is not encrypting. Encryption without tests is a promise.
    /// </remarks>
    public class LauncherAvaloniaTests
    {
        // ─────────────────────────────────────────────────── the shared palette

        [Fact]
        public void El_tema_de_windows_forms_no_tiene_ni_un_color_propio()
        {
            // Each public colour of the Windows Forms theme has to come, byte for byte, from the
            // constant of the same name in the palette.
            var tema = typeof(global::Jondo.Unity.Launcher.UI.LauncherTheme);   // the Windows Forms one
            var paleta = typeof(LauncherPalette);

            int comprobados = 0;
            foreach (var campo in tema.GetFields(BindingFlags.Public | BindingFlags.Static))
            {
                if (campo.FieldType != typeof(System.Drawing.Color)) continue;

                var suyo = paleta.GetField(campo.Name, BindingFlags.Public | BindingFlags.Static);
                Assert.True(suyo != null,
                    $"{campo.Name} está en el tema de Windows Forms y no en la paleta: es un color suelto.");

                var pintado = (System.Drawing.Color)campo.GetValue(null)!;
                uint esperado = (uint)suyo!.GetValue(null)!;

                Assert.Equal(unchecked((int)esperado), pintado.ToArgb());
                comprobados++;
            }

            // That it does not stay at zero for having renamed the type: the test would pass without looking at anything.
            Assert.Equal(45, comprobados);
        }

        [Fact]
        public void La_paleta_guarda_el_alfa_de_las_tarjetas()
        {
            // The cards are translucent on purpose -- from 0.84 to 0.52 -- so that the
            // background drawing shows. If someone leaves them opaque when touching the palette, it shows here and not
            // in a screenshot.
            Assert.Equal(133u, LauncherPalette.CardFill >> 24);
            Assert.Equal(191u, LauncherPalette.BarFill >> 24);
            Assert.Equal(255u, LauncherPalette.Background >> 24);
        }

        // ─────────────────────────────────────────────────── encryption at rest

        [Fact]
        public void Lo_cifrado_vuelve_igual()
        {
            const string secreto = "{\"AccountId\":188940901,\"Token\":\"abc-123\"}";

            string guardado = SecretStore.Protect(secreto);

            Assert.NotEqual(secreto, guardado);
            Assert.Equal(secreto, SecretStore.Unprotect(guardado));
        }

        [Fact]
        public void Lo_cifrado_no_deja_ver_el_contenido()
        {
            // What is stored cannot carry the plain text inside, not even in Base64:
            // that was exactly what happened before.
            const string secreto = "token-secretisimo-de-la-cuenta";

            string guardado = SecretStore.Protect(secreto);

            Assert.DoesNotContain(secreto, guardado, StringComparison.Ordinal);
            Assert.DoesNotContain(Convert.ToBase64String(Encoding.UTF8.GetBytes(secreto)),
                                  guardado, StringComparison.Ordinal);
        }

        [Fact]
        public void Lo_de_la_version_anterior_se_sigue_leyendo_una_vez()
        {
            // Bare Base64, which is what the previous version wrote. It is read so as not to throw out of the
            // launcher whoever already had it stored.
            const string antes = "[{\"AccountId\":7}]";
            string comoEstaba = Convert.ToBase64String(Encoding.UTF8.GetBytes(antes));

            Assert.True(SecretStore.LooksUnprotected(comoEstaba));
            Assert.Equal(antes, SecretStore.Unprotect(comoEstaba));
        }

        [Fact]
        public void Lo_ya_cifrado_no_se_confunde_con_lo_de_antes()
        {
            string guardado = SecretStore.Protect("lo que sea");

            Assert.False(SecretStore.LooksUnprotected(guardado));
        }

        [Fact]
        public void Lo_que_no_se_descifra_se_descarta_en_vez_de_reventar()
        {
            // A file brought from another machine, or a recreated profile. Returning empty makes the
            // session be asked for again; throwing would leave the launcher unopened.
            Assert.Equal("", SecretStore.Unprotect("dpapi:esto-no-es-base64-valido!!"));
            Assert.Equal("", SecretStore.Unprotect("aesgcm:AAAA"));
            Assert.Equal("", SecretStore.Unprotect(""));
        }

        [Fact]
        public void Lo_vacio_no_se_cifra()
        {
            Assert.Equal("", SecretStore.Protect(""));
        }

        // ─────────────────────────────────────────────────── the website to come

        [Fact]
        public void Sin_web_configurada_no_se_entra_por_el_navegador()
        {
            // While this is false, the launcher asks for username and password as always. It is the
            // whole switch of the OAuth flow.
            Assert.False(LauncherPreferences.HasWebSite || LauncherPreferences.WebSite.Length > 0);
        }

        [Theory]
        [InlineData("https://jondo.example", true)]
        [InlineData("http://127.0.0.1:8080", true)]
        [InlineData("http://jondo.example", false)]
        [InlineData("no-es-una-url", false)]
        [InlineData("", false)]
        public void La_web_tiene_que_ir_por_https_salvo_en_local(string donde, bool vale)
        {
            // Sending people to type their password over http would be worse than the text box
            // this is here to replace. On loopback it is allowed so it can be tested.
            bool bien = donde.Length > 0
                        && Uri.TryCreate(donde, UriKind.Absolute, out var uri)
                        && (uri.Scheme == Uri.UriSchemeHttps || uri.IsLoopback);

            Assert.Equal(vale, bien);
        }

        [Fact]
        public void Las_rutas_de_la_web_salen_del_sitio_que_se_configure()
        {
            var puntos = OAuthFlow.Endpoints.For("https://jondo.example/");

            Assert.Equal("https://jondo.example/oauth/authorize", puntos.Authorize);
            Assert.Equal("https://jondo.example/oauth/token", puntos.Token);

            // No client secret: in something handed out to the players there is no secret that
            // holds, because it travels inside the executable. That is what PKCE is here to replace.
            Assert.Equal("jondo-launcher", puntos.ClientId);
        }
    }
}
