using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Microsoft.Data.Sqlite;

namespace Jondo.Unity.Server.Managers
{
    /// <summary>
    /// The guild expeditions: the sixty dungeons Elmer Zenario (7860) sends a guild into from the
    /// function room of its hall, and the hall each character left from, which is where the
    /// expedition lets him out.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Nothing in the data links an expedition to its NPC: the catalogue's entrance for all of
    /// them is one placeholder map, and none declares a key. What links them is Elmer's own
    /// template. He declares five replies per expedition, always in the same order -- «Sí, quiero
    /// volver a esta sala.», «Retomar la expedición donde la dejaste.», «Comenzar la expedición.»,
    /// «Seleccionar otra expedición.» and «Seleccionar la expedición de X.» -- and two messages,
    /// «¿Seguro que quieres entrar en X?» and «¿Quieres volver a: $map#1?». The last reply of a
    /// group names the dungeon, and that name is the one the catalogue gives it after its
    /// «Expedición - », «Expedición de Audacia - » or «Expedición de Bravura - ».
    /// </para>
    /// <para>
    /// The ids are not arithmetic: the first twenty groups and the other forty were numbered
    /// apart. So the groups are cut where a «Seleccionar la expedición» closes one, and the
    /// dungeon is found by its name, not by position.
    /// </para>
    /// </remarks>
    internal static class Expeditions
    {
        /// <summary>Elmer Zenario.</summary>
        public const int Npc = 7860;

        /// <summary>One expedition as Elmer offers it.</summary>
        internal sealed record Offer(int DungeonId, long Select, long Begin, long Resume, long Other,
                                     long ReturnYes, long Confirm, long ReturnQuestion);

        /// <summary>What Elmer says outside the sixty groups.</summary>
        internal sealed record Talk(long Opening, long Which, long WhichExpeditions, long Abandon,
                                    long WhatAreThey, long[] Explanation, long[] ExplanationReplies,
                                    long Thanks);

        private const string SelectWording = "Seleccionar la expedición ";
        private const string BeginWording = "Comenzar la expedición.";
        private const string ResumeWording = "Retomar la expedición donde la dejaste.";
        private const string OtherWording = "Seleccionar otra expedición.";
        private const string ReturnYesWording = "Sí, quiero volver a esta sala.";
        private const string ConfirmWording = "¿Seguro que quieres entrar en ";
        private const string ReturnWording = "¿Quieres volver a";

        private static IReadOnlyList<Offer> _offers = Array.Empty<Offer>();
        private static Talk? _talk;

        public static IReadOnlyList<Offer> Offers => _offers;
        public static Talk? Lines => _talk;

        /// <summary>The expedition one of Elmer's replies belongs to, by any of its replies.</summary>
        public static Offer? ByReply(long reply)
            => _offers.FirstOrDefault(o => o.Select == reply || o.Begin == reply || o.Resume == reply
                                           || o.Other == reply || o.ReturnYes == reply);

        public static bool IsExpedition(int dungeonId) => _offers.Any(o => o.DungeonId == dungeonId);

        /// <summary>Reads Elmer's template and the catalogue. After the NPCs and the dungeons.</summary>
        public static void Initialize()
        {
            var template = Npcs.TemplateOf(Npc);
            if (template == null)
            {
                _offers = Array.Empty<Offer>();
                _talk = null;
                return;
            }

            var texts = TextsOf(template.ReplyTexts.Concat(template.MessageTexts));
            string[] replyTexts = template.ReplyTexts.Select(k => texts.GetValueOrDefault(k, "")).ToArray();
            string[] messageTexts = template.MessageTexts.Select(k => texts.GetValueOrDefault(k, "")).ToArray();
            // A room the client has no map for cannot be entered: 144 and 157 have theirs only in
            // the catalogue.
            var dungeons = DungeonManager.All.Values.Select(d => (d.Id, d.Name,
                d.Rooms.Count > 0 && d.Rooms.All(r => MapManager.GetMapInfo(r) != null && MapManager.WalkableCells.ContainsKey(r))));

            _offers = Parse(template.Replies, replyTexts, template.Messages, messageTexts, dungeons, out var skipped);
            _talk = TalkOf(template.DialogMessageId, template.Replies, replyTexts, template.Messages, messageTexts);
            Console.WriteLine($"[Expeditions] Elmer offers {_offers.Count} expeditions" +
                              (skipped.Count > 0 ? $"; left out: {string.Join(", ", skipped)}." : "."));
        }

        /// <summary>
        /// Elmer's groups, matched to the catalogue by name. A group whose dungeon has no rooms in
        /// this client, or that matches none, is left out and named in <paramref name="skipped"/>.
        /// </summary>
        internal static List<Offer> Parse(long[] replies, string[] replyTexts, long[] messages, string[] messageTexts,
                                          IEnumerable<(int Id, string Name, bool HasRooms)> dungeons,
                                          out List<string> skipped)
        {
            skipped = new List<string>();
            var result = new List<Offer>();
            if (replies == null || replyTexts == null || replies.Length != replyTexts.Length) return result;

            var byName = new Dictionary<string, (int Id, bool HasRooms)>();
            foreach (var (id, name, hasRooms) in dungeons)
            {
                string? key = CatalogueKey(name);
                if (key != null) byName[key] = (id, hasRooms);
            }

            var confirms = new List<long>();
            var returns = new List<long>();
            if (messages != null && messageTexts != null && messages.Length == messageTexts.Length)
            {
                foreach (var (id, text) in messages.Zip(messageTexts).OrderBy(m => m.First))
                {
                    if (text.StartsWith(ConfirmWording, StringComparison.Ordinal)) confirms.Add(id);
                    else if (text.StartsWith(ReturnWording, StringComparison.Ordinal)) returns.Add(id);
                }
            }

            long begin = 0, resume = 0, other = 0, yes = 0;
            int group = 0;
            foreach (var (id, text) in replies.Zip(replyTexts).OrderBy(r => r.First))
            {
                if (text == BeginWording) begin = id;
                else if (text == ResumeWording) resume = id;
                else if (text == OtherWording) other = id;
                else if (text == ReturnYesWording) yes = id;
                else if (text.StartsWith(SelectWording, StringComparison.Ordinal))
                {
                    string? key = SelectionKey(text);
                    long confirm = group < confirms.Count ? confirms[group] : 0;
                    long back = group < returns.Count ? returns[group] : 0;
                    group++;

                    if (key != null && (byName.TryGetValue(key, out var found) || Loosely(key, byName, out found))
                        && found.HasRooms && begin != 0)
                        result.Add(new Offer(found.Id, id, begin, resume, other, yes, confirm, back));
                    else
                        skipped.Add(key ?? text);
                    begin = resume = other = yes = 0;
                }
            }
            return result;
        }

        /// <summary>
        /// The one catalogue name of the same family whose words are all in Elmer's: he says «Sala
        /// del Trono de Minotot» where the catalogue says «Sala de Minotot». Two candidates are no
        /// match.
        /// </summary>
        private static bool Loosely(string key, Dictionary<string, (int Id, bool HasRooms)> byName,
                                    out (int Id, bool HasRooms) found)
        {
            found = default;
            int bar = key.IndexOf('|');
            string family = key.Substring(0, bar);
            var words = key.Substring(bar + 1).Split(' ', StringSplitOptions.RemoveEmptyEntries).ToHashSet();
            var candidates = byName.Where(e => e.Key.StartsWith(family + "|", StringComparison.Ordinal)
                                               && e.Key.Substring(bar + 1).Split(' ', StringSplitOptions.RemoveEmptyEntries)
                                                       .All(words.Contains))
                                   .ToList();
            if (candidates.Count != 1) return false;
            found = candidates[0].Value;
            return true;
        }

        /// <summary>«Seleccionar la expedición de audacia de la Cueva de Bworker.» -> "audacia|cueva de bworker".</summary>
        internal static string? SelectionKey(string text)
        {
            if (!text.StartsWith(SelectWording, StringComparison.Ordinal)) return null;
            string rest = text.Substring(SelectWording.Length).TrimEnd('.', ' ');
            string family = "";
            foreach (string f in new[] { "audacia", "bravura" })
            {
                if (rest.StartsWith("de " + f + " ", StringComparison.OrdinalIgnoreCase))
                {
                    family = f;
                    rest = rest.Substring(4 + f.Length);
                    break;
                }
            }
            foreach (string article in new[] { "de la ", "de los ", "de las ", "del ", "de " })
            {
                if (rest.StartsWith(article, StringComparison.OrdinalIgnoreCase))
                {
                    rest = rest.Substring(article.Length);
                    break;
                }
            }
            return family + "|" + rest.Trim().ToLowerInvariant();
        }

        /// <summary>«Expedición de Bravura - Sala de Minotot» -> "bravura|sala de minotot"; null for any other dungeon.</summary>
        internal static string? CatalogueKey(string name)
        {
            if (string.IsNullOrEmpty(name)) return null;
            int dash = name.IndexOf(" - ", StringComparison.Ordinal);
            if (dash < 0) return null;
            string head = name.Substring(0, dash).Trim().ToLowerInvariant();
            string family = head switch
            {
                "expedición" => "",
                "expedición de audacia" => "audacia",
                "expedición de bravura" => "bravura",
                _ => "?",
            };
            if (family == "?") return null;
            return family + "|" + name.Substring(dash + 3).Trim().ToLowerInvariant();
        }

        /// <summary>
        /// The lines around the groups: his opening, «¿Qué expedición debes realizar?», and the four
        /// that tell what an expedition is, each answered by the reply after it.
        /// </summary>
        internal static Talk? TalkOf(long opening, long[] replies, string[] replyTexts, long[] messages, string[] messageTexts)
        {
            if (replies == null || messages == null || replies.Length != replyTexts.Length
                || messages.Length != messageTexts.Length) return null;

            long R(string wording) => replies.Zip(replyTexts).Where(r => r.Second == wording)
                                             .Select(r => r.First).DefaultIfEmpty(0).Min();
            long which = messages.Zip(messageTexts).Where(m => m.Second.Contains("¿Qué expedición debes realizar?"))
                                 .Select(m => m.First).DefaultIfEmpty(0).Min();

            // The explanation: the messages between «¿Qué expedición...?» and the first group's
            // that are none of the groups' own, in order.
            long[] explanation = messages.Zip(messageTexts)
                .Where(m => m.First != opening && m.First != which
                            && !m.Second.StartsWith(ConfirmWording, StringComparison.Ordinal)
                            && !m.Second.StartsWith(ReturnWording, StringComparison.Ordinal))
                .Select(m => m.First).OrderBy(m => m).ToArray();

            // Each explanation line goes on with the reply that answers it, as the client shows
            // them: «Seguir escuchando.», «Conozco esa sensación.», «Sí, eso podría sacarme de la
            // rutina.»; the last one with the question of which, and thanks.
            long[] onward =
            {
                R("Seguir escuchando."),
                R("Conozco esa sensación."),
                R("Sí, eso podría sacarme de la rutina."),
            };

            return new Talk(opening, which, R("¿Qué expediciones puede hacer mi gremio?"),
                            R("Me gustaría abandonar la expedición."), R("¿En qué consisten las expediciones?"),
                            explanation, onward, R("Gracias por la información."));
        }

        private static Dictionary<long, string> TextsOf(IEnumerable<long> keys)
        {
            var result = new Dictionary<long, string>();
            var wanted = keys.Where(k => k > 0).Distinct().ToList();
            if (wanted.Count == 0) return result;
            try
            {
                using var connection = new SqliteConnection(DatabaseManager.WorldConnectionString);
                connection.Open();
                using var command = connection.CreateCommand();
                command.CommandText = "SELECT Key, Text FROM Translations WHERE Key IN (" +
                                      string.Join(",", wanted.Select(k => "'" + k.ToString(CultureInfo.InvariantCulture) + "'")) + ");";
                using var reader = command.ExecuteReader();
                while (reader.Read())
                {
                    if (long.TryParse(reader.GetString(0), NumberStyles.Integer, CultureInfo.InvariantCulture, out long k))
                        result[k] = reader.IsDBNull(1) ? "" : reader.GetString(1);
                }
            }
            catch (SqliteException ex)
            {
                Console.WriteLine($"[Expeditions] Could not read Elmer's texts: {ex.Message}");
            }
            return result;
        }

        // ─── The hall each character left from ─────────────────────────────────

        private static readonly ConcurrentDictionary<long, long> _hallOf = new();

        internal static void EnsureTable(SqliteConnection connection)
        {
            using var command = connection.CreateCommand();
            command.CommandText = @"
                CREATE TABLE IF NOT EXISTS CharacterExpeditionHall (
                    CharacterId INTEGER NOT NULL PRIMARY KEY,
                    MapId INTEGER NOT NULL
                );";
            command.ExecuteNonQuery();
        }

        /// <summary>Notes the hall a character sets off from: where his expeditions let him out.</summary>
        internal static void SetOffFrom(long characterId, long hallMapId)
        {
            if (characterId <= 0 || hallMapId == 0) return;
            _hallOf[characterId] = hallMapId;
            try
            {
                using var connection = new SqliteConnection(DatabaseManager.WorldConnectionString);
                connection.Open();
                using var command = connection.CreateCommand();
                command.CommandText = "INSERT INTO CharacterExpeditionHall (CharacterId, MapId) VALUES ($c, $m) " +
                                      "ON CONFLICT (CharacterId) DO UPDATE SET MapId = $m;";
                command.Parameters.AddWithValue("$c", characterId);
                command.Parameters.AddWithValue("$m", hallMapId);
                command.ExecuteNonQuery();
            }
            catch (SqliteException ex)
            {
                Console.WriteLine($"[Expeditions] Could not note the hall of {characterId}: {ex.Message}");
            }
        }

        /// <summary>The hall a character set off from, or 0 when he never did.</summary>
        internal static long HallOf(long characterId)
        {
            if (_hallOf.TryGetValue(characterId, out long map)) return map;
            try
            {
                using var connection = new SqliteConnection(DatabaseManager.WorldConnectionString);
                connection.Open();
                using var command = connection.CreateCommand();
                command.CommandText = "SELECT MapId FROM CharacterExpeditionHall WHERE CharacterId = $c;";
                command.Parameters.AddWithValue("$c", characterId);
                if (command.ExecuteScalar() is long found)
                {
                    _hallOf[characterId] = found;
                    return found;
                }
            }
            catch (SqliteException)
            {
            }
            return 0;
        }

        /// <summary>
        /// Where an expedition lets this character out: the hall he set off from. Any other
        /// dungeon, or a character with no hall noted, gets the catalogue's way out.
        /// </summary>
        internal static long WayOut(DungeonManager.Dungeon dungeon, long characterId)
        {
            if (dungeon != null && IsExpedition(dungeon.Id))
            {
                long hall = HallOf(characterId);
                if (hall != 0 && MapManager.GetMapInfo(hall) != null) return hall;
            }
            return DungeonManager.WayOut(dungeon!);
        }
    }
}
