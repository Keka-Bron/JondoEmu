using System.Diagnostics;
using System.Text;
using System.Text.RegularExpressions;
using ModelContextProtocol;

namespace Jondo.Unity.Mcp;

/// <summary>
/// dotnet test on a checkout, as by hand but without what went wrong by hand: the checkout as
/// JONDO_EMULATOR_ROOT (its content is what is tested), its bases/ filled with copies of the data
/// root's databases when it has none (a worktree's bases/ is not in git: an empty world.db made by
/// a failed fixture affected every related test), and the build where the tests look for the repository's
/// files (with --artifacts-path in %TEMP%, AdminMenuTests did not find JondoFix/Admin/*.xml).
/// </summary>
internal static class TestRunner
{
    private static readonly string[] Databases = { "world.db", "auth.db", "paquetes.db" };

    /// <summary>A world.db smaller than this is one the tests made empty, not the world's.</summary>
    private const long LeastWorld = 10L * 1024 * 1024;

    public static async Task<string> RunAsync(Emulator emulator, string checkout, string? filter, int maxFailures,
                                              CancellationToken cancellationToken)
    {
        string project = Path.Combine(checkout, "Jondo.Unity.Tests", "Jondo.Unity.Tests.csproj");
        if (!File.Exists(project)) throw new McpException($"No test project at {project}.");
        var said = new List<string>();
        said.AddRange(FillBases(emulator.DataRoot, checkout));

        var start = new ProcessStartInfo("dotnet") { WorkingDirectory = checkout, UseShellExecute = false, CreateNoWindow = true };
        start.ArgumentList.Add("test");
        start.ArgumentList.Add(project);
        if (!string.IsNullOrWhiteSpace(filter))
        {
            start.ArgumentList.Add("--filter");
            start.ArgumentList.Add(filter);
        }
        start.RedirectStandardOutput = true;
        start.RedirectStandardError = true;
        start.Environment["JONDO_EMULATOR_ROOT"] = checkout;

        var output = new StringBuilder();
        using var process = new Process { StartInfo = start };
        process.OutputDataReceived += (_, e) => { if (e.Data != null) lock (output) output.AppendLine(e.Data); };
        process.ErrorDataReceived += (_, e) => { if (e.Data != null) lock (output) output.AppendLine(e.Data); };
        process.Start();
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();
        using (var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken))
        {
            timeout.CancelAfter(TimeSpan.FromMinutes(40));
            try { await process.WaitForExitAsync(timeout.Token); }
            catch (OperationCanceledException)
            {
                try { process.Kill(entireProcessTree: true); } catch (InvalidOperationException) { }
                throw new McpException(cancellationToken.IsCancellationRequested ? "Cancelled." : "The tests ran over 40 minutes: stopped.");
            }
        }

        string all;
        lock (output) all = output.ToString();
        return string.Join('\n', said.Append(Summary(all, maxFailures)).Where(s => s.Length > 0));
    }

    /// <summary>The data root's databases copied into the checkout's bases/, each where it is missing or empty.</summary>
    private static IEnumerable<string> FillBases(string dataRoot, string checkout)
    {
        string from = Path.Combine(dataRoot, "bases"), to = Path.Combine(checkout, "bases");
        if (string.Equals(Path.GetFullPath(from), Path.GetFullPath(to), StringComparison.OrdinalIgnoreCase)) yield break;
        Directory.CreateDirectory(to);
        foreach (string name in Databases)
        {
            string source = Path.Combine(from, name), target = Path.Combine(to, name);
            if (!File.Exists(source)) continue;
            bool missing = !File.Exists(target) || name == "world.db" && new FileInfo(target).Length < LeastWorld;
            if (!missing) continue;
            foreach (string side in new[] { target + "-wal", target + "-shm" })
                if (File.Exists(side)) File.Delete(side);
            File.Copy(source, target, overwrite: true);
            yield return $"{name} copied into {to} from the data root.";
        }
    }

    private static readonly Regex FailedLine = new(@"^\s+Failed (?<name>\S+)", RegexOptions.Compiled);

    /// <summary>The totals, the build's errors, and each failure with the first lines of its message.</summary>
    private static string Summary(string output, int maxFailures)
    {
        var lines = output.Split('\n').Select(l => l.TrimEnd('\r')).ToList();
        var text = new StringBuilder();
        foreach (string error in lines.Where(l => l.Contains(" error ", StringComparison.Ordinal)).Distinct().Take(20))
            text.AppendLine(error.Trim());
        int failures = 0;
        for (int i = 0; i < lines.Count; i++)
        {
            var match = FailedLine.Match(lines[i]);
            if (!match.Success) continue;
            if (++failures > maxFailures) continue;
            text.AppendLine("FAILED " + match.Groups["name"].Value);
            int at = lines.FindIndex(i, l => l.Contains("Error Message:", StringComparison.Ordinal));
            if (at < 0 || at > i + 3) continue;
            foreach (string message in lines.Skip(at + 1).TakeWhile(l => !l.Contains("Stack Trace:", StringComparison.Ordinal)).Take(4))
                text.AppendLine("  " + message.Trim());
        }
        if (failures > maxFailures) text.AppendLine($"... and {failures - maxFailures} more failure(s).");
        var totals = lines.Where(l => l.Contains("Passed!", StringComparison.Ordinal) || l.Contains("Failed!", StringComparison.Ordinal)).ToList();
        if (totals.Count > 0) text.AppendLine(string.Join('\n', totals.Select(t => t.Trim())));
        else text.AppendLine("No totals: the build or the run failed. Last lines:\n" + string.Join('\n', lines.TakeLast(25)));
        return text.ToString().TrimEnd();
    }
}
