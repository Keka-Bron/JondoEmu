using System;
using System.IO;
using System.Linq;
using Jondo.Unity.Cytrus;
using Xunit;

namespace Jondo.Unity.Tests.Launcher
{
    /// <summary>
    /// The Cytrus manifest reader and the download plan built from it.
    /// </summary>
    /// <remarks>
    /// The real manifest is not in the repository (datos/cytrus is ignored, 50 MB each), so the
    /// test that reads it looks for it in this checkout and in the folders above it, which also
    /// finds the main tree's copy from a git worktree. Without it, that test passes without
    /// looking, like the other tests that need datos/.
    /// </remarks>
    public class CytrusManifestTests
    {
        private const string RealManifest = "6.0_3.6.10.10.manifest";

        [Fact]
        public void The_real_manifest_lists_31_files_per_texture_pack_with_contiguous_chunks()
        {
            string? path = FindUpwards(Path.Combine("datos", "cytrus", RealManifest));
            if (path == null) return;

            var manifest = CytrusManifest.Parse(File.ReadAllBytes(path));
            Assert.Contains("map_textures_2x", manifest.FragmentNames);
            Assert.Contains("map_textures_4x", manifest.FragmentNames);
            Assert.Equal(13, manifest.FragmentNames.Count);

            foreach (var (name, bundles, minGiB, maxGiB) in new[]
                     {
                         ("map_textures_2x", 313, 3.7, 3.9),
                         ("map_textures_4x", 670, 7.9, 8.2),
                     })
            {
                var fragment = manifest.Fragment(name)!;
                Assert.Equal(31, fragment.Files.Length);
                Assert.Equal(bundles, fragment.Bundles.Length);
                Assert.InRange(fragment.Size / (double)(1L << 30), minGiB, maxGiB);

                string folder = "Dofus_Data/StreamingAssets/Content/Map/Textures/" + name[^2..] + "/";
                Assert.All(fragment.Files, f => Assert.StartsWith(folder, f.Name));
                Assert.All(fragment.Files, f => Assert.False(f.Hash.IsEmpty));

                // The plan refuses files whose chunks do not tile them exactly, so building it
                // over the whole pack is the contiguity check. The packs use every bundle.
                var plan = CytrusPlan.For(fragment, fragment.Files);
                Assert.Equal(bundles, plan.Bundles.Count);
                Assert.True(plan.Bytes <= fragment.Size);
                Assert.All(plan.Bundles, b => Assert.True(b.RangeEnd - b.RangeStart < 64L << 20));
            }

            // The one file the research checked chunk by chunk against the installed client.
            var known = manifest.Fragment("map_textures_2x")!.Files
                .Single(f => f.Name.EndsWith("mapgfx_2x_130000_assets_all.bundle", StringComparison.Ordinal));
            Assert.Equal(276, known.Chunks.Length);
        }

        [Fact]
        public void A_missing_fragment_is_null()
        {
            string? path = FindUpwards(Path.Combine("datos", "cytrus", RealManifest));
            if (path == null) return;

            Assert.Null(CytrusManifest.Parse(File.ReadAllBytes(path)).Fragment("no_such_fragment"));
        }

        [Theory]
        [InlineData(0)]
        [InlineData(3)]
        [InlineData(64)]
        public void A_truncated_manifest_is_invalid_data_not_an_index_error(int length)
        {
            var bytes = new byte[length];
            if (length >= 4) bytes[0] = 0xF0;   // a root offset past the end
            Assert.Throws<InvalidDataException>(() => CytrusManifest.Parse(bytes));
        }

        [Fact]
        public void A_hash_prints_and_parses_as_lowercase_hex()
        {
            var hash = Sha1Hash.Of(new byte[] { 1, 2, 3 });
            string hex = hash.ToString();

            Assert.Equal(40, hex.Length);
            Assert.Equal(hex.ToLowerInvariant(), hex);
            Assert.Equal(hash, Sha1Hash.Parse(hex.ToUpperInvariant()));
            Assert.Equal("7037807198c22a7d2b0807371d763779a84fdfcf", hex);
        }

        [Fact]
        public void The_cdn_files_bundles_under_their_first_two_hex_digits()
        {
            var hash = Sha1Hash.Parse("7037807198c22a7d2b0807371d763779a84fdfcf");
            Assert.Equal("https://cytrus.cdn.ankama.com/dofus/bundles/70/7037807198c22a7d2b0807371d763779a84fdfcf",
                         CytrusCdn.BundleUrl("dofus", hash));
        }

        // ─────────────────────────────────────────────────── the plan

        [Fact]
        public void A_chunk_shared_by_two_files_is_fetched_once_and_written_twice()
        {
            var data = SyntheticPack.Build();
            var plan = CytrusPlan.For(data.Fragment, data.Fragment.Files);

            var shared = plan.Bundles.SelectMany(b => b.Pieces).Single(p => p.Targets.Length == 2);
            Assert.Equal(new[] { 0, 1 }, shared.Targets.Select(t => t.File).OrderBy(f => f));

            // Every chunk once; the unwanted chunk in the last bundle is not part of the plan.
            long unique = data.Chunks.Values.Sum(c => (long)c.Length);
            Assert.Equal(unique, plan.Bytes);
            Assert.Equal(3, plan.Bundles.Count);
        }

        [Fact]
        public void Only_the_bundles_of_the_wanted_files_are_planned()
        {
            var data = SyntheticPack.Build();
            var plan = CytrusPlan.For(data.Fragment, new[] { data.Fragment.Files[0] });

            Assert.Single(plan.Files);
            Assert.Equal(2, plan.Bundles.Count);
            Assert.All(plan.Bundles.SelectMany(b => b.Pieces).SelectMany(p => p.Targets), t => Assert.Equal(0, t.File));
        }

        [Fact]
        public void Chunks_that_leave_a_gap_are_refused()
        {
            var data = SyntheticPack.Build();
            var file = data.Fragment.Files[0];
            var holed = file with { Chunks = file.Chunks.Skip(1).ToArray() };

            Assert.Throws<InvalidDataException>(() => CytrusPlan.For(data.Fragment, new[] { holed }));
        }

        [Fact]
        public void A_chunk_in_no_bundle_is_refused()
        {
            var data = SyntheticPack.Build();
            var orphan = data.Fragment with { Bundles = data.Fragment.Bundles.Skip(1).ToArray() };

            Assert.Throws<InvalidDataException>(() => CytrusPlan.For(orphan, orphan.Files));
        }

        internal static string? FindUpwards(string relative)
        {
            var folder = new DirectoryInfo(AppContext.BaseDirectory);
            while (folder != null)
            {
                string candidate = Path.Combine(folder.FullName, relative);
                if (File.Exists(candidate)) return candidate;
                folder = folder.Parent;
            }
            return null;
        }
    }
}
