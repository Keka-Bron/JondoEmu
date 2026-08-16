using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using Jondo.Unity.Launcher.Network;

namespace Jondo.Unity.Launcher.Managers
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
    /// database: that one was captured from an earlier version of the protocol, with a different
    /// field numbering, and cannot be trusted.
    /// </summary>
    public static class BreedLookTable
    {
        /// <summary>Constant value of field 2 of the look block in every 3.6.10.10 capture.</summary>
        private const int LookType = 3;

        public sealed class BreedLook
        {
            public int Bones { get; set; } = 1;
            public List<long> Skins { get; set; } = new List<long>();
            public List<long> Scales { get; set; } = new List<long>();
            public List<long> Colors { get; set; } = new List<long>();
        }

        private static readonly Dictionary<int, Dictionary<int, BreedLook>> _byBreed
            = new Dictionary<int, Dictionary<int, BreedLook>>();
        private static bool _loaded;
        private static readonly object _lock = new object();

        /// <summary>Lazy loading: the first query reads the file.</summary>
        private static void EnsureLoaded()
        {
            if (_loaded) return;
            lock (_lock)
            {
                if (_loaded) return;
                _loaded = true;
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

        /// <summary>
        /// Assembles a character's look block. If custom colors are passed those are used;
        /// otherwise, the six defaults of the breed.
        ///
        /// The skins go out in the order seen in the capture: first the breed's, then the head.
        /// A character created in the real game came back as skins [110, 2172] — base and head —
        /// and without the second one the client draws it with no face.
        /// </summary>
        /// <param name="paraLaVentana">
        /// Cierto solo para la vista previa del panel de apariencias. Cambia una cosa: montado, la
        /// MASCOTA de apariencia no sale al mundo —no se puede llevar montura y mascota a la vez,
        /// porque comparten hueco de equipo— pero el panel sí la enseña, para que se vea lo que se
        /// ha elegido. Está medido: en las capturas hay 49 kmb del personaje montado y ninguno
        /// lleva mascota, mientras que 502 lxc montados sí la llevan. A pie sale en los dos.
        /// </param>
        public static byte[] BuildLook(int breedId, int sex, int headId = 0,
                                       IReadOnlyList<long>? customColors = null,
                                       long characterId = 0,
                                       bool paraLaVentana = false)
        {
            // Con id se pregunta por ese personaje, que es lo que hace falta en la pantalla de
            // selección; sin él, por el que está jugando.
            var mount = characterId != 0 ? Mounts.RiddenBy(characterId) : Mounts.Ridden();

            long quien = characterId != 0 ? characterId : Jondo.Unity.Launcher.Network.SessionContext.State.CharacterId;
            var prendas = Wardrobe.AppearanceOf(quien);

            // Montado, la raíz es la montura; una MASCOTURA o una MONTURA DE APARIENCIA mandan
            // sobre esa raíz, que es lo que hace que el dragopavo se vea como otra cosa. Las dos
            // van al mismo hueco y las dos pueden traer huesos, escala, color y —solo las de
            // apariencia— una piel propia.
            Cosmetics.PieceLook? impuesto = null;
            foreach (var prenda in prendas)
            {
                if (prenda.Slot != Cosmetics.SlotMount || prenda.Hidden) continue;
                var suyo = Cosmetics.MountLookOf(prenda.Gid);
                if (suyo != null) impuesto = suyo;
            }

            // Una mascotura de verdad ocupa el hueco 8 y se monta, pero su aspecto no está en
            // mounts.json, que solo trae dragopavos, mulaguas y vuelocerontes. Así que se dibuja
            // montado solo si hay de dónde sacar los huesos: los de la montura, o los de la prenda
            // de apariencia. Sin ninguno de los dos, mejor a pie que un esqueleto vacío.
            int huesosRaiz = (impuesto != null && impuesto.Bones != 0) ? impuesto.Bones
                                                                      : (mount?.Bones ?? 0);
            bool montado = mount != null && huesosRaiz != 0;

            var colores = ColorsFor(breedId, sex, customColors);
            var cuerpo = BuildBodyLook(breedId, sex, headId, customColors, montado, prendas);

            // A pie la raíz es el propio personaje, así que la mascota se le cuelga a él.
            if (!montado)
            {
                AddPets(cuerpo, prendas, colores);
                return cuerpo.Build();
            }

            return Mounted(cuerpo.Build(), mount!, impuesto,
                           paraLaVentana ? prendas : null, colores);
        }

        /// <summary>
        /// El personaje montado: el cuerpo que se dibuja es el de la montura y el jinete va dentro.
        ///
        ///   f1: los colores de la montura   f2: 3   f3: sus huesos   f5: [su escala]
        ///   f7 { f1: el aspecto del jinete, f4: dónde se engancha }
        ///
        /// Sale tal cual de una captura de equipar un dragopavo sin ningún cosmético puesto.
        /// </summary>
        private static byte[] Mounted(byte[] rider, Mounts.Look mount,
                                      Cosmetics.PieceLook? cosmetico = null,
                                      IReadOnlyList<Wardrobe.Worn>? prendas = null,
                                      IReadOnlyList<long>? coloresDelPortador = null)
        {
            var pb = Pb.New();

            if (cosmetico?.Colors != null) pb.Bytes(1, cosmetico.Colors);
            else if (mount.Colors.Count > 0) pb.Packed(1, mount.Colors);

            pb.Var(2, LookType);
            pb.Var(3, (cosmetico != null && cosmetico.Bones != 0) ? cosmetico.Bones : mount.Bones);

            long escala = (cosmetico != null && cosmetico.Scale > 0) ? cosmetico.Scale : mount.Scale;
            if (escala > 0) pb.Packed(5, new long[] { escala });

            // Solo las monturas de apariencia traen piel propia; las mascoturas no tocan el f6.
            if (cosmetico != null && cosmetico.Skin > 0) pb.Packed(6, new long[] { cosmetico.Skin });

            // La mascota va antes que el jinete, como en la captura.
            if (prendas != null) AddPets(pb, prendas, coloresDelPortador ?? new List<long>());

            pb.Msg(7, Pb.New()
                .Bytes(1, rider)
                .Var(4, Mounts.RiderBindingPoint));

            return pb.Build();
        }

        /// <summary>
        /// El aspecto del propio personaje, sin montura ni mascota. Devuelve el constructor a medio
        /// hacer y no los bytes, porque a pie hay que colgarle todavía la mascota.
        /// </summary>
        private static Pb BuildBodyLook(int breedId, int sex, int headId,
                                        IReadOnlyList<long>? customColors, bool riding,
                                        IReadOnlyList<Wardrobe.Worn>? appearance = null)
        {
            var baseLook = Get(breedId, sex);
            var pb = Pb.New();

            var colors = ColorsFor(breedId, sex, customColors);

            if (colors.Count > 0) pb.Packed(1, colors);
            pb.Var(2, LookType);
            // Montado, el jinete cambia de huesos: el cliente tiene una tabla RiderBones y el 2 es
            // el normal. En la captura se ve el mismo personaje con huesos 1 a pie y 2 encima del
            // dragopavo.
            pb.Var(3, riding ? Mounts.RiderBones : (baseLook?.Bones ?? 1));
            if (baseLook != null && baseLook.Scales.Count > 0) pb.Packed(5, baseLook.Scales);

            var skins = new List<long>();
            if (baseLook != null) skins.AddRange(baseLook.Skins);

            int headSkin = HeadTable.SkinFor(headId, breedId, sex);
            if (headSkin > 0) skins.Add(headSkin);

            // Y las prendas de apariencia. En el juego real SUSTITUYEN a la piel de la prenda de
            // verdad —quitan la 3637 de la capa y ponen la 5044 de la capa cosmética—; aquí se
            // añaden, porque la de la prenda de verdad nunca la hemos sabido. El resultado que se
            // ve es el mismo mientras no se lleve nada debajo.
            if (appearance != null)
            {
                foreach (var prenda in appearance)
                {
                    // La montura manda en la raíz y la mascota cuelga de ella: ninguna de las dos
                    // toca las pieles del cuerpo, y de las dos se encarga BuildLook.
                    if (prenda.Slot == Cosmetics.SlotMount || prenda.Slot == Cosmetics.SlotPet) continue;

                    // Con el ojo cerrado la prenda sigue puesta pero no se dibuja.
                    if (prenda.Hidden) continue;

                    // La variante viaja dentro del uid que compuso la ventana (gid*1000+variante),
                    // y hace falta: un objeto viviente imita una prenda u otra según cuál se elija.
                    foreach (int piel in Cosmetics.SkinsOf(prenda.Gid, VarianteDe(prenda)))
                    {
                        if (piel > 0 && !skins.Contains(piel)) skins.Add(piel);
                    }
                }
            }

            if (skins.Count > 0) pb.Packed(6, skins);

            return pb;
        }

        /// <summary>
        /// Cuelga las mascotas de apariencia del enganche 1 de la raíz.
        ///
        /// Cuarenta y cuatro de las medidas mandan un color idéntico byte a byte al del propio
        /// personaje: no es una paleta suya, es el tinte de quien la lleva, así que se copia. Y la
        /// escala ausente es la de por defecto, no cero.
        /// </summary>
        private static void AddPets(Pb raiz, IReadOnlyList<Wardrobe.Worn> prendas,
                                    IReadOnlyList<long> coloresDelPortador)
        {
            foreach (var prenda in prendas)
            {
                if (prenda.Slot != Cosmetics.SlotPet || prenda.Hidden) continue;

                var mascota = Cosmetics.PetOf(prenda.Gid);
                if (mascota == null) continue;

                var cuerpo = Pb.New();
                if (mascota.ColorsFromWearer)
                {
                    if (coloresDelPortador.Count > 0) cuerpo.Packed(1, coloresDelPortador);
                }
                else if (mascota.Colors != null) cuerpo.Bytes(1, mascota.Colors);

                cuerpo.Var(2, LookType).Var(3, mascota.Bones);
                if (mascota.Scale > 0) cuerpo.Packed(5, new long[] { mascota.Scale });

                raiz.Msg(7, Pb.New().Bytes(1, cuerpo.Build()).Var(4, PetBindingPoint));
            }
        }

        /// <summary>Los colores que lleva el personaje, ya indexados como van por el cable.</summary>
        private static List<long> ColorsFor(int breedId, int sex, IReadOnlyList<long>? customColors)
            => (customColors != null && customColors.Count > 0)
                ? new List<long>(customColors)
                : IndexColors(Get(breedId, sex)?.Colors);

        /// <summary>
        /// Los mismos colores pero SIN el índice, que es como los quiere el vestuario.
        ///
        /// En el aspecto cada color viaja con su hueco en el byte alto (0x01e1b99d, 0x02b4a1bb...);
        /// en un conjunto guardado van pelados (0xe1b99d, 0xb4a1bb...). Son los mismos seis y en el
        /// mismo orden —comprobado sobre el lyt de la captura—, pero si se mandan indexados el
        /// cliente no construye su ColorSet y la ventana de cosméticos se cae al abrirse.
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

        /// <summary>Dónde se enganchan las mascotas. El 2 es el jinete y el 6 el aura.</summary>
        private const int PetBindingPoint = 1;

        /// <summary>
        /// La variante que se eligió para una prenda. La ventana de apariencias no manda uid de
        /// inventario sino el número de plantilla, así que AppearanceHandler compone uno con la
        /// variante dentro (gid*1000+variante); aquí se deshace.
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
