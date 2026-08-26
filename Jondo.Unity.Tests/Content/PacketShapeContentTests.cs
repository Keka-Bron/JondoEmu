using System;
using System.Collections.Generic;
using System.IO;
using Jondo.Unity.World.Content;
using Xunit;

namespace Jondo.Unity.Tests.Content
{
    /// <summary>
    /// The authored layer for packets: the first thing the editor writes.
    /// </summary>
    /// <remarks>
    /// What is at stake here is not a crash, it is quiet loss. These notes are what somebody worked
    /// out over hours of staring at hex, and they exist in exactly one place. A save that drops a
    /// row, a load that skips one, or a file format that produces a whole-file diff every time
    /// somebody edits one line all end the same way: the notes stop being trusted and stop being
    /// written.
    /// </remarks>
    public class PacketShapeContentTests : IDisposable
    {
        private readonly string _path = Path.Combine(Path.GetTempPath(),
                                                     "jondo-shapes-" + Guid.NewGuid().ToString("N") + ".json");

        public void Dispose()
        {
            try { File.Delete(_path); } catch (IOException) { }
        }

        private static PacketNote Note(string opcode, string shape, PacketStatus status = PacketStatus.Named,
                                       string name = "", string notes = "")
            => new PacketNote { Opcode = opcode, Shape = shape, Status = status, Name = name, Notes = notes };

        // ─── Round trip ───────────────────────────────────────────────────────────

        [Fact]
        public void What_is_saved_is_what_comes_back()
        {
            PacketShapeContent.Save(_path, new[]
            {
                Note("kqz", "2:s,3:s", PacketStatus.Documented, "SetLanguageRequest", "field 3 is the code"),
                Note("kod", PacketShapeKey.AnyShape, PacketStatus.Handled, "PingRequest"),
            });

            var store = PacketShapeContent.Load(_path);

            Assert.Equal(2, store.Count);
            Assert.True(store.TryGet(new PacketShapeKey("kqz", "2:s,3:s"), out var row));
            Assert.Equal(PacketStatus.Documented, row.Value.Status);
            Assert.Equal("SetLanguageRequest", row.Value.Name);
            Assert.Equal("field 3 is the code", row.Value.Notes);
            Assert.Equal(ContentLayer.Authored, row.From.Layer);
        }

        /// <summary>
        /// A shape is full of braces, colons and commas. Escaped as { it is unreadable, and an
        /// authored file nobody can read in a diff is one nobody reviews.
        /// </summary>
        [Fact]
        public void A_shape_stays_readable_in_the_file()
        {
            PacketShapeContent.Save(_path, new[] { Note("jxw", "1:v,2:{3:v},4:s") });

            string text = File.ReadAllText(_path);

            Assert.Contains("1:v,2:{3:v},4:s", text);
            Assert.DoesNotContain("\\u", text);
        }

        /// <summary>
        /// Rows go out in a fixed order so that changing one line produces a one-line diff. A file
        /// whose order follows whatever a dictionary enumerated rewrites itself every save.
        /// </summary>
        [Fact]
        public void The_order_does_not_depend_on_the_order_they_were_added()
        {
            var one = new[] { Note("zzz", "1:v"), Note("aaa", "2:v"), Note("mmm", "1:v") };
            var other = new[] { Note("mmm", "1:v"), Note("zzz", "1:v"), Note("aaa", "2:v") };

            PacketShapeContent.Save(_path, one);
            string first = File.ReadAllText(_path);

            PacketShapeContent.Save(_path, other);
            string second = File.ReadAllText(_path);

            Assert.Equal(first, second);
            Assert.True(first.IndexOf("aaa", StringComparison.Ordinal) <
                        first.IndexOf("mmm", StringComparison.Ordinal));
        }

        [Fact]
        public void Two_shapes_of_one_opcode_are_two_rows()
        {
            PacketShapeContent.Save(_path, new[]
            {
                Note("jss", "1:v", PacketStatus.Named, "one thing"),
                Note("jss", "1:v,2:s", PacketStatus.Named, "quite another"),
            });

            var store = PacketShapeContent.Load(_path);

            Assert.Equal(2, store.Count);
            Assert.True(store.TryGet(new PacketShapeKey("jss", "1:v"), out var first));
            Assert.Equal("one thing", first.Value.Name);
        }

        [Fact]
        public void A_missing_shape_means_the_note_is_about_the_whole_opcode()
        {
            File.WriteAllText(_path, "{ \"packets\": [ { \"opcode\": \"jss\", \"status\": \"named\" } ] }");

            var store = PacketShapeContent.Load(_path);

            Assert.True(store.TryGet(new PacketShapeKey("jss", PacketShapeKey.AnyShape), out var row));
            Assert.True(row.Value.Key.IsAboutEveryShape);
        }

        // ─── Not falling over ─────────────────────────────────────────────────────

        [Fact]
        public void A_file_that_is_not_there_loads_as_nothing()
            => Assert.Equal(0, PacketShapeContent.Load(Path.Combine(Path.GetTempPath(), "nope.json")).Count);

        [Fact]
        public void A_broken_file_is_reported_rather_than_thrown()
        {
            File.WriteAllText(_path, "{ this is not json");

            string complaint = "";
            var store = PacketShapeContent.Load(_path, message => complaint = message);

            Assert.Equal(0, store.Count);
            Assert.Contains("unreadable", complaint);
        }

        [Fact]
        public void A_row_with_no_opcode_is_skipped_and_the_rest_still_load()
        {
            File.WriteAllText(_path,
                "{ \"packets\": [ { \"status\": \"named\" }, { \"opcode\": \"kqz\", \"status\": \"handled\" } ] }");

            var store = PacketShapeContent.Load(_path);

            Assert.Equal(1, store.Count);
        }

        [Fact]
        public void An_unrecognised_status_falls_back_to_unknown_rather_than_failing()
        {
            File.WriteAllText(_path,
                "{ \"packets\": [ { \"opcode\": \"kqz\", \"status\": \"marvellous\" } ] }");

            var store = PacketShapeContent.Load(_path);

            Assert.True(store.TryGet(new PacketShapeKey("kqz", PacketShapeKey.AnyShape), out var row));
            Assert.Equal(PacketStatus.Unknown, row.Value.Status);
        }

        [Fact]
        public void Status_names_are_read_whatever_their_case()
        {
            Assert.Equal(PacketStatus.Documented, PacketShapeContent.ParseStatus("DOCUMENTED"));
            Assert.Equal(PacketStatus.Handled, PacketShapeContent.ParseStatus("handled"));
            Assert.Equal(PacketStatus.Unknown, PacketShapeContent.ParseStatus(null));
        }

        /// <summary>
        /// The tombstone. Present for the same reason it is present everywhere else in the content
        /// layers: a row that came from a lower layer has to be removable without editing the file
        /// it came from, because that file is regenerated.
        /// </summary>
        [Fact]
        public void A_removed_row_stays_removed()
        {
            File.WriteAllText(_path,
                "{ \"packets\": [ { \"opcode\": \"kqz\", \"shape\": \"1:v\", \"remove\": true } ] }");

            var store = PacketShapeContent.Load(_path);

            Assert.Equal(0, store.Count);
            Assert.Equal(1, store.ErasedCount);
            Assert.False(store.Put(new PacketShapeKey("kqz", "1:v"), Note("kqz", "1:v"),
                                   Origin.Measured("a capture")));
        }

        // ─── Saving safely ────────────────────────────────────────────────────────

        /// <summary>
        /// Written to a temporary file and moved over. Half a JSON file on disk because the editor
        /// was closed mid-write is a boot failure nobody would connect to what they did.
        /// </summary>
        [Fact]
        public void Saving_leaves_no_half_written_file_behind()
        {
            PacketShapeContent.Save(_path, new[] { Note("kqz", "1:v") });

            Assert.True(File.Exists(_path));
            Assert.False(File.Exists(_path + ".writing"));
        }

        [Fact]
        public void Saving_over_an_existing_file_replaces_it()
        {
            PacketShapeContent.Save(_path, new[] { Note("aaa", "1:v"), Note("bbb", "1:v") });
            PacketShapeContent.Save(_path, new[] { Note("aaa", "1:v") });

            var store = PacketShapeContent.Load(_path);

            Assert.Equal(1, store.Count);
            Assert.False(store.Contains(new PacketShapeKey("bbb", "1:v")));
        }

        [Fact]
        public void Saving_nothing_is_allowed_and_reads_back_as_nothing()
        {
            PacketShapeContent.Save(_path, Array.Empty<PacketNote>());

            Assert.Equal(0, PacketShapeContent.Load(_path).Count);
        }

        [Fact]
        public void The_folder_is_made_if_it_is_not_there()
        {
            string nested = Path.Combine(Path.GetTempPath(),
                                         "jondo-" + Guid.NewGuid().ToString("N"), "packets", "shapes.json");
            try
            {
                PacketShapeContent.Save(nested, new[] { Note("kqz", "1:v") });
                Assert.True(File.Exists(nested));
            }
            finally
            {
                try { Directory.Delete(Path.GetDirectoryName(Path.GetDirectoryName(nested))!, true); }
                catch (IOException) { }
            }
        }

        // ─── The key ──────────────────────────────────────────────────────────────

        /// <summary>
        /// Measured over the 72,879 frames of the traffic log: 10 of the 664 shapes are shared by
        /// more than one opcode and between them cover 180 of the 242 opcodes, and 59 of those 242
        /// carry more than one shape. Neither half of the key can be dropped.
        /// </summary>
        [Fact]
        public void The_key_is_both_halves()
        {
            Assert.NotEqual(new PacketShapeKey("aaa", "1:v"), new PacketShapeKey("bbb", "1:v"));
            Assert.NotEqual(new PacketShapeKey("aaa", "1:v"), new PacketShapeKey("aaa", "2:v"));
            Assert.Equal(new PacketShapeKey("aaa", "1:v"), new PacketShapeKey("aaa", "1:v"));
        }

        [Fact]
        public void An_empty_shape_means_every_shape()
        {
            Assert.True(new PacketShapeKey("aaa", "").IsAboutEveryShape);
            Assert.True(new PacketShapeKey("aaa", null!).IsAboutEveryShape);
            Assert.False(new PacketShapeKey("aaa", "1:v").IsAboutEveryShape);
        }
    }
}
