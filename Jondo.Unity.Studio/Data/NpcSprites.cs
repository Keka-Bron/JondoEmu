using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using AssetsTools.NET;
using AssetsTools.NET.Extra;
using AssetsTools.NET.Texture;
using Avalonia;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Jondo.Unity.Launcher;

namespace Jondo.Unity.Studio.Data
{
    /// <summary>
    /// Draws an NPC, out of the client's own bones, as a still picture.
    /// </summary>
    /// <remarks>
    /// I said this could not be done without an animation player. That was wrong, and the reason it
    /// was wrong is worth writing down: <b>the still frame is the whole animation.</b> An
    /// <c>AnimStatique_&lt;direction&gt;</c> holds exactly one frame in 47 of 53 bones measured, so
    /// reading frame 0 is not a shortcut past a timeline — there is no timeline.
    ///
    /// The path, all of it inside the client:
    ///
    /// <code>
    ///   look "{58|||90}"  →  Characters/Bones/bones_assets_bone_58.bundle
    ///     AnimatedObjectDefinition   the rig: animations[], graphics[], boneAsset
    ///     animations["AnimStatique_1"].dataBytes    a frame table, little-endian
    ///     frame 0 records            node, flags, symbol, and a 2×3 matrix
    ///     graphics[symbol].part.skinChunks  →  SkinAsset vertices: pos and uv
    ///     Texture2D atlas, 256×8192, DXT5Crunched
    /// </code>
    ///
    /// A record's matrix is where that piece goes, and it carries rotation and mirroring — bone
    /// 3280's first matrix is a mirrored 2.5× scale — so this rasterises triangles rather than
    /// blitting rectangles.
    ///
    /// <b>Bone 1 is the humanoid rig</b>, and it is assembled differently: it has almost no body of
    /// its own, and its frame-0 records carry a NEGATIVE symbol meaning "a skin supplies this". The
    /// slot is <c>exposedNodeNames[record.name]</c> — "Tete_1", "Cape_1" — and it is looked up by
    /// name in the skin bundles the look names, in the order the look names them. Which skin file
    /// to pair with which rig comes from the breed table in
    /// <c>Data/data_assets_bodiesdataroot.asset.bundle</c>.
    ///
    /// <b>The bodies are coloured by the look, not by the atlas.</b> A skin part is not
    /// necessarily a piece of geometry: <c>Tete_1</c> in skin 30 has <em>zero</em> direct chunks
    /// and sixteen references, and following them is what makes a head appear at all. Along the
    /// way the referenced names carry the tint — 286 of skin 30's 629 symbols are called
    /// <c>ColorGray_&lt;n&gt;_something</c> — where <c>n</c> is an index into the look's colour
    /// list. The artwork under those is deliberately grey so that one drawing can be every hair
    /// colour in the game, and multiplying it by the look's colour is the whole of the trick.
    ///
    /// Skip the walk and you get a headless grey mannequin wearing a correct hat, which is exactly
    /// what this looked like before.
    ///
    /// Nothing is baked to disk. Pictures are cached in memory by look string, and NPCs share looks
    /// heavily, so the cost disappears after the first of each.
    /// </remarks>
    public sealed class NpcSprites : IDisposable
    {
        /// <summary>How tall the finished picture is, in pixels. Width follows the drawing.</summary>
        private const int Tall = 96;

        /// <summary>An animation named exactly this and nothing else.</summary>
        /// <remarks>
        /// Anchored on purpose. A plain prefix match sorts
        /// <c>AnimStatiqueCombat0_to_AnimStatiqueCombat3a_1</c> first and you get a fighting stance
        /// where you wanted somebody standing still.
        /// </remarks>
        private static readonly Regex Standing = new Regex(@"^AnimStatique_(\d+)$", RegexOptions.Compiled);

        private readonly Dictionary<string, Bitmap?> _drawn = new Dictionary<string, Bitmap?>();

        public int Rendered { get; private set; }

        public int Failed { get; private set; }

        /// <summary>What went wrong on the last one that did. For the overview screen.</summary>
        public string Trouble { get; private set; } = "";

        /// <summary>What the last drawing was made of. For the self test, and for finding out why.</summary>
        public string LastMakeup { get; private set; } = "";

        private int _fromBone;
        private int _fromSkin;
        private int _followed;
        private int _tinted;

        /// <summary>
        /// Why the ones that did not draw did not draw, counted by reason.
        /// </summary>
        /// <remarks>
        /// Counted rather than logged because this is a chain of six steps that each fail quietly,
        /// and "43 of 60" on its own tells you nothing about which link went. A histogram tells you
        /// whether to go and fix a parser or to accept that some bones do not ship a still frame.
        /// </remarks>
        public readonly Dictionary<string, int> Why = new Dictionary<string, int>(StringComparer.Ordinal);

        private void Blame(string reason)
        {
            Why.TryGetValue(reason, out int already);
            Why[reason] = already + 1;
        }

        /// <summary>The reasons, as one line, most common first.</summary>
        public string Reasons()
        {
            var parts = new List<string>();
            foreach (var pair in Why) parts.Add($"{pair.Value} {pair.Key}");
            parts.Sort((a, b) => string.CompareOrdinal(b, a));
            return string.Join(", ", parts);
        }

        /// <summary>The picture for a look string, or null when this build cannot draw it.</summary>
        public Bitmap? Of(string? lookString)
        {
            string key = (lookString ?? "").Trim();
            if (key.Length == 0) return null;

            if (_drawn.TryGetValue(key, out var already)) return already;

            Bitmap? picture = null;
            try
            {
                picture = Draw(NpcLook.Parse(key));
                if (picture != null) Rendered++;
                else Failed++;
            }
            catch (Exception ex)
            {
                Failed++;
                Trouble = $"{key}: {ex.GetType().Name}: {ex.Message}";
            }

            _drawn[key] = picture;
            return picture;
        }

        /// <summary>Whether a look is one this build knows how to draw, without drawing it.</summary>
        public static bool CanDraw(string? lookString)
        {
            var look = NpcLook.Parse(lookString);
            if (!look.Valid) return false;

            return look.Humanoid
                ? HumanoidBundleFor(look).Length > 0
                : File.Exists(BundleFor(look.Bone));
        }

        private static string BundleFor(int bone)
            => Path.Combine(Paths.ClientContentDir, "Characters", "Bones", $"bones_assets_bone_{bone}.bundle");

        /// <summary>A skin bundle: its named parts and the sheet they are cut from.</summary>
        private sealed record Wardrobe(Dictionary<string, AssetTypeValueField> Parts,
                                       AssetTypeValueField Mesh, Sheet Atlas);

        private Bitmap? Draw(NpcLook look)
        {
            if (!look.Valid) { Blame("no look"); return null; }

            string path = look.Humanoid ? HumanoidBundleFor(look) : BundleFor(look.Bone);
            if (path.Length == 0 || !File.Exists(path)) { Blame("no bone bundle"); return null; }

            var manager = new AssetsManager();
            try
            {
                var assets = manager.LoadAssetsFileFromBundle(manager.LoadBundleFile(path, true), 0, false);

                AssetTypeValueField? rig = null;
                var meshes = new Dictionary<long, AssetTypeValueField>();

                foreach (var info in assets.file.GetAssetsOfType(AssetClassID.MonoBehaviour))
                {
                    var field = manager.GetBaseField(assets, info);
                    if (field == null) continue;

                    // The rig is the one with a node budget; the meshes are the ones with vertices.
                    // A bundle can hold up to sixteen meshes, so "take the first" is wrong.
                    if (!field["maxNodeCount"].IsDummy) rig = field;
                    else if (!field["vertices"].IsDummy) meshes[info.PathId] = field;
                }

                if (rig == null || meshes.Count == 0) { Blame("no rig or no mesh"); return null; }

                byte[]? frames = StandingFrames(rig);
                if (frames == null) { Blame("no standing animation"); return null; }

                var records = Frame0(frames);
                if (records.Count == 0) { Blame("frame 0 unreadable"); return null; }

                _fromBone = _fromSkin = _followed = _tinted = 0;
                Following = coloured => { _followed++; if (coloured) _tinted++; };

                var atlas = Atlas(manager, assets);
                if (atlas == null) { Blame("atlas would not decode"); return null; }

                // The skins, in the order the look names them: first one that owns the slot wins.
                var wardrobe = new List<Wardrobe>();
                foreach (int skin in look.Skins)
                {
                    var dressed = Dress(skin);
                    if (dressed != null) wardrobe.Add(dressed);
                }

                var slots = rig["exposedNodeNames.Array"];
                var graphics = rig["graphics.Array"];
                var triangles = new List<Piece>();

                foreach (var piece in records)
                {
                    if (piece.Symbol >= 0)
                    {
                        if (piece.Symbol >= graphics.Children.Count) continue;

                        var entry = graphics.Children[piece.Symbol];
                        long which = entry["asset"]["m_PathID"].AsLong;
                        if (!meshes.TryGetValue(which, out var mesh)) continue;

                        int was = triangles.Count;
                        Walk(triangles, mesh, entry["part"], piece, atlas, wardrobe, Untinted, look, 0);
                        _fromBone += triangles.Count - was;
                        continue;
                    }

                    // Below minus one: a skin supplies this piece, and the slot is named.
                    if (piece.Name < 0 || piece.Name >= slots.Children.Count) continue;

                    string slot = slots.Children[piece.Name].AsString ?? "";
                    if (slot.Length == 0) continue;

                    foreach (var dressed in wardrobe)
                    {
                        if (!dressed.Parts.TryGetValue(slot, out var part)) continue;

                        int had = triangles.Count;
                        Walk(triangles, dressed.Mesh, part, piece, dressed.Atlas, wardrobe,
                             TintFor(slot, look), look, 0);
                        _fromSkin += triangles.Count - had;
                        break;
                    }
                }

                Following = null;
                LastMakeup = $"{triangles.Count} tris ({_fromBone} bone, {_fromSkin} skin), " +
                             $"{_followed} refs followed, {_tinted} tinted";

                var drawing = Rasterise(triangles);
                if (drawing == null) Blame("nothing to draw");
                return drawing;
            }
            finally
            {
                manager.UnloadAll();
            }
        }

        /// <summary>One skin bundle, opened and indexed by part name.</summary>
        private Wardrobe? Dress(int skin)
        {
            string path = Path.Combine(Paths.ClientContentDir, "Characters", "Skins",
                                       $"skins_assets_skin_{skin}.bundle");
            if (!File.Exists(path)) return null;

            var manager = new AssetsManager();
            try
            {
                var assets = manager.LoadAssetsFileFromBundle(manager.LoadBundleFile(path, true), 0, false);

                AssetTypeValueField? mesh = null;
                foreach (var info in assets.file.GetAssetsOfType(AssetClassID.MonoBehaviour))
                {
                    var field = manager.GetBaseField(assets, info);
                    if (field == null || field["vertices"].IsDummy) continue;

                    mesh = field;
                    break;
                }

                if (mesh == null) return null;

                var atlas = Atlas(manager, assets);
                if (atlas == null) return null;

                // In a skin bundle the symbols have names — "Tete_1", "Cape_1" — which is how a
                // slot on the rig finds the piece that fills it. In a bone bundle they are numbers.
                var parts = new Dictionary<string, AssetTypeValueField>(StringComparer.Ordinal);
                var keys = mesh["m_keys.Array"];
                var values = mesh["m_values.Array"];

                for (int i = 0; i < keys.Children.Count && i < values.Children.Count; i++)
                {
                    string name = keys.Children[i].AsString ?? "";
                    if (name.Length > 0) parts[name] = values.Children[i];
                }

                return new Wardrobe(parts, mesh, atlas);
            }
            catch (Exception)
            {
                return null;
            }
            finally
            {
                manager.UnloadAll();
            }
        }

        /// <summary>
        /// Which humanoid rig goes with this look.
        /// </summary>
        /// <remarks>
        /// The humanoid bones are per breed — <c>bones_assets_bone_1-&lt;breed&gt;-static.bundle</c>,
        /// nineteen of them — and what picks one is the look's <em>first skin</em>, through the body
        /// table in <c>Data/data_assets_bodiesdataroot.asset.bundle</c>.
        ///
        /// When that table cannot be read the fallback is to try the rigs in order and keep the
        /// first that exists, which gets a body of the wrong breed rather than no body at all. That
        /// is the right way round for an editor: a Sram standing in for an Iop is obvious on sight
        /// and an empty cell is not.
        /// </remarks>
        private static string HumanoidBundleFor(NpcLook look)
        {
            string folder = Path.Combine(Paths.ClientContentDir, "Characters", "Bones");

            int breed = look.Skins.Count > 0 ? Breeds.Of(look.Skins[0]) : 0;
            if (breed > 0)
            {
                string exact = Path.Combine(folder, $"bones_assets_bone_1-{breed}-static.bundle");
                if (File.Exists(exact)) return exact;
            }

            for (int guess = 1; guess <= 20; guess++)
            {
                string candidate = Path.Combine(folder, $"bones_assets_bone_1-{guess}-static.bundle");
                if (File.Exists(candidate)) return candidate;
            }

            return "";
        }

        /// <summary>
        /// Walks one part's display list, emitting triangles and carrying the tint down.
        /// </summary>
        /// <remarks>
        /// The list is a preorder tree flattened into an array, and each entry says which:
        ///
        /// <code>
        ///   entries == -1   a leaf: it consumes the next skinChunk, in order
        ///   entries == 0    a reference: referencedSymbols[symbolId] names another part
        ///   entries == N    a group: the next N entries are its subtree
        /// </code>
        ///
        /// The entries carry transforms too, and they are deliberately ignored: they are the bind
        /// pose, not a placement, and composing them double-transforms every piece. What places the
        /// part is the animation record's matrix, which is already in <paramref name="piece"/>.
        ///
        /// A part with no display list at all is the common case in bone bundles — every symbol is
        /// one quad — and its chunks are simply drawn.
        /// </remarks>
        /// <summary>Counts a reference being followed, and whether it carried a colour.</summary>
        private static Action<bool>? Following;

        private static void Walk(List<Piece> into, AssetTypeValueField mesh, AssetTypeValueField part,
                                 Placed piece, Sheet from, List<Wardrobe> wardrobe, int tint,
                                 NpcLook look, int depth)
        {
            var entries = part["DisplayListEntry.Array"];
            var chunks = part["skinChunks.Array"];

            if (entries.IsDummy || entries.Children.Count == 0)
            {
                foreach (var chunk in chunks) Emit(into, mesh, chunk, piece, from, tint);
                return;
            }

            var names = mesh["referencedSymbols.Array"];
            int cursor = 0;

            // The chunk cursor belongs to the whole part, not to a level of the tree: leaves
            // consume chunks in the order they are met, however deep down they are.
            int Step(int at, int inherited)
            {
                var entry = entries.Children[at];
                int count = entry["entries"].AsInt;
                int symbolId = entry["symbolId"].AsInt;

                if (count < 0)
                {
                    if (cursor < chunks.Children.Count)
                    {
                        Emit(into, mesh, chunks.Children[cursor], piece, from, inherited);
                        cursor++;
                    }

                    return at + 1;
                }

                // Anything that is not a leaf is a NAMED group, and the name is where the colour
                // comes from:
                //
                //     symbolId=2  entries=2   Abdomen_1
                //     symbolId=3  entries=1   ColorGray_3_tronc_1
                //     symbolId=-1 entries=-1              <- the geometry, coloured by 3
                //
                // This is the part read wrong the first time: entries > 0 was taken for an
                // anonymous container and the name only looked at when entries was zero. Of the
                // hundreds of ColorGray groups in a body, exactly five were ever seen — the few
                // that happen to be empty — so the body stayed grey while everything else came out
                // right, which is the worst way for it to be wrong.
                string name = symbolId >= 0 && symbolId < names.Children.Count
                    ? names.Children[symbolId].AsString ?? ""
                    : "";

                int here = TintFor(name, look);
                if (here == Untinted) here = inherited;

                if (count == 0)
                {
                    // An empty group is a reference to a symbol that lives somewhere else. The head
                    // in skin 30 is seventeen of these and no geometry of its own at all.
                    if (depth >= MaxReferenceDepth || name.Length == 0) return at + 1;

                    Following?.Invoke(here != Untinted);

                    // This skin first and then the others: a reference can cross from one skin to
                    // another, which is how a hat knows about the head it is sitting on.
                    foreach (var dressed in Wardrobes(mesh, wardrobe))
                    {
                        if (!dressed.Parts.TryGetValue(name, out var referenced)) continue;

                        Walk(into, dressed.Mesh, referenced, piece, dressed.Atlas, wardrobe,
                             here, look, depth + 1);
                        break;
                    }

                    return at + 1;
                }

                if (here != inherited) Following?.Invoke(true);

                int end = Math.Min(at + 1 + count, entries.Children.Count);
                int next = at + 1;
                while (next < end) next = Step(next, here);
                return end;
            }

            int i = 0;
            while (i < entries.Children.Count) i = Step(i, tint);
        }

        /// <summary>This part's own skin first, then the rest of what the look is wearing.</summary>
        private static IEnumerable<Wardrobe> Wardrobes(AssetTypeValueField mesh, List<Wardrobe> wardrobe)
        {
            foreach (var dressed in wardrobe)
            {
                if (ReferenceEquals(dressed.Mesh, mesh)) yield return dressed;
            }

            foreach (var dressed in wardrobe)
            {
                if (!ReferenceEquals(dressed.Mesh, mesh)) yield return dressed;
            }
        }

        /// <summary>
        /// The colour a symbol asks the look for, out of its name.
        /// </summary>
        /// <remarks>
        /// <c>ColorGray_3_TorseAbdomen_0</c> means "paint this with the look's colour 3". The
        /// artwork underneath is grey on purpose so that one drawing serves every colour in the
        /// game, which is also why skipping this leaves a grey mannequin rather than nothing.
        /// </remarks>
        private static int TintFor(string symbol, NpcLook look)
        {
            const string Marker = "ColorGray_";
            if (!symbol.StartsWith(Marker, StringComparison.Ordinal)) return Untinted;

            int end = symbol.IndexOf('_', Marker.Length);
            if (end <= Marker.Length) return Untinted;

            if (!int.TryParse(symbol[Marker.Length..end], out int index)) return Untinted;
            return look.Colours.TryGetValue(index, out int rgb) ? rgb : Untinted;
        }

        private static void Emit(List<Piece> into, AssetTypeValueField mesh, AssetTypeValueField chunk,
                                 Placed piece, Sheet from, int tint)
        {
            var vertices = mesh["vertices.Array"];
            var indices = mesh["triangles.Array"];

            {
                int firstVertex = chunk["startVertexIndex"].AsInt;
                int firstIndex = chunk["startIndexIndex"].AsInt;
                int count = chunk["indexCount"].AsInt;

                for (int i = 0; i + 2 < count; i += 3)
                {
                    var corners = new Corner[3];
                    bool ok = true;

                    for (int c = 0; c < 3; c++)
                    {
                        int slot = firstIndex + i + c;
                        if (slot < 0 || slot >= indices.Children.Count) { ok = false; break; }

                        // Chunk-local, not global. This is the one place it is easy to be wrong and
                        // get a plausible-looking mess instead of an obvious one.
                        int vertex = firstVertex + indices.Children[slot].AsInt;
                        if (vertex < 0 || vertex >= vertices.Children.Count) { ok = false; break; }

                        var v = vertices.Children[vertex];
                        float x = v["pos"]["x"].AsFloat;
                        float y = v["pos"]["y"].AsFloat;

                        corners[c] = new Corner(
                            piece.A * x + piece.C * y + piece.Tx,
                            piece.B * x + piece.D * y + piece.Ty,
                            v["uv"]["x"].AsFloat,
                            v["uv"]["y"].AsFloat);
                    }

                    if (ok) into.Add(new Piece(corners[0], corners[1], corners[2], from, tint));
                }
            }
        }

        /// <summary>
        /// The bytes of the lowest-numbered standing animation.
        /// </summary>
        /// <remarks>
        /// Read from <c>dataBytes</c> on the rig rather than from the TextAssets, which carry the
        /// same blob: three of sixty real NPC bones ship no TextAsset at all and still animate.
        ///
        /// Only five of the eight directions are ever authored, and 26 of 53 bones ship exactly
        /// one. There is no eight-way sprite set in this data to build a rotation control on.
        /// </remarks>
        private static byte[]? StandingFrames(AssetTypeValueField rig)
        {
            byte[]? standing = null;
            byte[]? nearly = null;
            byte[]? anything = null;
            int lowest = int.MaxValue;

            foreach (var animation in rig["animations.Array"])
            {
                var bytes = animation["dataBytes.Array"];
                if (bytes.IsDummy) continue;

                byte[] raw = bytes.AsByteArray;
                if (raw.Length == 0) continue;

                anything ??= raw;

                string name = animation["name"].AsString ?? "";
                var match = Standing.Match(name);

                if (match.Success && int.TryParse(match.Groups[1].Value, out int direction))
                {
                    if (direction >= lowest) continue;
                    lowest = direction;
                    standing = raw;
                    continue;
                }

                // A ladder, not a preference. Six of sixty bones ship no plain AnimStatique at all,
                // and for those a fighting stance is a great deal better than an empty cell — but
                // it has to be reached for second, or the transition animations sort first and
                // everybody comes out braced for a fight.
                if (nearly == null && name.StartsWith("AnimStatique", StringComparison.Ordinal)) nearly = raw;
            }

            return standing ?? nearly ?? anything;
        }

        /// <summary>
        /// One piece of the drawing, placed.
        /// </summary>
        /// <remarks>
        /// <paramref name="Symbol"/> of zero or more indexes the rig's own graphics. Below minus
        /// one it means a skin supplies this piece, and <paramref name="Name"/> indexes
        /// <c>exposedNodeNames</c> to say which slot it is.
        /// </remarks>
        private readonly record struct Placed(int Symbol, int Name,
                                              float A, float B, float Tx, float C, float D, float Ty);

        /// <summary>
        /// The records of frame 0.
        /// </summary>
        /// <remarks>
        /// Header, then a table of absolute frame offsets:
        /// <code>
        ///   u16 frameCount, u16 frame0Records, u16 events, u16 mask
        ///   events × { align2; u16 frame; u8 length; bytes }
        ///   align4; u32 offset[frameCount]
        /// </code>
        /// Records are variable length and the flags say which payloads are present. The one thing
        /// worth knowing before reading the code: a record that names a symbol always carries a
        /// matrix too, and a record with no flags at all carries nothing and is a third of them.
        /// </remarks>
        private static List<Placed> Frame0(byte[] b)
        {
            var placed = new List<Placed>();
            if (b.Length < 8) return placed;

            int frames = BitConverter.ToUInt16(b, 0);
            int events = BitConverter.ToUInt16(b, 4);

            int at = 8;
            for (int i = 0; i < events && at < b.Length; i++)
            {
                at = (at + 1) & ~1;
                at += 2;
                if (at >= b.Length) return placed;
                at += 1 + b[at];
            }

            at = (at + 3) & ~3;
            if (frames <= 0 || at + 4 * frames > b.Length) return placed;

            int start = (int)BitConverter.ToUInt32(b, at);
            int end = frames > 1 ? (int)BitConverter.ToUInt32(b, at + 4) : b.Length;

            // The integrity check the format hands you for free.
            if (start != at + 4 * frames) return placed;
            if (start < 0 || end > b.Length || end <= start) return placed;

            int read = start;
            while (read + 4 <= end)
            {
                int flags = BitConverter.ToUInt16(b, read + 2) & 0xFF;
                int payload = ((flags & 0x21) != 0 ? 8 : 0)
                            + ((flags & 0x06) != 0 ? 4 : 0)
                            + ((flags & 0x08) != 0 ? 4 : 0)
                            + ((flags & 0x10) != 0 ? 24 : 0)
                            + ((flags & 0x40) != 0 ? 4 : 0)
                            + ((flags & 0x80) != 0 ? 8 : 0);

                int after = read + 4 + payload;
                if (after > end) break;

                int cursor = read + 4;
                int symbol = -99;
                int name = -1;

                if ((flags & 0x21) != 0)
                {
                    symbol = BitConverter.ToInt16(b, cursor);
                    name = BitConverter.ToInt16(b, cursor + 2);
                    cursor += 8;
                }

                if ((flags & 0x06) != 0) cursor += 4;
                if ((flags & 0x08) != 0) cursor += 4;

                // Everything drawable carries a matrix, and everything without one is a container.
                if ((flags & 0x10) != 0 && symbol != -1 && symbol != -99)
                {
                    placed.Add(new Placed(
                        symbol, name,
                        BitConverter.ToSingle(b, cursor),
                        BitConverter.ToSingle(b, cursor + 4),
                        BitConverter.ToSingle(b, cursor + 8),
                        BitConverter.ToSingle(b, cursor + 12),
                        BitConverter.ToSingle(b, cursor + 16),
                        BitConverter.ToSingle(b, cursor + 20)));
                }

                read = after;
            }

            return placed;
        }

        /// <summary>The atlas, decoded and turned the right way up.</summary>
        private sealed record Sheet(byte[] Bgra, int Width, int Height);

        private static Sheet? Atlas(AssetsManager manager, AssetsFileInstance assets)
        {
            var textures = assets.file.GetAssetsOfType(AssetClassID.Texture2D);
            if (textures.Count == 0) return null;

            var field = manager.GetBaseField(assets, textures[0]);
            if (field == null) return null;

            var texture = TextureFile.ReadTextureFile(field);
            byte[] compressed = texture.FillPictureData(assets);
            byte[]? pixels = texture.DecodeTextureRaw(compressed, useBgra: true);

            if (pixels == null || texture.m_Width <= 0 || texture.m_Height <= 0) return null;

            int stride = texture.m_Width * 4;
            if (pixels.Length < stride * texture.m_Height) return null;

            // Bottom row first out of the decoder. Flipped once here so sampling can be written the
            // obvious way round.
            var upright = new byte[stride * texture.m_Height];
            for (int row = 0; row < texture.m_Height; row++)
            {
                Buffer.BlockCopy(pixels, (texture.m_Height - 1 - row) * stride, upright, row * stride, stride);
            }

            return new Sheet(upright, texture.m_Width, texture.m_Height);
        }

        /// <summary>One corner of a triangle: where it lands, and where it comes from.</summary>
        private readonly record struct Corner(float X, float Y, float U, float V);

        /// <summary>A triangle ready to draw: its corners, its sheet, and what to multiply it by.</summary>
        private readonly record struct Piece(Corner A, Corner B, Corner C, Sheet From, int Tint);

        /// <summary>A tint of minus one means leave the pixels alone.</summary>
        private const int Untinted = -1;

        /// <summary>How deep the reference tree is followed before giving up.</summary>
        private const int MaxReferenceDepth = 6;

        private static Bitmap? Rasterise(List<Piece> triangles)
        {
            float left = float.MaxValue, right = float.MinValue;
            float bottom = float.MaxValue, top = float.MinValue;

            foreach (var piece in triangles)
            {
                foreach (var corner in new[] { piece.A, piece.B, piece.C })
                {
                    if (corner.X < left) left = corner.X;
                    if (corner.X > right) right = corner.X;
                    if (corner.Y < bottom) bottom = corner.Y;
                    if (corner.Y > top) top = corner.Y;
                }
            }

            if (triangles.Count == 0 || right <= left || top <= bottom) return null;

            float scale = Tall / (top - bottom);
            int width = Math.Max(1, Math.Min(512, (int)MathF.Ceiling((right - left) * scale)));
            int height = Tall;

            var canvas = new byte[width * height * 4];

            foreach (var piece in triangles)
            {
                var (a, b, c) = (piece.A, piece.B, piece.C);

                // Y is up in the drawing and down on a screen.
                Fill(canvas, width, height, piece.From, piece.Tint,
                     (a.X - left) * scale, (top - a.Y) * scale, a.U, a.V,
                     (b.X - left) * scale, (top - b.Y) * scale, b.U, b.V,
                     (c.X - left) * scale, (top - c.Y) * scale, c.U, c.V);
            }

            return FromBgra(canvas, width, height);
        }

        /// <summary>
        /// One textured triangle, alpha over what is already there.
        /// </summary>
        /// <remarks>
        /// Barycentric and unapologetically plain. The whole drawing is 23 to 5,468 triangles, 370
        /// at the median, over a canvas of at most 512 by 96 — there is nothing here worth making
        /// clever, and a clever version would be the thing that broke on a degenerate triangle.
        /// </remarks>
        private static void Fill(byte[] canvas, int width, int height, Sheet atlas, int tint,
                                 float ax, float ay, float au, float av,
                                 float bx, float by, float bu, float bv,
                                 float cx, float cy, float cu, float cv)
        {
            float area = (bx - ax) * (cy - ay) - (by - ay) * (cx - ax);
            if (MathF.Abs(area) < 1e-6f) return;

            // The tint, once per triangle rather than once per pixel.
            int tintR = 255, tintG = 255, tintB = 255;
            if (tint >= 0)
            {
                tintR = (tint >> 16) & 0xFF;
                tintG = (tint >> 8) & 0xFF;
                tintB = tint & 0xFF;
            }

            int minX = Math.Max(0, (int)MathF.Floor(MathF.Min(ax, MathF.Min(bx, cx))));
            int maxX = Math.Min(width - 1, (int)MathF.Ceiling(MathF.Max(ax, MathF.Max(bx, cx))));
            int minY = Math.Max(0, (int)MathF.Floor(MathF.Min(ay, MathF.Min(by, cy))));
            int maxY = Math.Min(height - 1, (int)MathF.Ceiling(MathF.Max(ay, MathF.Max(by, cy))));

            for (int y = minY; y <= maxY; y++)
            {
                float py = y + 0.5f;
                for (int x = minX; x <= maxX; x++)
                {
                    float px = x + 0.5f;

                    float w0 = ((bx - px) * (cy - py) - (by - py) * (cx - px)) / area;
                    float w1 = ((cx - px) * (ay - py) - (cy - py) * (ax - px)) / area;
                    float w2 = 1f - w0 - w1;

                    if (w0 < -0.0005f || w1 < -0.0005f || w2 < -0.0005f) continue;

                    float u = w0 * au + w1 * bu + w2 * cu;
                    float v = w0 * av + w1 * bv + w2 * cv;

                    // The atlas has its origin at the bottom left.
                    int sx = (int)(u * atlas.Width);
                    int sy = (int)((1f - v) * atlas.Height);
                    if (sx < 0) sx = 0; else if (sx >= atlas.Width) sx = atlas.Width - 1;
                    if (sy < 0) sy = 0; else if (sy >= atlas.Height) sy = atlas.Height - 1;

                    int from = (sy * atlas.Width + sx) * 4;
                    int alpha = atlas.Bgra[from + 3];
                    if (alpha == 0) continue;

                    // BGRA out of the decoder, so blue is first.
                    int sourceB = atlas.Bgra[from] * tintB / 255;
                    int sourceG = atlas.Bgra[from + 1] * tintG / 255;
                    int sourceR = atlas.Bgra[from + 2] * tintR / 255;

                    int into = (y * width + x) * 4;
                    if (alpha == 255)
                    {
                        canvas[into] = (byte)sourceB;
                        canvas[into + 1] = (byte)sourceG;
                        canvas[into + 2] = (byte)sourceR;
                        canvas[into + 3] = 255;
                        continue;
                    }

                    int keep = 255 - alpha;
                    canvas[into] = (byte)((sourceB * alpha + canvas[into] * keep) / 255);
                    canvas[into + 1] = (byte)((sourceG * alpha + canvas[into + 1] * keep) / 255);
                    canvas[into + 2] = (byte)((sourceR * alpha + canvas[into + 2] * keep) / 255);
                    canvas[into + 3] = (byte)Math.Min(255, alpha + canvas[into + 3] * keep / 255);
                }
            }
        }

        private static Bitmap FromBgra(byte[] pixels, int width, int height)
        {
            var handle = GCHandle.Alloc(pixels, GCHandleType.Pinned);
            try
            {
                return new Bitmap(PixelFormat.Bgra8888, AlphaFormat.Unpremul,
                                  handle.AddrOfPinnedObject(),
                                  new PixelSize(width, height), new Vector(96, 96), width * 4);
            }
            finally
            {
                handle.Free();
            }
        }

        public void Dispose()
        {
            foreach (var picture in _drawn.Values) picture?.Dispose();
            _drawn.Clear();
        }
    }
}
