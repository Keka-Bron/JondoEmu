using System;
using Jondo.Unity.World.Content;
using Xunit;

namespace Jondo.Unity.Tests.Content
{
    /// <summary>
    /// The merge contract of the three content layers.
    /// </summary>
    /// <remarks>
    /// Every content domain is built on this store, so a mistake here is a mistake everywhere, and
    /// it shows up as "my edit disappeared" weeks later with no way to tell whether it was ever
    /// saved. These are the rules that make that impossible.
    /// </remarks>
    public class ContentStoreTests
    {
        private static readonly Origin Base = Origin.Base("client dump");
        private static readonly Origin Measured = Origin.Measured("a capture");
        private static readonly Origin Authored = Origin.Authored("by hand");

        private static ContentStore<int, string> New() => new ContentStore<int, string>();

        // ─── Precedence ───────────────────────────────────────────────────────────

        [Fact]
        public void Authored_beats_measured()
        {
            var store = New();
            store.Put(1, "from the capture", Measured);
            store.Put(1, "decided here", Authored);

            Assert.True(store.TryGet(1, out var row));
            Assert.Equal("decided here", row.Value);
        }

        [Fact]
        public void Measured_beats_base()
        {
            var store = New();
            store.Put(1, "guessed", Base);
            store.Put(1, "measured", Measured);

            Assert.True(store.TryGet(1, out var row));
            Assert.Equal("measured", row.Value);
        }

        /// <summary>
        /// The rule must not depend on the order the layers happen to load in. Startup order gets
        /// reshuffled — the NPC seeding already had to move once, ahead of template loading — and a
        /// rule that survives only in one order is a rule that breaks silently.
        /// </summary>
        [Fact]
        public void A_late_lower_layer_cannot_undo_a_higher_one()
        {
            var store = New();
            store.Put(1, "decided here", Authored);
            store.Put(1, "the capture, loaded afterwards", Measured);
            store.Put(1, "the dump, loaded last of all", Base);

            Assert.True(store.TryGet(1, out var row));
            Assert.Equal("decided here", row.Value);
        }

        [Fact]
        public void The_same_layer_overwrites_itself()
        {
            // Two rows for one key inside a single file: the last one read wins. Nothing clever,
            // but it has to be defined, because it is what happens when somebody duplicates a line.
            var store = New();
            store.Put(1, "first", Authored);
            store.Put(1, "second", Authored);

            Assert.True(store.TryGet(1, out var row));
            Assert.Equal("second", row.Value);
        }

        // ─── Provenance ───────────────────────────────────────────────────────────

        [Fact]
        public void A_row_remembers_which_layer_it_came_from()
        {
            var store = New();
            store.Put(1, "x", Measured);

            Assert.Equal(ContentLayer.Measured, store.OriginOf(1)?.Layer);
            Assert.Equal("a capture", store.OriginOf(1)?.Source);
        }

        [Fact]
        public void An_unknown_key_has_no_origin()
        {
            Assert.Null(New().OriginOf(404));
        }

        // ─── Erasing ──────────────────────────────────────────────────────────────

        [Fact]
        public void An_erased_row_stays_erased_when_a_generated_layer_puts_it_back()
        {
            // This is the whole reason erasing exists: datos/npcs_reales.json is rewritten by a
            // Python tool, so "delete the line" is not a durable way to remove a placement.
            var store = New();
            store.Put(2, "Ankama puts one here", Measured);
            store.Erase(2, Authored);
            store.Put(2, "Ankama puts one here again", Measured);

            Assert.False(store.Contains(2));
        }

        [Fact]
        public void Erasing_a_key_that_was_never_there_is_not_an_error()
        {
            // An authored file may name a placement that a later capture no longer produces. That
            // is stale, not broken, and it must not take the server down at startup.
            var store = New();
            store.Erase(999, Authored);

            Assert.False(store.Contains(999));
            Assert.Equal(1, store.ErasedCount);
        }

        [Theory]
        [InlineData(ContentLayer.Base)]
        [InlineData(ContentLayer.Measured)]
        public void Only_the_authored_layer_may_erase(ContentLayer layer)
        {
            // Erasing from a regenerable layer would be undone by the next regeneration, so it is
            // a mistake worth refusing loudly rather than half-honouring.
            var store = New();
            var from = new Origin(layer, "generated");

            var boom = Assert.Throws<InvalidOperationException>(() => store.Erase(1, from));
            Assert.Contains(layer.ToString(), boom.Message);
        }

        // ─── Census ───────────────────────────────────────────────────────────────

        [Fact]
        public void The_census_counts_surviving_rows_and_not_the_ones_that_lost()
        {
            var store = New();
            store.Put(1, "a", Measured);
            store.Put(1, "b", Authored);   // replaces the measured one
            store.Put(2, "c", Measured);
            store.Put(3, "d", Base);

            var census = store.Census();
            Assert.Equal(1, census[ContentLayer.Authored]);
            Assert.Equal(1, census[ContentLayer.Measured]);
            Assert.Equal(1, census[ContentLayer.Base]);
            Assert.Equal(3, store.Count);
        }

        [Fact]
        public void An_empty_store_still_reports_every_layer()
        {
            // The log line reads census[Measured] straight out, so a missing key would be a crash
            // at startup on a fresh install with no content at all.
            var census = New().Census();

            Assert.Equal(0, census[ContentLayer.Base]);
            Assert.Equal(0, census[ContentLayer.Measured]);
            Assert.Equal(0, census[ContentLayer.Authored]);
        }

        [Fact]
        public void Clearing_forgets_the_erasures_too()
        {
            var store = New();
            store.Put(1, "a", Measured);
            store.Erase(2, Authored);
            store.Clear();
            store.Put(2, "back", Measured);

            Assert.Equal(0, store.ErasedCount);
            Assert.True(store.Contains(2));
        }

        // ─── Return values ────────────────────────────────────────────────────────

        [Fact]
        public void Put_says_whether_the_row_went_in()
        {
            var store = New();
            Assert.True(store.Put(1, "a", Authored));
            Assert.False(store.Put(1, "b", Measured));   // lost to the higher layer

            store.Erase(2, Authored);
            Assert.False(store.Put(2, "c", Measured));   // erased
        }
    }
}
