using Google.Protobuf;
using System.Text.Json;
using Jondo.Unity.Launcher;
using Jondo.Unity.Protocol;
using Jondo.Unity.Server;
using Jondo.Unity.Server.Handlers;
using Jondo.Unity.Server.Managers;
using Jondo.Unity.Server.Network;
using Jondo.Unity.Protocol.Game;
using Jondo.Unity.Tests.Economy;
using Microsoft.Data.Sqlite;
using Xunit;

namespace Jondo.Unity.Tests.Sessions
{
    public class CharacterCreationAppearanceTests
    {
        // Dragon-Queen2's actual creation palette, captured on 2026-10-19.
        private static readonly long[] Rgb = { 0xC78B6F, 0xE80006, 0xF80007, 0xD30006, 0x795647, 0xCD0006 };
        private static readonly long[] Indexed = { 0x01C78B6F, 0x02E80006, 0x03F80007, 0x04D30006, 0x05795647, 0x06CD0006 };

        [Theory]
        [InlineData(18, 1, 283, 3499L, 57L)]
        [MemberData(nameof(BreedSexPairs))]
        public async Task Creation_persists_sex_and_palette_and_reuses_them_in_client_packets(
            int breed, int sex, int head, long body, long scale)
        {
            if (!File.Exists(Paths.WorldDb)) return;
            long account = 8_919_000_001 + breed * 2 + sex;
            await using var pipe = await ClientPipe.OpenAsync(account, account + 100_000, CharacterCreationHandler.StartingMap);
            using var scope = SessionContext.Push(pipe.Session);
            using var heads = JsonDocument.Parse(File.ReadAllText(Paths.HeadsJson));
            long headSkin = heads.RootElement.GetProperty("heads").GetProperty(head.ToString()).GetProperty("skin").GetInt64();
            void CheckLook(byte[] look) => AssertLook(look, body, scale, headSkin);
            // Unique, and within the client's naming rule (CharacterNames), which the server checks.
            string name = "Palette-" + string.Concat(Guid.NewGuid().ToString("N")[..11].Select(c => (char)('a' + Convert.ToInt32(c.ToString(), 16))));
            var creation = Pb.New().Str(1, name).Var(2, head)
                // The bytes are from the capture, independent of the server's colour writer.
                .Bytes(3, Convert.FromHexString("EF969E068680A0078780E0078680CC06C7ACE5038680B406"))
                .Msg(4, Pb.New().Var(1, 17).Var(2, 2))
                .Var(5, 42).VarIfNotZero(6, sex).Var(7, breed);
            try
            {
                await CharacterCreationHandler.CreateAsync(pipe.ToClient,
                    ConnectionProtocol.Push(Op.Kvz, Pb.New().Msg(1, creation).Build()), account, 290);
                Assert.Empty(await pipe.Next(Op.Kvb));
                for (int i = 0; i < 3; i++) await pipe.Next(Op.Kqp);
                byte[] list = await pipe.Next(Op.Kvi);
                await pipe.Next(Op.Jtg);

                var character = Assert.Single(DatabaseManager.GetCharactersByAccountId(account, 290));
                // Reload through the other SQL reader as on a subsequent connection.
                character = DatabaseManager.GetCharacterById(character.Id);
                Assert.NotNull(character);
                Assert.Equal(breed, character.Breed);
                Assert.Equal(sex, character.Sex);
                Assert.Equal(head, character.HeadId);
                CheckLook(Convert.FromHexString(character.LookHex));
                CheckLook(At(list, 1, 1, 4, 6));
                var sexFields = ProtoMessage.Parse(At(list, 1, 1, 4, 2)).Fields;
                Assert.Equal(sex, sexFields.SingleOrDefault(f => f.FieldNumber == 3)?.VarIntValue ?? 0);
                var selection = CharacterSelectionMessages.Success(character);
                var selectedBasic = Assert.IsType<CharacterComplementCaseCharacterBasicInformation>(selection.Value.Character.ComplementCase).Value;
                var selectedLook = Assert.IsType<CharacterBasicInformationComplementCaseCharacterLook>(selectedBasic.ComplementCase).Value;
                Assert.Equal(Indexed.Select(value => (int)value), selectedLook.Look.Colors);
                Assert.Equal(new[] { (int)body, (int)headSkin }, selectedLook.Look.Skins);
                Assert.Equal(new[] { (int)scale }, selectedLook.Look.Scales);
                Assert.Equal(sex == 1 ? Gender.Female : Gender.Male,
                    Assert.IsType<CharacterLookComplementCaseBaseInformation>(selectedLook.ComplementCase).Value.Gender);
                // Creation/login is not a disconnection boundary. Only the known
                // current connection date is sent; no fresh timestamp is invented.
                Assert.Empty(selectedLook.LastDisconnectionDate);
                Assert.Equal(character.LastConnection, selectedLook.LastConnectionDate);
                CheckLook(At(ConnectionProtocol.BuildPlayerActorBlock(character, 245, 1, account), 2, 3));
                CheckLook(At(ConnectionProtocol.BuildLookChanged(character), 2));

                byte[] appearance = At(ConnectionProtocol.BuildAppearanceState(character, "draft"), 3);
                Assert.Equal(Rgb, Packed(At(appearance, 1, 2)));
                CheckLook(At(appearance, 12));
                foreach (var outfit in ProtoMessage.Parse(ConnectionProtocol.BuildOutfits(character)).Fields)
                {
                    Assert.Equal(Rgb, Packed(At(outfit.BytesValue, 1, 2)));
                    CheckLook(At(outfit.BytesValue, 12));
                }
                Assert.Contains("2=#E80006", BreedLookTable.Drawable(character));
            }
            finally
            {
                using var db = new SqliteConnection(DatabaseManager.WorldConnectionString);
                db.Open();
                try
                {
                    using var outfits = db.CreateCommand();
                    outfits.CommandText = "DELETE FROM CharacterOutfits WHERE CharacterId IN " +
                                          "(SELECT Id FROM Characters WHERE AccountId = $a);";
                    outfits.Parameters.AddWithValue("$a", account);
                    outfits.ExecuteNonQuery();
                }
                catch (SqliteException)
                {
                    // a base the outfits never reached
                }
                using var delete = db.CreateCommand();
                delete.CommandText = @"
                    DELETE FROM CharacterItems WHERE CharacterId IN (SELECT Id FROM Characters WHERE AccountId = $a);
                    DELETE FROM Characters WHERE AccountId = $a;";
                delete.Parameters.AddWithValue("$a", account);
                delete.ExecuteNonQuery();
            }
        }

        [Theory]
        [MemberData(nameof(BreedSexIds))]
        public void Default_slots_and_black_do_not_shift_a_later_custom_colour(int breed, int sex)
        {
            var defaults = BreedLookTable.Get(breed, sex).Colors;
            long[] colors = { -1, 0, -1, 0xD30006, -1, -1 };
            Assert.Equal(new long[]
            {
                0x01000000 | defaults[0], 0x02000000, 0x03000000 | defaults[2],
                0x04D30006, 0x05000000 | defaults[4], 0x06000000 | defaults[5]
            }, BreedLookTable.CreationColors(breed, sex, colors));
            Assert.Equal(BreedLookTable.IndexedColors(breed, sex),
                BreedLookTable.CreationColors(breed, sex, new long[] { -1, -1, -1, -1, -1, -1 }));
            Assert.Equal(BreedLookTable.IndexedColors(breed, sex),
                BreedLookTable.CreationColors(breed, sex, Array.Empty<long>()));
        }

        // Read the expectations independently from the client-derived catalogues, not from the
        // production lookup under test. New catalogue entries automatically become test cases.
        public static IEnumerable<object[]> BreedSexPairs()
        {
            using var breeds = JsonDocument.Parse(File.ReadAllText(Paths.BreedLooksJson));
            using var heads = JsonDocument.Parse(File.ReadAllText(Paths.HeadsJson));
            foreach (var breed in breeds.RootElement.EnumerateObject())
            foreach (var gender in breed.Value.EnumerateObject())
            {
                int breedId = int.Parse(breed.Name);
                int sex = gender.Name == "female" ? 1 : 0;
                yield return new object[]
                {
                    breedId, sex, heads.RootElement.GetProperty("defaults").GetProperty($"{breedId}_{sex}").GetInt32(),
                    gender.Value.GetProperty("skins")[0].GetInt64(), gender.Value.GetProperty("scales")[0].GetInt64()
                };
            }
        }

        public static IEnumerable<object[]> CatalogueHeads()
        {
            using var breeds = JsonDocument.Parse(File.ReadAllText(Paths.BreedLooksJson));
            using var heads = JsonDocument.Parse(File.ReadAllText(Paths.HeadsJson));
            foreach (var head in heads.RootElement.GetProperty("heads").EnumerateObject())
            {
                int breed = head.Value.GetProperty("breed").GetInt32();
                int sex = head.Value.GetProperty("gender").GetInt32();
                var body = breeds.RootElement.GetProperty(breed.ToString()).GetProperty(sex == 1 ? "female" : "male");
                yield return new object[]
                {
                    breed, sex, int.Parse(head.Name), head.Value.GetProperty("skin").GetInt64(),
                    body.GetProperty("skins")[0].GetInt64(), body.GetProperty("scales")[0].GetInt64()
                };
            }
        }

        public static IEnumerable<object[]> BreedSexIds() => BreedSexPairs().Select(row => row.Take(2).ToArray());
        public static IEnumerable<object[]> DefaultHeads() => BreedSexPairs().Select(row => row.Take(3).ToArray());

        [Theory]
        [MemberData(nameof(CatalogueHeads))]
        public void Every_catalogue_head_keeps_its_skin_body_and_palette(
            int breed, int sex, int head, long headSkin, long body, long scale)
        {
            var character = new DatabaseManager.DbCharacter
            {
                Breed = breed, Sex = sex, HeadId = head,
                LookHex = Convert.ToHexString(Pb.New().Packed(1, Indexed).Var(2, 3).Var(3, 1).Build())
            };
            AssertLook(BreedLookTable.BuildLook(character), body, scale, headSkin);
            AssertLook(At(ConnectionProtocol.BuildCharactersList(new[] { character }), 1, 1, 4, 6), body, scale, headSkin);
        }

        [Theory]
        [MemberData(nameof(DefaultHeads))]
        public void Missing_heads_fall_back_to_the_selected_class_and_sex(int breed, int sex, int head)
        {
            using var heads = JsonDocument.Parse(File.ReadAllText(Paths.HeadsJson));
            long expected = heads.RootElement.GetProperty("heads").GetProperty(head.ToString()).GetProperty("skin").GetInt64();
            foreach (int missing in new[] { 0, -1, int.MaxValue })
                Assert.Equal(expected, HeadTable.SkinFor(missing, breed, sex));
        }

        [Fact]
        public void Existing_creations_with_bare_RGB_are_read_with_their_slot_indices()
        {
            var character = new DatabaseManager.DbCharacter
            {
                Breed = 18, Sex = 1,
                LookHex = Convert.ToHexString(Pb.New().Packed(1, Rgb).Var(2, 3).Var(3, 1).Build())
            };
            Assert.Equal(Indexed, BreedLookTable.SavedColors(character));
        }

        [Theory]
        [InlineData(null)]
        [InlineData("")]
        [InlineData("not hex")]
        [InlineData("0A0180")]
        [InlineData("080118032218A28B9B0FCBE5F615A4E1B91992A6C820888CA028F5B7CB34")]
        public void Missing_or_legacy_look_uses_the_breed_palette(string? look)
        {
            var character = new DatabaseManager.DbCharacter { Breed = 18, Sex = 1, LookHex = look };
            Assert.Equal(BreedLookTable.IndexedColors(18, 1), BreedLookTable.SavedColors(character));
        }

        [Fact]
        public void Saved_indexed_colours_keep_their_explicit_slots()
        {
            long[] colors = { 0x02000000, 0x04D30006 };
            var character = new DatabaseManager.DbCharacter
            {
                Breed = 18, Sex = 1,
                LookHex = Convert.ToHexString(Pb.New().Packed(1, colors).Var(2, 3).Var(3, 1).Build())
            };
            Assert.Equal(colors, BreedLookTable.SavedColors(character));
        }

        private static void AssertLook(byte[] look, long body, long scale, long headSkin)
        {
            Assert.Equal(Indexed, Packed(At(look, 1)));
            Assert.Equal(new[] { body, headSkin }, Packed(At(look, 6)));
            Assert.Equal(new[] { scale }, Packed(At(look, 5)));
        }

        private static byte[] At(byte[] message, params int[] path)
        {
            foreach (int number in path)
                message = ProtoMessage.Parse(message).Fields.First(f => f.FieldNumber == number && f.WireType == 2).BytesValue;
            return message;
        }

        private static long[] Packed(byte[] bytes)
        {
            var values = new List<long>();
            using var input = new CodedInputStream(bytes);
            while (!input.IsAtEnd) values.Add(input.ReadInt64());
            return values.ToArray();
        }
    }
}
