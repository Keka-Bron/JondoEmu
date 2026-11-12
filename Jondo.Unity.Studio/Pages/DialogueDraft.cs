using System.Collections.Generic;
using Jondo.Unity.World.Content;

namespace Jondo.Unity.Studio.Pages
{
    /// <summary>
    /// One NPC's conversation as the dialogue editor holds it while it is being changed: which
    /// lines are in the tree, the replies under each and where each leads.
    /// </summary>
    /// <remarks>
    /// The editor moves lines and replies around and nothing else. Everything else a tree says --
    /// the map it is for, its quest openings and pushes, its notes, a line's flags and every
    /// field of a reply (its quest, conditions, gifts, teleports...) -- is kept from the tree as
    /// it was read and written back untouched. Building the saved tree from the edited fields
    /// alone erased all of that, and put every per-map tree on «any map».
    /// </remarks>
    public sealed class DialogueDraft
    {
        public int NpcId;
        public long Opening;

        /// <summary>message → the replies hanging under it, and where each leads.</summary>
        public readonly Dictionary<long, List<DraftChoice>> Lines = new Dictionary<long, List<DraftChoice>>();

        /// <summary>The tree as it was read, or null for a new one.</summary>
        private NpcDialogue? _source;

        /// <summary>The lines as they were read, for their own flags.</summary>
        private readonly Dictionary<long, DialogueLine> _sourceLines = new Dictionary<long, DialogueLine>();

        public bool Has(long message) => Lines.ContainsKey(message);

        public List<DraftChoice> Under(long message)
            => Lines.TryGetValue(message, out var choices) ? choices : new List<DraftChoice>();

        public bool Empty => Lines.Count == 0;

        /// <summary>A draft of a tree read from the file.</summary>
        public static DialogueDraft From(NpcDialogue dialogue)
        {
            var draft = new DialogueDraft { NpcId = dialogue.NpcId, Opening = dialogue.Opening, _source = dialogue };
            foreach (var line in dialogue.Lines)
            {
                var choices = new List<DraftChoice>();
                foreach (var choice in line.Choices)
                    choices.Add(new DraftChoice { Reply = choice.Reply, Next = choice.Next, Source = choice });

                draft.Lines[line.Message] = choices;
                draft._sourceLines[line.Message] = line;
            }
            return draft;
        }

        public NpcDialogue Freeze()
        {
            var lines = new List<DialogueLine>();
            foreach (var pair in Lines)
            {
                var choices = pair.Value.ConvertAll(c => c.Freeze()).ToArray();
                lines.Add(_sourceLines.TryGetValue(pair.Key, out var read)
                    ? read.WithChoices(choices)
                    : new DialogueLine { Message = pair.Key, Choices = choices });
            }

            // A tree read from the file keeps its lines' order, the new ones after them, so a save
            // changes only what was edited. A new tree puts its opening line first, so the file
            // reads in the order the conversation happens.
            var order = new Dictionary<long, int>();
            if (_source != null)
                for (int i = 0; i < _source.Lines.Count; i++) order.TryAdd(_source.Lines[i].Message, i);
            lines.Sort((a, b) =>
            {
                int byFile = order.GetValueOrDefault(a.Message, int.MaxValue).CompareTo(order.GetValueOrDefault(b.Message, int.MaxValue));
                if (byFile != 0) return byFile;
                if (a.Message == Opening) return -1;
                if (b.Message == Opening) return 1;
                return a.Message.CompareTo(b.Message);
            });

            long opening = Opening != 0 ? Opening : (lines.Count > 0 ? lines[0].Message : 0);
            return _source != null
                ? _source.WithLines(opening, lines)
                : new NpcDialogue { NpcId = NpcId, MapId = NpcDialogueKey.AnyMap, Opening = opening, Lines = lines };
        }
    }

    /// <summary>One reply of a <see cref="DialogueDraft"/>: the reply, where it leads, and the rest it carries.</summary>
    public sealed class DraftChoice
    {
        public long Reply;
        public long Next;

        /// <summary>The choice as it was read -- everything the editor does not set -- or null for a new one.</summary>
        public DialogueChoice? Source;

        public DialogueChoice Freeze()
            => Source != null ? Source.Leading(Reply, Next) : new DialogueChoice { Reply = Reply, Next = Next };
    }
}
