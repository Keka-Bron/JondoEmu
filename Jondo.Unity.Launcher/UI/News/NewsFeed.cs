using System;
using System.IO;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Jondo.Unity.Launcher.Setup;

namespace Jondo.Unity.Launcher.UI.News
{
    /// <summary>What the News page shows of a release: its tag, title, date, notes and page.</summary>
    internal sealed record ReleaseNews(string Repository, string Tag, string Name, DateTime PublishedAt, string Notes)
    {
        /// <summary>The release's page on GitHub.</summary>
        public string Url => $"https://github.com/{Repository}/releases/tag/{Uri.EscapeDataString(Tag)}";

        public static ReleaseNews From(string repository, Release release)
            => new(repository, release.Tag, release.Name, release.PublishedAt, release.Notes);
    }

    /// <summary>
    /// The latest release's notes for the News page: asked of GitHub once per start of the launcher,
    /// and kept on disk so that the page still has something to say without a connection.
    /// </summary>
    /// <remarks>
    /// The same anonymous API the updater uses (60 questions an hour per address); a start asks
    /// once more, only when the page is opened. The copy lives next to the launcher's
    /// preferences, «news.json»; a broken copy is as good as none.
    /// </remarks>
    internal sealed class NewsFeed
    {
        private readonly Func<CancellationToken, Task<Release>> _latest;
        private readonly string _cacheFile;

        public NewsFeed(string repository, string cacheFile, Func<CancellationToken, Task<Release>>? latest = null)
        {
            Repository = repository;
            _cacheFile = cacheFile;
            _latest = latest ?? (cancel => new ReleaseFeed(repository).LatestAsync(cancel));
        }

        public string Repository { get; }

        /// <summary>The copy saved the last time GitHub answered; null when there is none.</summary>
        public ReleaseNews? Cached()
        {
            try
            {
                if (!File.Exists(_cacheFile)) return null;
                var news = JsonSerializer.Deserialize<ReleaseNews>(File.ReadAllText(_cacheFile));
                return news is { Tag.Length: > 0 } && news.Repository == Repository ? news : null;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or NotSupportedException)
            {
                return null;
            }
        }

        /// <summary>The latest release from GitHub, saved for the next time; it throws when GitHub cannot be asked.</summary>
        public async Task<ReleaseNews> FetchAsync(CancellationToken cancel = default)
        {
            var news = ReleaseNews.From(Repository, await _latest(cancel));
            try
            {
                string? folder = Path.GetDirectoryName(_cacheFile);
                if (!string.IsNullOrEmpty(folder)) Directory.CreateDirectory(folder);
                File.WriteAllText(_cacheFile, JsonSerializer.Serialize(news));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // Without a copy the page only loses its offline fallback.
            }
            return news;
        }

        /// <summary>Where the launcher keeps its copy: beside its preferences.</summary>
        public static string DefaultCacheFile
            => Path.Combine(Path.GetDirectoryName(Jondo.Unity.Launcher.ClientInstallation.PreferencesFile) ?? Path.GetTempPath(),
                            "news.json");
    }
}
