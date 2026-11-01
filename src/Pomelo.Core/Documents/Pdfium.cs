using System.Runtime.InteropServices;

namespace Pomelo.Core.Documents;

/// <summary>
/// The parts of PDFium's C API that the text editor uses. PDFium is not thread-safe: every call
/// must hold <see cref="Sync"/>.
/// </summary>
internal static unsafe partial class Pdfium
{
    private const string Library = "pdfium";

    public static readonly Lock Sync = new();
    private static bool _initialized;

    /// <summary>Initializes the library once; call while holding <see cref="Sync"/>.</summary>
    public static void EnsureInitialized()
    {
        if (_initialized)
            return;
        FPDF_InitLibrary();
        _initialized = true;
    }

    public const int ErrPassword = 4, ErrSecurity = 5;
    public const int PageObjText = 1;
    public const int TextRenderInvisible = 3;
    public const int FontTypeTrueType = 2;
    public const int BitmapBgra = 4;
    public const int RenderAnnotations = 0x01;
    public const uint NoIncremental = 1 << 1;

    [StructLayout(LayoutKind.Sequential)]
    public struct Matrix { public float A, B, C, D, E, F; }

    [StructLayout(LayoutKind.Sequential)]
    public struct FileWrite
    {
        public int Version;
        public delegate* unmanaged<FileWrite*, byte*, uint, int> WriteBlock;
    }

    [LibraryImport(Library)] private static partial void FPDF_InitLibrary();
    [LibraryImport(Library)] public static partial uint FPDF_GetLastError();
    [LibraryImport(Library)] public static partial IntPtr FPDF_LoadMemDocument64(void* data, nuint size, byte* password);
    [LibraryImport(Library)] public static partial void FPDF_CloseDocument(IntPtr document);
    [LibraryImport(Library)] public static partial int FPDF_GetPageCount(IntPtr document);
    [LibraryImport(Library)] public static partial int FPDF_GetSecurityHandlerRevision(IntPtr document);
    [LibraryImport(Library)] public static partial int FPDF_GetSignatureCount(IntPtr document);
    [LibraryImport(Library)] public static partial IntPtr FPDF_LoadPage(IntPtr document, int index);
    [LibraryImport(Library)] public static partial void FPDF_ClosePage(IntPtr page);
    [LibraryImport(Library)] public static partial float FPDF_GetPageWidthF(IntPtr page);
    [LibraryImport(Library)] public static partial float FPDF_GetPageHeightF(IntPtr page);
    [LibraryImport(Library)] public static partial int FPDF_PageToDevice(IntPtr page, int startX, int startY, int sizeX, int sizeY,
        int rotate, double pageX, double pageY, out int deviceX, out int deviceY);

    [LibraryImport(Library)] public static partial IntPtr FPDFText_LoadPage(IntPtr page);
    [LibraryImport(Library)] public static partial void FPDFText_ClosePage(IntPtr textPage);
    [LibraryImport(Library)] public static partial int FPDFText_CountChars(IntPtr textPage);
    [LibraryImport(Library)] public static partial uint FPDFText_GetUnicode(IntPtr textPage, int index);
    [LibraryImport(Library)] public static partial int FPDFText_IsGenerated(IntPtr textPage, int index);
    [LibraryImport(Library)] public static partial IntPtr FPDFText_GetTextObject(IntPtr textPage, int index);

    [LibraryImport(Library)] public static partial int FPDFPage_CountObjects(IntPtr page);
    [LibraryImport(Library)] public static partial IntPtr FPDFPage_GetObject(IntPtr page, int index);
    [LibraryImport(Library)] public static partial int FPDFPage_RemoveObject(IntPtr page, IntPtr pageObject);
    [LibraryImport(Library)] public static partial void FPDFPage_InsertObject(IntPtr page, IntPtr pageObject);
    [LibraryImport(Library)] public static partial int FPDFPage_GenerateContent(IntPtr page);
    [LibraryImport(Library)] public static partial int FPDFPageObj_GetType(IntPtr pageObject);
    [LibraryImport(Library)] public static partial void FPDFPageObj_Destroy(IntPtr pageObject);
    [LibraryImport(Library)] public static partial int FPDFPageObj_GetBounds(IntPtr pageObject, out float left, out float bottom, out float right, out float top);
    [LibraryImport(Library)] public static partial int FPDFPageObj_GetMatrix(IntPtr pageObject, out Matrix matrix);
    [LibraryImport(Library)] public static partial int FPDFPageObj_SetMatrix(IntPtr pageObject, in Matrix matrix);
    [LibraryImport(Library)] public static partial int FPDFPageObj_GetFillColor(IntPtr pageObject, out uint r, out uint g, out uint b, out uint a);
    [LibraryImport(Library)] public static partial int FPDFPageObj_SetFillColor(IntPtr pageObject, uint r, uint g, uint b, uint a);
    [LibraryImport(Library)] public static partial IntPtr FPDFPageObj_CreateTextObj(IntPtr document, IntPtr font, float size);

    [LibraryImport(Library)] public static partial uint FPDFTextObj_GetText(IntPtr textObject, IntPtr textPage, char* buffer, uint length);
    [LibraryImport(Library)] public static partial int FPDFText_SetText(IntPtr textObject, char* text);
    [LibraryImport(Library)] public static partial int FPDFTextObj_GetFontSize(IntPtr textObject, out float size);
    [LibraryImport(Library)] public static partial int FPDFTextObj_GetTextRenderMode(IntPtr textObject);
    [LibraryImport(Library)] public static partial IntPtr FPDFTextObj_GetFont(IntPtr textObject);
    [LibraryImport(Library)] public static partial nuint FPDFFont_GetBaseFontName(IntPtr font, byte* buffer, nuint length);
    [LibraryImport(Library)] public static partial int FPDFFont_GetIsEmbedded(IntPtr font);
    [LibraryImport(Library)] public static partial int FPDFFont_GetFontData(IntPtr font, byte* buffer, nuint length, out nuint written);
    [LibraryImport(Library)] public static partial int FPDFFont_GetWeight(IntPtr font);
    [LibraryImport(Library)] public static partial int FPDFFont_GetItalicAngle(IntPtr font, out int angle);
    [LibraryImport(Library)] public static partial IntPtr FPDFText_LoadFont(IntPtr document, byte* data, uint size, int fontType, int cid);
    [LibraryImport(Library)] public static partial void FPDFFont_Close(IntPtr font);

    [LibraryImport(Library)] public static partial IntPtr FPDFBitmap_CreateEx(int width, int height, int format, void* firstScan, int stride);
    [LibraryImport(Library)] public static partial int FPDFBitmap_FillRect(IntPtr bitmap, int left, int top, int width, int height, uint color);
    [LibraryImport(Library)] public static partial void FPDFBitmap_Destroy(IntPtr bitmap);
    [LibraryImport(Library)] public static partial void FPDF_RenderPageBitmap(IntPtr bitmap, IntPtr page, int startX, int startY, int sizeX, int sizeY, int rotate, int flags);

    [LibraryImport(Library)] public static partial int FPDF_SaveAsCopy(IntPtr document, FileWrite* writer, uint flags);

    /// <summary>Text of a text object; empty when it has none.</summary>
    public static string GetText(IntPtr textObject, IntPtr textPage)
    {
        uint bytes = FPDFTextObj_GetText(textObject, textPage, null, 0);
        if (bytes <= 2)
            return "";
        var buffer = new char[bytes / 2];
        fixed (char* b = buffer)
            FPDFTextObj_GetText(textObject, textPage, b, bytes);
        return new string(buffer, 0, buffer.Length - 1); // Drop the terminator.
    }

    public static void SetText(IntPtr textObject, string text)
    {
        fixed (char* t = text + "\0")
            FPDFText_SetText(textObject, t);
    }

    /// <summary>
    /// The text each object really contains, in page order. Unlike <see cref="GetText"/>, this
    /// leaves out the spaces and line breaks PDFium infers from gaps (e.g. between letter-spaced
    /// letters), so the caller can decide where words end.
    /// </summary>
    public static Dictionary<IntPtr, string> TextByObject(IntPtr textPage)
    {
        var texts = new Dictionary<IntPtr, System.Text.StringBuilder>();
        int count = FPDFText_CountChars(textPage);
        for (int i = 0; i < count; i++)
        {
            if (FPDFText_IsGenerated(textPage, i) != 0)
                continue;
            IntPtr obj = FPDFText_GetTextObject(textPage, i);
            uint unicode = FPDFText_GetUnicode(textPage, i);
            if (obj == IntPtr.Zero || unicode == 0)
                continue;
            if (!texts.TryGetValue(obj, out var text))
                texts[obj] = text = new();
            if (unicode <= 0xFFFF)
                text.Append((char)unicode);
            else if (unicode <= 0x10FFFF)
                text.Append(char.ConvertFromUtf32((int)unicode));
        }
        return texts.ToDictionary(t => t.Key, t => t.Value.ToString());
    }

    public static string BaseFontName(IntPtr font)
    {
        nuint length = FPDFFont_GetBaseFontName(font, null, 0);
        if (length <= 1)
            return "";
        var buffer = new byte[(int)length];
        fixed (byte* b = buffer)
            FPDFFont_GetBaseFontName(font, b, length);
        return System.Text.Encoding.UTF8.GetString(buffer, 0, buffer.Length - 1);
    }

    /// <summary>Writes the whole document (not incrementally) to <paramref name="output"/>.</summary>
    public static bool Save(IntPtr document, Stream output)
    {
        var handle = GCHandle.Alloc(output);
        try
        {
            var writer = new SaveWriter { Base = new FileWrite { Version = 1, WriteBlock = &Write }, Stream = GCHandle.ToIntPtr(handle) };
            return FPDF_SaveAsCopy(document, &writer.Base, NoIncremental) != 0;
        }
        finally
        {
            handle.Free();
        }
    }

    /// <summary>PDFium passes back the FileWrite pointer; the stream handle sits right after it.</summary>
    [StructLayout(LayoutKind.Sequential)]
    private struct SaveWriter
    {
        public FileWrite Base;
        public IntPtr Stream;
    }

    [UnmanagedCallersOnly]
    private static int Write(FileWrite* self, byte* data, uint size)
    {
        try
        {
            var stream = (Stream)GCHandle.FromIntPtr(((SaveWriter*)self)->Stream).Target!;
            stream.Write(new ReadOnlySpan<byte>(data, checked((int)size)));
            return 1;
        }
        catch (IOException)
        {
            return 0; // Disk full etc.: PDFium reports the save as failed.
        }
    }
}
