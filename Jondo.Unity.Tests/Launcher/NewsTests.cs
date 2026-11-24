using System;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Threading.Tasks;
using Jondo.Unity.Launcher.Setup;
using Jondo.Unity.Launcher.UI;
using Jondo.Unity.Launcher.UI.News;
using Xunit;

namespace Jondo.Unity.Tests.Launcher
{
    /// <summary>
    /// The launcher's News page: a release's Markdown notes turned into blocks, the copy kept on
    /// disk for the offline case, and the roadmap in every language.
    /// </summary>
    public class NewsTests : IDisposable
    {
        private readonly string _folder = Path.Combine(Path.GetTempPath(), "jondo-news-" + Guid.NewGuid().ToString("N"));

        public NewsTests() => Directory.CreateDirectory(_folder);

        public void Dispose()
        {
            try { Directory.Delete(_folder, recursive: true); } catch (IOException) { }
        }

        [Fact]
        public void Headings_bullets_and_paragraphs_come_apart()
        {
            var blocks = ReleaseNotes.Parse(
                "# Title\r\n\r\n## Section ##\n\n- one\n- two\n  - nested\n1. numbered\n\nA paragraph\nthat goes on.\n\n---\n#### Deep");

            Assert.Equal(new[] { NewsBlockKind.Heading, NewsBlockKind.Heading, NewsBlockKind.Bullet, NewsBlockKind.Bullet,
                                 NewsBlockKind.Bullet, NewsBlockKind.Bullet, NewsBlockKind.Paragraph, NewsBlockKind.Heading },
                         blocks.Select(b => b.Kind));
            Assert.Equal("Section", blocks[1].PlainText);
            Assert.Equal(2, blocks[1].Level);
            Assert.Equal(1, blocks[4].Level);
            Assert.Equal("numbered", blocks[5].PlainText);
            Assert.Equal("A paragraph that goes on.", blocks[6].PlainText);
            Assert.Equal(3, blocks[7].Level);   // capped
        }

        [Fact]
        public void Bold_code_and_links_inside_a_line()
        {
            var spans = ReleaseNotes.Inline("Run **the server** with `--watch`, see [the docs](https://x.y/z) &amp; <b>more</b>");

            Assert.Contains(spans, s => s.Bold && s.Text == "the server");
            Assert.Contains(spans, s => s.Code && s.Text == "--watch");
            Assert.Equal("Run the server with --watch, see the docs & more", string.Concat(spans.Select(s => s.Text)));
        }

        [Fact]
        public void A_continuation_line_stays_with_its_bullet_and_comments_vanish()
        {
            var blocks = ReleaseNotes.Parse("<!-- hidden -->\n- first line\n  carried on\n\n```\ncode line\n```");

            Assert.Equal(2, blocks.Count);
            Assert.Equal("first line carried on", blocks[0].PlainText);
            Assert.True(blocks[1].Spans.Single().Code);
            Assert.Equal("code line", blocks[1].PlainText);
        }

        [Fact]
        public void Empty_notes_give_no_blocks()
        {
            Assert.Empty(ReleaseNotes.Parse(null));
            Assert.Empty(ReleaseNotes.Parse("  \n\n"));
        }

        [Fact]
        public async Task A_fetched_release_is_kept_for_the_next_start()
        {
            string cache = Path.Combine(_folder, "sub", "news.json");
            var release = new Release("v9.9.9", "Nine", "- a note", new DateTime(2026, 11, 24, 0, 0, 0, DateTimeKind.Utc),
                                      Array.Empty<ReleaseAsset>());
            var feed = new NewsFeed("owner/repo", cache, _ => Task.FromResult(release));

            Assert.Null(feed.Cached());
            var news = await feed.FetchAsync();

            Assert.Equal("https://github.com/owner/repo/releases/tag/v9.9.9", news.Url);
            var kept = new NewsFeed("owner/repo", cache, _ => throw new HttpRequestException()).Cached();
            Assert.NotNull(kept);
            Assert.Equal("Nine", kept!.Name);
            Assert.Equal("- a note", kept.Notes);
        }

        [Fact]
        public async Task A_failed_fetch_throws_and_keeps_the_old_copy()
        {
            string cache = Path.Combine(_folder, "news.json");
            var release = new Release("v1", "One", "", DateTime.UtcNow, Array.Empty<ReleaseAsset>());
            await new NewsFeed("owner/repo", cache, _ => Task.FromResult(release)).FetchAsync();

            var offline = new NewsFeed("owner/repo", cache, _ => throw new HttpRequestException("offline"));
            await Assert.ThrowsAsync<HttpRequestException>(() => offline.FetchAsync());
            Assert.Equal("v1", offline.Cached()!.Tag);
        }

        [Fact]
        public void A_broken_copy_or_another_repository_is_none()
        {
            string cache = Path.Combine(_folder, "news.json");
            File.WriteAllText(cache, "{ not json");
            Assert.Null(new NewsFeed("owner/repo", cache).Cached());

            File.WriteAllText(cache, "{\"Repository\":\"other/repo\",\"Tag\":\"v1\",\"Name\":\"\",\"PublishedAt\":\"2026-11-24T00:00:00Z\",\"Notes\":\"\"}");
            Assert.Null(new NewsFeed("owner/repo", cache).Cached());
        }

        [Theory]
        [InlineData(Language.Es)]
        [InlineData(Language.En)]
        [InlineData(Language.Fr)]
        [InlineData(Language.Pt)]
        public void The_roadmap_ends_on_the_3_8_client_with_the_bellephone_in_january_2027(Language language)
        {
            var steps = Roadmap.For(language);

            Assert.Equal(RoadmapState.Next, steps[^1].State);
            Assert.Contains("3.8", steps[^1].Title);
            Assert.Contains("Bellephone", steps[^1].Text);
            Assert.Contains("2027", steps[^1].When);
            Assert.Single(steps, s => s.State == RoadmapState.Now);
        }
    }
}
