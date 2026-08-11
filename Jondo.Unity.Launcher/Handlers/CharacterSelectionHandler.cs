using Google.Protobuf;
using Jondo.Unity.Launcher.Data;
using Jondo.Unity.Launcher.Network;
using Jondo.Unity.Launcher.Data;
using Jondo.Unity.Protocol.Messages;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Sockets;
using System.Text;
using System.Threading.Tasks;

namespace Jondo.Unity.Launcher.Handlers
{
    public static class CharacterSelectionHandler
    {
        private static async Task SendGameMessage(NetworkStream stream, string typeName, byte[] payload)
        {
            byte[] packet =
                NetworkEnvelope.BuildGameNodePacket(
                    $"type.ankama.com/{typeName}",
                    payload
                );

            await Jondo.Protocol.NetworkMessage.WriteFrameAsync(
                stream,
                packet
            );

            Console.WriteLine(
                $"[Game Node] Sent {typeName} ({payload.Length} B)"
            );
        }

        public static async Task HandleGameNodeAuth361010(NetworkStream stream,byte[] payload,string payloadStr)
        {
            // kqz reçu

            // 1. kra
            await SendGameMessage(stream, "kra", Array.Empty<byte>());

            // 2. lqu
            await SendGameMessage(stream, "lqu", BuildLqu361010());

            // 3. hoy
            await SendGameMessage(stream,"hoy",Convert.FromHexString("081E100118013202667238C801"));

            // 4. kqu
            await SendGameMessage(
                stream,
                "kqu",
                Convert.FromHexString(
                    "0A1103070D1417697C7D7E88018F0191019601"
                )
            );

            // 5. mgq
            await SendGameMessage(
                stream,
                "mgq",
                Convert.FromHexString("10011801")
            );

            // 6. mgt
            await SendGameMessage(
                stream,
                "mgt",
                Convert.FromHexString("1200")
            );

            // 7. hpd
            await SendGameMessage(stream,"hpd",Convert.FromHexString("0801")
            );

            // 8. krs
            await SendGameMessage(stream, "krs", Array.Empty<byte>());

            Console.WriteLine(
                "[Game Node] 3.6.10.10 initial handshake sent"
            );
        }


        public static async Task HandleCharacterList361010(NetworkStream stream)
        {
            // On appellera cette méthode après krv.

            // kqp #1
            await SendGameMessage(
                stream,
                "kqp",
                Convert.FromHexString("08011001")
            );

            // kqp #2
            await SendGameMessage(
                stream,
                "kqp",
                Convert.FromHexString("0801")
            );

            // kqp #3
            await SendGameMessage(
                stream,
                "kqp",
                Array.Empty<byte>()
            );

            // Ensuite kvi = vraie liste des personnages.
            await SendGameMessage(
                stream,
                "kvi",
                BuildKvi361010()
            );

            // mgz
            await SendGameMessage(
                stream,
                "mgz",
                Convert.FromHexString("0880D2C205")
            );

            // jtg ensuite.
            // Pour le premier test, on pourra décider
            // si on le reproduit ou si on le génère proprement.
        }

        private static byte[] BuildLqu361010()
        {
            var msg = new ProtoMessage();

            // UTC+2 pendant ta capture officielle.
            msg.Fields.Add(new ProtoField
            {
                FieldNumber = 1,
                WireType = 0,
                VarIntValue = 120
            });

            msg.Fields.Add(new ProtoField
            {
                FieldNumber = 2,
                WireType = 0,
                VarIntValue =
                    DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()
            });

            return msg.ToByteArray();
        }
        private static byte[] BuildKvi361010()
        {
            const long accountId = 188940901;

            var characters =
                DatabaseManager.GetCharactersByAccountId(
                    accountId
                );

            Console.WriteLine(
                $"[KVI 3.6.10.10] Building character list: " +
                $"{characters.Count} character(s)"
            );

            var kvi =
                new ProtoMessage();

            foreach (var character in characters)
            {
                try
                {
                    byte[] characterInfo =
                        BuildCharacterInfoForList361010(
                            character
                        );

                    /*
                     * details
                     *
                     * field2 = Name
                     * field3 = Level
                     * field4 = CharacterInfo
                     */
                    var details =
                        new ProtoMessage();

                    details.Fields.Add(
                        new ProtoField
                        {
                            FieldNumber = 2,
                            WireType = 2,
                            BytesValue =
                                Encoding.UTF8.GetBytes(
                                    character.Name
                                )
                        }
                    );

                    details.Fields.Add(
                        new ProtoField
                        {
                            FieldNumber = 3,
                            WireType = 0,
                            VarIntValue =
                                character.Level
                        }
                    );

                    details.Fields.Add(
                        new ProtoField
                        {
                            FieldNumber = 4,
                            WireType = 2,
                            BytesValue =
                                characterInfo
                        }
                    );

                    /*
                     * Character wrapper
                     *
                     * field1 = details
                     * field2 = CharacterId
                     */
                    var wrapper =
                        new ProtoMessage();

                    wrapper.Fields.Add(
                        new ProtoField
                        {
                            FieldNumber = 1,
                            WireType = 2,
                            BytesValue =
                                details.ToByteArray()
                        }
                    );

                    wrapper.Fields.Add(
                        new ProtoField
                        {
                            FieldNumber = 2,
                            WireType = 0,
                            VarIntValue =
                                character.Id
                        }
                    );

                    /*
                     * KVI :
                     * repeated field1 = characters
                     */
                    kvi.Fields.Add(
                        new ProtoField
                        {
                            FieldNumber = 1,
                            WireType = 2,
                            BytesValue =
                                wrapper.ToByteArray()
                        }
                    );

                    Console.WriteLine(
                        $"[KVI] Added character: " +
                        $"ID={character.Id}, " +
                        $"Name={character.Name}, " +
                        $"Level={character.Level}, " +
                        $"Breed={character.Breed}, " +
                        $"Sex={character.Sex}, " +
                        $"Look={characterInfo.Length} B"
                    );
                }
                catch (Exception ex)
                {
                    Console.WriteLine(
                        $"[KVI] ERROR building character " +
                        $"{character.Id} ({character.Name}): " +
                        ex.Message
                    );
                }
            }

            byte[] result =
                kvi.ToByteArray();

            Console.WriteLine(
                $"[KVI 3.6.10.10] List built: " +
                $"{characters.Count} character(s), " +
                $"{result.Length} B"
            );

            return result;
        }

        private static byte[] BuildCharacterInfoForList361010(DatabaseManager.DbCharacter character)
        {
            if (string.IsNullOrWhiteSpace(
                character.LookHex
            ))
            {
                throw new InvalidOperationException(
                    "Character Look is empty."
                );
            }

            byte[] lookBytes =
                Convert.FromHexString(
                    character.LookHex
                );

            /*
             * Nouveau personnage créé directement
             * en 3.6.10.10.
             */
            if (IsNativeLook361010(
                lookBytes
            ))
            {
                Console.WriteLine(
                    $"[KVI] {character.Name}: " +
                    $"native 3.6.10.10 look"
                );

                return lookBytes;
            }

            /*
             * Ancien personnage 3.6.4.3.
             *
             * Pour Keka-Bron notamment.
             */
            Console.WriteLine(
                $"[KVI] {character.Name}: " +
                $"legacy look -> conversion 3.6.10.10"
            );

            byte[] visual =
                ConvertLegacyLookTo361010(
                    lookBytes
                );

            var characterInfo =
                new ProtoMessage();

            /*
             * field2
             *
             * Sur nos personnages 3.6.10.10 :
             * field2 contient un petit message avec field3 = Sex.
             */
            var sexInfo =
                new ProtoMessage();

            sexInfo.Fields.Add(
                new ProtoField
                {
                    FieldNumber = 3,
                    WireType = 0,
                    VarIntValue =
                        character.Sex
                }
            );

            characterInfo.Fields.Add(
                new ProtoField
                {
                    FieldNumber = 2,
                    WireType = 2,
                    BytesValue =
                        sexInfo.ToByteArray()
                }
            );

            /*
             * field6 = visual converti
             */
            characterInfo.Fields.Add(
                new ProtoField
                {
                    FieldNumber = 6,
                    WireType = 2,
                    BytesValue =
                        visual
                }
            );

            /*
             * field7 = Breed
             */
            characterInfo.Fields.Add(
                new ProtoField
                {
                    FieldNumber = 7,
                    WireType = 0,
                    VarIntValue =
                        character.Breed
                }
            );

            return characterInfo.ToByteArray();
        }

        public static async Task HandleCharacterSelect361010(NetworkStream stream,byte[] payload)
        {
            Console.WriteLine($"[Game Node] Received kvw [3.6.10.10] ({payload.Length} B)");

            // 1. kqp vide
            await SendGameMessage(
                stream,
                "kqp",
                Array.Empty<byte>()
            );

            // 2. kub
            await SendGameMessage(
                stream,
                "kub",
                BuildKub361010()
            );

            // 3. itg
            await SendGameMessage(
                stream,
                "itg",
                Convert.FromHexString("1001")
            );

            // On s'arrête volontairement ici pour le premier test.
        }

        public static async Task HandleCreateCharacter361010(NetworkStream stream, byte[] payload)
        {
            try
            {
                Console.WriteLine(
                    $"[Game Node] Received kvz [3.6.10.10] ({payload.Length} B)"
                );

                /*
                 * kvz
                 * field1 = message création
                 *
                 * création :
                 *   field1 = nom
                 *   field2 = valeur apparence/variant
                 *   field3 = couleurs brutes
                 *   field5 = valeur apparence
                 *   field6 = sex
                 *   field7 = breed
                 */

                var kvz =
                    ProtoMessage.Parse(payload);

                var creationField =
                    kvz.Fields.FirstOrDefault(
                        f => f.FieldNumber == 1 &&
                             f.WireType == 2
                    );

                if (creationField == null)
                {
                    Console.WriteLine(
                        "[KVZ] ERROR: creation field absent."
                    );

                    return;
                }

                var creation =
                    ProtoMessage.Parse(
                        creationField.BytesValue
                    );

                // -----------------------------
                // Name
                // -----------------------------

                var nameField =
                    creation.Fields.FirstOrDefault(
                        f => f.FieldNumber == 1 &&
                             f.WireType == 2
                    );

                if (nameField == null)
                {
                    Console.WriteLine(
                        "[KVZ] ERROR: name absent."
                    );

                    return;
                }

                string name =
                    Encoding.UTF8.GetString(
                        nameField.BytesValue
                    );

                // -----------------------------
                // Field2
                // -----------------------------

                long appearance1 =
                    creation.Fields.FirstOrDefault(
                        f => f.FieldNumber == 2 &&
                             f.WireType == 0
                    )?.VarIntValue ?? 0;

                // -----------------------------
                // Colors
                // -----------------------------

                byte[] colors =
                    creation.Fields.FirstOrDefault(
                        f => f.FieldNumber == 3 &&
                             f.WireType == 2
                    )?.BytesValue
                    ?? Array.Empty<byte>();

                // -----------------------------
                // Field5
                // -----------------------------

                long appearance2 =
                    creation.Fields.FirstOrDefault(
                        f => f.FieldNumber == 5 &&
                             f.WireType == 0
                    )?.VarIntValue ?? 0;

                // -----------------------------
                // Sex
                // -----------------------------

                int sex =
                    (int)(
                        creation.Fields.FirstOrDefault(
                            f => f.FieldNumber == 6 &&
                                 f.WireType == 0
                        )?.VarIntValue ?? 0
                    );

                // -----------------------------
                // Breed
                // -----------------------------

                int breed =
                    (int)(
                        creation.Fields.FirstOrDefault(
                            f => f.FieldNumber == 7 &&
                                 f.WireType == 0
                        )?.VarIntValue ?? 0
                    );

                Console.WriteLine(
                    $"[KVZ] Name={name}, " +
                    $"Breed={breed}, " +
                    $"Sex={sex}, " +
                    $"Appearance1={appearance1}, " +
                    $"Appearance2={appearance2}, " +
                    $"Colors={Convert.ToHexString(colors)}"
                );

                // ============================================================
                // Apparence 3.6.10.10 depuis BreedAppearanceDatabase
                // ============================================================

                int headId =
                    checked((int)appearance1);

                BreedAppearanceInfo breedInfo =
                    BreedAppearanceDatabase.GetBreed(
                        breed
                    );

                BreedGenderAppearance appearance =
                    BreedAppearanceDatabase.GetAppearance(
                        breed,
                        sex
                    );

                HeadAppearanceInfo head =
                    BreedAppearanceDatabase.GetHead(
                        breed,
                        sex,
                        headId
                    );

                Console.WriteLine(
                    $"[APPEARANCE] " +
                    $"Breed={breed} ({breedInfo.Name}), " +
                    $"Sex={sex}, " +
                    $"HeadId={head.Id}"
                );

                Console.WriteLine(
                    $"[APPEARANCE] " +
                    $"BaseLook={appearance.RawLook}, " +
                    $"BaseSkin={appearance.BaseSkinId}, " +
                    $"HeadSkins={string.Join(",", head.Skins)}, " +
                    $"Scale={appearance.Scale}, " +
                    $"CreatureBones={breedInfo.CreatureBonesId}"
                );

                // Pour l'instant on continue à stocker les données de création.
                // Le prochain bloc remplacera ça par un vrai CharacterInfo.
                string lookHex =
                    BuildCreatedCharacterInfo361010(
                    colors,
                    breed,
                    sex,
                    appearance1
                );

                long newCharacterId =
                    DatabaseManager.CreateCharacter361010(
                    accountId: 188940901,
                    name: name,
                    breed: breed,
                    sex: sex,
                    lookHex: lookHex
                );

                if (newCharacterId <= 0)
                {
                    Console.WriteLine(
                        $"[KVZ] Character creation failed: {name}"
                    );

                    await SendGameMessage(
                        stream,
                        "kvb",
                        Convert.FromHexString("08051002")
                    );

                    return;
                }

                Console.WriteLine(
                    $"[KVZ] Character created: " +
                    $"Name={name}, ID={newCharacterId}, " +
                    $"Breed={breed}, Sex={sex}"
                );
            }
            catch
            {
                Console.WriteLine(
                    $"error for Look"
                );
            }

        }

        //private static string BuildCreatedCharacterInfo361010(
        //    byte[] kvzColors,
        //    int breed,
        //    int sex)
        //{
        //    byte[] visualColors =
        //        ConvertKvzColorsToVisual361010(
        //            kvzColors
        //        );

        //    // ============================================================
        //    // VISUAL
        //    // ============================================================

        //    var visual =
        //        new ProtoMessage();

        //    // field1 = couleurs
        //    visual.Fields.Add(
        //        new ProtoField
        //        {
        //            FieldNumber = 1,
        //            WireType = 2,
        //            BytesValue = visualColors
        //        }
        //    );

        //    /*
        //     * Structure native 3.6.10.10 déjà validée
        //     * par le client.
        //     *
        //     * On remet exactement le format qui donnait
        //     * des CharacterInfo de 46 octets.
        //     */
        //    AddVarInt(
        //        visual,
        //        2,
        //        3
        //    );

        //    AddVarInt(
        //        visual,
        //        3,
        //        1
        //    );

        //    visual.Fields.Add(
        //        new ProtoField
        //        {
        //            FieldNumber = 5,
        //            WireType = 2,
        //            BytesValue =
        //                Encoding.UTF8.GetBytes("4")
        //        }
        //    );

        //    visual.Fields.Add(
        //        new ProtoField
        //        {
        //            FieldNumber = 6,
        //            WireType = 2,
        //            BytesValue =
        //                Convert.FromHexString(
        //                    "5BE410"
        //                )
        //        }
        //    );

        //    // ============================================================
        //    // CHARACTER INFO
        //    // ============================================================

        //    var characterInfo =
        //        new ProtoMessage();

        //    var sexInfo =
        //        new ProtoMessage();

        //    AddVarInt(
        //        sexInfo,
        //        3,
        //        sex
        //    );

        //    AddMessage(
        //        characterInfo,
        //        2,
        //        sexInfo.ToByteArray()
        //    );

        //    AddMessage(
        //        characterInfo,
        //        6,
        //        visual.ToByteArray()
        //    );

        //    AddVarInt(
        //        characterInfo,
        //        7,
        //        breed
        //    );

        //    byte[] result =
        //        characterInfo.ToByteArray();

        //    Console.WriteLine(
        //        $"[CREATE LOOK] Native CharacterInfo: " +
        //        $"Breed={breed}, " +
        //        $"Sex={sex}, " +
        //        $"Length={result.Length} B, " +
        //        $"Hex={Convert.ToHexString(result)}"
        //    );

        //    return Convert.ToHexString(
        //        result
        //    );
        //}
        private static string BuildCreatedCharacterInfo361010(
    byte[] kvzColors,
    int breed,
    int sex,
    long appearance1)
        {
            // ============================================================
            // DONNÉES RÉELLES DU CLIENT
            // ============================================================

            BreedAppearanceInfo breedInfo =
                BreedAppearanceDatabase.GetBreed(
                    breed
                );

            BreedGenderAppearance appearance =
                BreedAppearanceDatabase.GetAppearance(
                    breed,
                    sex
                );

            int headId =
                checked((int)appearance1);

            HeadAppearanceInfo head =
                BreedAppearanceDatabase.GetHead(
                    breed,
                    sex,
                    headId
                );

            if (head.Skins.Length == 0)
            {
                throw new InvalidOperationException(
                    $"Head {headId} has no skin."
                );
            }

            int headSkin =
                head.Skins[0];

            int baseSkin =
                appearance.BaseSkinId;

            int scale =
                appearance.Scale;

            Console.WriteLine(
                $"[CREATE LOOK] " +
                $"Breed={breed} ({breedInfo.Name}), " +
                $"Sex={sex}, " +
                $"BaseSkin={baseSkin}, " +
                $"HeadId={headId}, " +
                $"HeadSkin={headSkin}, " +
                $"Scale={scale}, " +
                $"CreatureBones={breedInfo.CreatureBonesId}"
            );

            // ============================================================
            // COULEURS
            // ============================================================

            byte[] visualColors =
                ConvertKvzColorsToVisual361010(
                    kvzColors
                );

            // ============================================================
            // SKINS
            //
            // field6 contient des varints bruts concaténés.
            //
            // Exemple Cra :
            // 91 + 2148
            //
            // Pour Dragonqueen :
            // 3499 + 5538
            // ============================================================

            using var skinsStream =
                new MemoryStream();

            WriteRawVarInt361010(
                skinsStream,
                baseSkin
            );

            WriteRawVarInt361010(
                skinsStream,
                headSkin
            );

            byte[] skins =
                skinsStream.ToArray();

            // ============================================================
            // SCALE
            //
            // field5 est également un bloc de varints bruts.
            // ============================================================

            using var scaleStream =
                new MemoryStream();

            WriteRawVarInt361010(
                scaleStream,
                scale
            );

            byte[] scaleBytes =
                scaleStream.ToArray();

            // ============================================================
            // VISUAL / EntityLook
            // ============================================================

            var visual =
                new ProtoMessage();

            // field1 = couleurs
            visual.Fields.Add(
                new ProtoField
                {
                    FieldNumber = 1,
                    WireType = 2,
                    BytesValue = visualColors
                }
            );

            // field2 = 3
            //
            // Constant sur les EntityLook observés jusqu'ici.
            AddVarInt(
                visual,
                2,
                3
            );

            // field3 = bonesId de l'EntityLook.
            //
            // MaleLook/FemaleLook commencent par :
            // {1|....}
            //
            // donc ici = 1.
            AddVarInt(
                visual,
                3,
                appearance.BonesId
            );

            // field5 = scale
            visual.Fields.Add(
                new ProtoField
                {
                    FieldNumber = 5,
                    WireType = 2,
                    BytesValue = scaleBytes
                }
            );

            // field6 = skins
            visual.Fields.Add(
                new ProtoField
                {
                    FieldNumber = 6,
                    WireType = 2,
                    BytesValue = skins
                }
            );

            // ============================================================
            // CHARACTER INFO
            // ============================================================

            var characterInfo =
                new ProtoMessage();

            var sexInfo =
                new ProtoMessage();

            AddVarInt(
                sexInfo,
                3,
                sex
            );

            AddMessage(
                characterInfo,
                2,
                sexInfo.ToByteArray()
            );

            AddMessage(
                characterInfo,
                6,
                visual.ToByteArray()
            );

            AddVarInt(
                characterInfo,
                7,
                breed
            );

            byte[] result =
                characterInfo.ToByteArray();

            Console.WriteLine(
                $"[CREATE LOOK] Native dynamic CharacterInfo: " +
                $"Length={result.Length} B, " +
                $"Skins={Convert.ToHexString(skins)}, " +
                $"Scale={Convert.ToHexString(scaleBytes)}, " +
                $"Hex={Convert.ToHexString(result)}"
            );

            return Convert.ToHexString(
                result
            );
        }


        private static byte[] ConvertKvzColorsToVisual361010(
    byte[] kvzColors)
        {
            List<long> colors =
                ReadRawVarInts361010(
                    kvzColors
                );

            using var ms =
                new MemoryStream();

            foreach (
                var pair in colors.Select(
                    (value, index) =>
                        new
                        {
                            value,
                            index
                        }
                )
            )
            {
                /*
                 * kvz :
                 *   0xF0E1DA
                 *
                 * kvi :
                 *   0x01F0E1DA
                 *
                 * donc :
                 * slot << 24 | RGB
                 */

                long color =
                    pair.value;

                /*
                 * -1 = couleur par défaut.
                 *
                 * Pour le moment on ne peut pas convertir
                 * dynamiquement -1 sans connaître la palette
                 * par défaut de chaque Breed.
                 *
                 * Les couleurs personnalisées sont supportées.
                 */

                if (color < 0)
                    continue;

                long visualColor =
                    ((long)(pair.index + 1) << 24)
                    |
                    (color & 0x00FFFFFF);

                WriteRawVarInt361010(
                    ms,
                    visualColor
                );
            }

            return ms.ToArray();
        }

        private static List<long> ReadRawVarInts361010(
    byte[] data)
        {
            var result =
                new List<long>();

            int position = 0;

            while (position < data.Length)
            {
                ulong value = 0;
                int shift = 0;

                while (position < data.Length)
                {
                    byte b =
                        data[position++];

                    value |=
                        ((ulong)(b & 0x7F))
                        << shift;

                    if ((b & 0x80) == 0)
                        break;

                    shift += 7;
                }

                result.Add(
                    unchecked((long)value)
                );
            }

            return result;
        }
        private static void WriteRawVarInt361010(
            Stream stream,
            long value)
        {
            ulong v =
                unchecked((ulong)value);

            while (v >= 0x80)
            {
                stream.WriteByte(
                    (byte)(
                        (v & 0x7F)
                        | 0x80
                    )
                );

                v >>= 7;
            }

            stream.WriteByte(
                (byte)v
            );
        }

        private static byte[] BuildKub361010()
        {
            byte[] ldn = BuildLdn361010();

            var kub = new ProtoMessage();

            // kub.field2 = ldn
            kub.Fields.Add(new ProtoField
            {
                FieldNumber = 2,
                WireType = 2,
                BytesValue = ldn
            });

            byte[] result = kub.ToByteArray();

            Console.WriteLine(
                $"[KUB 3.6.10.10] Built: {result.Length} bytes"
            );

            return result;
        }

        private static byte[] BuildLdn361010()
        {
            var ldn = new ProtoMessage();

            // field1
            AddVarInt(ldn, 1, 57_284_000);

            // field4
            AddVarInt(ldn, 4, 5);

            // field7
            AddVarInt(ldn, 7, 54_704_000);

            // field8
            AddVarInt(ldn, 8, 56_409_906);

            // field9 = llp
            AddMessage(
                ldn,
                9,
                BuildLlp361010()
            );

            // field10
            AddVarInt(ldn, 10, 1_457_364);

            // field11 = repeated lns
            foreach (byte[] characteristic in BuildMinimalCharacteristics361010())
            {
                AddMessage(
                    ldn,
                    11,
                    characteristic
                );
            }

            byte[] result = ldn.ToByteArray();

            Console.WriteLine(
                $"[LDN 3.6.10.10] Built: {result.Length} bytes"
            );

            return result;
        }

        private static byte[] BuildLlp361010()
        {
            var llp = new ProtoMessage();

            // llp.field2 = 2
            AddVarInt(
                llp,
                2,
                2
            );

            // llp.field3 = lln
            AddMessage(
                llp,
                3,
                BuildLln361010()
            );

            // llp.field5 = 87
            AddVarInt(
                llp,
                5,
                87
            );

            byte[] result = llp.ToByteArray();

            Console.WriteLine(
                $"[LLP 3.6.10.10] Built: {result.Length} bytes"
            );

            return result;
        }

        private static byte[] BuildLln361010()
        {
            var lln = new ProtoMessage();

            // field3 = 500
            AddVarInt(
                lln,
                3,
                500
            );

            return lln.ToByteArray();
        }

        private static byte[] BuildLnsLcr361010(int characteristicId,long value = 0)
        {
            var lns = new ProtoMessage();

            // lns.field1 = characteristic ID
            AddVarInt(
                lns,
                1,
                characteristicId
            );

            // lns.field4 = lcr
            AddMessage(
                lns,
                4,
                BuildLcr361010(value)
            );

            return lns.ToByteArray();
        }

        private static byte[] BuildLcr361010(long value)
        {
            var lcr = new ProtoMessage();

            if (value != 0)
            {
                AddVarInt(
                    lcr,
                    2,
                    value
                );
            }

            return lcr.ToByteArray();
        }

        private static byte[] BuildLnsLoa361010(
    int characteristicId,
    int value)
        {
            var lns = new ProtoMessage();

            AddVarInt(
                lns,
                1,
                characteristicId
            );

            AddMessage(
                lns,
                5,
                BuildLoa361010(value)
            );

            return lns.ToByteArray();
        }

        private static byte[] BuildLoa361010(int value1,int value2 = 0,int value3 = 0, int value4 = 0,int value5 = 0,int value6 = 0,int value7 = 0,int value8 = 0)
        {
            var loa = new ProtoMessage();

            if (value1 != 0) AddVarInt(loa, 1, value1);
            if (value2 != 0) AddVarInt(loa, 2, value2);
            if (value3 != 0) AddVarInt(loa, 3, value3);
            if (value4 != 0) AddVarInt(loa, 4, value4);
            if (value5 != 0) AddVarInt(loa, 5, value5);
            if (value6 != 0) AddVarInt(loa, 6, value6);
            if (value7 != 0) AddVarInt(loa, 7, value7);
            if (value8 != 0) AddVarInt(loa, 8, value8);

            return loa.ToByteArray();
        }

        private static byte[] BuildLnsLef361010(int characteristicId, byte[]? lef = null)
        {
            var lns = new ProtoMessage();
            AddVarInt(lns,1,characteristicId);
            AddMessage(lns,2,lef ?? BuildLef361010());
            return lns.ToByteArray();
        }

        private static byte[] BuildLef361010(int field1 = 0,int field2 = 0)
        {
            var lef = new ProtoMessage();

            if (field1 != 0)
                AddVarInt(lef, 1, field1);

            if (field2 != 0)
                AddVarInt(lef, 2, field2);

            return lef.ToByteArray();
        }

        private static IEnumerable<byte[]> BuildMinimalCharacteristics361010()
        {
            // ID 1 -> loa.field1 = 6
            yield return BuildLnsLoa361010(1, 6);

            // ID 3 -> lcr.field2 = 22
            yield return BuildLnsLcr361010(3, 22);

            // Caractéristiques présentes mais vides
            yield return BuildLnsLcr361010(5);
            yield return BuildLnsLcr361010(10);
            yield return BuildLnsLcr361010(11);

            // ID 12 -> 136
            yield return BuildLnsLcr361010(12, 136);

            // ID 27 / 28 -> 13
            yield return BuildLnsLcr361010(27, 13);
            yield return BuildLnsLcr361010(28, 13);

            // ID 29 -> lef vide
            yield return BuildLnsLef361010(29);

            // ID 40 -> 430
            yield return BuildLnsLcr361010(40, 430);

            // ID 47 -> lef.field2 = 10000
            yield return BuildLnsLef361010(
                47,
                BuildLef361010(field2: 10_000)
            );

            // ID 48 -> 100
            yield return BuildLnsLcr361010(48, 100);

            // ID 75 -> 10
            yield return BuildLnsLcr361010(75, 10);

            // ID 82 / 83 -> 13
            yield return BuildLnsLcr361010(82, 13);
            yield return BuildLnsLcr361010(83, 13);

            foreach (int id in new[]
            {
                107,
                120,
                121,
                122,
                123,
                124,
                125,
                141,
                142,
                143,
                150
            })
            {
                yield return BuildLnsLcr361010(id, 100);
            }
        }

        private static void AddVarInt(ProtoMessage message,int fieldNumber,long value)
        {
            message.Fields.Add(new ProtoField
            {
                FieldNumber = fieldNumber,
                WireType = 0,
                VarIntValue = value
            });
        }

        private static void AddMessage(ProtoMessage message,int fieldNumber,byte[] payload)
        {
            message.Fields.Add(new ProtoField
            {
                FieldNumber = fieldNumber,
                WireType = 2,
                BytesValue = payload
            });
        }

        private static byte[] ConvertLegacyLookTo361010(byte[] legacyLookBytes)
        {
            var legacy = ProtoMessage.Parse(legacyLookBytes);
            var visual = new ProtoMessage();

            // Legacy field4 -> 3.6.10.10 field1
            var legacyField4 = legacy.Fields.FirstOrDefault(
                f => f.FieldNumber == 4 && f.WireType == 2
            );

            if (legacyField4 != null)
            {
                visual.Fields.Add(new ProtoField
                {
                    FieldNumber = 1,
                    WireType = 2,
                    BytesValue = legacyField4.BytesValue
                });
            }

            // Legacy field3 -> 3.6.10.10 field2
            var legacyField3 = legacy.Fields.FirstOrDefault(
                f => f.FieldNumber == 3 && f.WireType == 0
            );

            if (legacyField3 != null)
            {
                visual.Fields.Add(new ProtoField
                {
                    FieldNumber = 2,
                    WireType = 0,
                    VarIntValue = legacyField3.VarIntValue
                });
            }

            // Legacy field1 -> 3.6.10.10 field3
            var legacyField1 = legacy.Fields.FirstOrDefault(
                f => f.FieldNumber == 1 && f.WireType == 0
            );

            if (legacyField1 != null)
            {
                visual.Fields.Add(new ProtoField
                {
                    FieldNumber = 3,
                    WireType = 0,
                    VarIntValue = legacyField1.VarIntValue
                });
            }

            // Legacy field8 -> 3.6.10.10 field5
            var legacyField8 = legacy.Fields.FirstOrDefault(
                f => f.FieldNumber == 8 && f.WireType == 2
            );

            if (legacyField8 != null)
            {
                visual.Fields.Add(new ProtoField
                {
                    FieldNumber = 5,
                    WireType = 2,
                    BytesValue = legacyField8.BytesValue
                });
            }

            // Legacy field5 -> 3.6.10.10 field6
            var legacyField5 = legacy.Fields.FirstOrDefault(
                f => f.FieldNumber == 5 && f.WireType == 2
            );

            if (legacyField5 != null)
            {
                visual.Fields.Add(new ProtoField
                {
                    FieldNumber = 6,
                    WireType = 2,
                    BytesValue = legacyField5.BytesValue
                });
            }

            byte[] result = visual.ToByteArray();

            Console.WriteLine(
                $"[KVI] Converted legacy look -> 3.6.10.10 visual: " +
                $"{Convert.ToHexString(result)}"
            );

            return result;
        }

        private static bool IsNativeLook361010(byte[]? lookBytes)
        {
            if (lookBytes == null || lookBytes.Length == 0)
                return false;

            try
            {
                var characterInfo =
                    ProtoMessage.Parse(lookBytes);

                /*
                 * Nouveau CharacterInfo 3.6.10.10 observé :
                 *
                 * field2 = sous-message
                 * field6 = visual
                 * field7 = breed
                 *
                 * Le critère important est surtout field6 :
                 *
                 * visual.field1 = couleurs (bytes)
                 * visual.field2 = varint
                 * visual.field3 = varint
                 */

                var visualField =
                    characterInfo.Fields.FirstOrDefault(
                        f => f.FieldNumber == 6 &&
                             f.WireType == 2
                    );

                var breedField =
                    characterInfo.Fields.FirstOrDefault(
                        f => f.FieldNumber == 7 &&
                             f.WireType == 0
                    );

                var infoField =
                    characterInfo.Fields.FirstOrDefault(
                        f => f.FieldNumber == 2 &&
                             f.WireType == 2
                    );

                if (visualField == null ||
                    breedField == null ||
                    infoField == null)
                {
                    return false;
                }

                /*
                 * Vérification du contenu réel de field6.
                 *
                 * C'est ça qui différencie vraiment :
                 *
                 * ancien field6 = 20 01
                 *
                 * nouveau field6 =
                 *   field1 = bloc couleurs
                 *   field2
                 *   field3
                 *   ...
                 */

                var visual =
                    ProtoMessage.Parse(
                        visualField.BytesValue
                    );

                var colorsField =
                    visual.Fields.FirstOrDefault(
                        f => f.FieldNumber == 1 &&
                             f.WireType == 2
                    );

                var visualField2 =
                    visual.Fields.FirstOrDefault(
                        f => f.FieldNumber == 2 &&
                             f.WireType == 0
                    );

                var visualField3 =
                    visual.Fields.FirstOrDefault(
                        f => f.FieldNumber == 3 &&
                             f.WireType == 0
                    );

                if (colorsField == null ||
                    visualField2 == null ||
                    visualField3 == null)
                {
                    return false;
                }

                /*
                 * Nos captures natives ont 6 couleurs varint
                 * encodées dans environ 24 octets.
                 *
                 * On ne force pas exactement 24,
                 * mais on exige un bloc non vide raisonnable.
                 */

                if (colorsField.BytesValue == null ||
                    colorsField.BytesValue.Length < 6)
                {
                    return false;
                }

                return true;
            }
            catch
            {
                return false;
            }
        }
    }
}
