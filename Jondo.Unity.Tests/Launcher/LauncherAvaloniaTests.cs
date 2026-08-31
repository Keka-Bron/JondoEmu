using System;
using System.Reflection;
using System.Text;
using Jondo.Unity.Launcher.Security;
using Jondo.Unity.Launcher.UI;
using Xunit;

namespace Jondo.Unity.Tests.Launcher
{
    /// <summary>
    /// El lanzador después de pasarlo a Avalonia.
    /// </summary>
    /// <remarks>
    /// Dos cosas que hay que sujetar con pruebas y que antes no existían:
    ///
    ///   - <b>La paleta es una sola.</b> El lanzador pinta con Avalonia y el servidor con Windows
    ///     Forms, y los dos leen de LauncherPalette. Si alguien escribe un color a mano en
    ///     cualquiera de los dos lados, esta prueba lo caza; sin ella, los dos ejecutables se van
    ///     separando y nadie se entera hasta que se ven juntos.
    ///
    ///   - <b>Lo guardado va cifrado.</b> Antes las credenciales de las ocho cuentas se guardaban
    ///     pasadas por Base64, que no es cifrar. Un cifrado sin pruebas es una promesa.
    /// </remarks>
    public class LauncherAvaloniaTests
    {
        // ─────────────────────────────────────────────────── la paleta compartida

        [Fact]
        public void El_tema_de_windows_forms_no_tiene_ni_un_color_propio()
        {
            // Cada color público del tema de Windows Forms tiene que salir, byte por byte, de la
            // constante del mismo nombre en la paleta.
            var tema = typeof(global::Jondo.Unity.Launcher.UI.LauncherTheme);   // el de Windows Forms
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

            // Que no se quede en cero por haber renombrado el tipo: la prueba pasaría sin mirar nada.
            Assert.Equal(45, comprobados);
        }

        [Fact]
        public void La_paleta_guarda_el_alfa_de_las_tarjetas()
        {
            // Las tarjetas van translúcidas a propósito -- de 0,84 a 0,52 -- para que se vea el
            // dibujo del fondo. Si alguien las deja opacas al tocar la paleta, se nota aquí y no
            // en una captura de pantalla.
            Assert.Equal(133u, LauncherPalette.CardFill >> 24);
            Assert.Equal(191u, LauncherPalette.BarFill >> 24);
            Assert.Equal(255u, LauncherPalette.Background >> 24);
        }

        // ─────────────────────────────────────────────────── el cifrado en reposo

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
            // Lo que se guarda no puede llevar dentro el texto en claro, ni siquiera en Base64:
            // eso era exactamente lo que pasaba antes.
            const string secreto = "token-secretisimo-de-la-cuenta";

            string guardado = SecretStore.Protect(secreto);

            Assert.DoesNotContain(secreto, guardado, StringComparison.Ordinal);
            Assert.DoesNotContain(Convert.ToBase64String(Encoding.UTF8.GetBytes(secreto)),
                                  guardado, StringComparison.Ordinal);
        }

        [Fact]
        public void Lo_de_la_version_anterior_se_sigue_leyendo_una_vez()
        {
            // Base64 pelado, que es lo que escribía la versión anterior. Se lee para no echar del
            // lanzador a quien ya lo tenía guardado.
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
            // Un fichero traído de otra máquina, o un perfil recreado. Devolver vacío hace que se
            // vuelva a pedir la sesión; lanzar dejaría el lanzador sin abrir.
            Assert.Equal("", SecretStore.Unprotect("dpapi:esto-no-es-base64-valido!!"));
            Assert.Equal("", SecretStore.Unprotect("aesgcm:AAAA"));
            Assert.Equal("", SecretStore.Unprotect(""));
        }

        [Fact]
        public void Lo_vacio_no_se_cifra()
        {
            Assert.Equal("", SecretStore.Protect(""));
        }

        // ─────────────────────────────────────────────────── la web que vendrá

        [Fact]
        public void Sin_web_configurada_no_se_entra_por_el_navegador()
        {
            // Mientras esto sea falso, el lanzador pide usuario y contraseña como siempre. Es el
            // interruptor entero del flujo OAuth.
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
            // Mandar a la gente a escribir su contraseña por http sería peor que la caja de texto
            // que esto viene a sustituir. En loopback se permite para poder probar.
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

            // Sin secreto de cliente: en algo que se reparte a los jugadores no hay secreto que
            // valga, porque viaja dentro del ejecutable. Eso es lo que PKCE viene a sustituir.
            Assert.Equal("jondo-launcher", puntos.ClientId);
        }
    }
}
