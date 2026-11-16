using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using SharpCompress.Archives;
using SharpCompress.Common;

namespace Jondo.Unity.Launcher.Setup
{
    /// <summary>
    /// Unpacks a zip or a 7z (the client comes as one) into a folder, reporting the bytes
    /// written out of the whole, and never writing outside the folder whatever the archive says.
    /// </summary>
    /// <remarks>
    /// A 7z is usually solid: its files are one compressed stream, and reading them out of order
    /// would decompress it again for each one. They are read in their order, once.
    /// </remarks>
    internal static class ArchiveExtractor
    {
        /// <summary>
        /// The one folder every entry is under, «Cliente 3.6.10.11», or null when the archive has
        /// its files loose at its root.
        /// </summary>
        public static string? CommonRoot(string archivePath)
        {
            using var archive = ArchiveFactory.OpenArchive(archivePath);
            string? root = null;
            foreach (var entry in archive.Entries)
            {
                string key = (entry.Key ?? "").Replace('\\', '/').TrimStart('/');
                int slash = key.IndexOf('/');
                string first = slash < 0 ? (entry.IsDirectory ? key : "") : key[..slash];
                if (first.Length == 0) return null;
                if (root == null) root = first;
                else if (!root.Equals(first, StringComparison.OrdinalIgnoreCase)) return null;
            }
            return root;
        }

        /// <summary>The bytes the archive's files take once unpacked.</summary>
        public static long UnpackedSize(string archivePath)
        {
            using var archive = ArchiveFactory.OpenArchive(archivePath);
            return archive.Entries.Where(e => !e.IsDirectory).Sum(e => e.Size);
        }

        /// <summary>
        /// Unpacks every file under <paramref name="destination"/>; <paramref name="stripRoot"/>, when
        /// given, is taken off the front of every path. <paramref name="onlyThese"/>, when given,
        /// limits it to those paths (after the strip), for an update that needs a few files of a zip.
        /// </summary>
        public static void Extract(string archivePath, string destination, string? stripRoot,
                                   IProgress<Transfer>? progress, CancellationToken cancel,
                                   ISet<string>? onlyThese = null, Action<string>? current = null)
        {
            string target = Path.GetFullPath(destination);
            Directory.CreateDirectory(target);
            using var archive = ArchiveFactory.OpenArchive(archivePath);
            long total = archive.Entries.Where(e => !e.IsDirectory && Wanted(e.Key)).Sum(e => e.Size);
            long done = 0;
            var meter = new PaceMeter();
            meter.Restart(0);
            var buffer = new byte[1 << 20];

            // A solid archive (the 7z) only in its own order, once; a zip entry by entry.
            if (archive.IsSolid || archive.Type == ArchiveType.SevenZip)
            {
                using var reader = archive.ExtractAllEntries();
                while (reader.MoveToNextEntry()) Write(reader.Entry, reader.OpenEntryStream);
            }
            else
            {
                foreach (var entry in archive.Entries) Write(entry, entry.OpenEntryStream);
            }
            progress?.Report(new Transfer(total, total, meter.Pace));

            void Write(IEntry entry, Func<Stream> open)
            {
                cancel.ThrowIfCancellationRequested();
                if (entry.IsDirectory || !Wanted(entry.Key)) return;
                string relative = Relative(entry.Key)!;
                string path = Path.GetFullPath(Path.Combine(target, relative));
                if (!path.StartsWith(target + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                    throw new SetupException($"The archive holds a file outside its folder: «{entry.Key}».");

                current?.Invoke(relative);
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                string temporary = path + ".jondo-new";
                using (var source = open())
                using (var sink = new FileStream(temporary, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 20))
                {
                    int read;
                    while ((read = source.Read(buffer, 0, buffer.Length)) > 0)
                    {
                        cancel.ThrowIfCancellationRequested();
                        sink.Write(buffer, 0, read);
                        done += read;
                        if (meter.Due(done) is { } pace) progress?.Report(new Transfer(done, total, pace));
                    }
                }
                ReplaceFile(temporary, path);
                if (entry.LastModifiedTime is { } when) TrySetTime(path, when);
            }

            bool Wanted(string? key)
            {
                string? r = Relative(key);
                return r != null && (onlyThese == null || onlyThese.Contains(r.Replace('\\', '/')));
            }

            string? Relative(string? key)
            {
                string k = (key ?? "").Replace('\\', '/').TrimStart('/');
                if (stripRoot != null)
                {
                    if (!k.StartsWith(stripRoot + "/", StringComparison.OrdinalIgnoreCase)) return null;
                    k = k[(stripRoot.Length + 1)..];
                }
                return k.Length == 0 ? null : k.Replace('/', Path.DirectorySeparatorChar);
            }
        }

        /// <summary>
        /// Puts a new file where an old one is. A file in use cannot be overwritten on Windows but
        /// can be renamed: the old one goes aside as <c>.old</c> and is removed at the next start.
        /// A running executable refuses the overwrite as access denied, a file merely open as a
        /// sharing violation; both go aside the same way.
        /// </summary>
        public static void ReplaceFile(string newFile, string path)
        {
            try
            {
                File.Move(newFile, path, overwrite: true);
            }
            catch (Exception ex) when ((ex is IOException or UnauthorizedAccessException) && File.Exists(path))
            {
                string aside = path + ".old";
                if (File.Exists(aside)) File.Delete(aside);
                File.Move(path, aside);
                File.Move(newFile, path);
            }
        }

        private static void TrySetTime(string path, DateTime when)
        {
            try { File.SetLastWriteTime(path, when); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }
    }
}
