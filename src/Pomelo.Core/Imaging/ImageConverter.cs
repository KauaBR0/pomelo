using System.Collections.ObjectModel;
using System.Runtime.InteropServices;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Pomelo.Core.Formats;
using Pomelo.Core.IO;

namespace Pomelo.Core.Imaging;

/// <summary>
/// Converts single-frame images with WIC. Bakes EXIF orientation into pixels, flattens
/// transparency onto white for formats without alpha, and never replaces existing files.
/// Use from a single thread per call (WPF imaging objects are thread-affine until frozen).
/// </summary>
public static class ImageConverter
{
    /// <returns>The path of the created file.</returns>
    public static string Convert(string sourcePath, ImageFormat target, string? outputDirectory = null)
    {
        BitmapSource image = Load(sourcePath, out var colorContexts);

        BitmapEncoder encoder = target switch
        {
            _ when target == ImageFormats.Jpeg => new JpegBitmapEncoder { QualityLevel = 92 },
            _ when target == ImageFormats.Png => new PngBitmapEncoder(),
            _ when target == ImageFormats.Tiff => new TiffBitmapEncoder { Compression = TiffCompressOption.Lzw },
            _ when target == ImageFormats.Bmp => new BmpBitmapEncoder(),
            _ when target == ImageFormats.Gif => new GifBitmapEncoder(),
            _ => throw new ArgumentOutOfRangeException(nameof(target), target.Name, "Formato de saída desconhecido."),
        };

        // PNG/JPEG/TIFF can embed the source ICC profile; BMP/GIF cannot, so convert those to sRGB.
        bool embedsProfile = target == ImageFormats.Jpeg || target == ImageFormats.Png || target == ImageFormats.Tiff;
        if (!embedsProfile && colorContexts is { Count: > 0 })
        {
            image = new ColorConvertedBitmap(image, colorContexts[0], new ColorContext(PixelFormats.Bgra32), PixelFormats.Bgra32);
            colorContexts = null;
        }

        if (target == ImageFormats.Jpeg || target == ImageFormats.Bmp)
            image = FlattenOnWhite(image);
        else if (target == ImageFormats.Gif)
            image = ToPalette(FlattenOnWhite(image));

        encoder.Frames.Add(BitmapFrame.Create(image, null, null, embedsProfile ? colorContexts : null));

        using var buffer = new MemoryStream();
        encoder.Save(buffer);

        string directory = outputDirectory ?? Path.GetDirectoryName(Path.GetFullPath(sourcePath))!;
        return UniqueFileWriter.Write(directory, Path.GetFileNameWithoutExtension(sourcePath), target.Extension,
            buffer.GetBuffer().AsSpan(0, (int)buffer.Length));
    }

    private static BitmapSource Load(string path, out ReadOnlyCollection<ColorContext>? colorContexts)
    {
        BitmapDecoder decoder;
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            decoder = BitmapDecoder.Create(stream, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);
        }
        catch (Exception ex) when (ex is NotSupportedException or FileFormatException or COMException)
        {
            string ext = Path.GetExtension(path).ToUpperInvariant().TrimStart('.');
            string hint = ext is "HEIC" or "HEIF" or "AVIF" or "WEBP"
                ? $" Instale as extensões de imagem {ext} da Microsoft Store."
                : "";
            throw new ConversionException($"O Windows não consegue ler este arquivo {ext}.{hint}", ex);
        }

        BitmapFrame frame;
        if (decoder is IconBitmapDecoder)
            frame = decoder.Frames.MaxBy(f => (long)f.PixelWidth * f.PixelHeight)!; // sizes of one icon
        else if (decoder.Frames.Count == 1)
            frame = decoder.Frames[0];
        else
            throw new ConversionException(
                $"Arquivos com {decoder.Frames.Count} quadros/páginas ainda não são suportados (GIF animado, TIFF multipágina).");

        colorContexts = frame.ColorContexts;
        return ApplyOrientation(frame, ReadOrientation(frame));
    }

    /// <summary>EXIF orientation tag 274; 1 = upright.</summary>
    private static int ReadOrientation(BitmapFrame frame)
    {
        if (frame.Metadata is not BitmapMetadata metadata)
            return 1;
        try
        {
            string? query = metadata.Format switch
            {
                "jpg" => "/app1/ifd/{ushort=274}",
                "tiff" => "/ifd/{ushort=274}",
                _ => null,
            };
            return query is not null && metadata.GetQuery(query) is ushort value and >= 1 and <= 8 ? value : 1;
        }
        catch (NotSupportedException)
        {
            return 1; // Codec exposes no queryable metadata.
        }
    }

    private static BitmapSource ApplyOrientation(BitmapSource source, int orientation)
    {
        // TransformedBitmap applies children in order; positive angles rotate clockwise.
        Transform? transform = orientation switch
        {
            2 => new ScaleTransform(-1, 1),
            3 => new RotateTransform(180),
            4 => new ScaleTransform(1, -1),
            5 => new TransformGroup { Children = { new RotateTransform(90), new ScaleTransform(-1, 1) } },
            6 => new RotateTransform(90),
            7 => new TransformGroup { Children = { new RotateTransform(270), new ScaleTransform(-1, 1) } },
            8 => new RotateTransform(270),
            _ => null,
        };
        return transform is null ? source : new TransformedBitmap(source, transform);
    }

    /// <summary>Composites onto opaque white. Premultiplied: out = c + (255 - a).</summary>
    private static BitmapSource FlattenOnWhite(BitmapSource source)
    {
        var premultiplied = new FormatConvertedBitmap(source, PixelFormats.Pbgra32, null, 0);
        int width = premultiplied.PixelWidth, height = premultiplied.PixelHeight;
        int stride = checked(width * 4);
        byte[] pixels = new byte[checked(stride * height)];
        premultiplied.CopyPixels(pixels, stride, 0);

        for (int i = 0; i < pixels.Length; i += 4)
        {
            int missing = 255 - pixels[i + 3];
            pixels[i] = (byte)(pixels[i] + missing);
            pixels[i + 1] = (byte)(pixels[i + 1] + missing);
            pixels[i + 2] = (byte)(pixels[i + 2] + missing);
            pixels[i + 3] = 255;
        }

        return BitmapSource.Create(width, height, Dpi(source.DpiX), Dpi(source.DpiY),
            PixelFormats.Bgr32, null, pixels, stride);
    }

    /// <summary>GIF requires 8-bit indexed pixels; build an optimized 256-colour palette.</summary>
    private static BitmapSource ToPalette(BitmapSource opaque) =>
        new FormatConvertedBitmap(opaque, PixelFormats.Indexed8, new BitmapPalette(opaque, 256), 0);

    private static double Dpi(double dpi) => dpi > 0 ? dpi : 96;
}
