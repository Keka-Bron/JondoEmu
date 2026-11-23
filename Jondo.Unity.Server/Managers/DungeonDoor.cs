using System;
using System.Collections.Generic;
using Microsoft.Data.Sqlite;

namespace Jondo.Unity.Server.Managers
{
    /// <summary>
    /// The replies a dungeon guardian uses for fresh entry and resuming a saved room.
    /// </summary>
    /// <remarks>
    /// The guardian was showing one option, "No.", because with no authored tree the server falls
    /// back to the last reply in the template. Mawy Ingals, who guards the Granero del Girasol
    /// Hambriento, declares nineteen of them, including these:
    ///
    /// <code>
    ///    6615  Utilizar el manojo de llaves.
    ///    2802  Darle la llave y entrar.
    ///    8870  Retomar la Mazmorra de los Campos donde la dejaste.
    ///   11162  Teletransportar a los miembros del grupo.
    ///   20907  No.                                    &lt;-- the only one that was shown
    /// </code>
    ///
    /// <b>The ids cannot be written down and do not need to be.</b> They are per NPC -- 121
    /// different ones across the game for the keyring alone -- but the WORDING is fixed, so each
    /// guardian's own reply is found by matching the text the client itself would draw. Nothing
    /// here is hardcoded per dungeon.
    ///
    /// <b>The one inference, named as one.</b> Some guardians declare the same wording twice,
    /// because they also guard the Expedición version of their dungeon -- Mawy has 2802 and 74100,
    /// both "Darle la llave y entrar". Every reply belonging to that newer generation is numbered
    /// above <see cref="Expedition"/>: 29 of them, and not one base-dungeon reply is. Dropping
    /// those leaves 104 of the 119 guardians with exactly one reply of each kind. The other 15
    /// guard several base dungeons at once and the data does not say which reply opens which; they
    /// get the lowest, and a line in the log saying so. A guardian who only has replies above the
    /// line -- the keepers of the dungeons that came with that generation, such as the Coto del
    /// Venerable and the Altar de la Despedida -- keeps them: there is no older one to prefer.
    /// </remarks>
    public static class DungeonDoor
    {
        /// <summary>What the client draws for the reply that spends the keyring.</summary>
        public const string KeyringWording = "Utilizar el manojo de llaves";

        /// <summary>
        /// The keyring as the guardians of the Crocantería, Dios Kao's temple and the Waddictos'
        /// foundry word it. Their only keyring reply says this, so the keyring never opened them.
        /// </summary>
        public static readonly string[] OtherKeyringWordings = { "Enseñarle el manojo de llaves." };

        /// <summary>And for the one that hands over the dungeon's own key.</summary>
        public const string KeyWording = "Darle la llave y entrar";

        /// <summary>
        /// The same reply as the Equipo Turist-A hosts word it: «Utilizar la llave.» before the
        /// Haunted Mansion and the Kwakwa's nest, where only the keyring was offered.
        /// </summary>
        public const string UseKeyWording = "Utilizar la llave";

        /// <summary>And as the kanniboul host of the isle of Moon words it: «Toma la llave.»</summary>
        public const string TakeKeyWording = "Toma la llave";

        /// <summary>
        /// And as the guardians who also take the keyring word it their own way. Without these their
        /// key reply led nowhere and only the keyring opened the door, so once the week's free entry
        /// was spent there was no way in at all, key in hand.
        /// </summary>
        /// <remarks>
        /// Read off the game's text table: of the 97 guardians that offer the keyring, 30 offered no
        /// key reply the server knew. Twenty-four of them simply say «Darle la llave.» -- director
        /// Grunob's doorman at the goblins' Akademia among them --, five name the key («Darle la llave
        /// del tofullinero.», «... de la Mazmorra de los Bworks.»), and the guardians of the
        /// Dragocerdo's den, Sfinter Cell's lair and the Morsagüino Real's dig have a sentence of
        /// their own.
        ///
        /// Whole sentences rather than «Darle la llave» alone: that prefix also opens «Darle la llave
        /// al capitán.» and «Darle la llave de Brakmar.», quest replies of two NPCs who keep no door.
        /// </remarks>
        public static readonly string[] OtherKeyWordings =
        {
            "Darle la llave.",
            "Darle la llave del ",
            "Darle la llave de la Mazmorra",
            "Darle la llave para entrar",
            "Darle la llave y atravesar las puertas",
            "Darle la llave y escuchar el canto de Kidibom",
            "Darle las dos llaves y entrar",
            "Presentar la llave y entrar",
            "Entregar las llaves y entrar",
            "Aceptar y entrar con la llave",
        };

        /// <summary>
        /// Reply ids at or above this belong to the Expedición generation, not the base dungeon.
        /// </summary>
        /// <remarks>
        /// Measured, not chosen: of the 119 guardians, 29 replies with these two wordings sit above
        /// 74000 and every one of them is an Expedición; none of the base ones reaches it.
        /// </remarks>
        public const long Expedition = 74000;

        /// <summary>
        /// What the guardian's own follow-up questions are answered with: «Sí.» to «¿Seguro que quieres
        /// utilizar una llave para entrar?», «Sí, quiero volver a esta sala.» to «¿Quieres volver a
        /// $map#1?».
        /// </summary>
        /// <remarks>
        /// One of them is often the template's LAST reply, the one a guardian with no written tree
        /// falls back on as his farewell: Otto Rino, at the door of Dramak's theatre, said «El teatro
        /// está cerrado al público» and offered the keyring, the key and a «Sí.» that agreed to nothing.
        /// </remarks>
        public const string YesWording = "Sí";

        public sealed class Options
        {
            /// <summary>"Utilizar el manojo de llaves.", or zero if this guardian has no such reply.</summary>
            public long Keyring { get; init; }

            /// <summary>"Darle la llave y entrar.", or zero.</summary>
            public long Key { get; init; }

            public long Resume { get; init; }

            /// <summary>Whether the guardian declared more than one of either, so the pick is a guess.</summary>
            public bool Ambiguous { get; init; }

            /// <summary>Whether the guardian asks the player to pick, with either reply.</summary>
            public bool Asks => Keyring != 0 || Key != 0;

            /// <summary>
            /// His answers to his own follow-up questions (<see cref="YesWording"/>), which the first
            /// line does not offer.
            /// </summary>
            public IReadOnlySet<long> Yes { get; init; } = new HashSet<long>();
        }

        private static readonly Dictionary<int, Options> _byNpc = new Dictionary<int, Options>();

        /// <summary>What this guardian can offer. Never null; both ids zero when it is not one.</summary>
        public static Options For(int npcId)
            => _byNpc.TryGetValue(npcId, out var options) ? options : Empty;

        private static readonly Options Empty = new Options();

        /// <summary>Every resume reply of each guardian, with the words it says.</summary>
        private static readonly Dictionary<int, List<(long Reply, string Words)>> _resumesByNpc = new();

        /// <summary>The words of each guardian's key and resume replies, below the Expedition ones.</summary>
        private static readonly Dictionary<int, List<string>> _doorWordsByNpc = new();

        /// <summary>
        /// Whether one of this guardian's key or resume replies names that dungeon.
        /// </summary>
        /// <remarks>
        /// Two dungeons can share a door map: Kurk Ingals keeps the Tofu House and Kazy Ingals the
        /// Royal Tofu House, both in the peasants' workshop. What each of them says at the door
        /// («Darle la llave del Tofullinero Real.», «Retomar la Mazmorra de los Tofus donde la
        /// dejaste.») is the only thing in the data that tells which door is whose.
        /// </remarks>
        public static bool Names(int npcId, string? dungeonName)
        {
            if (string.IsNullOrEmpty(dungeonName) || !_doorWordsByNpc.TryGetValue(npcId, out var words)) return false;
            return words.Any(w => w.Contains(dungeonName, StringComparison.OrdinalIgnoreCase));
        }

        /// <summary>
        /// The resume reply of this guardian for that dungeon: the one that names it, or the
        /// guardian's only pick when none does.
        /// </summary>
        /// <remarks>
        /// A guardian who keeps two dungeons has a resume reply for each: Lorkos, at the door of the
        /// Minotot Room and of the Minotoror's Labyrinth, offered «Retomar el Centro del Laberinto
        /// del Minotauroro donde lo dejaste» -- the lowest of his -- to resume the Minotot Room.
        /// </remarks>
        public static long ResumeFor(int npcId, string? dungeonName)
        {
            long any = For(npcId).Resume;
            if (string.IsNullOrEmpty(dungeonName) || !_resumesByNpc.TryGetValue(npcId, out var all)) return any;
            return ResumeNaming(all, dungeonName, any);
        }

        /// <summary>The lowest of those replies whose words name the dungeon, or the fallback.</summary>
        public static long ResumeNaming(IEnumerable<(long Reply, string Words)> replies, string dungeonName, long fallback)
        {
            long named = replies.Where(r => r.Words.Contains(dungeonName, StringComparison.OrdinalIgnoreCase))
                                .Select(r => r.Reply).DefaultIfEmpty(0).Min();
            return named != 0 ? named : fallback;
        }

        /// <summary>
        /// Reads the client's text table once and works out each guardian's entry replies.
        /// </summary>
        /// <remarks>
        /// Done at boot rather than per conversation: it is one pass over the NPC templates already
        /// in memory and queries for translation keys. Doing it while somebody is stood at a door would be a database round trip
        /// in the middle of a dialogue.
        /// </remarks>
        public static void Initialize()
        {
            _byNpc.Clear();
            _resumesByNpc.Clear();
            _doorWordsByNpc.Clear();

            var keyring = TextKeysSaying(KeyringWording);
            foreach (string wording in OtherKeyringWordings) keyring.UnionWith(TextKeysSaying(wording));
            var key = TextKeysSaying(KeyWording);
            key.UnionWith(TextKeysSaying(UseKeyWording));
            key.UnionWith(TextKeysSaying(TakeKeyWording));
            foreach (string wording in OtherKeyWordings) key.UnionWith(TextKeysSaying(wording));
            // «Sí.» and «Sí, ...», not every sentence that opens with those letters («Sígueme»).
            var yes = TextKeysSaying(YesWording + ".");
            yes.UnionWith(TextKeysSaying(YesWording + ","));
            var resume = TextKeysSaying("Retomar % donde % dejaste");
            // «Retomar la expedición donde la dejaste.» resumes an Expedición, which no door here opens.
            resume.ExceptWith(TextKeysSaying("Retomar la expedición"));

            int ambiguos = 0;
            foreach (var template in Npcs.Templates)
            {
                var options = Pick(template.Replies, template.ReplyTexts, keyring, key, yes, resume);
                if (options == null) continue;

                if (options.Ambiguous) ambiguos++;
                _byNpc[template.Id] = options;

                var resumes = new List<(long Reply, string Words)>();
                var doorWords = new List<string>();
                int upto = Math.Min(template.Replies?.Length ?? 0, template.ReplyTexts?.Length ?? 0);
                for (int i = 0; i < upto; i++)
                {
                    if (template.Replies![i] >= Expedition) continue;
                    long text = template.ReplyTexts![i];
                    bool isResume = resume.Contains(text);
                    if (!isResume && !key.Contains(text)) continue;
                    string words = WordsOf(text);
                    doorWords.Add(words);
                    if (isResume) resumes.Add((template.Replies[i], words));
                }
                if (resumes.Count > 1) _resumesByNpc[template.Id] = resumes;
                if (doorWords.Count > 0) _doorWordsByNpc[template.Id] = doorWords;
            }

            Console.WriteLine($"[Dungeon] {_byNpc.Count} guardians with a door to open" +
                              (ambiguos > 0
                                  ? $", {ambiguos} of them guard several and the first is taken."
                                  : "."));
        }

        /// <summary>
        /// A guardian's entry replies, picked out of everything it can say.
        /// </summary>
        /// <remarks>
        /// Its own method so it can be driven with a real guardian's declared replies in a test
        /// rather than only through a database. The rule it applies, in order: match the wording,
        /// prefer the older generation to the Expedición one for each kind of reply, and when a
        /// guardian still has more than one -- it guards several base dungeons -- take the lowest
        /// and say the pick was a guess.
        /// </remarks>
        public static Options? Pick(long[] replies, long[] texts,
                                    ISet<long> keyringTexts, ISet<long> keyTexts,
                                    ISet<long>? yesTexts = null, ISet<long>? resumeTexts = null)
        {
            long conManojo = 0, conLlave = 0;
            long resume = 0;
            int cuantosManojo = 0, cuantasLlaves = 0;
            var yes = new HashSet<long>();

            // The newer generation counts only for a kind the guardian has no older reply of: the
            // Coto del Venerable's and the Altar de la Despedida's keepers came with it and say
            // «Darle la llave y entrar.» only above the line.
            long newManojo = 0, newLlave = 0, newResume = 0;
            int newManojos = 0, newLlaves = 0;

            int upto = Math.Min(replies?.Length ?? 0, texts?.Length ?? 0);
            for (int i = 0; i < upto; i++)
            {
                long reply = replies![i];
                long text = texts![i];
                if (yesTexts != null && yesTexts.Contains(text)) yes.Add(reply);
                bool newer = reply >= Expedition;

                if (resumeTexts?.Contains(text) == true)
                {
                    if (newer) { if (newResume == 0 || reply < newResume) newResume = reply; }
                    else if (resume == 0 || reply < resume) resume = reply;
                }

                if (keyringTexts.Contains(text))
                {
                    if (newer) { newManojos++; if (newManojo == 0 || reply < newManojo) newManojo = reply; }
                    else { cuantosManojo++; if (conManojo == 0 || reply < conManojo) conManojo = reply; }
                }
                else if (keyTexts.Contains(text))
                {
                    if (newer) { newLlaves++; if (newLlave == 0 || reply < newLlave) newLlave = reply; }
                    else { cuantasLlaves++; if (conLlave == 0 || reply < conLlave) conLlave = reply; }
                }
            }

            if (conManojo == 0) { conManojo = newManojo; cuantosManojo = newManojos; }
            if (conLlave == 0) { conLlave = newLlave; cuantasLlaves = newLlaves; }
            if (resume == 0) resume = newResume;

            if (conManojo == 0 && conLlave == 0 && resume == 0) return null;

            return new Options
            {
                Keyring = conManojo,
                Key = conLlave,
                Resume = resume,
                Ambiguous = cuantosManojo > 1 || cuantasLlaves > 1,
                Yes = yes,
            };
        }

        /// <summary>
        /// The translation keys whose text starts with that sentence.
        /// </summary>
        /// <remarks>
        /// "Starts with" and not "equals" because the client's copies vary in punctuation -- some
        /// end in a full stop and some do not -- and 126 keys carry the keyring sentence between
        /// them. Matching loosely over the game's own table is still exact enough: no other reply
        /// in the game opens with those words.
        /// </remarks>
        /// <summary>The words of a translation key, or nothing.</summary>
        private static string WordsOf(long key)
        {
            try
            {
                using var connection = new SqliteConnection(DatabaseManager.WorldConnectionString);
                connection.Open();
                using var command = connection.CreateCommand();
                command.CommandText = "SELECT Text FROM Translations WHERE Key = $key;";
                command.Parameters.AddWithValue("$key", key.ToString());
                return command.ExecuteScalar() as string ?? "";
            }
            catch (Exception)
            {
                return "";
            }
        }

        private static HashSet<long> TextKeysSaying(string wording)
        {
            var found = new HashSet<long>();

            try
            {
                using var connection = new SqliteConnection(DatabaseManager.WorldConnectionString);
                connection.Open();

                using var command = connection.CreateCommand();
                command.CommandText = "SELECT Key FROM Translations WHERE Text LIKE $like;";
                command.Parameters.AddWithValue("$like", wording + "%");

                using var reader = command.ExecuteReader();
                while (reader.Read())
                {
                    if (long.TryParse(reader.GetString(0), out long key)) found.Add(key);
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[Dungeon] Could not read the door lines: {ex.Message}");
            }

            return found;
        }
    }
}
