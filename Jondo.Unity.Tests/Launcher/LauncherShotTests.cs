using System;
using System.IO;
using Avalonia.Controls;
using Button = Avalonia.Controls.Button;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Jondo.Unity.Launcher.UI;
using Xunit;

namespace Jondo.Unity.Tests.Launcher
{
    /// <summary>
    /// Deja una foto de la ventana del lanzador en disco, para poder mirarla.
    /// </summary>
    /// <remarks>
    /// No comprueba nada: es una herramienta. Avalonia sabe pintar sobre un lienzo en memoria con
    /// Skia, así que se puede ver cómo queda la interfaz sin abrir el lanzador ni tener pantalla,
    /// que es la única forma de trabajar el diseño con criterio en vez de a ciegas.
    ///
    /// La foto sale en <c>capturas-lanzador/</c>, junto a la solución, y ese directorio está en el
    /// .gitignore: son imágenes de trabajo, no parte del proyecto.
    ///
    /// Se pide con:
    ///
    ///   dotnet test --filter "FullyQualifiedName~LauncherShotTests"
    /// </remarks>
    public class LauncherShotTests
    {
        [AvaloniaTheory]
        [InlineData(1600, 900)]
        [InlineData(1280, 720)]
        public void Una_foto_de_la_ventana(int ancho, int alto)
        {
            var ventana = new MainWindow { Width = ancho, Height = alto };
            ventana.Show();

            var lienzo = ventana.CaptureRenderedFrame();
            Assert.NotNull(lienzo);

            string carpeta = Path.Combine(RaizDeLaSolucion(), "capturas-lanzador");
            Directory.CreateDirectory(carpeta);
            string destino = Path.Combine(carpeta, $"lanzador-{ancho}x{alto}.png");

            lienzo!.Save(destino);
            ventana.Close();

            Assert.True(new FileInfo(destino).Length > 0);
        }

        [AvaloniaTheory]
        [InlineData("SeccionCuentas", "cuentas")]
        [InlineData("SeccionAjustes", "ajustes")]
        public void Una_foto_de_cada_seccion(string boton, string comoSeLlama)
        {
            var ventana = new MainWindow { Width = 1280, Height = 800 };
            ventana.Show();

            var pestana = ventana.GetControl<Button>(boton);
            Assert.NotNull(pestana);
            pestana!.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(
                Button.ClickEvent));

            string carpeta = System.IO.Path.Combine(RaizDeLaSolucion(), "capturas-lanzador");
            System.IO.Directory.CreateDirectory(carpeta);
            ventana.CaptureRenderedFrame()!
                   .Save(System.IO.Path.Combine(carpeta, $"seccion-{comoSeLlama}.png"));
            ventana.Close();
        }

        [AvaloniaFact]
        public void Una_foto_de_jugar_con_equipo()
        {
            // Con cuentas dentro, que es la pantalla que se ve el 99 % de las veces y la que no
            // sale en las otras fotos: sin cuentas guardadas, Jugar enseña el estado vacío.
            var ventana = new MainWindow { Width = 1280, Height = 800 };
            ventana.Show();

            ventana.MeterCuentasDeMentira(3);

            string carpeta = System.IO.Path.Combine(RaizDeLaSolucion(), "capturas-lanzador");
            System.IO.Directory.CreateDirectory(carpeta);
            ventana.CaptureRenderedFrame()!
                   .Save(System.IO.Path.Combine(carpeta, "seccion-jugar.png"));
            ventana.Close();
        }

        [AvaloniaFact]
        public void Una_foto_del_rotulo_solo()
        {
            // Aislado y sobre un fondo oscuro, para ver si dibuja algo.
            var ventana = new Avalonia.Controls.Window
            {
                Width = 400, Height = 160,
                Background = Avalonia.Media.Brushes.Black,
                Content = new Jondo.Unity.Launcher.UI.Widgets.LogoBanner
                {
                    Width = 340, Height = 120, ConArranque = false,
                },
            };
            ventana.Show();
            var lienzo = ventana.CaptureRenderedFrame();
            string carpeta = System.IO.Path.Combine(RaizDeLaSolucion(), "capturas-lanzador");
            System.IO.Directory.CreateDirectory(carpeta);
            lienzo!.Save(System.IO.Path.Combine(carpeta, "rotulo.png"));
            ventana.Close();
        }

        [AvaloniaFact]
        public void Una_foto_del_retrato_de_cada_personaje()
        {
            // Los cosméticos NO se cargan solos: el servidor los carga al arrancar y aquí no hay
            // arranque. Sin esto las prendas de apariencia no meten piel y el retrato sale con lo
            // de debajo, que es justo lo que hay que poder mirar.
            Jondo.Unity.Server.Managers.Cosmetics.Initialize();
            Jondo.Unity.Server.Managers.EquipmentSkins.Initialize();

            // La MISMA cadena que manda el servidor al lanzador, sacada de los personajes que hay
            // de verdad en la base: con su cabeza, su equipo y sus cosméticos. Es la única forma
            // de ver si el retrato sale entero, sale desnudo o sale una tira de color.
            var personajes = new System.Collections.Generic.List<
                Jondo.Unity.Server.DatabaseManager.DbCharacter>();

            using (var conexion = new Microsoft.Data.Sqlite.SqliteConnection(
                       Jondo.Unity.Server.DatabaseManager.WorldConnectionString))
            {
                conexion.Open();
                var consulta = conexion.CreateCommand();
                consulta.CommandText = "SELECT Id FROM Characters ORDER BY Id;";
                using var lector = consulta.ExecuteReader();
                while (lector.Read())
                {
                    var quien = Jondo.Unity.Server.DatabaseManager
                        .GetCharacterById(lector.GetInt64(0));
                    if (quien != null) personajes.Add(quien);
                }
            }

            // Sin base poblada no hay nada que mirar, y esto es una herramienta: no falla por eso.
            if (personajes.Count == 0) return;

            using var pintor = new Jondo.Unity.Sprites.NpcSprites();
            string carpeta = System.IO.Path.Combine(RaizDeLaSolucion(), "capturas-lanzador");
            System.IO.Directory.CreateDirectory(carpeta);

            foreach (var quien in personajes)
            {
                string look = Jondo.Unity.Server.Managers.BreedLookTable.Drawable(quien);
                if (look.Length == 0) continue;

                var retrato = pintor.Of(look);
                Assert.True(retrato != null,
                    $"{quien.Name}: «{look}» no se ha podido dibujar. {pintor.Trouble} {pintor.Reasons()}");

                string limpio = quien.Name.Replace("[", "").Replace("]", "").Replace("#", "");
                retrato!.Save(System.IO.Path.Combine(carpeta, $"retrato-{limpio}.png"));
                System.Console.WriteLine($"{quien.Name}: {look}");
            }
        }

        [AvaloniaFact]
        public void Una_foto_de_cada_direccion()
        {
            // Una foto por dirección, para poder MIRAR cuál sale de frente. Hoy los retratos salen
            // de espaldas y no porque nadie lo haya elegido: ningún rig humanoide trae
            // «AnimStatique_<dir>» a secas, así que NpcSprites cae por su escalera de reserva y se
            // queda con la primera animación del array, que es de dirección 5 o 6 en 18 de las 19
            // razas. Esto no arregla nada: deja las cinco sobre la mesa.
            Jondo.Unity.Server.Managers.Cosmetics.Initialize();
            Jondo.Unity.Server.Managers.EquipmentSkins.Initialize();

            var personajes = new System.Collections.Generic.List<
                Jondo.Unity.Server.DatabaseManager.DbCharacter>();

            using (var conexion = new Microsoft.Data.Sqlite.SqliteConnection(
                       Jondo.Unity.Server.DatabaseManager.WorldConnectionString))
            {
                conexion.Open();
                var consulta = conexion.CreateCommand();
                consulta.CommandText =
                    "SELECT Id FROM Characters WHERE Name LIKE '%KEKA-BRON%' " +
                    "OR Name LIKE '%DRAGON-LORD%' ORDER BY Id;";
                using var lector = consulta.ExecuteReader();
                while (lector.Read())
                {
                    var quien = Jondo.Unity.Server.DatabaseManager
                        .GetCharacterById(lector.GetInt64(0));
                    if (quien != null) personajes.Add(quien);
                }
            }

            // Es una herramienta: sin esos personajes en la base no hay nada que mirar y no falla.
            if (personajes.Count == 0)
            {
                System.Console.WriteLine(
                    "No hay ni KEKA-BRON ni DRAGON-LORD en la base: no hay nada que fotografiar.");
                return;
            }

            string carpeta = System.IO.Path.Combine(RaizDeLaSolucion(), "capturas-lanzador");
            System.IO.Directory.CreateDirectory(carpeta);

            // Las ocho, no las cinco. Que estén autorizadas {0,1,2,5,6} está medido sobre los
            // bundles, pero preguntarlas todas es lo que convierte esa medición en una comprobación
            // en vez de en una suposición copiada.
            foreach (var quien in personajes)
            {
                string look = Jondo.Unity.Server.Managers.BreedLookTable.Drawable(quien);
                if (look.Length == 0)
                {
                    System.Console.WriteLine($"{quien.Name}: sin cadena de aspecto, me lo salto.");
                    continue;
                }

                string limpio = quien.Name.Replace("[", "").Replace("]", "").Replace("#", "");
                var salieron = new System.Collections.Generic.List<string>();
                var faltan = new System.Collections.Generic.List<string>();

                System.Console.WriteLine($"--- {quien.Name}: {look}");

                for (int direccion = 0; direccion <= 7; direccion++)
                {
                    // Un pintor por dirección: así ninguna caché ni ningún dato de la anterior
                    // puede contaminar lo que se mide de ésta.
                    using var pintor = new Jondo.Unity.Sprites.NpcSprites
                    {
                        Direction = direccion,
                    };

                    var retrato = pintor.Of(look);

                    if (retrato == null)
                    {
                        faltan.Add($"{direccion} (no dibuja: {pintor.Trouble} {pintor.Reasons()})");
                        continue;
                    }

                    if (!pintor.LastDirectionFound)
                    {
                        // El rig no la trae. Ha dibujado, sí, pero con la de reserva: guardarla
                        // sería guardar la misma foto ocho veces y creerse que son ocho.
                        faltan.Add($"{direccion} (el rig no la trae; habría caído en «{pintor.LastAnimation}»)");
                        continue;
                    }

                    string destino = System.IO.Path.Combine(
                        carpeta, $"direccion-{direccion}-{limpio}.png");
                    retrato.Save(destino);

                    salieron.Add($"{direccion} → {pintor.LastAnimation} " +
                                 $"({retrato.PixelSize.Width}×{retrato.PixelSize.Height})");
                }

                System.Console.WriteLine($"{quien.Name}: SALEN     {string.Join(" | ", salieron)}");
                System.Console.WriteLine($"{quien.Name}: NO SALEN  {string.Join(" | ", faltan)}");
            }
        }

        private static string RaizDeLaSolucion()
        {
            var carpeta = new DirectoryInfo(AppContext.BaseDirectory);
            while (carpeta != null && !File.Exists(Path.Combine(carpeta.FullName, "Jondo.Unity.sln")))
            {
                carpeta = carpeta.Parent;
            }
            return carpeta?.FullName ?? AppContext.BaseDirectory;
        }
    }
}
