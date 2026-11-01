using System.Text;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Pomelo.Core.Conversion;
using Pomelo.Core.Documents;

namespace Pomelo.Tests;

public sealed class PdfTextDocumentTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("pomelo-pdfedit-").FullName;

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    /// <summary>Every Tj below is its own text object, as Chrome and Word often write them.</summary>
    private const string Resume = """
        BT /F2 20 Tf 50 760 Td (T) Tj (\355) Tj (t) Tj (u) Tj (l) Tj (o) Tj ET
        BT /F1 12 Tf 50 730 Td (Hello ) Tj (world) Tj ET
        BT /F2 12 Tf 3 Tc 50 700 Td (R) Tj (E) Tj (S) Tj (U) Tj (M) Tj (O) Tj ET
        BT /F1 12 Tf 50 670 Td (Salvador) Tj 0.6 g ( | ) Tj 0 g (+55 71) Tj ET
        """;

    [Fact]
    public void Text_drawn_in_pieces_reads_as_lines_split_by_colour()
    {
        using var document = PdfTextDocument.Open(Pdf("a.pdf", Resume));

        Assert.Equal(["Título", "Hello world", "RESUMO", "Salvador", "|", "+55 71"], document.Lines.Select(l => l.Text));
        Assert.Equal([0, 1, 2, 3, 4, 5], document.Lines.Select(l => l.Index));
        Assert.Equal("Helvetica-Bold", document.Lines[0].FontName);
    }

    [Fact]
    public void An_edit_keeps_the_font_replaces_the_old_text_and_saves_a_separate_copy()
    {
        string source = Pdf("cv.pdf", Resume);
        byte[] original = File.ReadAllBytes(source);
        using var document = PdfTextDocument.Open(source);

        var edit = document.Apply([(document.Lines[1], "Olá mundo")]).Single();
        string saved = document.Save(_dir);

        Assert.Equal(PdfEditIssues.None, edit.Issues);
        Assert.Equal(Path.Combine(_dir, "cv (editado).pdf"), saved);
        Assert.Equal(original, File.ReadAllBytes(source));
        using (var reopened = PdfTextDocument.Open(saved))
        {
            Assert.Contains("Olá mundo", reopened.Lines.Select(l => l.Text));
            Assert.DoesNotContain(reopened.Lines, l => l.Text.Contains("Hello"));
            Assert.Equal("Helvetica", reopened.Lines.Single(l => l.Text == "Olá mundo").FontName);
        }
        Assert.Equal(Path.Combine(_dir, "cv (editado) (1).pdf"), document.Save(_dir));
    }

    [Fact]
    public void Letters_the_pdf_font_cannot_write_switch_to_an_installed_font()
    {
        using var document = PdfTextDocument.Open(Pdf("a.pdf", Resume));

        var edit = document.Apply([(document.Lines[1], "Ωmega")]).Single(); // Ω is not in WinAnsi.

        Assert.Equal(PdfEditIssues.SystemFont, edit.Issues);
        Assert.Equal("Arial", edit.SystemFontName);
        using var reopened = PdfTextDocument.Open(document.Save(_dir));
        Assert.Contains("Ωmega", reopened.Lines.Select(l => l.Text));
    }

    [Fact]
    public void A_longer_text_reports_running_into_the_next_item_and_a_shorter_one_does_not()
    {
        using var document = PdfTextDocument.Open(Pdf("a.pdf", Resume));
        var city = document.Lines[3];

        Assert.Equal(PdfEditIssues.Overflow, document.Apply([(city, "Salvador, Bahia, Brasil")]).Single().Issues);
        Assert.Equal(PdfEditIssues.None, document.Apply([(city, "Recife")]).Single().Issues);
    }

    [Fact]
    public void Edits_apply_to_the_original_each_time_and_empty_text_deletes_the_line()
    {
        using var document = PdfTextDocument.Open(Pdf("a.pdf", Resume));
        document.Apply([(document.Lines[1], "first try")]);

        document.Apply([(document.Lines[0], " "), (document.Lines[2], "SOBRE")]);

        using var reopened = PdfTextDocument.Open(document.Save(_dir));
        Assert.Equal(["Hello world", "SOBRE", "Salvador", "|", "+55 71"], reopened.Lines.Select(l => l.Text));
    }

    [Theory]
    [InlineData("BAAAAA+ArialMT", false, false, "Arial", "arial.ttf")]
    [InlineData("AAAAAA+Arial-BoldMT", false, false, "Arial Bold", "arialbd.ttf")]
    [InlineData("TimesNewRomanPS-ItalicMT", false, false, "Times New Roman Italic", "timesi.ttf")]
    [InlineData("Calibri", true, false, "Calibri Bold", "calibrib.ttf")]
    [InlineData("Helvetica-Oblique", false, false, "Arial Italic", "ariali.ttf")] // Not installed: Arial, same style.
    public void Pdf_font_names_map_to_the_installed_font_in_the_same_style(string pdfName, bool bold, bool italic, string name, string file)
    {
        var found = SystemFonts.Find(pdfName, bold, italic);

        Assert.Equal(name, found.Name);
        Assert.Equal(file, Path.GetFileName(found.Path), ignoreCase: true);
    }

    [Theory]
    [InlineData("type3", "Type 3")]
    [InlineData("signed", "assinado digitalmente")]
    [InlineData("damaged", "danificado")]
    [InlineData("image-only", "não tem texto editável")]
    public void Pdfs_that_cannot_be_edited_safely_are_refused_with_a_reason(string kind, string reason)
    {
        string path = kind switch
        {
            "type3" => Pdf("t3.pdf", "BT /F3 12 Tf 50 700 Td (a) Tj ET"),
            "signed" => Pdf("signed.pdf", Resume, signed: true),
            "damaged" => Write("damaged.pdf", Encoding.ASCII.GetBytes("%PDF-1.4 not really")),
            _ => ImageOnlyPdf(),
        };

        var error = Assert.Throws<ConversionException>(() => PdfTextDocument.Open(path));

        Assert.Contains(reason, error.Message);
    }

    [Fact]
    public void Saving_into_a_missing_folder_fails_without_creating_anything()
    {
        using var document = PdfTextDocument.Open(Pdf("a.pdf", Resume));
        document.Apply([(document.Lines[1], "x")]);

        Assert.Throws<ConversionException>(() => document.Save(Path.Combine(_dir, "missing")));
        Assert.Equal(["a.pdf"], Directory.GetFiles(_dir).Select(Path.GetFileName));
    }

    private string ImageOnlyPdf()
    {
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(BitmapSource.Create(4, 4, 96, 96, PixelFormats.Bgra32, null, new byte[64], 16)));
        string png = Path.Combine(_dir, "scan.png");
        using (var file = File.Create(png))
            encoder.Save(file);
        return PdfWriter.Combine([png], new ConversionOptions());
    }

    private string Write(string name, byte[] bytes)
    {
        string path = Path.Combine(_dir, name);
        File.WriteAllBytes(path, bytes);
        return path;
    }

    /// <summary>A one-page A4 PDF with Helvetica (/F1), Helvetica-Bold (/F2) and a Type 3 font (/F3).</summary>
    private string Pdf(string name, string content, bool signed = false)
    {
        var latin1 = Encoding.Latin1;
        string fields = signed ? " /AcroForm << /Fields [8 0 R] /SigFlags 3 >>" : "";
        string annots = signed ? " /Annots [8 0 R]" : "";
        string[] objects =
        [
            $"<< /Type /Catalog /Pages 2 0 R{fields} >>",
            "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 595 842] /Contents 4 0 R " +
            $"/Resources << /Font << /F1 5 0 R /F2 6 0 R /F3 7 0 R >> >>{annots} >>",
            $"<< /Length {latin1.GetByteCount(content)} >>\nstream\n{content}\nendstream",
            "<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica /Encoding /WinAnsiEncoding >>",
            "<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica-Bold /Encoding /WinAnsiEncoding >>",
            "<< /Type /Font /Subtype /Type3 /FontBBox [0 0 1000 1000] /FontMatrix [0.001 0 0 0.001 0 0] " +
            "/CharProcs << /a 9 0 R >> /Encoding << /Type /Encoding /Differences [97 /a] >> " +
            "/FirstChar 97 /LastChar 97 /Widths [1000] /Resources << >> >>",
            "<< /FT /Sig /T (Signature1) /V 10 0 R /Type /Annot /Subtype /Widget /Rect [0 0 0 0] /P 3 0 R >>",
            "<< /Length 30 >>\nstream\n1000 0 0 0 1000 1000 d1 0 0 9 9 re f\nendstream",
            "<< /Type /Sig /Filter /Adobe.PPKLite /SubFilter /adbe.pkcs7.detached /ByteRange [0 0 0 0] /Contents <00> >>",
        ];

        using var pdf = new MemoryStream();
        void Put(string text) => pdf.Write(latin1.GetBytes(text));
        Put("%PDF-1.7\n");
        var offsets = new List<long>();
        for (int i = 0; i < objects.Length; i++)
        {
            offsets.Add(pdf.Position);
            Put($"{i + 1} 0 obj\n{objects[i]}\nendobj\n");
        }
        long xref = pdf.Position;
        Put($"xref\n0 {objects.Length + 1}\n0000000000 65535 f \n");
        foreach (long offset in offsets)
            Put($"{offset:D10} 00000 n \n");
        Put($"trailer\n<< /Size {objects.Length + 1} /Root 1 0 R >>\nstartxref\n{xref}\n%%EOF\n");
        return Write(name, pdf.ToArray());
    }
}
