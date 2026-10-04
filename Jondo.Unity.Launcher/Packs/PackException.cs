using System;

namespace Jondo.Unity.Launcher.Packs
{
    /// <summary>Why a pack operation stopped, so the window can say it in the player's language.</summary>
    internal enum PackError
    {
        NoClient,
        NoVersion,
        ManifestUnavailable,
        ManifestCorrupt,
        FragmentMissing,
        BadManifest,
        ClientRunning,
        NotEnoughSpace,
        DownloadFailed,
        VerifyFailed,
        DiskError,
    }

    /// <summary>
    /// A pack operation that stopped for a reason the player can act on.
    /// </summary>
    /// <remarks>
    /// Cancellation is not one of these: it surfaces as <see cref="OperationCanceledException"/>
    /// and leaves the download resumable, which is the point of cancelling.
    /// </remarks>
    internal sealed class PackException : Exception
    {
        public PackException(PackError error, string detail, Exception? inner = null)
            : base(error + ": " + detail, inner)
        {
            Error = error;
            Detail = detail;
        }

        public PackError Error { get; }

        /// <summary>The technical part, in English: a URL, a file name, a status code.</summary>
        public string Detail { get; }

        /// <summary>For <see cref="PackError.NotEnoughSpace"/>: bytes needed and bytes free.</summary>
        public long Needed { get; init; }

        public long Free { get; init; }
    }
}
