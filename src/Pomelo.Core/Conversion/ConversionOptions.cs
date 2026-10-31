namespace Pomelo.Core.Conversion;

/// <param name="OutputDirectory">Folder for the result; null writes beside the original.</param>
/// <param name="JpegQuality">1–100; clamped.</param>
public sealed record ConversionOptions(string? OutputDirectory = null, int JpegQuality = 92);
