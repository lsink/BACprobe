using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using BACprobe.Core.Trends;

namespace BACprobe.App;

/// <summary>
/// A plain line chart for trend data: gridlines, axis labels, and a hover read-out of the nearest sample.
/// On/off data (every value 0 or 1) is drawn as a step line and labelled Inactive/Active.
/// </summary>
public sealed class TrendChart : Control
{
    private const double LeftMargin = 70, RightMargin = 16, TopMargin = 14, BottomMargin = 30;
    private static readonly Brush LineBrush = Freeze(new SolidColorBrush(Color.FromRgb(0x3C, 0x9B, 0xFF)));

    public static readonly DependencyProperty PointsProperty = DependencyProperty.Register(
        nameof(Points), typeof(IReadOnlyList<(DateTime Time, double Value)>), typeof(TrendChart),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty UnitsProperty = DependencyProperty.Register(
        nameof(Units), typeof(string), typeof(TrendChart),
        new FrameworkPropertyMetadata("", FrameworkPropertyMetadataOptions.AffectsRender));

    public IReadOnlyList<(DateTime Time, double Value)>? Points
    {
        get => (IReadOnlyList<(DateTime, double)>?)GetValue(PointsProperty);
        set => SetValue(PointsProperty, value);
    }

    public string Units
    {
        get => (string)GetValue(UnitsProperty);
        set => SetValue(UnitsProperty, value);
    }

    // What was drawn last, so the mouse can be matched to a sample.
    private (double X, double Y, DateTime Time, double Value)[] _drawn = [];
    private int _hover = -1;

    private static Brush Freeze(Brush b)
    {
        b.Freeze();
        return b;
    }

    protected override void OnRender(DrawingContext drawingContext)
    {
        var dc = drawingContext;
        var w = ActualWidth;
        var h = ActualHeight;
        dc.DrawRectangle(Brushes.Transparent, null, new Rect(0, 0, w, h)); // so the mouse hits the whole control
        _drawn = [];

        var plot = new Rect(LeftMargin, TopMargin, Math.Max(0, w - LeftMargin - RightMargin), Math.Max(0, h - TopMargin - BottomMargin));
        if (plot.Width < 40 || plot.Height < 40) return;

        var text = Foreground ?? Brushes.Gray;
        var faint = text.CloneCurrentValue();
        faint.Opacity = 0.22;
        var gridPen = new Pen(faint, 1);

        var raw = Points;
        if (raw is null || raw.Count == 0)
        {
            Label(dc, "No data to show yet", text, new Point(plot.Left + plot.Width / 2, plot.Top + plot.Height / 2), center: true);
            return;
        }

        var points = TrendStats.Downsample(raw, Math.Max(50, (int)plot.Width));
        var binary = points.All(p => p.Value is 0 or 1);
        double min = points.Min(p => p.Value), max = points.Max(p => p.Value);
        if (binary) (min, max) = (0, 1);
        else
        {
            var pad = (max - min) * 0.08;
            if (pad < 1e-9) pad = Math.Max(1, Math.Abs(max) * 0.05);
            min -= pad;
            max += pad;
        }

        DateTime t0 = points[0].Time, t1 = points[^1].Time;
        var span = Math.Max(1, (t1 - t0).TotalSeconds);
        double X(DateTime t) => plot.Left + (t - t0).TotalSeconds / span * plot.Width;
        double Y(double v) => plot.Bottom - (v - min) / (max - min) * plot.Height;

        // Horizontal gridlines and value labels, at round numbers
        var ticks = binary ? [0.0, 1.0] : TrendStats.NiceTicks(min, max);
        foreach (var v in ticks)
        {
            var y = Y(v);
            dc.DrawLine(gridPen, new Point(plot.Left, y), new Point(plot.Right, y));
            var label = binary ? (v == 0 ? "Inactive" : "Active") : v.ToString("0.##", CultureInfo.InvariantCulture);
            Label(dc, label, text, new Point(plot.Left - 8, y), right: true);
        }
        if (!binary && !string.IsNullOrEmpty(Units)) Label(dc, Units, text, new Point(4, 0));

        // Time labels along the bottom
        var longSpan = span > 86_400;
        string F(DateTime t) => t.ToString(longSpan ? "MM-dd HH:mm" : "HH:mm:ss", CultureInfo.InvariantCulture);
        Label(dc, F(t0), text, new Point(plot.Left, plot.Bottom + 6));
        Label(dc, F(t0.AddSeconds(span / 2)), text, new Point(plot.Left + plot.Width / 2, plot.Bottom + 6), center: true);
        Label(dc, F(t1), text, new Point(plot.Right, plot.Bottom + 6), right: true);

        // The line
        var geometry = new StreamGeometry();
        using (var g = geometry.Open())
        {
            var prevY = Y(points[0].Value);
            g.BeginFigure(new Point(X(points[0].Time), prevY), false, false);
            for (var i = 1; i < points.Count; i++)
            {
                var x = X(points[i].Time);
                var y = Y(points[i].Value);
                if (binary) g.LineTo(new Point(x, prevY), true, false); // hold the old state until the change
                g.LineTo(new Point(x, y), true, false);
                prevY = y;
            }
        }
        geometry.Freeze();
        dc.DrawGeometry(null, new Pen(LineBrush, 1.6) { LineJoin = PenLineJoin.Round }, geometry);

        _drawn = [.. points.Select(p => (X(p.Time), Y(p.Value), p.Time, p.Value))];

        // Hover read-out
        if (_hover >= 0 && _hover < _drawn.Length)
        {
            var d = _drawn[_hover];
            dc.DrawLine(new Pen(faint, 1) { DashStyle = DashStyles.Dash }, new Point(d.X, plot.Top), new Point(d.X, plot.Bottom));
            dc.DrawEllipse(LineBrush, null, new Point(d.X, d.Y), 4, 4);
            var shown = binary ? (d.Value >= 1 ? "Active" : "Inactive") : d.Value.ToString("0.##", CultureInfo.InvariantCulture) + (string.IsNullOrEmpty(Units) ? "" : " " + Units);
            var tip = $"{d.Time:yyyy-MM-dd HH:mm:ss}   {shown}";
            var ft = Format(tip, text);
            var tx = Math.Clamp(d.X + 10, plot.Left, Math.Max(plot.Left, plot.Right - ft.Width - 12));
            dc.DrawRoundedRectangle(ReadoutBackground(text), new Pen(faint, 1), new Rect(tx - 6, plot.Top + 4, ft.Width + 12, ft.Height + 8), 4, 4);
            dc.DrawText(ft, new Point(tx, plot.Top + 8));
        }
    }

    /// <summary>
    /// A background for the hover read-out that contrasts with the text, whatever the theme: light text means a dark
    /// theme, so use a dark box; dark text means a light theme, so use a light box.
    /// </summary>
    private static Brush ReadoutBackground(Brush text)
    {
        var textIsLight = text is not SolidColorBrush { Color: var c } || (0.299 * c.R + 0.587 * c.G + 0.114 * c.B) > 140;
        return textIsLight ? DarkReadout : LightReadout;
    }

    private static readonly Brush DarkReadout = Freeze(new SolidColorBrush(Color.FromRgb(0x2B, 0x2B, 0x2B)));
    private static readonly Brush LightReadout = Freeze(new SolidColorBrush(Color.FromRgb(0xF6, 0xF6, 0xF6)));

    private FormattedText Format(string s, Brush brush) =>
        new(s, CultureInfo.CurrentCulture, FlowDirection.LeftToRight, new Typeface("Segoe UI"), 12, brush,
            VisualTreeHelper.GetDpi(this).PixelsPerDip);

    private void Label(DrawingContext dc, string s, Brush brush, Point at, bool right = false, bool center = false)
    {
        var ft = Format(s, brush);
        var x = right ? at.X - ft.Width : center ? at.X - ft.Width / 2 : at.X;
        // Value labels sit centred on their gridline; everything else hangs from the point.
        dc.DrawText(ft, new Point(x, right && at.Y > 0 ? at.Y - ft.Height / 2 : center && at.Y > 0 && at.Y < ActualHeight - 40 ? at.Y - ft.Height / 2 : at.Y));
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        if (_drawn.Length == 0) return;
        var x = e.GetPosition(this).X;
        var best = 0;
        var bestDist = double.MaxValue;
        for (var i = 0; i < _drawn.Length; i++)
        {
            var d = Math.Abs(_drawn[i].X - x);
            if (d < bestDist) { bestDist = d; best = i; }
        }
        if (best != _hover)
        {
            _hover = best;
            InvalidateVisual();
        }
    }

    protected override void OnMouseLeave(MouseEventArgs e)
    {
        base.OnMouseLeave(e);
        _hover = -1;
        InvalidateVisual();
    }
}
