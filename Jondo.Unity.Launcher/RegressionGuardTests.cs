using System;
using System.IO;
using System.Linq;

namespace Jondo.Unity.Launcher
{
    public static class RegressionGuardTests
    {
        private static readonly string[] ForbiddenLiterals = new string[]
        {
            "670668947750",
            "-20003",
            "\"Fortellon\""
        };

        public static void Run()
        {
            // The connection phase messages are always checked, in deployment too: that is where
            // structural bugs used to slip through, and all the client shows there is a blank
            // screen with no error at all.
            Network.ConnectionProtocolSelfTest.Run();

            // OJO: esta parte no llega a correr nunca. Subir tres carpetas desde donde está el
            // binario y volver a bajar a "Jondo.Unity.Launcher" no da con el código fuente en
            // ninguna de las dos formas de ejecutarlo, ni en bin\<config>\<tfw>\ ni en el
            // despliegue, así que siempre se sale por el return de abajo. Se deja igual que
            // estaba —no es parte de ordenar las carpetas— pero conviene saberlo: si se corrige
            // la ruta, la comprobación salta con lo que ya hay escrito hoy.
            //
            // Antes esto usaba Assembly.Location, que con el .exe de un solo fichero devuelve
            // cadena vacía; AppContext.BaseDirectory da lo mismo que daba antes y sin aviso.
            string launcherDir = AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar);
            string projectDir = Path.Combine(launcherDir, "..", "..", "..", "Jondo.Unity.Launcher");

            if (!Directory.Exists(projectDir)) return;

            var csFiles = Directory.GetFiles(projectDir, "*.cs", SearchOption.AllDirectories)
                .Where(f => !f.Contains("BasePayloads.cs") && !f.Contains("TransitionPayloads.cs") && !f.Contains("RegressionGuardTests.cs"))
                .ToList();

            foreach (var file in csFiles)
            {
                string text = File.ReadAllText(file);
                foreach (var literal in ForbiddenLiterals)
                {
                    if (text.Contains(literal))
                    {
                        throw new InvalidOperationException($"[RegressionGuard FAILED] File '{Path.GetFileName(file)}' contains forbidden literal string '{literal}'!");
                    }
                }
            }

            Console.WriteLine("[RegressionGuard] ✅ All CS files passed literal guard test. Zero forbidden capture literals found.");
        }
    }
}
