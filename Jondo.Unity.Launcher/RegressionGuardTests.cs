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
            string launcherDir = Path.GetDirectoryName(typeof(RegressionGuardTests).Assembly.Location) ?? "";
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
