namespace Pomelo.Core.Formats;

/// <summary>What a conversion produces; decides which converter runs.</summary>
public enum MediaKind { Image, Audio, Video }

/// <summary>An output format, plus the extensions that already are that format.</summary>
public sealed record FileFormat(string Name, string Extension, IReadOnlyList<string> Aliases, MediaKind Kind)
{
    public bool Matches(string path) =>
        Aliases.Contains(Path.GetExtension(path), StringComparer.OrdinalIgnoreCase);
}
