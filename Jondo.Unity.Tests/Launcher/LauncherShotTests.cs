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
    /// Leaves a photo of the launcher window on disk, so it can be looked at.
    /// </summary>
    /// <remarks>
    /// It checks nothing: it is a tool. Avalonia knows how to paint onto an in-memory canvas with
    /// Skia, so one can see how the interface looks without opening the launcher or having a screen,
    /// which is the only way of working on the design with judgement instead of blindly.
    ///
    /// The photo comes out in <c>capturas-lanzador/</c>, next to the solution, and that directory is in the
    /// .gitignore: they are working images, not part of the project.
    ///
    /// It is asked for with:
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
            // With accounts inside, which is the screen seen 99 % of the time and the one that does not
            // come out in the other photos: without stored accounts, Jugar shows the empty state.
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
            // Isolated and on a dark background, to see whether it draws anything.
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
            // The cosmetics and the equipment skins load themselves the first time they are
            // asked for, so here they no longer have to be started by hand: without that the appearance
            // garments put in no skin and the portrait came out with what was underneath.

            // The SAME string the server sends the launcher, taken from the characters really in
            // the base: with their head, their equipment and their cosmetics. It is the only way
            // of seeing whether the portrait comes out whole, comes out naked or comes out as a colour strip.
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

            // Without a populated base there is nothing to look at, and this is a tool: it does not fail because of it.
            if (personajes.Count == 0) return;

            // And WITHOUT THE DOFUS CLIENT either, which is what broke continuous integration. The
            // portrait is drawn with the client's bones, and the client is not in the repo nor
            // can it be; on the GitHub machine there is not a single bundle, so Of() returned
            // null and the Assert below brought the check down with a «1 no bone bundle» that is not a
            // failure of the emulator but a machine with no game installed.
            if (!System.IO.File.Exists(System.IO.Path.Combine(
                    Jondo.Unity.Launcher.Paths.ClientContentDir, "Characters", "Bones",
                    "bones_assets_bone_1-9-static.bundle")))
            {
                return;
            }

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
            // One photo per direction, to be able to LOOK at which one comes out facing front. Today the portraits come out
            // from behind and not because anyone chose it: no humanoid rig brings
            // a bare «AnimStatique_<dir>», so NpcSprites falls back down its ladder and
            // keeps the array's first animation, which is direction 5 or 6 in 18 of the 19
            // breeds. This fixes nothing: it leaves the five on the table.

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

            // It is a tool: without those characters in the base there is nothing to look at and it does not fail.
            if (personajes.Count == 0)
            {
                System.Console.WriteLine(
                    "No hay ni KEKA-BRON ni DRAGON-LORD en la base: no hay nada que fotografiar.");
                return;
            }

            string carpeta = System.IO.Path.Combine(RaizDeLaSolucion(), "capturas-lanzador");
            System.IO.Directory.CreateDirectory(carpeta);

            // The eight, not the five. That {0,1,2,5,6} are allowed is measured on the
            // bundles, but asking for all of them is what turns that measurement into a check
            // instead of a copied assumption.
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
                    // One painter per direction: that way no cache and no data from the previous one
                    // can contaminate what is measured for this one.
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
                        // The rig does not bring it. It has drawn, yes, but with the fallback: storing it
                        // would be storing the same photo eight times and believing they are eight.
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
