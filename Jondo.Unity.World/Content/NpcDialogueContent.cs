using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Encodings.Web;
using System.Text.Json;

namespace Jondo.Unity.World.Content
{
    /// <summary>One thing the player can say, and where saying it leads.</summary>
    public sealed class DialogueChoice
    {
        /// <summary>The reply's id, out of the ones the NPC's template declares.</summary>
        public long Reply { get; init; }

        /// <summary>The line it leads to. Zero ends the conversation, which is most of them.</summary>
        public long Next { get; init; }

        /// <summary>
        /// The quest this reply belongs to, or zero when it belongs to no quest.
        /// </summary>
        /// <remarks>
        /// Without this the server has no way to hide a reply that has no business being there, and
        /// the result is what the fallback does today: Snori Nairb offers all thirty-nine of his
        /// replies at once, including the ones that only make sense halfway through a quest nobody
        /// has started.
        ///
        /// The rule the two fields express, in the order they are checked:
        ///
        ///   quest only        the quest has to be under way, or finished if <see cref="AfterQuest"/>
        ///   quest and step    it has to be under way AND on that step
        ///   neither           always offered
        /// </remarks>
        public int Quest { get; init; }

        /// <summary>The step of that quest it belongs to. Zero means any step.</summary>
        public int Step { get; init; }

        /// <summary>
        /// The quest this reply hands over, or zero.
        /// </summary>
        /// <remarks>
        /// Separate from <see cref="Quest"/> and it has to be: <see cref="Quest"/> hides a reply
        /// until the quest is under way, and this one is the reply that <em>puts</em> it under way,
        /// so marking it the same would hide the only way of starting it.
        ///
        /// Before this the engine started a quest on any reply of the line the step names, which
        /// meant "no thanks" handed it over too. The capture cannot say which reply is the yes —
        /// the extra field Ankama's replies carry is on 184 of the 429 captured ones and almost
        /// none are quest replies — so it is said here, where a person writing the tree knows.
        /// </remarks>
        public int StartsQuest { get; init; }

        /// <summary>True when the reply is for somebody who has already finished the quest.</summary>
        public bool AfterQuest { get; init; }

        public bool Ends => Next == 0;

        /// <summary>Whether this reply is offered to nobody in particular.</summary>
        public bool Always => Quest == 0;

        public override string ToString()
        {
            string where = Ends ? "✕" : "→ " + Next;
            if (Quest == 0) return $"{Reply} {where}";
            return Step == 0
                ? $"{Reply} {where} (quest {Quest})"
                : $"{Reply} {where} (quest {Quest} step {Step})";
        }
    }

    /// <summary>One thing the NPC says, and what can be said back.</summary>
    public sealed class DialogueLine
    {
        public long Message { get; init; }

        public IReadOnlyList<DialogueChoice> Choices { get; init; } = Array.Empty<DialogueChoice>();

        public DialogueChoice? Choice(long reply)
        {
            foreach (var choice in Choices)
            {
                if (choice.Reply == reply) return choice;
            }

            return null;
        }

        public long[] Replies()
        {
            var replies = new long[Choices.Count];
            for (int i = 0; i < Choices.Count; i++) replies[i] = Choices[i].Reply;
            return replies;
        }

        /// <summary>
        /// The replies this particular character should be shown.
        /// </summary>
        /// <remarks>
        /// The whole point of <see cref="DialogueChoice.Quest"/>. A reply that belongs to a quest
        /// is not offered to somebody who has not started it, and one that belongs to a step is not
        /// offered before that step is the one in hand — which is what stops a conversation from
        /// showing every answer to every question at once.
        ///
        /// <paramref name="onStep"/> answers "is this quest on that step". It is a callback rather
        /// than the quest log itself so that this file goes on knowing nothing about the server.
        /// </remarks>
        public long[] RepliesFor(Func<int, bool> active, Func<int, bool> finished,
                                 Func<int, int, bool> onStep)
        {
            var replies = new List<long>(Choices.Count);
            foreach (var choice in Choices)
            {
                if (choice.Always) { replies.Add(choice.Reply); continue; }

                if (choice.AfterQuest)
                {
                    if (finished(choice.Quest)) replies.Add(choice.Reply);
                    continue;
                }

                if (!active(choice.Quest)) continue;
                if (choice.Step != 0 && !onStep(choice.Quest, choice.Step)) continue;

                replies.Add(choice.Reply);
            }

            return replies.ToArray();
        }

        public override string ToString() => $"message {Message}, {Choices.Count} replies";
    }

    /// <summary>A whole conversation with one NPC.</summary>
    public sealed class NpcDialogue
    {
        public int NpcId { get; init; }

        /// <summary>Which map this is for. Zero means wherever the NPC stands.</summary>
        public long MapId { get; init; }

        /// <summary>The line it opens with. Zero falls back to the template's.</summary>
        public long Opening { get; init; }

        public IReadOnlyList<DialogueLine> Lines { get; init; } = Array.Empty<DialogueLine>();

        public NpcDialogueKey Key => new NpcDialogueKey(NpcId, MapId);

        public DialogueLine? Line(long message)
        {
            foreach (var line in Lines)
            {
                if (line.Message == message) return line;
            }

            return null;
        }

        /// <summary>The line it starts on, whether or not <see cref="Opening"/> was filled in.</summary>
        public DialogueLine? First()
            => Opening != 0 ? Line(Opening) : (Lines.Count > 0 ? Lines[0] : null);

        public override string ToString()
            => MapId == 0 ? $"npc {NpcId}" : $"npc {NpcId} on map {MapId}";
    }

    /// <summary>
    /// Which conversation this is: one NPC, on one map, or on any of them.
    /// </summary>
    /// <remarks>
    /// The map is in the key because the opening line is per map: the same character standing in
    /// two places has no reason to say the same thing, and the real game does not make it. A
    /// dialogue written with map 0 is the one used wherever nothing more specific was written,
    /// which is what most NPCs will ever need.
    /// </remarks>
    public readonly struct NpcDialogueKey : IEquatable<NpcDialogueKey>
    {
        /// <summary>Map 0: this conversation goes wherever the NPC does.</summary>
        public const long AnyMap = 0;

        public NpcDialogueKey(int npcId, long mapId)
        {
            NpcId = npcId;
            MapId = mapId;
        }

        public int NpcId { get; }

        public long MapId { get; }

        public bool IsForEveryMap => MapId == AnyMap;

        public bool Equals(NpcDialogueKey other) => NpcId == other.NpcId && MapId == other.MapId;
        public override bool Equals(object? obj) => obj is NpcDialogueKey other && Equals(other);
        public override int GetHashCode() => HashCode.Combine(NpcId, MapId);
        public override string ToString() => MapId == 0 ? NpcId.ToString() : $"{NpcId}@{MapId}";
    }

    /// <summary>
    /// Which reply leads to which line: the one thing about an NPC the client has never held.
    /// </summary>
    /// <remarks>
    /// This is the clearest case in the whole project for why the authored layer exists. The client
    /// ships every line an NPC can say and every reply it can be given — 6,467 NPCs' worth — and
    /// <b>nowhere does it say which goes with which</b>. That pairing has always lived on Ankama's
    /// server, so it cannot be extracted, measured or inferred. It has to be decided.
    ///
    /// What it costs not to have it is visible in the game: Snori Nairb has three lines and
    /// thirty-nine replies, and without a tree all thirty-nine are offered at once, under the first
    /// line, in the order the client happened to list them.
    ///
    /// So there is exactly one layer here and it is the authored one. There is no measured layer to
    /// merge underneath, because there is nothing to measure — and that is worth saying out loud
    /// rather than leaving as an empty slot somebody fills in later with a guess.
    /// </remarks>
    public static class NpcDialogueContent
    {
        /// <summary>The authored file, relative to the content root.</summary>
        public const string AuthoredFile = "npcs/dialogues.json";

        public static ContentStore<NpcDialogueKey, NpcDialogue> Load(string? authoredPath,
                                                                     Action<string>? report = null)
        {
            var store = new ContentStore<NpcDialogueKey, NpcDialogue>();
            if (string.IsNullOrEmpty(authoredPath) || !File.Exists(authoredPath)) return store;

            var from = Origin.Authored(Path.GetFileName(authoredPath));
            try
            {
                using var doc = JsonDocument.Parse(File.ReadAllText(authoredPath));
                if (!doc.RootElement.TryGetProperty("dialogues", out var list)) return store;

                foreach (var entry in list.EnumerateArray())
                {
                    int npcId = (int)Number(entry, "npc");
                    if (npcId == 0) continue;

                    long mapId = Number(entry, "map");
                    var key = new NpcDialogueKey(npcId, mapId);

                    if (entry.TryGetProperty("remove", out var gone) && gone.ValueKind == JsonValueKind.True)
                    {
                        store.Erase(key, from);
                        continue;
                    }

                    store.Put(key, new NpcDialogue
                    {
                        NpcId = npcId,
                        MapId = mapId,
                        Opening = Number(entry, "opening"),
                        Lines = ReadLines(entry, report, npcId),
                    }, from);
                }
            }
            catch (Exception ex)
            {
                report?.Invoke($"[Content] {Path.GetFileName(authoredPath)} is unreadable: {ex.Message}");
            }

            return store;
        }

        private static DialogueLine[] ReadLines(JsonElement entry, Action<string>? report, int npcId)
        {
            if (!entry.TryGetProperty("lines", out var lines) || lines.ValueKind != JsonValueKind.Array)
            {
                return Array.Empty<DialogueLine>();
            }

            var read = new List<DialogueLine>();
            foreach (var line in lines.EnumerateArray())
            {
                long message = Number(line, "message");
                if (message == 0)
                {
                    report?.Invoke($"[Content] npc {npcId} has a dialogue line with no message; skipped.");
                    continue;
                }

                var choices = new List<DialogueChoice>();
                if (line.TryGetProperty("choices", out var list) && list.ValueKind == JsonValueKind.Array)
                {
                    foreach (var choice in list.EnumerateArray())
                    {
                        long reply = Number(choice, "reply");
                        if (reply == 0) continue;
                        choices.Add(new DialogueChoice
                        {
                            Reply = reply,
                            Next = Number(choice, "next"),
                            Quest = (int)Number(choice, "quest"),
                            Step = (int)Number(choice, "step"),
                            StartsQuest = (int)Number(choice, "startsQuest"),
                            AfterQuest = choice.TryGetProperty("afterQuest", out var after)
                                         && after.ValueKind == JsonValueKind.True,
                        });
                    }
                }

                read.Add(new DialogueLine { Message = message, Choices = choices.ToArray() });
            }

            return read.ToArray();
        }

        /// <summary>
        /// The conversation for this NPC here: the one written for this map, or the one written for
        /// all of them, or nothing.
        /// </summary>
        public static NpcDialogue? For(ContentStore<NpcDialogueKey, NpcDialogue> store, int npcId, long mapId)
        {
            if (store.TryGet(new NpcDialogueKey(npcId, mapId), out var here)) return here.Value;
            return store.TryGet(new NpcDialogueKey(npcId, NpcDialogueKey.AnyMap), out var anywhere)
                ? anywhere.Value
                : null;
        }

        /// <summary>
        /// Writes the authored file back out, in a fixed order and through a temporary file.
        /// </summary>
        /// <remarks>
        /// Same reasoning as every other authored file: the order is fixed so that changing one
        /// line gives a one-line diff, and the write goes through a temporary file so that closing
        /// the editor mid-save cannot leave half a JSON file where the server will look for one.
        /// </remarks>
        public static void Save(string path, IEnumerable<NpcDialogue> dialogues,
                                IEnumerable<string>? comment = null)
        {
            var ordered = new List<NpcDialogue>(dialogues);
            ordered.Sort((a, b) =>
            {
                int byNpc = a.NpcId.CompareTo(b.NpcId);
                return byNpc != 0 ? byNpc : a.MapId.CompareTo(b.MapId);
            });

            using var buffer = new MemoryStream();
            using (var writer = new Utf8JsonWriter(buffer, new JsonWriterOptions
            {
                Indented = true,
                Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
            }))
            {
                writer.WriteStartObject();

                writer.WritePropertyName("_comment");
                writer.WriteStartArray();
                foreach (string line in comment ?? DefaultComment) writer.WriteStringValue(line);
                writer.WriteEndArray();

                writer.WritePropertyName("dialogues");
                writer.WriteStartArray();
                foreach (var dialogue in ordered)
                {
                    writer.WriteStartObject();
                    writer.WriteNumber("npc", dialogue.NpcId);
                    writer.WriteNumber("map", dialogue.MapId);
                    if (dialogue.Opening != 0) writer.WriteNumber("opening", dialogue.Opening);

                    writer.WritePropertyName("lines");
                    writer.WriteStartArray();
                    foreach (var line in dialogue.Lines)
                    {
                        writer.WriteStartObject();
                        writer.WriteNumber("message", line.Message);

                        writer.WritePropertyName("choices");
                        writer.WriteStartArray();
                        foreach (var choice in line.Choices)
                        {
                            writer.WriteStartObject();
                            writer.WriteNumber("reply", choice.Reply);
                            if (!choice.Ends) writer.WriteNumber("next", choice.Next);
                            if (choice.Quest != 0) writer.WriteNumber("quest", choice.Quest);
                            if (choice.Step != 0) writer.WriteNumber("step", choice.Step);
                            if (choice.StartsQuest != 0) writer.WriteNumber("startsQuest", choice.StartsQuest);
                            if (choice.AfterQuest) writer.WriteBoolean("afterQuest", true);
                            writer.WriteEndObject();
                        }

                        writer.WriteEndArray();
                        writer.WriteEndObject();
                    }

                    writer.WriteEndArray();
                    writer.WriteEndObject();
                }

                writer.WriteEndArray();
                writer.WriteEndObject();
            }

            string? folder = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(folder)) Directory.CreateDirectory(folder);

            string temporary = path + ".writing";
            File.WriteAllBytes(temporary, buffer.ToArray());
            File.Move(temporary, path, overwrite: true);
        }

        /// <summary>
        /// What is wrong with a dialogue, in words, before it is saved.
        /// </summary>
        /// <remarks>
        /// Checked rather than trusted because the failure is silent and remote: a reply pointing at
        /// a line that is not there leaves the player looking at a window with no way out, on
        /// somebody else's machine, days later. Cheap to check here, expensive to find there.
        /// </remarks>
        public static List<string> Complaints(NpcDialogue dialogue)
        {
            var wrong = new List<string>();
            var known = new HashSet<long>();

            foreach (var line in dialogue.Lines)
            {
                if (!known.Add(line.Message))
                {
                    wrong.Add($"message {line.Message} is written twice; only the first would be used.");
                }
            }

            if (dialogue.Opening != 0 && !known.Contains(dialogue.Opening))
            {
                wrong.Add($"it opens on message {dialogue.Opening}, which is not one of its lines.");
            }

            foreach (var line in dialogue.Lines)
            {
                // A line with no replies used to be a complaint, and it is not one any more. The
                // client draws its own "Leave" when the list is empty and that button does not
                // answer back — but the X does, with kla, and the server did not use to handle it.
                // It does now, so a line with nothing to say back is a dead end the player can
                // still walk out of.

                var seen = new HashSet<long>();
                foreach (var choice in line.Choices)
                {
                    if (!seen.Add(choice.Reply))
                    {
                        wrong.Add($"message {line.Message} offers reply {choice.Reply} twice.");
                    }

                    if (!choice.Ends && !known.Contains(choice.Next))
                    {
                        wrong.Add($"reply {choice.Reply} leads to message {choice.Next}, which is " +
                                  "not one of its lines.");
                    }
                }
            }

            return wrong;
        }

        private static long Number(JsonElement element, string name)
            => element.TryGetProperty(name, out var value) && value.TryGetInt64(out long number) ? number : 0;

        private static readonly string[] DefaultComment =
        {
            "Which reply leads to which line. The one thing about an NPC the client has never held.",
            "",
            "The client ships every line an NPC can say and every reply it can be given, and",
            "nowhere does it say which goes with which - measured across all 6,467 of them. That",
            "pairing has always lived on Ankama's server, so it cannot be extracted or measured.",
            "It has to be decided, and this is where the decision goes.",
            "",
            "  npc      the NPC's id",
            "  map      0 for wherever it stands; a map id to say something different there",
            "  opening  which line it starts on; the first one when left out",
            "  lines    one per thing it says, each with what can be said back",
            "  next     the line that reply leads to; left out, the conversation ends",
            "",
            "Every line needs at least one reply. With an empty list the client draws its own",
            "Leave button, and that button never answers back: the window stays up and there is no",
            "way out but reconnecting.",
        };
    }
}
