using System;
using System.Collections.Generic;
using System.Text;

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
        public static void Run()
        {
            var failures = new List<string>();

            CheckCharactersList(failures);
            CheckAuthenticationAccepted(failures);
            CheckServerSelection(failures);
            CheckEnvelope(failures);
            CheckWorldMessages(failures);

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
            byte[] frame = ConnectionProtocol.Push("kvi", new byte[] { 0x08, 0x01 });
            var root = ProtoMessage.Parse(frame);

            var wrapper = Field(root, 1, 2);
            if (wrapper == null) { failures.Add("envelope: the message must travel in f1 of the frame"); return; }

            var any = Field(ProtoMessage.Parse(wrapper.BytesValue), 1, 2);
            if (any == null) { failures.Add("envelope: the Any block is missing"); return; }

            var anyMsg = ProtoMessage.Parse(any.BytesValue);
            if (Text(anyMsg, 1) != ConnectionProtocol.UriPrefix + "kvi")
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

            string jsq = Hex(ConnectionProtocol.Answer("jsq", null, -1));
            if (jsq != CapturedJsq)
                failures.Add($"jsq: the answer to jqi does not match the capture ({jsq})");

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
