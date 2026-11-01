namespace Pomelo.Core.Formats;

/// <summary>What a conversion produces; decides which converter runs.</summary>
public enum MediaKind { Image, Audio, Video, Document }

/// <summary>An output format, plus the extensions that already are that format.</summary>
public sealed record FileFormat(string Name, string Extension, IReadOnlyList<string> Aliases, MediaKind Kind)
{
    public bool Matches(string path) =>
        Aliases.Contains(Path.GetExtension(path), StringComparer.OrdinalIgnoreCase);
}

public static class InputKind
{
    /// <returns>What Pomelo can read <paramref name="path"/> as (by extension), or null.</returns>
    public static MediaKind? Of(string path) =>
        ImageFormats.IsInput(path) ? MediaKind.Image
        : DocumentFormats.Pdf.Matches(path) ? MediaKind.Document
        : MediaFormats.IsVideoInput(path) ? MediaKind.Video
        : MediaFormats.IsAudioInput(path) ? MediaKind.Audio
        : null;
}
