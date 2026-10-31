using Pomelo.Core.Actions;

namespace Pomelo.Tests;

public sealed class ChecksumsTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("pomelo-hash-").FullName;

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    [Fact]
    public void Sha256_matches_the_standard_test_vector_across_chunk_boundaries()
    {
        string small = Path.Combine(_dir, "abc.txt");
        File.WriteAllText(small, "abc");
        // 3 MiB of 'a' spans several 1 MiB read chunks.
        string large = Path.Combine(_dir, "large.bin");
        File.WriteAllBytes(large, Enumerable.Repeat((byte)'a', 3 * 1024 * 1024).ToArray());

        Assert.Equal("ba7816bf8f01cfea414140de5dae2223b00361a396177a9cb410ff61f20015ad", Checksums.Sha256(small));
        Assert.Equal(Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(large))),
            Checksums.Sha256(large));
    }

    [Fact]
    public void Several_files_use_the_sha256sum_line_format()
    {
        string text = Checksums.Format([(@"C:\a\one.txt", "aa"), (@"C:\b\two.txt", "bb")]);

        Assert.Equal($"aa  one.txt{Environment.NewLine}bb  two.txt", text);
        Assert.Equal("aa", Checksums.Format([(@"C:\a\one.txt", "aa")]));
    }
}
