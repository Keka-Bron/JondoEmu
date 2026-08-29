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
        /// </summary>
        /// <remarks>
        /// CINCO, que es lo que manda el servidor real y no una cifra elegida. Medido sobre el
        /// crudo de «desde launcher a eleccion servidor.pcapng»: la pareja de bytes 1005 —el campo
        /// f2 con valor 5— aparece ocho veces en esa respuesta, y el 100 (1064) no aparece ni una.
        ///
        /// Aquí ponía 100, subido a mano con el razonamiento de que «no hay nada que limitar».
        /// Mandar un número que el cliente no ve nunca es justo lo que este proyecto no hace: no se
        /// sabe qué hace con él, y el botón de crear personaje seguía en gris igual.
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
        public static List<byte[]> BuildWelcomeBurst(IReadOnlyList<DatabaseManager.DbCharacter> characters)
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
                Push(Op.Mgq, Pb.New().Var(1, 1).Var(2, 1).Var(3, 1).Build()),
                Push(Op.Mgt, Pb.New().EmptyMsg(2).Build()),
                Push(Op.Hpd, Pb.New().Var(1, 1).Build()),
                Push(Op.Krs),
                Push(Op.Mgz, Pb.New().Var(1, CatalogMark).Build()),
                Push(Op.Kqp, Pb.New().Var(1, 1).Var(2, 1).Build()),
                Push(Op.Kqp, Pb.New().Var(1, 1).Build()),
                Push(Op.Kqp),
                Push(Op.Kvi, BuildCharactersList(characters)),

                // AQUÍ NO VA UN kvd, y mandarlo era lo que tenía muerta media pantalla.
                //
                // Se metió a ojo, con el razonamiento de que «cierra la lista de personajes» y de
                // que el botón de crear estaba apagado porque a la pantalla le faltaba el final.
                // Suena bien y es al revés. Medido sobre las capturas: el kvd sale en TRES, y las
                // tres son de entrar directo al mundo sin pasar por la pantalla —reconexión a un
                // combate y koliseo—, con esta pinta:
                //
                //   kvi(381)  kvd(0)  ipc  kva  mft        vuelve a un combate
                //   kra  kqu  kvd(0)  kva  ivx  hlm        koliseo, y ni siquiera hay kvi
                //
                // Y en la ráfaga de la pantalla de personajes de verdad NO ESTÁ: ni en la de la
                // cuenta con cuatro personajes y el botón activo, ni en la de la cuenta vacía que
                // crea uno, ni en la que falla por límite máximo. Las tres van kvi y detrás jtg.
                //
                // O sea que el kvd significa «no te pares aquí». Mandándolo siempre, el cliente
                // montaba la pantalla como si fuera de paso: el botón de crear personaje sin vida
                // y el de cambiar de servidor sin llevar a ninguna parte.
                Push(Op.Jtg, BuildGiftCatalogue())
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
            { Stat.DodgeActionPoints,    () => Jondo.Unity.Server.Network.SessionContext.State.StatWisdom / 10 },
            { Stat.DodgeMovementPoints,  () => Jondo.Unity.Server.Network.SessionContext.State.StatWisdom / 10 },
            { Stat.WithdrawActionPoints, () => Jondo.Unity.Server.Network.SessionContext.State.StatWisdom / 10 },
            { Stat.WithdrawMovementPoints, () => Jondo.Unity.Server.Network.SessionContext.State.StatWisdom / 10 },
            { Stat.Escape,               () => Jondo.Unity.Server.Network.SessionContext.State.StatAgility / 10 },
            { Stat.Lock,                 () => Jondo.Unity.Server.Network.SessionContext.State.StatAgility / 10 },
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

            // The six the player spends points on.
            var primary = new Dictionary<int, long>
            {
                { Stat.Strength, Jondo.Unity.Server.Network.SessionContext.State.StatStrength },
                { Stat.Vitality, Jondo.Unity.Server.Network.SessionContext.State.StatVitality },
                { Stat.Wisdom, Jondo.Unity.Server.Network.SessionContext.State.StatWisdom },
                { Stat.Chance, Jondo.Unity.Server.Network.SessionContext.State.StatChance },
                { Stat.Agility, Jondo.Unity.Server.Network.SessionContext.State.StatAgility },
                { Stat.Intelligence, Jondo.Unity.Server.Network.SessionContext.State.StatIntelligence },
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
            if (id == Stat.Pods) return BasePods + 5L * Jondo.Unity.Server.Network.SessionContext.State.StatStrength;
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

            AddNpcs(jss, mapId);

            // Behind the actors, which is where the capture puts it.
            var where = MapManager.GetMapInfo(mapId);
            if (where != null) jss.VarIfNotZero(6, where.SubAreaId);

            AddInteractiveElements(jss, mapId);

            return jss.Build();
        }

        /// <summary>
        /// Los NPCs del mapa.
        ///
        /// Van con la misma envoltura que el jugador y que los grupos de monstruos, y lo único que
        /// los distingue es que dentro de f2.f1 aparece el f7 —el jugador usa el f5 y un grupo de
        /// monstruos el f4—:
        ///
        ///   f1 { f1: casilla, f2: orientación }
        ///   f2 { f1 { f7 { f3: género, f5: plantilla } }
        ///        f3 { f1: colores, f2: 3, f3: huesos, f5: escalas, f6: pieles } }
        ///   f3: id contextual, negativo
        ///
        /// El nombre no se manda: el cliente lo saca de sus datos a partir de la plantilla. Y el
        /// bloque de aspecto es la cadena Look de NpcTemplates troceada, comprobado en los
        /// cincuenta y seis NPCs de la captura sin una sola discrepancia.
        ///
        /// Ojo con la escala: el f5 es una lista EMPAQUETADA de varints, no un byte suelto. Una
        /// escala de 200 son dos bytes (c8 01), y escribir el 0xC8 a pelo deja un varint a medias
        /// que revienta el parseo del jss entero en el cliente —con él, el mapa se queda sin
        /// dibujar del todo, ni NPCs ni monstruos ni personaje—.
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
            foreach (var npc in Managers.Npcs.Of(mapId))
            {
                var look = Pb.New();
                if (npc.Colors.Length > 0) look.Packed(1, npc.Colors);
                look.Var(2, LookKind);
                look.VarIfNotZero(3, npc.Bones);
                if (npc.Scales.Length > 0) look.Packed(5, npc.Scales);
                if (npc.Skins.Length > 0) look.Packed(6, npc.Skins);

                // El género sólo viaja cuando vale 1. Comprobado en las cincuenta y seis plantillas
                // de la captura: las veinte con género 1 lo mandan, las treinta y cinco con género
                // 0 lo omiten —eso es proto3— y la única con género 2, que es la montaña de kamas,
                // tampoco manda nada. O sea que el campo no es el género de la plantilla tal cual,
                // sino que sólo se pone cuando es exactamente 1.
                var template = Managers.Npcs.TemplateOf(npc.NpcId);
                bool female = template != null && template.Gender == NpcFemale;

                // LA MARCA DE LA CABEZA VA AQUI DENTRO, y esto es lo que faltaba para que no
                // saliera nunca la exclamacion verde.
                //
                // No la dibuja el iom. Se diferenciaron las dos cosas comparando byte a byte el
                // mismo mapa y el mismo actor: en el jss de Ankama del mapa 154010883, actor
                // -20000 (el NPC 2892), hay seis bytes que en el nuestro no estaban:
                //
                //   Ankama  ...1217 0a0b3a09 12041a02e00c 28cc16 1a08...
                //   Jondo   ...1211 0a053a03            28cc16 1a08...
                //                            ^^^^^^^^^^^^
                //                            f2 { f3: packed[1632] }
                //
                // 1632 es justo la mision que ese NPC reparte en ese mapa. Fuera de esos seis
                // bytes -y de los dos largos que crecen con ellos- las tramas son identicas. Lo
                // mismo en el 154010371 con el NPC 2905 y la mision 1639.
                //
                // Y hay una captura, "sin apariencias equipar un escudo", que NO lleva ni un iom
                // en todo el flujo y sin embargo sus NPCs salen marcados: la marca no puede venir
                // del iom. El iom es otra cosa -un indice de toda la SUBZONA, que nombra mapas en
                // los que el jugador no esta-, y por eso el que sigue a aceptar la mision 2432
                // nombra la 2427: son dos mapas distintos de la misma subzona 980.
                //
                //   f3  las que OFRECE      -> la exclamacion. 21 de 21 ids medidos son misiones
                //                              cuyo catalogo nombra a ESE npc en ESE mapa.
                //   f1  las que tiene EN CURSO y quieren algo de el.
                //
                // Va delante del genero, que es el orden de todas las capturas: 12041a02e70c 1801
                // 28d916. Y cuando no hay nada que decir no se manda el bloque: la pareja de bytes
                // 1200 no aparece ni una vez en las 145 tramas iom reales.
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
            foreach (var interactive in Managers.InteractiveRegistry.OnMap(mapId))
                Declare(jss, interactive);

            AddQuestElements(jss, mapId);
        }

        /// <summary>
        /// Lo que sólo ve quien lleva la misión: la estela, el catalejo, el cartel.
        ///
        /// Va aparte del registro a propósito. El registro es del mundo y es igual para todos —el
        /// zaap está para cualquiera—, y esto es de UN jugador: la estela aparece al coger la
        /// misión y se va al cumplir su objetivo. Meterlo en el registro habría hecho falso lo
        /// primero.
        ///
        /// Que se pueda preguntar por jugador aquí no es nuevo: <see cref="Declare"/> ya mira el
        /// nivel de oficio de quien mira el mapa para decidir si un recurso se le ofrece o se le
        /// pinta en rojo. Este jss se construye una vez por jugador y por llegada al mapa.
        ///
        /// La habilidad va en el f4 y sin estado, como todo lo que no es recurso. El 114 es
        /// «Utiliser», la misma que el cliente usa para el vestigio de anomalía, y de ella dicen
        /// las capturas que el cliente contesta con su iwo igual.
        /// </summary>
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
        /// Un elemento clicable: qué es, qué se puede hacer con él y dónde está.
        ///
        /// Los RECURSOS de oficio se declaran distinto según estén llenos o no, y hay que
        /// respetarlo o el cliente ofrece segar un trigo ya segado:
        ///
        ///   lleno     f11 { f1:1, f2:0, f4 { uid, habilidad }, ... }   f15 sin f4
        ///   agotado   f11 { f1:1,       f3 { uid, habilidad }, ... }   f15 f4 = 1
        ///   en uso    igual que agotado                                f15 f4 = 2
        ///
        /// Es decir, la habilidad se muda del campo 4 al 3 cuando deja de poder usarse. Medido en
        /// los veinticinco fresnos de un mismo mapa, sin una excepción. Todo lo que no es recurso
        /// —zaaps, cofres, puertas— va siempre en el 4 y sin estado, como hasta ahora.
        /// </summary>
        private static void Declare(Pb jss, Managers.RegisteredInteractive interactive)
        {
            bool gathering = Managers.Resources.Is(interactive.MapId, interactive.Element.Id);
            var state = gathering
                ? Managers.Resources.StateOf(interactive.MapId, interactive.Element.Id)
                : Managers.ResourceState.Full;

            // Y el nivel de oficio de QUIEN esté mirando el mapa. Un recurso que le queda grande
            // se declara igual que uno agotado, y el cliente lo pinta en rojo y no deja clicarlo:
            // es como lo hace el juego real, sin decirle nada a nadie por el chat.
            bool alcanza = !gathering || Managers.Resources.WithinReach(
                interactive.MapId, interactive.Element.Id);

            bool usable = !gathering || (state == Managers.ResourceState.Full && alcanza);

            var declaration = Pb.New().Var(1, 1);

            // El f2 sale a cero en la madera, el trigo y la salvia, y a 1 o 3 en los dos
            // caladeros. No se ha sabido qué lo distingue, así que va el cero, que es lo medido
            // en tres de los cuatro oficios.
            if (gathering && usable) declaration.Var(2, 0);

            foreach (var action in interactive.Actions)
            {
                declaration.Msg(usable ? 4 : 3, Pb.New()
                    .Var(1, action.SkillInstanceId)
                    .Var(2, action.SkillId));
            }

            jss.Msg(11, declaration
                .Var(5, interactive.Element.Id)
                .Var(6, interactive.Type));

            DeclarePlacement(jss, interactive.Element,
                gathering && !usable ? state : Managers.ResourceState.Full);
        }

        /// <summary>
        /// Dónde está un elemento y en qué estado (f15).
        ///
        /// El f15 acompaña SIEMPRE a un f11 y nunca va solo. Medido sobre las 305 capturas del
        /// juego real: en los 834 jss que llevan elementos hay 4.493 f11 y sólo 2.685 f15, y un
        /// f15 cuyo elemento no tenga su f11 aparece 3 veces —una sola vez en cada uno—, o sea
        /// el 0,36 %. Al revés pasa en 615 de los 834: un elemento con acción al que el servidor
        /// no le manda colocación.
        ///
        /// Es decir: el f15 es un SUBCONJUNTO del f11, no un superconjunto. Declarar uno por cada
        /// elemento del mapa —los 46.309 de interactive_elements.json, repartidos en 9.840 mapas,
        /// hasta 71 en el peor— pondría a Jondo a mandar lo contrario de lo que manda Ankama.
        ///
        /// El f1 dice que el elemento es de este mapa. La ausencia del f4 es el estado activo, que
        /// por eso no se escribe. El dibujo no viaja: el cliente lo saca de sus propios datos de
        /// mapa a partir del número del elemento.
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
        /// <summary>
        /// De dónde sale el hechizo: 1 los de la clase, 2 los que no lo son —el cuerpo a cuerpo,
        /// los de objeto, los de montura—.
        /// </summary>
        private const int OrigenQueNoEsDeClase = 2;

        /// <summary>
        /// El cuerpo a cuerpo es el HECHIZO CERO, y no hay que inventárselo: está en la base como
        /// <c>SpellTemplates.Id 0</c>, con el nombre 64658 —"Puñetazo"— y un único grado, el
        /// <c>SpellLevels.Id 10461</c>, de 3 PA y alcance 1.
        ///
        /// El servidor real lo manda como una entrada más de la lista de hechizos, con el número
        /// omitido —proto3 no escribe los ceros— y el origen a 2: los bytes son <c>08 01 20 02</c>.
        /// Está en las nueve capturas que traen un hms, desde el personaje de nivel 1 del tutorial
        /// hasta el de nivel 200. Sin ella el cliente no tiene ficha que poner en la casilla del
        /// arma: fuera de combate no la dibuja, y en combate la dibuja pero sin nada que lanzar
        /// —de ahí que saliera apagada y con el texto de objeto sin resolver, con las filas de
        /// "[QUANTITÉ EN INVENTAIRE]" y "[Valeurs théoriques]"—.
        /// </summary>
        private const int GradoDelCuerpoACuerpo = 1;

        public static byte[] BuildSpellList(int breed, int level)
        {
            var hms = Pb.New();

            hms.Msg(1, Pb.New().Var(1, GradoDelCuerpoACuerpo).Var(4, OrigenQueNoEsDeClase));

            foreach (var spell in SpellTable.KnownFor(breed, level, Managers.SpellChoices.Chosen))
            {
                hms.Msg(1, Pb.New().Var(1, spell.Grade).Var(3, spell.SpellId).Var(4, 1));
            }

            // El f2 suelto del final, que es el mismo tipo de descuido que tuvo la barra de
            // hechizos con su itg: va detrás de la lista, parece un hueco más y sin él vale cero.
            //
            // Está en las NUEVE capturas que traen un hms, desde el personaje de nivel 1 del
            // tutorial hasta el de nivel 200, y no salía en ninguno de los 138 que ha mandado este
            // emulador. Lo que se sospecha que apaga es la previsualización de daños: el cliente
            // lleva dentro un interruptor que se llama isDamagePreviewEnabled, y el texto de ayuda
            // del propio juego describe con una sola frase las dos mitades que faltaban —el daño
            // estimado y el desplazamiento—. No está demostrado que sea éste el interruptor; lo que
            // sí está medido es que el servidor real lo manda siempre y nosotros nunca.
            return hms.Var(2, 1).Build();
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
            var layout = Managers.FightSpellLayout.Current(breed, level);

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

        // ─── Recolección ────────────────────────────────────────────────────────

        /// <summary>
        /// El estado de un recurso (iwf): { f1 { f2: casilla, f3: elemento, f4: estado } }.
        ///
        /// Cero es lleno y el servidor real no manda el campo; 1 agotado y 2 en uso. Son los
        /// mismos números de campo que el f15 del jss, sin el f1 de aquél.
        /// </summary>
        public static byte[] BuildElementState(int cell, int elementId, int state)
            => Pb.New().Msg(1, Pb.New()
                .Var(2, cell)
                .Var(3, elementId)
                .VarIfNotZero(4, state)).Build();

        /// <summary>
        /// Vuelve a declarar un recurso (iwm) para que su habilidad deje de poder usarse, o
        /// vuelva a poder: { f3 { la misma forma que el f11 del jss } }.
        ///
        /// Es el mensaje que apaga el trigo recién segado sin tener que reenviar el mapa entero.
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
        /// El gesto de recolectar (iwn): { f2: elemento, f3: décimas, f4: habilidad, f5: quién }.
        ///
        /// OJO, no es el mismo iwn que el de usar un zaap o un taller. Aquél lleva f1 = 1 y no
        /// lleva duración; éste es al revés: sin f1 y con el f3. Medido en las cuatro capturas de
        /// oficio, y el f3 vale 30 en las cuatro —tres segundos— con el tiempo real entre este
        /// mensaje y el de fin midiendo 2.996, 2.999, 3.037 y 3.064 milisegundos.
        /// </summary>
        public static byte[] BuildGatherStarted(int elementId, int tenths, int skillId,
                                                long characterId)
            => Pb.New()
                .Var(2, elementId)
                .Var(3, tenths)
                .Var(4, skillId)
                .Var(5, characterId)
                .Build();

        /// <summary>Se acabó el gesto (iwi): { f1: elemento, f3: habilidad }.</summary>
        public static byte[] BuildGatherFinished(int elementId, int skillId)
            => Pb.New().Var(1, elementId).Var(3, skillId).Build();

        /// <summary>Lo recogido en esta pasada (itn): { f1: objeto, f2: cantidad }.</summary>
        public static byte[] BuildGathered(int itemId, int quantity)
            => Pb.New().Var(1, itemId).Var(2, quantity).Build();

        /// <summary>
        /// La experiencia de un oficio (irq): { f1 { f1: oficio, f2: siguiente nivel, f3: nivel,
        /// f4: suelo del nivel, f5: acumulada } }.
        ///
        /// Manda TOTALES, no incrementos. El f2 desaparece cuando el oficio está al tope, que es
        /// como salía el leñador de nivel 200 en la captura de la madera.
        /// </summary>
        public static byte[] BuildJobExperience(int jobId, long next, int level, long floor,
                                                long experience)
            => Pb.New().Msg(1, Pb.New()
                .Var(1, jobId)
                .VarIfNotZero(2, next)
                .VarIfNotZero(3, level)
                .VarIfNotZero(4, floor)
                .VarIfNotZero(5, experience)).Build();

        /// <summary>Cambia la cantidad de un objeto que ya estaba en la bolsa (ivj).</summary>
        public static byte[] BuildItemQuantity(long uid, int total)
            => Pb.New().Msg(3, Pb.New().Var(2, uid).Var(3, total)).Build();

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

        /// <summary>Serialized actor block used by map snapshots outside this protocol builder.</summary>
        public static byte[] BuildPlayerActorBlock(DatabaseManager.DbCharacter character, int cell,
                                                   int facing, long accountId)
            => PlayerActor(character, cell, facing, accountId).Build();

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

        /// <summary>
        /// Se acabó de usar un interactivo (iwi). Misma forma que el fin de recolección: el f1 es
        /// el elemento y el f3 la habilidad.
        ///
        /// Un teleport es instantáneo, pero hay que soltarlo igual ANTES del jru: si no, el
        /// cliente se puede quedar con el elemento marcado como ocupado en su caché y al volver
        /// al mapa su gráfico ya no aparece.
        /// </summary>
        public static byte[] BuildInteractiveUseEnded(int elementId, int skillId)
            => Pb.New().Var(1, elementId).Var(3, skillId).Build();

        /// <summary>Un destino de la lista de zaaps.</summary>
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
            /// En qué pestaña lo pone el cliente: 0 el zaap, 1 el zaapi, 4 la anomalía.
            ///
            /// El zaap normal no manda el campo —proto3 se come el cero— y por eso durante un
            /// tiempo pareció que no existía. Sale en las 69 entradas de las capturas de zaapi
            /// con valor 1 y en las 27 de anomalía con valor 4.
            /// </summary>
            public int Kind { get; }

            /// <summary>Minutos que le quedan a la anomalía. Sólo lo llevan las anomalías.</summary>
            public int MinutesLeft { get; }

            /// <summary>Minutos que dura. Cero en todo lo que no sea una anomalía.</summary>
            public int Duration { get; }
        }

        /// <summary>
        /// La lista de zaaps (hjj).
        ///
        ///   f2: el mapa donde está el zaap que se ha abierto
        ///   f3 (repetido) { f1: nivel de la zona, f2: lo que cuesta, f3: pestaña,
        ///                   f4 { f2: minutos que quedan, f3: minutos que dura },
        ///                   f5: mapa, f6: subzona }
        ///
        /// El destino en el que uno ya está viaja sin f2, que en proto3 es cero: ir a donde ya
        /// estás no cuesta nada. Comprobado contra las veinticinco entradas de la captura, donde
        /// el f6 cuadra con la subzona de MapPositions en todas.
        ///
        /// Las tres pestañas van en esta misma lista, no en mensajes distintos: el f3 dice en cuál
        /// cae cada entrada y el f4 de la entrada sólo lo llevan las anomalías, que caducan. Ver
        /// <see cref="Managers.Anomalies"/>.
        ///
        /// ─── El f4 de la RAÍZ: qué ventana abre el cliente ──────────────────────────────────
        ///
        /// No basta con mandar los destinos buenos: el cliente decide QUÉ VENTANA pinta por este
        /// campo, y no por el tipo del elemento que se ha clicado. Vale 0 el zaap —proto3 se lo
        /// come y no viaja—, 1 el zaapi y 3 el barco. Sale igual en las doce listas capturadas:
        ///
        ///   3 capturas de zaapi   f4 = 1, y sin f2
        ///   8 capturas de zaap    sin f4, y con f2
        ///   1 captura de barco    f4 = 3 Y f2: los dos campos son independientes
        ///
        /// Sin este 1, al clicar un zaapi el cliente abre la ventana del ZAAP —pestañas Zaap,
        /// Anomalía y Prisma— y como todos los destinos que le llegan son de zaapi, ninguna de
        /// esas pestañas los recoge y la ventana sale con «Ningún destino». La del zaapi es otra:
        /// se titula «Zaapi» y sus pestañas son Talleres, Mercadillos y Varios.
        ///
        /// Va al FINAL del mensaje, detrás de todos los destinos, que es donde lo pone el
        /// servidor real.
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

                // El reloj, sólo si lo tiene: al zaap y al zaapi el servidor real no se lo manda.
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
        public static byte[] BuildDialogClosed(int reason = DialogCloseReason)
            => Pb.New().Var(1, reason).Build();

        private const int DialogCloseReason = 10;

        /// <summary>
        /// La razón con la que se cierra el diálogo de un NPC, que no es la del zaap.
        ///
        /// En la captura del servidor de torneos el kld que cierra la conversación con la montaña
        /// de kamas lleva f1: 1, y sale las cuatro veces —tanto al aceptar como al rechazar—. El 10
        /// es el del zaap y hay un 5 midiendo otra ventana, así que son razones distintas de cierre
        /// y no un valor fijo; la regla que las separa no está descifrada.
        /// </summary>
        public const int NpcDialogCloseReason = 1;

        // ─── World: NPCs, their dialogue and their shops ────────────────────────

        /// <summary>
        /// El servidor abre la ventana de diálogo (ioc). Sólo devuelve a quién se le está hablando
        /// y dónde; no lleva ni f1 ni f2 ni f3.
        ///
        ///   f4: mapa      f5: id contextual del NPC
        /// </summary>
        public static byte[] BuildNpcDialog(long mapId, long contextualId)
            => Pb.New().Var(4, mapId).Var(5, contextualId).Build();

        /// <summary>
        /// La pregunta y sus respuestas (ios).
        ///
        ///   f1: id del mensaje
        ///   f2 (repetido) { f1: id de respuesta, f3 (repetido) { f1: id de efecto } }
        ///
        /// El f3 de cada respuesta es lo que la respuesta promete: en la montaña de kamas la que
        /// paga anuncia los efectos 194 ("+#1{{~1~2 a }}#2 kamas"), 193 y 351, y la que rechaza va
        /// sin ninguno. Aquí se manda sin f3, que es como viaja la respuesta de rechazo en la
        /// captura: se pierde el iconito del premio y nada más. Los ids de efecto no están en
        /// NpcTemplates, así que ponerlos sería inventárselos.
        /// </summary>
        public static byte[] BuildNpcQuestion(long messageId, IEnumerable<long> replies)
        {
            var ios = Pb.New().Var(1, messageId);
            foreach (long reply in replies)
            {
                ios.Msg(2, Pb.New().Var(1, reply));
            }
            return ios.Build();
        }

        /// <summary>
        /// El catálogo entero de una tienda (kbd), de una sola vez.
        ///
        ///   f1 (repetido) { f1: objeto
        ///                   f3 { f2: precio, f3: -1, f4: criterio }
        ///                   f4 (repetido): un efecto, con el id en f11 }
        ///   f2: el mismo id contextual que pidió el iov
        ///
        /// No está paginado: en la captura hay cincuenta y seis iov de tienda y cincuenta y seis
        /// kbd, uno a uno, y el mayor son 26.902 bytes con 444 entradas. Como no hay ni un caso de
        /// dos kbd para una misma tienda, tampoco hay prueba de que el cliente sepa juntarlos, así
        /// que el reparto en vendedores pequeños que hace el servidor real es también lo seguro.
        ///
        /// El f3.f3 vale -1 en las 6.106 entradas medidas. Se manda igual aunque no se sepa qué
        /// significa exactamente: lo único seguro es que el cliente lo recibe siempre así.
        ///
        /// El criterio de texto —"(SC=3|Sc=3500)" en el servidor de torneos— NO se manda. El SC=3
        /// es "servidor de torneo" y el propio servidor lo valida: las catorce entradas con el
        /// criterio más duro dieron error 243 al comprarlas. Aquí no somos un servidor de torneo, y
        /// hay doce entradas medidas que viajan sin criterio ninguno, así que se omite.
        ///
        /// El f3 es LA MONEDA, y con él una tienda cobra en un objeto en vez de en kamas. No hay
        /// que inventar nada: el cliente ya lo sabe hacer. Medido sobre las 305 capturas, hay 60
        /// kbd y 58 llevan sólo f1 y f2 —ésos cobran en kamas—; los otros dos llevan además el
        /// f3, con el id del objeto que hace de moneda:
        ///
        ///   f3 = 13052   «Sebuscalón»   (la tienda de la Torre de los Viajeros)
        ///   f3 = 30529   «Fidelicha»    (una de Pandala)
        ///
        /// Si el f3 no está, se cobra en kamas, que es por lo que va con VarIfNotZero: una tienda
        /// normal sigue mandando exactamente los mismos bytes que antes.
        ///
        /// OJO CON EL PRECIO. Se recibe la tienda entera y no sólo el id de la moneda, y es a
        /// propósito: la primera versión mandaba el f3 con la ficha pero seguía poniendo en cada
        /// entrada el precio EN KAMAS, así que el cliente enseñaba una capa a «1 ficha» y al
        /// comprarla el servidor cobraba 150. El precio que se enseña y el que se cobra tienen
        /// que salir del mismo sitio, y por eso salen los dos de aquí.
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

        /// <summary>Las existencias de la tienda. Constante en las 6.106 entradas de la captura.</summary>
        private const int ShopUnlimited = -1;

        /// <summary>
        /// La tienda se ha cerrado (khd). El f3 vale 11 en las cincuenta y seis de la captura.
        /// </summary>
        public static byte[] BuildShopClosed() => Pb.New().Var(3, ShopClosedKind).Build();

        private const int ShopClosedKind = 11;

        /// <summary>
        /// Un mensaje de información (lqn), que es COMO SE LE HABLA AL JUGADOR.
        ///
        ///   f1: el tipo           f2: qué mensaje       f4 (repetido): sus parámetros
        ///
        /// El servidor no manda texto: manda dos números y el cliente pone la frase, ya traducida,
        /// sacándola de InfoMessagesDataRoot. El tipo decide cómo la pinta —0 información, 1 aviso—
        /// y proto3 se come el cero, que es por lo que en las capturas unos lqn llevan f1 y otros
        /// no. Ver <see cref="Managers.InfoMessages"/>.
        ///
        /// Esto y no una línea de chat: el chat sale por el canal general y lo lee todo el mundo.
        /// </summary>
        public static byte[] BuildSystemMessage(int messageId, params string[] parameters)
            => BuildInfoMessage(Managers.InfoMessages.Info, messageId, parameters);

        /// <summary>El mismo, diciendo de qué tipo es.</summary>
        public static byte[] BuildInfoMessage(int type, int messageId, params string[] parameters)
        {
            var lqn = Pb.New().VarIfNotZero(1, type).Var(2, messageId);
            foreach (string parameter in parameters) lqn.Str(4, parameter);
            return lqn.Build();
        }

        /// <summary>
        /// El aviso de la última conexión, con su fecha y la dirección desde la que se hizo.
        ///
        /// El cliente tiene dos plantillas y la diferencia es la IP:
        ///
        ///   193  «Última conexión a esta cuenta realizada el {2}/{1}/{0} a las {3}:{4}»
        ///   152  la misma «… mediante la dirección IP {5}»
        ///
        /// Los parámetros van en orden año, mes, día, hora, minuto y dirección — el orden de la
        /// plantilla no es el de lectura, y el bloque grabado lo confirma: manda el 193 con
        /// ["2026","08","09","18","53"] y el cliente pinta «09/08/2026 a las 18:53».
        ///
        /// Sin IP se manda el 193, que es exactamente lo que hace el servidor real cuando no la
        /// tiene: enseñar una dirección vacía queda peor que no enseñarla.
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

        /// <summary>«Última conexión… a las {3}:{4}», sin dirección.</summary>
        public const int LastConnectionMessage = 193;

        /// <summary>La misma, «… mediante la dirección IP {5}».</summary>
        public const int LastConnectionWithIpMessage = 152;

        /// <summary>El mensaje de "has recibido kamas", con la cifra como parámetro.</summary>
        public const int KamasReceivedMessage = 45;

        /// <summary>El de "comprado": objeto, uid, cantidad y precio.</summary>
        public const int PurchaseMessage = 252;

        /// <summary>
        /// El mismo aviso pero cuando se ha pagado en fichas. Medido en la tienda de la Torre de
        /// los Viajeros: seis parámetros, «798, 1055401001, 1, 20, 13052, 0», que son el objeto
        /// comprado y su uid, la cantidad, el precio, y el id y el uid de la moneda.
        /// </summary>
        public const int TokenPurchaseMessage = 364;

        // ─── World: changing map ────────────────────────────────────────────────

        /// <summary>
        /// Saca a un actor del mapa (jsd): quién se va y POR DÓNDE.
        ///
        /// El por dónde faltaba, y es lo que dejaba al personaje plantado en el borde en la
        /// pantalla de los demás en vez de desaparecer. Le llegaba el aviso —está medido en el
        /// registro del servidor— y el cliente no hacía nada con él.
        ///
        /// La captura de un grupo siguiendo al líder por mapas cercanos lo enseña claro, con
        /// veinticinco de estos: el campo 3 sólo vale 2, 4 ó 6, que son las direcciones
        /// cardinales de Dofus (0 derecha, 2 abajo, 4 izquierda, 6 arriba). El cliente saca al
        /// muñeco andando hacia ese lado y entonces lo borra.
        ///
        ///   10 a282f0a6c408 18 06     quién, y se fue por arriba
        ///   10 a282f0a6c408           quién, y ya está
        ///
        /// Los que van sin dirección son las salidas que no tienen ninguna: por el zaap uno no se
        /// va hacia ningún lado, desaparece. Por eso es opcional.
        /// </summary>
        public static byte[] BuildActorLeft(long contextualId, int? porDonde = null)
        {
            var pb = Pb.New().Var(2, contextualId);
            if (porDonde.HasValue) pb.Var(3, porDonde.Value);
            return Push(Op.Jsd, pb.Build());
        }

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
        /// Los zaaps que el personaje lleva DESCUBIERTOS (hjk), en una lista empaquetada.
        ///
        /// Esto no es «qué mapas has visto»: es la única razón por la que el cliente enseña algo
        /// en la ventana de viaje. El servidor real manda esta lista entera al entrar al mundo
        /// —182 bytes con 45 mapas, y los 45 son zaaps activados— y luego uno suelto cada vez que
        /// se pisa un zaap nuevo. Sin ella el cliente no da por descubierto ninguno y la ventana
        /// sale con «Ningún destino» por muchos destinos que traiga el hjj.
        ///
        /// El emulador no guarda descubrimientos por personaje —aquí se tienen todos— así que la
        /// lista es siempre la misma: todos los zaaps activados de los que además se sabe dónde
        /// está su elemento, que son de los que se puede salir.
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

        // ─── Grupos ─────────────────────────────────────────────────────────────

        /// <summary>
        /// Te han invitado a un grupo (ijz): saca la ventanita.
        ///
        ///   f1: a quién invitan   f2: quién invita   f3: plazas
        ///   f5: el grupo          f6: ¿?             f7: el nombre de quien invita
        ///
        /// Medido: 08a28280c8e708 10a282f0a6c408 1808 28e8ac04 3001 3a064861726d6f6f, o sea
        /// invitado 302677754146, anfitrión 293213045026, ocho plazas, grupo 71272, y «Harmoo».
        /// El f6 vale 1 en una captura y 2 en otra y no se ha sabido qué distingue; se manda 1,
        /// que es el de la invitación que se acepta.
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

        /// <summary>Se acabó la invitación, para quien la rechaza (ilo): { f1: grupo, f2: quién invitaba }.</summary>
        public static byte[] BuildInvitationClosed(int partyId, long hostId)
            => Pb.New().Var(1, partyId).Var(2, hostId).Build();

        /// <summary>Quita al invitado de la lista, para quien invitó (iko): { f1: invitado, f2: grupo }.</summary>
        public static byte[] BuildInvitationWithdrawn(long guestId, int partyId)
            => Pb.New().Var(1, guestId).Var(2, partyId).Build();

        /// <summary>El grupo se ha deshecho (imy): { f1: grupo }.</summary>
        public static byte[] BuildPartyDissolved(int partyId) => Pb.New().Var(1, partyId).Build();

        /// <summary>Te has salido (ils): { f1: grupo }.</summary>
        public static byte[] BuildPartyLeft(int partyId) => Pb.New().Var(1, partyId).Build();

        /// <summary>
        /// Hay jefe nuevo (ilx): { f1: el nuevo jefe, f2: el grupo }.
        ///
        /// Once bytes, y NO se reenvía el grupo entero: se comprobó comparando la ficha del mismo
        /// grupo antes y después del cambio, y lo único que cambia es su campo 4.
        /// </summary>
        public static byte[] BuildPartyLeader(long leaderId, int partyId)
            => Pb.New().Var(1, leaderId).Var(2, partyId).Build();

        /// <summary>
        /// Un mensaje privado (kth): { f1: fecha, f4: vacío, f5: id del otro, f6: su nombre,
        /// f7: el texto }.
        ///
        /// Medido de la captura del gremio, donde el susurro a «Hiierbita-Xx» SÍ llegó:
        ///
        ///   0a19 «2026-08-12T22:54:29+02:00»  2200  28 a282acfea805
        ///   320c «Hiierbita-Xx»  3a04 «hola»
        ///
        /// Ojo con dos cosas. No lleva CANAL: el cliente sabe que es privado por el propio
        /// mensaje, y por eso mandarlo como un kti por el canal 9 no pinta nada. Y lo que lleva no
        /// es quién habla sino EL OTRO —en tu copia, a quién se lo dices—, así que el mismo
        /// mensaje sirve para los dos lados cambiando de quién se pone la identidad.
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
        /// La ventana de subida de nivel (kua): { f1: el nivel nuevo }.
        ///
        /// Dos bytes, y con eso el cliente saca la ventana entera —música, animación y los datos
        /// del nivel— y la deja abierta hasta que el jugador la cierra. No contesta nada al
        /// cerrarla, así que no hay nada que escuchar.
        ///
        /// Sale exactamente dos veces en las 305 capturas, las dos en el tutorial y en el
        /// milisegundo justo de cada subida: 0802 al pasar a nivel 2 y 0803 al pasar a nivel 3.
        /// Detrás van iun, kub y kfe, pero esos tres salen también al entrar al mundo sin subir
        /// nada, así que el único mensaje propio de la subida es éste.
        ///
        /// Lo que la ventana enseña —puntos ganados, vida, hechizos— lo saca el cliente del kub
        /// que va detrás, no de aquí. Por eso hay que mandar el kua ANTES de las características
        /// nuevas, que es el orden de la captura.
        /// </summary>
        public static byte[] BuildLevelUp(int level) => Pb.New().Var(1, level).Build();

        /// <summary>
        /// El chat no ha podido con algo (ktl), con el motivo en su unico campo. Medido: 0802 es
        /// lo que contesta el servidor real al susurrarse a uno mismo.
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
