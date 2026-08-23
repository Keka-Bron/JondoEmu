using System;
using System.Collections.Generic;
using System.Text;
using Jondo.Unity.Protocol;

namespace Jondo.Unity.Launcher.Network
{
    /// <summary>
    /// Check that the connection-phase messages come out with the exact shape they have in the
    /// real 3.6.10.10 client captures.
    ///
    /// It runs at startup. The point is that a change in the builders that alters the structure
    /// blows up here and not in the client, where all you get to see is an empty screen.
    /// It checks the shape, not the values: the data comes from the database.
    /// </summary>
    public static class ConnectionProtocolSelfTest
    {
        /// <summary>
        /// Los mensajes de la preparación del combate, contra los bytes de verdad.
        ///
        /// Esto no comprueba la forma a ojo: compara byte a byte con lo que mandó el servidor real
        /// en la captura *combate contra poutch nivel 50…*, con las mismas casillas, el mismo
        /// combatiente y el mismo mapa que salen ahí. Si un constructor cambia de campo o de orden,
        /// aquí se ve; en el cliente lo único que se vería es un combate que no arranca.
        /// </summary>
        private static void CheckFightPreparation(List<string> failures)
        {
            const long fighter = 302677754146L;   // el personaje de la captura
            const long mapId = 99222029L;

            long[] blue = { 285, 273, 317, 373, 413, 411, 368, 312, 271, 288, 298, 302, 382, 386, 397, 400 };
            long[] red = { 270, 260, 303, 387, 428, 425, 381, 297, 257, 274, 284, 289, 396, 401, 410, 414 };

            Same(failures, Op.Kba,
                 "0a440a209d029102bd02f5029d039b03f002b8028f02a002aa02ae02fe0282038d0390031220"
                 + "8e028402af028303ac03a903fd02a902810292029c02a1028c0391039a039e03",
                 FightProtocol.BuildPlacementCells(blue, red));

            Same(failures, Op.Jzu,
                 "12091a0710a28280c8e708120d1a0b10ffffffffffffffffff01",
                 FightProtocol.BuildTeams(new[] { fighter, FightProtocol.Nobody }));

            // Y con cuatro monstruos son CINCO bloques, cada uno con su propio negativo, que es lo
            // que desmonta la lectura de "un bloque por equipo".
            Same(failures, "jzu (cuatro monstruos)",
                 "12091a0710a28280c8e708120d1a0b10ffffffffffffffffff01120d1a0b10feffffffffffffffff01"
                 + "120d1a0b10fdffffffffffffffff01120d1a0b10fcffffffffffffffff01",
                 FightProtocol.BuildTeams(new[] { fighter, -1L, -2L, -3L, -4L }));

            Same(failures, Op.Jrk, "100a1a00208d84a82f", FightProtocol.BuildFightMap(mapId));

            Same(failures, Op.Kmp, "0801", FightProtocol.BuildFightMapComing());

            // La jxg entera son cientos de bytes de ficha, así que aquí se comprueba el
            // ENVOLTORIO, que es donde estaba el fallo: todo va dentro de un f2, y dentro de él la
            // casilla en f1, el cuerpo en f2 y quién es en f3. Sin ese f2 de fuera el cliente pinta
            // el tablero y ni un solo combatiente encima.
            byte[] fighterMsg = FightProtocol.BuildFighter(
                270, 1, -1, new[] { (1, 6L, 0L) }, new byte[] { 0x10, 0x03 },
                FightProtocol.MonsterIdentity(3, 494, 50), isMonster: true);
            var outer = ProtoMessage.Parse(fighterMsg).Fields;
            if (outer.Count != 1 || outer[0].FieldNumber != 2 || outer[0].WireType != 2)
            {
                failures.Add("jxg: lo de dentro tiene que ir envuelto en un único f2");
            }
            else
            {
                var inner = ProtoMessage.Parse(outer[0].BytesValue).Fields;
                bool where = inner.Exists(f => f.FieldNumber == 1 && f.WireType == 2);
                bool body = inner.Exists(f => f.FieldNumber == 2 && f.WireType == 2);
                bool who = inner.Exists(f => f.FieldNumber == 3 && f.WireType == 0);
                if (!where || !body || !who)
                {
                    failures.Add("jxg: dentro del f2 faltan la casilla (f1), el cuerpo (f2) o quién es (f3)");
                }
            }

            Same(failures, Op.Kah, "08a28280c8e7081801", FightProtocol.BuildReadyAck(fighter));

            // Un lanzamiento, byte a byte. Lo que importa aquí es el f7 de dentro: el hechizo va
            // en DOS números, el 25188 (el hechizo) y el 63926 (su grado), y el f8 vale uno y no
            // es el hechizo. Cuando esto se mandaba mal, el cliente pintaba un puñetazo.
            Same(failures, "jwe (lanzar un hechizo)",
                 "18a28280c8e7083a1f10a28280c8e708220720a28280c8e708308f023a0810e4c40118b6f303"
                 + "400170ac02",
                 FightProtocol.BuildAction(
                     fighter, FightProtocol.Cast,
                     FightProtocol.CastAt(fighter, 0, 271, 25188, 63926, critical: false),
                     FightProtocol.CastDetail));

            // Colocarse: la casilla que se deja va con -1 y la que se ocupa, con quién la ocupa.
            Same(failures, Op.Kmk,
                 "1210088e02100118ffffffffffffffffff01120c088f02100518a28280c8e708",
                 FightProtocol.BuildFightersPlaced(new[]
                 {
                     (270, 1, FightProtocol.Nobody),
                     (271, 5, fighter),
                 }));

            CheckFightResults(failures, fighter);
        }

        /// <summary>
        /// La pantalla de fin de combate (jyg), contra los 86 bytes de la victoria del poutch.
        ///
        /// El personaje de la captura está en el nivel 354 con 23.793.534.387 de experiencia y no
        /// gana nada en ese combate; el poutch, muerto, va sin ficha. Los dos números que el
        /// mensaje no lleva —lo que pide el nivel 354 y lo que pide el 355— los pone la tabla del
        /// cliente, así que esto comprueba de paso que la tabla está cargada y cuadra.
        /// </summary>
        private static void CheckFightResults(List<string> failures, long fighter)
        {
            if (!ExperienceTable.IsLoaded) ExperienceTable.Initialize();
            if (!ExperienceTable.IsLoaded)
            {
                failures.Add("jyg: no se puede comprobar, falta la tabla de experiencia " +
                             "(character_xp.json)");
                return;
            }

            var results = new List<FightProtocol.FightResult>
            {
                new FightProtocol.FightResult
                {
                    Fighter = fighter,
                    Winner = true,
                    Level = 354,
                    Xp = 23793534387L,
                    XpGained = 0,
                    Spoils = new FightProtocol.Spoils(),
                },
                new FightProtocol.FightResult { Fighter = FightProtocol.Nobody },
            };

            Same(failures, "jyg (fin de combate)",
                 "123212001a2c08a28280c8e70812210a1c121a109e88dc93591801200128b38bd2d15830eeaaada558"
                 + "4001480110e20218012002121112001a0d08ffffffffffffffffff01180120cdb303"
                 + "40ffffffffffffffffff01",
                 FightProtocol.BuildFightResults(results, durationMs: 55757));
        }

        private static void Same(List<string> failures, string opcode, string expected, byte[] got)
        {
            string mine = Convert.ToHexString(got).ToLowerInvariant();
            if (mine != expected)
            {
                failures.Add($"{opcode} does not match the capture:\n        capture: {expected}\n        ours:    {mine}");
            }
        }

        public static void Run()
        {
            var failures = new List<string>();

            CheckCharactersList(failures);
            CheckAuthenticationAccepted(failures);
            CheckServerSelection(failures);
            CheckEnvelope(failures);
            CheckWorldMessages(failures);
            CheckFightPreparation(failures);
            CheckTravelList(failures);

            if (failures.Count > 0)
            {
                Console.ForegroundColor = ConsoleColor.Red;
                Console.WriteLine("[Protocol] The connection message check failed:");
                foreach (string f in failures) Console.WriteLine("    - " + f);
                Console.ResetColor();
                return;
            }

            Console.WriteLine("[Protocol] The connection messages match the captured shape.");
        }

        /// <summary>
        /// Las tres pestañas de la lista de viaje (hjj), contra los bytes de verdad.
        ///
        /// Las tres viajan en el mismo mensaje y en el mismo campo repetido, y lo único que las
        /// separa es el f3 de cada entrada. Eso es fácil de romper sin enterarse, y el síntoma en
        /// el cliente no es un error: es una pestaña vacía, o un destino que aparece donde no toca.
        /// Por eso se compara byte a byte con lo que mandó el servidor real.
        ///
        /// Cada cadena es UNA entrada sacada de su captura, con sus valores exactos:
        ///
        ///   zaap      Castillo de Amakna, sin f3            «zaap desde castillo de amakna a bonta…»
        ///   zaapi     taller forjamagos de Bonta, f3 = 1    «usar zaapi en bonta a taller forjamagos»
        ///   anomalía  Cuna de Alma, f3 = 4 y su reloj       «entrar a mapa con vestigio de zaap…»
        ///
        /// El de la anomalía llegó con 43 minutos por delante de 120 y sin coste, porque el
        /// personaje estaba de pie en el mapa del vestigio. Los 43 van escritos a mano a propósito:
        /// aquí se comprueba el constructor, no el reloj.
        /// </summary>
        private static void CheckTravelList(List<string> failures)
        {
            Same(failures, "hjj (zaap)",
                 "1a0d082810be01288196b82830b201",
                 ConnectionProtocol.BuildZaapList(0, new[]
                 {
                     new ConnectionProtocol.ZaapDestination(84806401, 178, 40, 190),
                 }));

            // El 2001 del final es el f4 de la raíz: sin él el cliente abre la ventana del zaap
            // y la deja vacía. Sale así en las tres capturas de zaapi y en ninguna de zaap.
            Same(failures, "hjj (zaapi)",
                 "1a0e080a10141801288180806930cf072001",
                 ConnectionProtocol.BuildZaapList(0, new[]
                 {
                     new ConnectionProtocol.ZaapDestination(220200961, 975, 10, 20,
                                                            Managers.Zaapis.Kind),
                 }, Managers.Zaapis.Teleporter));

            // Los grupos, con los bytes de la captura de "recibir invitacion de grupo y aceptar":
            // Harmoo (293213045026) invita a Sacri-Master (302677754146) al grupo 71272.
            Same(failures, "ijz (te invitan)",
                 "08a28280c8e70810a282f0a6c408180828e8ac0430013a064861726d6f6f",
                 ConnectionProtocol.BuildPartyInvitation(
                     302677754146, 293213045026, "Harmoo", 71272, 8));

            // Y los del rechazo, de la captura del koliseo y la del que invita.
            Same(failures, "ilo (invitacion cerrada)", "08d8af0410a28280c8e708",
                 ConnectionProtocol.BuildInvitationClosed(71640, 302677754146));
            Same(failures, "iko (invitado fuera)", "08a282a8ffa40e10999c04",
                 ConnectionProtocol.BuildInvitationWithdrawn(490967007522, 69145));
            Same(failures, "imy (grupo deshecho)", "08999c04",
                 ConnectionProtocol.BuildPartyDissolved(69145));
            Same(failures, "ils (te has salido)", "08c29c04",
                 ConnectionProtocol.BuildPartyLeft(69186));
            Same(failures, "ilx (jefe nuevo)", "08a282acf7bd1a10a69c04",
                 ConnectionProtocol.BuildPartyLeader(909978042658, 69158));

            // El aviso de la ultima conexion. Sin IP tiene que salir byte a byte igual al que
            // trae el bloque grabado: 9 de agosto de 2026 a las 18:53. Es lo que fija el orden de
            // los parametros, que no es el de lectura.
            Same(failures, "lqn (ultima conexion, sin IP)",
                 "10c101220432303236220230382202303922023138220235 33".Replace(" ", ""),
                 ConnectionProtocol.BuildLastConnection(
                     new DateTimeOffset(2026, 8, 9, 18, 53, 0, TimeSpan.Zero), ""));

            // Y con IP, que anade el sexto parametro y cambia de plantilla.
            Same(failures, "lqn (ultima conexion, con IP)",
                 "1098012204323032362202303822023039220231382202353322093132372e302e302e31",
                 ConnectionProtocol.BuildLastConnection(
                     new DateTimeOffset(2026, 8, 9, 18, 53, 0, TimeSpan.Zero), "127.0.0.1"));

            // El mensaje privado, tal cual lo mando el servidor real al susurrar a Hiierbita-Xx.
            Same(failures, "kth (mensaje privado)",
                 "0a19323032362d30382d31325432323a35343a32392b30323a3030220028a282acfea805"
                 + "320c4869696572626974612d58783a04686f6c61",
                 ConnectionProtocol.BuildPrivateMessage(
                     "2026-08-12T22:54:29+02:00", 182801072418, "Hiierbita-Xx", "hola"));

            // La ventana de subida de nivel, tal cual sale en el tutorial.
            Same(failures, "kua (nivel 2)", "0802", ConnectionProtocol.BuildLevelUp(2));
            Same(failures, "kua (nivel 3)", "0803", ConnectionProtocol.BuildLevelUp(3));

            // El rechazo de un susurro. 0802 es lo que contesta el servidor real al susurrarse
            // a uno mismo, en la captura de la lista de artesanos.
            Same(failures, "ktl (susurro rechazado)", "0802",
                 ConnectionProtocol.BuildChatError(Handlers.PrivateMessageHandler.CannotWhisper));

            // Los mensajes de información, que es como se le habla al jugador. El tipo 0 no se
            // manda —proto3 se come el cero— y el 1 sí; los cuatro salen de capturas distintas.
            Same(failures, "lqn (bienvenida, tipo 1)", "08011059",
                 ConnectionProtocol.BuildInfoMessage(Managers.InfoMessages.Warning, 89));
            Same(failures, "lqn (hechizo imposible, tipo 1)", "080110af01",
                 ConnectionProtocol.BuildInfoMessage(Managers.InfoMessages.Warning, 175));
            Same(failures, "lqn (kamas ganados, tipo 0)", "102d220132",
                 ConnectionProtocol.BuildSystemMessage(Managers.InfoMessages.KamasGained, "2"));
            Same(failures, "lqn (objeto conseguido, tipo 0)", "101522013122053130373834",
                 ConnectionProtocol.BuildSystemMessage(Managers.InfoMessages.ItemGained, "1", "10784"));

            // Y la lista de zaaps descubiertos, que es lo que hace que la ventana no salga vacía.
            // Son los 45 del personaje de la captura, en su orden, y tienen que dar sus 182 bytes.
            Same(failures, "hjk (zaaps descubiertos)",
                 "0ab3018196b82892b8098880e060a9baea198290d8208184d0588380d0209b96903c8488b00d858a"
                 + "b0498388a039808cb0498386b849838cb0658180c0558388c0658490c02d8084e05581a6802a81"
                 + "8e800a8188882a8998b0468a8a882a8490b05e8880e04a9092802a928880528294c04a978e882a"
                 + "8288d0528992c06a8196e04e878af0069080b04e8194f036878ce04e8080802387849837848880"
                 + "638290905bc9b6ea198684c02f8eace0438184e82f8080f033",
                 ConnectionProtocol.BuildDiscoveredZaaps(new long[]
                 {
                     84806401, 154642, 202899464, 54172969, 68552706, 185860609, 68419587,
                     126094107, 28050436, 153879813, 120062979, 153880064, 154010371, 212600323,
                     179306497, 212861955, 95422468, 179831296, 88085249, 20973313, 88212481,
                     147590153, 88212746, 197920772, 156762120, 88082704, 171967506, 156240386,
                     88213271, 173278210, 223349001, 165153537, 14419207, 164364304, 115083777,
                     165152263, 73400320, 115737095, 207619076, 191105026, 54172489, 99615238,
                     142087694, 100270593, 108789760,
                 }));

            Same(failures, "hjj (anomalía)",
                 "1a13088c0118042204102b187828c9e6e91930e104",
                 ConnectionProtocol.BuildZaapList(0, new[]
                 {
                     new ConnectionProtocol.ZaapDestination(54162249, 609, 140, 0,
                                                            Managers.Anomalies.Kind, 43, 120),
                 }));
        }

        private static readonly DatabaseManager.DbCharacter TestCharacter = new DatabaseManager.DbCharacter
        {
            Id = 4242424242L,
            Name = "Test",
            Breed = 9,
            Sex = 1,
            Level = 50,
            ServerId = DatabaseManager.DefaultServerId,
            LastConnection = "2026-01-01T00:00:00.000Z"
        };

        /// <summary>
        /// kvi: f1 (repeated) { f1 { f2: name, f3: level, f4 { f2: sex, f6: look, f7: breed } }, f2: id }
        /// </summary>
        private static void CheckCharactersList(List<string> failures)
        {
            byte[] kvi = ConnectionProtocol.BuildCharactersList(new[] { TestCharacter });
            var root = ProtoMessage.Parse(kvi);

            var entry = Field(root, 1, 2);
            if (entry == null) { failures.Add("kvi: the character entry (f1) is missing"); return; }

            var entryMsg = ProtoMessage.Parse(entry.BytesValue);
            if (Varint(entryMsg, 2) != TestCharacter.Id)
                failures.Add("kvi: the character id is not in f2");

            var details = Field(entryMsg, 1, 2);
            if (details == null) { failures.Add("kvi: the character details (f1) are missing"); return; }

            var detailsMsg = ProtoMessage.Parse(details.BytesValue);
            if (Text(detailsMsg, 2) != TestCharacter.Name)
                failures.Add("kvi: the name is not in f2 of the details");
            if (Varint(detailsMsg, 3) != TestCharacter.Level)
                failures.Add("kvi: the level is not in f3 of the details");

            var traits = Field(detailsMsg, 4, 2);
            if (traits == null) { failures.Add("kvi: the traits block (f4) is missing"); return; }

            var traitsMsg = ProtoMessage.Parse(traits.BytesValue);
            if (Varint(traitsMsg, 7) != TestCharacter.Breed)
                failures.Add("kvi: the breed is not in f7 of the traits");

            var sex = Field(traitsMsg, 2, 2);
            if (sex == null) failures.Add("kvi: the sex block (f2) must always be present");
            else if (Varint(ProtoMessage.Parse(sex.BytesValue), 3) != TestCharacter.Sex)
                failures.Add("kvi: the sex is not in f3 inside its block");

            var look = Field(traitsMsg, 6, 2);
            if (look == null || look.BytesValue.Length == 0)
            {
                failures.Add("kvi: the look (f6) is missing. Generate breed_looks.json with tools/extract_breed_looks.py");
            }
            else
            {
                var lookMsg = ProtoMessage.Parse(look.BytesValue);
                if (Field(lookMsg, 1, 2) == null) failures.Add("look: the colors (f1) are missing");
                if (Varint(lookMsg, 2) != 3) failures.Add("look: f2 must be 3");
                if (Varint(lookMsg, 3) == 0) failures.Add("look: the bonesId (f3) is missing");
                if (Field(lookMsg, 6, 2) == null) failures.Add("look: the skins (f6) are missing");
            }
        }

        /// <summary>
        /// f2 { f1: language, f3 { f1 { f1: account, f2: nickname, f3: tag, f4: servers, f5: date, f6: {} } } }
        /// </summary>
        private static void CheckAuthenticationAccepted(List<string> failures)
        {
            var servers = new[]
            {
                new DatabaseManager.DbServer { Id = DatabaseManager.DefaultServerId, Name = "Test", Status = 1 }
            };

            byte[] msg = ConnectionProtocol.BuildAuthenticationAccepted(
                "0", 12345L, "Nickname", "0001", "2099-01-01T00:00:00Z", servers, new[] { TestCharacter });

            var result = Field(ProtoMessage.Parse(msg), 2, 2);
            if (result == null) { failures.Add("authentication: the message must travel in f2"); return; }

            var resultMsg = ProtoMessage.Parse(result.BytesValue);
            var wrapper = Field(resultMsg, 3, 2);
            if (wrapper == null) { failures.Add("authentication: the result (f3) is missing"); return; }

            var accepted = Field(ProtoMessage.Parse(wrapper.BytesValue), 1, 2);
            if (accepted == null) { failures.Add("authentication: the accepted block (f3.f1) is missing"); return; }

            var acceptedMsg = ProtoMessage.Parse(accepted.BytesValue);
            if (Varint(acceptedMsg, 1) != 12345L) failures.Add("authentication: the account is not in f1");
            if (Text(acceptedMsg, 2) != "Nickname") failures.Add("authentication: the nickname is not in f2");

            var list = Field(acceptedMsg, 4, 2);
            if (list == null) { failures.Add("authentication: the server list (f4) is missing"); return; }

            var listMsg = ProtoMessage.Parse(list.BytesValue);
            int slots = 0;
            foreach (var f in listMsg.Fields) if (f.FieldNumber == 2) slots++;
            if (slots != 7) failures.Add($"authentication: expected 7 slot blocks but found {slots}");

            var server = Field(listMsg, 1, 2);
            if (server == null) { failures.Add("authentication: the server (f1) is missing"); return; }

            var serverMsg = ProtoMessage.Parse(server.BytesValue);
            var header = Field(serverMsg, 1, 2);
            if (header == null) failures.Add("server: the header (f1) is missing");
            else if (Varint(ProtoMessage.Parse(header.BytesValue), 1) != DatabaseManager.DefaultServerId)
                failures.Add("server: the id is not in f1 of the header");

            var summary = Field(serverMsg, 3, 2);
            if (summary == null) { failures.Add("server: the character summary (f3) is missing"); return; }

            var summaryMsg = ProtoMessage.Parse(summary.BytesValue);
            if (Text(summaryMsg, 1) != TestCharacter.Name) failures.Add("summary: the name is not in f1");
            // Here the breed is zero-based, a detail you only spot by comparing with the capture.
            if (Varint(summaryMsg, 2) != TestCharacter.Breed - 1) failures.Add("summary: the breed must be zero-based in f2");
            if (Varint(summaryMsg, 3) != TestCharacter.Sex) failures.Add("summary: the sex is not in f3");
            if (Varint(summaryMsg, 4) != TestCharacter.Level) failures.Add("summary: the level is not in f4");
            if (string.IsNullOrEmpty(Text(summaryMsg, 5)))
                failures.Add("summary: the connection date (f5) is missing");

            // A character that has never played must still carry a date. Without it the client
            // draws an empty server-selection screen and gives no clue why.
            var neverPlayed = new DatabaseManager.DbCharacter
            {
                Id = TestCharacter.Id,
                Name = TestCharacter.Name,
                Breed = TestCharacter.Breed,
                Sex = TestCharacter.Sex,
                Level = TestCharacter.Level,
                ServerId = TestCharacter.ServerId,
                LastConnection = ""
            };

            byte[] withoutDate = ConnectionProtocol.BuildAuthenticationAccepted(
                "0", 12345L, "Nickname", "0001", "2099-01-01T00:00:00Z", servers, new[] { neverPlayed });

            if (!FindsConnectionDate(withoutDate))
                failures.Add("summary: a character with no stored date must still get one");
        }

        /// <summary>Walks down to the summary and says whether it carries the date (f5).</summary>
        private static bool FindsConnectionDate(byte[] authenticationAccepted)
        {
            var result = Field(ProtoMessage.Parse(authenticationAccepted), 2, 2);
            if (result == null) return false;
            var wrapper = Field(ProtoMessage.Parse(result.BytesValue), 3, 2);
            if (wrapper == null) return false;
            var accepted = Field(ProtoMessage.Parse(wrapper.BytesValue), 1, 2);
            if (accepted == null) return false;
            var list = Field(ProtoMessage.Parse(accepted.BytesValue), 4, 2);
            if (list == null) return false;
            var server = Field(ProtoMessage.Parse(list.BytesValue), 1, 2);
            if (server == null) return false;
            var summary = Field(ProtoMessage.Parse(server.BytesValue), 3, 2);
            if (summary == null) return false;
            return !string.IsNullOrEmpty(Text(ProtoMessage.Parse(summary.BytesValue), 5));
        }

        /// <summary>f2 { f1: language, f4 { f1 { f1: ticket, f2: address, f3: ports } } }</summary>
        private static void CheckServerSelection(List<string> failures)
        {
            byte[] msg = ConnectionProtocol.BuildServerSelected("0", "abc123", "127.0.0.1", 5555, 5555);

            var root = Field(ProtoMessage.Parse(msg), 2, 2);
            if (root == null) { failures.Add("selection: the message must travel in f2"); return; }

            var selection = Field(ProtoMessage.Parse(root.BytesValue), 4, 2);
            if (selection == null) { failures.Add("selection: the redirect block (f4) is missing"); return; }

            var info = Field(ProtoMessage.Parse(selection.BytesValue), 1, 2);
            if (info == null) { failures.Add("selection: the server info (f4.f1) is missing"); return; }

            var infoMsg = ProtoMessage.Parse(info.BytesValue);
            if (Text(infoMsg, 1) != "abc123") failures.Add("selection: the ticket is not in f1");
            if (Text(infoMsg, 2) != "127.0.0.1") failures.Add("selection: the address is not in f2");

            var ports = Field(infoMsg, 3, 2);
            // Port 5555 twice, as concatenated varints: b3 2b b3 2b.
            if (ports == null || ports.BytesValue.Length != 4)
                failures.Add("selection: the ports must go as concatenated varints in f3");
        }

        /// <summary>The messages pushed by the server travel in field 1 of the frame.</summary>
        private static void CheckEnvelope(List<string> failures)
        {
            byte[] frame = ConnectionProtocol.Push(Op.Kvi, new byte[] { 0x08, 0x01 });
            var root = ProtoMessage.Parse(frame);

            var wrapper = Field(root, 1, 2);
            if (wrapper == null) { failures.Add("envelope: the message must travel in f1 of the frame"); return; }

            var any = Field(ProtoMessage.Parse(wrapper.BytesValue), 1, 2);
            if (any == null) { failures.Add("envelope: the Any block is missing"); return; }

            var anyMsg = ProtoMessage.Parse(any.BytesValue);
            if (Text(anyMsg, 1) != ConnectionProtocol.UriPrefix + Op.Kvi)
                failures.Add("envelope: the type_url is not in f1 of the Any");
            if (Field(anyMsg, 2, 2) == null)
                failures.Add("envelope: the payload is not in f2 of the Any");
        }

        /// <summary>
        /// The two world messages that carry no data of ours, so they can be checked byte for byte
        /// against the capture instead of only by shape.
        ///
        /// Both are answers the client waits for and neither used to be sent:
        ///
        ///   kqy  answer to the heartbeat the client repeats every five seconds. Replying to it
        ///        with the map block made the client reload the world over and over.
        ///   lva  end of the actor list. It goes right behind jss in every capture that loads a
        ///        map; without it the map never finishes loading.
        ///
        /// The bytes are the ones on the wire in the movement captures, minus the length prefix,
        /// which is added when the frame is written.
        /// </summary>
        private static void CheckWorldMessages(List<string> failures)
        {
            const string CapturedKqy =
                "0a1b0a190a13747970652e616e6b616d612e636f6d2f6b717912020801";
            const string CapturedLva =
                "0a170a150a13747970652e616e6b616d612e636f6d2f6c7661";

            string kqy = Hex(ConnectionProtocol.BuildHeartbeatAnswer());
            if (kqy != CapturedKqy)
                failures.Add($"kqy: the heartbeat answer does not match the capture ({kqy})");

            string lva = Hex(ConnectionProtocol.BuildActorsComplete());
            if (lva != CapturedLva)
                failures.Add($"lva: the end of the actor list does not match the capture ({lva})");

            // The go-ahead for a map change. It is the one message here that does NOT travel in
            // root field 1: answers use field 3 and repeat the id the request came with. Getting
            // that wrong leaves the client standing on the edge of the map for good, so it is
            // worth checking byte for byte.
            const string CapturedJsq =
                "1a220a150a13747970652e616e6b616d612e636f6d2f6a737110ffffffffffffffffff01";

            string jsq = Hex(ConnectionProtocol.Answer(Op.Jsq, null, -1));
            if (jsq != CapturedJsq)
                failures.Add($"jsq: the answer to jqi does not match the capture ({jsq})");

            // Un téléporteur instantané ferme son utilisation avant de quitter la map. Sans cet
            // iwi, le client peut garder l'ElementId occupé et ne plus dessiner son gfx au retour.
            var ended = ProtoMessage.Parse(
                ConnectionProtocol.BuildInteractiveUseEnded(515742, 114));
            if (Varint(ended, 1) != 515742 || Varint(ended, 3) != 114)
                failures.Add("iwi: interactive-use end must carry ElementId in f1 and USE114 in f3");

            // And the two containers of kub that are not f4. The client throws a
            // NullReferenceException and drops the whole sheet when one of these goes out in the
            // wrong field, which is what left the characteristics button greyed out.
            foreach (int id in new[] { 1, 23 })
            {
                if (WorldEntry.CharacteristicIds.Count > 0 && WorldEntry.ContainerOf(id) != 5)
                    failures.Add($"kub: characteristic {id} should travel in f5, not f{WorldEntry.ContainerOf(id)}");
            }
            foreach (int id in new[] { 29, 47, 96 })
            {
                if (WorldEntry.CharacteristicIds.Count > 0 && WorldEntry.ContainerOf(id) != 2)
                    failures.Add($"kub: characteristic {id} should travel in f2, not f{WorldEntry.ContainerOf(id)}");
            }
        }

        // ─── Read helpers ───────────────────────────────────────────────────────

        private static string Hex(byte[] data)
        {
            var text = new StringBuilder(data.Length * 2);
            foreach (byte b in data) text.Append(b.ToString("x2"));
            return text.ToString();
        }


        private static ProtoField? Field(ProtoMessage msg, int number, int wireType)
        {
            foreach (var f in msg.Fields)
            {
                if (f.FieldNumber == number && f.WireType == wireType) return f;
            }
            return null;
        }

        private static long Varint(ProtoMessage msg, int number)
        {
            var f = Field(msg, number, 0);
            return f?.VarIntValue ?? 0;
        }

        private static string Text(ProtoMessage msg, int number)
        {
            var f = Field(msg, number, 2);
            return f == null ? "" : Encoding.UTF8.GetString(f.BytesValue);
        }
    }
}
