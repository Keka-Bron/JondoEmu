using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Text.RegularExpressions;
using Jondo.Unity.Server.Network;
using Microsoft.Data.Sqlite;

namespace Jondo.Unity.Server.Managers
{
    /// <summary>
    /// What an NPC's line says through its «#1» when nothing written for the conversation says it:
    /// read off the line's own text, the one the client is about to show.
    /// </summary>
    /// <remarks>
    /// The client fills a line's «#n» with the strings its ios carries (f3) and leaves an empty gap
    /// when there are none: Hal Ylanza greeted «¡Te doy la bienvenida, !» and Captain Amakna
    /// congratulated nobody. The templates do not say what goes there -- a message is an id and a
    /// text key, nothing more --, so the text is what tells.
    ///
    /// Of the 1,619 NPC messages with a placeholder in the Spanish texts, 960 are the character's
    /// name and nothing else («¡Enhorabuena, #1!», «Tú eres #1, ¿verdad?»), 29 are his breed
    /// («$breed#1», which the client turns into the breed's name) and two his level («nivel #1»).
    /// The rest is left alone because it is something this server cannot know from the line:
    /// «$map#1» (the transporters' «¿Quieres volver a $map#1?»), a figure followed by its unit
    /// (the dopeuls' hours, a fee in kamas, pods), a line with several placeholders, and the three
    /// NPCs whose #1 is never a character: the tax collector and the prism, which name their guild
    /// or alliance, and the counting game's croupier, who says the running total.
    /// </remarks>
    public static class LinePlaceholders
    {
        /// <summary>What a line's «#1» stands for.</summary>
        public enum Kind
        {
            /// <summary>Nothing this server can fill, or no placeholder at all.</summary>
            None,
            /// <summary>The character's name.</summary>
            Name,
            /// <summary>The character's breed, as «$breed#1»: its id, which the client names.</summary>
            Breed,
            /// <summary>The character's level, as in «Soy de nivel #1».</summary>
            Level,
        }

        /// <summary>
        /// The NPCs whose #1 is never the character: the tax collector (1) and the prism (2141) name
        /// their guild or alliance, and the croupier of the counting game (7577) the total so far
        /// («Voy por #1»).
        /// </summary>
        private static readonly HashSet<int> NeverTheCharacter = new() { 1, 2141, 7577 };

        private static readonly Regex Placeholder = new(@"(\$(\w+))?#(\d+)", RegexOptions.Compiled);

        /// <summary>A figure: the placeholder followed by its unit.</summary>
        private static readonly Regex Figure = new(
            @"#1\s*(horas?|h|kamas|pods|puntos?|minutos?|segundos?|días?|veces|%)(?!\w)",
            RegexOptions.Compiled | RegexOptions.IgnoreCase);

        private static readonly Regex LevelBefore = new(@"\bnivel\s+#1\b", RegexOptions.Compiled | RegexOptions.IgnoreCase);
        private static readonly Regex FeeBefore = new(@"\b(costará|cuesta)\s+#1\b", RegexOptions.Compiled | RegexOptions.IgnoreCase);

        /// <summary>What the «#1» of this text stands for, from the text alone.</summary>
        public static Kind KindOf(string? text)
        {
            if (string.IsNullOrEmpty(text)) return Kind.None;
            var found = Kind.None;
            foreach (Match match in Placeholder.Matches(text))
            {
                if (match.Groups[3].Value != "1") return Kind.None;
                Kind here;
                if (match.Groups[2].Success)
                    here = match.Groups[2].Value == "breed" ? Kind.Breed : Kind.None;
                else
                    here = Kind.Name;
                if (here == Kind.None) return Kind.None;
                if (found != Kind.None && found != here) return Kind.None;
                found = here;
            }
            if (found != Kind.Name) return found;
            if (Figure.IsMatch(text) || FeeBefore.IsMatch(text)) return Kind.None;
            return LevelBefore.IsMatch(text) ? Kind.Level : Kind.Name;
        }

        /// <summary>The string a kind of placeholder takes for this character.</summary>
        public static string? ValueOf(Kind kind, string name, int breed, int level) => kind switch
        {
            Kind.Name => name,
            Kind.Breed => breed.ToString(System.Globalization.CultureInfo.InvariantCulture),
            Kind.Level => level.ToString(System.Globalization.CultureInfo.InvariantCulture),
            _ => null,
        };

        /// <summary>
        /// The line parameters of an NPC's message for the character in this session, or null when
        /// its text has nothing this server fills.
        /// </summary>
        public static IReadOnlyList<string>? For(long messageId, int npcId)
        {
            if (messageId <= 0 || NeverTheCharacter.Contains(npcId)) return null;
            var template = Npcs.TemplateOf(npcId);
            if (template == null) return null;
            int at = Array.IndexOf(template.Messages, messageId);
            if (at < 0 || at >= template.MessageTexts.Length) return null;

            var kind = _kinds.GetOrAdd(template.MessageTexts[at], key => KindOf(SpanishText(key)));
            var state = SessionContext.State;
            string? value = ValueOf(kind, state.CharacterName ?? "", state.Breed, state.CharacterLevel);
            return value == null ? null : new[] { value };
        }

        /// <summary>Each text key's kind, read once: the texts do not change while the server runs.</summary>
        private static readonly ConcurrentDictionary<long, Kind> _kinds = new();

        /// <summary>A text key's Spanish text from the base's Translations, empty when there is none.</summary>
        private static string SpanishText(long key)
        {
            if (key <= 0) return "";
            try
            {
                using var connection = new SqliteConnection(DatabaseManager.WorldConnectionString);
                connection.Open();
                var command = connection.CreateCommand();
                command.CommandText = "SELECT Text FROM Translations WHERE Key = $key;";
                command.Parameters.AddWithValue("$key", key.ToString(System.Globalization.CultureInfo.InvariantCulture));
                return command.ExecuteScalar() as string ?? "";
            }
            catch
            {
                return "";
            }
        }
    }
}
