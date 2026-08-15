using System;
using System.Collections.Generic;
using System.Text;
using Jondo.Unity.Launcher.Managers;

namespace Jondo.Unity.Launcher.Network
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

            // Cuántos personajes caben por tipo de servidor. Siete entradas, tipos 0 a 6.
            //
            // La captura real de la pantalla de creación de personaje trae cinco en los siete, con
            // una cuenta que tenía cuatro personajes en su servidor y el botón activo. Así que esto
            // es el tope, no la cuenta, y subirlo es lo correcto; lo que tenía el botón apagado era
            // otra cosa (la fecha de abono, en GameServerProxy).
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
        /// Cuántos personajes caben por servidor.
        ///
        /// El servidor real anuncia cinco, que es el límite de una cuenta de verdad. Aquí no hay
        /// nada que limitar: es un emulador de un solo jugador y el tope solo servía para apagar el
        /// botón de crear personaje en cuanto se llenaba.
        /// </summary>
        public const int MaxCharactersPerServer = 100;

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
        public static List<byte[]> BuildWelcomeBurst(IReadOnlyList<DatabaseManager.DbCharacter> characters)
        {
            var burst = new List<byte[]>
            {
                Push("kra"),
                Push("lqu", Pb.New()
                    .Var(1, SyncRate)
                    .Var(2, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds())
                    .Build()),
                Push("hoy", BuildHoy()),
                Push("kqu", Pb.New().Packed(1, ActiveFeatures).Build()),
                Push("mgq", Pb.New().Var(1, 1).Var(2, 1).Var(3, 1).Build()),
                Push("mgt", Pb.New().EmptyMsg(2).Build()),
                Push("hpd", Pb.New().Var(1, 1).Build()),
                Push("krs"),
                Push("mgz", Pb.New().Var(1, CatalogMark).Build()),
                Push("kqp", Pb.New().Var(1, 1).Var(2, 1).Build()),
                Push("kqp", Pb.New().Var(1, 1).Build()),
                Push("kqp"),
                Push("kvi", BuildCharactersList(characters)),

                // kvd cierra la lista de personajes. Va vacío y justo detrás del kvi en la ráfaga
                // real, y no lo mandábamos. Es el candidato a lo que tenía el botón de crear
                // personaje apagado: el cliente recibía la lista y nada que dijera que ya está
                // toda, así que la pantalla se quedaba a medio montar.
                Push("kvd"),

                Push("jtg", BuildGiftCatalogue())
            };
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
        /// El saludo del servidor de juego, con los mismos valores que la captura.
        ///
        ///   f1: 30   f2: 1   f3: 1   f6: idioma   f7: 200
        ///
        /// Sin f5. Mandábamos un f5 = 2 que no está en ninguna de las tres capturas del arranque, y
        /// el idioma iba en inglés cuando el cliente arranca en español. Son las dos únicas
        /// diferencias que quedaban entre nuestra ráfaga de bienvenida y la real.
        /// </summary>
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

        /// <summary>El idioma con el que se lanza el cliente.</summary>
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

            // Con el id, para que en la pantalla de selección salga montado si lo está. Sin él la
            // montura solo se sabía del personaje que ya estuviera jugando.
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
            public const int Energy = 47;
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
            { Stat.DodgeActionPoints,    () => GameState.StatWisdom / 10 },
            { Stat.DodgeMovementPoints,  () => GameState.StatWisdom / 10 },
            { Stat.WithdrawActionPoints, () => GameState.StatWisdom / 10 },
            { Stat.WithdrawMovementPoints, () => GameState.StatWisdom / 10 },
            { Stat.Escape,               () => GameState.StatAgility / 10 },
            { Stat.Lock,                 () => GameState.StatAgility / 10 },
        };

        /// <summary>Points a character starts with, before anything is spent or equipped.</summary>
        private const int BaseActionPoints = 6;
        private const int BaseMovementPoints = 3;
        private const int BasePods = 1000;
        private const int BaseEnergy = 10000;

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
            int level = GameState.CharacterLevel;

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
                .VarIfNotZero(8, GameState.Experience)
                .Bytes(9, FreshUnknownF9())
                .VarIfNotZero(10, GameState.Kamas);

            // The six the player spends points on.
            var primary = new Dictionary<int, long>
            {
                { Stat.Strength, GameState.StatStrength },
                { Stat.Vitality, GameState.StatVitality },
                { Stat.Wisdom, GameState.StatWisdom },
                { Stat.Chance, GameState.StatChance },
                { Stat.Agility, GameState.StatAgility },
                { Stat.Intelligence, GameState.StatIntelligence },
            };

            IReadOnlyList<int> ids = WorldEntry.CharacteristicIds;
            if (ids.Count == 0)
            {
                // No capture to learn from: at least the ones we know about travel.
                var fallback = new List<int>
                {
                    Stat.LifePoints, Stat.ActionPoints, Stat.RemainingPoints,
                    Stat.MovementPoints, Stat.Energy, Stat.Pods
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
                        AddStat(body, id, value, equipped);
                        break;
                }
            }

            return Pb.New().Msg(2, body).Build();
        }

        /// <summary>Value of a characteristic that the player does not spend points on.</summary>
        private static long ValueOf(int id, int level)
        {
            if (id == Stat.LifePoints) return BaseLife(level);
            if (id == Stat.ActionPoints) return BaseActionPoints;
            if (id == Stat.MovementPoints) return BaseMovementPoints;
            if (id == Stat.Energy) return BaseEnergy;
            // Five pods a point of strength on top of the base, which is what the capture shows:
            // five points of strength moved this characteristic by twenty-five.
            if (id == Stat.Pods) return BasePods + 5L * GameState.StatStrength;
            if (id == Stat.RemainingPoints) return GameState.CharacterRemainingPoints;
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

        private static void AddStat(Pb body, int id, long value, long fromEquipment = 0)
        {
            // Characteristic 0 is life, and it is the one entry of the real message that carries
            // no id at all: proto3 leaves the field out when the value is zero, and zero is its
            // id. Writing Var(1, 0) here would put a field the real one does not have.
            var entry = Pb.New();
            if (id != 0) entry.Var(1, id);
            entry.Msg(4, Pb.New().VarIfNotZero(2, value).VarIfNotZero(7, fromEquipment));
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
        public static byte[] BuildHeartbeatAnswer() => Push("kqy", Pb.New().Var(1, 1).Build());

        /// <summary>
        /// Closes a map load (lva). It carries nothing: it is the "that is every actor" mark.
        ///
        /// It goes immediately behind jss in every capture where a map is loaded — the four
        /// movement ones, the entry into the world and the tutorial. Without it the client never
        /// finishes loading the map: it waits about two seconds, asks again with knm, kno and kny,
        /// and starts over.
        /// </summary>
        public static byte[] BuildActorsComplete() => Push("lva");

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

                var creatures = Pb.New();
                for (int i = 1; i < group.Members.Count; i++)
                {
                    var member = group.Members[i];
                    creatures.Msg(1, Pb.New()
                        .Var(1, member.Monster.Id)
                        .VarIfNotZero(2, LevelOf(member))
                        .Msg(3, Pb.New()
                            .Var(2, LookKind)
                            .VarIfNotZero(3, BonesOf(member.Monster.Look)))
                        .VarIfNotZero(4, GradeOf(member)));
                }

                var leader = group.Members[0];
                creatures.Msg(2, Pb.New()
                    .Var(1, leader.Monster.Id)
                    .VarIfNotZero(2, LevelOf(leader))
                    .VarIfNotZero(4, GradeOf(leader)));

                jss.Msg(5, Pb.New()
                    .Msg(1, Pb.New().Var(1, group.CellId).Var(2, 1))
                    .Msg(2, Pb.New()
                        .Msg(1, Pb.New().Msg(4, Pb.New()
                            .Var(1, 1)
                            .Msg(2, creatures)
                            .Var(5, -1)))
                        .Msg(3, Pb.New()
                            .Var(2, LookKind)
                            .VarIfNotZero(3, BonesOf(leader.Monster.Look))))
                    .Var(3, group.MobId));
            }

            // Behind the actors, which is where the capture puts it.
            var where = MapManager.GetMapInfo(mapId);
            if (where != null) jss.VarIfNotZero(6, where.SubAreaId);

            AddInteractiveElements(jss, mapId);

            return jss.Build();
        }

        /// <summary>
        /// Lo que se puede clicar en el mapa. De momento, el zaap.
        ///
        ///   f11 { f1: 1, f4 { f1: uid de la habilidad, f2: habilidad }, f5: elemento, f6: tipo }
        ///   f15 { f1: estado, f2: casilla, f3: elemento }
        ///
        /// Son dos mensajes distintos y hacen falta los dos: el f11 dice qué elemento existe y qué
        /// se puede hacer con él, y el f15 dónde está y en qué estado. El número del elemento sale
        /// de los datos del propio cliente (<see cref="Managers.Interactives"/>), así que el
        /// cliente ya sabe qué dibujo ponerle y dónde.
        ///
        /// Van al final, detrás de la subzona, que es donde los pone la captura real.
        /// </summary>
        private static void AddInteractiveElements(Pb jss, long mapId)
        {
            foreach (var zaap in Managers.Interactives.ZaapElements(mapId))
            {
                Declare(jss, zaap, Managers.Interactives.UseSkill, Managers.Interactives.ZaapType);
            }

            // Y en el merkasako, el cofre y la máquina de la lotería.
            var chest = Managers.Merkasako.ChestOf(mapId);
            if (chest.Id != 0)
            {
                Declare(jss, chest, Managers.Merkasako.ChestSkill, Managers.Merkasako.ChestType);
            }

            var machine = Managers.Lottery.Of(mapId);
            if (machine.Id != 0)
            {
                Declare(jss, machine, Managers.Lottery.Skill, Managers.Lottery.Type);
            }
        }

        /// <summary>Un elemento clicable: qué es, qué se puede hacer con él y dónde está.</summary>
        private static void Declare(Pb jss, Managers.Interactives.Element element, int skill, int type)
        {
            jss.Msg(11, Pb.New()
                .Var(1, 1)
                .Msg(4, Pb.New()
                    .Var(1, Managers.Interactives.SkillInstanceOf(element.Id))
                    .Var(2, skill))
                .Var(5, element.Id)
                .Var(6, type));

            jss.Msg(15, Pb.New()
                .Var(1, 1)
                .Var(2, element.Cell)
                .Var(3, element.Id));
        }

        /// <summary>
        /// Level of a spawned monster: the one the spawner rolled, or failing that the one its
        /// grade declares.
        /// </summary>
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
        /// El grado de un monstruo, de 1 a 5.
        ///
        /// Y ahí está el tope, que es lo que importa: en trescientos y pico monstruos de las
        /// capturas reales el grado sale 1, 2, 3, 4 o 5 y nunca más. Nuestros datos no se portan
        /// igual —4.098 monstruos tienen cinco grados, pero 479 tienen seis, 169 tienen diez y uno
        /// tiene veinte— y el generador elegía cualquiera, así que salían grados 6 y más arriba.
        ///
        /// Al cliente eso le sienta mal en silencio: el grupo se dibuja, pero pasarle el ratón por
        /// encima no enseña nada y la tecla W lo salta. Por eso solo se veía la información de uno
        /// o dos grupos de los cuatro del mapa.
        /// </summary>
        private const int MaxGrade = 5;

        private static long GradeOf(Managers.MobSpawnManager.MobMember member)
            => Math.Clamp(member.GradeIndex + 1, 1, MaxGrade);

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
        public static byte[] BuildSpellList(int breed, int level)
        {
            var hms = Pb.New();
            foreach (var spell in SpellTable.KnownFor(breed, level, Managers.SpellChoices.Chosen))
            {
                hms.Msg(1, Pb.New().Var(1, spell.Grade).Var(3, spell.SpellId).Var(4, 1));
            }
            return hms.Build();
        }

        /// <summary>
        /// The shortcut bar (itg). The real server sends two of them, one for the spells and one
        /// for the items; this is the spell one.
        ///
        ///   f1 (repeated) { f2: slot, f6 { f2: spell id } }
        ///   f2: 1        ← QUÉ BARRA ES
        ///
        /// Ese f2 del final es lo que tenía la barra vacía. Va suelto al terminar la lista y no se
        /// ve leyendo el árbol por encima, porque parece un hueco más; es el tipo de barra, y sin
        /// él vale cero, que es la de objetos. El cliente recibía treinta y cuatro hechizos
        /// declarados como atajos de la barra de objetos y no los pintaba en ninguna de las dos.
        /// La de objetos, que es la que sí es del tipo cero, no lo lleva.
        ///
        /// The slot is left out when it is zero, as proto3 does everywhere else. The client edits
        /// a slot with itz —f2 el atajo, f3 la barra— and the server echoes it in ivk.
        /// </summary>
        public static byte[] BuildSpellBar(int breed, int level)
        {
            var known = SpellTable.KnownFor(breed, level, Managers.SpellChoices.Chosen);

            // Un hechizo que ya no se tiene —porque se cambió de variante— no puede quedarse en la
            // barra: el cliente pinta un hueco que no sabe resolver.
            var has = new HashSet<int>();
            foreach (var spell in known) has.Add(spell.SpellId);

            var placed = new List<(int Slot, int SpellId)>();
            var taken = new HashSet<int>();
            foreach (var pair in Managers.SpellChoices.Bar)
            {
                if (pair.Key >= SpellBarSlots || !has.Contains(pair.Value)) continue;
                placed.Add((pair.Key, pair.Value));
                taken.Add(pair.Value);
            }

            // Y lo que el jugador todavía no ha colocado se reparte por los huecos libres, que es
            // lo que hace el juego con un personaje recién hecho.
            int next = 0;
            foreach (var spell in known)
            {
                if (taken.Contains(spell.SpellId)) continue;
                while (next < SpellBarSlots && placed.Exists(p => p.Slot == next)) next++;
                if (next >= SpellBarSlots) break;
                placed.Add((next, spell.SpellId));
                next++;
            }

            placed.Sort((a, b) => a.Slot.CompareTo(b.Slot));
            Managers.SpellChoices.RememberBar(placed);

            var itg = Pb.New();
            foreach (var (slot, spellId) in placed)
            {
                itg.Msg(1, Pb.New()
                    .VarIfNotZero(2, slot)
                    .Msg(6, Pb.New().Var(2, spellId)));
            }
            return itg.Var(2, SpellBar).Build();
        }

        /// <summary>Qué barra es: 0 la de objetos, 1 la de hechizos.</summary>
        public const int SpellBar = 1;

        /// <summary>
        /// El hechizo que sustituye a su pareja (hng), y el hueco de la barra donde queda (iuq).
        ///
        /// Leído de cuatro capturas reales de cambiar de variante, desde el panel y desde la barra:
        ///
        ///   cliente  hmt { f1: el hechizo que quiere }
        ///   servidor iuq { f2 { f2: hueco, f6 { f2: hechizo } }, f3: qué barra }   uno por hueco
        ///   servidor hng { f2: hechizo, f3: grado }
        ///
        /// Los iuq van primero y hay uno por cada hueco que tuviera la mitad vieja: en la captura
        /// de Liberación por Magnetismo salieron dos, porque el hechizo estaba puesto dos veces.
        /// </summary>
        public static byte[] BuildSpellSwapped(int spellId, int grade)
            => Pb.New().Var(2, spellId).Var(3, grade).Build();

        public static byte[] BuildShortcutChanged(int slot, int spellId)
            => Pb.New()
                .Msg(2, Pb.New().VarIfNotZero(2, slot).Msg(6, Pb.New().Var(2, spellId)))
                .Var(3, SpellBar)
                .Build();

        /// <summary>How many slots of the bar we fill. The captured one runs from 0 to 48.</summary>
        private const int SpellBarSlots = 40;

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

        // ─── World: apariencia ──────────────────────────────────────────────────

        /// <summary>
        /// El bloque de un personaje dentro del mapa: dónde está, quién es y qué aspecto tiene.
        ///
        ///   f1 { f1: casilla, f2: hacia dónde mira }
        ///   f2 { f1 { f5: nombre y cuenta }, f3: el aspecto }
        ///   f3: el identificador
        ///
        /// Es el mismo bloque en dos sitios: repetido en el f5 del jss, que es el mapa entero, y
        /// suelto dentro del jsn, que es un solo actor. Por eso está aquí y no dentro de ninguno de
        /// los dos.
        /// </summary>
        private static Pb PlayerActor(DatabaseManager.DbCharacter character, int cell, int facing,
                                     long accountId)
        {
            // El orden y los campos son los de un jsn real con título puesto:
            //
            //   f1 { f2: 3, f5: nivel }      f3: la cuenta
            //   f5 (repetido): las opciones — gremio, título, ornamento, y el f7:1 que va siempre
            //   f6: 1                        f7: 0x0b
            //
            // Faltaban el f1, el f5{f7:1} y el f7, y sin ellos el cliente no pintaba el título ni
            // el ornamento al pasar el ratón por encima.
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

        /// <summary>
        /// "Este actor ha cambiado" (jsn), que es lo que redibuja al personaje en el mapa.
        ///
        /// El lxc no vale para esto. En la captura de equipar un dragopavo salen los dos, y son
        /// cosas distintas: el lxc lleva un UUID que no aparece en ningún otro sitio del flujo —ni
        /// en el jss, ni en ningún jsn— mientras que el jsn lleva el bloque del actor completo, con
        /// su casilla, su identificador y el aspecto nuevo con los huesos de la montura. El cliente
        /// dibuja lo que le diga el jsn.
        ///
        /// Mandando solo el lxc, la muñeca del inventario se enteraba y el muñeco del mapa no: uno
        /// se quedaba montado en el dragopavo de antes por mucho que se cambiara de montura o se
        /// quitaran todas.
        ///
        ///   jsn f1 { el bloque del actor }
        ///
        /// El servidor real manda tres seguidos; con uno basta.
        /// </summary>
        public static byte[] BuildActorRefreshed(DatabaseManager.DbCharacter character, int cell,
                                                 int facing, long accountId)
            => Pb.New()
                .Msg(1, PlayerActor(character, cell, facing, accountId))
                .Build();

        /// <summary>
        /// "Tu aspecto ha cambiado" (lxc), que es lo que el servidor manda al equipar algo.
        ///
        ///   f1: un identificador con forma de UUID
        ///   f2: el aspecto nuevo
        ///
        /// El UUID sale igual en todos los lxc de una misma sesión y cambia entre personajes, así
        /// que parece identificar al dueño del aspecto. No se ha encontrado dónde lo aprende el
        /// cliente —en el jss el único UUID que hay es el de una alianza, no éste— así que aquí se
        /// deriva del id del personaje: constante para él y distinto del de cualquier otro. Si el
        /// cliente no lo comprueba, da igual; si lo comprueba, al menos es coherente.
        /// </summary>
        public static byte[] BuildLookChanged(DatabaseManager.DbCharacter character)
            => Pb.New()
                .Str(1, LookIdOf(character.Id))
                .Bytes(2, BreedLookTable.BuildLook(
                    character.Breed, character.Sex, character.HeadId, null, character.Id,
                    paraLaVentana: true))
                .Build();

        /// <summary>
        /// El estado de la ventana de apariencias (lxo), la respuesta al lyy.
        ///
        ///   f1: 1
        ///   f3 { f3: cuándo, f5: raza, f7: uuid de la vista previa, f8: 3, f10: título,
        ///        f11: nivel, f12: el aspecto, f15: -1, f16: ornamento,
        ///        f17 (repetido) { f1: hueco, f2 { f2: prenda } } }
        ///
        /// El f7 es el mismo uuid que lleva el lxc de la vista previa, y el f12 su mismo aspecto:
        /// así es como el panel sabe que lo que le llega es lo suyo.
        /// </summary>
        public static byte[] BuildAppearanceState(DatabaseManager.DbCharacter character, string draftId)
            => Pb.New().Var(1, 1).Bytes(3, AppearanceBody(character, draftId)).Build();

        /// <summary>
        /// Un CONJUNTO del vestuario: el mismo bloque que va dentro del lxo, y por eso está aquí
        /// aparte. El lyt lo repite —uno por conjunto guardado en el f1, y en el f2 el que está
        /// puesto— y es lo que el panel de apariencias necesita para poder abrirse.
        /// </summary>
        private static byte[] AppearanceBody(DatabaseManager.DbCharacter character, string draftId)
        {
            var (title, ornament) = Managers.Wardrobe.Of(character.Id);

            var body = Pb.New();

            // Los colores del conjunto, pelados y sin índice. Sin esto el cliente revienta al abrir
            // la ventana de cosméticos: su propio registro lo dice, ColorSet..ctor con la lista
            // nula, dentro del manejador del lyt.
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
        /// Los conjuntos del vestuario (lyt), que llegan al entrar al mundo.
        ///
        ///   f1 (repetido): cada conjunto guardado     f2: el que se lleva puesto
        ///
        /// HAY QUE MANDARLO SÍ O SÍ. Sin él, el cliente abre la ventana de cosméticos, suena, y se
        /// queda sin dibujar: revienta en CosmeticUi.DisplayOutfit con una referencia nula porque no
        /// tiene ningún conjunto que enseñar. Se vio en su propio Player.log.
        ///
        /// Aquí va uno solo, el del personaje que juega, con su aspecto y sus prendas de verdad. La
        /// captura traía dos, pero eran los de la cuenta grabada y por eso dejó de reenviarse.
        /// </summary>
        public static byte[] BuildOutfits(DatabaseManager.DbCharacter character)
        {
            byte[] conjunto = AppearanceBody(character, OutfitIdOf(character.Id));
            return Pb.New().Bytes(1, conjunto).Bytes(2, conjunto).Build();
        }

        /// <summary>Un uuid propio del conjunto, distinto del de la vista previa.</summary>
        private static string OutfitIdOf(long characterId) => LookIdOf(characterId * 17 + 3);

        /// <summary>El f8 del estado, constante en las veinte capturas donde sale.</summary>
        private const int AppearanceStateKind = 3;

        /// <summary>Un UUID estable a partir del id del personaje.</summary>
        public static string LookIdOf(long characterId)
        {
            var bytes = new byte[16];
            BitConverter.GetBytes(characterId).CopyTo(bytes, 0);
            BitConverter.GetBytes(characterId * 2654435761L).CopyTo(bytes, 8);
            return new Guid(bytes).ToString();
        }

        // ─── World: zaaps ───────────────────────────────────────────────────────

        /// <summary>
        /// "Ese elemento está en uso" (iwn), la respuesta inmediata al clic sobre un zaap.
        ///
        ///   f1: 1, f2: EL ELEMENTO, f4: la habilidad, f5: quién lo usa
        ///
        /// El f2 es el elemento, no el identificador de la instancia de habilidad. Se ve cruzando
        /// el iwn con el iwo que lo provoca en la misma captura:
        ///
        ///   iwo  f1: 14110  f2: 538795
        ///   iwn  f1: 1      f2: 538795   f4: 114
        ///
        /// El cliente manda los dos números y el servidor le devuelve el segundo. Mandarle el
        /// primero deja al cliente marcando como ocupado un elemento que no existe.
        /// </summary>
        public static byte[] BuildElementInUse(int elementId, int skillId, long who)
            => Pb.New()
                .Var(1, 1)
                .Var(2, elementId)
                .Var(4, skillId)
                .Var(5, who)
                .Build();

        /// <summary>Un destino de la lista de zaaps.</summary>
        public readonly struct ZaapDestination
        {
            public ZaapDestination(long mapId, int subAreaId, int level, long cost)
            {
                MapId = mapId; SubAreaId = subAreaId; Level = level; Cost = cost;
            }

            public long MapId { get; }
            public int SubAreaId { get; }
            public int Level { get; }
            public long Cost { get; }
        }

        /// <summary>
        /// La lista de zaaps (hjj).
        ///
        ///   f2: el mapa donde está el zaap que se ha abierto
        ///   f3 (repetido) { f1: nivel de la zona, f2: lo que cuesta, f5: mapa, f6: subzona }
        ///
        /// El destino en el que uno ya está viaja sin f2, que en proto3 es cero: ir a donde ya
        /// estás no cuesta nada. Comprobado contra las veinticinco entradas de la captura, donde
        /// el f6 cuadra con la subzona de MapPositions en todas.
        /// </summary>
        public static byte[] BuildZaapList(long here, IEnumerable<ZaapDestination> destinations)
        {
            var hjj = Pb.New().VarIfNotZero(2, here);
            foreach (var destination in destinations)
            {
                hjj.Msg(3, Pb.New()
                    .VarIfNotZero(1, destination.Level)
                    .VarIfNotZero(2, destination.Cost)
                    .Var(5, destination.MapId)
                    .VarIfNotZero(6, destination.SubAreaId));
            }
            return hjj.Build();
        }

        /// <summary>Los kamas que le quedan al personaje (ivf).</summary>
        public static byte[] BuildKamas(long kamas) => Pb.New().Var(1, kamas).Build();

        /// <summary>
        /// "Cierra el diálogo" (kld).
        ///
        /// El cliente NO cierra la ventana del zaap por su cuenta: espera que el servidor se lo
        /// diga. En las capturas sale dos veces con el mismo valor —al llegar al destino, justo
        /// antes del jss, y como respuesta al kla vacío que manda el botón de cerrar— así que el
        /// f1 es una razón fija y no algo que haya que calcular.
        /// </summary>
        public static byte[] BuildDialogClosed() => Pb.New().Var(1, DialogCloseReason).Build();

        private const int DialogCloseReason = 10;

        // ─── World: changing map ────────────────────────────────────────────────

        /// <summary>Takes an actor off the map (jsd). Its own move to another map counts.</summary>
        public static byte[] BuildActorLeft(long contextualId)
            => Push("jsd", Pb.New().Var(2, contextualId).Build());

        /// <summary>"Load this map" (jru).</summary>
        public static byte[] BuildLoadMap(long mapId)
            => Push("jru", Pb.New().Var(2, mapId).Build());

        /// <summary>
        /// The two that travel with jru on every map change of the capture: lqu, which carries a
        /// 120 and the server clock in milliseconds, and hjk, which carries the map id in a packed
        /// list. lqn goes out between them in the capture and does not go out here: its one field
        /// is a number we have not been able to explain (197 on entering the world, 24 on changing
        /// map, 470 after a characteristics reset), and inventing it is worse than leaving it out.
        /// </summary>
        public static byte[] BuildMapClock()
            => Push("lqu", Pb.New()
                .Var(1, 120)
                .Var(2, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds())
                .Build());

        public static byte[] BuildMapDiscovered(long mapId)
            => Push("hjk", Pb.New().Packed(1, new long[] { mapId }).Build());

        /// <summary>
        /// Movement along a map (jsj), which is what the server sends back to a jrw.
        ///
        ///   f1: the cells walked, packed
        ///   f2: how the actor ends up facing
        ///   f5: whose movement it is
        /// </summary>
        public static byte[] BuildActorMoved(long contextualId, IEnumerable<long> cells, int facing)
            => Push("jsj", Pb.New()
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
            var ivx = Pb.New();
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
        /// Una entrada de efecto: el id en f11 y el valor en el campo que le toque.
        ///
        /// El campo no es un hueco cualquiera, es el que dice de qué tipo es el efecto. f4 lleva un
        /// número suelto, f5 un rango y f6 tres números, y los dos últimos son SUBMENSAJES. Meter
        /// un varint donde el cliente espera un submensaje no es un valor raro, es un tipo de
        /// alambre que no cuadra: el cliente no encuentra los parámetros y pinta el arma sin daños
        /// y el dofus con "{spellNoLvl,,}" en lugar del nombre del hechizo.
        /// </summary>
        private static Pb? EffectEntry(Managers.Equipment.ItemEffect effect)
        {
            // Los que no son un número van con su texto en f1: el 988 es "Fabricado por: #4" y el
            // #4 es esta cadena. Sin texto no van, porque la etiqueta saldría vacía.
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
                    entry.VarIfNotZero(4, v1);
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

        // ─── World: títulos y ornamentos ────────────────────────────────────────

        /// <summary>
        /// Lo que uno TIENE (hhy). El cliente ya lleva el catálogo entero dentro; lo que no esté en
        /// esta lista lo pinta en gris.
        ///
        ///   f1: [títulos]   f2: [ornamentos]   los dos empaquetados
        ///
        /// Sale una sola vez, en la entrada al mundo. En la captura de un personaje recién creado
        /// llega con cero bytes: no tiene ninguno todavía.
        /// </summary>
        public static byte[] BuildTitlesOwned(IEnumerable<long> titles, IEnumerable<long> ornaments)
            => Pb.New().Packed(1, titles).Packed(2, ornaments).Build();

        /// <summary>
        /// El título puesto (hid) y el ornamento puesto (hif). Sin nada equipado el mensaje va
        /// VACÍO —no con un cero dentro—, que es como el servidor real dice "ninguno".
        /// </summary>
        public static byte[] BuildTitleUpdated(int titleId)
            => titleId == Managers.Wardrobe.None ? Array.Empty<byte>()
                                                 : Pb.New().Var(1, titleId).Build();

        public static byte[] BuildOrnamentUpdated(int ornamentId)
            => ornamentId == Managers.Wardrobe.None ? Array.Empty<byte>()
                                                    : Pb.New().Var(1, ornamentId).Build();

        /// <summary>
        /// Las "opciones" del personaje dentro del bloque del actor: el título y el ornamento.
        ///
        ///   f5 { f2 { f2: título } }
        ///   f5 { f9 { f1: contador, f4: ornamento } }
        ///
        /// Van repetidas dentro del mismo f3 que ya lleva la cuenta, y la que no se tiene no se
        /// emite. El f9.f1 es un contador propio del personaje que el servidor real reparte sin
        /// patrón visible; aquí se deriva del id para que sea estable.
        /// </summary>
        private static void AddCharacterOptions(Pb humanoidBody, long characterId)
        {
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

        /// <summary>El f2 del bloque de identidad. Vale 3 en los jugadores de las capturas.</summary>
        private const int HumanKind = 3;

        /// <summary>El f7 que cierra el bloque, un solo byte con el mismo valor en toda captura.</summary>
        private static readonly byte[] HumanTrailer = { 0x0b };

        // ─── World: merkasako ───────────────────────────────────────────────────

        /// <summary>
        /// Los muebles colocados en la habitación (jbu), que el cliente espera detrás del mapa.
        ///
        ///   f1 (repetido) { f1: casilla, f2: mueble, f3: giro }
        ///
        /// Es la misma forma que el jbg con el que el cliente los guarda, solo que en f1 en vez de
        /// en f2. En la captura de alguien que lo tiene decorado son mil y pico bytes.
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

        /// <summary>
        /// "El cofre está abierto" (kci). De la captura del cofre de una casa:
        ///
        ///   f1: 100   f3: 4
        ///
        /// Los dos son constantes ahí; el 100 tiene pinta de ser cuántos huecos tiene.
        /// </summary>
        public static byte[] BuildStorageOpened()
            => Pb.New().Var(1, StorageSlots).Var(3, StorageKind).Build();

        private const int StorageSlots = 100;
        private const int StorageKind = 4;

        /// <summary>
        /// Lo que hay dentro del cofre (iwb). Misma forma que el inventario, con la bolsa como
        /// posición de todo: dentro de un cofre no hay nada equipado.
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

        /// <summary>Un objeto que se va (itc del cofre, ium de la bolsa): solo su identificador.</summary>
        public static byte[] BuildItemGone(long uid) => Pb.New().Var(1, uid).Build();

        /// <summary>
        /// Lo que contesta la máquina de la lotería (jbs).
        ///
        ///   f2: el premio       f3: el motivo del rechazo
        ///
        /// De las dos capturas: una sale con f2 y otra, la del "ya la has usado hoy", con f3: 1.
        /// Aquí siempre toca, así que siempre va el f2.
        /// </summary>
        public static byte[] BuildLotteryResult(long prizeUid)
            => Pb.New().Var(2, prizeUid).Build();

        /// <summary>El cofre cerrado (khd). En la captura lleva f3: 11.</summary>
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
