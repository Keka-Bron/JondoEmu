using System;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;

namespace Jondo.Unity.Launcher.Setup
{
    /// <summary>How a download goes: bytes done of the total, and the pace.</summary>
    internal readonly record struct Transfer(long Done, long Total, double BytesPerSecond)
    {
        public double Fraction => Total <= 0 ? 0 : Math.Clamp(Done / (double)Total, 0, 1);
    }

    /// <summary>
    /// Downloads a URL into a file, picking up where a cut left it: what has arrived is kept
    /// beside the target as <c>.part</c> and asked for again from there (an HTTP range). Retries
    /// a dropped connection a few times with a growing wait, and checks the SHA-256 when one is given.
    /// </summary>
    internal static class Downloader
    {
        private const int Attempts = 6;

        public static async Task DownloadAsync(string url, string target, long expectedSize, string? sha256,
                                               IProgress<Transfer>? progress, CancellationToken cancel,
                                               HttpClient? http = null)
        {
            http ??= Http.Shared;
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(target))!);
            string part = target + ".part";

            // A finished file already there, with the right hash, is not fetched again.
            if (sha256 != null && File.Exists(target) && UpdatePlan.HashOf(target).Equals(sha256, StringComparison.OrdinalIgnoreCase))
            {
                long size = new FileInfo(target).Length;
                progress?.Report(new Transfer(size, size, 0));
                return;
            }
            // Without a hash, the size: a retried install does not fetch its zip twice.
            if (sha256 == null && expectedSize > 0 && File.Exists(target) && new FileInfo(target).Length == expectedSize)
            {
                progress?.Report(new Transfer(expectedSize, expectedSize, 0));
                return;
            }

            var meter = new PaceMeter();
            Exception? last = null;
            for (int attempt = 1; attempt <= Attempts; attempt++)
            {
                cancel.ThrowIfCancellationRequested();
                try
                {
                    long have = File.Exists(part) ? new FileInfo(part).Length : 0;
                    if (expectedSize > 0 && have > expectedSize) { File.Delete(part); have = 0; }

                    using var request = new HttpRequestMessage(HttpMethod.Get, url);
                    if (have > 0) request.Headers.Range = new RangeHeaderValue(have, null);
                    using var answer = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancel);

                    if (answer.StatusCode == HttpStatusCode.RequestedRangeNotSatisfiable && have > 0 && have == expectedSize)
                    {
                        // Everything had arrived already; only the move was missing.
                    }
                    else
                    {
                        answer.EnsureSuccessStatusCode();
                        // A server that ignores the range sends everything again from the start.
                        if (have > 0 && answer.StatusCode != HttpStatusCode.PartialContent) have = 0;
                        long total = expectedSize > 0 ? expectedSize
                                   : (answer.Content.Headers.ContentLength ?? -1) + (have > 0 ? have : 0);

                        await using var source = await answer.Content.ReadAsStreamAsync(cancel);
                        await using var sink = new FileStream(part, have > 0 ? FileMode.Append : FileMode.Create,
                                                              FileAccess.Write, FileShare.None, 1 << 20, useAsync: true);
                        var buffer = new byte[1 << 17];
                        long done = have;
                        meter.Restart(done);
                        int read;
                        while ((read = await source.ReadAsync(buffer, cancel)) > 0)
                        {
                            await sink.WriteAsync(buffer.AsMemory(0, read), cancel);
                            done += read;
                            if (meter.Due(done) is { } pace) progress?.Report(new Transfer(done, total, pace));
                        }
                        progress?.Report(new Transfer(done, Math.Max(total, done), meter.Pace));
                    }

                    if (expectedSize > 0 && new FileInfo(part).Length != expectedSize)
                        throw new IOException($"the download stopped at {new FileInfo(part).Length} of {expectedSize} bytes");

                    if (sha256 != null)
                    {
                        string got = UpdatePlan.HashOf(part);
                        if (!got.Equals(sha256, StringComparison.OrdinalIgnoreCase))
                        {
                            File.Delete(part);
                            throw new SetupException($"{Path.GetFileName(target)} arrived damaged (its hash is not the release's).");
                        }
                    }
                    File.Move(part, target, overwrite: true);
                    return;
                }
                catch (OperationCanceledException) { throw; }
                catch (SetupException) when (attempt < Attempts) { last = null; }
                catch (Exception ex) when (ex is HttpRequestException or IOException)
                {
                    last = ex;
                    Console.WriteLine($"[Setup] {url}: attempt {attempt} failed ({ex.Message}).");
                }
                await Task.Delay(TimeSpan.FromSeconds(Math.Min(30, 2 * attempt * attempt)), cancel);
            }
            throw new SetupException($"Could not download {Path.GetFileName(target)}: {last?.Message ?? "it kept arriving damaged"}.", last);
        }
    }

    /// <summary>The bytes per second of a transfer, a few reports a second at most.</summary>
    internal sealed class PaceMeter
    {
        private readonly Stopwatch _clock = Stopwatch.StartNew();
        private long _markBytes;
        private TimeSpan _markTime;
        // Far enough back for the first report to be due; MinValue would overflow the subtraction.
        private TimeSpan _lastReport = TimeSpan.FromDays(-1);

        public double Pace { get; private set; }

        public void Restart(long done)
        {
            _markBytes = done;
            _markTime = _clock.Elapsed;
        }

        /// <summary>The pace when a report is due (five a second), null otherwise.</summary>
        public double? Due(long done)
        {
            var now = _clock.Elapsed;
            if (now - _lastReport < TimeSpan.FromMilliseconds(200)) return null;
            _lastReport = now;
            double seconds = (now - _markTime).TotalSeconds;
            if (seconds >= 1)
            {
                double pace = (done - _markBytes) / seconds;
                Pace = Pace <= 0 ? pace : Pace * 0.7 + pace * 0.3;
                _markBytes = done;
                _markTime = now;
            }
            return Pace;
        }
    }
}
