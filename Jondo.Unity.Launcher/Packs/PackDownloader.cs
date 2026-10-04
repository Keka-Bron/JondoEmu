using System;
using System.Buffers;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Jondo.Unity.Cytrus;
using Microsoft.Win32.SafeHandles;

namespace Jondo.Unity.Launcher.Packs
{
    /// <summary>
    /// Rebuilds files of a fragment from Ankama's bundles, resumably.
    /// </summary>
    /// <remarks>
    /// How it works, per file set:
    ///
    ///   - every target gets a <c>&lt;file&gt;.part</c> the size of the finished file;
    ///   - bundles are fetched four at a time, one range request each, and read as a stream: each
    ///     chunk's SHA-1 is checked before it is written at its offset in every file it belongs
    ///     to, so a bundle is never held in memory and a corrupt chunk never reaches the disk;
    ///   - a bundle that fails (network, bad chunk, stalled transfer) is retried with backoff;
    ///   - each finished bundle is flushed to disk and then appended to a journal in the pack
    ///     folder, so an interruption loses at most the bundles that were in flight;
    ///   - when every bundle is in, each <c>.part</c> is hashed whole and only then renamed over
    ///     its final name.
    ///
    /// The journal is tied to the exact set of files it was started for (names, sizes and
    /// hashes). If that changes, or a <c>.part</c> file is missing or the wrong size, the journal
    /// is discarded and the download starts over: resuming onto the wrong files would only show
    /// up as a hash mismatch at the very end.
    /// </remarks>
    internal sealed class PackDownloader
    {
        public const string JournalName = "jondo-download.journal";
        public const string PartSuffix = ".part";

        private const string JournalHeader = "jondo-cytrus-journal 1 ";

        /// <summary>Bundles are at most about 42 MiB, so no chunk can be larger than this.</summary>
        private const long LargestChunk = 64L << 20;

        private readonly HttpClient _http;

        public PackDownloader(HttpClient http) => _http = http;

        public int Concurrency { get; init; } = 4;

        public int Attempts { get; init; } = 5;

        public Func<int, TimeSpan> Backoff { get; init; } = attempt => TimeSpan.FromSeconds(Math.Min(30, 1 << attempt));

        public TimeSpan StallTimeout { get; init; } = TimeSpan.FromSeconds(60);

        /// <summary>
        /// Downloads <paramref name="targets"/> into place under <paramref name="root"/>.
        /// </summary>
        /// <param name="packFolder">Where the journal lives; removed together with the pack.</param>
        public async Task DownloadAsync(
            string root,
            string packFolder,
            CytrusFragment fragment,
            IReadOnlyList<CytrusFile> targets,
            ProgressMeter meter,
            CancellationToken cancel)
        {
            var plan = CytrusPlan.For(fragment, targets);
            string[] finals = plan.Files.Select(f => Path.Combine(root, f.Name.Replace('/', Path.DirectorySeparatorChar))).ToArray();
            string[] parts = finals.Select(f => f + PartSuffix).ToArray();

            Directory.CreateDirectory(packFolder);
            string journalPath = Path.Combine(packFolder, JournalName);
            string identity = Identity(plan.Files);

            var done = LoadJournal(journalPath, identity);
            for (int i = 0; i < parts.Length && done != null; i++)
            {
                var part = new FileInfo(parts[i]);
                if (!part.Exists || part.Length != plan.Files[i].Size) done = null;
            }

            if (done == null)
            {
                done = new HashSet<Sha1Hash>();
                File.WriteAllText(journalPath, JournalHeader + identity + "\n");
            }

            var handles = new SafeFileHandle[parts.Length];
            try
            {
                for (int i = 0; i < parts.Length; i++)
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(parts[i])!);
                    handles[i] = File.OpenHandle(parts[i], FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.Read);
                    if (RandomAccess.GetLength(handles[i]) != plan.Files[i].Size)
                        RandomAccess.SetLength(handles[i], plan.Files[i].Size);
                }

                var pending = plan.Bundles.Where(b => !done.Contains(b.Hash)).ToList();
                meter.Start(PackPhase.Downloading, plan.Bytes, plan.Bytes - pending.Sum(b => b.Bytes));

                await using (var journal = new FileStream(journalPath, FileMode.Append, FileAccess.Write, FileShare.Read))
                {
                    var gate = new object();
                    var options = new ParallelOptions { MaxDegreeOfParallelism = Concurrency, CancellationToken = cancel };
                    await Parallel.ForEachAsync(pending, options, async (job, token) =>
                    {
                        await FetchWithRetriesAsync(job, handles, meter, token);

                        // The bytes first, then the line that says they are there.
                        foreach (int file in job.Pieces.SelectMany(p => p.Targets).Select(t => t.File).Distinct())
                            RandomAccess.FlushToDisk(handles[file]);

                        byte[] line = Encoding.ASCII.GetBytes(job.Hash + "\n");
                        lock (gate)
                        {
                            journal.Write(line);
                            journal.Flush(flushToDisk: true);
                        }
                    });
                }

                // Every chunk was checked on the way in, but the file is only put in place once
                // its whole hash is the manifest's.
                meter.Start(PackPhase.Verifying, plan.Files.Sum(f => f.Size));
                for (int i = 0; i < handles.Length; i++)
                {
                    var hash = await HashAsync(handles[i], plan.Files[i].Size, meter, cancel);
                    if (hash != plan.Files[i].Hash)
                    {
                        // Start that file over next time. Its .part is removed, which also
                        // invalidates the journal: bytes on disk that the journal vouched for were
                        // wrong, so none of its claims are worth keeping.
                        handles[i].Dispose();
                        TryDelete(parts[i]);
                        TryDelete(journalPath);
                        throw new PackException(PackError.VerifyFailed, plan.Files[i].Name);
                    }
                }
            }
            finally
            {
                foreach (var handle in handles) handle?.Dispose();
            }

            for (int i = 0; i < parts.Length; i++)
            {
                try
                {
                    File.Move(parts[i], finals[i], overwrite: true);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    throw new PackException(PackError.DiskError, finals[i] + ": " + ex.Message, ex);
                }
            }

            TryDelete(journalPath);
        }

        /// <summary>
        /// What the journal is for: the file set, as a hash over each file's name, size and hash.
        /// </summary>
        public static string Identity(IEnumerable<CytrusFile> files)
        {
            var text = new StringBuilder();
            foreach (var file in files.OrderBy(f => f.Name, StringComparer.Ordinal))
                text.Append(file.Name).Append('\t').Append(file.Size).Append('\t').Append(file.Hash).Append('\n');
            return Convert.ToHexStringLower(SHA1.HashData(Encoding.UTF8.GetBytes(text.ToString())));
        }

        /// <summary>The bundles the journal says are on disk, or null if it is for another file set.</summary>
        public static HashSet<Sha1Hash>? LoadJournal(string path, string identity)
        {
            try
            {
                if (!File.Exists(path)) return null;
                var lines = File.ReadAllLines(path);
                if (lines.Length == 0 || lines[0] != JournalHeader + identity) return null;

                var done = new HashSet<Sha1Hash>();
                foreach (string line in lines.Skip(1))
                {
                    // A line cut short by a crash is simply not a finished bundle.
                    if (line.Length == Sha1Hash.Length * 2 && IsHex(line)) done.Add(Sha1Hash.Parse(line));
                }
                return done;
            }
            catch (IOException)
            {
                return null;
            }
        }

        private static bool IsHex(string text) => text.All(Uri.IsHexDigit);

        // ─── One bundle ──────────────────────────────────────────────────────────────────

        private async Task FetchWithRetriesAsync(
            CytrusPlan.BundleJob job, SafeFileHandle[] handles, ProgressMeter meter, CancellationToken cancel)
        {
            for (int attempt = 1; ; attempt++)
            {
                long counted = 0;
                try
                {
                    await FetchAsync(job, handles, bytes => { counted += bytes; meter.Add(bytes); }, cancel);
                    return;
                }
                catch (Exception ex) when (IsTransient(ex, cancel))
                {
                    meter.Add(-counted);
                    if (attempt >= Attempts)
                        throw new PackException(PackError.DownloadFailed, "bundle " + job.Hash + ": " + ex.Message, ex);
                    Program.LogDebug($"[Packs] Bundle {job.Hash}, attempt {attempt} failed: {ex.Message}");
                    await Task.Delay(Backoff(attempt), cancel);
                }
            }
        }

        /// <summary>
        /// Worth another try: the network, the server, a stalled transfer or a chunk that did not
        /// match its hash. Not: the player cancelling, the disk, or a manifest that makes no sense.
        /// </summary>
        private static bool IsTransient(Exception ex, CancellationToken cancel)
            => !cancel.IsCancellationRequested
               && ex is HttpRequestException or IOException or OperationCanceledException or CorruptChunkException;

        private async Task FetchAsync(
            CytrusPlan.BundleJob job, SafeFileHandle[] handles, Action<long> counted, CancellationToken cancel)
        {
            if (job.Pieces.Any(p => p.Size > LargestChunk))
                throw new PackException(PackError.BadManifest, "bundle " + job.Hash + " lists a chunk larger than a bundle");

            using var stall = CancellationTokenSource.CreateLinkedTokenSource(cancel);
            stall.CancelAfter(StallTimeout);

            using var request = new HttpRequestMessage(HttpMethod.Get, CytrusCdn.BundleUrl(ManifestStore.Game, job.Hash));
            request.Headers.Range = new RangeHeaderValue(job.RangeStart, job.RangeEnd - 1);

            using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, stall.Token);
            if (!response.IsSuccessStatusCode)
            {
                int code = (int)response.StatusCode;
                string what = "bundle " + job.Hash + ": HTTP " + code;
                // A 404 or a 403 will not fix itself; timeouts, throttling and 5xx might.
                if (code >= 500 || code == 408 || code == 429) throw new HttpRequestException(what);
                throw new PackException(PackError.DownloadFailed, what);
            }

            // 206 starts where we asked. A server that ignores ranges sends the whole bundle
            // with a 200, and then the stream starts at zero.
            long position = 0;
            if (response.StatusCode == HttpStatusCode.PartialContent)
            {
                long? from = response.Content.Headers.ContentRange?.From;
                if (from != null && from != job.RangeStart)
                    throw new IOException("bundle " + job.Hash + ": the server answered from byte " + from);
                position = job.RangeStart;
            }

            await using var stream = await response.Content.ReadAsStreamAsync(stall.Token);
            int largest = (int)Math.Max(81920, job.Pieces.Max(p => p.Size));
            byte[] buffer = ArrayPool<byte>.Shared.Rent(largest);
            try
            {
                foreach (var piece in job.Pieces)
                {
                    if (piece.Offset < position)
                        throw new PackException(PackError.BadManifest, "bundle " + job.Hash + " has overlapping chunks");

                    // Chunks nobody asked for are read past, not stored.
                    long skip = piece.Offset - position;
                    while (skip > 0)
                    {
                        int step = (int)Math.Min(skip, buffer.Length);
                        await ReadExactlyAsync(stream, buffer.AsMemory(0, step), stall);
                        skip -= step;
                    }

                    int size = (int)piece.Size;
                    await ReadExactlyAsync(stream, buffer.AsMemory(0, size), stall);
                    position = piece.Offset + piece.Size;

                    if (Sha1Hash.Of(buffer.AsSpan(0, size)) != piece.Hash)
                        throw new CorruptChunkException("chunk " + piece.Hash + " of bundle " + job.Hash + " does not match its hash");

                    foreach (var target in piece.Targets)
                    {
                        try
                        {
                            RandomAccess.Write(handles[target.File], buffer.AsSpan(0, size), target.Offset);
                        }
                        catch (IOException ex)
                        {
                            // Disk full, or the file taken away: retrying the network will not help.
                            throw new PackException(PackError.DiskError, ex.Message, ex);
                        }
                    }

                    counted(size);
                }
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(buffer);
            }
        }

        private async Task ReadExactlyAsync(Stream stream, Memory<byte> into, CancellationTokenSource stall)
        {
            int got = 0;
            while (got < into.Length)
            {
                stall.CancelAfter(StallTimeout);
                int read = await stream.ReadAsync(into.Slice(got), stall.Token);
                if (read == 0) throw new EndOfStreamException("the bundle ended early");
                got += read;
            }
        }

        // ─── Hashing ─────────────────────────────────────────────────────────────────────

        /// <summary>The SHA-1 of the first <paramref name="length"/> bytes of an open file.</summary>
        public static async Task<Sha1Hash> HashAsync(SafeFileHandle handle, long length, ProgressMeter? meter, CancellationToken cancel)
        {
            using var sha = IncrementalHash.CreateHash(HashAlgorithmName.SHA1);
            byte[] buffer = ArrayPool<byte>.Shared.Rent(1 << 20);
            try
            {
                long at = 0;
                while (at < length)
                {
                    int want = (int)Math.Min(buffer.Length, length - at);
                    int read = await RandomAccess.ReadAsync(handle, buffer.AsMemory(0, want), at, cancel);
                    if (read == 0) break;
                    sha.AppendData(buffer, 0, read);
                    at += read;
                    meter?.Add(read);
                }
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(buffer);
            }
            return Sha1Hash.FromBytes(sha.GetHashAndReset());
        }

        /// <summary>The SHA-1 of a whole file on disk.</summary>
        public static async Task<Sha1Hash> HashFileAsync(string path, ProgressMeter? meter, CancellationToken cancel)
        {
            using var handle = File.OpenHandle(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            return await HashAsync(handle, RandomAccess.GetLength(handle), meter, cancel);
        }

        private static void TryDelete(string path)
        {
            try { File.Delete(path); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        }

        /// <summary>A chunk whose bytes are not the ones its hash names.</summary>
        private sealed class CorruptChunkException : Exception
        {
            public CorruptChunkException(string message) : base(message) { }
        }
    }
}
