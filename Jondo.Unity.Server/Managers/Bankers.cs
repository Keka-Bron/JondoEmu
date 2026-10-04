using System;
using System.Collections.Generic;
using Microsoft.Data.Sqlite;

namespace Jondo.Unity.Server.Managers
{
    /// <summary>
    /// Which NPCs are bankers, and which of their replies opens the bank.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The bank is not an interactive. It opens through the banker's dialogue, as the only bank
    /// visit in the captures shows ("Interactivos varios/entrar en banco bonta-abrir cofre
    /// gremio-usarlo-abrir cofre personal del banco"):
    ///
    ///   frame 72  C iov { f1: 3 (talk), f2: map 217059328, f3: -20000 }
    ///   frame 73  S ioc { f4: map, f5: -20000 }
    ///   frame 74  S ios { f1: 47810, f2 { f1: 63535, f3 { f1: 196 } }, f2 { f1: 63536 }, f3: "1397" }
    ///   frame 79  C ioy { f1: 63535 }                            "Quiero consultar mi cofre."
    ///
    /// -20000 is the Banquero bontariano, template 6394, on cell 289 of that map (the jss of frame
    /// 16). 47810 is his greeting and 63535 the reply that asks for the chest.
    /// </para>
    /// <para>
    /// <b>No reply id is written down here</b>, the way <see cref="DungeonDoor"/> writes none: they
    /// are per NPC. What is fixed is the WORDING the client draws, "Quiero consultar mi cofre.",
    /// which 22 translation keys carry across 8 templates -- the Bontarian and Brakmarian bankers,
    /// the three of Astrub (100, 520, 522), Moneo, Droopica and Yendong. Each banker's own reply is
    /// found by that text.
    /// </para>
    /// <para>
    /// <b>The inference, named.</b> Every one of them declares that wording more than once -- the
    /// Bontarian twice, 63534 and 63535, and the real server offered the SECOND. The data does not
    /// say what the other is for, so the rule taken is the one the capture shows: the last one the
    /// template declares.
    /// </para>
    /// <para>
    /// <b>Where the banks are, and where a banker stands.</b> The client marks ten banks on the
    /// world map (HintData with gfx 401, "Banco", read out of HintsDataRoot in the 3.6.10.11
    /// client's bundles; the emulator does not load it), and each has its interior at the same
    /// coordinates, recognisable by the guild chest every bank got (gfx 70671, elements 524188 to
    /// 524199, 524212 and 524415 in datos/interactive_elements.json):
    ///
    ///   Bonta            217059328 (+ 217060352, 217061376)   banker 6394, cell 289 -- CAPTURED
    ///   Astrub           192415750     520, 522 from the quest layer; 100 on 289 (inferred)
    ///   Brakmar          214695944, 214696968                  6374 on 329 (inferred)
    ///   Amakna village   99095051                              100 on 343 (inferred)
    ///   Pandala village  207618052                             5653 on 289 (inferred)
    ///   Pueblo de los ganaderos 84935175                       100 on 343 (inferred)
    ///   Sufokia          91753985                              100 on 344 (inferred)
    ///   Pueblo costero   86511105                              100 on 289 (inferred)
    ///   Burgo (Frigost)  54534165                              100 on 370 (inferred)
    ///   Picanesburgo     173937154                             3516 on 289 (inferred)
    ///
    /// Of all those maps only Bonta's first room is in a capture -- one jss, frame 16 of the bank
    /// visit -- so it is the only banker whose place is measured. The others are placed in
    /// content/npcs/spawns.json, not from another emulator's data but on a stated rule: the free
    /// walkable cell of the bank's first room nearest to Bonta's 289, facing 3, with the client's
    /// own banker for the place -- see that file's comment for why each one is who he is.
    /// </para>
    /// </remarks>
    public static class Bankers
    {
        /// <summary>What the client draws for the reply that opens the bank.</summary>
        public const string ConsultWording = "Quiero consultar mi cofre";

        public sealed class Banker
        {
            public int NpcId { get; init; }

            /// <summary>The reply that opens the bank.</summary>
            public long Consult { get; init; }

            /// <summary>How many replies of this template carry that wording.</summary>
            public int Candidates { get; init; }
        }

        private static readonly Dictionary<int, Banker> _byNpc = new Dictionary<int, Banker>();

        /// <summary>How many placed NPCs were recognised as bankers.</summary>
        public static int Count => _byNpc.Count;

        /// <summary>The banker this NPC is, or null.</summary>
        public static Banker? Of(int npcId)
            => npcId != 0 && _byNpc.TryGetValue(npcId, out var banker) ? banker : null;

        /// <summary>
        /// Works out, once, which of the templates in memory are bankers.
        /// </summary>
        /// <remarks>
        /// Runs after <see cref="Npcs.Initialize"/>, over the templates it read -- the ones of the
        /// NPCs standing somewhere -- as <see cref="DungeonDoor"/> does. A banker nobody placed
        /// needs no reply.
        /// </remarks>
        public static void Initialize()
        {
            _byNpc.Clear();

            var consult = TextKeysSaying(ConsultWording);
            if (consult.Count == 0)
            {
                Console.WriteLine("[Bank] No text says \"" + ConsultWording + "\": no banker will offer the bank.");
                return;
            }

            foreach (var template in Npcs.Templates)
            {
                var banker = Pick(template.Id, template.Replies, template.ReplyTexts, consult);
                if (banker != null) _byNpc[template.Id] = banker;
            }

            Console.WriteLine($"[Bank] {_byNpc.Count} banker(s) standing in the world offer the bank: " +
                              string.Join(", ", _byNpc.Keys) + ".");
        }

        /// <summary>
        /// One template's bank reply, out of everything it can say. Null when it has none.
        /// </summary>
        /// <remarks>
        /// Its own method so a test can drive it with a real banker's declared replies.
        /// </remarks>
        public static Banker? Pick(int npcId, long[] replies, long[] texts, ISet<long> consultTexts)
        {
            long chosen = 0;
            int found = 0;

            int upto = Math.Min(replies?.Length ?? 0, texts?.Length ?? 0);
            for (int i = 0; i < upto; i++)
            {
                if (!consultTexts.Contains(texts![i])) continue;
                found++;
                chosen = replies![i];   // the last one wins; see the remarks on the class
            }

            return chosen == 0 ? null : new Banker { NpcId = npcId, Consult = chosen, Candidates = found };
        }

        /// <summary>Registers a banker by hand, for tests that have no world loaded.</summary>
        internal static void Register(Banker banker) => _byNpc[banker.NpcId] = banker;

        /// <summary>Forgets one registered by hand.</summary>
        internal static void Forget(int npcId) => _byNpc.Remove(npcId);

        /// <summary>The translation keys whose text starts with that sentence.</summary>
        internal static HashSet<long> TextKeysSaying(string wording)
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
                Console.WriteLine($"[Bank] The bankers' texts could not be read: {ex.Message}");
            }
            return found;
        }
    }
}
