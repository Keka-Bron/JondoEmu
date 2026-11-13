using Jondo.Unity.Launcher;
using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using Jondo.Unity.Server.Network;
using Jondo.Unity.Protocol.Game;

namespace Jondo.Unity.Server.Managers
{
    /// <summary>
    /// Base look of every breed, exactly as the client itself defines it.
    ///
    /// The data comes from breed_looks.json, which tools/extract_breed_looks.py generates by
    /// reading the client's breed bundle. That is where the bonesId, the skins, the scales and
    /// the six default colors of each breed and sex come from.
    ///
    /// With that we assemble the look block of the 3.6.10.10 protocol, whose layout has been
    /// checked against the character selection captures:
    ///
    ///     f1 : indexed colors, packed, each one (index &lt;&lt; 24) | rgb
    ///     f2 : 3   (constant across every observed sample)
    ///     f3 : bonesId
    ///     f5 : scales, packed
    ///     f6 : skins, packed
    ///     f7 : sub-entities (mount, pet), with the same nested layout
    ///
    /// It is rebuilt from the client data instead of reusing the look that ended up stored in the
    /// database: older rows use a different protocol layout and current equipment can change.
    /// Only the palette is recovered from a stored look in the current layout.
    /// </summary>
    public static class BreedLookTable
    {
        /// <summary>Constant value of field 2 of the look block in every 3.6.10.10 capture.</summary>
        private const int LookType = 3;

        // The EQUIPMENT slots -- not the appearance window's, which are others -- of the three
        // families that carry a skin. They come from looking at which item type takes each slot
        // in the database: slot 6 is taken by type 16, 7 by type 17 and 15 by type 82.
        private const int SlotSombrero = 6;
        private const int SlotCapa = 7;
        private const int SlotEscudo = 15;

        public sealed class BreedLook
        {
            public int Bones { get; set; } = 1;
            public List<long> Skins { get; set; } = new List<long>();
            public List<long> Scales { get; set; } = new List<long>();
            public List<long> Colors { get; set; } = new List<long>();
        }

        private static readonly Dictionary<int, Dictionary<int, BreedLook>> _byBreed
            = new Dictionary<int, Dictionary<int, BreedLook>>();
        /// <summary>
        /// Whether the table is filled in and safe to read. Volatile, and raised LAST: see
        /// <see cref="EnsureLoaded"/>.
        /// </summary>
        private static volatile bool _loaded;
        private static readonly object _lock = new object();

        /// <summary>Lazy loading: the first query reads the file.</summary>
        /// <remarks>
        /// THE FLAG GOES UP AFTER THE LOAD, NEVER BEFORE. It used to be raised on entry, and the
        /// lock-free fast path right above then waved every other thread straight through while
        /// this one was still parsing the json: they read a dictionary that was still empty, Get
        /// answered null, and the character came out with no look at all.
        ///
        /// That window is not theoretical. With sixteen threads asking at the same instant --
        /// which is what the test suite does, running its classes in parallel -- fourteen of them
        /// got nothing, every single run.
        ///
        /// And nothing is drawn and nothing is logged when it happens, which is exactly why it
        /// looked like a flaky test instead of a race: the same call answers correctly a moment
        /// later, once the load has finished.
        ///
        /// Volatile because the flag is read outside the lock, so it has to be the write that
        /// publishes the dictionary rather than one a reader may see out of order.
        /// </remarks>
        private static void EnsureLoaded()
        {
            if (_loaded) return;
            lock (_lock)
            {
                if (_loaded) return;
                try
                {
                    string path = Paths.BreedLooksJson;
                    if (!File.Exists(path))
                    {
                        Console.WriteLine($"[Looks] Cannot find {Path.GetFileName(path)}. " +
                                          "Run tools/extract_breed_looks.py to generate it.");
                        return;
                    }

                    using var doc = JsonDocument.Parse(File.ReadAllText(path));
                    foreach (var breed in doc.RootElement.EnumerateObject())
                    {
                        if (!int.TryParse(breed.Name, out int breedId)) continue;
                        var bySex = new Dictionary<int, BreedLook>();
                        foreach (var sex in breed.Value.EnumerateObject())
                        {
                            int sexId = sex.Name == "female" ? 1 : 0;
                            bySex[sexId] = ReadLook(sex.Value);
                        }
                        _byBreed[breedId] = bySex;
                    }
                    Console.WriteLine($"[Looks] Loaded the base looks of {_byBreed.Count} breeds.");
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"[Looks] Error loading the breed looks: {ex.Message}");
                }
                finally
                {
                    // In a finally so that a missing file or a broken json still counts as tried:
                    // the early return above would otherwise leave the flag down and we would go
                    // back to the disk, and log the same complaint, for every look built after.
                    _loaded = true;
                }
            }
        }

        private static BreedLook ReadLook(JsonElement el)
        {
            var look = new BreedLook();
            if (el.TryGetProperty("bones", out var b) && b.TryGetInt32(out int bones)) look.Bones = bones;
            look.Skins = ReadList(el, "skins");
            look.Scales = ReadList(el, "scales");
            look.Colors = ReadList(el, "colors");
            return look;
        }

        private static List<long> ReadList(JsonElement el, string name)
        {
            var values = new List<long>();
            if (el.TryGetProperty(name, out var arr) && arr.ValueKind == JsonValueKind.Array)
            {
                foreach (var v in arr.EnumerateArray())
                {
                    if (v.TryGetInt64(out long n)) values.Add(n);
                }
            }
            return values;
        }

        public static BreedLook? Get(int breedId, int sex)
        {
            EnsureLoaded();
            if (_byBreed.TryGetValue(breedId, out var bySex))
            {
                if (bySex.TryGetValue(sex == 1 ? 1 : 0, out var look)) return look;
                foreach (var any in bySex.Values) return any;
            }
            return null;
        }

        /// <summary>Resolve the creation screen's bare RGB values and -1 defaults without moving slots.</summary>
        public static List<long> CreationColors(int breedId, int sex, IReadOnlyList<long> colors)
        {
            var defaults = Get(breedId, sex)?.Colors;
            var rgb = new List<long>();
            int count = defaults?.Count ?? colors.Count;
            for (int i = 0; i < count; i++)
                rgb.Add(i < colors.Count && colors[i] >= 0
                    ? colors[i] & 0xFFFFFF
                    : (defaults != null ? defaults[i] : 0));
            return IndexColors(rgb);
        }

        /// <summary>
        /// Read only the palette from the persisted look; body, equipment and mounts are rebuilt.
        /// Older protocol layouts have no packed f1 and fall back to the breed's palette.
        /// </summary>
        public static List<long> SavedColors(DatabaseManager.DbCharacter character)
        {
            if (!string.IsNullOrEmpty(character.LookHex))
            {
                try
                {
                    var look = ProtoMessage.Parse(Convert.FromHexString(character.LookHex));
                    foreach (var field in look.Fields)
                    {
                        if (field.FieldNumber != 1 || field.WireType != 2) continue;
                        var colors = new List<long>();
                        using var input = new Google.Protobuf.CodedInputStream(field.BytesValue);
                        while (!input.IsAtEnd) colors.Add(input.ReadInt64());
                        if (colors.Count == 0) continue;

                        // Creations made before the fix stored bare RGB in this field.
                        if (colors.TrueForAll(c => c >= 0 && c <= 0xFFFFFF))
                            return IndexColors(colors);
                        return colors;
                    }
                }
                catch (FormatException) { }
                catch (Google.Protobuf.InvalidProtocolBufferException) { }
            }
            return IndexedColors(character.Breed, character.Sex);
        }

        /// <summary>Rebuild a character with their persisted palette and current equipment.</summary>
        /// <summary>
        /// A character's look on the map: a disguise's look when he wears one and is not mutated
        /// (SlotLooks), else his look. Not in his fights, where a disguise does not go.
        /// </summary>
        public static byte[] MapLook(DatabaseManager.DbCharacter character)
        {
            if (Phantoms.Is(character.Id)) return ConnectionProtocol.MonsterLook(Phantoms.LookOf(character.Sex), false).Build();
            if (Mutations.OfCharacter(character.Id) == null)
            {
                string? disguise = SlotLooks.DisguiseLook(SlotLooks.Of(character.Id));
                if (disguise != null) return ConnectionProtocol.MonsterLook(disguise, false).Build();
            }
            return BuildLook(character);
        }

        public static byte[] BuildLook(DatabaseManager.DbCharacter character,
                                       bool paraLaVentana = false, bool onFoot = false)
            => WriteLook(SemanticLook(character, paraLaVentana, onFoot)).Build();

        /// <summary>The resolved appearance, before any wire serialization.</summary>
        public static EntityLook SemanticLook(DatabaseManager.DbCharacter character,
                                              bool paraLaVentana = false, bool onFoot = false)
            => Mutations.OfCharacter(character.Id) is { } mutation && !paraLaVentana
                ? ConnectionProtocol.SemanticMonsterLook(mutation.Look, false)
                : SemanticLook(character.Breed, character.Sex, character.HeadId, SavedColors(character),
                               character.Id, paraLaVentana, onFoot);

        /// <summary>
        /// Assembles a character's look block. If custom colors are passed those are used;
        /// otherwise, the six defaults of the breed.
        ///
        /// The skins go out in the order seen in the capture: first the breed's, then the head.
        /// A character created in the real game came back as skins [110, 2172] — base and head —
        /// and without the second one the client draws it with no face.
        /// </summary>
        /// <param name="customColors">Colours already indexed for the look. CreationColors converts the creation screen's RGB values.</param>
        /// <param name="paraLaVentana">
        /// True only for the appearance panel's preview. It changes one thing: mounted, the appearance
        /// PET does not go out to the world -- a mount and a pet cannot be carried at once, because they
        /// share an equipment slot -- but the panel does show it, so that what was chosen is seen. It is
        /// measured: in the captures there are 49 kmb of the mounted character and none carries a pet,
        /// while 502 mounted lxc do. On foot it goes out in both.
        /// </param>
        public static byte[] BuildLook(int breedId, int sex, int headId = 0,
                                       IReadOnlyList<long>? customColors = null,
                                       long characterId = 0,
                                       bool paraLaVentana = false,
                                       bool onFoot = false)
            => WriteLook(SemanticLook(breedId, sex, headId, customColors, characterId, paraLaVentana, onFoot)).Build();

        private static EntityLook SemanticLook(int breedId, int sex, int headId,
                                              IReadOnlyList<long>? customColors, long characterId,
                                              bool paraLaVentana, bool onFoot)
        {
            // With an id that character is asked about, which is what is needed on the selection
            // screen; without it, the one playing.
            var mount = characterId != 0 ? Mounts.RiddenBy(characterId) : Mounts.Ridden();

            long quien = characterId != 0 ? characterId : Jondo.Unity.Server.Network.SessionContext.State.CharacterId;
            var prendas = Wardrobe.AppearanceOf(quien);

            // Mounted, the root is the mount; a PETSMOUNT or an APPEARANCE MOUNT rules over that root,
            // which is what makes the dragoturkey look like something else. Both go to the same slot
            // and both can bring bones, scale, colour and -- only the appearance ones -- a skin of
            // their own.
            Cosmetics.PieceLook? impuesto = null;
            foreach (var prenda in prendas)
            {
                if (prenda.Slot != Cosmetics.SlotMount || prenda.Hidden) continue;
                var suyo = Cosmetics.MountLookOf(prenda.Gid);
                if (suyo != null) impuesto = suyo;
            }

            // A real petsmount takes slot 8 and is ridden, but its look is not in mounts.json, which
            // only carries dragoturkeys, muldos and flyhorns. So it is drawn mounted only if there is
            // somewhere to take the bones from: the mount's, or the appearance garment's. With
            // neither of the two, better on foot than an empty skeleton.
            int huesosRaiz = (impuesto != null && impuesto.Bones != 0) ? impuesto.Bones
                                                                      : (mount?.Bones ?? 0);
            bool montado = !onFoot && mount != null && huesosRaiz != 0;

            var colores = ColorsFor(breedId, sex, customColors);
            var cuerpo = BuildBodyLook(breedId, sex, headId, customColors, montado, prendas, quien);

            // The aura, on the map look only: no preview (lxc) of the captures carries it.
            int auraBones = paraLaVentana ? 0 : Cosmetics.AuraBones(Wardrobe.ShownBy(quien));

            // On foot the root is the character himself, so the pet hangs off him, and the aura after it.
            if (!montado)
            {
                return AddAura(AddPets(cuerpo, prendas, colores), auraBones);
            }

            return Mounted(cuerpo, mount!, impuesto,
                           paraLaVentana ? prendas : null, colores, auraBones);
        }

        /// <summary>
        /// A look put together from its parts, for a fighter the server dresses itself -- a
        /// JondoBot: the body (its bones, its skins, its colours already indexed, its scales) and,
        /// when it rides, the mount -- one of mounts.json, or an appearance one -- with the body
        /// inside as the rider, on the rider's bones.
        /// </summary>
        public static byte[] Composed(long bones, IReadOnlyList<long> skins, IReadOnlyList<long> colors,
                                      IReadOnlyList<long> scales, Mounts.Look? mount = null,
                                      Cosmetics.PieceLook? appearanceMount = null)
            => WriteLook(Compose(bones, skins, colors, scales, mount, appearanceMount)).Build();

        internal static EntityLook Compose(long bones, IReadOnlyList<long> skins, IReadOnlyList<long> colors,
                                           IReadOnlyList<long> scales, Mounts.Look? mount = null,
                                           Cosmetics.PieceLook? appearanceMount = null)
        {
            bool riding = mount != null || (appearanceMount != null && appearanceMount.Bones != 0);
            var body = new EntityLook(Ints(colors), LookType, checked((int)(riding ? Mounts.RiderBones : bones)),
                Ints(scales), Ints(skins), []);
            if (!riding) return body;

            var root = mount ?? new Mounts.Look { Bones = appearanceMount!.Bones, Scale = appearanceMount.Scale };
            return Mounted(body, root, appearanceMount, null, colors);
        }

        /// <summary>A breed's colours as they travel, each with its index in the high byte.</summary>
        public static List<long> IndexedColors(int breedId, int sex) => ColorsFor(breedId, sex, null);

        /// <summary>
        /// The mounted character: the body drawn is the mount's and the rider goes inside.
        ///
        ///   f1: the mount's colours   f2: 3   f3: its bones   f5: [its scale]
        ///   f7 { f1: the rider's look, f4: where it attaches }
        ///
        /// It comes as is from a capture of equipping a dragoturkey with no cosmetic on.
        /// </summary>
        private static EntityLook Mounted(EntityLook rider, Mounts.Look mount,
                                          Cosmetics.PieceLook? cosmetico = null,
                                          IReadOnlyList<Wardrobe.Worn>? prendas = null,
                                          IReadOnlyList<long>? coloresDelPortador = null,
                                          int auraBones = 0)
        {
            long escala = (cosmetico != null && cosmetico.Scale > 0) ? cosmetico.Scale : mount.Scale;
            var root = new EntityLook(
                cosmetico?.Colors != null ? PackedColors(cosmetico.Colors) : Ints(mount.Colors),
                LookType, (cosmetico != null && cosmetico.Bones != 0) ? cosmetico.Bones : mount.Bones,
                escala > 0 ? [checked((int)escala)] : [],
                cosmetico != null && cosmetico.Skin > 0 ? [cosmetico.Skin] : [], []);

            // The pet goes before the rider, as in the capture, and so does the aura: it hangs off
            // the root, the mount, in all 73 mounted looks that carry one.
            if (prendas != null) root = AddPets(root, prendas, coloresDelPortador ?? new List<long>());
            root = AddAura(root, auraBones);
            return root with { SubEntities = [.. root.SubEntities,
                new SubEntityInformation(rider, 0, SubEntityBindingPointCategory.HookPointCategoryMountDriver)] };
        }

        /// <summary>
        /// The character's own look, without mount, pet or aura.
        /// </summary>
        /// <param name="quien">
        /// Whose body it is. It is needed for the SIZE: f5 is a multiplier and what the breed declares --
        /// between 43 and 55 depending on breed and sex -- is its hundred per cent, so the percentage
        /// <see cref="CharacterSize"/> stores is applied on top of that and does not replace the number.
        /// Zero means "nobody in particular" and then it is drawn at the breed's size.
        /// </param>
        private static EntityLook BuildBodyLook(int breedId, int sex, int headId,
                                               IReadOnlyList<long>? customColors, bool riding,
                                               IReadOnlyList<Wardrobe.Worn>? appearance = null,
                                               long quien = 0,
                                               IReadOnlyList<(int Slot, int Template)>? worn = null,
                                               int? bodyId = null)
        {
            var baseLook = Get(breedId, sex);
            var colors = ColorsFor(breedId, sex, customColors);
            // Mounted, the rider changes bones: the client has a RiderBones table and 2 is the normal
            // one. In the capture the same character is seen with bones 1 on foot and 2 on top of the
            // dragoturkey.
            var skins = SkinsWornBy(breedId, sex, headId, quien, appearance, worn, bodyId);
            return new EntityLook(Ints(colors), LookType, riding ? Mounts.RiderBones : (baseLook?.Bones ?? 1),
                baseLook != null ? Ints(CharacterSize.Applied(baseLook.Scales, quien)) : [], Ints(skins), []);
        }

        /// <summary>
        /// A saved outfit's look, the one its tile in the outfits list draws: its body, head,
        /// colours and garments over the character's real equipment, on foot.
        /// </summary>
        public static byte[] OutfitLook(DatabaseManager.DbCharacter character, int headId, int bodyId,
                                        IReadOnlyList<long> colors, IReadOnlyList<Wardrobe.Worn> garments)
        {
            var body = BuildBodyLook(character.Breed, character.Sex, headId, colors, riding: false,
                                     garments, character.Id, bodyId: bodyId);
            return WriteLook(AddPets(body, garments, ColorsFor(character.Breed, character.Sex, colors))).Build();
        }

        /// <summary>
        /// A build's two looks (CharacterPresets, the igs of the «Conjuntos» window): f1 the body
        /// with its equipment and nothing else, and f3 the same with the character's appearance
        /// garments over it -- the one the window draws.
        /// </summary>
        /// <remarks>
        /// MEASURED in «Conjuntos/crear nuevo conjunto…»: f1 carries no f2, and f3 carries the 3
        /// every look of ours carries; both on foot, without mount or pet. The skins of f1 are the
        /// breed's, the head and the build's equipment; those of f3 replace what a garment covers.
        /// </remarks>
        /// <summary>
        /// The character with nothing on, his body and head: the player card's f8, which in the
        /// capture carries only skins [110, 5312] beside the full look of its f14.
        /// </summary>
        public static byte[] BareLook(DatabaseManager.DbCharacter character)
            => WriteLook(BuildBodyLook(character.Breed, character.Sex, character.HeadId, SavedColors(character),
                              riding: false, appearance: null, character.Id,
                              worn: Array.Empty<(int Slot, int Template)>())).Build();

        public static (byte[] Equipment, byte[] Appearance) PresetLooks(DatabaseManager.DbCharacter character,
                                                                        IReadOnlyList<(int Slot, int Template)> worn)
        {
            var baseLook = Get(character.Breed, character.Sex);
            var colors = ColorsFor(character.Breed, character.Sex, SavedColors(character));

            var equipment = Pb.New();
            if (colors.Count > 0) equipment.Packed(1, colors);
            equipment.Var(3, baseLook?.Bones ?? 1);
            if (baseLook != null && baseLook.Scales.Count > 0)
                equipment.Packed(5, CharacterSize.Applied(baseLook.Scales, character.Id));
            var skins = SkinsWornBy(character.Breed, character.Sex, character.HeadId, character.Id, null, worn);
            if (skins.Count > 0) equipment.Packed(6, skins);

            var appearance = BuildBodyLook(character.Breed, character.Sex, character.HeadId, SavedColors(character),
                                           riding: false, Wardrobe.AppearanceOf(character.Id), character.Id, worn);
            return (equipment.Build(), WriteLook(appearance).Build());
        }

        /// <summary>
        /// Every skin a character has on: the breed's own, the head, the real equipment and the
        /// cosmetics over it, in the order the client reads them.
        /// </summary>
        /// <remarks>
        /// This used to live inside <see cref="BuildBodyLook"/>, and is out here because the look
        /// travels in two shapes and both need the same answer: the protobuf that goes to the game
        /// client, and the brace form that <see cref="Drawable"/> hands to the sprite reader.
        /// Composing the list twice is how the two drift apart.
        ///
        /// THE FIRST SKIN IS THE BODY. The sprite reader picks the humanoid rig with
        /// Breeds.Of(skins[0]), so the breed's own skin has to stay at the head of the list:
        /// anything put in front of it -- the chosen head, a hat -- leaves the rig unresolved and
        /// nothing is drawn.
        /// </remarks>
        private static List<long> SkinsWornBy(int breedId, int sex, int headId, long quien,
                                              IReadOnlyList<Wardrobe.Worn>? appearance,
                                              IReadOnlyList<(int Slot, int Template)>? worn = null,
                                              int? bodyId = null)
        {
            var baseLook = Get(breedId, sex);

            var skins = new List<long>();
            if (baseLook != null) skins.AddRange(baseLook.Skins);

            // The body picked in the appearance window takes the breed's own place, first.
            int bodySkin = bodyId is int picked ? BodyTable.SkinFor(picked, breedId, sex)
                         : quien > 0 ? BodyTable.SkinFor(Wardrobe.BodyOf(quien), breedId, sex) : 0;
            if (bodySkin > 0)
            {
                if (skins.Count > 0) skins[0] = bodySkin;
                else skins.Add(bodySkin);
            }

            int headSkin = HeadTable.SkinFor(headId, breedId, sex);
            if (headSkin > 0) skins.Add(headSkin);

            // Which equipment slots are COVERED by a cosmetic. In the real game the appearance garment
            // is not added to the real one: it replaces it -- it removes the cape's 3637 and puts the
            // cosmetic cape's 5044 --, so the one underneath cannot come out in f6.
            //
            // While the real piece's skin was not known this could not be done and both were added;
            // with equipment_skins.json it now can. The three slots are the three types that have a
            // skin: hat (6), cape (7) and shield (15).
            var tapados = new HashSet<int>();
            if (appearance != null)
            {
                foreach (var prenda in appearance)
                {
                    // With the eye closed it still covers: the slot shows nothing, real piece included.
                    if (prenda.Slot == Cosmetics.SlotHat) tapados.Add(SlotSombrero);
                    else if (prenda.Slot == Cosmetics.SlotCape) tapados.Add(SlotCapa);
                    else if (prenda.Slot == Cosmetics.SlotShield) tapados.Add(SlotEscudo);
                }
            }

            // The real equipment, the one that gives the characteristics. It comes from WornOf and not
            // from Equipment.All: that one reads the session, so it only knew about the character
            // playing and drew all the others -- those of the selection screen, the other players on
            // the map, the rival in a fight -- with nothing on.
            //
            // Only the skin of what is measured in equipment_skins.json is known; the rest does not dress.
            foreach (var (hueco, plantilla) in worn ?? Equipment.WornOf(quien))
            {
                if (tapados.Contains(hueco)) continue;   // a cosmetic covers it

                int piel = EquipmentSkins.SkinOf(plantilla);
                if (piel > 0 && !skins.Contains(piel)) skins.Add(piel);
            }

            // And the appearance garments, which go in place of the one just left out.
            if (appearance != null)
            {
                foreach (var prenda in appearance)
                {
                    // The mount rules the root and the pet hangs from it: neither touches the body's skins,
                    // and BuildLook takes care of both.
                    if (prenda.Slot == Cosmetics.SlotMount || prenda.Slot == Cosmetics.SlotPet) continue;

                    // With the eye closed the garment is still on but is not drawn.
                    if (prenda.Hidden) continue;

                    // The variant travels inside the uid the window composed (gid*1000+variant), and it is
                    // needed: a living item imitates one garment or another depending on which is chosen.
                    foreach (int piel in Cosmetics.SkinsOf(prenda.Gid, VarianteDe(prenda)))
                    {
                        if (piel > 0 && !skins.Contains(piel)) skins.Add(piel);
                    }
                }
            }

            return skins;
        }

        /// <summary>
        /// The same character, written the way the sprite reader wants it:
        /// <c>{bones|skins|colours|scale}</c>.
        /// </summary>
        /// <remarks>
        /// The look travels in two shapes and they are not interchangeable. The game client is
        /// sent the protobuf, which is what <see cref="BuildLook"/> builds and what the Look
        /// column stores; what the bone reader draws is this brace form. Handing one where the
        /// other is expected does not fail -- NpcLook.Parse calls it invalid and nothing is drawn,
        /// without a single error anywhere.
        ///
        /// The skins are the same list the game client gets, equipment and cosmetics included, so
        /// the portrait is the character as they look in the world.
        ///
        /// TWO THINGS THIS CANNOT SAY, both because the brace form is a single flat look with no
        /// sub-entities: the mount and the pet. A character who is riding is drawn here on foot.
        /// For a portrait that is the wanted answer anyway -- the face is what identifies the
        /// account, not the dragoturkey underneath.
        /// </remarks>
        public static string Drawable(DatabaseManager.DbCharacter character)
        {
            var baseLook = Get(character.Breed, character.Sex);
            if (baseLook == null) return "";

            var skins = SkinsWornBy(character.Breed, character.Sex, character.HeadId,
                                    character.Id, Wardrobe.AppearanceOf(character.Id));
            if (skins.Count == 0) return "";

            var colours = new List<string>();
            var plain = PlainColors(character.Breed, character.Sex, SavedColors(character));
            for (int i = 0; i < plain.Count; i++)
            {
                colours.Add($"{i + 1}=#{plain[i] & 0xFFFFFF:X6}");
            }

            // The real size, the same that goes to f5: what the breed declares with the character's
            // percentage already applied.
            long scale = 100;
            if (baseLook.Scales.Count > 0)
            {
                var applied = CharacterSize.Applied(baseLook.Scales, character.Id);
                if (applied.Count > 0) scale = applied[0];
            }

            return $"{{{baseLook.Bones}|{string.Join(",", skins)}|{string.Join(",", colours)}|{scale}}}";
        }

        /// <summary>
        /// Hangs the appearance pets from attachment 1 of the root.
        ///
        /// Forty-four of the measured ones send a colour identical byte for byte to the character's own:
        /// it is not a palette of their own, it is the tint of whoever carries them, so it is copied. And
        /// a missing scale is the default one, not zero.
        /// </summary>
        private static EntityLook AddPets(EntityLook raiz, IReadOnlyList<Wardrobe.Worn> prendas,
                                          IReadOnlyList<long> coloresDelPortador)
        {
            var children = new List<SubEntityInformation>(raiz.SubEntities);
            foreach (var prenda in prendas)
            {
                if (prenda.Slot != Cosmetics.SlotPet || prenda.Hidden) continue;

                var mascota = Cosmetics.PetOf(prenda.Gid);
                if (mascota == null) continue;

                var cuerpo = new EntityLook(mascota.ColorsFromWearer ? Ints(coloresDelPortador)
                    : mascota.Colors != null ? PackedColors(mascota.Colors) : [],
                    LookType, mascota.Bones, mascota.Scale > 0 ? [mascota.Scale] : [], [], []);
                children.Add(new SubEntityInformation(cuerpo, 0, SubEntityBindingPointCategory.HookPointCategoryPet));
            }
            return raiz with { SubEntities = children };
        }

        /// <summary>
        /// Hangs an aura from binding point 6 of the root: { f1 { f2: 3, f3: bones }, f4: 6 }, the
        /// shape of all 129 looks of the captures that carry one.
        /// </summary>
        private static EntityLook AddAura(EntityLook root, int bones)
        {
            if (bones <= 0) return root;
            return root with { SubEntities = [.. root.SubEntities,
                new SubEntityInformation(new EntityLook([], LookType, bones, [], [], []), 0,
                    SubEntityBindingPointCategory.HookPointCategoryBaseForeground)] };
        }

        private static int[] Ints(IReadOnlyList<long> values)
            => values.Select(value => checked((int)value)).ToArray();

        // Persisted/catalogue palettes are packed varints; this reads stored data, not a built packet.
        private static int[] PackedColors(byte[] bytes)
        {
            var colors = new List<int>();
            using var input = new Google.Protobuf.CodedInputStream(bytes);
            while (!input.IsAtEnd) colors.Add(input.ReadInt32());
            return colors.ToArray();
        }

        /// <summary>Compatibility serializer for gameplay paths still consuming look bytes.</summary>
        internal static Pb WriteLook(EntityLook look)
        {
            var pb = Pb.New();
            if (look.Colors.Count > 0) pb.Packed(1, look.Colors.Select(value => (long)value).ToArray());
            pb.Var(2, look.LookType).VarIfNotZero(3, look.Bones);
            if (look.Scales.Count > 0) pb.Packed(5, look.Scales.Select(value => (long)value).ToArray());
            if (look.Skins.Count > 0) pb.Packed(6, look.Skins.Select(value => (long)value).ToArray());
            foreach (var child in look.SubEntities)
            {
                int category = child.BindingPointCategory switch
                {
                    SubEntityBindingPointCategory.HookPointCategoryPet => 1,
                    SubEntityBindingPointCategory.HookPointCategoryMountDriver => 2,
                    SubEntityBindingPointCategory.HookPointCategoryBaseForeground => 6,
                    _ => throw new InvalidOperationException("Unsupported composed appearance attachment."),
                };
                pb.Msg(7, Pb.New().Msg(1, WriteLook(child.SubEntityLook))
                    .VarIfNotZero(3, child.BindingPointIndex).Var(4, category));
            }
            return pb;
        }

        /// <summary>The colours the character wears, already indexed as they go on the wire.</summary>
        private static List<long> ColorsFor(int breedId, int sex, IReadOnlyList<long>? customColors)
            => (customColors != null && customColors.Count > 0)
                ? new List<long>(customColors)
                : IndexColors(Get(breedId, sex)?.Colors);

        /// <summary>
        /// The same colours but WITHOUT the index, which is how the wardrobe wants them.
        ///
        /// In the look each colour travels with its slot in the high byte (0x01e1b99d, 0x02b4a1bb...); in a
        /// saved set they go bare (0xe1b99d, 0xb4a1bb...). They are the same six and in the same order --
        /// checked on the capture's lyt --, but if they are sent indexed the client does not build its
        /// ColorSet and the cosmetics window falls over on opening.
        /// </summary>
        public static List<long> PlainColors(int breedId, int sex, IReadOnlyList<long>? customColors)
        {
            var fuente = (customColors != null && customColors.Count > 0)
                ? customColors
                : (IReadOnlyList<long>?)Get(breedId, sex)?.Colors;

            var salida = new List<long>();
            if (fuente == null) return salida;
            foreach (long color in fuente) salida.Add(color & 0xFFFFFF);
            return salida;
        }

        /// <summary>
        /// The variant chosen for a garment. The appearance window does not send an inventory uid but the
        /// template number, so AppearanceHandler composes one with the variant inside (gid*1000+variant);
        /// here it is undone.
        /// </summary>
        private static int VarianteDe(Wardrobe.Worn prenda)
        {
            long esperado = prenda.Gid * 1000L;
            long resto = prenda.Uid - esperado;
            return (resto >= 0 && resto < 1000) ? (int)resto : 0;
        }

        /// <summary>
        /// The client's colors come as bare rgb; on the wire they travel indexed, with the slot
        /// number (1..6) in the high byte.
        /// </summary>
        private static List<long> IndexColors(List<long>? rgb)
        {
            var indexed = new List<long>();
            if (rgb == null) return indexed;
            for (int i = 0; i < rgb.Count; i++)
            {
                indexed.Add(((long)(i + 1) << 24) | (rgb[i] & 0xFFFFFF));
            }
            return indexed;
        }
    }
}
