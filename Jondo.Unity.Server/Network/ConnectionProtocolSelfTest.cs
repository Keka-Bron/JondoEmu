using System;
using System.Collections.Generic;
using System.Text;
using Jondo.Unity.Protocol;
using Jondo.Unity.Protocol.Game;
using Jondo.Unity.Protocol.Game.Wire;

namespace Jondo.Unity.Server.Network
{
    /// <summary>
    /// Check that game-phase messages come out with the exact shape they have in the
    /// real 3.6.10.10 client captures.
    ///
    /// It runs at startup. The point is that a change in the builders that alters the structure
    /// blows up here and not in the client, where all you get to see is an empty screen.
    /// It checks the shape, not the values: the data comes from the database.
    /// </summary>
    public static class ConnectionProtocolSelfTest
    {
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
            CheckEnvelope(failures);
            CheckWorldMessages(failures);
            CheckTravelList(failures);

            if (failures.Count > 0)
            {
                // This did a «return», that is the server started all the same with the protocol
                // broken. It was the only one of the eight guards that did not stop, and precisely the one
                // watching the phase where a failure gives NO error: the client is left with a blank
                // screen, with no message, no log and nothing to look at. Starting like that does not
                // help anyone; the failures are shown and it stops.
                Console.ForegroundColor = ConsoleColor.Red;
                Console.WriteLine("[Protocol] The game message check failed:");
                foreach (string f in failures) Console.WriteLine("    - " + f);
                Console.ResetColor();

                throw new InvalidOperationException(
                    $"[Protocol] {failures.Count} game message(s) do not match the capture. " +
                    "They are written above, each with the capture's bytes and ours.");
            }

            Console.WriteLine("[Protocol] The game messages match the captured shape.");
        }

        /// <summary>
        /// The three tabs of the travel list (hjj), against the real bytes.
        ///
        /// All three travel in the same message and in the same repeated field, and the only thing
        /// separating them is each entry's f3. That is easy to break without noticing, and the symptom in
        /// the client is not an error: it is an empty tab, or a destination that appears where it should not.
        /// That is why it is compared byte by byte with what the real server sent.
        ///
        /// Each string is ONE entry taken from its capture, with its exact values:
        ///
        ///   zaap      Amakna Castle, without f3              «zaap desde castillo de amakna a bonta…»
        ///   zaapi     Bonta smithmages' workshop, f3 = 1     «usar zaapi en bonta a taller forjamagos»
        ///   anomaly   Cuna de Alma, f3 = 4 and its clock     «entrar a mapa con vestigio de zaap…»
        ///
        /// The anomaly one arrived with 43 minutes to go out of 120 and with no cost, because the
        /// character was standing on the vestige's map. The 43 are written by hand on purpose:
        /// here the builder is checked, not the clock.
        /// </summary>
        private static void CheckTravelList(List<string> failures)
        {
            Same(failures, "hjj (zaap)",
                 "1a0d082810be01288196b82830b201",
                 ConnectionProtocol.BuildZaapList(0, new[]
                 {
                     new ConnectionProtocol.ZaapDestination(84806401, 178, 40, 190),
                 }));

            // The 2001 at the end is the root's f4: without it the client opens the zaap window
            // and leaves it empty. It comes out like that in the three zaapi captures and in none of the zaap ones.
            Same(failures, "hjj (zaapi)",
                 "1a0e080a10141801288180806930cf072001",
                 ConnectionProtocol.BuildZaapList(0, new[]
                 {
                     new ConnectionProtocol.ZaapDestination(220200961, 975, 10, 20,
                                                            Managers.Zaapis.Kind),
                 }, Managers.Zaapis.Teleporter));

            // The parties, with the bytes of the "recibir invitacion de grupo y aceptar" capture:
            // Leader (290000000003) invites Hero-Example (300000000001) to party 71272.
            Same(failures, "ijz (te invitan)",
                 "0881f092cbdd081083a8e3aab808180828e8ac0430013a064c6561646572",
                 ConnectionProtocol.BuildPartyInvitation(
                     300000000001, 290000000003, "Leader", 71272, 8));

            // And the decline ones, from the koliseo capture and the inviter's.
            Same(failures, "ilo (invitation closed)", "08d8af041081f092cbdd08",
                 ConnectionProtocol.BuildInvitationClosed(71640, 300000000001));
            Same(failures, "iko (guest out)", "088cc89ab2a10e10999c04",
                 ConnectionProtocol.BuildInvitationWithdrawn(490000000012, 69145));
            Same(failures, "imy (party broken up)", "08999c04",
                 ConnectionProtocol.BuildPartyDissolved(69145));
            Same(failures, "ils (you have left)", "08c29c04",
                 ConnectionProtocol.BuildPartyLeft(69186));
            Same(failures, "ilx (new leader)", "088ad0b8e1981a10a69c04",
                 ConnectionProtocol.BuildPartyLeader(900000000010, 69158));

            // Semantic notice policies: parameter order is year/month/day/hour/minute,
            // with an optional sixth IP parameter selecting the other template.
            var previousWhen = new DateTimeOffset(2026, 8, 9, 18, 53, 0, TimeSpan.Zero);
            var previousWithoutIp = EntryInformationMessages.PreviousConnection(new() { When = previousWhen });
            if (previousWithoutIp == null || previousWithoutIp.MessageType != TextInformationType.TextInformationMessage
                || previousWithoutIp.MessageId != 193
                || !previousWithoutIp.Parameters.SequenceEqual(new[] { "2026", "08", "09", "18", "53" }))
                failures.Add("last connection without IP: semantic values differ");
            var previousWithIp = EntryInformationMessages.PreviousConnection(new() { When = previousWhen, Ip = "127.0.0.1" });
            if (previousWithIp == null || previousWithIp.MessageType != TextInformationType.TextInformationMessage
                || previousWithIp.MessageId != 152
                || !previousWithIp.Parameters.SequenceEqual(new[] { "2026", "08", "09", "18", "53", "127.0.0.1" }))
                failures.Add("last connection with IP: semantic values differ");

            // The private message, just as the real server sent it on whispering to Player-Alpha.
            Same(failures, "kth (private message)",
                 "0a19323032362d30382d31325432323a35343a32392b30323a30302200288590d8c69e05"
                 + "320c506c617965722d416c7068613a04686f6c61",
                 ConnectionProtocol.BuildPrivateMessage(
                     "2026-08-12T22:54:29+02:00", 180000000005, "Player-Alpha", "hola"));

            // The level-up window, just as it comes out in the tutorial.
            Same(failures, "kua (nivel 2)", "0802", ConnectionProtocol.BuildLevelUp(2));
            Same(failures, "kua (nivel 3)", "0803", ConnectionProtocol.BuildLevelUp(3));

            // The refusal of a whisper. 0802 is what the real server answers on whispering
            // to oneself, in the capture of the craftsmen list.
            Same(failures, "ktl (whisper rejected)", "0802",
                 ConnectionProtocol.BuildChatError(Handlers.PrivateMessageHandler.CannotWhisper));

            // The information messages, which is how the player is spoken to. Type 0 is not
            // sent —proto3 swallows the zero— and 1 is; the four come from different captures.
            Same(failures, "lqn (welcome, type 1)", "08011059",
                 ConnectionProtocol.BuildInfoMessage(Managers.InfoMessages.Warning, 89));
            Same(failures, "lqn (impossible spell, type 1)", "080110af01",
                 ConnectionProtocol.BuildInfoMessage(Managers.InfoMessages.Warning, 175));
            Same(failures, "lqn (kamas earned, type 0)", "102d220132",
                 ConnectionProtocol.BuildSystemMessage(Managers.InfoMessages.KamasGained, "2"));
            Same(failures, "lqn (item obtained, type 0)", "101522013122053130373834",
                 ConnectionProtocol.BuildSystemMessage(Managers.InfoMessages.ItemGained, "1", "10784"));

            // The historical discovered-zaap list now has an independent stable-codec
            // fixture in the Game protocol suite; production builds it semantically.

            Same(failures, "hjj (anomaly)",
                 "1a13088c0118042204102b187828c9e6e91930e104",
                 ConnectionProtocol.BuildZaapList(0, new[]
                 {
                     new ConnectionProtocol.ZaapDestination(54162249, 609, 140, 0,
                                                            Managers.Anomalies.Kind, 43, 120),
                 }));
        }

        private static readonly DbCharacter TestCharacter = new DbCharacter
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
        /// The semantic character-list snapshot, including its resolved appearance.
        /// </summary>
        private static void CheckCharactersList(List<string> failures)
        {
            var characters = CharacterListMessages.Live.Snapshot(new[] { TestCharacter }).Characters;
            if (characters.Count != 1)
            {
                failures.Add("Character list: expected one character.");
                return;
            }
            var entry = characters[0];
            if (entry.Id != TestCharacter.Id) failures.Add("Character list: incorrect character id.");
            if (entry.ComplementCase is not CharacterComplementCaseCharacterBasicInformation basic)
            {
                failures.Add("Character list: basic information is missing.");
                return;
            }
            if (basic.Value.Name != TestCharacter.Name) failures.Add("Character list: incorrect name.");
            if (basic.Value.Level != TestCharacter.Level) failures.Add("Character list: incorrect level.");
            if (basic.Value.ComplementCase is not CharacterBasicInformationComplementCaseCharacterLook appearance)
            {
                failures.Add("Character list: appearance is missing.");
                return;
            }
            if (appearance.Value.BreedId != TestCharacter.Breed) failures.Add("Character list: incorrect breed.");
            if (appearance.Value.ComplementCase is not CharacterLookComplementCaseBaseInformation identity)
                failures.Add("Character list: base information is missing.");
            else if (identity.Value.Gender != Gender.Female)
                failures.Add("Character list: incorrect gender.");

            var look = appearance.Value.Look;
            if (look.Colors.Count == 0) failures.Add("Character look: colors are missing.");
            if (look.LookType != 3) failures.Add("Character look: expected look type 3.");
            if (look.Bones == 0) failures.Add("Character look: bones are missing.");
            if (look.Skins.Count == 0) failures.Add("Character look: skins are missing.");
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
#if !JONDO_PROTOCOL_3_8_1_2
            // The typed encoder selects the build's profile; this fixture is from 3.6.10.10.
            const string CapturedKqy =
                "0a1b0a190a13747970652e616e6b616d612e636f6d2f6b717912020801";
            Same(failures, "kqy (heartbeat answer)", CapturedKqy, new GameCodec().Encode(new(new PongEvent(Quiet: true))));
#endif

            const string CapturedLva =
                "0a170a150a13747970652e616e6b616d612e636f6d2f6c7661";

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

            // A teleport releases its use before leaving the map. Without that iwi the client can
            // leave the element busy and not draw its graphic again on coming back.
            var ended = ProtoMessage.Parse(
                ConnectionProtocol.BuildInteractiveUseEnded(515742, 114));
            if (Varint(ended, 1) != 515742 || Varint(ended, 3) != 114)
                failures.Add("iwi: interactive-use end must carry ElementId in f1 and USE114 in f3");

            // And the two containers of kub that are not f4. The client throws a
            // NullReferenceException and drops the whole sheet when one of these goes out in the
            // wrong field, which is what left the characteristics button greyed out.
            foreach (int id in new[] { 1, 23 })
            {
                if (CharacteristicCatalogue.Default.KindOf(id) != CharacteristicKind.Usable)
                    failures.Add($"kub: characteristic {id} should be Usable");
            }
            foreach (int id in new[] { 29, 47, 96 })
            {
                if (CharacteristicCatalogue.Default.KindOf(id) != CharacteristicKind.Value)
                    failures.Add($"kub: characteristic {id} should be Value");
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
