using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Pomelo.Core.Conversion;
using Pomelo.Core.Documents;

namespace Pomelo.PdfEditor;

/// <summary>
/// Lists every line of a PDF for editing beside a live preview of the edited pages. Saving
/// writes "name (editado).pdf"; the original is never touched.
/// </summary>
internal sealed partial class PdfEditorWindow : Window
{
    private static readonly Brush SelectedFill = Frozen(new SolidColorBrush(Color.FromArgb(0x30, 0x2F, 0x6F, 0xEB)));
    private static readonly Brush SelectedEdge = Frozen(new SolidColorBrush(Color.FromRgb(0x2F, 0x6F, 0xEB)));
    private static readonly Brush ChangedEdge = Frozen(new SolidColorBrush(Color.FromArgb(0xC0, 0x1E, 0x9E, 0x55)));
    private static readonly Brush WarningEdge = Frozen(new SolidColorBrush(Color.FromRgb(0xE0, 0x7A, 0x00)));

    private readonly PdfTextDocument _document;
    private readonly Func<string> _outputDirectory;
    private readonly ObservableCollection<LineItem> _items;
    private readonly ICollectionView _view;
    private readonly DispatcherTimer _resize;
    private Dictionary<PdfTextLine, PdfLineEdit> _edits = [];
    private int _page;
    private int _pixelWidth, _pixelHeight;
    private bool _saved;

    /// <summary>Raised with the created PDF after a successful save, just before the window closes.</summary>
    public event Action<string>? Saved;

    /// <param name="document">Owned by the window from now on; disposed when it closes.</param>
    /// <param name="outputDirectory">Where "(editado)" copies go, read at save time.</param>
    public PdfEditorWindow(PdfTextDocument document, Func<string> outputDirectory)
    {
        InitializeComponent();
        _document = document;
        _outputDirectory = outputDirectory;
        Title = $"Editar textos — {Path.GetFileName(document.SourcePath)}";
        FileText.Text = Path.GetFileName(document.SourcePath);
        FileText.ToolTip = document.SourcePath;

        // Separators and bullets ("|", "·", "•") stay in the PDF but would only clutter the list.
        _items = new(document.Lines.Where(l => l.Text.Any(char.IsLetterOrDigit)).Select(l => new LineItem(l, document.PageCount > 1)));
        foreach (var item in _items)
            item.PropertyChanged += OnItemChanged;
        LineList.ItemsSource = _items;
        _view = CollectionViewSource.GetDefaultView(_items);

        _resize = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(120) };
        _resize.Tick += (_, _) =>
        {
            _resize.Stop();
            RenderPage();
        };
        PageScroll.SizeChanged += (_, _) => { _resize.Stop(); _resize.Start(); };

        PreviousPage.Click += (_, _) => ShowPage(_page - 1);
        NextPage.Click += (_, _) => ShowPage(_page + 1);
        SearchBox.TextChanged += (_, _) =>
        {
            SearchHint.Visibility = SearchBox.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
            Filter();
        };
        ReplaceAllButton.Click += (_, _) => ReplaceAll();
        LineList.SelectionChanged += (_, _) => OnSelected();
        Overlay.MouseLeftButtonDown += OnPageClicked;
        SaveButton.Click += (_, _) => Save();
        CloseButton.Click += (_, _) => Close();
        Closing += OnClosing;
        Closed += (_, _) => _document.Dispose();

        UpdateStatus();
        Loaded += (_, _) => ShowPage(0);
    }

    // ---- Editing ----

    private void OnItemChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(LineItem.Text))
            ApplyEdits();
    }

    private void ApplyEdits()
    {
        var results = _document.Apply(_items.Where(i => i.Changed).Select(i => (i.Line, i.Text)));
        _edits = results.ToDictionary(r => r.Line);
        foreach (var item in _items)
            item.Warning = _edits.TryGetValue(item.Line, out var edit) ? Describe(edit) : null;
        UpdateStatus();
        RenderPage();
    }

    private static string? Describe(PdfLineEdit edit)
    {
        var notes = new List<string>();
        if (edit.Issues.HasFlag(PdfEditIssues.SystemFont))
            notes.Add($"O PDF não tem algumas destas letras: a linha foi escrita em {edit.SystemFontName} do Windows (o arquivo fica maior).");
        if (edit.Issues.HasFlag(PdfEditIssues.MissingLetters))
            notes.Add("Algumas letras não existem em nenhuma fonte disponível e vão faltar.");
        if (edit.Issues.HasFlag(PdfEditIssues.Overflow))
            notes.Add("O texto passou do espaço da linha e pode ficar por cima do que vem ao lado.");
        return notes.Count == 0 ? null : string.Join(" ", notes);
    }

    private void OnRevert(object sender, RoutedEventArgs e)
    {
        if (((FrameworkElement)sender).Tag is LineItem item)
            item.Text = item.Line.Text;
    }

    private void OnLineKeyDown(object sender, KeyEventArgs e)
    {
        var box = (TextBox)sender;
        if (e.Key == Key.Enter)
        {
            box.GetBindingExpression(TextBox.TextProperty)?.UpdateSource();
            e.Handled = true;
        }
        else if (e.Key == Key.Escape && box.DataContext is LineItem item)
        {
            box.Text = item.Text; // Drop what was typed since the last commit.
            e.Handled = true;
        }
    }

    private void OnLineFocused(object sender, KeyboardFocusChangedEventArgs e)
    {
        if (((FrameworkElement)sender).DataContext is LineItem item && !Equals(LineList.SelectedItem, item))
            LineList.SelectedItem = item;
    }

    private void ReplaceAll()
    {
        string find = FindBox.Text;
        if (find.Length == 0)
            return;
        var comparison = MatchCaseBox.IsChecked == true ? StringComparison.Ordinal : StringComparison.CurrentCultureIgnoreCase;
        int changed = 0;
        foreach (var item in _items)
        {
            item.PropertyChanged -= OnItemChanged; // One Apply for the whole batch.
            string replaced = item.Text.Replace(find, ReplaceBox.Text, comparison);
            if (replaced != item.Text)
            {
                item.Text = replaced;
                changed++;
            }
            item.PropertyChanged += OnItemChanged;
        }
        if (changed > 0)
            ApplyEdits();
        StatusText.Text = changed switch
        {
            0 => $"\"{find}\" não foi encontrado (a busca não atravessa de uma linha para outra).",
            1 => "Substituído em 1 linha.",
            _ => $"Substituído em {changed} linhas.",
        };
    }

    private void Filter()
    {
        string query = SearchBox.Text.Trim();
        var compare = CultureInfo.CurrentCulture.CompareInfo;
        const CompareOptions loose = CompareOptions.IgnoreCase | CompareOptions.IgnoreNonSpace;
        _view.Filter = query.Length == 0
            ? null
            : o => o is LineItem item && (compare.IndexOf(item.Text, query, loose) >= 0 || compare.IndexOf(item.Line.Text, query, loose) >= 0);
    }

    private void UpdateStatus()
    {
        int changed = _items.Count(i => i.Changed);
        int warnings = _items.Count(i => i.HasWarning);
        StatusText.Text = changed == 0
            ? $"{_items.Count} linhas em {_document.PageCount} {(_document.PageCount == 1 ? "página" : "páginas")}. O original não é alterado."
            : $"{changed} {(changed == 1 ? "linha alterada" : "linhas alteradas")}" + (warnings > 0 ? $" · {warnings} com aviso" : "");
        SaveButton.IsEnabled = changed > 0;
    }

    // ---- Preview ----

    private void ShowPage(int page)
    {
        _page = Math.Clamp(page, 0, _document.PageCount - 1);
        PageText.Text = $"Página {_page + 1} de {_document.PageCount}";
        PreviousPage.IsEnabled = _page > 0;
        NextPage.IsEnabled = _page < _document.PageCount - 1;
        RenderPage();
    }

    private void RenderPage()
    {
        double available = PageScroll.ViewportWidth > 0 ? PageScroll.ViewportWidth : PageScroll.ActualWidth;
        if (available <= 40)
            return;
        var (pageWidth, pageHeight) = _document.PageSize(_page);
        double width = Math.Min(available - 32, 1100);
        double height = width * pageHeight / pageWidth;
        double scale = VisualTreeHelper.GetDpi(this).PixelsPerDip;
        _pixelWidth = Math.Max(1, (int)Math.Round(width * scale));
        _pixelHeight = Math.Max(1, (int)Math.Round(height * scale));

        byte[] pixels = _document.Render(_page, _pixelWidth, _pixelHeight);
        var bitmap = BitmapSource.Create(_pixelWidth, _pixelHeight, 96 * scale, 96 * scale, PixelFormats.Bgra32, null, pixels, _pixelWidth * 4);
        bitmap.Freeze();
        PageImage.Source = bitmap;
        PageHost.Width = Overlay.Width = width;
        PageHost.Height = Overlay.Height = height;
        DrawOverlay();
    }

    private void DrawOverlay()
    {
        Overlay.Children.Clear();
        if (_pixelWidth == 0)
            return;
        var selected = LineList.SelectedItem as LineItem;
        foreach (var item in _items.Where(i => i.Line.Page == _page && (i.Changed || i == selected)))
        {
            var rect = PageRect(item);
            if (rect.IsEmpty)
                continue;
            var box = new System.Windows.Shapes.Rectangle
            {
                Width = rect.Width + 4, Height = rect.Height + 4, StrokeThickness = item == selected ? 2 : 1.2,
                Stroke = item == selected ? SelectedEdge : item.HasWarning ? WarningEdge : ChangedEdge,
                Fill = item == selected ? SelectedFill : Brushes.Transparent, RadiusX = 2, RadiusY = 2,
                IsHitTestVisible = false,
            };
            Canvas.SetLeft(box, rect.X - 2);
            Canvas.SetTop(box, rect.Y - 2);
            Overlay.Children.Add(box);
        }
    }

    /// <summary>Where the line is drawn now (its edited text, or the original), in overlay units.</summary>
    private Rect PageRect(LineItem item)
    {
        var bounds = item.Line.Bounds; // Deleted lines still show where they were.
        if (_edits.TryGetValue(item.Line, out var edit) && edit.Text.Length > 0)
            bounds = edit.Bounds;
        var (x, y, w, h) = _document.ToPixels(item.Line.Page, bounds, _pixelWidth, _pixelHeight);
        double toDip = Overlay.Width / _pixelWidth;
        return new Rect(x * toDip, y * toDip, Math.Max(2, w * toDip), Math.Max(2, h * toDip));
    }

    private void OnSelected()
    {
        if (LineList.SelectedItem is not LineItem item)
        {
            DrawOverlay();
            return;
        }
        if (item.Line.Page != _page)
            ShowPage(item.Line.Page);
        else
            DrawOverlay();
        // Scroll only when the line is out of view: clicking a visible line must not move the page under the cursor.
        var rect = PageRect(item);
        double top = rect.Y + PageHost.Margin.Top, bottom = top + rect.Height;
        if (top < PageScroll.VerticalOffset || bottom > PageScroll.VerticalOffset + PageScroll.ViewportHeight)
            PageScroll.ScrollToVerticalOffset(Math.Max(0, top - PageScroll.ViewportHeight / 3));
    }

    private void OnPageClicked(object sender, MouseButtonEventArgs e)
    {
        Point point = e.GetPosition(Overlay);
        var hit = _items.Where(i => i.Line.Page == _page)
            .Select(i => (Item: i, Rect: PageRect(i)))
            .Where(c => { var r = c.Rect; r.Inflate(3, 3); return r.Contains(point); })
            .OrderBy(c => c.Rect.Width * c.Rect.Height)
            .Select(c => c.Item)
            .FirstOrDefault();
        if (hit is null)
            return;
        SearchBox.Text = ""; // The line must be visible in the list.
        LineList.SelectedItem = hit;
        LineList.ScrollIntoView(hit);
        Dispatcher.BeginInvoke(() =>
        {
            if (LineList.ItemContainerGenerator.ContainerFromItem(hit) is ListBoxItem container
                && FindChild<TextBox>(container) is { } box)
            {
                box.Focus();
                box.CaretIndex = box.Text.Length;
            }
        }, DispatcherPriority.Loaded);
    }

    private static T? FindChild<T>(DependencyObject parent) where T : DependencyObject
    {
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
        {
            var child = VisualTreeHelper.GetChild(parent, i);
            if (child is T match)
                return match;
            if (FindChild<T>(child) is { } nested)
                return nested;
        }
        return null;
    }

    // ---- Saving ----

    private void Save()
    {
        if (Keyboard.FocusedElement is TextBox box) // Commit a line still being typed.
            box.GetBindingExpression(TextBox.TextProperty)?.UpdateSource();
        try
        {
            Cursor = Cursors.Wait;
            string saved = _document.Save(_outputDirectory());
            _saved = true;
            Saved?.Invoke(saved);
            Close();
        }
        catch (ConversionException ex)
        {
            MessageBox.Show(this, ex.Message, "Não foi possível salvar", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            MessageBox.Show(this, ex.Message, "Não foi possível salvar", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        finally
        {
            Cursor = null;
        }
    }

    private void OnClosing(object? sender, CancelEventArgs e)
    {
        if (_saved || !_items.Any(i => i.Changed))
            return;
        var answer = MessageBox.Show(this, "As alterações ainda não foram salvas. Fechar mesmo assim?", "Editar textos",
            MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.No);
        e.Cancel = answer != MessageBoxResult.Yes;
    }

    private static T Frozen<T>(T freezable) where T : Freezable
    {
        freezable.Freeze();
        return freezable;
    }

    internal sealed class LineItem(PdfTextLine line, bool showPage) : INotifyPropertyChanged
    {
        private string _text = line.Text;
        private string? _warning;

        public PdfTextLine Line { get; } = line;

        public string Text
        {
            get => _text;
            set
            {
                if (_text == value)
                    return;
                _text = value;
                Notify();
                Notify(nameof(Changed));
                Notify(nameof(Header));
            }
        }

        public bool Changed => _text != Line.Text;

        public string Header => (showPage ? $"Página {Line.Page + 1} · " : "") + Line.FontName
                                + (Changed ? (Text.Trim().Length == 0 ? " · linha apagada" : " · alterada") : "");

        public string? Warning
        {
            get => _warning;
            set
            {
                if (_warning == value)
                    return;
                _warning = value;
                Notify();
                Notify(nameof(HasWarning));
            }
        }

        public bool HasWarning => _warning is not null;

        public event PropertyChangedEventHandler? PropertyChanged;

        private void Notify([CallerMemberName] string? name = null) => PropertyChanged?.Invoke(this, new(name));
    }
}
