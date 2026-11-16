using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace Jondo.Unity.Launcher.Setup
{
    /// <summary>
    /// The Dofus client from the link the repository's README gives today: a MEGA file link or
    /// a plain HTTP one, a zip or a 7z, unpacked into the installation folder as
    /// «Cliente 3.6.10.11».
    /// </summary>
    internal static class ClientDownload
    {
        /// <summary>The client version the emulator speaks to, the one the README links.</summary>
        public const string Version = "3.6.10.11";

        /// <summary>
        /// Room the download needs: the archive and what it unpacks to side by side, until the
        /// archive is deleted. Read from the archive's size at the source; the unpacked client
        /// is about 1.25 times the archive.
        /// </summary>
        public static long SpaceNeeded(long archiveSize) => archiveSize + (long)(archiveSize * 1.25);

        /// <summary>Free bytes on the drive a folder is on.</summary>
        public static long FreeSpace(string folder)
        {
            string? root = Path.GetPathRoot(Path.GetFullPath(folder));
            return string.IsNullOrEmpty(root) ? long.MaxValue : new DriveInfo(root).AvailableFreeSpace;
        }

        /// <summary>The archive's name and size at the link, without downloading it.</summary>
        public static async Task<(string Name, long Size)> ProbeAsync(string url, CancellationToken cancel)
        {
            if (MegaLink.TryParse(url) is { } mega)
            {
                var info = await mega.InfoAsync(Http.Shared, cancel, withUrl: false);
                return (info.Name, info.Size);
            }
            using var request = new System.Net.Http.HttpRequestMessage(System.Net.Http.HttpMethod.Head, url);
            using var answer = await Http.Shared.SendAsync(request, cancel);
            answer.EnsureSuccessStatusCode();
            string name = answer.Content.Headers.ContentDisposition?.FileNameStar
                          ?? answer.Content.Headers.ContentDisposition?.FileName?.Trim('"')
                          ?? Path.GetFileName(answer.RequestMessage?.RequestUri?.LocalPath ?? url);
            return (name, answer.Content.Headers.ContentLength ?? 0);
        }

        /// <summary>Downloads the archive into the downloads folder; returns its path.</summary>
        public static async Task<string> DownloadAsync(string url, string downloads, IProgress<Transfer> progress,
                                                       Action<DateTime?> waiting, CancellationToken cancel)
        {
            var (name, size) = await ProbeAsync(url, cancel);
            if (string.IsNullOrWhiteSpace(name)) name = $"Cliente {Version}.7z";
            name = string.Concat(name.Split(Path.GetInvalidFileNameChars()));
            string target = Path.Combine(downloads, name);

            long free = FreeSpace(downloads);
            long have = File.Exists(target + ".part") ? new FileInfo(target + ".part").Length : 0;
            if (size > 0 && free < SpaceNeeded(size) - have)
                throw new SetupException($"The client needs {Format.Bytes(SpaceNeeded(size))} free on that drive " +
                                         $"(the archive and the game unpacked); there are {Format.Bytes(free)}.");

            if (MegaLink.TryParse(url) is { } mega)
                await mega.DownloadAsync(target, progress, waiting, cancel);
            else
                await Downloader.DownloadAsync(url, target, size, null, progress, cancel);
            return target;
        }

        /// <summary>
        /// Unpacks the archive into the installation folder; returns the client's executable.
        /// An archive with one folder at its root («Cliente 3.6.10.11/») keeps it; one with its
        /// files loose goes into «Cliente 3.6.10.11».
        /// </summary>
        public static string Unpack(string archive, string installRoot, IProgress<Transfer> progress,
                                    Action<string> current, CancellationToken cancel)
        {
            string? folder = ArchiveExtractor.CommonRoot(archive);
            // Its own folder when it has one, with the name it has; else the files go into one.
            string destination = folder != null ? Path.Combine(installRoot, folder) : Path.Combine(installRoot, $"Cliente {Version}");
            ArchiveExtractor.Extract(archive, destination, folder, progress, cancel, current: current);
            var client = ClientInstallation.InDirectory(destination)
                         ?? throw new SetupException("The archive did not hold a Dofus client.");
            return client.Executable;
        }
    }

    /// <summary>Sizes and times as the window writes them.</summary>
    internal static class Format
    {
        public static string Bytes(long bytes)
        {
            string[] units = { "B", "KB", "MB", "GB", "TB" };
            double value = bytes;
            int unit = 0;
            while (value >= 1024 && unit < units.Length - 1) { value /= 1024; unit++; }
            return unit == 0 ? $"{bytes} B" : $"{value:0.#} {units[unit]}";
        }

        public static string Speed(double bytesPerSecond) => bytesPerSecond > 0 ? Bytes((long)bytesPerSecond) + "/s" : "";

        public static string Duration(TimeSpan? span)
        {
            if (span is not { } t) return "…";
            if (t.TotalSeconds < 1) return "0 s";
            if (t.TotalMinutes < 1) return $"{(int)t.TotalSeconds} s";
            if (t.TotalHours < 1) return $"{(int)t.TotalMinutes} min {t.Seconds:00} s";
            return $"{(int)t.TotalHours} h {t.Minutes:00} min";
        }
    }
}
