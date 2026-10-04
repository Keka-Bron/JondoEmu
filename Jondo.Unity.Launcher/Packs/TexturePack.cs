using System.Collections.Generic;
using System.IO;

namespace Jondo.Unity.Launcher.Packs
{
    /// <summary>
    /// One of the client's optional scenery texture packs.
    /// </summary>
    /// <remarks>
    /// The names are the client's, from its own <c>zaap.yml</c>: when Ankama's launcher has the
    /// "HD/4K pack" checkbox ticked it installs the fragment and adds the argument, with no value.
    /// The argument only tells the client the pack is on disk; the pack actually shown is picked
    /// in the game's options, and the choice applies on the next map change.
    /// </remarks>
    internal sealed record TexturePack(string Id, string Fragment, string Folder, string LaunchFlag)
    {
        public static readonly TexturePack Hd = new("hd", "map_textures_2x", "2x", "--hdReady");
        public static readonly TexturePack FourK = new("4k", "map_textures_4x", "4x", "--4kReady");

        public static IReadOnlyList<TexturePack> All { get; } = new[] { Hd, FourK };

        /// <summary>Where the pack lives, relative to the client folder.</summary>
        public string RelativeFolder
            => Path.Combine("Dofus_Data", "StreamingAssets", "Content", "Map", "Textures", Folder);
    }

    /// <summary>What is on disk for a pack.</summary>
    internal enum PackState
    {
        /// <summary>No Dofus.exe, so no folder to look in.</summary>
        NoClient,

        /// <summary>The client's version file is missing or unreadable: nothing can be matched.</summary>
        NoVersion,

        /// <summary>Nothing of the pack is on disk.</summary>
        Missing,

        /// <summary>An interrupted download, or files missing from a pack that was verified.</summary>
        Partial,

        /// <summary>
        /// Files on disk that have not been verified for this client version: installed by
        /// Ankama's launcher, copied by hand, or verified for a version the client no longer is.
        /// </summary>
        Unverified,

        /// <summary>Verified against this client version's manifest, and every file still there.</summary>
        Installed,
    }

    /// <summary>A pack's state, with what the window shows next to it.</summary>
    /// <param name="ClientVersion">The version the client folder says it is, if readable.</param>
    /// <param name="VerifiedVersion">The version the pack was last verified for, if ever.</param>
    /// <param name="Size">The pack's installed size, when known.</param>
    internal sealed record PackStatus(
        TexturePack Pack,
        PackState State,
        string ClientVersion,
        string VerifiedVersion,
        long? Size)
    {
        /// <summary>True when something of the pack is on disk and "Remove" makes sense.</summary>
        public bool HasFiles => State is PackState.Partial or PackState.Unverified or PackState.Installed;
    }
}
