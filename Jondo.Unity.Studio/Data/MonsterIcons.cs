using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
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
    /// The monsters' pictures, read out of the client where they already live.
    /// </summary>
    /// <remarks>
    /// <c>Content/Picto/Monsters/monster_assets_1x.bundle</c> holds 2,395 textures of 64×64.
    /// Nothing is copied out of it: the client is already on the disk and a copy of ours would be
    /// one more thing to regenerate on patch day and one more thing to be quietly out of date until
    /// somebody did.
    ///
    /// <b>The texture is named after the monster's <c>gfxId</c>, not its id</b>, and getting that
    /// wrong was worse than it sounds. Keyed by monster id, 847 of the 5,134 monsters found a
    /// picture — and every one of those 847 was <em>somebody else's picture</em>, because the
    /// number happened to exist in the set for a different creature. 1,548 of the 2,395 textures
    /// matched no monster id at all. Keyed by gfxId: <b>5,130 of 5,134</b>, and they are the right
    /// ones. Several monsters share a gfxId, which is why there are fewer textures than monsters
    /// and is exactly what you would expect - a Gobbly and a bigger Gobbly are the same drawing.
    ///
    /// Decoded one at a time and kept. The textures are BC7, so decoding all 2,395 up front would
    /// be several seconds and a hundred megabytes for a screen that shows twenty of them.
    ///
    /// There is deliberately no equivalent for NPCs, and it is worth saying why rather than leaving
    /// it looking unfinished: <b>the client has no NPC sprites.</b> An NPC is drawn from a skeleton
    /// — the <c>{bones|skins|colours|scales}</c> look string — animated at run time, so there is no
    /// picture to read. Drawing one properly means an animation player, which is a project of its
    /// own.
    /// </remarks>
    public sealed class MonsterIcons : IDisposable
    {
        private readonly Dictionary<int, Bitmap?> _decoded = new Dictionary<int, Bitmap?>();
        private readonly Dictionary<int, AssetFileInfo> _where = new Dictionary<int, AssetFileInfo>();

        private AssetsManager? _manager;
        private AssetsFileInstance? _assets;
        private bool _tried;

        /// <summary>How many pictures the bundle turned out to hold. Zero when it could not be read.</summary>
        public int Count => _where.Count;

        /// <summary>What went wrong, if anything did.</summary>
        public string Trouble { get; private set; } = "";

        /// <summary>
        /// Whether the bundle holds a picture for this drawing, without decoding it.
        /// </summary>
        /// <remarks>
        /// Separate from <see cref="Of"/> because turning bytes into a <c>Bitmap</c> needs
        /// Avalonia's render backend, which does not exist outside a running app — so a test that
        /// wanted to check the <em>keying</em> was failing on the drawing instead, for a reason
        /// that had nothing to do with what it was checking.
        /// </remarks>
        public bool Has(int gfxId)
        {
            if (gfxId <= 0) return false;

            Open();
            return _where.ContainsKey(gfxId);
        }

        /// <summary>
        /// The picture for one <c>gfxId</c>, or null when there is not one.
        /// </summary>
        /// <remarks>
        /// A gfxId, not a monster id. See the note on the class for what happens when the two are
        /// confused: it does not fail, it draws the wrong creature.
        /// </remarks>
        public Bitmap? Of(int gfxId)
        {
            if (gfxId <= 0) return null;
            if (_decoded.TryGetValue(gfxId, out var already)) return already;

            Open();
            if (_assets == null || !_where.TryGetValue(gfxId, out var info))
            {
                _decoded[gfxId] = null;
                return null;
            }

            Bitmap? picture = null;
            try
            {
                var field = _manager!.GetBaseField(_assets, info);
                var texture = TextureFile.ReadTextureFile(field);

                // Two steps, and both are needed: the first finds the compressed bytes, which may
                // be in the asset or off in a side file, and the second turns BC7 into pixels.
                byte[] compressed = texture.FillPictureData(_assets);
                byte[]? pixels = texture.DecodeTextureRaw(compressed, useBgra: true);

                if (pixels != null && texture.m_Width > 0 && texture.m_Height > 0)
                {
                    picture = FromBgra(pixels, texture.m_Width, texture.m_Height);
                }
            }
            catch (Exception ex)
            {
                // One picture that will not decode is one row without a picture.
                Trouble = ex.Message;
            }

            _decoded[gfxId] = picture;
            return picture;
        }

        private void Open()
        {
            if (_tried) return;
            _tried = true;

            string path = Paths.MonsterIconsBundle;
            if (!File.Exists(path))
            {
                Trouble = $"{Path.GetFileName(path)} is not there; monsters will show without a picture.";
                return;
            }

            try
            {
                _manager = new AssetsManager();
                var bundle = _manager.LoadBundleFile(path, true);
                _assets = _manager.LoadAssetsFileFromBundle(bundle, 0, false);

                foreach (var info in _assets.file.GetAssetsOfType(AssetClassID.Texture2D))
                {
                    // The texture's name is the monster's id, which is the whole reason this is
                    // usable without a second index.
                    //
                    // Read off the parsed asset rather than through the fast path, which needs a
                    // class database this has no reason to carry: the bundle ships its own type
                    // trees, so the fields are self-describing.
                    try
                    {
                        var field = _manager.GetBaseField(_assets, info);
                        string name = field?["m_Name"].AsString ?? "";
                        if (int.TryParse(name, out int gfxId)) _where[gfxId] = info;
                    }
                    catch (Exception)
                    {
                        // One texture that will not parse is one monster without a picture.
                    }
                }

                if (_where.Count == 0)
                {
                    Trouble = $"{Path.GetFileName(path)} opened but held no picture with a number for a name.";
                }
            }
            catch (Exception ex)
            {
                Trouble = $"{Path.GetFileName(path)} could not be read: {ex.GetType().Name}: {ex.Message}";
                _assets = null;
            }
        }

        /// <summary>
        /// Wraps decoded pixels as something Avalonia can draw.
        /// </summary>
        /// <remarks>
        /// The decoder hands back straight BGRA, bottom row first, which is how the graphics card
        /// wants it and upside down for everything else. Flipping here rather than at draw time
        /// costs one pass over 16 KB, once per monster.
        /// </remarks>
        private static Bitmap FromBgra(byte[] pixels, int width, int height)
        {
            int stride = width * 4;
            var flipped = new byte[stride * height];
            for (int row = 0; row < height; row++)
            {
                Buffer.BlockCopy(pixels, (height - 1 - row) * stride, flipped, row * stride, stride);
            }

            var handle = GCHandle.Alloc(flipped, GCHandleType.Pinned);
            try
            {
                return new Bitmap(PixelFormat.Bgra8888, AlphaFormat.Unpremul,
                                  handle.AddrOfPinnedObject(),
                                  new PixelSize(width, height), new Vector(96, 96), stride);
            }
            finally
            {
                handle.Free();
            }
        }

        public void Dispose()
        {
            foreach (var picture in _decoded.Values) picture?.Dispose();
            _decoded.Clear();
            _manager?.UnloadAll();
        }
    }
}
