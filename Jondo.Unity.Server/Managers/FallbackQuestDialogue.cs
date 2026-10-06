using System;
using System.Collections.Generic;
using Jondo.Unity.World.Content;
using Microsoft.Data.Sqlite;

namespace Jondo.Unity.Server.Managers
{
    /// <summary>
    /// Accept / refuse conversations for quest givers that have no authored tree.
    /// </summary>
    /// <remarks>
    /// The client never ships which reply leads where — that lived on Ankama's server — so
    /// <c>content/npcs/dialogues.json</c> is written by hand. Until a tree exists for an NPC, the
    /// old fallback offered a single goodbye reply and almost no quest could be taken (only the
    /// seventy whose first step happens to be the template's opening line).
    ///
    /// This builds a one-line tree from the NPC's own template: one Accept that starts the first
    /// quest they can offer right now, and one Refuse that closes the window. Reply ids stay ones
    /// the template already declares, so the client can resolve their wording.
    /// </remarks>
    public static class FallbackQuestDialogue
    {
        /// <summary>Spanish and French openings of "yes, I'll take it" replies in the client table.</summary>
        private static readonly string[] AcceptWordings =
        {
            "Aceptar",
            "Vale",
            "De acuerdo",
            "Accepter",
            "Oui",
        };

        /// <summary>Spanish and French openings of "no thanks" replies in the client table.</summary>
        private static readonly string[] DeclineWordings =
        {
            "No, gracias",
            "Voy a pensármelo",
            "Tomarte un tiempo",
            "Non, merci",
            "Refuser",
            "Non",
        };

        private static HashSet<long>? _acceptTexts;
        private static HashSet<long>? _declineTexts;
        private static readonly object _lock = new object();

        /// <summary>
        /// A minimal accept/refuse dialogue, or null when this NPC has nothing to hand over or no
        /// usable replies.
        /// </summary>
        public static NpcDialogue? Build(int npcId, long mapId, Npcs.Template template)
        {
            if (template.DialogMessageId == 0 || template.Replies.Length == 0) return null;

            var offers = Quests.OfferedRightNowBy(npcId, mapId);
            if (offers.Count == 0) return null;

            if (!TryPickReplies(template, out long accept, out long decline)) return null;

            var choices = new List<DialogueChoice>(2)
            {
                new DialogueChoice { Reply = accept, StartsQuest = offers[0] },
            };
            if (decline != 0 && decline != accept)
            {
                choices.Add(new DialogueChoice { Reply = decline });
            }

            return new NpcDialogue
            {
                NpcId = npcId,
                MapId = mapId,
                Opening = template.DialogMessageId,
                Lines = new[]
                {
                    new DialogueLine
                    {
                        Message = template.DialogMessageId,
                        Choices = choices,
                    },
                },
            };
        }

        /// <summary>
        /// Picks an accept reply and a refuse reply from the template. Exposed for tests.
        /// </summary>
        internal static bool TryPickReplies(Npcs.Template template, out long accept, out long decline)
        {
            accept = 0;
            decline = 0;
            EnsureTextKeys();

            int upto = Math.Min(template.Replies.Length, template.ReplyTexts.Length);
            for (int i = 0; i < upto; i++)
            {
                long text = template.ReplyTexts[i];
                long reply = template.Replies[i];
                if (accept == 0 && _acceptTexts!.Contains(text)) accept = reply;
                if (decline == 0 && _declineTexts!.Contains(text)) decline = reply;
                if (accept != 0 && decline != 0) break;
            }

            if (accept == 0)
            {
                // No labelled Accept in this template: first reply is the best guess, last is leave.
                accept = template.Replies[0];
                if (template.Replies.Length > 1) decline = template.Replies[^1];
            }
            else if (decline == 0 && template.Replies.Length > 1)
            {
                long chosen = accept;
                foreach (long reply in template.Replies)
                {
                    if (reply != chosen) { decline = reply; break; }
                }
            }

            return accept != 0;
        }

        private static void EnsureTextKeys()
        {
            if (_acceptTexts != null && _declineTexts != null) return;
            lock (_lock)
            {
                if (_acceptTexts != null && _declineTexts != null) return;
                _acceptTexts = TextKeysStartingWith(AcceptWordings);
                _declineTexts = TextKeysStartingWith(DeclineWordings);
            }
        }

        /// <summary>Resets the cached text keys. For tests that load a different Translations table.</summary>
        internal static void ResetCache()
        {
            lock (_lock)
            {
                _acceptTexts = null;
                _declineTexts = null;
            }
        }

        /// <summary>Installs known accept/decline text keys without reading the world database.</summary>
        internal static void SeedTextKeysForTests(IEnumerable<long> accept, IEnumerable<long> decline)
        {
            lock (_lock)
            {
                _acceptTexts = new HashSet<long>(accept);
                _declineTexts = new HashSet<long>(decline);
            }
        }

        private static HashSet<long> TextKeysStartingWith(IEnumerable<string> wordings)
        {
            var found = new HashSet<long>();
            try
            {
                using var connection = new SqliteConnection(DatabaseManager.WorldConnectionString);
                connection.Open();

                foreach (string wording in wordings)
                {
                    using var command = connection.CreateCommand();
                    command.CommandText = "SELECT Key FROM Translations WHERE Text LIKE $like;";
                    command.Parameters.AddWithValue("$like", wording + "%");

                    using var reader = command.ExecuteReader();
                    while (reader.Read())
                    {
                        if (long.TryParse(reader.GetString(0), out long key)) found.Add(key);
                    }
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[Quests] Fallback dialogue texts could not be read: {ex.Message}");
            }

            return found;
        }
    }
}
