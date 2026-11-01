namespace Pomelo.Core.Formats;

/// <summary>
/// Tells an animated GIF (two or more frames) from a still one by walking the GIF block
/// structure. Frame data is skipped, not decoded, so the check stays cheap enough to run
/// while the wheel opens.
/// </summary>
public static class GifAnimation
{
    private const byte ImageDescriptor = 0x2C;
    private const byte Extension = 0x21;

    /// <returns>False for still, unreadable, truncated or non-GIF files.</returns>
    public static bool IsAnimated(string path)
    {
        if (!ImageFormats.Gif.Matches(path))
            return false;
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete,
                bufferSize: 4096);
            return CountsTwoFrames(stream);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or EndOfStreamException)
        {
            return false;
        }
    }

    private static bool CountsTwoFrames(Stream stream)
    {
        Span<byte> header = stackalloc byte[13]; // Signature (6) + logical screen descriptor (7).
        stream.ReadExactly(header);
        if (!header[..3].SequenceEqual("GIF"u8))
            return false;
        SkipColorTable(stream, header[10]);

        int frames = 0;
        Span<byte> descriptor = stackalloc byte[9];
        while (true)
        {
            int block = stream.ReadByte();
            switch (block)
            {
                case ImageDescriptor:
                    if (++frames == 2)
                        return true;
                    stream.ReadExactly(descriptor);
                    SkipColorTable(stream, descriptor[8]);
                    Next(stream); // LZW minimum code size.
                    SkipSubBlocks(stream);
                    break;
                case Extension:
                    Next(stream); // Label.
                    SkipSubBlocks(stream);
                    break;
                default: // Trailer, end of file or garbage.
                    return false;
            }
        }
    }

    /// <param name="flags">Packed field: bit 7 = table present, bits 0–2 = size exponent.</param>
    private static void SkipColorTable(Stream stream, byte flags)
    {
        if ((flags & 0x80) != 0)
            Skip(stream, 3 << ((flags & 0x07) + 1));
    }

    private static void SkipSubBlocks(Stream stream)
    {
        int size;
        while ((size = Next(stream)) != 0)
            Skip(stream, size);
    }

    private static int Next(Stream stream) =>
        stream.ReadByte() is var b and >= 0 ? b : throw new EndOfStreamException();

    private static void Skip(Stream stream, int count)
    {
        if (stream.Position + count > stream.Length)
            throw new EndOfStreamException();
        stream.Seek(count, SeekOrigin.Current);
    }
}
