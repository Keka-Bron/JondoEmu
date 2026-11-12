using System;
using System.Collections.Generic;
using Microsoft.Data.Sqlite;

namespace Jondo.Unity.Server.Managers
{
    /// <summary>
    /// The two replies a dungeon guardian offers: the keyring, and the loose key.
    /// </summary>
    /// <remarks>
    /// The guardian was showing one option, "No.", because with no authored tree the server falls
    /// back to the last reply in the template. Mawy Ingals, who guards the Granero del Girasol
    /// Hambriento, declares nineteen of them, and two are the ones that matter:
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
    /// get the lowest, and a line in the log saying so.
    /// </remarks>
    public static class DungeonDoor
    {
        /// <summary>What the client draws for the reply that spends the keyring.</summary>
        public const string KeyringWording = "Utilizar el manojo de llaves";

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

        /// <summary>
        /// Reads the client's text table once and works out each guardian's two replies.
        /// </summary>
        /// <remarks>
        /// Done at boot rather than per conversation: it is one pass over the NPC templates already
        /// in memory plus one query for the handful of translation keys that carry those two
        /// sentences, and doing it while somebody is stood at a door would be a database round trip
        /// in the middle of a dialogue.
        /// </remarks>
        public static void Initialize()
        {
            _byNpc.Clear();

            var keyring = TextKeysSaying(KeyringWording);
            var key = TextKeysSaying(KeyWording);
            key.UnionWith(TextKeysSaying(UseKeyWording));
            key.UnionWith(TextKeysSaying(TakeKeyWording));
            // «Sí.» and «Sí, ...», not every sentence that opens with those letters («Sígueme»).
            var yes = TextKeysSaying(YesWording + ".");
            yes.UnionWith(TextKeysSaying(YesWording + ","));

            if (keyring.Count == 0 && key.Count == 0)
            {
                Console.WriteLine("[Dungeon] The door lines were not found: " +
                                  "the guardians will offer neither key nor keyring.");
                return;
            }

            int ambiguos = 0;
            foreach (var template in Npcs.Templates)
            {
                var options = Pick(template.Replies, template.ReplyTexts, keyring, key, yes);
                if (options == null) continue;

                if (options.Ambiguous) ambiguos++;
                _byNpc[template.Id] = options;
            }

            Console.WriteLine($"[Dungeon] {_byNpc.Count} guardians with a door to open" +
                              (ambiguos > 0
                                  ? $", {ambiguos} of them guard several and the first is taken."
                                  : "."));
        }

        /// <summary>
        /// One guardian's two replies, picked out of everything it can say. Null when it says neither.
        /// </summary>
        /// <remarks>
        /// Its own method so it can be driven with a real guardian's declared replies in a test
        /// rather than only through a database. The rule it applies, in order: skip the Expedición
        /// generation, match the wording, and when a guardian still has more than one -- it guards
        /// several base dungeons -- take the lowest and say the pick was a guess.
        /// </remarks>
        public static Options? Pick(long[] replies, long[] texts,
                                    ISet<long> keyringTexts, ISet<long> keyTexts,
                                    ISet<long>? yesTexts = null)
        {
            long conManojo = 0, conLlave = 0;
            int cuantosManojo = 0, cuantasLlaves = 0;
            var yes = new HashSet<long>();

            int upto = Math.Min(replies?.Length ?? 0, texts?.Length ?? 0);
            for (int i = 0; i < upto; i++)
            {
                long reply = replies![i];
                long text = texts![i];
                if (yesTexts != null && yesTexts.Contains(text)) yes.Add(reply);
                if (reply >= Expedition) continue;

                if (keyringTexts.Contains(text))
                {
                    cuantosManojo++;
                    if (conManojo == 0 || reply < conManojo) conManojo = reply;
                }
                else if (keyTexts.Contains(text))
                {
                    cuantasLlaves++;
                    if (conLlave == 0 || reply < conLlave) conLlave = reply;
                }
            }

            if (conManojo == 0 && conLlave == 0) return null;

            return new Options
            {
                Keyring = conManojo,
                Key = conLlave,
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
