using System.Collections.Generic;

namespace Jondo.Unity.Sprites
{
    /// <summary>
    /// Which humanoid body a skin belongs to.
    /// </summary>
    /// <remarks>
    /// The humanoid rig is not one rig. It is nineteen —
    /// <c>bones_assets_bone_1-&lt;breed&gt;-static.bundle</c>, one per class — and a look does not
    /// say which: it names its skins, and the <em>first skin</em> decides the body.
    ///
    /// <b>This is the one table in the editor that is copied out of the client rather than read
    /// from it, and it is worth saying why.</b> The 114 <c>BodyData</c> records live in
    /// <c>Data/data_assets_bodiesdataroot.asset.bundle</c> inside Unity's managed-reference
    /// registry — <c>objectsById</c> holds only an <c>rid</c> per record and the fields hang off
    /// <c>references.RefIds</c>. <c>AssetsTools.NET</c> parses that registry as an empty container,
    /// so from C# it cannot be read at all; measured, the walk comes back with zero records. The
    /// alternatives were to give every humanoid the wrong body, or to write down 114 numbers that
    /// do not change within a client version.
    ///
    /// To regenerate after a patch, with UnityPy, which can read it:
    /// <code>
    ///   env = UnityPy.load(".../data_assets_bodiesdataroot.asset.bundle")
    ///   for o in env.objects:
    ///       for r in o.read_typetree().get("references", {}).get("RefIds", []):
    ///           print(r["data"]["skins"], r["data"]["breed"])
    /// </code>
    ///
    /// A miss is survivable: <see cref="NpcSprites"/> falls back to the first rig that exists,
    /// which draws the body of the wrong class rather than no body. In an editor that is the right
    /// way round — a wrong body is obvious on sight, an empty cell is indistinguishable from a bug.
    /// </remarks>
    public static class Breeds
    {
        /// <summary>How many skins the table covers.</summary>
        public static int Count => BySkin.Count;

        /// <summary>The breed a skin belongs to, or zero.</summary>
        public static int Of(int skin) => BySkin.TryGetValue(skin, out int breed) ? breed : 0;

        /// <summary>
        /// Skin to breed, read out of the client's body table for 3.6.10.11.
        /// </summary>
        /// <remarks>
        /// Six skins per breed and nineteen breeds. The first two of each are the original pair and
        /// the other four are later additions, which is why breeds 1 to 12 look like a pattern
        /// (10, 11 · 20, 21 · …) and 13 upwards do not.
        /// </remarks>
        private static readonly Dictionary<int, int> BySkin = new Dictionary<int, int>
        {
            [10] = 1, [11] = 1, [5861] = 1, [5862] = 1, [5941] = 1, [5942] = 1,
            [20] = 2, [21] = 2, [5863] = 2, [5864] = 2, [5943] = 2, [5944] = 2,
            [30] = 3, [31] = 3, [5865] = 3, [5866] = 3, [5945] = 3, [5946] = 3,
            [40] = 4, [41] = 4, [5867] = 4, [5868] = 4, [5947] = 4, [5948] = 4,
            [50] = 5, [51] = 5, [5869] = 5, [5870] = 5, [5949] = 5, [5950] = 5,
            [60] = 6, [61] = 6, [5871] = 6, [5872] = 6, [5951] = 6, [5952] = 6,
            [70] = 7, [71] = 7, [5873] = 7, [5874] = 7, [5953] = 7, [5954] = 7,
            [80] = 8, [81] = 8, [5875] = 8, [5876] = 8, [5955] = 8, [5956] = 8,
            [90] = 9, [91] = 9, [5877] = 9, [5878] = 9, [5957] = 9, [5958] = 9,
            [100] = 10, [101] = 10, [5879] = 10, [5880] = 10, [5959] = 10, [5960] = 10,
            [110] = 11, [111] = 11, [5881] = 11, [5882] = 11, [5961] = 11, [5962] = 11,
            [120] = 12, [121] = 12, [5883] = 12, [5884] = 12, [5963] = 12, [5964] = 12,
            [1405] = 13, [1407] = 13, [5885] = 13, [5886] = 13, [5965] = 13, [5966] = 13,
            [1437] = 14, [1438] = 14, [5887] = 14, [5888] = 14, [5967] = 14, [5968] = 14,
            [1663] = 15, [1664] = 15, [5889] = 15, [5890] = 15, [5969] = 15, [5970] = 15,
            [3179] = 16, [3180] = 16, [5891] = 16, [5892] = 16, [5971] = 16, [5972] = 16,
            [3285] = 17, [3286] = 17, [5893] = 17, [5894] = 17, [5973] = 17, [5974] = 17,
            [3498] = 18, [3499] = 18, [5895] = 18, [5896] = 18, [5975] = 18, [5976] = 18,
            [3221] = 20, [3633] = 20, [5897] = 20, [5898] = 20, [5977] = 20, [5978] = 20,
        };
    }
}
