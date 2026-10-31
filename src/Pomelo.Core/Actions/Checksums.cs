using System.Buffers;
using System.Security.Cryptography;

namespace Pomelo.Core.Actions;

public static class Checksums
{
    /// <summary>Lower-case hex SHA-256, read in chunks so huge files never sit in memory.</summary>
    public static string Sha256(string path, CancellationToken cancellation = default)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1, FileOptions.SequentialScan);
        byte[] buffer = ArrayPool<byte>.Shared.Rent(1 << 20);
        try
        {
            int read;
            while ((read = stream.Read(buffer, 0, buffer.Length)) > 0)
            {
                cancellation.ThrowIfCancellationRequested();
                hash.AppendData(buffer, 0, read);
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
        return Convert.ToHexStringLower(hash.GetHashAndReset());
    }

    /// <summary>One file: just the hash. Several: "hash  name" lines, the format sha256sum reads.</summary>
    public static string Format(IReadOnlyList<(string Path, string Hash)> hashes) =>
        hashes.Count == 1
            ? hashes[0].Hash
            : string.Join(Environment.NewLine, hashes.Select(h => $"{h.Hash}  {Path.GetFileName(h.Path)}"));
}
