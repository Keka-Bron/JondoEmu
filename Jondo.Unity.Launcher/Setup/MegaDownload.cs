using System;
using System.Buffers.Binary;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace Jondo.Unity.Launcher.Setup
{
    /// <summary>
    /// A public MEGA file link, «https://mega.nz/file/HANDLE#KEY», downloaded and decrypted as it
    /// arrives.
    /// </summary>
    /// <remarks>
    /// <para>
    /// MEGA stores the file encrypted with AES-128 in counter mode; the key is in the link, after
    /// the «#», and never reaches MEGA's servers. Its 32 bytes are the AES key XOR-folded in two
    /// halves, then the counter's nonce (8 bytes) and the file's MAC (8). The API, asked for the
    /// handle, answers the size, the encrypted attributes (the name) and a URL to fetch the bytes from.
    /// </para>
    /// <para>
    /// Anonymous downloads have a transfer quota; past it, the server answers 509 and how long
    /// until it lets this address go on. The download is kept as <c>.part</c>, already
    /// decrypted, and goes on from the byte where it stopped: the counter of a block is its
    /// offset over sixteen, so any block can be decrypted on its own.
    /// </para>
    /// </remarks>
    internal sealed class MegaLink
    {
        private static readonly Regex NewShape = new(@"mega\.(?:nz|co\.nz)/file/(?<h>[A-Za-z0-9_-]{8})#(?<k>[A-Za-z0-9_-]{43})", RegexOptions.CultureInvariant);
        private static readonly Regex OldShape = new(@"mega\.(?:nz|co\.nz)/#!(?<h>[A-Za-z0-9_-]{8})!(?<k>[A-Za-z0-9_-]{43})", RegexOptions.CultureInvariant);

        public string Handle { get; }
        internal byte[] Key { get; }
        internal byte[] Nonce { get; }

        private MegaLink(string handle, byte[] raw)
        {
            Handle = handle;
            Key = new byte[16];
            for (int i = 0; i < 16; i++) Key[i] = (byte)(raw[i] ^ raw[i + 16]);
            Nonce = raw.AsSpan(16, 8).ToArray();
        }

        public static bool IsMega(string url) => TryParse(url) != null;

        public static MegaLink? TryParse(string url)
        {
            var m = NewShape.Match(url ?? "");
            if (!m.Success) m = OldShape.Match(url ?? "");
            if (!m.Success) return null;
            byte[] raw = FromBase64Url(m.Groups["k"].Value);
            return raw.Length == 32 ? new MegaLink(m.Groups["h"].Value, raw) : null;
        }

        internal static byte[] FromBase64Url(string text)
        {
            string s = text.Replace('-', '+').Replace('_', '/');
            s += new string('=', (4 - s.Length % 4) % 4);
            return Convert.FromBase64String(s);
        }

        /// <summary>What the API says of the file: its name, its size and where its bytes are.</summary>
        internal sealed record Info(string Name, long Size, string Url);

        private const string Api = "https://g.api.mega.co.nz/cs";
        private static int _sequence = Random.Shared.Next(1, 1 << 30);

        public async Task<Info> InfoAsync(HttpClient http, CancellationToken cancel, bool withUrl = true)
        {
            string body = withUrl ? $"[{{\"a\":\"g\",\"g\":1,\"ssl\":0,\"p\":\"{Handle}\"}}]"
                                  : $"[{{\"a\":\"g\",\"p\":\"{Handle}\"}}]";
            for (int attempt = 1; ; attempt++)
            {
                using var answer = await http.PostAsync($"{Api}?id={Interlocked.Increment(ref _sequence)}",
                                                        new StringContent(body, Encoding.UTF8, "application/json"), cancel);
                answer.EnsureSuccessStatusCode();
                using var json = JsonDocument.Parse(await answer.Content.ReadAsStringAsync(cancel));
                var root = json.RootElement;
                // A bare number is an error: -3 "try again", -9 gone, -16 blocked, -18 unavailable for now.
                var first = root.ValueKind == JsonValueKind.Array ? root[0] : root;
                if (first.ValueKind == JsonValueKind.Number)
                {
                    int code = first.GetInt32();
                    if ((code == -3 || code == -18) && attempt < 5)
                    {
                        await Task.Delay(TimeSpan.FromSeconds(3 * attempt), cancel);
                        continue;
                    }
                    throw new SetupException(code switch
                    {
                        -9 => "The client's MEGA link no longer exists.",
                        -16 => "MEGA has blocked the client's file.",
                        -11 => "MEGA refuses access to the client's file.",
                        _ => $"MEGA answered an error ({code}) for the client's file.",
                    });
                }
                long size = first.GetProperty("s").GetInt64();
                string name = NameIn(first.GetProperty("at").GetString() ?? "");
                string url = first.TryGetProperty("g", out var g) ? g.GetString() ?? "" : "";
                return new Info(name, size, url);
            }
        }

        /// <summary>The attributes, «MEGA{"n":"name"}», decrypted with the key in CBC and a zero IV.</summary>
        internal string NameIn(string attributes)
        {
            try
            {
                byte[] at = FromBase64Url(attributes);
                using var aes = Aes.Create();
                aes.Key = Key;
                byte[] plain = aes.DecryptCbc(at, new byte[16], PaddingMode.None);
                string text = Encoding.UTF8.GetString(plain).TrimEnd('\0');
                if (!text.StartsWith("MEGA{", StringComparison.Ordinal)) return "";
                using var json = JsonDocument.Parse(text[4..]);
                return json.RootElement.TryGetProperty("n", out var n) ? n.GetString() ?? "" : "";
            }
            catch (Exception ex) when (ex is FormatException or CryptographicException or JsonException)
            {
                return "";
            }
        }

        /// <summary>
        /// Downloads the file into <paramref name="target"/>, decrypted, resuming a <c>.part</c>
        /// left before. While the quota holds it back, <paramref name="waiting"/> is told until when.
        /// </summary>
        public async Task DownloadAsync(string target, IProgress<Transfer>? progress, Action<DateTime?>? waiting,
                                        CancellationToken cancel, HttpClient? http = null, long? stopAt = null)
        {
            http ??= Http.Shared;
            string part = target + ".part";
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(target))!);
            var info = await InfoAsync(http, cancel);
            long size = info.Size;
            long end = stopAt is { } s ? Math.Min(s, size) : size;
            if (File.Exists(target) && new FileInfo(target).Length == size)
            {
                progress?.Report(new Transfer(size, size, 0));
                return;
            }

            var meter = new PaceMeter();
            int failures = 0;
            string url = info.Url;
            while (true)
            {
                cancel.ThrowIfCancellationRequested();
                long have = File.Exists(part) ? new FileInfo(part).Length : 0;
                // Whole blocks only: a counter starts on a block.
                long start = have - have % 16;
                if (start >= end) break;
                try
                {
                    using var request = new HttpRequestMessage(HttpMethod.Get, url);
                    request.Headers.Range = new RangeHeaderValue(start, end - 1);
                    using var answer = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancel);
                    if ((int)answer.StatusCode == 509)
                    {
                        // The quota: how long MEGA says, or a quarter of an hour.
                        int seconds = answer.Headers.TryGetValues("X-MEGA-Time-Left", out var left)
                                      && int.TryParse(System.Linq.Enumerable.FirstOrDefault(left), out int t) && t > 0 ? t : 900;
                        var until = DateTime.Now.AddSeconds(seconds + 5);
                        Console.WriteLine($"[Setup] MEGA's transfer quota is spent; going on at {until:HH:mm}.");
                        waiting?.Invoke(until);
                        await Task.Delay(TimeSpan.FromSeconds(seconds + 5), cancel);
                        waiting?.Invoke(null);
                        url = (await InfoAsync(http, cancel)).Url;
                        continue;
                    }
                    if (answer.StatusCode is HttpStatusCode.Forbidden or HttpStatusCode.NotFound or HttpStatusCode.Gone)
                    {
                        // The URL MEGA gave has expired: a new one.
                        url = (await InfoAsync(http, cancel)).Url;
                        if (++failures > 8) answer.EnsureSuccessStatusCode();
                        continue;
                    }
                    answer.EnsureSuccessStatusCode();
                    bool ranged = answer.StatusCode == HttpStatusCode.PartialContent;
                    if (!ranged) start = 0;

                    await using var source = await answer.Content.ReadAsStreamAsync(cancel);
                    await using var sink = new FileStream(part, FileMode.OpenOrCreate, FileAccess.Write, FileShare.None, 1 << 20, useAsync: true);
                    sink.SetLength(start);
                    sink.Position = start;
                    using var decryptor = new CtrDecryptor(Key, Nonce, start);
                    var buffer = new byte[1 << 18];
                    long done = start;
                    meter.Restart(done);
                    int read;
                    while ((read = await ReadFullAsync(source, buffer, cancel)) > 0)
                    {
                        if (done + read > end) read = (int)(end - done);
                        decryptor.Transform(buffer.AsSpan(0, read));
                        await sink.WriteAsync(buffer.AsMemory(0, read), cancel);
                        done += read;
                        if (meter.Due(done) is { } pace) progress?.Report(new Transfer(done, end, pace));
                        if (done >= end) break;
                    }
                    failures = 0;
                    progress?.Report(new Transfer(done, end, meter.Pace));
                }
                catch (OperationCanceledException) { throw; }
                catch (Exception ex) when (ex is HttpRequestException or IOException)
                {
                    if (++failures > 8) throw new SetupException($"The client's download from MEGA keeps failing: {ex.Message}", ex);
                    Console.WriteLine($"[Setup] MEGA: {ex.Message}; going on in a moment.");
                    await Task.Delay(TimeSpan.FromSeconds(Math.Min(60, 3 * failures * failures)), cancel);
                }
            }
            if (stopAt == null) File.Move(part, target, overwrite: true);
        }

        /// <summary>Fills the buffer as far as the stream allows, so the decryption works on whole blocks.</summary>
        private static async Task<int> ReadFullAsync(Stream source, byte[] buffer, CancellationToken cancel)
        {
            int total = 0;
            while (total < buffer.Length)
            {
                int read = await source.ReadAsync(buffer.AsMemory(total), cancel);
                if (read == 0) break;
                total += read;
            }
            return total;
        }
    }

    /// <summary>
    /// AES-128 in counter mode from any block: the counter is the nonce (8 bytes) followed by the
    /// block's index (8 bytes, big-endian). Encrypting the counters in batches is what keeps it
    /// at disk speed.
    /// </summary>
    internal sealed class CtrDecryptor : IDisposable
    {
        private readonly Aes _aes;
        private readonly byte[] _nonce;
        private ulong _block;
        private readonly byte[] _keystream = new byte[16];
        private int _used;

        public CtrDecryptor(byte[] key, byte[] nonce, long offset)
        {
            if (offset % 16 != 0) throw new ArgumentException("The offset must start a block.", nameof(offset));
            _aes = Aes.Create();
            _aes.Key = key;
            _nonce = nonce;
            _block = (ulong)(offset / 16);
            _used = 16;
        }

        public void Transform(Span<byte> data)
        {
            int i = 0;
            // What is left of a block's keystream from the last call.
            while (i < data.Length && _used < 16) data[i++] ^= _keystream[_used++];
            int whole = (data.Length - i) / 16;
            if (whole > 0)
            {
                var counters = new byte[whole * 16];
                for (int b = 0; b < whole; b++) Counter(counters.AsSpan(b * 16, 16), _block++);
                var stream = new byte[counters.Length];
                _aes.EncryptEcb(counters, stream, PaddingMode.None);
                var span = data.Slice(i, whole * 16);
                for (int j = 0; j < span.Length; j++) span[j] ^= stream[j];
                i += whole * 16;
            }
            if (i < data.Length)
            {
                Span<byte> counter = stackalloc byte[16];
                Counter(counter, _block++);
                _aes.EncryptEcb(counter, _keystream, PaddingMode.None);
                _used = 0;
                while (i < data.Length) data[i++] ^= _keystream[_used++];
            }
        }

        private void Counter(Span<byte> into, ulong block)
        {
            _nonce.CopyTo(into);
            BinaryPrimitives.WriteUInt64BigEndian(into[8..], block);
        }

        public void Dispose() => _aes.Dispose();
    }
}
