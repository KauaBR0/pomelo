using Pomelo.Core.Documents;
using Pomelo.Core.Formats;
using Pomelo.Core.Media;

namespace Pomelo.Core.Conversion;

/// <summary>Something the wheel can do to each dropped file: a conversion or a tool.</summary>
public interface IFileOperation
{
    /// <summary>Short wheel sector text.</summary>
    string Label { get; }
    /// <summary>Shown in the wheel hub while the sector is hovered.</summary>
    string HubText { get; }
    /// <summary>Notification title once done, e.g. "Convertido para JPG".</summary>
    string DoneTitle { get; }

    /// <summary>Creates a new file beside the original or in the configured folder; never modifies the original.</summary>
    /// <returns>The path of the created file.</returns>
    string Apply(string path, ConversionOptions options, FfmpegInfo? ffmpeg, CancellationToken cancellation);
}

/// <summary>An operation that turns all dropped files into a single result, run once per batch.</summary>
public interface ICombiningOperation : IFileOperation
{
    /// <returns>The path of the created file.</returns>
    string ApplyAll(IReadOnlyList<string> paths, ConversionOptions options, CancellationToken cancellation);
}

public sealed record ConvertOperation(FileFormat Target) : IFileOperation
{
    public string Label => Target.Name;
    public string HubText => $"→ {Target.Name}";
    public string DoneTitle => $"Convertido para {Target.Name}";

    public string Apply(string path, ConversionOptions options, FfmpegInfo? ffmpeg, CancellationToken cancellation) =>
        FileConverter.Convert(path, Target, options, ffmpeg, cancellation);

    /// <summary>The wheel operation for <paramref name="target"/>; PDF combines every image into one file.</summary>
    public static IFileOperation For(FileFormat target) =>
        target == DocumentFormats.Pdf ? new CombineToPdfOperation() : new ConvertOperation(target);
}

/// <summary>Images → one PDF, one page each, in drop order.</summary>
public sealed record CombineToPdfOperation : ICombiningOperation
{
    public string Label => DocumentFormats.Pdf.Name;
    public string HubText => "→ PDF (um só arquivo)";
    public string DoneTitle => "PDF criado";

    public string Apply(string path, ConversionOptions options, FfmpegInfo? ffmpeg, CancellationToken cancellation) =>
        ApplyAll([path], options, cancellation);

    public string ApplyAll(IReadOnlyList<string> paths, ConversionOptions options, CancellationToken cancellation) =>
        PdfWriter.Combine(paths, options, cancellation);
}
