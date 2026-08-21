using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace Jondo.Unity.Launcher;

/// <summary>
/// Installs the official x64 MelonLoader archive and the bundled JondoFix mod into a validated
/// Dofus installation.  The archive version and digest are deliberately pinned: updating the
/// loader is an application release decision, not something an end user's launcher discovers at
/// runtime.
///
/// The implementation follows MelonLoader's documented manual installation layout instead of
/// launching its graphical installer.  The official installer does not document a supported
/// unattended command-line interface.
/// </summary>
public static class MelonLoaderInstaller
{
    public const string MelonLoaderVersion = "0.7.3";
    public const string AssetName = "MelonLoader.x64.zip";
    public const string OfficialReleaseUrl =
        "https://github.com/LavaGang/MelonLoader/releases/tag/v0.7.3";
    public const string OfficialAssetUrl =
        "https://github.com/LavaGang/MelonLoader/releases/download/v0.7.3/MelonLoader.x64.zip";
    public const string OfficialArchiveSha256 =
        "5B2B2F3D1CD42B59EC886C5BDC2663EDAE87A0097A4F4A8F58C0965A99DDA416";

    // These two hashes were calculated from the archive identified by OfficialArchiveSha256.
    private const string OfficialProxySha256 =
        "0CE7A4E530C7F83F172A2C44AED45EEDB3BDCF06B83760F143A0FBA6885FA929";
    private const string OfficialCoreSha256 =
        "BDD43DC0F3893C208C95389B863AB61C967AE14204DA0DD8051647E754C8709B";

    private const long MaximumArchiveBytes = 32L * 1024 * 1024;
    private const long MaximumExpandedBytes = 256L * 1024 * 1024;
    private const int MaximumArchiveEntries = 2_000;
    private const string MarkerRelativePath = @"UserData\Jondo\melonloader-install.json";
    private const string StagePrefix = ".jondo-melon-stage-";
    private const string BackupPrefix = ".jondo-melon-backup-";

    private static readonly SemaphoreSlim InstallationGate = new(1, 1);
    private static readonly Regex DisableSetting = new(
        @"^(?<indent>\s*)disable\s*=\s*(?<value>true|false)\s*(?<comment>#.*)?$",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);

    private static readonly HttpClient DownloadClient = CreateDownloadClient();

    public enum InstallationState
    {
        InvalidGame,
        NotInstalled,
        LoaderOnly,
        ModOnly,
        NeedsUpdate,
        Ready,
        Disabled,
    }

    public enum InstallPhase
    {
        Validating,
        Downloading,
        Verifying,
        Staging,
        Installing,
        Configuring,
        Complete,
    }

    public sealed class InstallProgress
    {
        public InstallPhase Phase { get; init; }
        public int Percent { get; init; }
        public string Message { get; init; } = "";
    }

    public sealed class InstallationStatus
    {
        public InstallationState State { get; init; }
        public bool GameIsValid { get; init; }
        public string GameDirectory { get; init; } = "";
        public bool LoaderPresent { get; init; }
        public bool LoaderVersionMatches { get; init; }
        public string InstalledLoaderVersion { get; init; } = "";
        public bool JondoFixPresent { get; init; }
        public bool JondoFixMatches { get; init; }
        public bool ManagedByLauncher { get; init; }
        public bool Disabled { get; init; }
        public string Message { get; init; } = "";

        public bool IsReady => State == InstallationState.Ready;
    }

    public sealed class OperationResult
    {
        public bool Success { get; init; }
        public string Message { get; init; } = "";
        public InstallationStatus? Status { get; init; }

        public static OperationResult Ok(string message, InstallationStatus? status = null) =>
            new() { Success = true, Message = message, Status = status };

        public static OperationResult Fail(string message, InstallationStatus? status = null) =>
            new() { Success = false, Message = message, Status = status };
    }

    /// <summary>Inspects the selected Dofus installation without changing it.</summary>
    public static InstallationStatus GetStatus(string dofusExecutable)
    {
        if (!TryValidateDofusExecutable(dofusExecutable, out string gameDirectory, out string problem))
        {
            return new InstallationStatus
            {
                State = InstallationState.InvalidGame,
                Message = problem,
            };
        }

        string proxyPath = Path.Combine(gameDirectory, "version.dll");
        string corePath = Path.Combine(gameDirectory, "MelonLoader", "net6", "MelonLoader.dll");
        string modPath = Path.Combine(gameDirectory, "Mods", "JondoFix.dll");
        string markerPath = Path.Combine(gameDirectory, MarkerRelativePath);

        bool proxyPresent = File.Exists(proxyPath);
        bool corePresent = File.Exists(corePath);
        bool loaderPresent = proxyPresent && corePresent;
        string loaderVersion = ReadAssemblyVersion(corePath);
        bool loaderMatches = loaderPresent
            && string.Equals(loaderVersion, MelonLoaderVersion, StringComparison.OrdinalIgnoreCase)
            && HashMatches(proxyPath, OfficialProxySha256)
            && HashMatches(corePath, OfficialCoreSha256);
        bool modPresent = File.Exists(modPath);
        bool modMatches = modPresent && HashMatches(modPath, JondoFixPayload.Sha256);
        bool disabled = ReadDisabled(Path.Combine(gameDirectory, "UserData", "Loader.cfg"));
        bool managed = TryReadMarker(markerPath, out InstallMarker? marker)
            && string.Equals(marker?.ArchiveSha256, OfficialArchiveSha256,
                StringComparison.OrdinalIgnoreCase);

        InstallationState state;
        string message;
        if (loaderMatches && modMatches)
        {
            state = disabled ? InstallationState.Disabled : InstallationState.Ready;
            message = disabled
                ? "MelonLoader and JondoFix are installed, but mod loading is disabled."
                : "MelonLoader and JondoFix are ready.";
        }
        else if (!loaderPresent && !modPresent)
        {
            state = InstallationState.NotInstalled;
            message = "MelonLoader and JondoFix are not installed.";
        }
        else if (loaderPresent && !modPresent)
        {
            state = loaderMatches ? InstallationState.LoaderOnly : InstallationState.NeedsUpdate;
            message = loaderMatches
                ? "MelonLoader is installed, but JondoFix is missing."
                : "The installed MelonLoader files do not match the supported version.";
        }
        else if (!loaderPresent && modPresent)
        {
            state = InstallationState.ModOnly;
            message = "JondoFix is present, but MelonLoader is incomplete.";
        }
        else
        {
            state = InstallationState.NeedsUpdate;
            message = !loaderMatches
                ? "MelonLoader needs to be installed or updated."
                : "JondoFix needs to be updated.";
        }

        return new InstallationStatus
        {
            State = state,
            GameIsValid = true,
            GameDirectory = gameDirectory,
            LoaderPresent = loaderPresent,
            LoaderVersionMatches = loaderMatches,
            InstalledLoaderVersion = loaderVersion,
            JondoFixPresent = modPresent,
            JondoFixMatches = modMatches,
            ManagedByLauncher = managed,
            Disabled = disabled,
            Message = message,
        };
    }

    /// <summary>
    /// Installs or repairs the pinned loader and bundled mod.  Dofus must be closed.  Existing
    /// destination files are moved to a private rollback directory until every step succeeds.
    /// </summary>
    public static async Task<OperationResult> EnsureInstalledAsync(
        string dofusExecutable,
        IProgress<InstallProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        Report(progress, InstallPhase.Validating, 0, "Validating Dofus installation...");
        InstallationStatus initial = GetStatus(dofusExecutable);
        if (!initial.GameIsValid)
            return OperationResult.Fail(initial.Message, initial);

        if (IsGameRunning(dofusExecutable))
            return OperationResult.Fail("Close Dofus before installing or updating MelonLoader.", initial);

        await InstallationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            // Recheck after taking the gate: another caller may have completed the work.
            initial = GetStatus(dofusExecutable);
            if (initial.State == InstallationState.Ready && initial.ManagedByLauncher)
                return OperationResult.Ok("MelonLoader and JondoFix are already ready.", initial);

            string archivePath = await GetVerifiedArchiveAsync(progress, cancellationToken)
                .ConfigureAwait(false);
            byte[] jondoFix = JondoFixPayload.GetBytes();
            if (!HashMatches(jondoFix, JondoFixPayload.Sha256))
                return OperationResult.Fail("The bundled JondoFix payload failed its integrity check.", initial);

            string gameDirectory = initial.GameDirectory;
            string stageDirectory = Path.Combine(gameDirectory, StagePrefix + Guid.NewGuid().ToString("N"));
            string backupDirectory = Path.Combine(gameDirectory, BackupPrefix + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(stageDirectory);

            var transaction = new InstallTransaction(gameDirectory, backupDirectory);
            try
            {
                Report(progress, InstallPhase.Staging, 55, "Staging verified MelonLoader files...");
                ExtractVerifiedArchive(archivePath, stageDirectory, cancellationToken);

                string stagedProxy = Path.Combine(stageDirectory, "version.dll");
                string stagedCore = Path.Combine(stageDirectory, "MelonLoader", "net6", "MelonLoader.dll");
                if (!HashMatches(stagedProxy, OfficialProxySha256)
                    || !HashMatches(stagedCore, OfficialCoreSha256))
                {
                    throw new InvalidDataException("Staged MelonLoader files failed their integrity checks.");
                }

                string stagedMod = Path.Combine(stageDirectory, "JondoFix.dll");
                await File.WriteAllBytesAsync(stagedMod, jondoFix, cancellationToken)
                    .ConfigureAwait(false);
                if (!HashMatches(stagedMod, JondoFixPayload.Sha256))
                    throw new InvalidDataException("Staged JondoFix failed its integrity check.");

                Report(progress, InstallPhase.Installing, 75, "Installing MelonLoader and JondoFix...");
                transaction.ReplaceDirectory(
                    Path.Combine(stageDirectory, "MelonLoader"),
                    Path.Combine(gameDirectory, "MelonLoader"));
                transaction.ReplaceFile(stagedProxy, Path.Combine(gameDirectory, "version.dll"));

                string modsDirectory = Path.Combine(gameDirectory, "Mods");
                EnsureOrdinaryDirectory(gameDirectory, modsDirectory);
                Directory.CreateDirectory(modsDirectory);
                transaction.ReplaceFile(stagedMod, Path.Combine(modsDirectory, "JondoFix.dll"));

                Report(progress, InstallPhase.Configuring, 90, "Writing launcher installation metadata...");
                string markerPath = Path.Combine(gameDirectory, MarkerRelativePath);
                Directory.CreateDirectory(Path.GetDirectoryName(markerPath)!);
                string stagedMarker = Path.Combine(stageDirectory, "melonloader-install.json");
                var marker = new InstallMarker
                {
                    SchemaVersion = 1,
                    MelonLoaderVersion = MelonLoaderVersion,
                    AssetName = AssetName,
                    ArchiveSha256 = OfficialArchiveSha256,
                    ProxySha256 = OfficialProxySha256,
                    JondoFixSha256 = JondoFixPayload.Sha256,
                    InstalledUtc = DateTimeOffset.UtcNow,
                };
                await File.WriteAllTextAsync(
                    stagedMarker,
                    JsonSerializer.Serialize(marker, JsonOptions),
                    new UTF8Encoding(false),
                    cancellationToken).ConfigureAwait(false);
                transaction.ReplaceFile(stagedMarker, markerPath);

                transaction.Commit();
            }
            catch
            {
                transaction.Rollback();
                throw;
            }
            finally
            {
                DeleteOwnedDirectory(gameDirectory, stageDirectory, StagePrefix);
                if (transaction.CanDiscardBackup)
                    DeleteOwnedDirectory(gameDirectory, backupDirectory, BackupPrefix);
            }

            InstallationStatus complete = GetStatus(dofusExecutable);
            if (complete.State is not (InstallationState.Ready or InstallationState.Disabled))
                return OperationResult.Fail("Installation finished, but verification did not pass.", complete);

            Report(progress, InstallPhase.Complete, 100, "MelonLoader and JondoFix are installed.");
            return OperationResult.Ok("MelonLoader and JondoFix were installed successfully.", complete);
        }
        catch (OperationCanceledException)
        {
            return OperationResult.Fail("MelonLoader installation was cancelled.", GetStatus(dofusExecutable));
        }
        catch (UnauthorizedAccessException ex)
        {
            return OperationResult.Fail(
                "The launcher cannot write to the Dofus directory. Check its permissions. " + ex.Message,
                GetStatus(dofusExecutable));
        }
        catch (Exception ex)
        {
            return OperationResult.Fail("MelonLoader installation failed: " + ex.Message,
                GetStatus(dofusExecutable));
        }
        finally
        {
            InstallationGate.Release();
        }
    }

    /// <summary>
    /// Enables or disables MelonLoader using its documented Loader.cfg setting.  No loader or mod
    /// files are removed.
    /// </summary>
    public static OperationResult SetEnabled(string dofusExecutable, bool enabled)
    {
        InstallationStatus status = GetStatus(dofusExecutable);
        if (!status.GameIsValid) return OperationResult.Fail(status.Message, status);
        if (!status.LoaderPresent)
            return OperationResult.Fail("MelonLoader is not installed.", status);
        if (IsGameRunning(dofusExecutable))
            return OperationResult.Fail("Close Dofus before changing the MelonLoader setting.", status);

        try
        {
            string configPath = Path.Combine(status.GameDirectory, "UserData", "Loader.cfg");
            WriteDisabled(configPath, disabled: !enabled);
            InstallationStatus updated = GetStatus(dofusExecutable);
            return OperationResult.Ok(
                enabled ? "MelonLoader was enabled." : "MelonLoader was disabled.", updated);
        }
        catch (Exception ex)
        {
            return OperationResult.Fail("Could not update MelonLoader settings: " + ex.Message, status);
        }
    }

    /// <summary>
    /// Removes the launcher-managed loader and JondoFix.  Other files in Mods, Plugins and
    /// UserData are preserved.  An unmarked installation is left alone unless force is explicit.
    /// </summary>
    public static async Task<OperationResult> UninstallAsync(
        string dofusExecutable,
        bool force = false,
        CancellationToken cancellationToken = default)
    {
        InstallationStatus initial = GetStatus(dofusExecutable);
        if (!initial.GameIsValid) return OperationResult.Fail(initial.Message, initial);
        if (IsGameRunning(dofusExecutable))
            return OperationResult.Fail("Close Dofus before uninstalling MelonLoader.", initial);
        if (!initial.ManagedByLauncher && !force)
        {
            return OperationResult.Fail(
                "This MelonLoader installation is not marked as launcher-managed; it was left unchanged.",
                initial);
        }

        await InstallationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            string gameDirectory = initial.GameDirectory;
            string markerPath = Path.Combine(gameDirectory, MarkerRelativePath);
            if (!force && TryReadMarker(markerPath, out InstallMarker? marker))
            {
                string proxy = Path.Combine(gameDirectory, "version.dll");
                string mod = Path.Combine(gameDirectory, "Mods", "JondoFix.dll");
                if (File.Exists(proxy) && !HashMatches(proxy, marker!.ProxySha256))
                    return OperationResult.Fail("version.dll changed after installation; uninstall was stopped.", initial);
                if (File.Exists(mod) && !HashMatches(mod, marker.JondoFixSha256))
                    return OperationResult.Fail("JondoFix.dll changed after installation; uninstall was stopped.", initial);
            }

            DeleteKnownDirectory(gameDirectory, Path.Combine(gameDirectory, "MelonLoader"));
            DeleteKnownFile(gameDirectory, Path.Combine(gameDirectory, "version.dll"));
            DeleteKnownFile(gameDirectory, Path.Combine(gameDirectory, "Mods", "JondoFix.dll"));
            DeleteKnownFile(gameDirectory, markerPath);

            InstallationStatus complete = GetStatus(dofusExecutable);
            return OperationResult.Ok(
                "MelonLoader and JondoFix were removed. Other Mods, Plugins and UserData were preserved.",
                complete);
        }
        catch (Exception ex)
        {
            return OperationResult.Fail("MelonLoader uninstall failed: " + ex.Message,
                GetStatus(dofusExecutable));
        }
        finally
        {
            InstallationGate.Release();
        }
    }

    private static async Task<string> GetVerifiedArchiveAsync(
        IProgress<InstallProgress>? progress,
        CancellationToken cancellationToken)
    {
        string cacheDirectory = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Jondo", "packages", "MelonLoader", "v" + MelonLoaderVersion);
        Directory.CreateDirectory(cacheDirectory);
        string archivePath = Path.Combine(cacheDirectory, AssetName);

        if (File.Exists(archivePath) && HashMatches(archivePath, OfficialArchiveSha256))
        {
            Report(progress, InstallPhase.Verifying, 50, "Using the verified cached MelonLoader package.");
            return archivePath;
        }

        if (File.Exists(archivePath)) File.Delete(archivePath);

        Report(progress, InstallPhase.Downloading, 5, "Downloading official MelonLoader v0.7.3...");
        string temporaryPath = archivePath + ".part-" + Guid.NewGuid().ToString("N");
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, OfficialAssetUrl);
            request.Headers.UserAgent.ParseAdd("Jondo-Launcher/" + LauncherService.Version);
            using HttpResponseMessage response = await DownloadClient.SendAsync(
                request, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
            response.EnsureSuccessStatusCode();
            ValidateFinalDownloadUri(response.RequestMessage?.RequestUri);

            long? length = response.Content.Headers.ContentLength;
            if (length is > MaximumArchiveBytes)
                throw new InvalidDataException("The MelonLoader package is larger than expected.");

            await using Stream source = await response.Content.ReadAsStreamAsync(cancellationToken)
                .ConfigureAwait(false);
            await using var destination = new FileStream(
                temporaryPath, FileMode.CreateNew, FileAccess.Write, FileShare.None,
                128 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);

            byte[] buffer = new byte[128 * 1024];
            long received = 0;
            while (true)
            {
                int read = await source.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
                if (read == 0) break;
                received += read;
                if (received > MaximumArchiveBytes)
                    throw new InvalidDataException("The MelonLoader download exceeded its size limit.");
                await destination.WriteAsync(buffer.AsMemory(0, read), cancellationToken)
                    .ConfigureAwait(false);

                if (length is > 0)
                {
                    int percent = 5 + (int)Math.Min(40, received * 40 / length.Value);
                    Report(progress, InstallPhase.Downloading, percent,
                        $"Downloading MelonLoader ({received / 1024 / 1024} MB)...");
                }
            }

            await destination.FlushAsync(cancellationToken).ConfigureAwait(false);
            destination.Close();

            Report(progress, InstallPhase.Verifying, 48, "Verifying MelonLoader package integrity...");
            if (!HashMatches(temporaryPath, OfficialArchiveSha256))
                throw new InvalidDataException("The MelonLoader package SHA-256 does not match the official release.");

            File.Move(temporaryPath, archivePath, true);
            return archivePath;
        }
        finally
        {
            if (File.Exists(temporaryPath)) File.Delete(temporaryPath);
        }
    }

    private static void ExtractVerifiedArchive(
        string archivePath,
        string stageDirectory,
        CancellationToken cancellationToken)
    {
        using ZipArchive archive = ZipFile.OpenRead(archivePath);
        if (archive.Entries.Count > MaximumArchiveEntries)
            throw new InvalidDataException("The MelonLoader package contains too many entries.");

        bool hasLoader = false;
        bool hasProxy = false;
        long expandedBytes = 0;
        string stageRoot = EnsureTrailingSeparator(Path.GetFullPath(stageDirectory));

        foreach (ZipArchiveEntry entry in archive.Entries)
        {
            cancellationToken.ThrowIfCancellationRequested();
            string archiveName = entry.FullName.Replace('\\', '/');
            if (archiveName.Length == 0) continue;
            if (Path.IsPathRooted(archiveName)
                || archiveName.Split('/').Any(part => part is ".." or "."))
            {
                throw new InvalidDataException("The MelonLoader package contains an unsafe path.");
            }

            bool allowed = archiveName.StartsWith("MelonLoader/", StringComparison.Ordinal)
                || string.Equals(archiveName, "version.dll", StringComparison.Ordinal)
                || string.Equals(archiveName, "dobby.dll", StringComparison.Ordinal);
            if (!allowed)
                throw new InvalidDataException("Unexpected file in MelonLoader package: " + archiveName);

            hasLoader |= archiveName.StartsWith("MelonLoader/", StringComparison.Ordinal);
            hasProxy |= string.Equals(archiveName, "version.dll", StringComparison.Ordinal);
            expandedBytes += entry.Length;
            if (expandedBytes > MaximumExpandedBytes)
                throw new InvalidDataException("The expanded MelonLoader package exceeds its size limit.");

            string destination = Path.GetFullPath(Path.Combine(
                stageDirectory,
                archiveName.Replace('/', Path.DirectorySeparatorChar)));
            if (!destination.StartsWith(stageRoot, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("The MelonLoader package tried to escape the staging directory.");

            if (archiveName.EndsWith("/", StringComparison.Ordinal))
            {
                Directory.CreateDirectory(destination);
                continue;
            }

            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            using Stream input = entry.Open();
            using var output = new FileStream(destination, FileMode.CreateNew, FileAccess.Write, FileShare.None);
            input.CopyTo(output);
        }

        if (!hasLoader || !hasProxy)
            throw new InvalidDataException("The MelonLoader package is missing required files.");
    }

    private static bool TryValidateDofusExecutable(
        string dofusExecutable,
        out string gameDirectory,
        out string problem)
    {
        gameDirectory = "";
        problem = "";
        if (string.IsNullOrWhiteSpace(dofusExecutable))
        {
            problem = "Select Dofus.exe before installing MelonLoader.";
            return false;
        }

        string fullPath;
        try { fullPath = Path.GetFullPath(dofusExecutable); }
        catch (Exception ex)
        {
            problem = "The selected game path is invalid: " + ex.Message;
            return false;
        }

        if (!string.Equals(Path.GetFileName(fullPath), "Dofus.exe", StringComparison.OrdinalIgnoreCase)
            || !File.Exists(fullPath))
        {
            problem = "The selected file must be an existing Dofus.exe.";
            return false;
        }

        gameDirectory = Path.GetDirectoryName(fullPath) ?? "";
        if (gameDirectory.Length == 0
            || !File.Exists(Path.Combine(gameDirectory, "GameAssembly.dll"))
            || !File.Exists(Path.Combine(gameDirectory, "UnityPlayer.dll"))
            || !Directory.Exists(Path.Combine(gameDirectory, "Dofus_Data")))
        {
            problem = "The selected file is not in a complete Dofus IL2CPP installation.";
            return false;
        }

        if (!IsAmd64PortableExecutable(fullPath))
        {
            problem = "The selected Dofus.exe is not a Windows x64 executable.";
            return false;
        }

        try
        {
            FileAttributes attributes = File.GetAttributes(gameDirectory);
            if ((attributes & FileAttributes.ReparsePoint) != 0)
            {
                problem = "The Dofus directory cannot be a symbolic link or junction.";
                return false;
            }
        }
        catch (Exception ex)
        {
            problem = "The Dofus directory cannot be inspected: " + ex.Message;
            return false;
        }

        return true;
    }

    private static bool IsAmd64PortableExecutable(string path)
    {
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            using var reader = new BinaryReader(stream);
            if (reader.ReadUInt16() != 0x5A4D) return false; // MZ
            stream.Position = 0x3C;
            int peOffset = reader.ReadInt32();
            if (peOffset < 0x40 || peOffset > stream.Length - 6) return false;
            stream.Position = peOffset;
            if (reader.ReadUInt32() != 0x00004550) return false; // PE\0\0
            return reader.ReadUInt16() == 0x8664; // IMAGE_FILE_MACHINE_AMD64
        }
        catch { return false; }
    }

    private static bool IsGameRunning(string dofusExecutable)
    {
        string expected = Path.GetFullPath(dofusExecutable);
        foreach (Process process in Process.GetProcessesByName("Dofus"))
        {
            using (process)
            {
                try
                {
                    string? actual = process.MainModule?.FileName;
                    if (actual != null
                        && string.Equals(Path.GetFullPath(actual), expected, StringComparison.OrdinalIgnoreCase))
                        return true;
                }
                catch
                {
                    // An inaccessible Dofus process is still a reason not to modify its files.
                    return true;
                }
            }
        }
        return false;
    }

    private static string ReadAssemblyVersion(string path)
    {
        if (!File.Exists(path)) return "";
        try
        {
            Version? version = AssemblyName.GetAssemblyName(path).Version;
            return version == null ? "" : $"{version.Major}.{version.Minor}.{version.Build}";
        }
        catch { return ""; }
    }

    private static bool ReadDisabled(string configPath)
    {
        if (!File.Exists(configPath)) return false;
        try
        {
            bool inLoader = false;
            foreach (string line in File.ReadLines(configPath))
            {
                string trimmed = line.Trim();
                if (trimmed.StartsWith("[", StringComparison.Ordinal)
                    && trimmed.EndsWith("]", StringComparison.Ordinal))
                {
                    inLoader = string.Equals(trimmed, "[loader]", StringComparison.OrdinalIgnoreCase);
                    continue;
                }
                if (inLoader && DisableSetting.Match(line) is { Success: true } match)
                    return bool.Parse(match.Groups["value"].Value);
            }
        }
        catch { }
        return false;
    }

    private static void WriteDisabled(string configPath, bool disabled)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(configPath)!);
        var lines = File.Exists(configPath)
            ? File.ReadAllLines(configPath).ToList()
            : new List<string>();

        int loaderIndex = lines.FindIndex(line =>
            string.Equals(line.Trim(), "[loader]", StringComparison.OrdinalIgnoreCase));
        if (loaderIndex < 0)
        {
            if (lines.Count > 0 && lines[^1].Length > 0) lines.Add("");
            lines.Add("[loader]");
            lines.Add("disable = " + disabled.ToString().ToLowerInvariant());
        }
        else
        {
            int end = lines.FindIndex(loaderIndex + 1, line =>
            {
                string trimmed = line.Trim();
                return trimmed.StartsWith("[", StringComparison.Ordinal)
                    && trimmed.EndsWith("]", StringComparison.Ordinal);
            });
            if (end < 0) end = lines.Count;

            int setting = -1;
            for (int i = loaderIndex + 1; i < end; i++)
            {
                if (DisableSetting.IsMatch(lines[i]))
                {
                    setting = i;
                    break;
                }
            }

            if (setting >= 0)
            {
                Match match = DisableSetting.Match(lines[setting]);
                lines[setting] = match.Groups["indent"].Value
                    + "disable = " + disabled.ToString().ToLowerInvariant()
                    + (match.Groups["comment"].Success ? " " + match.Groups["comment"].Value : "");
            }
            else
            {
                lines.Insert(loaderIndex + 1, "disable = " + disabled.ToString().ToLowerInvariant());
            }
        }

        string temporary = configPath + ".jondo-" + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            File.WriteAllLines(temporary, lines, new UTF8Encoding(false));
            File.Move(temporary, configPath, true);
        }
        finally
        {
            if (File.Exists(temporary)) File.Delete(temporary);
        }
    }

    private static bool HashMatches(string path, string expected)
    {
        if (!File.Exists(path)) return false;
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            return string.Equals(Convert.ToHexString(SHA256.HashData(stream)), expected,
                StringComparison.OrdinalIgnoreCase);
        }
        catch { return false; }
    }

    private static bool HashMatches(byte[] content, string expected) =>
        string.Equals(Convert.ToHexString(SHA256.HashData(content)), expected,
            StringComparison.OrdinalIgnoreCase);

    private static bool TryReadMarker(string path, out InstallMarker? marker)
    {
        marker = null;
        if (!File.Exists(path)) return false;
        try
        {
            marker = JsonSerializer.Deserialize<InstallMarker>(File.ReadAllText(path), JsonOptions);
            return marker is { SchemaVersion: 1 };
        }
        catch { return false; }
    }

    private static void ValidateFinalDownloadUri(Uri? uri)
    {
        if (uri == null || uri.Scheme != Uri.UriSchemeHttps)
            throw new InvalidDataException("MelonLoader download did not use HTTPS.");

        bool officialHost = string.Equals(uri.Host, "github.com", StringComparison.OrdinalIgnoreCase)
            || uri.Host.EndsWith(".githubusercontent.com", StringComparison.OrdinalIgnoreCase);
        if (!officialHost)
            throw new InvalidDataException("MelonLoader download was redirected outside GitHub.");
    }

    private static HttpClient CreateDownloadClient()
    {
        var handler = new HttpClientHandler
        {
            AllowAutoRedirect = true,
            AutomaticDecompression = DecompressionMethods.All,
            MaxAutomaticRedirections = 8,
        };
        return new HttpClient(handler) { Timeout = TimeSpan.FromMinutes(5) };
    }

    private static void EnsureOrdinaryDirectory(string gameRoot, string directory)
    {
        EnsureInside(gameRoot, directory);
        if (Directory.Exists(directory)
            && (File.GetAttributes(directory) & FileAttributes.ReparsePoint) != 0)
        {
            throw new IOException("Refusing to write through a symbolic link or junction: " + directory);
        }
    }

    private static void DeleteOwnedDirectory(string gameRoot, string directory, string requiredPrefix)
    {
        string fullRoot = EnsureTrailingSeparator(Path.GetFullPath(gameRoot));
        string fullDirectory = Path.GetFullPath(directory);
        if (!fullDirectory.StartsWith(fullRoot, StringComparison.OrdinalIgnoreCase)
            || !string.Equals(Path.GetDirectoryName(fullDirectory), fullRoot.TrimEnd(Path.DirectorySeparatorChar),
                StringComparison.OrdinalIgnoreCase)
            || !Path.GetFileName(fullDirectory).StartsWith(requiredPrefix, StringComparison.Ordinal))
        {
            throw new InvalidOperationException("Refusing to delete an unowned directory.");
        }
        if (Directory.Exists(fullDirectory)) Directory.Delete(fullDirectory, true);
    }

    private static void DeleteKnownDirectory(string gameRoot, string directory)
    {
        EnsureInside(gameRoot, directory);
        EnsureOrdinaryDirectory(gameRoot, directory);
        if (Directory.Exists(directory)) Directory.Delete(directory, true);
    }

    private static void DeleteKnownFile(string gameRoot, string file)
    {
        EnsureInside(gameRoot, file);
        if (File.Exists(file))
        {
            if ((File.GetAttributes(file) & FileAttributes.ReparsePoint) != 0)
                throw new IOException("Refusing to delete a symbolic link: " + file);
            File.Delete(file);
        }
    }

    private static void EnsureInside(string root, string candidate)
    {
        string fullRootDirectory = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar);
        string fullRoot = EnsureTrailingSeparator(fullRootDirectory);
        string fullCandidate = Path.GetFullPath(candidate);
        if (!string.Equals(fullCandidate, fullRootDirectory, StringComparison.OrdinalIgnoreCase)
            && !fullCandidate.StartsWith(fullRoot, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Path escapes the validated Dofus directory.");
    }

    private static string EnsureTrailingSeparator(string path) =>
        path.EndsWith(Path.DirectorySeparatorChar) ? path : path + Path.DirectorySeparatorChar;

    private static void Report(
        IProgress<InstallProgress>? progress,
        InstallPhase phase,
        int percent,
        string message) =>
        progress?.Report(new InstallProgress { Phase = phase, Percent = percent, Message = message });

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
    };

    private sealed class InstallMarker
    {
        public int SchemaVersion { get; set; }
        public string MelonLoaderVersion { get; set; } = "";
        public string AssetName { get; set; } = "";
        public string ArchiveSha256 { get; set; } = "";
        public string ProxySha256 { get; set; } = "";
        public string JondoFixSha256 { get; set; } = "";
        public DateTimeOffset InstalledUtc { get; set; }
    }

    /// <summary>Move-based transaction used only inside one validated game directory.</summary>
    private sealed class InstallTransaction
    {
        private readonly string _gameRoot;
        private readonly string _backupRoot;
        private readonly List<Replacement> _replacements = new();
        private bool _committed;

        public bool CanDiscardBackup { get; private set; }

        public InstallTransaction(string gameRoot, string backupRoot)
        {
            _gameRoot = Path.GetFullPath(gameRoot);
            _backupRoot = Path.GetFullPath(backupRoot);
            EnsureInside(_gameRoot, _backupRoot);
            Directory.CreateDirectory(_backupRoot);
        }

        public void ReplaceDirectory(string source, string destination)
        {
            EnsureInside(_gameRoot, source);
            EnsureInside(_gameRoot, destination);
            if (!Directory.Exists(source)) throw new DirectoryNotFoundException(source);
            EnsureOrdinaryDirectory(_gameRoot, destination);

            var replacement = Backup(destination, directory: true);
            Directory.Move(source, destination);
            replacement.NewValueInstalled = true;
        }

        public void ReplaceFile(string source, string destination)
        {
            EnsureInside(_gameRoot, source);
            EnsureInside(_gameRoot, destination);
            if (!File.Exists(source)) throw new FileNotFoundException("Staged file is missing.", source);

            string? parent = Path.GetDirectoryName(destination);
            if (parent != null)
            {
                EnsureOrdinaryDirectory(_gameRoot, parent);
                Directory.CreateDirectory(parent);
            }

            var replacement = Backup(destination, directory: false);
            File.Move(source, destination);
            replacement.NewValueInstalled = true;
        }

        public void Commit()
        {
            _committed = true;
            CanDiscardBackup = true;
        }

        public void Rollback()
        {
            if (_committed) return;
            bool restored = true;
            foreach (Replacement replacement in _replacements.AsEnumerable().Reverse())
            {
                try
                {
                    if (replacement.NewValueInstalled)
                    {
                        if (replacement.IsDirectory && Directory.Exists(replacement.Destination))
                            Directory.Delete(replacement.Destination, true);
                        else if (!replacement.IsDirectory && File.Exists(replacement.Destination))
                            File.Delete(replacement.Destination);
                    }

                    if (replacement.HadOriginal)
                    {
                        if (replacement.IsDirectory)
                            Directory.Move(replacement.Backup, replacement.Destination);
                        else
                            File.Move(replacement.Backup, replacement.Destination);
                    }
                }
                catch
                {
                    // Preserve the original installation error.  The backup folder is deliberately
                    // left in place if a restore itself cannot complete.
                    restored = false;
                }
            }
            CanDiscardBackup = restored;
        }

        private Replacement Backup(string destination, bool directory)
        {
            string relative = Path.GetRelativePath(_gameRoot, destination);
            if (relative.StartsWith("..", StringComparison.Ordinal))
                throw new InvalidOperationException("Destination escaped the game directory.");

            string backup = Path.Combine(_backupRoot, relative);
            var replacement = new Replacement(destination, backup, directory);
            _replacements.Add(replacement);

            bool exists = directory ? Directory.Exists(destination) : File.Exists(destination);
            if (!exists) return replacement;

            Directory.CreateDirectory(Path.GetDirectoryName(backup)!);
            if (directory) Directory.Move(destination, backup);
            else File.Move(destination, backup);
            replacement.HadOriginal = true;
            return replacement;
        }

        private sealed class Replacement(string destination, string backup, bool isDirectory)
        {
            public string Destination { get; } = destination;
            public string Backup { get; } = backup;
            public bool IsDirectory { get; } = isDirectory;
            public bool HadOriginal { get; set; }
            public bool NewValueInstalled { get; set; }
        }
    }
}
