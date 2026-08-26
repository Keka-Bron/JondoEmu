using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using Jondo.Unity.Launcher;
using Jondo.Unity.World.Client;
using Microsoft.Data.Sqlite;

namespace Jondo.Unity.Studio.Data
{
    /// <summary>An NPC, by id and by name.</summary>
    public sealed class NpcSummary
    {
        public int Id { get; init; }

        public string Name { get; init; } = "";

        /// <summary>How many lines its template declares, and how many replies.</summary>
        /// <summary>
        /// Its appearance, as <c>{bones|skins|colours|scales}</c>. What the client draws it from.
        /// </summary>
        public string Look { get; init; } = "";

        public int Messages { get; init; }

        public int Replies { get; init; }

        public override string ToString() => $"{Id}  {Name}";
    }

    /// <summary>One thing an NPC can say, or one thing that can be said to it, with its text.</summary>
    public sealed class DialogueText
    {
        public long Id { get; init; }

        public string Text { get; init; } = "";

        public override string ToString()
            => Text.Length == 0 ? Id.ToString() : $"{Id}  {Short()}";

        public string Short(int limit = 110)
        {
            string flat = Text.Replace('\n', ' ').Replace('\r', ' ');
            return flat.Length <= limit ? flat : flat[..limit] + "…";
        }
    }

    /// <summary>Everything an NPC's template declares that a dialogue can be built from.</summary>
    public sealed class NpcDialogueSource
    {
        public int NpcId { get; init; }

        public string Name { get; init; } = "";

        /// <summary>The lines it can say. Ankama's own, in the order the client lists them.</summary>
        public IReadOnlyList<DialogueText> Messages { get; init; } = Array.Empty<DialogueText>();

        /// <summary>The replies it can be given. The same list, and nothing says which goes with which.</summary>
        public IReadOnlyList<DialogueText> Replies { get; init; } = Array.Empty<DialogueText>();
    }

    /// <summary>
    /// The NPCs, their names, and the closed set of things they can say and be told.
    /// </summary>
    /// <remarks>
    /// A dialogue editor showing numbers would be unusable — nobody can decide that reply 6016
    /// belongs under line 3312 without reading either of them — so this resolves the text, and it
    /// takes two different routes to do it because Ankama stores the two halves differently:
    ///
    /// <code>
    ///   a reply   dialogReplies [6016, 23739]  →  Translations[23739]  →  "Informarse sobre…"
    ///   a line    dialogData    messageId 6169 →  npc_dialogos.json    →  Translations[…]
    /// </code>
    ///
    /// The reply carries its translation key next to its id. The line does not: its
    /// <c>messageId</c> is an id into <c>NpcMessagesDataRoot</c>, which is 16.8 MB of the client
    /// dump, and <c>tools/extraer_dialogos_npc.py</c> boils it down to the one number per entry
    /// that matters. Without that file this still works and shows ids for the lines.
    ///
    /// Nothing is cached wholesale. There are 6,468 NPCs and 339,175 translations, and holding all
    /// of it would cost more memory than the whole rest of the editor; a template is read when
    /// somebody clicks it.
    /// </remarks>
    public sealed class NpcCatalogue : IDisposable
    {
        private readonly SqliteConnection? _world;
        private readonly Dictionary<long, string> _messageKeys = new Dictionary<long, string>();

        /// <summary>
        /// The client's own text table, in whatever language is in use.
        /// </summary>
        /// <remarks>
        /// Preferred over the Translations table in world.db, which holds one language only —
        /// whichever one happened to be extracted. The client ships five, and an NPC is not called
        /// the same thing in Spanish and in French, so a name that cannot follow the language is a
        /// name that is wrong two thirds of the time.
        /// </remarks>
        private readonly ClientText? _text;

        public NpcCatalogue(ClientText? text = null, Action<string>? report = null)
        {
            _text = text;

            try
            {
                _world = new SqliteConnection(Paths.WorldConnectionString + ";Mode=ReadOnly");
                _world.Open();
            }
            catch (Exception ex)
            {
                report?.Invoke($"world.db could not be opened: {ex.Message}");
                _world = null;
            }

            ReadMessageKeys(report);
        }

        /// <summary>True when there is a world database to read. Everything degrades without one.</summary>
        public bool Ready => _world != null;

        /// <summary>How many line-to-text jumps were loaded. Zero means lines show as ids.</summary>
        public int MessageKeys => _messageKeys.Count;

        private void ReadMessageKeys(Action<string>? report)
        {
            string path = Paths.NpcDialoguesJson;
            if (!File.Exists(path))
            {
                report?.Invoke($"{Path.GetFileName(path)} is not there; NPC lines will show as " +
                               "numbers. Run tools/extraer_dialogos_npc.py to build it.");
                return;
            }

            try
            {
                using var doc = JsonDocument.Parse(File.ReadAllText(path));
                if (!doc.RootElement.TryGetProperty("mensajes", out var table)) return;

                foreach (var entry in table.EnumerateObject())
                {
                    if (long.TryParse(entry.Name, out long id) && entry.Value.ValueKind == JsonValueKind.String)
                    {
                        _messageKeys[id] = entry.Value.GetString() ?? "";
                    }
                }
            }
            catch (Exception ex)
            {
                report?.Invoke($"{Path.GetFileName(path)} is unreadable: {ex.Message}");
            }
        }

        /// <summary>Every NPC there is, by name. What the placement screen picks from.</summary>
        public List<NpcSummary> All() => Read(onlyTalkers: false);

        /// <summary>Every NPC that has something to say. What the dialogue screen picks from.</summary>
        public List<NpcSummary> WithDialogue() => Read(onlyTalkers: true);

        private List<NpcSummary> Read(bool onlyTalkers)
        {
            var all = new List<NpcSummary>();
            if (_world == null) return all;

            using var command = _world.CreateCommand();
            command.CommandText = @"
                SELECT n.Id, n.NameId, t.Text, n.Data
                FROM NpcTemplates n
                LEFT JOIN Translations t ON t.Key = CAST(n.NameId AS TEXT);";

            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                string data = reader.IsDBNull(3) ? "" : reader.GetString(3);
                var (messages, replies) = CountOf(data);
                if (onlyTalkers && messages == 0 && replies == 0) continue;

                all.Add(new NpcSummary
                {
                    Id = reader.GetInt32(0),
                    Name = Named(reader.IsDBNull(1) ? 0 : reader.GetInt64(1),
                                 reader.IsDBNull(2) ? "" : reader.GetString(2)),
                    Look = LookOf(data),
                    Messages = messages,
                    Replies = replies,
                });
            }

            all.Sort((a, b) => string.Compare(a.Name, b.Name, StringComparison.CurrentCultureIgnoreCase));
            return all;
        }

        /// <summary>Everything one NPC declares, with the text resolved.</summary>
        public NpcDialogueSource? Source(int npcId)
        {
            if (_world == null) return null;

            using var command = _world.CreateCommand();
            command.CommandText = @"
                SELECT n.NameId, t.Text, n.Data
                FROM NpcTemplates n
                LEFT JOIN Translations t ON t.Key = CAST(n.NameId AS TEXT)
                WHERE n.Id = $id;";
            command.Parameters.AddWithValue("$id", npcId);

            using var reader = command.ExecuteReader();
            if (!reader.Read()) return null;

            string name = Named(reader.IsDBNull(0) ? 0 : reader.GetInt64(0),
                                reader.IsDBNull(1) ? "" : reader.GetString(1));
            string data = reader.IsDBNull(2) ? "" : reader.GetString(2);

            var messages = new List<DialogueText>();
            var replies = new List<DialogueText>();
            ReadTemplate(data, messages, replies);

            return new NpcDialogueSource
            {
                NpcId = npcId,
                Name = name,
                Messages = messages,
                Replies = replies,
            };
        }

        private List<DialogueText>? _everyLine;

        /// <summary>
        /// Every line any NPC can say, which is 55,037 of them.
        /// </summary>
        /// <remarks>
        /// Here because of what the protocol turned out to allow. The <c>ios</c> the server sends
        /// carries the line as a plain id into the game's own message table — nothing ties it to
        /// the NPC that is speaking — so an NPC can be made to say <b>any</b> line in the game, not
        /// only the handful its template declares.
        ///
        /// That is as close to writing your own text as this can get, and it is a long way closer
        /// than it looks: 55,037 lines is most of what anyone would want to say.
        /// </remarks>
        public List<DialogueText> EveryLine()
        {
            if (_everyLine != null) return _everyLine;

            _everyLine = new List<DialogueText>(_messageKeys.Count);
            foreach (var pair in _messageKeys)
            {
                string said = Text(pair.Value);
                if (said.Length == 0) continue;
                _everyLine.Add(new DialogueText { Id = pair.Key, Text = said });
            }

            _everyLine.Sort((a, b) => string.Compare(a.Text, b.Text, StringComparison.CurrentCultureIgnoreCase));
            return _everyLine;
        }

        private List<DialogueText>? _everyReply;

        /// <summary>
        /// Every reply in the game, not just the ones one NPC declares.
        /// </summary>
        /// <remarks>
        /// Here because of a real limit that looks like a missing feature: a reply's text belongs
        /// to Ankama and lives in the client, so one cannot be typed. What can be done — and is
        /// almost always what somebody actually wants — is to use a line that already exists
        /// somewhere else in the game. "Sí.", "No, gracias." and "Cuéntame más." are all in there
        /// already, several times over.
        ///
        /// Built by walking all 6,468 templates, which takes a moment, so it is built the first
        /// time somebody asks for it and not before.
        /// </remarks>
        public List<DialogueText> EveryReply()
        {
            if (_everyReply != null) return _everyReply;

            _everyReply = new List<DialogueText>();
            if (_world == null) return _everyReply;

            var seen = new HashSet<long>();
            using var command = _world.CreateCommand();
            command.CommandText = "SELECT Data FROM NpcTemplates WHERE Data IS NOT NULL;";

            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                var messages = new List<DialogueText>();
                var replies = new List<DialogueText>();
                ReadTemplate(reader.GetString(0), messages, replies);

                foreach (var reply in replies)
                {
                    if (seen.Add(reply.Id)) _everyReply.Add(reply);
                }
            }

            _everyReply.Sort((a, b) => string.Compare(a.Text, b.Text, StringComparison.CurrentCultureIgnoreCase));
            return _everyReply;
        }

        /// <summary>The appearance out of a template's JSON.</summary>
        private static string LookOf(string data)
        {
            if (data.Length == 0) return "";

            try
            {
                using var doc = JsonDocument.Parse(data);
                return doc.RootElement.TryGetProperty("look", out var look) ? look.GetString() ?? "" : "";
            }
            catch (JsonException)
            {
                return "";
            }
        }

        /// <summary>The name in the language in use, falling back to the one baked into world.db.</summary>
        private string Named(long nameId, string fallback)
        {
            if (nameId != 0 && _text != null)
            {
                string said = _text.Of(nameId);
                if (said.Length > 0) return said;
            }

            return fallback;
        }

        /// <summary>One translation, by its key. Empty when there is not one.</summary>
        public string Text(string key)
        {
            if (key.Length == 0) return "";

            if (_text != null && long.TryParse(key, out long number))
            {
                string said = _text.Of(number);
                if (said.Length > 0) return said;
            }

            if (_world == null) return "";

            using var command = _world.CreateCommand();
            command.CommandText = "SELECT Text FROM Translations WHERE Key = $key;";
            command.Parameters.AddWithValue("$key", key);

            object? text = command.ExecuteScalar();
            return text as string ?? "";
        }

        private static (int Messages, int Replies) CountOf(string data)
        {
            if (data.Length == 0) return (0, 0);

            try
            {
                using var doc = JsonDocument.Parse(data);
                int messages = ArrayLength(doc.RootElement, "dialogData");
                int replies = ArrayLength(doc.RootElement, "dialogReplies");
                return (messages, replies);
            }
            catch (JsonException)
            {
                return (0, 0);
            }
        }

        private static int ArrayLength(JsonElement root, string name)
            => root.TryGetProperty(name, out var holder)
            && holder.TryGetProperty("Array", out var array)
            && array.ValueKind == JsonValueKind.Array
                ? array.GetArrayLength()
                : 0;

        private void ReadTemplate(string data, List<DialogueText> messages, List<DialogueText> replies)
        {
            if (data.Length == 0) return;

            try
            {
                using var doc = JsonDocument.Parse(data);
                var root = doc.RootElement;

                if (root.TryGetProperty("dialogData", out var dialog)
                    && dialog.TryGetProperty("Array", out var blocks)
                    && blocks.ValueKind == JsonValueKind.Array)
                {
                    foreach (var block in blocks.EnumerateArray())
                    {
                        // The id that matters is messageId, NOT the block's own id.
                        //
                        // dialogData carries both and they are different numbers: Snori Nairb's
                        // first line is id 3312, messageId 6169. What the server puts in the ios
                        // it sends is 6169 — checked against the captures, where his dialogue
                        // opens with exactly that. Keying a tree on 3312 would have made every
                        // authored NPC say some other line, or nothing at all, and the editor
                        // would have looked right the whole time.
                        if (!block.TryGetProperty("messageId", out var m) || !m.TryGetInt64(out long messageId))
                        {
                            continue;
                        }

                        if (messageId == 0) continue;

                        messages.Add(new DialogueText
                        {
                            Id = messageId,
                            Text = _messageKeys.TryGetValue(messageId, out string? key) ? Text(key) : "",
                        });
                    }
                }

                // A reply is a pair: its own id and, right next to it, the key of its text. Which is
                // why replies read straight out and lines have to go the long way round.
                if (root.TryGetProperty("dialogReplies", out var list)
                    && list.TryGetProperty("Array", out var array)
                    && array.ValueKind == JsonValueKind.Array)
                {
                    foreach (var reply in array.EnumerateArray())
                    {
                        if (!reply.TryGetProperty("values", out var values)) continue;
                        if (!values.TryGetProperty("Array", out var pair)) continue;
                        if (pair.ValueKind != JsonValueKind.Array || pair.GetArrayLength() < 2) continue;

                        long replyId = pair[0].TryGetInt64(out long first) ? first : 0;
                        long textKey = pair[1].TryGetInt64(out long second) ? second : 0;
                        if (replyId == 0) continue;

                        replies.Add(new DialogueText
                        {
                            Id = replyId,
                            Text = textKey == 0 ? "" : Text(textKey.ToString()),
                        });
                    }
                }
            }
            catch (JsonException)
            {
                // A template that will not parse is one NPC missing from the list, not a reason to
                // take the section down.
            }
        }

        public void Dispose() => _world?.Dispose();
    }
}
