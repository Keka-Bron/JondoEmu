using Jondo.Unity.Launcher;
using System;
using System.Collections.Generic;
using System.IO;
using System.Net.Sockets;
using System.Text;
using System.Threading.Tasks;
using Jondo.Unity.Protocol;

namespace Jondo.Unity.Server.Network
{
    /// <summary>
    /// Entry into the world, replayed from the 3.6.10.10 capture.
    ///
    /// It is not one burst. The real server sends a block, waits for the client to confirm, and
    /// carries on, three times over:
    ///
    ///   client kvw  ->  block 1: character, stats, quests, almanach...  (330 messages)
    ///   client lqc  ->  block 2: the four big catalogues                (4 messages)
    ///   client kqo  ->  block 3: the map                                (29 messages)
    ///
    /// Sending it all at once does not work: the client has not asked for the map yet and
    /// discards it.
    ///
    /// The bytes are the real ones, which is the only way to get this far without a schema for
    /// every message. What does get replaced is the identity: kva is rebuilt from the database so
    /// the client plays as its own character, and jru carries the map the character is standing
    /// on. Everything else still describes the account that was captured, and that is the next
    /// thing to unpick.
    /// </summary>
    public static class WorldEntry
    {
        /// <summary>
        /// The tag with which content/world/entry.json marks the SPELL bar.
        /// </summary>
        /// <remarks>
        /// There are two itg in the sequence and they are only told apart by what the manifest says: before,
        /// the payload was looked inside —f6 items, f9 spells— and that payload no longer travels. It is a
        /// string and not an enum because tools/decode_world_entry.py writes it, which is in another
        /// language; if the two stop matching, WorldEntryContentTests says so.
        /// </remarks>
        public const string SpellBarLabel = "ConnectionProtocol.BuildSpellBar";

        /// <summary>The three blocks, with the name content/world/entry.json gives them.</summary>
        public const string BlockAfterCharacter = "afterCharacter";
        public const string BlockAfterConfirm = "afterConfirm";
        public const string BlockMap = "map";

        /// <summary>
        /// Messages of the capture that are not replayed.
        ///
        /// The list is deliberately short. Everything else travels, even when it describes the
        /// account that was recorded: those messages are also what sets up the interface, and a
        /// client with somebody else's numbers in a panel is a smaller problem than a client with
        /// a panel that will not open.
        ///
        /// Three that used to be here are back on the wire, because the reason they were taken out
        /// did not survive being checked:
        ///
        ///   itg  really is the shortcut bar — two messages, f6 for SPELLS and f9 for items — and
        ///        dropping it is why the spell bar came up empty. The plan was to build it from
        ///        the database and that never happened.
        ///
        ///        That «f6 for items and f9 for spells» written here was false and kept
        ///        contradicting HoldsSpells, a stone's throw further down, which did the opposite and
        ///        was right. Measured over the two frames: the 436-byte one carries 44 slots with f6 and
        ///        all 44 values are real SpellTemplates ids, from 350 to 24,017; the
        ///        1,208-byte one uses f9 and its values reach 507,645,866, which are item uids.
        ///   ife  was labelled the friends list. It is not: the contacts are in kqg. It is the
        ///        alliances, by name and by tag. It came back on the wire for a while and has gone
        ///        out again — not for the reason it was taken out the first time, but because
        ///        those alliances are real and one of them is the one the capture's own account
        ///        belongs to.
        ///   ivi  was labelled the inventory. It is not: 9694 pairs of id and value, ids from 44
        ///        to 34352 and values into the hundreds of millions. It looks like the account's
        ///        statistics counters.
        /// </summary>
        private static readonly HashSet<string> NotReplayed = new HashSet<string>
        {
            // Built from our own database instead. This one is not dropped, it is replaced: the
            // captured kub carries a level 154 character's sheet.
            Op.Kub,

            // What names real people. This is not only somebody else's data: the emulator is meant
            // to be shared, and the friends panel was showing five real accounts with their
            // nicknames, levels, guilds and alliances. Whoever ran it would be looking at the
            // contact list of whoever recorded the capture.
            //
            //   kqg  the contact list
            //   jhe  the guild
            //   jhh  the guild again: the date it was founded, its level, how many are in it
            //   jhk  the guild's name, spelled out
            //   hol  the spouse and the guild of the character
            //   jgu  the spouse again, with its look
            //   ihb  the fourteen saved outfits, each one carrying the looks of that account
            //   koj  twenty Ankama accounts, each with its id, its nickname and its tag:
            //        f2 { f2: account id, f4 { f1: nickname, f2: tag }, f5: 3 }
            //   ife  the alliances, by name and by tag
            //   jjs  a player's stall standing on the map, with the account behind it:
            //        f5 { f2 { f8 { f1: nickname, f2: tag } } }, harmoo#4742
            //   jaa  the same thing in its own message, Sacrogrito69#4234
            //
            // The last four went unnoticed for a while because the check that was supposed to
            // catch them looked for names it had to be told in advance, and nobody had told it
            // about these. tools/leak.py no longer works that way: it sweeps every readable string
            // out of everything the server sends and groups it by message, so a name shows up
            // whether or not anyone knew to look for it.
            //
            // jhh and jhk were travelling until now, and the client's own Player.log shows what
            // that cost: a NullReferenceException on each of them, out of the same handler. It
            // makes sense — they describe a guild whose own message (jhe) is not being sent, so
            // there is nothing for them to attach to. Leaving them out takes two of the client's
            // six crashes away and one more real name off the wire.
            //
            // Nothing goes out in their place for now, which is what a fresh account looks like
            // anyway. Building them from our own database is the next step; there is nothing to
            // build them from yet, because no account here has friends, a guild or a spouse.
            //
            // tools/leak.py checks that no real name reaches the wire. Run it after touching this.
            Op.Kqg, Op.Jhe, Op.Jhh, Op.Jhk, Op.Hol, Op.Jgu, Op.Ihb, Op.Koj, Op.Ife, Op.Jjs, Op.Jaa,

            // The captured account's adornments. They are not names, but they are its own all the same, and the
            // emulator already sends those of the character logging in:
            //
            //   hhy  the titles and ornaments THAT account has: 62 and 28. They arrived on choosing
            //        a character and then the emulator sent its own —the 539 and the 167— on entering
            //        the map, so the client received two different lists and the first was
            //        someone else's. WardrobeHandler.SendOwnedAsync sends it.
            //   lyt  the wardrobe's stored outfits. It stays in the list, even though in these
            //        blocks there are none: it comes out 8 times in the captures and the day another
            //        session is replicated it had better be out already.
            //
            // CAREFUL WITH THIS PAIR. Block 1 brings an «lty», which is NOT this «lyt»: they are two different
            // opcodes —lty comes out 7 times in the captures and lyt 8— and for a while the comment
            // here described the lty believing it was the lyt, so this list entry
            // filtered absolutely nothing and nobody noticed. The real lty is five empty
            // slots (all -1 and 5) with the account's creation date, so it carries nothing of
            // anybody and still travels; but the lesson is that a three-letter opcode gets confused
            // with its anagram without any error firing.
            Op.Hhy, Op.Lyt,

            // The captured account's quest journal: 261 frames in block 1 and 4 more in
            // the map one, each with a quest, its step and its objectives. It is someone else's on two
            // counts: they are the quests that account was carrying, and on top of that they contradict what the
            // server believes, because since there is a quest engine the client would receive 261
            // quests its character does not have and none of those it does.
            //
            // It is not an assumption about what idu is: in the 401 captures there are 448 of its frames and all
            // 448 name a step that really belongs to the quest they name, with 1,479
            // objectives that really belong to that step.
            //
            // In its place goes ours, from the base: Managers.Quests.SendJournalAsync.
            Op.Idu,

            // And the little box of followed quests of that same account, which is something other than the journal
            // and which slipped in even though the idu and the idr were out. A single frame in block
            // 1, with quests 1869 and 2406 —«El daño de Búril» and «Cuando el despertar no es más
            // que un sueño»— and their objectives.
            //
            // What made it harmful is that it REPLACES the list, it does not extend it. The character's
            // journal came out fine —«Diario enviado: 1 en curso» in the log— and even so the box
            // showed two foreign quests and none of its own, because this frame arrived later and
            // had the last word. Right after starting a quest it was seen, because the
            // live ief adds it; on logging in again, it disappeared.
            //
            // Nothing goes in its place for now: a new character follows none, and the client
            // puts in the box the ones it has started.
            Op.Iel,

            // And that account's counters: 9,694 id and value pairs, with ids from 44 to 34,352 and
            // values in the hundreds of millions. It is what the client draws in the statistics panel,
            // and they are that account's: games played, monsters killed, kamas earned. 87,878 bytes,
            // 88 % of everything that was still being copied as is. A new account has none.
            Op.Ivi,

            // And THE SAME JOURNAL AGAIN, whole and in a single frame. Removing only the idu changed
            // nothing of what the player sees, because the block brings both things: 261 loose idu
            // frames AND this idr with those same 261 inside, plus 548 quests considered finished.
            //
            //   f1 (repeated)  a quest in progress, with the shape of the idu's body
            //   f3 (repeated)  { f1: 1, f2: quest id }, a finished one
            //   f4             one, empty
            //
            // The 261 ids of f1 are real quests, all 261, and the 548 of f3 too: they are
            // exactly the two numbers the client writes on its tabs. With this coming in,
            // the player saw all those of Incarnam and Astrub as done, no NPC with the green
            // mark above it and none willing to give anything, because for the client he already had them.
            //
            // In its place goes ours, with the same shape, from CharacterQuests.
            Op.Idr,

            // That account's achievements: 954 entries, and the 954 ids they carry are real achievements.
            // It is the reason the character came in with all of the captured player's achievements.
            // In their place goes the character's own list: Managers.Achievements.SendListAsync.
            Op.Mft,

            // And that account's emotes: 47 of them, where a new character has four (1, 97, 98
            // and 127, in the three captures that create one). In their place goes the
            // character's own list: Managers.Emotes.SendListAsync.
            Op.Khn,
        };

        /// <summary>Character id the capture belongs to. Learned from the blocks, never written down.</summary>
        private static long _capturedCharacterId;

        /// <summary>
        /// Every characteristic id the real kub declares, in the order it declares them.
        ///
        /// It matters that all of them travel, even at zero. Sending only the handful we know
        /// leaves the rest undeclared, and the client fills those in by itself: that is where the
        /// -100% damage and the 50% resistances came from. The list is structure, taken from the
        /// capture; the values are ours.
        /// </summary>
        public static IReadOnlyList<int> CharacteristicIds => _characteristicIds;
        private static List<int> _characteristicIds = new List<int>();

        /// <summary>
        /// Which field of the entry holds the value, per characteristic id.
        ///
        /// Three ids travel in f2 and two in f5 while the rest use f4, and there is no rule to it
        /// that we can see, so it is read rather than guessed. Sending one of them in the wrong
        /// container makes the client throw a NullReferenceException and lose the whole sheet;
        /// its own Player.log names the message and the entry type.
        /// </summary>
        private static readonly Dictionary<int, int> _containers = new Dictionary<int, int>();

        public static int ContainerOf(int characteristicId)
            => _containers.TryGetValue(characteristicId, out int field) ? field : 4;

        private static void LearnCharacteristicIds()
        {
            _characteristicIds = new List<int>();
            _containers.Clear();
            foreach (string block in new[] { BlockMap, BlockAfterCharacter })
            {
                foreach (byte[] frame in WorldEntryContent.Frames(block))
                {
                    byte[]? kub = ConnectionProtocol.ReadPayload(frame, Op.Kub);
                    if (kub == null || kub.Length == 0) continue;

                    var body = Field(ProtoMessage.Parse(kub), 2);
                    if (body == null) continue;

                    foreach (var f in ProtoMessage.Parse(body).Fields)
                    {
                        if (f.FieldNumber != 11 || f.WireType != 2) continue;

                        // An entry with no f1 is characteristic 0, which is life: proto3 leaves
                        // the field out when the value is zero, and zero is its id. Reading only
                        // the entries that declare an id lost exactly that one, and with it the
                        // life bar, which is why it sat at 0/0.
                        int id = 0, container = 4;
                        foreach (var g in ProtoMessage.Parse(f.BytesValue).Fields)
                        {
                            if (g.FieldNumber == 1 && g.WireType == 0) id = (int)g.VarIntValue;
                            else if (g.WireType == 2) container = g.FieldNumber;
                        }
                        if (!_characteristicIds.Contains(id)) _characteristicIds.Add(id);
                        _containers[id] = container;
                    }

                    if (_characteristicIds.Count > 0)
                    {
                        var odd = new List<string>();
                        foreach (var pair in _containers)
                        {
                            if (pair.Value != 4) odd.Add($"{pair.Key}->f{pair.Value}");
                        }
                        odd.Sort();
                        Console.WriteLine($"[World] {_characteristicIds.Count} characteristics declared by " +
                                          $"the real kub; not in f4: {string.Join(", ", odd)}.");
                        return;
                    }
                }
            }

            // And if the blocks bring no kub —which is what happens: none of the three
            // carries one—, the list comes from the measured file.
            AprenderDelFichero();
        }

        /// <summary>
        /// The list of characteristics and their slot, taken from the captures.
        ///
        /// The three data blocks in datos/ do NOT contain a single kub, so the above was always left
        /// with the empty list and the sheet fell back to the emergency one: six characteristics
        /// plus the primary ones, twenty-five in total instead of a hundred and twenty. The character appeared
        /// without critical, without power, without range, without tackle, without flee, without dodges, without
        /// elemental damage and without resistances, because those entries simply did not travel.
        ///
        /// It was not noticed because the good panel is drawn by replaying the blocks on entering the
        /// world, and this builder was hardly used. When it started being sent also at the end of a
        /// fight, it went on to overwrite the good sheet.
        ///
        /// The file is generated by tools/extraer_caracteristicas_kub.py from the 672 real kub there are
        /// in the captures. Each one's slot matters: three go in f2 —29, 47 and 96—, two in
        /// f5 —the action and movement points— and the remaining 115 in f4. Sending one
        /// in the wrong slot makes the client blow up and lose the whole sheet.
        /// </summary>
        private static void AprenderDelFichero()
        {
            try
            {
                // Through Paths and not relative: relative only works if the working directory
                // is the root, which is what the launcher leaves. Started any other way
                // —from the IDE, from a service, from another folder— the file did not show up,
                // the sheet stayed at 25 entries out of 120, and with it went the critical, the
                // power, the range and all the resistances. Without a single error message.
                string ruta = Paths.CharacteristicFieldsJson;
                if (!System.IO.File.Exists(ruta))
                {
                    Console.WriteLine("[World] No está datos/caracteristicas_kub.json: la ficha de " +
                                      "características saldrá corta.");
                    return;
                }

                using var doc = System.Text.Json.JsonDocument.Parse(System.IO.File.ReadAllText(ruta));
                foreach (var entrada in doc.RootElement.EnumerateArray())
                {
                    int id = entrada.GetProperty("id").GetInt32();
                    int hueco = entrada.GetProperty("hueco").GetInt32();
                    if (!_characteristicIds.Contains(id)) _characteristicIds.Add(id);
                    _containers[id] = hueco;
                }

                Console.WriteLine($"[World] {_characteristicIds.Count} características leídas de " +
                                  $"datos/caracteristicas_kub.json.");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[World] No se pudo leer la lista de características: {ex.Message}");
            }
        }

        /// <summary>
        /// The character's professions, with whatever progress they have.
        ///
        /// The LIST of professions is taken from the capture: which professions exist and in what order
        /// the client wants them are game data. What is not inherited is the progress, which belonged
        /// to whoever recorded —he had a dozen at the maximum—.
        ///
        /// Before, this sent all of them at level 1 and that was it, and it made sense while there was no
        /// profession experience anywhere. Now there is: it is stored in CharacterJobs and
        /// loaded on choosing a character, so the real one is put here. Without this, you fished
        /// until reaching Fisher 3 and on logging in again the twenty came out at level 1 once more.
        ///
        ///   irq: f1 (repeated) { f1: profession, f2: next level, f3: level, f4: floor, f5: total }
        /// </summary>
        private static byte[] SendJobs(byte[] frame)
        {
            var jobs = Pb.New();
            byte[]? payload = ConnectionProtocol.ReadPayload(frame, Op.Irq);

            // Empty, not null, is the case to catch. ReadPayload returns a zero-byte
            // array when the message brings no body, and it CANNOT return null because Rebuilt uses
            // that same `!= null` to know which opcode each frame belongs to. When the world
            // entry started being read from the manifest and this frame was left without a body, the loop below
            // did not go round once, count stayed at zero —which also silenced the console
            // line— and the player came in without a single profession, with his experience stored intact in
            // CharacterJobs and nobody to show it to.
            if (payload == null || payload.Length == 0)
            {
                Console.WriteLine("[World] El irq viene sin cuerpo: no se puede saber qué oficios " +
                                  "existen y el personaje entra sin ninguno.");
                return jobs.Build();
            }

            var progreso = SessionContext.State.Jobs;
            int count = 0;
            int conNivel = 0;

            foreach (var f in ProtoMessage.Parse(payload).Fields)
            {
                if (f.FieldNumber != 1 || f.WireType != 2) continue;

                foreach (var g in ProtoMessage.Parse(f.BytesValue).Fields)
                {
                    if (g.FieldNumber != 1 || g.WireType != 0) continue;

                    int jobId = (int)g.VarIntValue;
                    long experiencia = progreso.TryGetValue(jobId, out var suyo) ? suyo.Experience : 0;
                    int nivel = Managers.JobExperience.LevelOf(experiencia);
                    if (nivel > 1 || experiencia > 0) conNivel++;

                    jobs.Msg(1, Pb.New()
                        .Var(1, jobId)
                        .VarIfNotZero(2, Managers.JobExperience.Next(nivel))
                        .Var(3, nivel)
                        .VarIfNotZero(4, Managers.JobExperience.Floor(nivel))
                        .VarIfNotZero(5, experiencia));
                    count++;
                    break;
                }
            }

            // Always, also with zero: a zero in the log is what would have given away the day
            // they stopped coming out, instead of printing nothing and seeming that nothing had happened.
            Console.WriteLine($"[World] {count} oficios enviados, {conNivel} con progreso.");
            return jobs.Build();
        }

        /// <summary>
        /// The messages of the capture we rebuild from the database instead of replaying, and what
        /// goes out in their place. Null means "replay it as it is".
        ///
        /// This is the list that shrinks as the emulator stops being a recording. Each one of them
        /// was showing the player the account that was captured:
        ///
        ///   kva  the character it is playing: name, level, breed and look
        ///   irq  the jobs, which arrived maxed out
        ///   hms  the spells it has
        ///   ivx  the inventory
        ///   itg  both bars: the spell one is rebuilt, the item one goes out empty
        ///
        /// Both bars go now, not only the spell one. The item one pointed at 72 uids of the
        /// captured account, and since the inventory comes from the database those items do not
        /// exist: the client was left with a bar full of slots it cannot resolve.
        /// </summary>
        /// <summary>Which information message an lqn carries, or zero.</summary>
        private static int MessageOf(byte[] lqn)
        {
            foreach (var field in ProtoMessage.Parse(lqn).Fields)
            {
                if (field.FieldNumber == 2 && field.WireType == 0) return (int)field.VarIntValue;
            }
            return 0;
        }

        private static byte[]? Rebuilt(byte[] frame, string built, DatabaseManager.DbCharacter character)
        {
            if (ConnectionProtocol.ReadPayload(frame, Op.Kva) != null)
            {
                return ConnectionProtocol.Push(Op.Kva,
                    ConnectionProtocol.BuildCharacterSelectedSuccess(character));
            }

            if (ConnectionProtocol.ReadPayload(frame, Op.Irq) != null)
            {
                return ConnectionProtocol.Push(Op.Irq, SendJobs(frame));
            }

            // The Koliseo ladder: the captured one was the capturer's, unplaced in the season of
            // 14/03/2025. This character's own leagues, in this server's season.
            if (ConnectionProtocol.ReadPayload(frame, Op.Lty) != null)
            {
                return ConnectionProtocol.Push(Op.Lty, Handlers.KoliseoHandler.BuildRanks(character.Id, character.Level));
            }

            // The artisan settings of every job. The captured ones were the capturer's -- a
            // minimum level of 150 for the miner, 69 for the farmer -- handed to everybody.
            if (ConnectionProtocol.ReadPayload(frame, Op.Isd) != null)
            {
                return ConnectionProtocol.Push(Op.Isd, Handlers.ArtisanHandler.BuildSettings(SessionContext.State));
            }

            if (ConnectionProtocol.ReadPayload(frame, Op.Hms) != null)
            {
                return ConnectionProtocol.Push(Op.Hms,
                    ConnectionProtocol.BuildSpellList(character.Breed, character.Level,
                        SessionContext.Current.AccountId));
            }

            if (ConnectionProtocol.ReadPayload(frame, Op.Ivx) != null)
            {
                return ConnectionProtocol.Push(Op.Ivx, ConnectionProtocol.BuildInventory());
            }

            // The last connection notice. The recorded block brings that of whoever captured —9
            // August at 18:53— and that is someone else's. It is swapped for this character's, with his
            // date and the address he logged in from last time, read from the base before
            // overwriting them. The block's other lqn are let through as is.
            byte[]? lqn = ConnectionProtocol.ReadPayload(frame, Op.Lqn);
            if (lqn != null && MessageOf(lqn) == ConnectionProtocol.LastConnectionMessage)
            {
                var anterior = SessionContext.State.PreviousVisit;
                if (anterior == null) return Array.Empty<byte>();

                return ConnectionProtocol.Push(Op.Lqn,
                    ConnectionProtocol.BuildLastConnection(anterior.When, anterior.Ip));
            }

            byte[]? itg = ConnectionProtocol.ReadPayload(frame, Op.Itg);
            if (itg != null)
            {
                // Which of the two bars it is, the manifest says, not the body. Before, the payload was
                // looked inside —f6 items, f9 spells— and that stopped working as soon as the
                // payload stopped travelling: both would have gone out as the item one, and the spell
                // one empty, which is exactly the bug already fixed once another way.
                if (built == SpellBarLabel)
                {
                    return ConnectionProtocol.Push(Op.Itg,
                        ConnectionProtocol.BuildSpellBar(character.Breed, character.Level));
                }

                // The other bar, the item one. It went as is and pointed at 72 uids of the
                // captured account: items that do not exist in this inventory. It goes out empty, which is what
                // a character who has not yet put anything in it has.
                return ConnectionProtocol.Push(Op.Itg, Array.Empty<byte>());
            }

            return null;
        }


        /// <summary>Is this one of the messages that carry other players' data?</summary>
        private static bool ShouldSkip(byte[] message)
        {
            foreach (string opcode in NotReplayed)
            {
                if (ConnectionProtocol.ReadPayload(message, opcode) != null) return true;
            }
            return false;
        }

        /// <summary>Name that goes with it, read from the same place.</summary>
        private static string _capturedName = "";

        /// <summary>Reads the three blocks off disk. Missing files are reported, not thrown.</summary>
        public static void Initialize()
        {
            // From content/world/entry.json and not from the three .bin. They are the same bytes —
            // WorldEntryContentTests proves it frame by frame— but written field by field, so that what
            // goes out on the wire can be read and compared in a diff. That is how the quest journal
            // and the achievements of the captured account were found late: there was no way to see them.
            WorldEntryContent.Load(Paths.ContentFile(WorldEntryContent.AuthoredFile), Console.WriteLine);

            if (WorldEntryContent.Ready)
            {
                Console.WriteLine($"[World] Entrada al mundo: {WorldEntryContent.Count(BlockAfterCharacter)} + " +
                                  $"{WorldEntryContent.Count(BlockAfterConfirm)} + " +
                                  $"{WorldEntryContent.Count(BlockMap)} tramas, sin abrir un solo .bin.");
            }

            LearnCapturedIdentity();
            LearnSignatures();
            LearnCharacteristicIds();
        }

        /// <summary>
        /// Works out which character the capture belongs to by reading its kva, the message that
        /// carries the name and the id together.
        ///
        /// It is read rather than written into the source on purpose: the name belongs to a real
        /// player and has no business being in the code, and this way regenerating the blocks from
        /// a different capture needs no changes here.
        /// </summary>
        private static void LearnCapturedIdentity()
        {
            _capturedCharacterId = 0;
            _capturedName = "";

            foreach (byte[] frame in WorldEntryContent.Frames(BlockAfterCharacter))
            {
                byte[]? kva = ConnectionProtocol.ReadPayload(frame, Op.Kva);
                if (kva == null || kva.Length == 0) continue;

                // kva: f1 { f1 { f1 { f2: name, ... }, f2: id } }
                var outer = Field(ProtoMessage.Parse(kva), 1);
                if (outer == null) return;
                var inner = Field(ProtoMessage.Parse(outer), 1);
                if (inner == null) return;

                var innerMsg = ProtoMessage.Parse(inner);
                foreach (var f in innerMsg.Fields)
                {
                    if (f.FieldNumber == 2 && f.WireType == 0) _capturedCharacterId = f.VarIntValue;
                }

                var details = Field(innerMsg, 1);
                if (details != null)
                {
                    foreach (var f in ProtoMessage.Parse(details).Fields)
                    {
                        if (f.FieldNumber == 2 && f.WireType == 2)
                            _capturedName = Encoding.UTF8.GetString(f.BytesValue);
                    }
                }

                Console.WriteLine($"[World] The blocks belong to character {_capturedCharacterId} " +
                                  $"({_capturedName.Length} characters in the name). Its identity is " +
                                  "swapped for the one playing.");
                return;
            }

            // And this is no longer a warning: it is the norm since the manifest carries no body in the
            // frames the server rebuilds, and the kva is one of them. The identity is not needed
            // because the kva is built whole from the base —Rebuilt replaces it completely— and
            // because nothing of the recorded character is left to replace: WorldEntryContentTests
            // checks it frame by frame. The warning said «el cliente se negará a entrar al mundo», which
            // was false and came out on every start.
            Console.WriteLine("[World] El manifiesto no trae el kva de la captura, que es lo " +
                              "esperado: el del personaje se construye desde la base de datos.");
        }

        /// <summary>Every submessage under that field number, not just the first one.</summary>
        private static IEnumerable<byte[]> Repeated(byte[] message, int number)
        {
            foreach (var f in ProtoMessage.Parse(message).Fields)
            {
                if (f.FieldNumber == number && f.WireType == 2) yield return f.BytesValue;
            }
        }

        private static byte[]? Field(ProtoMessage message, int number)
        {
            foreach (var f in message.Fields)
            {
                if (f.FieldNumber == number && f.WireType == 2) return f.BytesValue;
            }
            return null;
        }

        /// <summary>What has to change so the whole block talks about the character playing.</summary>
        private static CaptureRewriter.Identity IdentityFor(DatabaseManager.DbCharacter character)
        {
            var identity = new CaptureRewriter.Identity();
            if (_capturedCharacterId != 0) identity.Number(_capturedCharacterId, character.Id);
            if (!string.IsNullOrEmpty(_capturedName)) identity.Text(_capturedName, character.Name);
            foreach (string signature in _signatures) identity.Text(signature, character.Name);
            return identity;
        }

        /// <summary>
        /// The names that sign the forgemaged items of the inventory, which is what the client
        /// shows as "Modificado por" in an item's tooltip.
        ///
        ///   ivx: f3 (repeated) { f5 { f2 { f1: who did it } } }
        ///
        /// Most of them are the captured character itself and got swapped along with the rest of
        /// its identity, which is why the tooltip already read the right name. But not all of the
        /// work was its own: somebody else's name was in there too, going out on the wire on every
        /// entry into the world, and nothing was replacing it because the swap only knew about one
        /// name. They are read off the block instead of being written down here, the same as the
        /// character's own, and every one of them becomes the name of whoever is playing.
        /// </summary>
        private static readonly List<string> _signatures = new List<string>();

        private static void LearnSignatures()
        {
            _signatures.Clear();
            foreach (string block in new[] { BlockAfterCharacter, BlockAfterConfirm, BlockMap })
            {
                foreach (byte[] frame in WorldEntryContent.Frames(block))
                {
                    byte[]? ivx = ConnectionProtocol.ReadPayload(frame, Op.Ivx);
                    if (ivx == null || ivx.Length == 0) continue;

                    // The same message is the inventory, and reading it is what puts the equipment
                    // on the character sheet.
                    Managers.Equipment.LearnFrom(ivx);

                    // Every one of the three levels repeats, and taking the first of each finds
                    // nothing at all: the five signatures of the captured character sit in later
                    // entries than the first.
                    foreach (var item in Repeated(ivx, 3))
                    foreach (var forge in Repeated(item, 5))
                    foreach (var by in Repeated(forge, 2))
                    foreach (var f in ProtoMessage.Parse(by).Fields)
                    {
                        if (f.FieldNumber != 1 || f.WireType != 2) continue;

                        string name = Encoding.UTF8.GetString(f.BytesValue);
                        if (name.Length == 0 || name == _capturedName) continue;
                        if (!_signatures.Contains(name)) _signatures.Add(name);
                    }
                }
            }

            if (_signatures.Count > 0)
            {
                Console.WriteLine($"[World] {_signatures.Count} other name(s) signing forgemaged " +
                                  "items; they will be swapped for the player's own.");
            }
        }

        private static byte[]? Read(string path, string what)
        {
            try
            {
                if (!File.Exists(path))
                {
                    Console.ForegroundColor = ConsoleColor.Yellow;
                    Console.WriteLine($"[World] Missing the block for {what}: {Path.GetFileName(path)}. " +
                                      "Generate it with extraer_world.py.");
                    Console.ResetColor();
                    return null;
                }

                byte[] data = File.ReadAllBytes(path);
                Console.WriteLine($"[World] Block for {what}: {data.Length} bytes.");
                return data;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[World] Could not read {Path.GetFileName(path)}: {ex.Message}");
                return null;
            }
        }

        /// <summary>
        /// Splits a block into the messages it holds, WITHOUT their length prefix.
        ///
        /// The prefix is left out on purpose: the rewriter would take it for part of the message,
        /// and it has to be recomputed anyway because swapping the identity changes the size.
        /// </summary>
        private static IEnumerable<byte[]> Frames(byte[] block)
        {
            int p = 0;
            while (p < block.Length)
            {
                int length = 0, shift = 0, start = p;
                while (p < block.Length)
                {
                    byte b = block[p++];
                    length |= (b & 0x7F) << shift;
                    if ((b & 0x80) == 0) break;
                    shift += 7;
                }

                if (length <= 0 || p + length > block.Length) yield break;

                var message = new byte[length];
                Array.Copy(block, p, message, 0, length);
                p += length;
                yield return message;
            }
        }

        /// <summary>
        /// Block 1. The kva of the capture is swapped for one built from the database: it is the
        /// message that tells the client which character it is playing, and with the captured one
        /// it would play as somebody else's.
        /// </summary>
        public static async Task<int> SendAfterCharacterAsync(NetworkStream stream, DatabaseManager.DbCharacter character)
        {
            var identity = IdentityFor(character);
            int sent = 0, rewritten = 0;

            int skipped = 0;
            foreach (var row in WorldEntryContent.Rows(BlockAfterCharacter))
            {
                byte[] frame = row.Frame;
                if (ShouldSkip(frame)) { skipped++; continue; }

                // Rebuilt whole, not rewritten. Rewriting only swaps the id and the name, and kva
                // also carries the level, the breed and the look: leaving the captured ones
                // through is why the client showed level 154, another breed and somebody else's
                // look.
                byte[]? rehechoAqui = Rebuilt(row.Frame, row.Built, character);

                // An empty array means "this message is not sent": the last connection
                // notice uses it when the character logs in for the first time and there is no previous one to
                // tell. Sending it empty would be a zero-length frame.
                if (rehechoAqui != null && rehechoAqui.Length == 0) { skipped++; continue; }
                byte[] toSend;

                if (rehechoAqui != null)
                {
                    toSend = rehechoAqui;
                    rewritten++;
                }
                else
                {
                    toSend = CaptureRewriter.Rewrite(frame, identity);
                    if (!ReferenceEquals(toSend, frame)) rewritten++;
                }

                await EnviarAsync(stream, toSend);
                sent++;
            }
            if (skipped > 0) Console.WriteLine($"[World] {skipped} messages left out: they belong to another account.");

            // The jobs this character keeps in the public artisans' list, so that the window's
            // box says so after a relog and the next toggle takes them off rather than on.
            var listed = SessionContext.State.CrafterSettings.Where(s => s.Value.Listed).Select(s => (s.Key, true)).ToList();
            if (listed.Count > 0)
                await EnviarAsync(stream, ConnectionProtocol.Push(Op.Iro, Handlers.ArtisanHandler.BuildListing(listed)));

            // The guild frames the captured jhe/jhh/jhk were dropped for: built from our own
            // database now, so a character who has a guild sees it. Nothing goes out for one who
            // has none, which is what the discard already did. The captured ranks are a fixed
            // default template (jco), reused here.
            // In the capture's order, "jco jhe jhh": the ranks, then belonging (jhe) -- not the
            // jgw of joining, which printed "acabas de unirte al gremio" at every login.
            var guild = Managers.GuildStore.GuildOf(character.Id);
            if (guild != null)
            {
                int rank = Managers.GuildStore.RankOf(character.Id);
                var members = Managers.GuildStore.Members(guild.Id);
                await EnviarAsync(stream, ConnectionProtocol.Push(Op.Jco, GuildProtocol.BuildDefaultRanks()));
                await EnviarAsync(stream, ConnectionProtocol.Push(Op.Jhe,
                    GuildProtocol.BuildMembership(guild, rank, Managers.GuildStore.ContributedBy(character.Id))));
                await EnviarAsync(stream, ConnectionProtocol.Push(Op.Jhh,
                    GuildProtocol.BuildGuildInfo(guild, members.Count)));
                Console.WriteLine($"[World] Guild sent for {character.Name}: {guild.Name} ({members.Count} members).");
            }

            // Back on a dream's map: the dream again, or out of it when there is none to go back to.
            await Handlers.DreamHandler.OnWorldEntryAsync(stream);

            // What the account sold in the marketplaces, and what came back unsold, for the sales
            // history window: its "last connection" box is the part since the last logout.
            byte[]? sales = Handlers.MarketplaceHandler.SalesHistoryAtEntry(character.Id, SessionContext.Current.AccountId);
            if (sales != null) await EnviarAsync(stream, ConnectionProtocol.Push(Op.Las, sales));

            // And in place of the characteristics of the capture, the ones of this character.
            await EnviarAsync(stream, ConnectionProtocol.Push(Op.Kub, ConnectionProtocol.BuildCharacteristics()));
            Console.WriteLine($"[World] Characteristics sent for {character.Name}: level " +
                              $"{Jondo.Unity.Server.Network.SessionContext.State.CharacterLevel}, {Jondo.Unity.Server.Network.SessionContext.State.Kamas} kamas.");

            Console.WriteLine($"[World] Block 1 sent: {sent} messages, {rewritten} rewritten for " +
                              $"{character.Name}.");
            return sent;
        }

        /// <summary>
        /// Writes a frame and records it in the traffic log, the same as the rest of the emulator.
        /// Sending without logging leaves no way to tell "it never went out" from "it went out and
        /// the client ignored it", which is exactly the question worth answering here.
        /// </summary>
        private static async Task EnviarAsync(NetworkStream stream, byte[] message)
        {
            byte[] prefix = CaptureRewriter.VarInt(message.Length);
            var frame = new byte[prefix.Length + message.Length];
            Array.Copy(prefix, 0, frame, 0, prefix.Length);
            Array.Copy(message, 0, frame, prefix.Length, message.Length);

            await Jondo.Protocol.NetworkMessage.WriteRawFrameAsync(stream, frame);
            GameServerProxy.LogTraffic("S->C", frame, frame.Length);
        }

        public static async Task<int> SendAfterConfirmAsync(NetworkStream stream, DatabaseManager.DbCharacter character)
        {
            return await SendRewrittenAsync(stream, BlockAfterConfirm, "Block 2", IdentityFor(character));
        }

        /// <summary>
        /// Block 3, the map. jru carries the map id in field 2, and it is replaced with the one
        /// the character is standing on: otherwise everyone would land on the map of the capture.
        /// </summary>
        /// <param name="fightToRejoin">
        /// Set when the character is going straight back into a fight. The block then takes the
        /// shape of the two reconnection captures: the kmp says "fight" (f1 = 1), the jru is the
        /// arena, and behind the lqu go "{0} acaba de volver a conectarse al combate" and the
        /// lva; the ktz and the iom of a roleplay entry are not sent. Everything else is the
        /// same block.
        /// </param>
        public static async Task<int> SendMapAsync(NetworkStream stream, DatabaseManager.DbCharacter character, long mapId,
                                                   Jondo.Unity.World.Fights.FightInstance? fightToRejoin = null)
        {
            var identity = IdentityFor(character);
            int sent = 0;

            foreach (var row in WorldEntryContent.Rows(BlockMap))
            {
                byte[] frame = row.Frame;
                if (ShouldSkip(frame)) continue;

                byte[]? rehecho = Rebuilt(row.Frame, row.Built, character);
                if (rehecho != null && rehecho.Length == 0) continue;
                byte[] toSend = rehecho ?? CaptureRewriter.Rewrite(frame, identity);

                // jru says which map to load. Replacing it with the character's own map is only
                // safe once we build the actor list ourselves: the actors travel in this same
                // block and still describe the captured map, so changing just the id leaves the
                // client loading one map and being told about another, and it draws nobody.
                if (mapId > 0 && ConnectionProtocol.ReadPayload(frame, Op.Jru) != null)
                {
                    toSend = ConnectionProtocol.Push(Op.Jru, Pb.New().Var(2, mapId).Build());
                }

                if (fightToRejoin != null)
                {
                    if (ConnectionProtocol.ReadPayload(frame, Op.Ktz) != null
                        || ConnectionProtocol.ReadPayload(frame, Op.Iom) != null) continue;
                    if (ConnectionProtocol.ReadPayload(frame, Op.Kmp) != null)
                    {
                        toSend = ConnectionProtocol.Push(Op.Kmp, FightProtocol.BuildFightMapComing());
                    }
                }

                await EnviarAsync(stream, toSend);
                sent++;

                if (fightToRejoin != null && ConnectionProtocol.ReadPayload(frame, Op.Lqu) != null)
                {
                    await EnviarAsync(stream, ConnectionProtocol.Push(Op.Lqn,
                        ConnectionProtocol.BuildBackInTheFight(character.Name)));
                    await EnviarAsync(stream, ConnectionProtocol.BuildActorsComplete());
                    sent += 2;
                }
            }

            // The block carries the captured ktz -- regeneration begins, rate 5 -- right behind
            // its kml kmp, so the client's counter starts here. The kuq at fight entry reports
            // how long it ran, and that is counted from this moment. Not when going back into a
            // fight: that block carries no ktz.
            if (fightToRejoin == null) SessionContext.State.RegenerationStartedUtc = DateTime.UtcNow;

            // The characteristics go out again here. The real server sends its kub twice, once
            // with the character and once with the map, and it is this second one the client
            // keeps: sending it only in the first block left the sheet empty.
            await EnviarAsync(stream, ConnectionProtocol.Push(Op.Kub, ConnectionProtocol.BuildCharacteristics()));

            // The discovered zaaps. It is not in the recorded blocks —it was checked, none of the
            // four brings an hjk— and without it the travel window comes out with «Ningún destino» even though the
            // hjj sends it the sixty-three. The real server sends it right here, on entering.
            var descubiertos = new List<long>(Managers.Interactives.DiscoveredZaapMaps());
            await EnviarAsync(stream, ConnectionProtocol.Push(
                Op.Hjk, ConnectionProtocol.BuildDiscoveredZaaps(descubiertos)));

            Console.WriteLine($"[World] Block 3 sent: {sent} messages, map {mapId}, characteristics " +
                              $"resent, {descubiertos.Count} zaaps descubiertos.");
            return sent;
        }

        private static async Task<int> SendRewrittenAsync(NetworkStream stream, string block,
                                                          string name, CaptureRewriter.Identity identity)
        {
            int sent = 0;
            foreach (byte[] frame in WorldEntryContent.Frames(block))
            {
                if (ShouldSkip(frame)) continue;
                await EnviarAsync(stream, CaptureRewriter.Rewrite(frame, identity));
                sent++;
            }

            Console.WriteLine($"[World] {name} sent: {sent} messages.");
            return sent;
        }
    }
}
