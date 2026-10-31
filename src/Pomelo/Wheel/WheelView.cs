using System.Globalization;
using System.Windows;
using System.Windows.Media;

namespace Pomelo.Wheel;

/// <summary>Ring of target sectors around a hub. Sector 0 starts at 12 o'clock, clockwise.</summary>
internal sealed class WheelView : FrameworkElement
{
    private const double OuterInset = 8;
    private const double InnerRadius = 70;

    private static readonly Brush RingFill = Frozen(new SolidColorBrush(Color.FromArgb(0xF2, 0x1C, 0x1C, 0x1E)));
    private static readonly Brush HubFill = Frozen(new SolidColorBrush(Color.FromArgb(0xF7, 0x2A, 0x2A, 0x2D)));
    public static readonly Brush ConvertAccent = Frozen(new SolidColorBrush(Color.FromRgb(0xFF, 0x8A, 0x1F)));
    public static readonly Brush ToolsAccent = Frozen(new SolidColorBrush(Color.FromRgb(0x3C, 0xC7, 0xB5)));
    private static readonly Brush Text = Brushes.White;
    private static readonly Brush MutedText = Frozen(new SolidColorBrush(Color.FromArgb(0xB0, 0xFF, 0xFF, 0xFF)));
    private static readonly Pen Edge = Frozen(new Pen(Frozen(new SolidColorBrush(Color.FromArgb(0x40, 0xFF, 0xFF, 0xFF))), 1));
    private static readonly Typeface Face = new(new FontFamily("Segoe UI Variable Display, Segoe UI"),
        FontStyles.Normal, FontWeights.SemiBold, FontStretches.Normal);

    private IReadOnlyList<string> _labels = [];
    private int _hover = -1;
    private string _title = "", _subtitle = "";

    public IReadOnlyList<string> Labels { get => _labels; set { _labels = value; _hover = -1; InvalidateVisual(); } }

    /// <summary>Fill of the hovered sector; distinguishes the conversion and tools wheels.</summary>
    public Brush Highlight { get; set; } = ConvertAccent;

    public int Hover
    {
        get => _hover;
        set { if (_hover != value) { _hover = value; InvalidateVisual(); } }
    }

    public void SetHub(string title, string subtitle)
    {
        _title = title;
        _subtitle = subtitle;
        InvalidateVisual();
    }

    private Point Center => new(ActualWidth / 2, ActualHeight / 2);
    private double OuterRadius => Math.Min(ActualWidth, ActualHeight) / 2 - OuterInset;

    /// <returns>Sector index under <paramref name="point"/>, or -1 for the hub/outside.</returns>
    public int SectorAt(Point point)
    {
        if (_labels.Count == 0)
            return -1;
        Vector v = point - Center;
        double r = v.Length;
        if (r < InnerRadius || r > OuterRadius)
            return -1;
        double degrees = (Math.Atan2(v.Y, v.X) * 180 / Math.PI + 90 + 360) % 360;
        return Math.Min((int)(degrees / (360.0 / _labels.Count)), _labels.Count - 1);
    }

    protected override void OnRender(DrawingContext dc)
    {
        Point c = Center;
        double outer = OuterRadius;
        double ppd = VisualTreeHelper.GetDpi(this).PixelsPerDip;

        var ring = new CombinedGeometry(GeometryCombineMode.Exclude,
            new EllipseGeometry(c, outer, outer), new EllipseGeometry(c, InnerRadius, InnerRadius));
        dc.DrawGeometry(RingFill, Edge, ring);

        int n = _labels.Count;
        double sweep = n == 0 ? 0 : 360.0 / n;
        for (int i = 0; i < n; i++)
        {
            double start = i * sweep;
            if (i == _hover)
                dc.DrawGeometry(Highlight, null, n == 1 ? ring : Sector(c, InnerRadius + 3, outer - 3, start, sweep));
            if (n > 1)
                dc.DrawLine(Edge, Polar(c, InnerRadius, start), Polar(c, outer, start));

            var label = new FormattedText(_labels[i], CultureInfo.CurrentUICulture, FlowDirection.LeftToRight,
                Face, 17, i == _hover ? Brushes.Black : Text, ppd);
            Point mid = Polar(c, (InnerRadius + outer) / 2, start + sweep / 2);
            dc.DrawText(label, new Point(mid.X - label.Width / 2, mid.Y - label.Height / 2));
        }

        dc.DrawEllipse(HubFill, Edge, c, InnerRadius - 6, InnerRadius - 6);
        var title = new FormattedText(_title, CultureInfo.CurrentUICulture, FlowDirection.LeftToRight, Face, 15, Text, ppd)
        { MaxTextWidth = (InnerRadius - 14) * 2, TextAlignment = TextAlignment.Center };
        var subtitle = new FormattedText(_subtitle, CultureInfo.CurrentUICulture, FlowDirection.LeftToRight, Face, 12, MutedText, ppd)
        { MaxTextWidth = (InnerRadius - 14) * 2, TextAlignment = TextAlignment.Center };
        double top = c.Y - (title.Height + subtitle.Height) / 2;
        dc.DrawText(title, new Point(c.X - title.MaxTextWidth / 2, top));
        dc.DrawText(subtitle, new Point(c.X - subtitle.MaxTextWidth / 2, top + title.Height));
    }

    /// <summary>Angle 0 = 12 o'clock, clockwise.</summary>
    private static Point Polar(Point c, double r, double degrees)
    {
        double rad = (degrees - 90) * Math.PI / 180;
        return new Point(c.X + r * Math.Cos(rad), c.Y + r * Math.Sin(rad));
    }

    private static Geometry Sector(Point c, double inner, double outer, double start, double sweep)
    {
        bool large = sweep > 180;
        var figure = new PathFigure { StartPoint = Polar(c, inner, start), IsClosed = true };
        figure.Segments.Add(new LineSegment(Polar(c, outer, start), false));
        figure.Segments.Add(new ArcSegment(Polar(c, outer, start + sweep), new Size(outer, outer), 0, large, SweepDirection.Clockwise, false));
        figure.Segments.Add(new LineSegment(Polar(c, inner, start + sweep), false));
        figure.Segments.Add(new ArcSegment(Polar(c, inner, start), new Size(inner, inner), 0, large, SweepDirection.Counterclockwise, false));
        return new PathGeometry([figure]);
    }

    private static T Frozen<T>(T freezable) where T : Freezable
    {
        freezable.Freeze();
        return freezable;
    }
}
