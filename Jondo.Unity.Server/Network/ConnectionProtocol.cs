using System;
using System.Collections.Generic;
using System.Text;
using Jondo.Unity.Server.Managers;
using Jondo.Unity.Protocol;

namespace Jondo.Unity.Server.Network
{
    /// <summary>
    /// Connection-phase messages: authentication, server list and character list.
    ///
    /// The whole shape of these messages was taken from the real 3.6.10.10 client captures
    /// under Wireshark captures from real game/Authentication-Server-Character. Nothing here
    /// is built from memory: if a field does not show up in a capture, we do not send it.
    ///
    /// Two different protocols share the same port:
    ///
    ///   1. Connection server. Bare messages, no envelope. The client presents the account
    ///      token and receives the server list; then it picks one and receives a ticket
    ///      along with the address to reconnect to.
    ///   2. Game server. Messages wrapped in type.ankama.com/xxx. The client presents the
    ///      ticket (kqz) and receives the burst that ends with the character list (kvi).
    ///
    /// In 3.6.10.10 the messages pushed by the server travel in field 1 of the frame
    /// (390 out of 391 in the enter-world capture); field 3 is the one the client uses.
    /// </summary>
    public static class ConnectionProtocol
    {
        public const string UriPrefix = "type.ankama.com/";

        // ─── Envelope ───────────────────────────────────────────────────────────

        /// <summary>
        /// Wraps a message pushed by the server: f1 { f1 { f1: type_url, f2: payload } }.
        /// </summary>
        public static byte[] Push(string opcode, byte[]? payload = null)
        {
            var any = Pb.New().Str(1, UriPrefix + opcode);
            if (payload != null && payload.Length > 0) any.Bytes(2, payload);

            return Pb.New()
                .Msg(1, Pb.New().Bytes(1, any.Build()))
                .Build();
        }

        // ─── Connection server ──────────────────────────────────────────────────

        /// <summary>
        /// Authentication-accepted response, carrying the server list and, on each server,
        /// a summary of the characters the account owns there.
        ///
        ///   f2 { f1: language
        ///        f3 { f1 { f1: accountId, f2: nickname, f3: tag
        ///                  f4 { f1 (repeated): server
        ///                       f2 (repeated): { f1: type, f2: slots } }
        ///                  f5: subscription end
        ///                  f6: {} } } }
        ///
        /// And each server:
        ///
        ///   f1 { f1 { f1: serverId, f3: type }
        ///        f3 (repeated) { f1: name, f2: breed-1, f3: sex, f4: level, f5: last connection } }
        /// </summary>
        public static byte[] BuildAuthenticationAccepted(
            string lang,
            long accountId,
            string nickname,
            string accountTag,
            string subscriptionEndDate,
            IReadOnlyList<DatabaseManager.DbServer> servers,
            IReadOnlyList<DatabaseManager.DbCharacter> characters)
        {
            var serversList = Pb.New();

            foreach (var server in servers)
            {
                var entry = Pb.New()
                    .Msg(1, Pb.New()
                        .Var(1, server.Id)
                        .VarIfNotZero(3, server.Type));

                foreach (var character in characters)
                {
                    if (character.ServerId != server.Id) continue;

                    var summary = Pb.New().Str(1, character.Name);
                    // Here the breed travels zero-based, one less than in the rest of the protocol.
                    summary.VarIfNotZero(2, character.Breed - 1);
                    summary.VarIfNotZero(3, character.Sex);
                    summary.VarIfNotZero(4, character.Level);
                    // The date is never left out. Every character summary in the capture carries
                    // one, and a character without it leaves the client on an empty server-
                    // selection screen: it renders nothing at all, with no error.
                    summary.Str(5, LastConnectionOrNow(character.LastConnection));
                    entry.Msg(3, summary);
                }

                serversList.Msg(1, entry);
            }

            // How many characters fit per server type. Seven entries, types 0 to 6.
            //
            // The real capture of the character creation screen brings five in all seven, with
            // an account that had four characters on its server and the button active. So this
            // is the cap, not the count, and raising it is right; what had the button greyed out was
            // something else (the subscription date, in GameServerProxy).
            for (int type = 0; type <= 6; type++)
            {
                var slots = Pb.New();
                slots.VarIfNotZero(1, type);
                slots.Var(2, MaxCharactersPerServer);
                serversList.Msg(2, slots);
            }

            var accepted = Pb.New()
                .Var(1, accountId)
                .Str(2, nickname)
                .Str(3, accountTag)
                .Msg(4, serversList)
                .Str(5, subscriptionEndDate)
                .EmptyMsg(6);

            return Pb.New()
                .Msg(2, Pb.New()
                    .Str(1, lang)
                    .Msg(3, Pb.New().Msg(1, accepted)))
                .Build();
        }

        /// <summary>
        /// How many characters fit per server.
        /// </summary>
        /// <remarks>
        /// FIVE, which is what the real server sends and not a chosen figure. Measured on the
        /// raw data of «desde launcher a eleccion servidor.pcapng»: the byte pair 1005 —field
        /// f2 with value 5— appears eight times in that response, and 100 (1064) does not appear once.
        ///
        /// Here it said 100, raised by hand with the reasoning that «there is nothing to limit».
        /// Sending a number the client never sees is exactly what this project does not do: it is not
        /// known what it does with it, and the create character button was still grey all the same.
        /// </remarks>
        public const int MaxCharactersPerServer = 5;

        /// <summary>
        /// Format of the connection dates in the capture: ISO 8601 with milliseconds and the
        /// local UTC offset, for instance 2026-08-09T16:29:18.033+02:00.
        /// </summary>
        public const string ConnectionDateFormat = "yyyy-MM-ddTHH:mm:ss.fffzzz";

        /// <summary>
        /// The stored date, or the current time when the character has never entered the world.
        /// Sending nothing is not an option: the client needs the field to draw the character
        /// on the server-selection screen.
        /// </summary>
        public static string LastConnectionOrNow(string? stored)
        {
            return string.IsNullOrEmpty(stored)
                ? DateTimeOffset.Now.ToString(ConnectionDateFormat)
                : stored;
        }

        /// <summary>
        /// Redirect after picking a server: single-use ticket, address and ports.
        ///
        ///   f2 { f1: language, f4 { f1 { f1: ticket, f2: address, f3: ports } } }
        ///
        /// The ports go as concatenated varints inside a single bytes field.
        /// </summary>
        public static byte[] BuildServerSelected(string lang, string ticket, string host, params int[] ports)
        {
            var portList = new List<long>();
            foreach (int p in ports) portList.Add(p);

            var info = Pb.New()
                .Str(1, ticket)
                .Str(2, host)
                .Packed(3, portList);

            return Pb.New()
                .Msg(2, Pb.New()
                    .Str(1, lang)
                    .Msg(4, Pb.New().Msg(1, info)))
                .Build();
        }

        // ─── Game server: welcome burst ─────────────────────────────────────────

        /// <summary>
        /// The burst the real server sends as soon as the client presents the ticket, in the
        /// same order as the capture. It ends with the character list.
        ///
        /// kvc and krv were deliberately not copied from the capture: there they are client
        /// messages, and the previous version of this emulator sent them back to the client
        /// by mistake.
        /// </summary>
        /// <summary>
        /// The character list as it actually travels: three kqp, the list, and the gift catalogue.
        /// </summary>
        /// <remarks>
        /// Five frames, never the kvi on its own. That is how every capture sends it -- in the
        /// welcome burst, after a creation, and after a deletion -- and sending only the kvi is
        /// what made a freshly created character invisible to the client.
        ///
        /// The symptom was ugly and easy to blame on the wrong thing: create a character, press
        /// play, and the PREVIOUS character walked into the world. Nothing was mixed up
        /// server-side; the selection arrived naming the old character and was honoured correctly,
        /// because the client still held the list it had before the creation and sent the only
        /// entry it knew. Going back to the selection screen refreshed it and everything worked,
        /// which is exactly the shape of a stale list rather than a wrong lookup.
        ///
        /// Measured in "crear personaje - borrar personaje", where the real server answers a
        /// successful kvb with the whole set again:
        ///
        /// <code>
        ///   kvb (empty, success)  ->  kqp kqp kqp  kvi  jtg
        ///   kvn (deleted)         ->  kqp kqp kqp  kvi  jtg
        /// </code>
        /// </remarks>
        public static List<byte[]> CharacterListFrames(IReadOnlyList<DatabaseManager.DbCharacter> characters)
            => new()
            {
                Push(Op.Kqp, Pb.New().Var(1, 1).Var(2, 1).Build()),
                Push(Op.Kqp, Pb.New().Var(1, 1).Build()),
                Push(Op.Kqp),
                Push(Op.Kvi, BuildCharactersList(characters)),
                Push(Op.Jtg, BuildGiftCatalogue()),
            };

        /// <param name="withList">
        /// Whether the character list closes the burst. It does on an ordinary login; when a
        /// character of the account is still in a fight the real burst stops at the krs, the
        /// client asks with kvc, and the answer is the list followed by the kvd. Measured in the
        /// two reconnection captures: "kra lqu hoy kqu mgq mgt hpd krs", then "kvc krv" from the
        /// client, then "kvi kvd".
        /// </param>
        public static List<byte[]> BuildWelcomeBurst(IReadOnlyList<DatabaseManager.DbCharacter> characters,
                                                     bool withList = true)
        {
            var burst = new List<byte[]>
            {
                Push(Op.Kra),
                Push(Op.Lqu, Pb.New()
                    .Var(1, SyncRate)
                    .Var(2, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds())
                    .Build()),
                Push(Op.Hoy, BuildHoy()),
                Push(Op.Kqu, Pb.New().Packed(1, ActiveFeatures).Build()),
                // mgq without field 1. That field is the whole reason "create a character" was
                // dark, and it is the one frame in the burst where this server said something the
                // captures never say on an account that can create. Sorted by whether creation
                // worked, every capture in "Autenticacion-Servidor-Personaje" falls on the right
                // side of it:
                //
                //   10011801         creation succeeds        "creacion personaje-exito"
                //   10011801         creation succeeds        "crear personaje - borrar personaje"
                //   10011801         already in the world     "tutorial completo"
                //   080110011801     refused, maximum reached "fallo por limite maximo"
                //   080110011801     account sitting at 4/5   "eleccion servidor a eleccion personaje"
                //
                // The same account sends it in one session and not in another, so it is a state and
                // not a property of the account. Ours sent it always, on every login, which is why
                // an account with a single character was told it had reached its maximum -- and why
                // an empty one could still make its first: one is a limit you have not hit yet.
                //
                // What field 1 means exactly is not decoded here, and the comment says so. What is
                // measured is that no capture where creation works carries it.
                Push(Op.Mgq, Pb.New().Var(2, 1).Var(3, 1).Build()),
                Push(Op.Mgt, Pb.New().EmptyMsg(2).Build()),
                Push(Op.Hpd, Pb.New().Var(1, 1).Build()),
                Push(Op.Krs),
                Push(Op.Mgz, Pb.New().Var(1, CatalogMark).Build()),

                // A kvd DOES NOT GO HERE, and sending it was what had half the screen dead.
                //
                // It was put in by eye, with the reasoning that «it closes the character list» and
                // that the create button was greyed out because the screen was missing its ending.
                // It sounds right and it is the other way round. Measured on the captures: the kvd appears in THREE, and the
                // three are of entering the world directly without going through the screen —reconnection to a
                // fight and koliseo—, looking like this:
                //
                //   kvi(381)  kvd(0)  ipc  kva  mft        back to a fight
                //   kra  kqu  kvd(0)  kva  ivx  hlm        koliseo, and there is not even a kvi
                //
                // And in the burst of the real character screen it IS NOT THERE: neither in the one of the
                // account with four characters and the button active, nor in the one of the empty account that
                // creates one, nor in the one that fails on the maximum limit. All three go kvi and then jtg.
                //
                // That is, the kvd means «do not stop here». Sending it always, the client
                // built the screen as if it were passing through: the create character button lifeless
                // and the change server one leading nowhere.
            };

            // The list closes the burst, framed the way it always travels.
            if (withList) burst.AddRange(CharacterListFrames(characters));
            return burst;
        }

        /// <summary>
        /// Catalogue of gift items attached to the account (jtg). It closes the burst in all three
        /// captures, right after the character list.
        ///
        /// It goes out empty, which is what it means here: the message is a repeated field and our
        /// accounts own no gifts. The entries are not invented, only the envelope is real:
        ///   f3 (repeated) { f1 { f2: name, f3 { ...item... }, f6 { ...description... } }, f2: id }
        /// </summary>
        public static byte[] BuildGiftCatalogue() => Array.Empty<byte>();

        /// <summary>How often the server synchronizes the clock with the client.</summary>
        private const int SyncRate = 120;

        /// <summary>
        /// Identifier of the content catalogue the client asks for afterwards. It is an opaque
        /// value copied from the capture: the client only compares it against itself.
        /// </summary>
        private const int CatalogMark = 304672615;

        /// <summary>
        /// List of features enabled on the server. Copied verbatim from the capture, with no
        /// interpretation: they are opaque identifiers.
        /// </summary>
        private static readonly long[] ActiveFeatures =
            { 3, 7, 13, 20, 23, 105, 124, 125, 126, 136, 143, 145, 150 };

        /// <summary>
        /// The game server's greeting, with the same values as the capture.
        ///
        ///   f1: 30   f2: 1   f3: 1   f6: language   f7: 200
        ///
        /// Without f5. We were sending an f5 = 2 that is in none of the three startup captures, and
        /// the language went in English when the client starts in Spanish. They are the only two
        /// differences that remained between our welcome burst and the real one.
        /// </summary>
        /// <summary>
        /// The frame that carries, among other things, what the account is entitled to.
        /// </summary>
        /// <remarks>
        /// Field 5 is deliberately NOT written, and the reason is worth keeping because it was
        /// added here once on a correlation and taken straight back out.
        ///
        /// Across the first six captures f5 appeared in exactly one of the two accounts recorded,
        /// and that was the account holding four characters on a single server -- which reads like
        /// a subscription marker and is not one. The capture "crear personaje - borrar personaje"
        /// settles it: the SAME account, in three consecutive logins, creating an eleventh
        /// character and deleting it again, sends
        ///
        /// <code>
        ///   081e100118013202667238c801      no f5, and the create button is working
        /// </code>
        ///
        /// So f5 comes and goes for one account between sessions and says nothing about what the
        /// account may do. Sending it was copying a value nobody had decoded.
        /// </remarks>
        private static byte[] BuildHoy()
        {
            return Pb.New()
                .Var(1, 30)
                .Var(2, 1)
                .Var(3, 1)
                .Str(6, ClientLanguage)
                .Var(7, 200)
                .Build();
        }



        /// <summary>The language the client is launched with.</summary>
        public const string ClientLanguage = "es";

        // ─── Character list (kvi) ───────────────────────────────────────────────

        /// <summary>
        /// List of the account's characters on the chosen server.
        ///
        ///   f1 (repeated) { f1 { f2: name
        ///                        f3: level
        ///                        f4 { f2: { f3: sex }   (present but empty when the sex is 0)
        ///                             f6: look
        ///                             f7: breed } }
        ///                   f2: characterId }
        /// </summary>
        public static byte[] BuildCharactersList(IReadOnlyList<DatabaseManager.DbCharacter> characters)
        {
            var kvi = Pb.New();

            foreach (var character in characters)
            {
                kvi.Msg(1, Pb.New()
                    .Msg(1, BuildCharacterDetails(character))
                    .Var(2, character.Id));
            }

            return kvi.Build();
        }

        /// <summary>
        /// The character block shared by the character list and the selection reply.
        ///
        ///   f2: name, f3: level, f4 { f2: sex, f6: look, f7: breed }
        ///
        /// When <paramref name="withDates"/> is set the two dates the selection reply carries are
        /// added: f4.f1 is when the character was created and f4.f4 is the server's timestamp.
        /// </summary>
        private static Pb BuildCharacterDetails(DatabaseManager.DbCharacter character, bool withDates = false)
        {
            var traits = Pb.New();

            if (withDates)
            {
                traits.Str(1, string.IsNullOrEmpty(character.LastConnection)
                    ? DateTimeOffset.UtcNow.ToString("yyyy-MM-ddTHH:mm:ss.fffZ")
                    : character.LastConnection);
            }

            // The sex block is always there: empty for sex 0, carrying f3 for sex 1.
            if (character.Sex != 0) traits.Msg(2, Pb.New().Var(3, character.Sex));
            else traits.EmptyMsg(2);

            if (withDates)
            {
                traits.Str(4, DateTimeOffset.UtcNow.ToString("yyyy-MM-ddTHH:mm:ss.fffffffZ"));
            }

            // With the id, so that on the selection screen he comes out mounted if he is. Without it the
            // mount was only known for the character already playing.
            traits.Bytes(6, BreedLookTable.BuildLook(
                character.Breed, character.Sex, character.HeadId, null, character.Id));
            traits.VarIfNotZero(7, character.Breed);

            return Pb.New()
                .Str(2, character.Name)
                .VarIfNotZero(3, character.Level)
                .Msg(4, traits);
        }

        /// <summary>
        /// Reply to the character selection (kva). Without it the client stays on the character
        /// screen with the hourglass up: it is the message that tells it which character it is
        /// now playing.
        ///
        ///   f1 { f1 { f1: details, f2: characterId } }
        ///
        /// Two wrappers deeper than the character list, and the details carry the two dates.
        /// Taken from the world-entry capture.
        /// </summary>
        public static byte[] BuildCharacterSelectedSuccess(DatabaseManager.DbCharacter character)
        {
            return Pb.New()
                .Msg(1, Pb.New()
                    .Msg(1, Pb.New()
                        .Msg(1, BuildCharacterDetails(character, withDates: true))
                        .Var(2, character.Id)))
                .Build();
        }

        // ─── World: characteristics ─────────────────────────────────────────────

        /// <summary>
        /// Characteristic ids, worked out by lining the captured kub up against the character
        /// sheet of the account it was recorded from. The five resistances settled it: the
        /// capture carries 33, 39, 9, 37 and 14, and the sheet showed exactly those percentages
        /// for earth, fire, water, air and neutral.
        /// </summary>
        public static class Stat
        {
            public const int LifePoints = 0;
            public const int ActionPoints = 1;
            /// <summary>Points still to be spent. Proved by the capture: 995 before spending
            /// fifteen on the sheet, 980 after.</summary>
            public const int RemainingPoints = 3;
            public const int Strength = 10;
            public const int Vitality = 11;
            public const int Wisdom = 12;
            public const int Chance = 13;
            public const int Agility = 14;
            public const int Intelligence = 15;
            public const int Critical = 18;
            public const int Range = 19;
            public const int MovementPoints = 23;
            public const int Power = 25;
            public const int Summons = 26;
            public const int DodgeActionPoints = 27;
            public const int DodgeMovementPoints = 28;
            public const int Pods = 40;
            public const int Initiative = 44;

            /// <summary>
            /// The energy, "energyPoints" in the client's catalogue, and the gauge it fills,
            /// "maxEnergyPoints". 47 was named Energy here and carried the 10,000 of the gauge,
            /// while 29 went out at zero: every captured kub has 10000 in both, or 8000 in the 29
            /// once a fight is lost. See <see cref="Managers.Energy"/>.
            /// </summary>
            public const int EnergyPoints = 29;
            public const int MaxEnergyPoints = 47;

            /// <summary>
            /// "hitPointLoss": the life missing outside a fight, negative, in the f2 of its f4.
            /// "f4 { f2: -576 }" after the defeats of the captures. See <see cref="Managers.RestingLife"/>.
            /// </summary>
            public const int HitPointLoss = 97;
            public const int Prospecting = 48;
            public const int Heals = 49;
            public const int Escape = 78;
            public const int Lock = 79;
            public const int WithdrawActionPoints = 82;
            public const int WithdrawMovementPoints = 83;
            public const int Shield = 96;
        }

        /// <summary>
        /// The four wisdom gives and the two agility gives, ten points of the characteristic for
        /// one of each. They are not sent by anybody else and the client does not work them out on
        /// its own, so with wisdom and agility spent the panel still read zero across the board.
        ///
        /// Which id is which comes from the client's own table, extracted by
        /// extract_characteristics.py: 27 and 28 are the dodges, 82 and 83 the withdrawals, 78
        /// escape and 79 lock. The same table is what showed 46 is the alignment rank and not
        /// prospecting, which is 48.
        /// </summary>
        private static readonly Dictionary<int, Func<long>> Derived = new Dictionary<int, Func<long>>
        {
            { Stat.DodgeActionPoints,    () => Jondo.Unity.Server.Network.SessionContext.State.TotalWisdom / 10 },
            { Stat.DodgeMovementPoints,  () => Jondo.Unity.Server.Network.SessionContext.State.TotalWisdom / 10 },
            { Stat.WithdrawActionPoints, () => Jondo.Unity.Server.Network.SessionContext.State.TotalWisdom / 10 },
            { Stat.WithdrawMovementPoints, () => Jondo.Unity.Server.Network.SessionContext.State.TotalWisdom / 10 },
            { Stat.Escape,               () => Jondo.Unity.Server.Network.SessionContext.State.TotalAgility / 10 },
            { Stat.Lock,                 () => Jondo.Unity.Server.Network.SessionContext.State.TotalAgility / 10 },
        };

        /// <summary>Points a character starts with, before anything is spent or equipped.</summary>
        private const int BaseActionPoints = 6;
        private const int BaseMovementPoints = 3;
        private const int BasePods = 1000;

        /// <summary>
        /// What every characteristic is worth on a character that has just been created, taken
        /// from the kub of the character-creation capture. It is the only place where the game
        /// shows its own defaults with nothing on top.
        ///
        /// This is what the -100% and the blanket 50% resistances were: a whole family of
        /// characteristics that are percentages and start at 100, sent at zero. The client reads
        /// zero and draws the difference against the hundred it expects.
        ///
        /// Anything not listed starts at zero, which is most of them.
        ///
        /// Characteristic 97 used to be here at -55 and is not any more. The creation capture does
        /// carry it, but the played character of the other capture has it empty, so -55 is not a
        /// default: it is something about a character that has only just been made.
        /// </summary>
        private static readonly Dictionary<int, long> FreshCharacter = new Dictionary<int, long>
        {
            { 48, 100 },
            { 75, 10 },
            { 107, 100 },
            { 120, 100 }, { 121, 100 }, { 122, 100 }, { 123, 100 }, { 124, 100 }, { 125, 100 },
            { 141, 100 }, { 142, 100 }, { 143, 100 },
            { 150, 100 },
        };

        /// <summary>
        /// Life a character has by its level alone, before vitality adds anything.
        ///
        /// The creation capture gives 55 at level 1 and the game's own rule is fifty plus five a
        /// level, which lands exactly there. The captured level-154 character carries more than
        /// the formula gives, and that surplus is what quests and parchments hand out over a
        /// lifetime: ours will come from the database the day we store it.
        /// </summary>
        private static long BaseLife(int level) => 50 + 5L * Math.Max(1, level);

        /// <summary>
        /// Characteristics of the character (kub). Without it the client shows the life bar at
        /// 0/0 and every characteristic empty.
        ///
        ///   f2 { f1: experience, f4: ?, f7: floor of the level, f8: experience for the next one,
        ///        f9 { ... }, f10: kamas,
        ///        f11 (repeated) { f1: id, container { ... } } }
        ///
        /// The container is NOT the same for every characteristic, and getting that wrong is not
        /// a cosmetic mistake. The client's own log, Player.log, caught it:
        ///
        ///   NullReferenceException at giq.bkjt (llp a) ... at ees.wuc (kub a)
        ///
        /// llp is one characteristic entry and kub is this message: putting a characteristic in
        /// the wrong container leaves the client reading a field that is not there, it throws,
        /// and the whole sheet dies with it. That is what greyed the characteristics button out
        /// while the C key still opened the panel: the panel is built from client data, the
        /// button is enabled by the handler that never finished.
        ///
        ///   f4 { f2: base, f3: from parchments, f7: from equipment }   almost all of them
        ///   f5 { f1: base, f5: bonus }                                 1 and 23, action and movement
        ///   f2 { f2: value }                                           29, 47 and 96
        ///
        /// Which id goes in which container is read off the captured kub rather than written
        /// down here, the same as the list of ids: see WorldEntry.ContainerOf.
        ///
        /// f3 is not the constant 100 it looked like either. The captured character had exactly
        /// 100 in all six primaries because it had drunk every parchment in the game, which is the
        /// cap. Copying it made every characteristic of ours read a hundred points higher than
        /// the database says, and the life bar with it.
        /// </summary>
        public static byte[] BuildCharacteristics()
        {
            int level = Jondo.Unity.Server.Network.SessionContext.State.CharacterLevel;

            // The three experience fields, and they are not in the order they look:
            //
            //   f1  where the NEXT level starts
            //   f7  where this one started
            //   f8  what the character has
            //
            // The kub of a character that has just been created settles it: f1 is 110 with f7 and
            // f8 absent, and 110 is exactly the threshold of level 2. A brand new character has
            // no experience, so f1 cannot be what it has; and f7 <= f8 <= f1 is the only reading
            // that also fits the level 154 of the other capture.
            //
            // We had f1 and f8 the other way round, which handed the client an experience above
            // the threshold it was being told to reach: hence a bar full to the brim and a
            // "next level in 0 XP" at every level.
            var body = Pb.New()
                .VarIfNotZero(1, ExperienceTable.NextLevelFloor(level))
                .Var(4, FreshUnknownF4)
                .VarIfNotZero(7, ExperienceTable.LevelFloor(level))
                .VarIfNotZero(8, Jondo.Unity.Server.Network.SessionContext.State.Experience)
                .Bytes(9, FreshUnknownF9())
                .VarIfNotZero(10, Jondo.Unity.Server.Network.SessionContext.State.Kamas);

            // The six the player spends points on: the base, which is the points, and beside it
            // what the scrolls gave. The two travel in different fields and the client draws them
            // as two lines, "Base" and "Adicional".
            var primary = new Dictionary<int, long>
            {
                { Stat.Strength, Jondo.Unity.Server.Network.SessionContext.State.StatStrength },
                { Stat.Vitality, Jondo.Unity.Server.Network.SessionContext.State.StatVitality },
                { Stat.Wisdom, Jondo.Unity.Server.Network.SessionContext.State.StatWisdom },
                { Stat.Chance, Jondo.Unity.Server.Network.SessionContext.State.StatChance },
                { Stat.Agility, Jondo.Unity.Server.Network.SessionContext.State.StatAgility },
                { Stat.Intelligence, Jondo.Unity.Server.Network.SessionContext.State.StatIntelligence },
            };
            var scrolled = new Dictionary<int, long>
            {
                { Stat.Strength, Jondo.Unity.Server.Network.SessionContext.State.ScrolledStrength },
                { Stat.Vitality, Jondo.Unity.Server.Network.SessionContext.State.ScrolledVitality },
                { Stat.Wisdom, Jondo.Unity.Server.Network.SessionContext.State.ScrolledWisdom },
                { Stat.Chance, Jondo.Unity.Server.Network.SessionContext.State.ScrolledChance },
                { Stat.Agility, Jondo.Unity.Server.Network.SessionContext.State.ScrolledAgility },
                { Stat.Intelligence, Jondo.Unity.Server.Network.SessionContext.State.ScrolledIntelligence },
            };

            IReadOnlyList<int> ids = WorldEntry.CharacteristicIds;
            if (ids.Count == 0)
            {
                // No capture to learn from: at least the ones we know about travel.
                var fallback = new List<int>
                {
                    Stat.LifePoints, Stat.ActionPoints, Stat.RemainingPoints,
                    Stat.MovementPoints, Stat.EnergyPoints, Stat.MaxEnergyPoints, Stat.Pods
                };
                fallback.AddRange(primary.Keys);
                fallback.AddRange(FreshCharacter.Keys);
                ids = fallback;
            }

            // What the equipment adds, which travels in field 7 of each entry.
            var fromEquipment = Managers.Equipment.Bonuses();

            foreach (int id in ids)
            {
                long value = primary.TryGetValue(id, out long spent) ? spent : ValueOf(id, level);
                fromEquipment.TryGetValue(id, out long equipped);
                scrolled.TryGetValue(id, out long fromScrolls);

                switch (WorldEntry.ContainerOf(id))
                {
                    case 5:
                        // Action and movement points: f1 is the base, f5 whatever adds to it.
                        body.Msg(11, Pb.New()
                            .Var(1, id)
                            .Msg(5, Pb.New().Var(1, value).VarIfNotZero(5, equipped)));
                        break;

                    case 2:
                        body.Msg(11, Pb.New().Var(1, id).Msg(2, Pb.New().VarIfNotZero(2, value)));
                        break;

                    default:
                        AddStat(body, id, value, equipped, fromScrolls);
                        break;
                }
            }

            return Pb.New().Msg(2, body).Build();
        }

        /// <summary>Value of a characteristic that the player does not spend points on.</summary>
        internal static long ValueOf(int id, int level)
        {
            if (id == Stat.LifePoints) return BaseLife(level);
            if (id == Stat.ActionPoints) return BaseActionPoints;
            if (id == Stat.MovementPoints) return BaseMovementPoints;
            if (id == Stat.MaxEnergyPoints) return Jondo.Unity.World.Fights.DefeatPenalty.MaxEnergy;
            if (id == Stat.EnergyPoints)
                return Managers.Energy.Of(Jondo.Unity.Server.Network.SessionContext.State.CharacterId);
            if (id == Stat.HitPointLoss)
            {
                // What was missing when the client's regeneration counter last started: it counts
                // on from there by itself (the ktz of every return to roleplay).
                var state = Jondo.Unity.Server.Network.SessionContext.State;
                DateTime since = state.RegenerationStartedUtc == default ? DateTime.UtcNow : state.RegenerationStartedUtc;
                return -Managers.RestingLife.MissingAt(state.CharacterId, since);
            }
            // Five pods a point of strength on top of the base, which is what the capture shows:
            // five points of strength moved this characteristic by twenty-five.
            if (id == Stat.Pods) return BasePods + 5L * Jondo.Unity.Server.Network.SessionContext.State.TotalStrength;
            if (id == Stat.RemainingPoints) return Jondo.Unity.Server.Network.SessionContext.State.CharacterRemainingPoints;
            if (Derived.TryGetValue(id, out var from)) return from();
            return FreshCharacter.TryGetValue(id, out long value) ? value : 0;
        }

        /// <summary>
        /// Two fields of the body we cannot name yet, sent with the value a character has the day
        /// it is created.
        ///
        /// f4 is 5 on a brand new character and 30 on the level 154 of the other capture, so it
        /// grows with something; f9 is a block that reads { f2: 2, f3 { f3: 500 }, f5: 1 } when new
        /// and { f1: 100, f2: 3, f3 { f3: 500 }, f5: 200 } on the old character. Until we know what
        /// they are, the honest value is the one the game itself gives a new character: it is ours
        /// to send, it is not somebody else's number, and leaving them out altogether is not the
        /// same as sending the default.
        /// </summary>
        private const long FreshUnknownF4 = 5;

        private static byte[] FreshUnknownF9() =>
            Pb.New().Var(2, 2).Msg(3, Pb.New().Var(3, 500)).Var(5, 1).Build();

        /// <summary>
        /// One characteristic: f2 the base, f3 what the scrolls gave, f7 what the equipment adds.
        /// </summary>
        /// <remarks>
        /// f3 is MEASURED: every scrolled character in the captures carries its scrolls there --
        /// <c>f4 { f2: 398, f3: 100, f7: 499 }</c> is a real strength -- and a characteristic the
        /// player never scrolled leaves it out, as proto3 does with a zero.
        /// </remarks>
        private static void AddStat(Pb body, int id, long value, long fromEquipment = 0, long fromScrolls = 0)
        {
            // Characteristic 0 is life, and it is the one entry of the real message that carries
            // no id at all: proto3 leaves the field out when the value is zero, and zero is its
            // id. Writing Var(1, 0) here would put a field the real one does not have.
            var entry = Pb.New();
            if (id != 0) entry.Var(1, id);
            entry.Msg(4, Pb.New().VarIfNotZero(2, value).VarIfNotZero(3, fromScrolls).VarIfNotZero(7, fromEquipment));
            body.Msg(11, entry);
        }

        // ─── World: heartbeat ───────────────────────────────────────────────────

        /// <summary>
        /// Answer to kqo, which is a heartbeat and not a request for anything.
        ///
        /// The client sends kqo every five seconds for as long as it is in the world and the real
        /// server answers it with this single message. In the tutorial capture there are
        /// twenty-four of them in a row, 5.000 ms apart, and after every one the server sends kqy
        /// and nothing more.
        ///
        /// The frame this builds comes out byte for byte like the captured one:
        /// 1d 0a 1b 0a 19 0a 13 type.ankama.com/kqy 12 02 08 01.
        /// </summary>
        public static byte[] BuildHeartbeatAnswer() => Push(Op.Kqy, Pb.New().Var(1, 1).Build());

        /// <summary>
        /// Closes a map load (lva). It carries nothing: it is the "that is every actor" mark.
        ///
        /// It goes immediately behind jss in every capture where a map is loaded — the four
        /// movement ones, the entry into the world and the tutorial. Without it the client never
        /// finishes loading the map: it waits about two seconds, asks again with knm, kno and kny,
        /// and starts over.
        /// </summary>
        public static byte[] BuildActorsComplete() => Push(Op.Lva);

        // ─── World: life regeneration ────────────────────────────────────────

        /// <summary>
        /// The regeneration rate the real server hands out on every return to roleplay, in
        /// tenths of a second per life point: 135 of the 143 ktz in the captures carry it.
        /// </summary>
        /// <remarks>
        /// The other eight carry 1 -- the Trool fair and one world entry with a guild raid -- and
        /// what makes a rate fast is not measured, so nothing here decides it.
        /// </remarks>
        public const int RegenerationRate = 5;

        /// <summary>One regeneration tick, in milliseconds: the rate is in tenths of a second.</summary>
        public const int RegenerationTickMs = RegenerationRate * 100;

        /// <summary>
        /// Life regeneration begins (ktz): f1 the rate. Right behind every "kml kmp" back to
        /// roleplay -- the world entry replays the captured one, and the fight end builds this.
        /// </summary>
        public static byte[] BuildRegenerationStarted(int rate)
            => Push(Op.Ktz, Pb.New().Var(1, rate).Build());

        /// <summary>
        /// Life regeneration ends (kuq): f1 the life, f2 the ticks the counter had run, f4 the
        /// maximum. Between the lqu and the lva of the tactical map load, at every fight entry.
        /// </summary>
        /// <remarks>
        /// This is what stops the client's own counter. Without it the counter started at the
        /// world entry keeps adding one point every tick THROUGH the fight, to the local player's
        /// bar only, which is what "the Ocra recovers life tick by tick" was: the roleplay
        /// regeneration drawn over a fight. Measured against the two challenge captures of the
        /// 9th of August: 08db2810970120bb29 is {5211, 151, 5307}.
        /// </remarks>
        public static byte[] BuildRegenerationEnded(int life, int ticks, int maxLife)
            => Push(Op.Kuq, Pb.New().Var(1, life).VarIfNotZero(2, ticks).Var(4, maxLife).Build());

        /// <summary>
        /// How many ticks a counter started at <paramref name="startedUtc"/> has run by
        /// <paramref name="nowUtc"/>: the f2 of the kuq. Zero when it was never started.
        /// </summary>
        public static int RegenerationTicksSince(DateTime startedUtc, DateTime nowUtc)
        {
            if (startedUtc == default || nowUtc <= startedUtc) return 0;
            return (int)Math.Min(int.MaxValue, (long)(nowUtc - startedUtc).TotalMilliseconds / RegenerationTickMs);
        }

        // ─── World: actors on the map ───────────────────────────────────────────

        /// <summary>
        /// The actors on a map (jss). The client asks for it with jrh, carrying the map id, and
        /// without an answer the map comes up empty: no avatar, no NPCs, no monsters.
        ///
        ///   f2: map id
        ///   f6: subarea id
        ///   f5 (repeated) { f1 { f1: cell, f2: facing }
        ///                   f2 { ...what it is... }
        ///                   f3: contextual id }
        ///
        /// f6 is not decoration. The client's own Player.log shows what happens without it:
        ///
        ///   at MapInfoUI.SetInfoFromSubarea (System.Int16 subAreaId)
        ///   at MapInfoUI.SetMapInfoData (System.Int64 mapId, System.Int16 subAreaId, ...)
        ///   at MapInfoUI.OnMapComplementaryInformationsData (ccn message)
        ///   at ehl.xxt (jss a)
        ///
        /// It looks the subarea up, finds nothing because we were sending zero, and throws. With
        /// it goes everything that widget sets: the name of the map, its coordinates, and the
        /// little figure on the minimap, which is why that stayed painted on the zaap however far
        /// the character walked.
        ///
        /// The value is the one the map has in the database, checked against the capture: map
        /// 154010371 travels with 450 and map 154010882 with 442, and those are exactly the
        /// subareas MapPositions gives for them.
        ///
        /// Still missing from this message, and unrelated to the above: f11, the interactive
        /// elements of the map (doors, resources), and f15, the state each of them is in.
        ///
        /// What each actor is comes from which field appears inside f2.f1, as seen in the
        /// movement captures: f5 a player, f7 an NPC, f4 a group of monsters. Every one of the
        /// three carries its look in f2.f3, the group included.
        ///
        /// NPCs and monster groups use negative contextual ids, which is how the client tells
        /// them from players.
        /// </summary>
        public static byte[] BuildMapActors(long mapId, DatabaseManager.DbCharacter character,
                                            int cell, int facing, long accountId)
        {
            var jss = Pb.New().Var(2, mapId);

            jss.Msg(5, PlayerActor(character, cell, facing, accountId));

            // Other connected players already standing on this map. Each client keeps its own
            // socket and state; only immutable snapshots are read while building this response.
            foreach (var other in SessionRegistry.OnMap(mapId))
            {
                if (other.CharacterId <= 0 || other.CharacterId == character.Id) continue;
                var otherCharacter = DatabaseManager.GetCharacterById(other.CharacterId);
                if (otherCharacter == null) continue;
                jss.Msg(5, PlayerActor(otherCharacter, other.State.CellId,
                                      other.State.Orientation, other.AccountId));
            }

            // The monster groups already placed by the spawner.
            //
            // The shape is not the obvious one, and getting it wrong is what kept the map empty.
            // The group is ONE message, not one per monster:
            //
            //   f4 { f1: 1
            //        f2 { f1 (repeated): underling { f1: id, f2: level, f3: look, f4: grade }
            //             f2:            leader    { f1: id, f2: level,           f4: grade } }
            //        f5: -1 }
            //   f3 { f2: 3, f3: bones }      the group's look, NEXT to f4 and not inside it
            //
            // The leader appears exactly once and without a look of its own, because its look is
            // the group's: that is the sprite the client draws on the cell. Checked against nine
            // groups across the combat and movement captures, and the count always comes out as
            // one leader plus however many underlings.
            //
            // What we used to send was one f2 per monster, straight under f4. That puts a varint
            // where the client's parser expects a submessage, and a generated parser does not
            // shrug that off: it throws and drops the whole jss. Which is why nothing was drawn
            // at all — not the monsters, not the NPCs, and not the player either, even though the
            // player's own entry was fine.
            //
            // Two more details from the capture, both easy to get backwards: the level goes in f2
            // and the grade (1..5) in f4, not the other way round, and the group closes with
            // f5 = -1.
            foreach (var group in Managers.MobSpawnManager.GetMobsForMap(mapId))
            {
                if (group.Members.Count == 0) continue;

                // A group being fought went off the map with a kmu when its fight opened (the
                // follow capture, frame 132): it is not drawn for whoever comes now either.
                if (Handlers.FightHandler.IsGroupFighting(group.MobId)) continue;

                jss.Msg(5, MonsterGroupActor(group));
            }

            AddNpcs(jss, mapId);

            // Behind the actors, which is where the capture puts it.
            var where = MapManager.GetMapInfo(mapId);
            if (where != null) jss.VarIfNotZero(6, where.SubAreaId);

            // The houses, between the subarea and the elements as in both house captures: f7 the
            // one the viewer is inside, f9 those on this street that can be owned.
            Handlers.HouseHandler.AddToMap(jss, mapId);

            AddInteractiveElements(jss, mapId, accountId);

            // And last, the fights in their placement: the swords. Each f12 is what an hpy carries
            // (the sword capture's jss at frame 53, the jalatós one at 840); a fight past its
            // placement has none -- it is only counted, by the jqz that follows the jss.
            foreach (var fight in Handlers.FightHandler.PlacementFightsOnMap(mapId))
            {
                jss.Msg(12, Handlers.FightHandler.MapEntryOf(fight));
            }

            return jss.Build();
        }

        /// <summary>
        /// A monster group as an actor of the map: its entry in the jss, and what a jsn carries
        /// to draw it again (the follow capture's frame 131 is this same block).
        /// </summary>
        internal static Pb MonsterGroupActor(Managers.MobSpawnManager.MobGroup group)
        {
            var creatures = Pb.New();
            for (int i = 1; i < group.Members.Count; i++)
            {
                var member = group.Members[i];
                creatures.Msg(1, Pb.New()
                    .Var(1, member.Monster.Id)
                    .VarIfNotZero(2, LevelOf(member))
                    .Msg(3, MonsterLook(member.Monster.Look))
                    .VarIfNotZero(4, GradeOf(member)));
            }

            var leader = group.Members[0];
            creatures.Msg(2, Pb.New()
                .Var(1, leader.Monster.Id)
                .VarIfNotZero(2, LevelOf(leader))
                .VarIfNotZero(4, GradeOf(leader)));

            if (group.Modular) AddAlternatives(creatures, group.Members);

            return Pb.New()
                .Msg(1, Pb.New().Var(1, group.CellId).Var(2, group.Orientation))
                .Msg(2, Pb.New()
                    .Msg(1, Pb.New().Msg(4, Pb.New()
                        .Var(1, 1)
                        .Msg(2, creatures)
                        .Var(5, -1)))
                    .Msg(3, MonsterLook(leader.Monster.Look)))
                .Var(3, group.MobId);
        }

        /// <summary>
        /// A monster's look as a group carries it: its colours, bones, scale and skins, the same
        /// block an NPC's look is (<see cref="BuildNpcLook"/>).
        /// </summary>
        /// <remarks>
        /// It used to be the bones alone, read off a belief that the captures sent nothing else.
        /// They do: of the 2,372 monster groups in the 756 captures' jss, 1,799 carry their scale
        /// (f5) and 113 their colours (f1) -- "{706|...|115}" goes out with f5 = 115. Without it the
        /// client draws every monster at 100: the Conde Kontatrás, "{2069|||150}", came out a third
        /// smaller than a person, and 2,478 other monsters with a scale of their own were drawn off
        /// it too.
        /// </remarks>
        internal static Pb MonsterLook(string look)
        {
            var variants = string.IsNullOrEmpty(look) ? null : Managers.Npcs.Variantes(look);
            if (variants == null || variants.Count == 0)
                return Pb.New().Var(2, LookKind).VarIfNotZero(3, BonesOf(look));

            var first = variants[0];
            var pb = Pb.New();
            if (first.Colors.Length > 0) pb.Packed(1, first.Colors);
            pb.Var(2, LookKind);
            pb.VarIfNotZero(3, first.Bones);
            if (first.Scales.Length > 0) pb.Packed(5, first.Scales);
            if (first.Skins.Length > 0) pb.Packed(6, first.Skins);
            return pb;
        }

        /// <summary>
        /// The map's NPCs.
        ///
        /// They go with the same envelope as the player and the monster groups, and the only thing
        /// telling them apart is that inside f2.f1 the f7 appears —the player uses f5 and a monster
        /// group f4—:
        ///
        ///   f1 { f1: cell, f2: orientation }
        ///   f2 { f1 { f7 { f3: gender, f5: template } }
        ///        f3 { f1: colours, f2: 3, f3: bones, f5: scales, f6: skins } }
        ///   f3: contextual id, negative
        ///
        /// The name is not sent: the client takes it from its data based on the template. And the
        /// look block is NpcTemplates's Look string split up, checked on the
        /// fifty-six NPCs of the capture without a single discrepancy.
        ///
        /// Mind the scale: f5 is a PACKED list of varints, not a loose byte. A
        /// scale of 200 is two bytes (c8 01), and writing the bare 0xC8 leaves a half varint
        /// that blows up the parsing of the whole jss in the client —with it, the map is left
        /// not drawn at all, neither NPCs nor monsters nor character—.
        /// </summary>
        private const int NpcFemale = 1;

        /// <summary>
        /// The marker over an NPC's head, written into its own actor record.
        /// </summary>
        /// <remarks>
        /// Its own method so the bytes can be pinned against the capture without a world loaded.
        /// Nothing is written when there is nothing to say: <c>1200</c>, an empty block, appears
        /// zero times in the 145 real frames that carry markers.
        /// </remarks>
        public static void AddQuestMarker(Pb identity, IReadOnlyList<int> offered, IReadOnlyList<int> doing)
        {
            if (offered.Count == 0 && doing.Count == 0) return;

            var marker = Pb.New();
            if (doing.Count > 0) marker.Packed(1, Longs(doing));
            if (offered.Count > 0) marker.Packed(3, Longs(offered));
            identity.Msg(2, marker);
        }

        private static List<long> Longs(IReadOnlyList<int> values)
        {
            var made = new List<long>(values.Count);
            foreach (int value in values) made.Add(value);
            return made;
        }

        private static void AddNpcs(Pb jss, long mapId)
        {
            // Who is asking, for the NPCs drawn in several ways. It is built ONCE per
            // map and only if needed: the resolver looks up the guild in the base, and doing it for
            // each NPC would be one query per actor on each map load.
            Jondo.Unity.World.Content.Criterion.Resolver quien = null;

            foreach (var npc in Managers.Npcs.Of(mapId))
            {
                long bones = npc.Bones;
                var skins = npc.Skins;
                var colors = npc.Colors;
                var scales = npc.Scales;

                if (npc.Variants.Count > 1)
                {
                    quien ??= Managers.GuildRaidManager.ResolverFor(GameState.CharacterId);
                    var otro = Managers.Npcs.VariantFor(npc, quien);
                    if (otro != null)
                    {
                        bones = otro.Bones;
                        skins = otro.Skins;
                        colors = otro.Colors;
                        scales = otro.Scales;
                    }
                }

                var look = Pb.New();
                if (colors.Length > 0) look.Packed(1, colors);
                look.Var(2, LookKind);
                look.VarIfNotZero(3, bones);
                if (scales.Length > 0) look.Packed(5, scales);
                if (skins.Length > 0) look.Packed(6, skins);

                // The gender only travels when it is 1. Checked on the fifty-six templates
                // of the capture: the twenty with gender 1 send it, the thirty-five with gender
                // 0 omit it —that is proto3— and the only one with gender 2, which is the kama mountain,
                // does not send anything either. So the field is not the template's gender as is,
                // but is only set when it is exactly 1.
                var template = Managers.Npcs.TemplateOf(npc.NpcId);
                bool female = template != null && template.Gender == NpcFemale;

                // THE HEAD MARK GOES IN HERE, and this is what was missing for the green
                // exclamation mark to ever come out.
                //
                // The iom does not draw it. The two things were told apart by comparing byte by byte the
                // same map and the same actor: in Ankama's jss of map 154010883, actor
                // -20000 (NPC 2892), there are six bytes that were not in ours:
                //
                //   Ankama  ...1217 0a0b3a09 12041a02e00c 28cc16 1a08...
                //   Jondo   ...1211 0a053a03            28cc16 1a08...
                //                            ^^^^^^^^^^^^
                //                            f2 { f3: packed[1632] }
                //
                // 1632 is exactly the quest that NPC hands out on that map. Outside those six
                // bytes -and the two lengths that grow with them- the frames are identical. The
                // same on 154010371 with NPC 2905 and quest 1639.
                //
                // And there is a capture, "sin apariencias equipar un escudo", that does NOT carry a single iom
                // in the whole flow and yet its NPCs come out marked: the mark cannot come
                // from the iom. The iom is something else -an index of the whole SUBZONE, which names maps
                // the player is not on-, and that is why the one following accepting quest 2432
                // names 2427: they are two different maps of the same subzone 980.
                //
                //   f3  the ones it OFFERS   -> the exclamation mark. 21 of 21 measured ids are quests
                //                              whose catalogue names THAT npc on THAT map.
                //   f1  the ones IN PROGRESS that want something from it.
                //
                // It goes before the gender, which is the order of all the captures: 12041a02e70c 1801
                // 28d916. And when there is nothing to say the block is not sent: the byte pair
                // 1200 does not appear once in the 145 real iom frames.
                var offered = Managers.Quests.OfferedRightNowBy(npc.NpcId, mapId);
                var doing = Managers.Quests.InProgressWith(npc.NpcId, mapId);

                var identity = Pb.New();
                AddQuestMarker(identity, offered, doing);
                identity.VarIfNotZero(3, female ? NpcFemale : 0).Var(5, npc.NpcId);

                jss.Msg(5, Pb.New()
                    .Msg(1, Pb.New().Var(1, npc.Cell).VarIfNotZero(2, npc.Orientation))
                    .Msg(2, Pb.New()
                        .Msg(1, Pb.New().Msg(7, identity))
                        .Msg(3, look))
                    .Var(3, npc.ContextualId));
            }
        }

        /// <summary>
        /// The map's graphic elements and, when there is one, their server action.
        ///
        ///   f11 { f1: 1, f4 { f1: skill uid, f2: skill }, f5: element, f6: type }
        ///   f15 { f1: state, f2: cell, f3: element }
        ///
        /// f11 says which element exists and what can be done with it. f15 is reserved
        /// for the subset that has a dynamic state. The element's number comes
        /// from the client's own data (<see cref="Managers.Interactives"/>), so the
        /// client already knows which drawing to give it and where.
        ///
        /// They go at the end, after the subzone, which is where the real capture puts them.
        /// </summary>
        private static void AddInteractiveElements(Pb jss, long mapId, long viewerAccountId)
        {
            foreach (var interactive in Managers.InteractiveRegistry.OnMap(mapId))
                Declare(jss, interactive, viewerAccountId);

            AddQuestElements(jss, mapId);
            AddReadableElements(jss, mapId);
        }

        /// <summary>
        /// What only whoever carries the quest sees: the trail, the spyglass, the sign.
        ///
        /// It goes apart from the registry on purpose. The registry belongs to the world and is the same for everyone —the
        /// zaap is there for anyone—, and this belongs to ONE player: the trail appears on taking the
        /// quest and goes away on meeting its objective. Putting it in the registry would have made the
        /// first false.
        ///
        /// Being able to ask per player here is not new: <see cref="Declare"/> already looks at the
        /// profession level of whoever looks at the map to decide whether a resource is offered to him or
        /// drawn in red. This jss is built once per player and per arrival on the map.
        ///
        /// The skill goes in f4 and without state, like everything that is not a resource. 114 is
        /// «Utiliser», the same one the client uses for the anomaly vestige, and of it the
        /// captures say the client answers with its iwo all the same.
        /// </summary>
        /// <summary>
        /// This map's signs and books, declared as pressable.
        /// </summary>
        /// <remarks>
        /// It has to be said. The server can answer a sign's click wonderfully and be
        /// of no use: if the element does not travel in the actor list with its skill, the
        /// client does not draw it as interactive and there is no click to answer. That is exactly what
        /// happened with the tavern's job offer.
        ///
        /// It goes apart from the quest ones because they depend on none: a sign is read with or without
        /// one, and that is why nothing is asked of the journal here. The skill is the same generic «Utiliser»
        /// the quest elements use.
        /// </remarks>
        private static void AddReadableElements(Pb jss, long mapId)
        {
            foreach (int elementId in Managers.Readables.OnMap(mapId))
            {
                var element = Managers.Interactives.ByElementId(mapId, elementId);
                if (element.Id == 0) continue;

                jss.Msg(11, Pb.New()
                    .Var(1, 1)
                    .Msg(4, Pb.New()
                        .Var(1, Managers.Interactives.SkillInstanceOf(elementId))
                        .Var(2, Jondo.Unity.World.Quests.QuestBinding.DefaultSkill))
                    .Var(5, elementId)
                    .Var(6, Jondo.Unity.World.Quests.QuestBinding.DefaultType));

                DeclarePlacement(jss, element, Managers.ResourceState.Full);
            }
        }

        private static void AddQuestElements(Pb jss, long mapId)
        {
            foreach (var binding in Managers.Quests.Bindings.OnMap(mapId))
            {
                if (!Managers.Quests.ShouldSee(binding)) continue;

                foreach (var (where, elementId) in binding.Elements)
                {
                    if (where != mapId) continue;

                    var element = Managers.Interactives.ByElementId(mapId, elementId);
                    if (element.Id == 0) continue;

                    jss.Msg(11, Pb.New()
                        .Var(1, 1)
                        .Msg(4, Pb.New()
                            .Var(1, Managers.Interactives.SkillInstanceOf(elementId))
                            .Var(2, binding.SkillId))
                        .Var(5, elementId)
                        .Var(6, binding.TypeId));

                    DeclarePlacement(jss, element, Managers.ResourceState.Full);
                }
            }
        }

        /// <summary>
        /// A map element: its identity, its type and its possible actions.
        ///
        /// Profession RESOURCES are declared differently depending on whether they are full or not, and it has to be
        /// respected or the client offers to reap an already reaped wheat:
        ///
        ///   full      f11 { f1:1, f2:0, f4 { uid, skill }, ... }   f15 without f4
        ///   depleted  f11 { f1:1,       f3 { uid, skill }, ... }   f15 f4 = 1
        ///   in use    same as depleted                             f15 f4 = 2
        ///
        /// That is, the skill moves from field 4 to 3 when it can no longer be used. Measured on
        /// the twenty-five ash trees of one same map, without an exception. Everything that is not a resource
        /// —zaaps, chests, doors— always goes in 4 and without state, as until now.
        /// </summary>
        private static void Declare(Pb jss, Managers.RegisteredInteractive interactive, long viewerAccountId)
        {
            bool gathering = Managers.Resources.Is(interactive.MapId, interactive.Element.Id);
            var state = gathering
                ? Managers.Resources.StateOf(interactive.MapId, interactive.Element.Id)
                : Managers.ResourceState.Full;

            // And the profession level of WHOEVER is looking at the map. A resource that is beyond him
            // is declared just like a depleted one, and the client draws it red and does not let it be clicked:
            // it is how the real game does it, without telling anybody anything through the chat.
            bool alcanza = !gathering || Managers.Resources.WithinReach(
                interactive.MapId, interactive.Element.Id);

            bool usable = !gathering || (state == Managers.ResourceState.Full && alcanza);

            var declaration = Pb.New().Var(1, 1);

            // f2 comes out at zero for wood, wheat and sage, and at 1 or 3 for the two
            // fishing spots. What distinguishes it has not been worked out, so zero goes, which is what was measured
            // in three of the four professions.
            if (gathering && usable) declaration.Var(2, 0);

            // A house's door and chests offer each viewer his own skills: the owner sells, a
            // stranger buys. Every other element offers all of them.
            foreach (var action in Handlers.HouseHandler.VisibleActions(interactive, viewerAccountId))
            {
                declaration.Msg(usable ? 4 : 3, Pb.New()
                    .Var(1, action.SkillInstanceId)
                    .Var(2, action.SkillId));
            }

            jss.Msg(11, declaration
                .Var(5, interactive.Element.Id)
                .Var(6, interactive.Type));

            // In the official capture, exits 515742/gfx3520 and 515801 have no f15.
            // f15 is a dynamic state, not the drawing's declaration. Passive elements and
            // routes are visible through their f11 and must therefore not receive this artificial
            // state, which kept the client from associating the sun with its map data.
            // Nothing belonging to the Dreams either. Measured and without exception: the 22 real jss
            // of map 238551040 and the 36 of the rooms have NO f15 — not for the well,
            // nor for the four arches, nor for the three doors of each room.
            //
            // And it is not a cosmetic detail: the comment above already says so for the
            // exit suns — an artificial f15 keeps the client from attaching the element to the
            // drawing of its own map data. That is what left the doors invisible
            // as long as the interactives key was not held down.
            //
            // A marketplace counter has none either: 22 declarations of 17 counters in the
            // captures, 212600837's equipment to 207625216's cosmetics, and not one f15 among them.
            bool sinColocacion = false;
            foreach (var action in interactive.Actions)
            {
                if (action.Kind == Managers.InteractiveActionKind.Teleport
                    || action.Kind == Managers.InteractiveActionKind.Dream
                    || action.Kind == Managers.InteractiveActionKind.DreamDoor
                    || action.Kind == Managers.InteractiveActionKind.Marketplace) sinColocacion = true;
            }
            if (interactive.Actions.Count == 0 || sinColocacion) return;

            DeclarePlacement(jss, interactive.Element,
                gathering && !usable ? state : Managers.ResourceState.Full);
        }

        /// <summary>
        /// Where an element is and in what state (f15).
        ///
        /// f15 ALWAYS accompanies an f11 and never goes alone. Measured over the 305 captures of the
        /// real game: in the 834 jss carrying elements there are 4,493 f11 and only 2,685 f15, and an
        /// f15 whose element has no f11 appears 3 times —only once in each—, that is
        /// 0.36 %. The other way round happens in 615 of the 834: an element with an action for which the server
        /// sends no placement.
        ///
        /// That is: f15 is a SUBSET of f11, not a superset. Declaring one for each
        /// element of the map —the 46,309 of interactive_elements.json, spread over 9,840 maps,
        /// up to 71 in the worst— would have Jondo sending the opposite of what Ankama sends.
        ///
        /// f1 says the element belongs to this map. The absence of f4 is the active state, which
        /// is why it is not written. The drawing does not travel: the client takes it from its own map
        /// data based on the element's number.
        /// </summary>
        private static void DeclarePlacement(Pb jss, Managers.Interactives.Element element,
                                             Managers.ResourceState state)
        {
            var placement = Pb.New()
                .Var(1, 1)
                .Var(2, element.Cell)
                .Var(3, element.Id);
            if (state != Managers.ResourceState.Full) placement.Var(4, (int)state);
            jss.Msg(15, placement);
        }

        /// <summary>
        /// Level of a spawned monster: the one the spawner rolled, or failing that the one its
        /// grade declares.
        /// </summary>
        /// <summary>
        /// The groups a dungeon room shows by team size, behind the leader in the same f2:
        ///
        ///   f3 { f1 (repeated): member { f1: id, f2: level, f4: grade }   f2: team size }
        ///
        /// Measured on the five rooms of the jalatós capture: one alternative for 1 player with
        /// the first four of the eight, then 5, 6, 7 and 8 with the first five to eight -- none
        /// for 2, 3 or 4, which fight the four of the first. Members with no look of their own,
        /// like the leader.
        /// </summary>
        internal static void AddAlternatives(Pb creatures, IReadOnlyList<Managers.MobSpawnManager.MobMember> members)
        {
            for (int size = Managers.MobSpawnManager.DungeonMinimum;
                 size <= Math.Min(members.Count, Managers.MobSpawnManager.DungeonGroupSize); size++)
            {
                var alternative = Pb.New();
                for (int i = 0; i < size; i++)
                {
                    alternative.Msg(1, Pb.New()
                        .Var(1, members[i].Monster.Id)
                        .VarIfNotZero(2, LevelOf(members[i]))
                        .VarIfNotZero(4, GradeOf(members[i])));
                }
                alternative.Var(2, size == Managers.MobSpawnManager.DungeonMinimum ? 1 : size);
                creatures.Msg(3, alternative);
            }
        }

        private static long LevelOf(Managers.MobSpawnManager.MobMember member)
        {
            if (member.Level > 0) return member.Level;

            var grades = member.Monster.Grades;
            if (member.GradeIndex >= 0 && member.GradeIndex < grades.Count) return grades[member.GradeIndex].Level;
            return 0;
        }

        /// <summary>Constant value of field 2 of a look block, the same in every capture.</summary>
        private const int LookKind = 3;

        /// <summary>
        /// An NPC's look as the actor block carries it: { f1: packed colours, f2: 3, f3: bones,
        /// f5: packed scales, f6: packed skins }. The same bytes the map's NPCs go out with.
        /// </summary>
        public static byte[] BuildNpcLook(long bones, long[] skins, long[] colors, long[] scales)
        {
            var look = Pb.New();
            if (colors.Length > 0) look.Packed(1, colors);
            look.Var(2, LookKind);
            look.VarIfNotZero(3, bones);
            if (scales.Length > 0) look.Packed(5, scales);
            if (skins.Length > 0) look.Packed(6, skins);
            return look.Build();
        }

        /// <summary>
        /// A monster's grade, from 1 to 5, or up to 6 if the monster declares six.
        ///
        /// The cap of five is what matters: in three hundred-odd wild monsters of the
        /// real captures the grade comes out 1, 2, 3, 4 or 5 and never more. Our data does not behave
        /// the same —4,098 monsters have five grades, but 479 have six, 169 have ten and one
        /// has twenty— and the generator picked any of them, so grades 6 and higher came out.
        ///
        /// The client takes that badly in silence: the group is drawn, but hovering over
        /// it shows nothing and the W key skips it. That is why the information was only seen for one
        /// or two of the four groups on the map.
        ///
        /// And the sixth, measured later: the kanojedo's level 200 Puch Ingball travels as
        /// <c>f2=200 f4=6</c> in two captures, and the client draws it and lets it be inspected, because its
        /// own data gives it six grades. The sixth only comes out when the monster has it, and
        /// nobody hands it to the generated ones; what is seen above that is still unmeasured.
        /// </summary>
        private const int MaxGrade = 5;
        private const int MaxDeclaredGrade = 6;

        private static long GradeOf(Managers.MobSpawnManager.MobMember member)
        {
            int declared = member.Monster?.Grades?.Count ?? 0;
            int top = declared >= MaxDeclaredGrade ? MaxDeclaredGrade : MaxGrade;
            return Math.Clamp(member.GradeIndex + 1, 1, top);
        }

        /// <summary>
        /// The bonesId out of the look the database stores for a monster, which comes in the
        /// client's own notation: "{4907|||130}", where the first number is the bones.
        /// </summary>
        private static long BonesOf(string look)
        {
            if (string.IsNullOrEmpty(look)) return 0;

            int start = look.IndexOf('{');
            if (start < 0) return 0;

            int end = start + 1;
            while (end < look.Length && char.IsDigit(look[end])) end++;

            return long.TryParse(look.Substring(start + 1, end - start - 1), out long bones) ? bones : 0;
        }

        // ─── World: spells ──────────────────────────────────────────────────────

        /// <summary>
        /// The spells the character has (hms), each at the grade its level has opened.
        ///
        ///   f1 (repeated) { f1: grade, f3: spell id, f4: 1 }
        ///
        /// The captured one belongs to a level 154 Sacrieur, which is why a level 50 Cra was
        /// holding somebody else's spells. Both the list and the grades come from the client's own
        /// data through <see cref="SpellTable"/>; only the breed and the level are ours.
        /// </summary>
        /// <summary>
        /// Where the spell comes from: 1 the class ones, 2 the ones that are not —melee,
        /// item ones, mount ones—.
        /// </summary>
        private const int OrigenQueNoEsDeClase = 2;

        /// <summary>
        /// Melee is SPELL ZERO, and it does not have to be invented: it is in the base as
        /// <c>SpellTemplates.Id 0</c>, with name 64658 —"Puñetazo"— and a single grade,
        /// <c>SpellLevels.Id 10461</c>, of 3 AP and range 1.
        ///
        /// The real server sends it as one more entry of the spell list, with the number
        /// omitted —proto3 does not write zeros— and the origin at 2: the bytes are <c>08 01 20 02</c>.
        /// It is in the nine captures that bring an hms, from the tutorial's level 1 character
        /// to the level 200 one. Without it the client has no token to put in the weapon
        /// slot: out of combat it does not draw it, and in combat it draws it but with nothing to cast
        /// —hence it came out greyed and with the item text unresolved, with the rows
        /// "[QUANTITÉ EN INVENTAIRE]" and "[Valeurs théoriques]"—.
        /// </summary>
        private const int GradoDelCuerpoACuerpo = 1;

        public static byte[] BuildSpellList(int breed, int level, long accountId = 0)
        {
            var hms = Pb.New();

            hms.Msg(1, Pb.New().Var(1, GradoDelCuerpoACuerpo).Var(4, OrigenQueNoEsDeClase));

            foreach (var spell in SpellTable.KnownFor(breed, level, Managers.SpellChoices.Chosen))
            {
                hms.Msg(1, Pb.New().Var(1, spell.Grade).Var(3, spell.SpellId).Var(4, 1));
            }

            // And the administration ones, which go after and only for whoever is one. The role is checked
            // against the base each time: taking it away from someone takes effect as soon as he comes back
            // in, with nothing stored in the session going stale.
            if (Managers.AdminSpells.Para(accountId))
            {
                hms.Msg(1, Pb.New()
                    .Var(1, Managers.AdminSpells.GradoDeDoom)
                    .Var(3, Managers.AdminSpells.DoomDeMasas)
                    .Var(4, 1));
            }

            // The loose f2 at the end, which is the same kind of oversight the spell bar
            // had with its itg: it goes after the list, looks like one more slot and without it is zero.
            //
            // It is in the NINE captures that bring an hms, from the tutorial's level 1
            // character to the level 200 one, and it did not come out in any of the 138 this
            // emulator has sent. What it is suspected to switch off is the damage preview: the client
            // carries inside a switch called isDamagePreviewEnabled, and the game's own help
            // text describes in a single sentence the two halves that were missing —the estimated
            // damage and the displacement—. It is not proven that this is the switch; what
            // is measured is that the real server always sends it and we never did.
            return hms.Var(2, 1).Build();
        }

        /// <summary>
        /// The shortcut bar (itg). The real server sends two of them, one for the spells and one
        /// for the items; this is the spell one.
        ///
        ///   f1 (repeated) { f2: slot, f6 { f2: spell id } }
        ///   f2: 1        ← WHICH BAR IT IS
        ///
        /// That f2 at the end is what had the bar empty. It goes loose at the end of the list and is not
        /// seen reading the tree at a glance, because it looks like one more slot; it is the bar type, and without
        /// it it is zero, which is the item one. The client received thirty-four spells
        /// declared as shortcuts of the item bar and drew them in neither of the two.
        /// The item one, which really is type zero, does not carry it.
        ///
        /// The slot is left out when it is zero, as proto3 does everywhere else. The client edits
        /// a slot with itz —f2 the shortcut, f3 the bar— and the server echoes it in ivk.
        /// </summary>
        public static byte[] BuildSpellBar(int breed, int level, long accountId = 0)
        {
            var layout = Managers.FightSpellLayout.Current(breed, level, accountId);

            var remembered = new List<(int Slot, int SpellId)>();
            foreach (var (slot, spell) in layout.Bar)
            {
                if (spell != Network.FightProtocol.HechizoCuerpoACuerpo)
                    remembered.Add((slot, spell));
            }
            Managers.SpellChoices.RememberBar(remembered);

            var itg = Pb.New();
            foreach (var (slot, spell) in layout.Bar)
            {
                var shortcut = Pb.New().VarIfNotZero(2, slot);
                if (spell == Network.FightProtocol.HechizoCuerpoACuerpo) shortcut.EmptyMsg(6);
                else shortcut.Msg(6, Pb.New().Var(2, spell));
                itg.Msg(1, shortcut);
            }

            return itg.Var(2, SpellBar).Build();
        }

        /// <summary>Which bar it is: 0 the item one, 1 the spell one.</summary>
        public const int SpellBar = 1;

        /// <summary>
        /// The spell that replaces its pair (hng), and the bar slot where it ends up (iuq).
        ///
        /// Read from four real captures of changing variant, from the panel and from the bar:
        ///
        ///   client   hmt { f1: the spell it wants }
        ///   server   iuq { f2 { f2: slot, f6 { f2: spell } }, f3: which bar }   one per slot
        ///   server   hng { f2: spell, f3: grade }
        ///
        /// The iuq go first and there is one for each slot the old half had: in the capture
        /// of Liberación por Magnetismo two came out, because the spell was placed twice.
        /// </summary>
        public static byte[] BuildSpellSwapped(int spellId, int grade)
            => Pb.New().Var(2, spellId).Var(3, grade).Build();

        public static byte[] BuildShortcutChanged(int slot, int spellId)
            => Pb.New()
                .Msg(2, Pb.New().VarIfNotZero(2, slot).Msg(6, Pb.New().Var(2, spellId)))
                .Var(3, SpellBar)
                .Build();

        /// <summary>How many slots of the bar we fill. The captured one runs from 0 to 48.</summary>

        // ─── World: weight carried ──────────────────────────────────────────────

        /// <summary>
        /// Pods (iun): f1 what the character is carrying, f3 what it can carry.
        ///
        /// Identified by arithmetic rather than by name: distributing five points of strength
        /// moved f3 by exactly 25, and characteristic 40 by the same 25. Five pods a point of
        /// strength is the game's own rule, and f3 is a thousand above characteristic 40, which
        /// is the base every character has.
        /// </summary>
        public static byte[] BuildPods(long carried, long capacity)
            => Pb.New().VarIfNotZero(1, carried).VarIfNotZero(3, capacity).Build();

        // ─── Gathering ──────────────────────────────────────────────────────────

        /// <summary>
        /// A resource's state (iwf): { f1 { f2: cell, f3: element, f4: state } }.
        ///
        /// Zero is full and the real server does not send the field; 1 depleted and 2 in use. They are the
        /// same field numbers as the jss's f15, without its f1.
        /// </summary>
        public static byte[] BuildElementState(int cell, int elementId, int state)
            => Pb.New().Msg(1, Pb.New()
                .Var(2, cell)
                .Var(3, elementId)
                .VarIfNotZero(4, state)).Build();

        /// <summary>
        /// Declares a resource again (iwm) so that its skill can no longer be used, or
        /// can again: { f3 { the same shape as the jss's f11 } }.
        ///
        /// It is the message that switches off the freshly reaped wheat without having to resend the whole map.
        /// </summary>
        public static byte[] BuildElementRedeclared(int skillInstanceId, int skillId,
                                                    int elementId, int type, bool usable)
        {
            var declaration = Pb.New().Var(1, 1);
            if (usable) declaration.Var(2, 0);
            declaration.Msg(usable ? 4 : 3, Pb.New().Var(1, skillInstanceId).Var(2, skillId));
            return Pb.New().Msg(3, declaration.Var(5, elementId).Var(6, type)).Build();
        }

        /// <summary>
        /// The gathering gesture (iwn): { f2: element, f3: tenths, f4: skill, f5: who }.
        ///
        /// CAREFUL, it is not the same iwn as that of using a zaap or a workshop. That one carries f1 = 1 and does not
        /// carry a duration; this one is the other way round: without f1 and with f3. Measured in the four profession
        /// captures, and f3 is 30 in all four —three seconds— with the real time between this
        /// message and the end one measuring 2,996, 2,999, 3,037 and 3,064 milliseconds.
        /// </summary>
        public static byte[] BuildGatherStarted(int elementId, int tenths, int skillId,
                                                long characterId)
            => Pb.New()
                .Var(2, elementId)
                .Var(3, tenths)
                .Var(4, skillId)
                .Var(5, characterId)
                .Build();

        /// <summary>The gesture is over (iwi): { f1: element, f3: skill }.</summary>
        public static byte[] BuildGatherFinished(int elementId, int skillId)
            => Pb.New().Var(1, elementId).Var(3, skillId).Build();

        /// <summary>What was gathered in this pass (itn): { f1: item, f2: quantity }.</summary>
        public static byte[] BuildGathered(int itemId, int quantity)
            => Pb.New().Var(1, itemId).Var(2, quantity).Build();

        /// <summary>
        /// A profession's experience (irq): { f1 { f1: profession, f2: next level, f3: level,
        /// f4: level floor, f5: accumulated } }.
        ///
        /// It sends TOTALS, not increments. f2 disappears when the profession is at the cap, which is
        /// how the level 200 lumberjack came out in the wood capture.
        /// </summary>
        public static byte[] BuildJobExperience(int jobId, long next, int level, long floor,
                                                long experience)
            => Pb.New().Msg(1, Pb.New()
                .Var(1, jobId)
                .VarIfNotZero(2, next)
                .VarIfNotZero(3, level)
                .VarIfNotZero(4, floor)
                .VarIfNotZero(5, experience)).Build();

        /// <summary>
        /// Several jobs in one irq, the way the entry into the world lists them all: one f1 entry
        /// each, the same five fields as <see cref="BuildJobExperience"/>.
        /// </summary>
        public static byte[] BuildJobsExperience(IEnumerable<(int JobId, long Next, int Level, long Floor, long Experience)> jobs)
        {
            var irq = Pb.New();
            foreach (var (jobId, next, level, floor, experience) in jobs)
                irq.Msg(1, Pb.New()
                    .Var(1, jobId)
                    .VarIfNotZero(2, next)
                    .VarIfNotZero(3, level)
                    .VarIfNotZero(4, floor)
                    .VarIfNotZero(5, experience));
            return irq.Build();
        }

        /// <summary>Changes the quantity of an item that was already in the bag (ivj).</summary>
        public static byte[] BuildItemQuantity(long uid, int total)
            => Pb.New().Msg(3, Pb.New().Var(2, uid).Var(3, total)).Build();

        // ─── World: apariencia ──────────────────────────────────────────────────

        /// <summary>
        /// A character's block within the map: where he is, who he is and what he looks like.
        ///
        ///   f1 { f1: cell, f2: which way he faces }
        ///   f2 { f1 { f5: name and account }, f3: the look }
        ///   f3: the identifier
        ///
        /// It is the same block in two places: repeated in the jss's f5, which is the whole map, and
        /// loose inside the jsn, which is a single actor. That is why it is here and not inside either of
        /// the two.
        /// </summary>
        private static Pb PlayerActor(DatabaseManager.DbCharacter character, int cell, int facing,
                                     long accountId)
        {
            // The order and the fields are those of a real jsn with a title on:
            //
            //   f1 { f2: 3, f5: level }      f3: the account
            //   f5 (repeated): the options — guild, title, ornament, and the f7:1 that always goes
            //   f6: 1                        f7: 0x0b
            //
            // f1, f5{f7:1} and f7 were missing, and without them the client did not draw the title nor
            // the ornament on hovering over.
            var cuerpo = Pb.New()
                .Msg(1, Pb.New().Var(2, HumanKind).VarIfNotZero(5, character.Level))
                .Var(3, accountId);

            AddCharacterOptions(cuerpo, character.Id);

            cuerpo.Msg(5, Pb.New().Var(7, 1));
            cuerpo.Var(6, 1);
            cuerpo.Bytes(7, HumanTrailer);

            var humanoid = Pb.New()
                .Str(1, character.Name)
                .Msg(3, cuerpo);

            return Pb.New()
                .Msg(1, Pb.New().Var(1, cell).Var(2, facing))
                .Msg(2, Pb.New()
                    .Msg(1, Pb.New().Msg(5, humanoid))
                    .Bytes(3, BreedLookTable.BuildLook(
                        character.Breed, character.Sex, character.HeadId, null, character.Id)))
                .Var(3, character.Id);
        }

        /// <summary>Serialized actor block used by map snapshots outside this protocol builder.</summary>
        public static byte[] BuildPlayerActorBlock(DatabaseManager.DbCharacter character, int cell,
                                                   int facing, long accountId)
            => PlayerActor(character, cell, facing, accountId).Build();

        /// <summary>
        /// "This actor has changed" (jsn), which is what redraws the character on the map.
        ///
        /// The lxc does not do for this. In the capture of equipping a dragoturkey both come out, and they are
        /// different things: the lxc carries a UUID that appears nowhere else in the flow —neither
        /// in the jss, nor in any jsn— while the jsn carries the complete actor block, with
        /// its cell, its identifier and the new look with the mount's bones. The client
        /// draws what the jsn tells it.
        ///
        /// Sending only the lxc, the inventory doll found out and the map figure did not: one
        /// stayed mounted on the previous dragoturkey however much the mount was changed or
        /// all of them were taken off.
        ///
        ///   jsn f1 { the actor block }
        ///
        /// The real server sends three in a row; one is enough.
        /// </summary>
        public static byte[] BuildActorRefreshed(DatabaseManager.DbCharacter character, int cell,
                                                 int facing, long accountId)
            => Pb.New()
                .Msg(1, PlayerActor(character, cell, facing, accountId))
                .Build();

        /// <summary>
        /// "Your look has changed" (lxc), which is what the server sends on equipping something.
        ///
        ///   f1: an identifier shaped like a UUID
        ///   f2: the new look
        ///
        /// The UUID comes out the same in all the lxc of one same session and changes between characters, so
        /// it seems to identify the look's owner. Where the client learns it has not been found
        /// —in the jss the only UUID there is is an alliance's, not this one— so here it is
        /// derived from the character's id: constant for him and different from anyone else's. If the
        /// client does not check it, it does not matter; if it checks it, at least it is consistent.
        /// </summary>
        public static byte[] BuildLookChanged(DatabaseManager.DbCharacter character)
            => Pb.New()
                .Str(1, LookIdOf(character.Id))
                .Bytes(2, BreedLookTable.BuildLook(
                    character.Breed, character.Sex, character.HeadId, null, character.Id,
                    paraLaVentana: true))
                .Build();

        /// <summary>
        /// The appearance window's state (lxo), the answer to the lyy.
        ///
        ///   f1: 1
        ///   f3 { f3: when, f5: breed, f7: preview uuid, f8: 3, f10: title,
        ///        f11: level, f12: the look, f15: -1, f16: ornament,
        ///        f17 (repeated) { f1: slot, f2 { f2: garment } } }
        ///
        /// f7 is the same uuid the preview's lxc carries, and f12 its same look:
        /// that is how the panel knows that what reaches it is its own.
        /// </summary>
        public static byte[] BuildAppearanceState(DatabaseManager.DbCharacter character, string draftId)
            => Pb.New().Var(1, 1).Bytes(3, AppearanceBody(character, draftId)).Build();

        /// <summary>
        /// A wardrobe OUTFIT: the same block that goes inside the lxo, and that is why it is here
        /// apart. The lyt repeats it —one per outfit stored in f1, and in f2 the one that is
        /// on— and it is what the appearance panel needs to be able to open.
        /// </summary>
        private static byte[] AppearanceBody(DatabaseManager.DbCharacter character, string draftId)
        {
            var (title, ornament) = Managers.Wardrobe.Of(character.Id);

            var body = Pb.New();

            // The outfit's colours, bare and without an index. Without this the client blows up on opening
            // the cosmetics window: its own log says so, ColorSet..ctor with the null
            // list, inside the lyt handler.
            var colores = BreedLookTable.PlainColors(character.Breed, character.Sex, null);
            if (colores.Count > 0) body.Msg(1, Pb.New().Packed(2, colores));

            body
                .Str(3, DateTimeOffset.UtcNow.ToString("yyyy-MM-ddTHH:mm:ss.fffffffZ"))
                .VarIfNotZero(5, character.Breed)
                .Str(7, draftId)
                .Var(8, AppearanceStateKind)
                .VarIfNotZero(10, title)
                .VarIfNotZero(11, character.Level)
                .Bytes(12, BreedLookTable.BuildLook(
                    character.Breed, character.Sex, character.HeadId, null, character.Id,
                    paraLaVentana: true))
                .Var(15, -1)
                .VarIfNotZero(16, ornament);

            foreach (var worn in Managers.Wardrobe.AppearanceOf(character.Id))
            {
                body.Msg(17, Pb.New()
                    .VarIfNotZero(1, worn.Slot)
                    .Msg(2, Pb.New().Var(2, worn.Gid)));
            }

            return body.Build();
        }

        /// <summary>
        /// The wardrobe outfits (lyt), which arrive on entering the world.
        ///
        ///   f1 (repeated): each stored outfit     f2: the one being worn
        ///
        /// IT HAS TO BE SENT NO MATTER WHAT. Without it, the client opens the cosmetics window, makes its sound, and is
        /// left undrawn: it blows up in CosmeticUi.DisplayOutfit with a null reference because it does not
        /// have any outfit to show. It was seen in its own Player.log.
        ///
        /// Only one goes here, that of the character playing, with his real look and garments. The
        /// capture brought two, but they were the recorded account's and that is why it stopped being resent.
        /// </summary>
        public static byte[] BuildOutfits(DatabaseManager.DbCharacter character)
        {
            byte[] conjunto = AppearanceBody(character, OutfitIdOf(character.Id));
            return Pb.New().Bytes(1, conjunto).Bytes(2, conjunto).Build();
        }

        /// <summary>The outfit's own uuid, different from the preview's.</summary>
        private static string OutfitIdOf(long characterId) => LookIdOf(characterId * 17 + 3);

        /// <summary>The state's f8, constant in the twenty captures where it appears.</summary>
        private const int AppearanceStateKind = 3;

        /// <summary>A stable UUID from the character's id.</summary>
        public static string LookIdOf(long characterId)
        {
            var bytes = new byte[16];
            BitConverter.GetBytes(characterId).CopyTo(bytes, 0);
            BitConverter.GetBytes(characterId * 2654435761L).CopyTo(bytes, 8);
            return new Guid(bytes).ToString();
        }

        // ─── World: zaaps ───────────────────────────────────────────────────────

        /// <summary>
        /// "That element is in use" (iwn), the immediate answer to the click on a zaap.
        ///
        ///   f1: 1, f2: THE ELEMENT, f4: the skill, f5: who uses it
        ///
        /// f2 is the element, not the skill instance identifier. It is seen by crossing
        /// the iwn with the iwo that causes it in the same capture:
        ///
        ///   iwo  f1: 14110  f2: 538795
        ///   iwn  f1: 1      f2: 538795   f4: 114
        ///
        /// The client sends both numbers and the server returns the second. Sending it the
        /// first leaves the client marking as busy an element that does not exist.
        /// </summary>
        public static byte[] BuildElementInUse(int elementId, int skillId, long who)
            => Pb.New()
                .Var(1, 1)
                .Var(2, elementId)
                .Var(4, skillId)
                .Var(5, who)
                .Build();

        /// <summary>
        /// An interactive has finished being used (iwi). Same shape as the end of gathering: f1 is
        /// the element and f3 the skill.
        ///
        /// A teleport is instant, but it has to be released all the same BEFORE the jru: otherwise, the
        /// client can be left with the element marked as busy in its cache and on coming back
        /// to the map its graphic no longer appears.
        /// </summary>
        public static byte[] BuildInteractiveUseEnded(int elementId, int skillId)
            => Pb.New().Var(1, elementId).Var(3, skillId).Build();

        /// <summary>A destination of the zaap list.</summary>
        public readonly struct ZaapDestination
        {
            public ZaapDestination(long mapId, int subAreaId, int level, long cost,
                                   int kind = 0, int minutesLeft = 0, int duration = 0)
            {
                MapId = mapId; SubAreaId = subAreaId; Level = level; Cost = cost;
                Kind = kind; MinutesLeft = minutesLeft; Duration = duration;
            }

            public long MapId { get; }
            public int SubAreaId { get; }
            public int Level { get; }
            public long Cost { get; }

            /// <summary>
            /// Which tab the client puts it in: 0 the zaap, 1 the zaapi, 4 the anomaly.
            ///
            /// The normal zaap does not send the field —proto3 swallows the zero— and that is why for a
            /// while it seemed not to exist. It appears in the 69 entries of the zaapi captures
            /// with value 1 and in the 27 anomaly ones with value 4.
            /// </summary>
            public int Kind { get; }

            /// <summary>Minutes the anomaly has left. Only anomalies carry it.</summary>
            public int MinutesLeft { get; }

            /// <summary>Minutes it lasts. Zero in everything that is not an anomaly.</summary>
            public int Duration { get; }
        }

        /// <summary>
        /// The zaap list (hjj).
        ///
        ///   f2: the map where the opened zaap is
        ///   f3 (repeated) { f1: zone level, f2: what it costs, f3: tab,
        ///                   f4 { f2: minutes left, f3: minutes it lasts },
        ///                   f5: map, f6: subzone }
        ///
        /// The destination one is already at travels without f2, which in proto3 is zero: going where you already
        /// are costs nothing. Checked against the twenty-five entries of the capture, where
        /// f6 matches MapPositions's subzone in all of them.
        ///
        /// The three tabs go in this same list, not in different messages: f3 says which one
        /// each entry falls in and the entry's f4 is only carried by anomalies, which expire. See
        /// <see cref="Managers.Anomalies"/>.
        ///
        /// ─── The ROOT's f4: which window the client opens ───────────────────────────────────
        ///
        /// Sending the right destinations is not enough: the client decides WHICH WINDOW it draws by this
        /// field, and not by the type of the element clicked. It is 0 for the zaap —proto3 swallows
        /// it and it does not travel—, 1 the zaapi and 3 the boat. It comes out the same in the twelve captured lists:
        ///
        ///   3 zaapi captures      f4 = 1, and without f2
        ///   8 zaap captures       without f4, and with f2
        ///   1 boat capture        f4 = 3 AND f2: the two fields are independent
        ///
        /// Without this 1, on clicking a zaapi the client opens the ZAAP window —tabs Zaap,
        /// Anomalía and Prisma— and since all the destinations reaching it are zaapi ones, none of
        /// those tabs picks them up and the window comes out with «Ningún destino». The zaapi one is different:
        /// it is titled «Zaapi» and its tabs are Talleres, Mercadillos and Varios.
        ///
        /// It goes at the END of the message, after all the destinations, which is where the
        /// real server puts it.
        /// </summary>
        public static byte[] BuildZaapList(long here, IEnumerable<ZaapDestination> destinations,
                                           int teleporter = 0)
        {
            var hjj = Pb.New().VarIfNotZero(2, here);
            foreach (var destination in destinations)
            {
                var entry = Pb.New()
                    .VarIfNotZero(1, destination.Level)
                    .VarIfNotZero(2, destination.Cost)
                    .VarIfNotZero(3, destination.Kind);

                // The clock, only if it has one: the real server does not send it for the zaap and the zaapi.
                if (destination.Duration > 0)
                {
                    entry.Msg(4, Pb.New()
                        .VarIfNotZero(2, destination.MinutesLeft)
                        .Var(3, destination.Duration));
                }

                hjj.Msg(3, entry
                    .Var(5, destination.MapId)
                    .VarIfNotZero(6, destination.SubAreaId));
            }

            return hjj.VarIfNotZero(4, teleporter).Build();
        }

        /// <summary>The kamas the character has left (ivf).</summary>
        public static byte[] BuildKamas(long kamas) => Pb.New().Var(1, kamas).Build();

        /// <summary>
        /// "Close the dialogue" (kld).
        ///
        /// The client does NOT close the zaap window on its own: it waits for the server to tell it
        /// to. In the captures it appears twice with the same value —on reaching the destination, right
        /// before the jss, and as the answer to the empty kla the close button sends— so
        /// f1 is a fixed reason and not something that has to be computed.
        /// </summary>
        public static byte[] BuildDialogClosed(int reason = DialogCloseReason)
            => Pb.New().Var(1, reason).Build();

        private const int DialogCloseReason = 10;

        /// <summary>
        /// The reason an NPC's dialogue is closed with, which is not the zaap's.
        ///
        /// In the tournament server capture the kld that closes the conversation with the kama
        /// mountain carries f1: 1, and it appears all four times —both on accepting and on declining—. 10
        /// is the zaap's and there is a 5 measuring another window, so they are different closing reasons
        /// and not a fixed value; the rule that separates them has not been deciphered.
        /// </summary>
        public const int NpcDialogCloseReason = 1;

        // ─── World: NPCs, their dialogue and their shops ────────────────────────

        /// <summary>
        /// The server opens the dialogue window (ioc). It only returns who is being spoken to
        /// and where; it carries neither f1 nor f2 nor f3.
        ///
        ///   f4: map      f5: the NPC's contextual id
        /// </summary>
        public static byte[] BuildNpcDialog(long mapId, long contextualId)
            => Pb.New().Var(4, mapId).Var(5, contextualId).Build();

        /// <summary>
        /// The question and its replies (ios).
        ///
        ///   f1: message id
        ///   f2 (repeated) { f1: reply id, f3 (repeated) { f1: effect id } }
        ///
        /// Each reply's f3 is what the reply promises: at the kama mountain the one that
        /// pays announces effects 194 ("+#1{{~1~2 a }}#2 kamas"), 193 and 351, and the one that declines goes
        /// with none. Here it is sent without f3, which is how the decline reply travels in the
        /// capture: the little prize icon is lost and nothing else. The effect ids are not in
        /// NpcTemplates, so putting them would be inventing them.
        /// </summary>
        public static byte[] BuildNpcQuestion(long messageId, IEnumerable<long> replies)
            => BuildNpcQuestion(messageId, replies, null);

        /// <summary>
        /// The same question, with the PARAMETERS some replies carry inside.
        /// </summary>
        /// <remarks>
        /// A reply is not always just an identifier: it can bring numbers the client
        /// puts in its text. The fountain of the Infinite Dreams is the clear case —the Rey Gob—, and in
        /// the long capture its reply travels like this:
        ///
        ///   f2 { f1: 82314, f3 { f1: 4037 }, f3 { f1: 4035 } }
        ///
        /// f3 is repeated and each one carries a number. Without them the reply is offered all the same,
        /// but bare: the client draws the number's slot empty.
        /// </remarks>
        public static byte[] BuildNpcQuestion(long messageId, IEnumerable<long> replies,
                                              IReadOnlyDictionary<long, IReadOnlyList<long>> parametros)
        {
            var ios = Pb.New().Var(1, messageId);
            foreach (long reply in replies)
            {
                var una = Pb.New().Var(1, reply);

                if (parametros != null && parametros.TryGetValue(reply, out var suyos))
                {
                    foreach (long valor in suyos) una.Msg(3, Pb.New().Var(1, valor));
                }

                ios.Msg(2, una);
            }
            return ios.Build();
        }

        /// <summary>
        /// A shop's whole catalogue (kbd), in one go.
        ///
        ///   f1 (repeated) { f1: item
        ///                   f3 { f2: price, f3: -1, f4: criterion }
        ///                   f4 (repeated): an effect, with the id in f11 }
        ///   f2: the same contextual id the iov asked for
        ///
        /// It is not paginated: in the capture there are fifty-six shop iov and fifty-six
        /// kbd, one to one, and the biggest is 26,902 bytes with 444 entries. Since there is not a single case of
        /// two kbd for one same shop, there is no proof either that the client knows how to join them, so
        /// the split into small sellers the real server does is also the safe thing.
        ///
        /// f3.f3 is -1 in the 6,106 measured entries. It is sent all the same even though what it
        /// means exactly is not known: the only sure thing is that the client always receives it that way.
        ///
        /// The text criterion —"(SC=3|Sc=3500)" on the tournament server— is NOT sent. SC=3
        /// is "tournament server" and the server itself validates it: the fourteen entries with the
        /// hardest criterion gave error 243 on buying them. Here we are not a tournament server, and
        /// there are twelve measured entries travelling with no criterion at all, so it is omitted.
        ///
        /// f3 is THE CURRENCY, and with it a shop charges in an item instead of in kamas. Nothing
        /// has to be invented: the client already knows how to do it. Measured over the 305 captures, there are 60
        /// kbd and 58 carry only f1 and f2 —those charge in kamas—; the other two also carry
        /// f3, with the id of the item acting as currency:
        ///
        ///   f3 = 13052   «Sebuscalón»   (the Travellers' Tower shop)
        ///   f3 = 30529   «Fidelicha»    (one in Pandala)
        ///
        /// If f3 is not there, it charges in kamas, which is why it goes with VarIfNotZero: a normal
        /// shop still sends exactly the same bytes as before.
        ///
        /// MIND THE PRICE. The whole shop is received and not only the currency's id, and it is on
        /// purpose: the first version sent f3 with the token but kept putting in each
        /// entry the price IN KAMAS, so the client showed a cape at «1 token» and on
        /// buying it the server charged 150. The price shown and the price charged have
        /// to come from the same place, and that is why both come from here.
        /// </summary>
        public static byte[] BuildShop(long contextualId, IEnumerable<int> gids,
                                       Managers.TokenShops.Shop? tokenShop = null)
        {
            var kbd = Pb.New();
            foreach (int gid in gids)
            {
                var entry = Pb.New()
                    .Var(1, gid)
                    .Msg(3, Pb.New()
                        .VarIfNotZero(2, tokenShop == null
                            ? Managers.NpcShops.PriceOf(gid)
                            : Managers.TokenShops.PriceOf(tokenShop, gid))
                        .Var(3, ShopUnlimited));

                foreach (var effect in Managers.Equipment.ParseEffects(Managers.NpcShops.EffectsOf(gid)))
                {
                    var value = EffectEntry(effect);
                    if (value != null) entry.Msg(4, value);
                }

                kbd.Msg(1, entry);
            }
            return kbd.Var(2, contextualId).VarIfNotZero(3, tokenShop?.TokenGid ?? 0).Build();
        }

        /// <summary>The shop's stock. Constant in the 6,106 entries of the capture.</summary>
        private const int ShopUnlimited = -1;

        /// <summary>
        /// The shop has been closed (khd). f3 is 11 in all fifty-six of the capture.
        /// </summary>
        public static byte[] BuildShopClosed() => Pb.New().Var(3, ShopClosedKind).Build();

        private const int ShopClosedKind = 11;

        /// <summary>
        /// An information message (lqn), which is HOW THE PLAYER IS SPOKEN TO.
        ///
        ///   f1: the type          f2: which message     f4 (repeated): its parameters
        ///
        /// The server does not send text: it sends two numbers and the client supplies the sentence, already translated,
        /// taking it from InfoMessagesDataRoot. The type decides how it draws it —0 information, 1 warning—
        /// and proto3 swallows the zero, which is why in the captures some lqn carry f1 and others
        /// do not. See <see cref="Managers.InfoMessages"/>.
        ///
        /// This and not a chat line: the chat goes out through the general channel and everybody reads it.
        /// </summary>
        public static byte[] BuildSystemMessage(int messageId, params string[] parameters)
            => BuildInfoMessage(Managers.InfoMessages.Info, messageId, parameters);

        /// <summary>
        /// A line of free text as an information message (lqn), only to the one it is for: the
        /// answer of a command, or what a window we have no measured frame for would have said.
        /// Before this those went as a chat line in the player's own name, on the channel they
        /// wrote in -- the general one, most of the time -- so it read as them talking.
        /// </summary>
        public static byte[] BuildNotice(string text)
            => BuildInfoMessage(Managers.InfoMessages.Info, Managers.InfoMessages.FreeText, text ?? "");

        /// <summary>
        /// "{0} acaba de volver a conectarse al combate." (lqn, type 1, text 184). The real
        /// server sends it right behind the lqu of the tactical map when somebody reconnects
        /// into his fight, in both reconnection captures, before the lva.
        /// </summary>
        public static byte[] BuildBackInTheFight(string name)
            => BuildInfoMessage(Managers.InfoMessages.Warning, Managers.InfoMessages.BackInTheFight, name);

        /// <summary>The same, saying what type it is.</summary>
        public static byte[] BuildInfoMessage(int type, int messageId, params string[] parameters)
        {
            var lqn = Pb.New().VarIfNotZero(1, type).VarIfNotZero(2, messageId);
            foreach (string parameter in parameters) lqn.Str(4, parameter);
            return lqn.Build();
        }

        /// <summary>
        /// The last connection notice, with its date and the address it was made from.
        ///
        /// The client has two templates and the difference is the IP:
        ///
        ///   193  «Última conexión a esta cuenta realizada el {2}/{1}/{0} a las {3}:{4}»
        ///   152  the same «… mediante la dirección IP {5}»
        ///
        /// The parameters go in the order year, month, day, hour, minute and address — the template's
        /// order is not the reading order, and the recorded block confirms it: it sends 193 with
        /// ["2026","08","09","18","53"] and the client draws «09/08/2026 a las 18:53».
        ///
        /// Without an IP 193 is sent, which is exactly what the real server does when it does not
        /// have it: showing an empty address looks worse than not showing it.
        /// </summary>
        public static byte[] BuildLastConnection(DateTimeOffset when, string ip)
        {
            string[] cuando =
            {
                when.Year.ToString("D4"),
                when.Month.ToString("D2"),
                when.Day.ToString("D2"),
                when.Hour.ToString("D2"),
                when.Minute.ToString("D2"),
            };

            if (string.IsNullOrWhiteSpace(ip))
                return BuildSystemMessage(LastConnectionMessage, cuando);

            var conIp = new string[6];
            Array.Copy(cuando, conIp, 5);
            conIp[5] = ip;
            return BuildSystemMessage(LastConnectionWithIpMessage, conIp);
        }

        /// <summary>«Última conexión… a las {3}:{4}», without an address.</summary>
        public const int LastConnectionMessage = 193;

        /// <summary>The same, «… mediante la dirección IP {5}».</summary>
        public const int LastConnectionWithIpMessage = 152;

        /// <summary>The "you have received kamas" message, with the figure as a parameter.</summary>
        public const int KamasReceivedMessage = 45;

        /// <summary>The "bought" one: item, uid, quantity and price.</summary>
        public const int PurchaseMessage = 252;

        /// <summary>
        /// The same notice but when it was paid in tokens. Measured in the Travellers' Tower
        /// shop: six parameters, «798, 1055401001, 1, 20, 13052, 0», which are the item
        /// bought and its uid, the quantity, the price, and the currency's id and uid.
        /// </summary>
        public const int TokenPurchaseMessage = 364;

        // ─── World: changing map ────────────────────────────────────────────────

        /// <summary>
        /// Removes an actor from the map (jsd): who is leaving and WHICH WAY.
        ///
        /// The which way was missing, and it is what left the character standing on the edge on the
        /// others' screens instead of disappearing. The notice reached them —it is measured in the
        /// server log— and the client did nothing with it.
        ///
        /// The capture of a party following the leader through nearby maps shows it clearly, with
        /// twenty-five of these: field 3 is only 2, 4 or 6, which are Dofus's cardinal
        /// directions (0 right, 2 down, 4 left, 6 up). The client takes the
        /// figure out walking towards that side and then deletes it.
        ///
        ///   10 a282f0a6c408 18 06     who, and he left through the top
        ///   10 a282f0a6c408           who, and he left to the east (0, off the wire)
        /// </summary>
        /// <remarks>
        /// The one without f3 is direction 0, east, which proto3 leaves off the wire; it is not an
        /// exit with no direction. Frame 5 of that capture is the leader walking from [1,-32] to
        /// [2,-32], and the member's own jsd of frame 19 is the same walk. So a 0 is written the
        /// same way here, and a jump -- which has no way out at all -- sends no jsd: see
        /// <see cref="SessionRegistry.LeaveNotices"/>.
        /// </remarks>
        public static byte[] BuildActorLeft(long contextualId, int? porDonde = null)
        {
            var pb = Pb.New().Var(2, contextualId);
            if (porDonde.HasValue) pb.VarIfNotZero(3, porDonde.Value);
            return Push(Op.Jsd, pb.Build());
        }

        /// <summary>
        /// Takes an actor off the map in the client of someone watching (kmu): only who.
        /// </summary>
        /// <remarks>
        /// This, and not the jsd, is what makes a character who left disappear from the others'
        /// screens. In "Movimiento/captura otro personaje saliendo del mapa" two characters walk
        /// off the observer's map, and each time the server sends their jsj to the edge and then
        /// kmu { f2: their id } -- no jsd at all. The jsd goes to the one leaving, before his jru,
        /// and to his party: in "Grupos/con grupo seguir desplazamiento del lider..." the member
        /// watching gets the leader's jsd and, right behind it, the same kmu. With the jsd alone
        /// the character walked to the edge on the others' screens and stayed there.
        /// </remarks>
        public static byte[] BuildActorRemoved(long contextualId)
            => Push(Op.Kmu, Pb.New().Var(2, contextualId).Build());

        /// <summary>"Load this map" (jru).</summary>
        public static byte[] BuildLoadMap(long mapId)
            => Push(Op.Jru, Pb.New().Var(2, mapId).Build());

        /// <summary>
        /// The two that travel with jru on every map change of the capture: lqu, which carries a
        /// 120 and the server clock in milliseconds, and hjk, which carries the map id in a packed
        /// list. lqn goes out between them in the capture and does not go out here: its one field
        /// is a number we have not been able to explain (197 on entering the world, 24 on changing
        /// map, 470 after a characteristics reset), and inventing it is worse than leaving it out.
        /// </summary>
        public static byte[] BuildMapClock()
            => Push(Op.Lqu, Pb.New()
                .Var(1, 120)
                .Var(2, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds())
                .Build());

        /// <summary>
        /// The zaaps the character has DISCOVERED (hjk), in a packed list.
        ///
        /// This is not «which maps you have seen»: it is the only reason the client shows anything
        /// in the travel window. The real server sends this whole list on entering the world
        /// —182 bytes with 45 maps, and all 45 are activated zaaps— and then a single one each time
        /// a new zaap is stepped on. Without it the client considers none discovered and the window
        /// comes out with «Ningún destino» however many destinations the hjj brings.
        ///
        /// The emulator does not store discoveries per character —here one has them all— so the
        /// list is always the same: all the activated zaaps for which it is also known where
        /// their element is, which are the ones that can be left from.
        /// </summary>
        public static byte[] BuildDiscoveredZaaps(IEnumerable<long> mapIds)
            => Pb.New().Packed(1, mapIds).Build();

        public static byte[] BuildMapDiscovered(long mapId)
            => Push(Op.Hjk, BuildDiscoveredZaaps(new long[] { mapId }));

        /// <summary>
        /// Movement along a map (jsj), which is what the server sends back to a jrw.
        ///
        ///   f1: the cells walked, packed
        ///   f2: how the actor ends up facing
        ///   f5: whose movement it is
        /// </summary>
        public static byte[] BuildActorMoved(long contextualId, IEnumerable<long> cells, int facing)
            => Push(Op.Jsj, Pb.New()
                .Packed(1, cells)
                .VarIfNotZero(2, facing)
                .Var(5, contextualId)
                .Build());

        // ─── World: inventory ───────────────────────────────────────────────────

        /// <summary>
        /// The inventory (ivx), built from the database instead of replayed from the capture.
        ///
        ///   f3 (repeated) { f1: slot,
        ///                   f5 { f1: template, f2 (repeated) { &lt;valor&gt;, f11: effect },
        ///                        f3: how many, f4: uid } }
        ///
        /// The slot is left out when it is zero, as proto3 does everywhere: zero is the amulet.
        /// </summary>
        public static byte[] BuildInventory()
        {
            // f1 is the kamas: every ivx of every capture carries them there, 66,381,547 on the way
            // into the world, 61,898,327 when the oven opens. Without them a refresh of the bag
            // reads as a character with none.
            var ivx = Pb.New().VarIfNotZero(1, SessionContext.State.Kamas);
            foreach (var item in Managers.Equipment.All)
            {
                var body = Pb.New().Var(1, item.Template);
                foreach (var effect in item.Effects)
                {
                    var entry = EffectEntry(effect);
                    if (entry != null) body.Msg(2, entry);
                }
                body.Var(3, Math.Max(1, item.Quantity)).Var(4, item.Uid);

                ivx.Msg(3, Pb.New().VarIfNotZero(1, item.Position).Msg(5, body));
            }
            return ivx.Build();
        }

        /// <summary>
        /// An effect entry: the id in f11 and the value in whichever field it belongs.
        ///
        /// The field is not just any slot, it is the one saying what type the effect is. f4 carries a
        /// loose number, f5 a range and f6 three numbers, and the last two are SUBMESSAGES. Putting
        /// a varint where the client expects a submessage is not an odd value, it is a wire
        /// type that does not match: the client does not find the parameters and draws the weapon without damage
        /// and the dofus with "{spellNoLvl,,}" instead of the spell's name.
        /// </summary>
        private static Pb? EffectEntry(Managers.Equipment.ItemEffect effect)
        {
            // The smithmagic pool and any other line of ours that is no effect of the client's:
            // it lives with the item and never goes on the wire.
            if (effect.Effect <= 0) return null;

            // The ones that are not a number go with their text in f1: 988 is "Fabricado por: #4" and the
            // #4 is this string. Without text they do not go, because the label would come out empty.
            if (!string.IsNullOrEmpty(effect.Text))
            {
                return Pb.New().Str(1, effect.Text).Var(11, effect.Effect);
            }

            var (field, v1, v2, v3) = Managers.EffectFields.Shape(
                effect.Effect, effect.Value, effect.DiceNum, effect.DiceSide);
            if (field == Managers.EffectFields.Skip) return null;

            var entry = Pb.New();
            switch (field)
            {
                case Managers.EffectFields.AsNumber:
                    // Written even at zero: "Ninguna forjamagia futura" (2825) travels as
                    // 20 00 58 89 16 on the captured shield, its f4 there and empty.
                    entry.Var(4, v1);
                    break;
                case Managers.EffectFields.AsRange:
                    entry.Msg(5, Pb.New().VarIfNotZero(1, v1).VarIfNotZero(2, v2));
                    break;
                case Managers.EffectFields.AsDice:
                    entry.Msg(6, Pb.New().VarIfNotZero(1, v1).VarIfNotZero(2, v2).VarIfNotZero(3, v3));
                    break;
            }
            return entry.Var(11, effect.Effect);
        }

        // ─── World: titles and ornaments ────────────────────────────────────────

        /// <summary>
        /// What one HAS (hhy). The client already carries the whole catalogue inside; what is not in
        /// this list it draws in grey.
        ///
        ///   f1: [titles]   f2: [ornaments]   both packed
        ///
        /// It goes out only once, on entering the world. In the capture of a freshly created character
        /// it arrives with zero bytes: he has none yet.
        /// </summary>
        public static byte[] BuildTitlesOwned(IEnumerable<long> titles, IEnumerable<long> ornaments)
            => Pb.New().Packed(1, titles).Packed(2, ornaments).Build();

        /// <summary>
        /// The title worn (hid) and the ornament worn (hif). With nothing equipped the message goes
        /// EMPTY —not with a zero inside—, which is how the real server says "none".
        /// </summary>
        public static byte[] BuildTitleUpdated(int titleId)
            => titleId == Managers.Wardrobe.None ? Array.Empty<byte>()
                                                 : Pb.New().Var(1, titleId).Build();

        public static byte[] BuildOrnamentUpdated(int ornamentId)
            => ornamentId == Managers.Wardrobe.None ? Array.Empty<byte>()
                                                    : Pb.New().Var(1, ornamentId).Build();

        /// <summary>
        /// The character's "options" within the actor block: the title and the ornament.
        ///
        ///   f5 { f2 { f2: title } }
        ///   f5 { f9 { f1: counter, f4: ornament } }
        ///
        /// They go repeated inside the same f3 that already carries the account, and the one not held is not
        /// emitted. f9.f1 is a counter of the character's own that the real server hands out with no
        /// visible pattern; here it is derived from the id so that it is stable.
        /// </summary>
        private static void AddCharacterOptions(Pb humanoidBody, long characterId)
        {
            // The guild, the first of the options. Measured in the founder's jsn right after founding
            // «Jondo»: f5 { f4 { f1{f3 emblem}, f2 id, f3 name, f4 level } }, before the
            // ornament and the f7:1. Without this a character with a guild does not carry its name on the
            // map, neither for him nor for the others.
            var guild = Managers.GuildStore.GuildOf(characterId);
            if (guild != null)
            {
                humanoidBody.Msg(5, Pb.New().Msg(4, GuildProtocol.GuildBlock(guild)));
            }

            var (title, ornament) = Managers.Wardrobe.Of(characterId);

            if (title != Managers.Wardrobe.None)
            {
                humanoidBody.Msg(5, Pb.New().Msg(2, Pb.New().Var(2, title)));
            }

            if (ornament != Managers.Wardrobe.None)
            {
                humanoidBody.Msg(5, Pb.New().Msg(9, Pb.New()
                    .Var(1, OrnamentCounterOf(characterId))
                    .Var(4, ornament)));
            }
        }

        private static long OrnamentCounterOf(long characterId) => (characterId % 300) + 174;

        /// <summary>The identity block's f2. It is 3 for the players in the captures.</summary>
        private const int HumanKind = 3;

        /// <summary>The f7 that closes the block, a single byte with the same value in every capture.</summary>
        private static readonly byte[] HumanTrailer = { 0x0b };

        // ─── World: merkasako ───────────────────────────────────────────────────

        /// <summary>
        /// The furniture placed in the room (jbu), which the client expects after the map.
        ///
        ///   f1 (repeated) { f1: cell, f2: furniture, f3: rotation }
        ///
        /// It is the same shape as the jbg the client stores them with, only in f1 instead of
        /// in f2. In the capture of someone who has it decorated it is a thousand-odd bytes.
        /// </summary>
        public static byte[] BuildHavenBagFurniture(IEnumerable<Managers.HavenBagStore.Furniture> pieces)
        {
            var jbu = Pb.New();
            foreach (var piece in pieces)
            {
                jbu.Msg(1, Pb.New()
                    .VarIfNotZero(1, piece.Cell)
                    .Var(2, piece.TypeId)
                    .VarIfNotZero(3, piece.Orientation));
            }
            return jbu.Build();
        }

        // ─── World: cofre ───────────────────────────────────────────────────────

        // "El cofre está abierto" (kci) is built per kind of storage -- a house chest's is not a
        // bin's nor the haven bag's -- in StorageProtocol.BuildOpened.

        /// <summary>
        /// What is inside the chest (iwb). Same shape as the inventory, with the bag as
        /// the position of everything: inside a chest nothing is equipped.
        /// </summary>
        public static byte[] BuildStorageContent(IEnumerable<Managers.HavenBagStore.StoredItem> items)
        {
            var iwb = Pb.New();
            foreach (var item in items)
            {
                var body = Pb.New().Var(1, item.Gid);
                foreach (var effect in Managers.Equipment.ParseEffects(item.Effects))
                {
                    var entry = EffectEntry(effect);
                    if (entry != null) body.Msg(2, entry);
                }
                body.Var(3, Math.Max(1, item.Quantity)).Var(4, item.Uid);

                iwb.Msg(1, Pb.New().Var(1, Managers.Equipment.Bag).Msg(5, body));
            }
            return iwb.Build();
        }

        /// <summary>Un objeto que entra en un sitio (iua para el cofre, itd para la bolsa).</summary>
        public static byte[] BuildItemArrived(int field, Managers.HavenBagStore.StoredItem item)
        {
            var body = Pb.New().Var(1, item.Gid);
            foreach (var effect in Managers.Equipment.ParseEffects(item.Effects))
            {
                var entry = EffectEntry(effect);
                if (entry != null) body.Msg(2, entry);
            }
            body.Var(3, Math.Max(1, item.Quantity)).Var(4, item.Uid);

            return Pb.New()
                .Msg(field, Pb.New().Var(1, Managers.Equipment.Bag).Msg(5, body))
                .Build();
        }

        /// <summary>
        /// An item the way every item message carries it: { f1: template, f2: effects, f3: how
        /// many, f4: uid }. The uid is left out when it is zero, as the kdr of a craft of several
        /// does.
        /// </summary>
        internal static Pb ItemBody(int gid, IEnumerable<Managers.Equipment.ItemEffect> effects, int quantity, long uid)
        {
            var body = Pb.New().Var(1, gid);
            foreach (var effect in effects)
            {
                var entry = EffectEntry(effect);
                if (entry != null) body.Msg(2, entry);
            }
            return body.Var(3, Math.Max(1, quantity)).VarIfNotZero(4, uid);
        }

        /// <summary>
        /// An item's effects, one entry each under the field given, the way <see cref="ItemBody"/>
        /// writes them under f2. The marketplace's offers carry them under f4 (kbt, kgp) and f1
        /// (kfi), with nothing else of the item around them.
        /// </summary>
        internal static Pb AddEffects(Pb target, int field, IEnumerable<Managers.Equipment.ItemEffect> effects)
        {
            foreach (var effect in effects)
            {
                var entry = EffectEntry(effect);
                if (entry != null) target.Msg(field, entry);
            }
            return target;
        }

        /// <summary>An item that leaves (itc from the chest, ium from the bag): only its identifier.</summary>
        public static byte[] BuildItemGone(long uid) => Pb.New().Var(1, uid).Build();

        /// <summary>
        /// What the lottery machine answers (jbs).
        ///
        ///   f2: the prize       f3: the reason for refusal
        ///
        /// From the two captures: one comes out with f2 and the other, the "you have already used it today" one, with f3: 1.
        /// Here one always wins, so f2 always goes.
        /// </summary>
        public static byte[] BuildLotteryResult(long prizeUid)
            => Pb.New().Var(2, prizeUid).Build();

        /// <summary>The chest closed (khd). In the capture it carries f3: 11.</summary>
        public static byte[] BuildStorageClosed() => Pb.New().Var(3, StorageCloseReason).Build();

        private const int StorageCloseReason = 11;

        // ─── World: chat ────────────────────────────────────────────────────────

        /// <summary>
        /// A line of chat coming back (kti). What the client sends is
        /// ktm { f2: the text, f3: the channel }, and this is the answer:
        ///
        ///   f3: when, as "2026-08-09T20:28:01+02:00"
        ///   f4: who said it
        ///   f5: their character
        ///   f6: their account
        ///   f7: what they said
        ///   f8: {} , empty in every line of every capture
        ///   f9: the channel
        ///
        /// Channels, from the capture that goes through all of them in one sitting: 0 general
        /// (left out, being zero), 1 team, 2 guild, 3 alliance, 4 party, 5 trade, 6 recruitment,
        /// and 9, 11, 16, 18 and 19 for the rest. A private message is a different message, ktb,
        /// and it carries who it is for.
        /// </summary>
        public static byte[] BuildChatLine(string who, long characterId, long accountId,
                                           string text, int channel)
            => Pb.New()
                .Str(3, DateTimeOffset.Now.ToString("yyyy-MM-ddTHH:mm:sszzz"))
                .Str(4, who ?? "")
                .Var(5, characterId)
                .VarIfNotZero(6, accountId)
                .Str(7, text ?? "")
                .EmptyMsg(8)
                .VarIfNotZero(9, channel)
                .Build();

        // ─── Grupos ─────────────────────────────────────────────────────────────

        /// <summary>
        /// You have been invited to a party (ijz): it brings up the little window.
        ///
        ///   f1: who is invited    f2: who invites   f3: places
        ///   f5: the party         f6: ?             f7: the inviter's name
        ///
        /// Measured: 08a28280c8e708 10a282f0a6c408 1808 28e8ac04 3001 3a064861726d6f6f, that is
        /// invitee 302677754146, host 293213045026, eight places, party 71272, and «Harmoo».
        /// f6 is 1 in one capture and 2 in another and what distinguishes them has not been worked out; 1 is sent,
        /// which is that of the invitation that gets accepted.
        /// </summary>
        public static byte[] BuildPartyInvitation(long guestId, long hostId, string hostName,
                                                  int partyId, int seats)
            => Pb.New()
                .Var(1, guestId)
                .Var(2, hostId)
                .Var(3, seats)
                .Var(5, partyId)
                .Var(6, 1)
                .Str(7, hostName ?? "")
                .Build();

        /// <summary>The invitation is over, for whoever declines it (ilo): { f1: party, f2: who was inviting }.</summary>
        public static byte[] BuildInvitationClosed(int partyId, long hostId)
            => Pb.New().Var(1, partyId).Var(2, hostId).Build();

        /// <summary>Removes the invitee from the list, for whoever invited (iko): { f1: invitee, f2: party }.</summary>
        public static byte[] BuildInvitationWithdrawn(long guestId, int partyId)
            => Pb.New().Var(1, guestId).Var(2, partyId).Build();

        /// <summary>The party has broken up (imy): { f1: party }.</summary>
        public static byte[] BuildPartyDissolved(int partyId) => Pb.New().Var(1, partyId).Build();

        /// <summary>Te has salido (ils): { f1: grupo }.</summary>
        public static byte[] BuildPartyLeft(int partyId) => Pb.New().Var(1, partyId).Build();

        /// <summary>
        /// There is a new leader (ilx): { f1: the new leader, f2: the party }.
        ///
        /// Eleven bytes, and the whole party is NOT resent: it was checked by comparing the sheet of the same
        /// party before and after the change, and the only thing that changes is its field 4.
        /// </summary>
        public static byte[] BuildPartyLeader(long leaderId, int partyId)
            => Pb.New().Var(1, leaderId).Var(2, partyId).Build();

        /// <summary>
        /// A private message (kth): { f1: date, f4: empty, f5: the other's id, f6: his name,
        /// f7: the text }.
        ///
        /// Measured from the guild capture, where the whisper to «Hiierbita-Xx» DID arrive:
        ///
        ///   0a19 «2026-08-12T22:54:29+02:00»  2200  28 a282acfea805
        ///   320c «Hiierbita-Xx»  3a04 «hola»
        ///
        /// Mind two things. It carries no CHANNEL: the client knows it is private by the message
        /// itself, and that is why sending it as a kti through channel 9 draws nothing. And what it carries is
        /// not who speaks but THE OTHER —in your copy, whom you are saying it to—, so the same
        /// message serves both sides by changing whose identity is put in.
        /// </summary>
        public static byte[] BuildPrivateMessage(string when, long otherId, string otherName,
                                                 string text)
            => Pb.New()
                .Str(1, when)
                .EmptyMsg(4)
                .Var(5, otherId)
                .Str(6, otherName ?? "")
                .Str(7, text ?? "")
                .Build();

        /// <summary>
        /// The level-up window (kua): { f1: the new level }.
        ///
        /// Two bytes, and with that the client brings up the whole window —music, animation and the level's
        /// data— and leaves it open until the player closes it. It answers nothing on
        /// closing it, so there is nothing to listen for.
        ///
        /// It appears exactly twice in the 305 captures, both in the tutorial and in the exact
        /// millisecond of each level-up: 0802 on reaching level 2 and 0803 on reaching level 3.
        /// After it go iun, kub and kfe, but those three also come out on entering the world without levelling
        /// up, so the only message belonging to the level-up is this one.
        ///
        /// What the window shows —points earned, life, spells— the client takes from the kub
        /// that comes after, not from here. That is why the kua has to be sent BEFORE the new
        /// characteristics, which is the order of the capture.
        /// </summary>
        public static byte[] BuildLevelUp(int level) => Pb.New().Var(1, level).Build();

        /// <summary>
        /// The chat could not manage something (ktl), with the reason in its only field. Measured: 0802 is
        /// what the real server answers on whispering to oneself.
        /// </summary>
        public static byte[] BuildChatError(int reason) => Pb.New().Var(1, reason).Build();

        // ─── Envelope for answers ───────────────────────────────────────────────

        /// <summary>
        /// Wraps an answer to something the client asked for: f3 { f1 { f1: type_url, f2: payload },
        /// f2: the id the request came with }.
        ///
        /// Three different root fields are in use and they are not interchangeable. Field 1 is a
        /// message the server pushes on its own; field 2 is what the client sends; field 3 is an
        /// answer, and it repeats the request's id so the client can pair them. jsq, the go-ahead
        /// for a map change, is the one that made this necessary.
        /// </summary>
        public static byte[] Answer(string opcode, byte[]? payload, long requestId)
        {
            var any = Pb.New().Str(1, UriPrefix + opcode);
            if (payload != null && payload.Length > 0) any.Bytes(2, payload);

            return Pb.New()
                .Msg(3, Pb.New().Bytes(1, any.Build()).Var(2, requestId))
                .Build();
        }

        /// <summary>
        /// The id a client request carries, in field 2 of the root. It is -1 for everything seen
        /// so far, and it is read rather than assumed because the answer has to echo it.
        /// </summary>
        public static long RequestId(byte[] frame)
        {
            try
            {
                foreach (var f in ProtoMessage.Parse(frame).Fields)
                {
                    if (f.FieldNumber != 2 || f.WireType != 2) continue;
                    foreach (var g in ProtoMessage.Parse(f.BytesValue).Fields)
                    {
                        if (g.FieldNumber == 2 && g.WireType == 0) return g.VarIntValue;
                    }
                }
            }
            catch { }
            return -1;
        }

        // ─── Helpers ────────────────────────────────────────────────────────────

        /// <summary>
        /// Pulls the payload out of a wrapped message by looking for its type_url in the frame.
        ///
        /// Scanning for the raw marker is deliberate: that way it does not matter which root
        /// field the message is wrapped in, which differs between client and server messages.
        /// Returns an empty array when the message is there but carries no payload.
        /// </summary>
        public static byte[]? ReadPayload(byte[] frame, string opcode)
        {
            if (frame == null) return null;
            byte[] marker = Encoding.ASCII.GetBytes(UriPrefix + opcode);

            for (int i = 0; i + marker.Length <= frame.Length; i++)
            {
                bool matches = true;
                for (int j = 0; j < marker.Length; j++)
                {
                    if (frame[i + j] != marker[j]) { matches = false; break; }
                }
                if (!matches) continue;

                // Right behind the type_url comes field 2 of the Any, holding the payload.
                int p = i + marker.Length;
                if (p >= frame.Length || frame[p] != 0x12) return Array.Empty<byte>();

                p++;
                int length = 0, shift = 0;
                while (p < frame.Length)
                {
                    byte b = frame[p++];
                    length |= (b & 0x7F) << shift;
                    if ((b & 0x80) == 0) break;
                    shift += 7;
                }

                if (length < 0 || p + length > frame.Length) return Array.Empty<byte>();
                byte[] payload = new byte[length];
                Array.Copy(frame, p, payload, 0, length);
                return payload;
            }
            return null;
        }

        /// <summary>
        /// Pulls the three-letter opcode out of a wrapped frame, or null if it has no envelope.
        /// </summary>
        public static string? ReadOpcode(byte[] frame)
        {
            if (frame == null || frame.Length == 0) return null;
            byte[] marker = Encoding.ASCII.GetBytes(UriPrefix);
            for (int i = 0; i + marker.Length + 3 <= frame.Length; i++)
            {
                bool matches = true;
                for (int j = 0; j < marker.Length; j++)
                {
                    if (frame[i + j] != marker[j]) { matches = false; break; }
                }
                if (matches)
                {
                    return Encoding.ASCII.GetString(frame, i + marker.Length, 3);
                }
            }
            return null;
        }
    }
}
