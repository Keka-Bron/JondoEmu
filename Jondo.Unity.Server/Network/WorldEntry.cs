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
        /// La etiqueta con la que content/world/entry.json marca la barra de HECHIZOS.
        /// </summary>
        /// <remarks>
        /// Hay dos itg en la secuencia y sólo se distinguen por lo que el manifiesto diga: antes se
        /// miraba dentro del payload —f6 objetos, f9 hechizos— y ese payload ya no viaja. Es una
        /// cadena y no un enum porque la escribe tools/decode_world_entry.py, que es de otro
        /// lenguaje; si las dos dejan de coincidir, WorldEntryContentTests lo dice.
        /// </remarks>
        public const string SpellBarLabel = "ConnectionProtocol.BuildSpellBar";

        /// <summary>Los tres bloques, con el nombre que les da content/world/entry.json.</summary>
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
        ///        Ese «f6 para objetos y f9 para hechizos» que ponía aquí era falso y estuvo
        ///        contradiciendo a HoldsSpells, tres metros más abajo, que hacía lo contrario y
        ///        acertaba. Medido sobre las dos tramas: la de 436 bytes lleva 44 huecos con f6 y
        ///        los 44 valores son ids de SpellTemplates de verdad, del 350 al 24.017; la de
        ///        1.208 usa f9 y sus valores llegan a 507.645.866, que son uid de objeto.
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

            // Los adornos de la cuenta capturada. No son nombres, pero son suyos igual, y el
            // emulador ya manda los del personaje que se conecta:
            //
            //   hhy  los títulos y ornamentos que ESA cuenta tiene: 62 y 28. Llegaban al elegir
            //        personaje y luego el emulador mandaba los suyos —los 539 y los 167— al entrar
            //        al mapa, así que el cliente recibía dos listas distintas y la primera era de
            //        otro. Lo manda WardrobeHandler.SendOwnedAsync.
            //   lyt  los conjuntos guardados del vestuario. Se queda en la lista, aunque en estos
            //        bloques no hay ninguno: sale 8 veces en las capturas y el dia que se replique
            //        otra sesion conviene que ya este fuera.
            //
            // OJO CON ESTA PAREJA. El bloque 1 trae un «lty», que NO es este «lyt»: son dos opcodes
            // distintos —lty sale 7 veces en las capturas y lyt 8— y durante un tiempo el comentario
            // de aqui describia el lty creyendo que era el lyt, con lo que esta entrada de la lista
            // no filtraba absolutamente nada y nadie lo notaba. El lty de verdad son cinco huecos
            // vacios (todo -1 y 5) con la fecha de creacion de la cuenta, asi que no lleva nada de
            // nadie y sigue viajando; pero la leccion es que un opcode de tres letras se confunde
            // con su anagrama sin que salte ningun error.
            Op.Hhy, Op.Lyt,

            // El diario de misiones de la cuenta capturada: 261 tramas en el bloque 1 y 4 más en
            // el del mapa, cada una con una misión, su paso y sus objetivos. Es de otro por partida
            // doble: son las misiones que llevaba esa cuenta, y además contradicen a lo que el
            // servidor cree, porque desde que hay motor de misiones el cliente recibiría 261
            // misiones que su personaje no tiene y ninguna de las que sí.
            //
            // No es una suposición sobre qué es idu: en las 401 capturas hay 448 tramas suyas y las
            // 448 nombran un paso que pertenece de verdad a la misión que nombran, con 1.479
            // objetivos que pertenecen de verdad a ese paso.
            //
            // En su lugar va lo nuestro, desde la base: Managers.Quests.SendJournalAsync.
            Op.Idu,

            // Y los contadores de esa cuenta: 9.694 pares de id y valor, con ids del 44 al 34.352 y
            // valores de cientos de millones. Es lo que el cliente pinta en el panel de estadisticas,
            // y son las suyas: partidas jugadas, monstruos matados, kamas ganados. 87.878 bytes, el
            // 88 % de todo lo que quedaba copiandose tal cual. Una cuenta nueva no tiene ninguna.
            Op.Ivi,

            // Y EL MISMO DIARIO OTRA VEZ, entero y en una sola trama. Quitar sólo el idu no cambió
            // nada de lo que el jugador ve, porque el bloque trae las dos cosas: 261 tramas idu
            // sueltas Y este idr con esas mismas 261 dentro, más 548 misiones dadas por terminadas.
            //
            //   f1 (repetido)  una misión en curso, con la forma del cuerpo del idu
            //   f3 (repetido)  { f1: 1, f2: id de misión }, una terminada
            //   f4             uno, vacío
            //
            // Los 261 ids del f1 son misiones de verdad, los 261, y los 548 del f3 también: son
            // exactamente los dos números que el cliente escribe en sus pestañas. Con esto entrando,
            // el jugador veía todas las de Incarnam y Astrub como hechas, ningún NPC con la marca
            // verde encima y ninguno dispuesto a dar nada, porque para el cliente ya las tenía.
            //
            // En su lugar va el nuestro, con la misma forma, desde CharacterQuests.
            Op.Idr,

            // Los logros de esa cuenta: 954 entradas, y los 954 ids que llevan son logros de verdad.
            // Es la razón de que el personaje entrara con todos los logros del jugador capturado.
            // Todavía no se manda nada en su lugar, que es lo que ve una cuenta nueva.
            Op.Mft,
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

            // Y si los bloques no traen ningún kub —que es lo que pasa: ninguno de los tres lo
            // lleva—, la lista sale del fichero medido.
            AprenderDelFichero();
        }

        /// <summary>
        /// La lista de características y su hueco, sacada de las capturas.
        ///
        /// Los tres bloques de datos/ NO contienen ni un kub, así que lo de arriba se quedaba
        /// siempre con la lista vacía y la ficha caía en la de emergencia: seis características
        /// más las primarias, veinticinco en total en vez de ciento veinte. El personaje aparecía
        /// sin crítico, sin potencia, sin alcance, sin placaje, sin huida, sin esquivas, sin daños
        /// elementales y sin resistencias, porque esas entradas sencillamente no viajaban.
        ///
        /// No se notaba porque el panel bueno lo pinta la reproducción de los bloques al entrar al
        /// mundo, y este constructor apenas se usaba. Al empezar a mandarlo también al acabar un
        /// combate, pasó a pisar la ficha buena.
        ///
        /// El fichero lo genera tools/extraer_caracteristicas_kub.py de los 672 kub reales que hay
        /// en las capturas. El hueco de cada una importa: tres van en el f2 —29, 47 y 96—, dos en
        /// el f5 —los puntos de acción y de movimiento— y las 115 restantes en el f4. Mandar una
        /// en el hueco que no le toca hace que el cliente reviente y pierda la ficha entera.
        /// </summary>
        private static void AprenderDelFichero()
        {
            try
            {
                // Por Paths y no relativo: relativo sólo funciona si el directorio de trabajo
                // es la raíz, que es lo que deja el lanzador. Arrancado de cualquier otra forma
                // —desde el IDE, desde un servicio, desde otra carpeta— el fichero no aparecía,
                // la ficha se quedaba en 25 entradas de 120, y con ella se iban el crítico, la
                // potencia, el alcance y todas las resistencias. Sin un solo mensaje de error.
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
        /// Los oficios del personaje, con el progreso que tenga.
        ///
        /// La LISTA de oficios sí se aprovecha de la captura: qué oficios existen y en qué orden
        /// los quiere el cliente son datos del juego. Lo que no se hereda es el progreso, que era
        /// del que grabó —tenía una docena al máximo—.
        ///
        /// Antes esto mandaba todos a nivel 1 y punto, y tenía sentido mientras no hubiera
        /// experiencia de oficio en ninguna parte. Ahora sí la hay: se guarda en CharacterJobs y
        /// se carga al elegir personaje, así que aquí se pone la de verdad. Sin esto, pescabas
        /// hasta subir a Pescador 3 y al volver a entrar te salían los veinte a nivel 1 otra vez.
        ///
        ///   irq: f1 (repeated) { f1: oficio, f2: siguiente nivel, f3: nivel, f4: suelo, f5: total }
        /// </summary>
        private static byte[] SendJobs(byte[] frame)
        {
            var jobs = Pb.New();
            byte[]? payload = ConnectionProtocol.ReadPayload(frame, Op.Irq);
            if (payload == null) return jobs.Build();

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

            if (count > 0)
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
        ///   itg  las dos barras: la de hechizos se rehace, la de objetos sale vacía
        ///
        /// Las dos barras van ya, no solo la de hechizos. La de objetos apuntaba a 72 uid de la
        /// cuenta capturada, y desde que el inventario sale de la base de datos esos objetos no
        /// existen: el cliente se quedaba con una barra llena de huecos que no sabe resolver.
        /// </summary>
        /// <summary>Qué mensaje de información lleva un lqn, o cero.</summary>
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

            if (ConnectionProtocol.ReadPayload(frame, Op.Hms) != null)
            {
                return ConnectionProtocol.Push(Op.Hms,
                    ConnectionProtocol.BuildSpellList(character.Breed, character.Level));
            }

            if (ConnectionProtocol.ReadPayload(frame, Op.Ivx) != null)
            {
                return ConnectionProtocol.Push(Op.Ivx, ConnectionProtocol.BuildInventory());
            }

            // El aviso de la última conexión. El bloque grabado trae el del que capturó —el 9 de
            // agosto a las 18:53— y eso es de otro. Se cambia por el de este personaje, con su
            // fecha y la dirección desde la que entró la vez pasada, leídas de la base antes de
            // pisarlas. Los demás lqn del bloque se dejan pasar tal cual.
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
                // Cuál de las dos barras es lo dice el manifiesto, no el cuerpo. Antes se miraba
                // dentro del payload —f6 objetos, f9 hechizos— y eso dejó de funcionar en cuanto el
                // payload dejó de viajar: las dos habrían salido como la de objetos, y la de
                // hechizos vacía, que es justo el fallo que ya se arregló una vez por otro camino.
                if (built == SpellBarLabel)
                {
                    return ConnectionProtocol.Push(Op.Itg,
                        ConnectionProtocol.BuildSpellBar(character.Breed, character.Level));
                }

                // La otra barra, la de objetos. Iba tal cual y apuntaba a 72 uid de la cuenta
                // capturada: objetos que en este inventario no existen. Sale vacía, que es lo que
                // tiene un personaje que todavía no ha puesto nada en ella.
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
            // De content/world/entry.json y no de los tres .bin. Son los mismos bytes —lo prueba
            // WorldEntryContentTests trama a trama— pero escritos campo a campo, de modo que lo que
            // sale al cable se puede leer y comparar en un diff. Asi es como se encontraron tarde
            // el diario de misiones y los logros de la cuenta capturada: no habia forma de verlos.
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

            Console.ForegroundColor = ConsoleColor.Yellow;
            Console.WriteLine("[World] No kva found in the blocks: the identity cannot be swapped " +
                              "and the client will refuse to enter the world.");
            Console.ResetColor();
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

                // Un array vacio quiere decir "este mensaje no se manda": lo usa el aviso de la
                // ultima conexion cuando el personaje entra por primera vez y no hay anterior que
                // contar. Mandarlo vacio seria una trama de longitud cero.
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
        public static async Task<int> SendMapAsync(NetworkStream stream, DatabaseManager.DbCharacter character, long mapId)
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

                await EnviarAsync(stream, toSend);
                sent++;
            }

            // The characteristics go out again here. The real server sends its kub twice, once
            // with the character and once with the map, and it is this second one the client
            // keeps: sending it only in the first block left the sheet empty.
            await EnviarAsync(stream, ConnectionProtocol.Push(Op.Kub, ConnectionProtocol.BuildCharacteristics()));

            // Los zaaps descubiertos. No está en los bloques grabados —se comprobó, ninguno de los
            // cuatro trae un hjk— y sin él la ventana de viaje sale con «Ningún destino» aunque el
            // hjj le mande los sesenta y tres. El servidor real lo manda justo aquí, al entrar.
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
