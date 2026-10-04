namespace Jondo.Unity.Cytrus;

/// <summary>
/// Which bundles to fetch, and where each of their chunks goes, to rebuild some files of a
/// fragment.
/// </summary>
/// <remarks>
/// The manifest lists each file as chunks with the offset they take in the file, and each bundle
/// as chunks with the offset they take in the bundle. The plan joins the two: for every bundle
/// that holds at least one wanted chunk, the chunks to take out of it and every place in every
/// file each one is written to. A chunk that appears twice (the same bytes in two files, or twice
/// in one) is downloaded once and written twice.
///
/// It also refuses a manifest whose chunks do not tile each file exactly, from offset zero to
/// its size. That would still produce a file, just not the one the manifest names, and it would
/// only show up as a hash mismatch after gigabytes of downloading.
/// </remarks>
public sealed class CytrusPlan
{
    /// <summary>Where a verified chunk is written: which file of <see cref="Files"/>, and where in it.</summary>
    public readonly record struct Placement(int File, long Offset);

    /// <summary>A chunk to take out of a bundle, and the places it goes.</summary>
    public sealed record Piece(Sha1Hash Hash, long Offset, long Size, Placement[] Targets);

    /// <summary>One bundle and the chunks wanted from it, sorted by their offset in the bundle.</summary>
    public sealed record BundleJob(Sha1Hash Hash, Piece[] Pieces)
    {
        /// <summary>The first byte to ask for.</summary>
        public long RangeStart => Pieces[0].Offset;

        /// <summary>One past the last byte to ask for.</summary>
        public long RangeEnd => Pieces[^1].Offset + Pieces[^1].Size;

        /// <summary>The bytes that will be written, which is what progress counts.</summary>
        public long Bytes => Pieces.Sum(p => p.Size);
    }

    private CytrusPlan(CytrusFile[] files, BundleJob[] bundles)
    {
        Files = files;
        Bundles = bundles;
        Bytes = bundles.Sum(b => b.Bytes);
    }

    public IReadOnlyList<CytrusFile> Files { get; }

    public IReadOnlyList<BundleJob> Bundles { get; }

    /// <summary>Every byte the plan downloads, once per chunk.</summary>
    public long Bytes { get; }

    /// <summary>The plan to rebuild <paramref name="files"/>, which must belong to <paramref name="fragment"/>.</summary>
    public static CytrusPlan For(CytrusFragment fragment, IEnumerable<CytrusFile> files)
    {
        var wanted = files.ToArray();

        // Every place each chunk goes, and its size as the files see it.
        var targets = new Dictionary<Sha1Hash, (long Size, List<Placement> Places)>();
        for (int f = 0; f < wanted.Length; f++)
        {
            var file = wanted[f];
            long end = 0;
            foreach (var piece in file.Pieces.OrderBy(p => p.Offset))
            {
                if (piece.Offset != end)
                    throw new InvalidDataException(file.Name + ": its chunks leave a gap or overlap at byte " + end);
                end = piece.Offset + piece.Size;

                if (!targets.TryGetValue(piece.Hash, out var entry))
                {
                    entry = (piece.Size, new List<Placement>());
                    targets[piece.Hash] = entry;
                }
                else if (entry.Size != piece.Size)
                {
                    throw new InvalidDataException("chunk " + piece.Hash + " is listed with two different sizes");
                }
                entry.Places.Add(new Placement(f, piece.Offset));
            }

            if (end != file.Size)
                throw new InvalidDataException(file.Name + ": its chunks add up to " + end + " bytes, not " + file.Size);
        }

        // Each chunk is taken from the first bundle that has it. A chunk can live in more than
        // one bundle, and any of them is as good as another.
        var lodged = new HashSet<Sha1Hash>();
        var jobs = new List<BundleJob>();
        foreach (var bundle in fragment.Bundles)
        {
            var pieces = new List<Piece>();
            foreach (var chunk in bundle.Chunks)
            {
                if (!targets.TryGetValue(chunk.Hash, out var entry) || !lodged.Add(chunk.Hash)) continue;
                if (chunk.Size != entry.Size)
                    throw new InvalidDataException("chunk " + chunk.Hash + " has a different size in bundle " + bundle.Hash);
                pieces.Add(new Piece(chunk.Hash, chunk.Offset, chunk.Size, entry.Places.ToArray()));
            }

            if (pieces.Count > 0)
                jobs.Add(new BundleJob(bundle.Hash, pieces.OrderBy(p => p.Offset).ToArray()));
        }

        foreach (var hash in targets.Keys)
        {
            if (!lodged.Contains(hash))
                throw new InvalidDataException("chunk " + hash + " is in no bundle of " + fragment.Name);
        }

        return new CytrusPlan(wanted, jobs.ToArray());
    }
}
