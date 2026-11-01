using System.Runtime.InteropServices;
using Pomelo.Core.Conversion;
using Pomelo.Core.IO;
using static Pomelo.Core.Documents.Pdfium;

namespace Pomelo.Core.Documents;

/// <summary>Rectangle in PDF page space: points (1/72 inch), origin at the bottom-left.</summary>
public readonly record struct PdfRect(double Left, double Bottom, double Right, double Top)
{
    public double Width => Right - Left;
    public double Height => Top - Bottom;
}

/// <summary>
/// A line as the editor shows it: neighbouring pieces of text on one baseline with the same
/// font, size and colour. PDFs often store text word by word or letter by letter.
/// </summary>
/// <param name="Index">Position among the lines of <paramref name="Page"/> (0-based), top to bottom, left to right.</param>
public sealed record PdfTextLine(int Page, int Index, string Text, PdfRect Bounds, string FontName);

[Flags]
public enum PdfEditIssues
{
    None = 0,
    /// <summary>The new text runs past the space the line had (into the next item or the page margin).</summary>
    Overflow = 1,
    /// <summary>The PDF lacks some letters, so the line was rewritten in an installed font.</summary>
    SystemFont = 2,
    /// <summary>Some letters could not be written in any font; they will be missing.</summary>
    MissingLetters = 4,
}

/// <param name="Bounds">Where the new text ends up; empty when the line was deleted.</param>
public sealed record PdfLineEdit(PdfTextLine Line, string Text, PdfEditIssues Issues, string? SystemFontName, PdfRect Bounds);

/// <summary>
/// Edits the text of a PDF in place, with PDFium (Chrome's PDF engine): a changed line keeps
/// its font, size, colour and position, and the old text is removed from the file (not hidden).
/// Lines never reflow, so a longer text can run into its neighbours (reported, not prevented).
/// The original file is never modified: <see cref="Save"/> writes "name (editado).pdf".
/// </summary>
public sealed unsafe class PdfTextDocument : IDisposable
{
    private readonly byte* _data;
    private readonly nuint _length;
    private readonly (double Width, double Height)[] _pageSizes;
    private IntPtr _working;
    private readonly Dictionary<int, IntPtr> _openPages = [];
    private readonly Dictionary<string, IntPtr> _systemFonts = [];
    private IReadOnlyList<PdfLineEdit> _applied = [];
    private bool _disposed;

    public string SourcePath { get; }
    public int PageCount => _pageSizes.Length;
    public IReadOnlyList<PdfTextLine> Lines { get; }

    private PdfTextDocument(string path, byte* data, nuint length, IntPtr document)
    {
        SourcePath = path;
        _data = data;
        _length = length;
        _working = document;
        _pageSizes = new (double, double)[FPDF_GetPageCount(document)];
        var lines = new List<PdfTextLine>();
        try
        {
            for (int i = 0; i < _pageSizes.Length; i++)
            {
                IntPtr page = Page(i);
                _pageSizes[i] = (FPDF_GetPageWidthF(page), FPDF_GetPageHeightF(page));
                IntPtr textPage = FPDFText_LoadPage(page);
                try
                {
                    int pageIndex = i;
                    lines.AddRange(Group(Collect(page, textPage)).Select((segment, index) =>
                        new PdfTextLine(pageIndex, index, Join(segment), Union(segment), Display(segment[0].FontName))));
                }
                finally
                {
                    FPDFText_ClosePage(textPage);
                }
            }
        }
        catch
        {
            foreach (IntPtr page in _openPages.Values)
                FPDF_ClosePage(page);
            _openPages.Clear();
            throw; // Open closes the document and frees the data.
        }
        Lines = lines;
    }

    /// <exception cref="ConversionException">Unreadable, protected, signed, without text, or using fonts that cannot be edited.</exception>
    public static PdfTextDocument Open(string path)
    {
        byte[] bytes;
        try
        {
            bytes = File.ReadAllBytes(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new ConversionException($"Não foi possível ler o PDF: {ex.Message}", ex);
        }

        var length = (nuint)bytes.Length;
        var data = (byte*)NativeMemory.Alloc(Math.Max(length, 1));
        bytes.CopyTo(new Span<byte>(data, bytes.Length));
        lock (Sync)
        {
            EnsureInitialized();
            IntPtr document = FPDF_LoadMemDocument64(data, length, null);
            if (document == IntPtr.Zero)
            {
                uint error = FPDF_GetLastError();
                NativeMemory.Free(data);
                throw new ConversionException(error switch
                {
                    ErrPassword => "O PDF é protegido por senha.",
                    ErrSecurity => "O PDF usa uma proteção que o Pomelo não suporta.",
                    _ => "O PDF está danificado ou não é um PDF.",
                });
            }

            PdfTextDocument? opened = null;
            try
            {
                if (FPDF_GetSecurityHandlerRevision(document) != -1)
                    throw new ConversionException("O PDF tem proteção contra alterações e não pode ser editado.");
                if (FPDF_GetSignatureCount(document) > 0)
                    throw new ConversionException("O PDF é assinado digitalmente: editar o texto invalidaria a assinatura.");
                opened = new PdfTextDocument(path, data, length, document);
                if (opened.Lines.Count == 0)
                    throw new ConversionException("O PDF não tem texto editável (é escaneado ou só tem imagens).");
                return opened;
            }
            catch
            {
                if (opened is not null)
                {
                    opened.Dispose(); // Closes the document and frees the data.
                }
                else
                {
                    FPDF_CloseDocument(document);
                    NativeMemory.Free(data);
                }
                throw;
            }
        }
    }

    /// <summary>Page size in points, as displayed (rotation applied).</summary>
    public (double Width, double Height) PageSize(int page) => _pageSizes[page];

    /// <summary>
    /// Rebuilds the edited document from the original with <paramref name="edits"/> (line → new
    /// text; empty text deletes the line). Previews and <see cref="Save"/> reflect the last call.
    /// </summary>
    public IReadOnlyList<PdfLineEdit> Apply(IEnumerable<(PdfTextLine Line, string Text)> edits)
    {
        lock (Sync)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            CloseWorking();
            _working = FPDF_LoadMemDocument64(_data, _length, null);
            var results = new List<PdfLineEdit>();
            foreach (var pageEdits in edits.Where(e => e.Text != e.Line.Text).GroupBy(e => e.Line.Page).OrderBy(g => g.Key))
                results.AddRange(ApplyToPage(pageEdits.Key, pageEdits.ToList()));
            _applied = results;
            return results;
        }
    }

    private List<PdfLineEdit> ApplyToPage(int pageIndex, List<(PdfTextLine Line, string Text)> edits)
    {
        IntPtr page = Page(pageIndex);
        IntPtr textPage = FPDFText_LoadPage(page);
        List<List<Piece>> segments;
        try
        {
            segments = Group(Collect(page, textPage));
        }
        finally
        {
            FPDFText_ClosePage(textPage);
        }

        var obstacles = Obstacles(page);
        double textLeft = segments.Min(s => s[0].Bounds.Left);
        var changes = new List<Change>();
        foreach (var (line, text) in edits)
        {
            var segment = segments[line.Index];
            var change = new Change(line, text, RightLimit(segment, obstacles, pageIndex, textLeft));
            bool delete = string.IsNullOrWhiteSpace(text);
            foreach (var piece in segment.Skip(delete ? 0 : 1))
            {
                FPDFPage_RemoveObject(page, piece.Handle);
                FPDFPageObj_Destroy(piece.Handle);
            }
            if (!delete)
            {
                change.Target = segment[0].Handle;
                SetText(change.Target, text);
            }
            changes.Add(change);
        }

        // Letters missing from the PDF's (subset) font read back wrong: rewrite those lines in an installed font.
        WithTextPage(page, textPage =>
        {
            foreach (var change in changes.Where(c => c.Target != IntPtr.Zero))
                if (Normalize(GetText(change.Target, textPage)) != Normalize(change.Text))
                {
                    (change.Target, change.SystemFont) = RewriteInSystemFont(page, change.Target, change.Text);
                    change.Issues |= PdfEditIssues.SystemFont;
                }
        });

        FPDFPage_GenerateContent(page);

        // Measure and re-check once the page content is final.
        var results = new List<PdfLineEdit>();
        WithTextPage(page, textPage =>
        {
            foreach (var change in changes)
            {
                if (change.Target == IntPtr.Zero)
                {
                    results.Add(new(change.Line, "", PdfEditIssues.None, null, default));
                    continue;
                }
                var bounds = Bounds(change.Target);
                if (bounds.Right > change.Limit + 0.5)
                    change.Issues |= PdfEditIssues.Overflow;
                if (Normalize(GetText(change.Target, textPage)) != Normalize(change.Text))
                    change.Issues |= PdfEditIssues.MissingLetters;
                results.Add(new(change.Line, change.Text, change.Issues, change.SystemFont, bounds));
            }
        });
        return results;
    }

    private sealed class Change(PdfTextLine line, string text, double limit)
    {
        public PdfTextLine Line { get; } = line;
        public string Text { get; } = text;
        public double Limit { get; } = limit;
        /// <summary>The text object now holding the line; zero when the line was deleted.</summary>
        public IntPtr Target { get; set; }
        public PdfEditIssues Issues { get; set; }
        public string? SystemFont { get; set; }
    }

    private static void WithTextPage(IntPtr page, Action<IntPtr> use)
    {
        IntPtr textPage = FPDFText_LoadPage(page);
        try
        {
            use(textPage);
        }
        finally
        {
            FPDFText_ClosePage(textPage);
        }
    }

    private (IntPtr Replacement, string FontName) RewriteInSystemFont(IntPtr page, IntPtr original, string text)
    {
        IntPtr font = FPDFTextObj_GetFont(original);
        bool bold = FPDFFont_GetWeight(font) >= 600;
        bool italic = FPDFFont_GetItalicAngle(font, out int angle) != 0 && angle != 0;
        var (file, name) = SystemFonts.Find(BaseFontName(font), bold, italic);
        IntPtr systemFont = LoadSystemFont(file);
        if (systemFont == IntPtr.Zero)
            return (original, name);

        FPDFTextObj_GetFontSize(original, out float size);
        IntPtr replacement = FPDFPageObj_CreateTextObj(_working, systemFont, size);
        SetText(replacement, text);
        FPDFPageObj_GetMatrix(original, out var matrix);
        FPDFPageObj_SetMatrix(replacement, matrix);
        if (FPDFPageObj_GetFillColor(original, out uint r, out uint g, out uint b, out uint a) != 0)
            FPDFPageObj_SetFillColor(replacement, r, g, b, a);
        FPDFPage_RemoveObject(page, original);
        FPDFPageObj_Destroy(original);
        FPDFPage_InsertObject(page, replacement);
        return (replacement, name);
    }

    private IntPtr LoadSystemFont(string file)
    {
        if (_systemFonts.TryGetValue(file, out IntPtr font))
            return font;
        try
        {
            byte[] data = File.ReadAllBytes(file);
            fixed (byte* d = data)
                font = FPDFText_LoadFont(_working, d, (uint)data.Length, FontTypeTrueType, cid: 1);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            font = IntPtr.Zero;
        }
        _systemFonts[file] = font;
        return font;
    }

    /// <summary>Renders <paramref name="page"/> of the edited document to BGRA pixels on white.</summary>
    public byte[] Render(int page, int width, int height)
    {
        var pixels = new byte[checked(width * height * 4)];
        lock (Sync)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            fixed (byte* p = pixels)
            {
                IntPtr bitmap = FPDFBitmap_CreateEx(width, height, BitmapBgra, p, width * 4);
                try
                {
                    FPDFBitmap_FillRect(bitmap, 0, 0, width, height, 0xFFFFFFFF);
                    FPDF_RenderPageBitmap(bitmap, Page(page), 0, 0, width, height, 0, RenderAnnotations);
                }
                finally
                {
                    FPDFBitmap_Destroy(bitmap);
                }
            }
        }
        return pixels;
    }

    /// <summary>Maps a page rectangle to pixels of a <paramref name="width"/> × <paramref name="height"/> rendering.</summary>
    public (double X, double Y, double Width, double Height) ToPixels(int page, PdfRect rect, int width, int height)
    {
        lock (Sync)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            IntPtr p = Page(page);
            FPDF_PageToDevice(p, 0, 0, width, height, 0, rect.Left, rect.Top, out int x1, out int y1);
            FPDF_PageToDevice(p, 0, 0, width, height, 0, rect.Right, rect.Bottom, out int x2, out int y2);
            return (Math.Min(x1, x2), Math.Min(y1, y2), Math.Abs(x2 - x1), Math.Abs(y2 - y1));
        }
    }

    /// <summary>
    /// Writes the edited document as "name (editado).pdf" (or "name (editado) (n).pdf") in
    /// <paramref name="directory"/>, after checking that the written file reopens with every
    /// edit in place. Nothing is created when the check fails.
    /// </summary>
    /// <returns>The path of the created PDF.</returns>
    public string Save(string directory)
    {
        if (!Directory.Exists(directory))
            throw new ConversionException($"A pasta de saída não existe: {directory}");
        string temp = Path.Combine(directory, $"~pomelo-{Guid.NewGuid():N}.part");
        try
        {
            lock (Sync)
            {
                ObjectDisposedException.ThrowIf(_disposed, this);
                bool written;
                using (var file = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                    written = Pdfium.Save(_working, file);
                if (!written)
                    throw new ConversionException("Não foi possível gravar o PDF (disco cheio ou sem permissão?).");
                Verify(temp);
            }
            if (PdfPages.CountPages(temp) != PageCount)
                throw new ConversionException("O PDF gravado não abriu corretamente no leitor do Windows; nada foi salvo.");
            string name = Path.GetFileNameWithoutExtension(SourcePath) + " (editado)";
            return UniqueFileWriter.MoveIntoPlace(temp, directory, name, ".pdf");
        }
        finally
        {
            File.Delete(temp); // No-op after a successful move.
        }
    }

    /// <summary>Reopens the written file and checks that each edited page holds its new text.</summary>
    private void Verify(string path)
    {
        byte[] bytes = File.ReadAllBytes(path);
        fixed (byte* data = bytes)
        {
            IntPtr document = FPDF_LoadMemDocument64(data, (nuint)bytes.Length, null);
            if (document == IntPtr.Zero)
                throw new ConversionException("O PDF gravado não pôde ser reaberto; nada foi salvo.");
            try
            {
                if (FPDF_GetPageCount(document) != PageCount)
                    throw new ConversionException("O PDF gravado perdeu páginas; nada foi salvo.");
                foreach (var pageEdits in _applied.Where(e => e.Text.Length > 0).GroupBy(e => e.Line.Page))
                {
                    IntPtr page = FPDF_LoadPage(document, pageEdits.Key);
                    IntPtr textPage = FPDFText_LoadPage(page);
                    try
                    {
                        string pageText = Normalize(string.Concat(Collect(page, textPage).Select(p => p.Text)));
                        var lost = pageEdits.FirstOrDefault(e => !pageText.Contains(Normalize(e.Text), StringComparison.Ordinal));
                        if (lost is not null)
                            throw new ConversionException($"A alteração \"{lost.Text}\" não ficou no PDF gravado; nada foi salvo.");
                    }
                    finally
                    {
                        FPDFText_ClosePage(textPage);
                        FPDF_ClosePage(page);
                    }
                }
            }
            finally
            {
                FPDF_CloseDocument(document);
            }
        }
    }

    public void Dispose()
    {
        lock (Sync)
        {
            if (_disposed)
                return;
            _disposed = true;
            CloseWorking();
            NativeMemory.Free(_data);
        }
    }

    private IntPtr Page(int index)
    {
        if (!_openPages.TryGetValue(index, out IntPtr page))
            _openPages[index] = page = FPDF_LoadPage(_working, index);
        return page;
    }

    private void CloseWorking()
    {
        foreach (IntPtr page in _openPages.Values)
            FPDF_ClosePage(page);
        _openPages.Clear();
        foreach (IntPtr font in _systemFonts.Values.Where(f => f != IntPtr.Zero))
            FPDFFont_Close(font);
        _systemFonts.Clear();
        if (_working != IntPtr.Zero)
            FPDF_CloseDocument(_working);
        _working = IntPtr.Zero;
    }

    // ---- Text pieces and lines ----

    private sealed record Piece(IntPtr Handle, string Text, PdfRect Bounds, double Baseline, double Em, bool Upright, string FontName, uint Color);

    /// <exception cref="ConversionException">The page uses Type 3 fonts.</exception>
    private static List<Piece> Collect(IntPtr page, IntPtr textPage)
    {
        var pieces = new List<Piece>();
        var texts = TextByObject(textPage);
        int count = FPDFPage_CountObjects(page);
        for (int i = 0; i < count; i++)
        {
            IntPtr obj = FPDFPage_GetObject(page, i);
            if (FPDFPageObj_GetType(obj) != PageObjText || FPDFTextObj_GetTextRenderMode(obj) == TextRenderInvisible)
                continue; // Invisible text is the searchable layer of scanned PDFs, not something to edit.
            IntPtr font = FPDFTextObj_GetFont(obj);
            // Type 3 fonts draw each letter as a picture and carry no font program. Rewriting a page
            // that uses them produces content other readers reject, so such PDFs are refused.
            if (FPDFFont_GetIsEmbedded(font) != 0 && FPDFFont_GetFontData(font, null, 0, out nuint size) != 0 && size == 0)
                throw new ConversionException("O PDF usa fontes desenhadas (Type 3), que o Pomelo não consegue editar sem estragar a página.");

            string text = texts.GetValueOrDefault(obj, "");
            if (text.Length == 0)
                continue;
            var bounds = Bounds(obj);
            FPDFPageObj_GetMatrix(obj, out var m);
            FPDFTextObj_GetFontSize(obj, out float fontSize);
            // Effective size = Tf size × the object's scale (Chrome writes Tf 28 with a 0.75 matrix).
            // Glyph bounds are no substitute: a "·" or "." is a fraction of the line height.
            double scale = Math.Sqrt(Math.Abs(m.A * m.D - m.B * m.C));
            double em = fontSize > 0 && scale > 0 ? fontSize * scale : Math.Max(1, bounds.Height * 1.4);
            uint color = FPDFPageObj_GetFillColor(obj, out uint r, out uint g, out uint b, out uint a) != 0
                ? r << 24 | g << 16 | b << 8 | a
                : 0;
            bool upright = Math.Abs(m.B) < 1e-3 && Math.Abs(m.C) < 1e-3 && m.A > 0 && m.D > 0;
            pieces.Add(new(obj, text, bounds, m.F, em, upright, BaseFontName(font), color));
        }
        return pieces;
    }

    /// <summary>
    /// Rows by baseline (top to bottom), then pieces left to right, split where the font, size or
    /// colour changes or the gap is wider than a word space. Rotated text stays one piece per line.
    /// </summary>
    private static List<List<Piece>> Group(List<Piece> pieces)
    {
        var segments = new List<List<Piece>>();
        var upright = pieces.Where(p => p.Upright).OrderByDescending(p => p.Baseline).ToList();
        int start = 0;
        while (start < upright.Count)
        {
            var first = upright[start];
            int end = start + 1;
            while (end < upright.Count && first.Baseline - upright[end].Baseline <= first.Em * 0.25)
                end++;
            List<Piece>? current = null;
            foreach (var piece in upright.GetRange(start, end - start).OrderBy(p => p.Bounds.Left))
            {
                var last = current?[^1];
                bool joins = last is not null && piece.FontName == last.FontName && piece.Color == last.Color
                             && Math.Abs(piece.Em - last.Em) <= 0.5 && piece.Bounds.Left - last.Bounds.Right <= last.Em;
                if (!joins)
                    segments.Add(current = []);
                current!.Add(piece);
            }
            start = end;
        }
        segments.AddRange(pieces.Where(p => !p.Upright).Select(p => new List<Piece> { p }));
        return segments.Where(s => !string.IsNullOrWhiteSpace(Join(s))).ToList();
    }

    /// <summary>
    /// The line's text. A space is implied where pieces are clearly further apart than usual for
    /// this line, so letter-spaced headings ("R E S U M O" drawn letter by letter) stay one word.
    /// </summary>
    private static string Join(List<Piece> segment)
    {
        var gaps = segment.Zip(segment.Skip(1), (a, b) => b.Bounds.Left - a.Bounds.Right).Order().ToList();
        double typical = gaps.Count > 0 ? Math.Max(0, gaps[gaps.Count / 2]) : 0;
        var text = new System.Text.StringBuilder(segment[0].Text);
        for (int i = 1; i < segment.Count; i++)
        {
            var (previous, piece) = (segment[i - 1], segment[i]);
            bool spaced = char.IsWhiteSpace(previous.Text[^1]) || char.IsWhiteSpace(piece.Text[0]);
            double gap = piece.Bounds.Left - previous.Bounds.Right;
            if (!spaced && gap > previous.Em * 0.2 && gap > typical * 2 + previous.Em * 0.05)
                text.Append(' ');
            text.Append(piece.Text);
        }
        return text.ToString().Trim();
    }

    /// <summary>Bounds of every object on the page (text, drawings, images): things a line must not run into.</summary>
    private static List<(IntPtr Handle, PdfRect Bounds)> Obstacles(IntPtr page)
    {
        var obstacles = new List<(IntPtr, PdfRect)>();
        int count = FPDFPage_CountObjects(page);
        for (int i = 0; i < count; i++)
        {
            IntPtr obj = FPDFPage_GetObject(page, i);
            obstacles.Add((obj, Bounds(obj)));
        }
        return obstacles;
    }

    /// <summary>Where the next item on the line starts, or the page's right margin (mirroring the left one).</summary>
    private double RightLimit(List<Piece> segment, List<(IntPtr Handle, PdfRect Bounds)> obstacles, int page, double textLeft)
    {
        var bounds = Union(segment);
        var own = segment.Select(p => p.Handle).ToHashSet();
        double limit = _pageSizes[page].Width - Math.Max(0, textLeft);
        foreach (var (handle, rect) in obstacles)
        {
            if (own.Contains(handle) || rect.Left < bounds.Right - 0.5)
                continue;
            double overlap = Math.Min(rect.Top, bounds.Top) - Math.Max(rect.Bottom, bounds.Bottom);
            if (overlap >= bounds.Height * 0.5)
                limit = Math.Min(limit, rect.Left);
        }
        return limit;
    }

    private static PdfRect Bounds(IntPtr obj) =>
        FPDFPageObj_GetBounds(obj, out float l, out float b, out float r, out float t) != 0 ? new(l, b, r, t) : default;

    private static PdfRect Union(List<Piece> segment) => new(
        segment.Min(p => p.Bounds.Left), segment.Min(p => p.Bounds.Bottom),
        segment.Max(p => p.Bounds.Right), segment.Max(p => p.Bounds.Top));

    /// <summary>"BAAAAA+Arial-BoldMT" → "Arial-BoldMT".</summary>
    private static string Display(string fontName) =>
        fontName.Length > 7 && fontName[6] == '+' ? fontName[7..] : fontName;

    /// <summary>Text compared without any whitespace (PDF writers space words differently).</summary>
    private static string Normalize(string text) => new(text.Where(c => !char.IsWhiteSpace(c) && c != '\0').ToArray());
}
