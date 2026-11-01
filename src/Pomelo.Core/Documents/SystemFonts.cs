using Microsoft.Win32;

namespace Pomelo.Core.Documents;

/// <summary>
/// Finds the installed TrueType file for a PDF font name such as "BAAAAA+Arial-BoldMT" or
/// "TimesNewRomanPS-ItalicMT", so text that needs letters missing from the PDF can be written
/// in the same typeface. Falls back to Arial in the same style.
/// </summary>
internal static class SystemFonts
{
    private static readonly Lazy<Dictionary<string, string>> Installed = new(Load);

    /// <returns>The font file and a display name such as "Arial Bold".</returns>
    public static (string Path, string Name) Find(string pdfFontName, bool bold, bool italic)
    {
        string name = pdfFontName;
        if (name.Length > 7 && name[6] == '+' && name[..6].All(char.IsAsciiLetterUpper))
            name = name[7..]; // Subset prefix.
        int split = name.IndexOfAny(['-', ',']);
        string family = split >= 0 ? name[..split] : name;
        string style = split >= 0 ? name[(split + 1)..] : "";
        bold |= style.Contains("Bold", StringComparison.OrdinalIgnoreCase) || style.Contains("Black", StringComparison.OrdinalIgnoreCase);
        italic |= style.Contains("Italic", StringComparison.OrdinalIgnoreCase) || style.Contains("Oblique", StringComparison.OrdinalIgnoreCase);
        foreach (string suffix in (string[])["PSMT", "MT", "PS"])
            if (family.EndsWith(suffix, StringComparison.Ordinal) && family.Length > suffix.Length)
            {
                family = family[..^suffix.Length];
                break;
            }

        string variant = (bold, italic) switch { (true, true) => " Bold Italic", (true, false) => " Bold", (false, true) => " Italic", _ => "" };
        if (Installed.Value.TryGetValue(Key(family + variant), out string? file))
            return (file, Display(family) + variant);
        if (variant.Length == 0 && Installed.Value.TryGetValue(Key(family + " Regular"), out file))
            return (file, Display(family));

        string arial = (bold, italic) switch { (true, true) => "arialbi.ttf", (true, false) => "arialbd.ttf", (false, true) => "ariali.ttf", _ => "arial.ttf" };
        return (Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Fonts), arial), "Arial" + variant);
    }

    private static string Key(string name) => new string(name.Where(char.IsLetterOrDigit).ToArray()).ToLowerInvariant();

    /// <summary>"TimesNewRoman" → "Times New Roman".</summary>
    private static string Display(string family) =>
        string.Concat(family.Select((c, i) => i > 0 && char.IsUpper(c) && char.IsLower(family[i - 1]) ? " " + c : c.ToString()));

    /// <summary>Registry "Arial Bold (TrueType)" → arialbd.ttf, for machine-wide and per-user fonts. TrueType files only.</summary>
    private static Dictionary<string, string> Load()
    {
        var fonts = new Dictionary<string, string>();
        string fontsFolder = Environment.GetFolderPath(Environment.SpecialFolder.Fonts);
        foreach (var root in (RegistryKey[])[Registry.LocalMachine, Registry.CurrentUser])
        {
            using var key = root.OpenSubKey(@"SOFTWARE\Microsoft\Windows NT\CurrentVersion\Fonts");
            if (key is null)
                continue;
            foreach (string entry in key.GetValueNames())
            {
                if (key.GetValue(entry) is not string file || !file.EndsWith(".ttf", StringComparison.OrdinalIgnoreCase))
                    continue;
                string path = Path.IsPathRooted(file) ? file : Path.Combine(fontsFolder, file);
                string name = entry.Replace("(TrueType)", "").Replace("(OpenType)", "");
                fonts.TryAdd(Key(name), path);
            }
        }
        return fonts;
    }
}
