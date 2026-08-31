using System;
using System.Collections.Generic;
using System.Globalization;

namespace Jondo.Unity.Sprites
{
    /// <summary>
    /// An appearance as the client writes it: <c>{bones|skins|colours|scales}</c>.
    /// </summary>
    /// <remarks>
    /// Every section after the first is optional, so <c>{2713}</c> and <c>{58|||90}</c> are both
    /// whole looks. Measured over the 6,468 NPC templates in <c>world.db</c>:
    ///
    /// <code>
    ///   4,815 (74.4%)  a plain numeric bone, with or without colours and scale
    ///     480 (7.4%)   a numeric bone plus skins
    ///   1,038 (16.0%)  bone 1, the humanoid rig, dressed entirely by its skins
    ///     135 (2.1%)   more than one look, chosen by a condition on world state
    /// </code>
    ///
    /// The last kind looks like
    /// <c>{9262$1;0;0;},{9262$2;0;0;WE=228|WE=252}</c> — boolean guards over things the server
    /// knows. The first group is taken as the canonical appearance, because an editor showing one
    /// of them is right and an editor showing none is useless.
    /// </remarks>
    public readonly struct NpcLook
    {
        private NpcLook(int bone, IReadOnlyList<int> skins, IReadOnlyDictionary<int, int> colours,
                        int scale, bool conditional)
        {
            Bone = bone;
            Skins = skins;
            Colours = colours;
            Scale = scale;
            Conditional = conditional;
        }

        /// <summary>The rig. 1 is the humanoid one, which is dressed by its skins and nothing else.</summary>
        public int Bone { get; }

        public IReadOnlyList<int> Skins { get; }

        /// <summary>Colour index to a 24-bit RGB value.</summary>
        public IReadOnlyDictionary<int, int> Colours { get; }

        /// <summary>A percentage. 100 is as drawn.</summary>
        public int Scale { get; }

        /// <summary>True when the look had more than one group and the first was taken.</summary>
        public bool Conditional { get; }

        public bool Valid => Bone > 0;

        /// <summary>True for the humanoid rig, which this build cannot dress yet.</summary>
        public bool Humanoid => Bone == 1;

        public static readonly NpcLook None = new NpcLook(0, Array.Empty<int>(),
            new Dictionary<int, int>(), 100, false);

        public static NpcLook Parse(string? look)
        {
            if (string.IsNullOrWhiteSpace(look)) return None;

            string text = look.Trim();

            // More than one group: take the first and remember that a choice was made.
            bool conditional = false;
            int comma = text.IndexOf("},", StringComparison.Ordinal);
            if (comma > 0)
            {
                conditional = true;
                text = text[..(comma + 1)];
            }

            text = text.Trim();
            if (text.StartsWith("{", StringComparison.Ordinal)) text = text[1..];
            if (text.EndsWith("}", StringComparison.Ordinal)) text = text[..^1];

            string[] parts = text.Split('|');
            if (parts.Length == 0) return None;

            // The bone can carry a dollar suffix in the conditional form: "9262$1".
            string boneText = parts[0];
            int dollar = boneText.IndexOf('$');
            if (dollar >= 0) boneText = boneText[..dollar];

            int semicolon = boneText.IndexOf(';');
            if (semicolon >= 0) boneText = boneText[..semicolon];

            if (!int.TryParse(boneText.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out int bone))
            {
                return None;
            }

            var skins = new List<int>();
            if (parts.Length > 1)
            {
                foreach (string skin in parts[1].Split(',', StringSplitOptions.RemoveEmptyEntries))
                {
                    if (int.TryParse(skin.Trim(), out int id)) skins.Add(id);
                }
            }

            var colours = new Dictionary<int, int>();
            if (parts.Length > 2)
            {
                foreach (string pair in parts[2].Split(',', StringSplitOptions.RemoveEmptyEntries))
                {
                    int equals = pair.IndexOf('=');
                    if (equals <= 0) continue;

                    if (!int.TryParse(pair[..equals].Trim(), out int index)) continue;

                    string value = pair[(equals + 1)..].Trim();
                    int rgb;
                    if (value.StartsWith("#", StringComparison.Ordinal))
                    {
                        // Short forms are right-padded, the way the client reads them.
                        string hex = value[1..].PadRight(6, '0');
                        if (!int.TryParse(hex[..6], NumberStyles.HexNumber, CultureInfo.InvariantCulture, out rgb))
                        {
                            continue;
                        }
                    }
                    else if (!int.TryParse(value, out rgb))
                    {
                        continue;
                    }

                    colours[index] = rgb & 0xFFFFFF;
                }
            }

            int scale = 100;
            if (parts.Length > 3 && int.TryParse(parts[3].Trim(), out int asked) && asked > 0)
            {
                scale = asked;
            }

            return new NpcLook(bone, skins, colours, scale, conditional);
        }

        public override string ToString()
            => Valid ? $"bone {Bone}, {Skins.Count} skin(s), {Colours.Count} colour(s), {Scale}%" : "(none)";
    }
}
