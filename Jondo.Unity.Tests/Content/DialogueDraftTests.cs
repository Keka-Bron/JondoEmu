using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using Jondo.Unity.Launcher;
using Jondo.Unity.Studio.Pages;
using Jondo.Unity.World.Content;
using Xunit;

namespace Jondo.Unity.Tests.Content
{
    /// <summary>
    /// The Studio's dialogue editor: what it does not edit, it writes back as it was read.
    /// </summary>
    /// <remarks>
    /// It moves lines and replies around. Its draft used to keep only those and a reply's quest
    /// markings, so opening any tree and saving it erased every other field -- a reply's
    /// conditions, gifts and teleports, the tree's quest openings and pushes, its notes -- and put
    /// every per-map tree on «any map».
    /// </remarks>
    public class DialogueDraftTests : IDisposable
    {
        private readonly string _path = Path.Combine(Path.GetTempPath(),
                                                     "jondo-draft-" + Guid.NewGuid().ToString("N") + ".json");

        public void Dispose()
        {
            try { File.Delete(_path); } catch (IOException) { }
        }

        /// <summary>Every settable property of <paramref name="target"/> to something that is not its default.</summary>
        private static T Filled<T>(T target, int seed)
        {
            int n = seed;
            foreach (var property in typeof(T).GetProperties(BindingFlags.Public | BindingFlags.Instance))
            {
                if (property.SetMethod == null) continue;
                var type = property.PropertyType;
                object? value =
                    type == typeof(long) ? (object)(long)++n :
                    type == typeof(int) ? ++n :
                    type == typeof(bool) ? true :
                    type == typeof(string) ? "note " + ++n :
                    type == typeof(IReadOnlyList<int>) ? new[] { ++n, ++n } :
                    type == typeof(IReadOnlyList<long>) ? new long[] { ++n, ++n } :
                    type == typeof(IReadOnlyList<IReadOnlyList<long>>) ? new IReadOnlyList<long>[] { new long[] { ++n } } :
                    null;
                if (value != null) property.SetValue(target, value);
            }
            return target;
        }

        private static string Saved(string path, NpcDialogue tree)
        {
            NpcDialogueContent.Save(path, new[] { tree }, new[] { "test" });
            return File.ReadAllText(path);
        }

        private static NpcDialogue EverythingSet()
        {
            var choice = Filled(new DialogueChoice(), 1000);
            var other = Filled(new DialogueChoice(), 2000);
            var line = Filled(new DialogueLine(), 3000);
            var opening = Filled(new DialogueOpening { ByBreed = new Dictionary<int, long> { [3] = 12 } }, 5000);
            var push = Filled(new DialoguePush { Line = Filled(new DialogueOpening(), 6000) }, 7000);
            return Filled(new NpcDialogue { Openings = new[] { opening }, Pushes = new[] { push } }, 4000)
                .WithLines(10, new[]
                {
                    new DialogueLine { Message = 10, NamesTheCharacter = true, HoursToDailyReset = true }
                        .WithChoices(new[] { choice.Leading(20, 11), other.Leading(21, 0) }),
                    line.WithChoices(new[] { other.Leading(22, 10) }),
                });
        }

        [Fact]
        public void A_tree_with_every_field_set_round_trips_through_the_draft_unchanged()
        {
            var tree = EverythingSet();
            var frozen = DialogueDraft.From(tree).Freeze();

            Assert.Equal(tree.MapId, frozen.MapId);
            Assert.NotEqual(NpcDialogueKey.AnyMap, frozen.MapId);
            Assert.Same(tree.Openings, frozen.Openings);
            Assert.Same(tree.Pushes, frozen.Pushes);

            // Every property of every choice and line, saved or not.
            var before = tree.Lines.SelectMany(l => l.Choices).ToList();
            var after = frozen.Lines.SelectMany(l => l.Choices).ToList();
            Assert.Equal(before.Count, after.Count);
            foreach (var property in typeof(DialogueChoice).GetProperties())
            {
                for (int i = 0; i < before.Count; i++)
                    Assert.Equal(property.GetValue(before[i]), property.GetValue(after[i]));
            }
            foreach (var property in typeof(DialogueLine).GetProperties().Where(p => p.Name != nameof(DialogueLine.Choices)))
            {
                foreach (var line in tree.Lines)
                    Assert.Equal(property.GetValue(line), property.GetValue(frozen.Lines.Single(l => l.Message == line.Message)));
            }

            // And the file reads the same.
            string other = _path + ".frozen";
            try
            {
                Assert.Equal(Saved(_path, tree), Saved(other, frozen));
            }
            finally
            {
                File.Delete(other);
            }
        }

        [Fact]
        public void An_edited_reply_keeps_what_the_editor_does_not_touch()
        {
            var tree = EverythingSet();
            var draft = DialogueDraft.From(tree);
            draft.Under(10)[0].Next = 0;
            draft.Under(10).Add(new DraftChoice { Reply = 23 });

            var frozen = draft.Freeze();
            var edited = frozen.Line(10)!.Choices;
            Assert.Equal(0, edited[0].Next);
            Assert.Equal(tree.Line(10)!.Choices[0].Completes, edited[0].Completes);
            Assert.Equal(tree.Line(10)!.Choices[0].TeleportsTo, edited[0].TeleportsTo);
            Assert.Equal(23, edited[2].Reply);
            Assert.Equal(0, edited[2].Quest);
        }

        /// <summary>The shipped trees, every one opened in the editor and saved: the file says the same.</summary>
        [Fact]
        public void The_shipped_dialogues_come_out_of_the_editor_as_they_went_in()
        {
            string shipped = Paths.ContentFile(NpcDialogueContent.AuthoredFile);
            if (!File.Exists(shipped)) return;
            var trees = NpcDialogueContent.Load(shipped).Rows.Select(r => r.Value.Value).ToList();
            Assert.NotEmpty(trees);

            string other = _path + ".frozen";
            try
            {
                NpcDialogueContent.Save(_path, trees, new[] { "test" });
                NpcDialogueContent.Save(other, trees.Select(t => DialogueDraft.From(t).Freeze()).ToList(), new[] { "test" });
                Assert.Equal(File.ReadAllText(_path), File.ReadAllText(other));
            }
            finally
            {
                File.Delete(other);
            }
        }

        [Fact]
        public void A_tree_s_notes_and_the_file_s_comment_survive_a_save()
        {
            var tree = new NpcDialogue
            {
                NpcId = 7, MapId = 70, Opening = 10, Why = "why it is so", ByHand = true, SourceNote = "where from",
                Lines = new[] { new DialogueLine { Message = 10, Choices = new[] { new DialogueChoice { Reply = 20 } } } },
            };
            NpcDialogueContent.Save(_path, new[] { tree }, new[] { "the file's own", "comment" });
            NpcDialogueContent.Save(_path, NpcDialogueContent.Load(_path).Rows.Select(r => r.Value.Value).ToList());

            var read = NpcDialogueContent.Load(_path).Rows.Single().Value.Value;
            Assert.Equal("why it is so", read.Why);
            Assert.True(read.ByHand);
            Assert.Equal("where from", read.SourceNote);
            Assert.Equal(70, read.MapId);
            Assert.Contains("the file's own", File.ReadAllText(_path));
        }
    }
}
