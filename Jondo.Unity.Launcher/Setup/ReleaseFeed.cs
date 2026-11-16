using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Reflection;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace Jondo.Unity.Launcher.Setup
{
    /// <summary>One file of a release on GitHub.</summary>
    internal sealed record ReleaseAsset(string Name, string Url, long Size);

    /// <summary>A published release: its tag, its notes and its files.</summary>
    internal sealed record Release(string Tag, string Name, string Notes, DateTime PublishedAt, IReadOnlyList<ReleaseAsset> Assets)
    {
        public ReleaseAsset? Asset(string name)
            => Assets.FirstOrDefault(a => a.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// The repository the emulator updates from, read through GitHub's public API: its latest
    /// published release and the README the client's download link is written in.
    /// </summary>
    /// <remarks>
    /// Anonymous: the API lets an address ask 60 times an hour, and a start of the launcher asks
    /// once. The repository is the one in the settings, «owner/name»; by default the one the
    /// launcher was built from (<see cref="DefaultRepository"/>).
    /// </remarks>
    internal sealed class ReleaseFeed
    {
        private readonly HttpClient _http;

        /// <param name="apiBase">GitHub's API; JONDO_RELEASE_API stands in for it in tests, a local copy of a release.</param>
        /// <param name="rawBase">GitHub's raw files; JONDO_RELEASE_RAW stands in for it the same way.</param>
        public ReleaseFeed(string repository, HttpClient? http = null, string? apiBase = null, string? rawBase = null)
        {
            if (!IsRepository(repository)) throw new ArgumentException($"Not a GitHub repository: «{repository}»", nameof(repository));
            Repository = repository.Trim();
            _http = http ?? Http.Shared;
            ApiBase = (apiBase ?? Override("JONDO_RELEASE_API") ?? "https://api.github.com").TrimEnd('/');
            RawBase = (rawBase ?? Override("JONDO_RELEASE_RAW") ?? "https://raw.githubusercontent.com").TrimEnd('/');
        }

        private static string? Override(string variable)
            => Environment.GetEnvironmentVariable(variable) is { Length: > 0 } value ? value : null;

        public string Repository { get; }
        public string ApiBase { get; }
        public string RawBase { get; }

        /// <summary>The repository the launcher was built from, as the build wrote it in the assembly.</summary>
        public static string DefaultRepository
            => typeof(ReleaseFeed).Assembly.GetCustomAttributes<AssemblyMetadataAttribute>()
                   .FirstOrDefault(a => a.Key == "JondoRepository")?.Value is { Length: > 0 } built && IsRepository(built)
                ? built
                : "Keka-Bron/JondoEmu";

        private static readonly Regex RepositoryShape = new(@"^[A-Za-z0-9](?:[A-Za-z0-9-]{0,38})/[A-Za-z0-9._-]{1,100}$", RegexOptions.CultureInvariant);

        /// <summary>Whether a text is «owner/name» as GitHub allows them.</summary>
        public static bool IsRepository(string? text) => text != null && RepositoryShape.IsMatch(text.Trim());

        /// <summary>«https://github.com/owner/name(.git)» or «owner/name» to «owner/name»; null when neither.</summary>
        public static string? Normalize(string? text)
        {
            if (string.IsNullOrWhiteSpace(text)) return null;
            string t = text.Trim();
            var url = Regex.Match(t, @"^(?:https?://)?(?:www\.)?github\.com/([^/\s]+/[^/\s#?]+?)(?:\.git)?/?$", RegexOptions.IgnoreCase);
            if (url.Success) t = url.Groups[1].Value;
            return IsRepository(t) ? t : null;
        }

        /// <summary>The latest published release, never a draft nor a pre-release.</summary>
        public async Task<Release> LatestAsync(CancellationToken cancel = default)
        {
            using var answer = await _http.GetAsync($"{ApiBase}/repos/{Repository}/releases/latest", cancel);
            if ((int)answer.StatusCode == 404)
                throw new SetupException($"{Repository} has no published release.");
            if ((int)answer.StatusCode == 403 || (int)answer.StatusCode == 429)
                throw new SetupException("GitHub refuses more questions from this address for now; try again in a while.");
            answer.EnsureSuccessStatusCode();
            using var json = JsonDocument.Parse(await answer.Content.ReadAsStringAsync(cancel));
            return Parse(json.RootElement);
        }

        internal static Release Parse(JsonElement root)
        {
            var assets = new List<ReleaseAsset>();
            if (root.TryGetProperty("assets", out var list) && list.ValueKind == JsonValueKind.Array)
                foreach (var a in list.EnumerateArray())
                    assets.Add(new ReleaseAsset(a.GetProperty("name").GetString() ?? "",
                                                a.GetProperty("browser_download_url").GetString() ?? "",
                                                a.TryGetProperty("size", out var s) ? s.GetInt64() : 0));
            DateTime published = root.TryGetProperty("published_at", out var p) && p.ValueKind == JsonValueKind.String
                ? p.GetDateTime() : DateTime.MinValue;
            return new Release(root.GetProperty("tag_name").GetString() ?? "",
                               root.TryGetProperty("name", out var n) ? n.GetString() ?? "" : "",
                               root.TryGetProperty("body", out var b) ? b.GetString() ?? "" : "",
                               published, assets);
        }

        /// <summary>The README at a tag (or a branch), as text.</summary>
        public async Task<string> ReadmeAsync(string reference, CancellationToken cancel = default)
            => await _http.GetStringAsync($"{RawBase}/{Repository}/{reference}/README.md", cancel);

        /// <summary>A file of the repository at a tag, as GitHub stores it.</summary>
        public string RawUrl(string reference, string path)
            => $"{RawBase}/{Repository}/{reference}/{string.Join('/', path.Replace('\\', '/').Split('/').Select(Uri.EscapeDataString))}";

        private static readonly Regex ClientLink = new(
            @"\[[^\]]*?Dofus\s+(?<version>\d+(?:\.\d+){2,3})[^\]]*\]\((?<url>https?://[^)\s]+)\)",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

        /// <summary>
        /// The client's download link the README gives, «[Dofus 3.6.10.11 — download](url)», and
        /// the version it names; null when there is none.
        /// </summary>
        public static (string Version, string Url)? ClientLinkIn(string readme)
        {
            var match = ClientLink.Match(readme ?? "");
            return match.Success ? (match.Groups["version"].Value, match.Groups["url"].Value) : null;
        }

        /// <summary>«v0.2.33» against «v0.2.30»: whether the first is newer. Tags that are not versions compare as text.</summary>
        public static bool IsNewer(string candidate, string installed)
        {
            if (string.IsNullOrEmpty(installed)) return !string.IsNullOrEmpty(candidate);
            if (System.Version.TryParse(candidate.TrimStart('v', 'V'), out var a) &&
                System.Version.TryParse(installed.TrimStart('v', 'V'), out var b))
                return a > b;
            return !string.Equals(candidate, installed, StringComparison.OrdinalIgnoreCase);
        }
    }

    /// <summary>A step that cannot go on, with a message the player can read.</summary>
    internal sealed class SetupException : Exception
    {
        public SetupException(string message, Exception? inner = null) : base(message, inner) { }
    }

    /// <summary>The launcher's one HTTP client for downloads, with a name GitHub accepts.</summary>
    internal static class Http
    {
        public static HttpClient Shared { get; } = Create();

        private static HttpClient Create()
        {
            var handler = new SocketsHttpHandler
            {
                AutomaticDecompression = System.Net.DecompressionMethods.All,
                PooledConnectionLifetime = TimeSpan.FromMinutes(5),
                ConnectTimeout = TimeSpan.FromSeconds(20),
            };
            var http = new HttpClient(handler) { Timeout = System.Threading.Timeout.InfiniteTimeSpan };
            string version = typeof(Http).Assembly.GetName().Version?.ToString() ?? "0";
            http.DefaultRequestHeaders.UserAgent.ParseAdd($"JondoEmu-Launcher/{version}");
            http.DefaultRequestHeaders.Accept.ParseAdd("*/*");
            return http;
        }
    }
}
