namespace Pomelo.Core.Formats;

public static class DocumentFormats
{
    /// <summary>As a target: the dropped images combined into one PDF. As an input: pages to images.</summary>
    public static readonly FileFormat Pdf = new("PDF", ".pdf", [".pdf"], MediaKind.Document);

    /// <summary>Wheel order for PDF inputs: each page becomes an image.</summary>
    public static readonly IReadOnlyList<FileFormat> PdfPageOutputs = [ImageFormats.Jpeg, ImageFormats.Png];
}
