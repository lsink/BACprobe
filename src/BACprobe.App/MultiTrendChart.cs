using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using BACprobe.Core.Trends;

namespace BACprobe.App;

/// <summary>
/// Several points on one chart. With one shared unit the axis shows real values; with mixed units each line spans its own range and
/// the axis shows "% of range" (the legend and hover give the real values). Hover shows every line's value at that moment.
/// </summary>
public sealed class MultiTrendChart : Control
{
    private const double LeftMargin = 70, RightMargin = 16, TopMargin = 14, BottomMargin = 30;

    /// <summary>Line colours that read on both the light and the dark background.</summary>
    public static readonly Color[] Palette =
    [
        Color.FromRgb(0x3C, 0x9B, 0xFF), Color.FromRgb(0xF2, 0x8C, 0x28), Color.FromRgb(0x3B, 0xB2, 0x73), Color.FromRgb(0xD6, 0x5D, 0xB1),
        Color.FromRgb(0xC9, 0xA2, 0x27), Color.FromRgb(0x2B, 0xB3, 0xC0), Color.FromRgb(0xE0, 0x4F, 0x4F), Color.FromRgb(0x8E, 0x7C, 0xE8),
    ];

    public static readonly DependencyProperty TrendProperty = DependencyProperty.Register(
        nameof(Trend), typeof(MultiTrend), typeof(MultiTrendChart), new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

    public MultiTrend? Trend
    {
        get => (MultiTrend?)GetValue(TrendProperty);
        set => SetValue(TrendProperty, value);
    }

    private double _hoverX = -1;

    /// <summary>Called by the window after each sample: the trend changes in place, so the chart is told to redraw.</summary>
    public void Refresh() => InvalidateVisual();

    protected override void OnRender(DrawingContext drawingContext)
    {
        var dc = drawingContext;
        var w = ActualWidth;
        var h = ActualHeight;
        dc.DrawRectangle(Brushes.Transparent, null, new Rect(0, 0, w, h));
        var plot = new Rect(LeftMargin, TopMargin, Math.Max(0, w - LeftMargin - RightMargin), Math.Max(0, h - TopMargin - BottomMargin));
        if (plot.Width < 40 || plot.Height < 40) return;

        var text = Foreground ?? Brushes.Gray;
        var faint = text.CloneCurrentValue();
        faint.Opacity = 0.22;
        var gridPen = new Pen(faint, 1);

        var trend = Trend;
        var lines = trend?.Series.Select(s => s.Trend.Series).ToList() ?? [];
        if (trend is null || lines.All(l => l.Count == 0))
        {
            Label(dc, "Waiting for the first samples...", text, new Point(plot.Left + plot.Width / 2, plot.Top + plot.Height / 2), center: true, middle: true);
            return;
        }

        var all = lines.SelectMany(l => l).ToList();
        DateTime t0 = all.Min(p => p.Time), t1 = all.Max(p => p.Time);
        var span = Math.Max(1, (t1 - t0).TotalSeconds);
        double X(DateTime t) => plot.Left + (t - t0).TotalSeconds / span * plot.Width;

        // One real axis when the units match; otherwise each line on its own 0-100 % of range.
        var shared = trend.SharedAxis;
        var sharedRange = MultiTrend.RangeOf(all.Select(p => p.Value));
        var ranges = lines.Select(l => shared ? sharedRange : MultiTrend.RangeOf(l.Select(p => p.Value))).ToList();
        double Y(int line, double v) => plot.Bottom - MultiTrend.Normalise(v, ranges[line].Min, ranges[line].Max) * plot.Height;

        if (shared)
        {
            foreach (var v in TrendStats.NiceTicks(sharedRange.Min, sharedRange.Max))
            {
                var y = plot.Bottom - MultiTrend.Normalise(v, sharedRange.Min, sharedRange.Max) * plot.Height;
                dc.DrawLine(gridPen, new Point(plot.Left, y), new Point(plot.Right, y));
                Label(dc, v.ToString("0.##", CultureInfo.InvariantCulture), text, new Point(plot.Left - 8, y), right: true, middle: true);
            }
            if (trend.Series[0].Units is { Length: > 0 } u) Label(dc, u, text, new Point(4, 0));
        }
        else
        {
            foreach (var pct in new[] { 0, 25, 50, 75, 100 })
            {
                var y = plot.Bottom - pct / 100.0 * plot.Height;
                dc.DrawLine(gridPen, new Point(plot.Left, y), new Point(plot.Right, y));
                Label(dc, $"{pct} %", text, new Point(plot.Left - 8, y), right: true, middle: true); // "% of each line's range": the window says so
            }
        }

        var longSpan = span > 86_400;
        string F(DateTime t) => t.ToString(longSpan ? "MM-dd HH:mm" : "HH:mm:ss", CultureInfo.InvariantCulture);
        Label(dc, F(t0), text, new Point(plot.Left, plot.Bottom + 6));
        Label(dc, F(t1), text, new Point(plot.Right, plot.Bottom + 6), right: true);

        for (var i = 0; i < lines.Count; i++)
        {
            var points = TrendStats.Downsample(lines[i], Math.Max(50, (int)plot.Width));
            if (points.Count == 0) continue;
            var binary = points.All(p => p.Value is 0 or 1);
            var geometry = new StreamGeometry();
            using (var g = geometry.Open())
            {
                var prevY = Y(i, points[0].Value);
                g.BeginFigure(new Point(X(points[0].Time), prevY), false, false);
                for (var k = 1; k < points.Count; k++)
                {
                    var y = Y(i, points[k].Value);
                    if (binary) g.LineTo(new Point(X(points[k].Time), prevY), true, false); // hold the old state until the change
                    g.LineTo(new Point(X(points[k].Time), y), true, false);
                    prevY = y;
                }
            }
            geometry.Freeze();
            dc.DrawGeometry(null, new Pen(new SolidColorBrush(Palette[i % Palette.Length]), 1.6) { LineJoin = PenLineJoin.Round }, geometry);
        }

        // Hover: every line's real value at the nearest sampling moment.
        if (_hoverX >= plot.Left && _hoverX <= plot.Right)
        {
            var at = t0.AddSeconds((_hoverX - plot.Left) / plot.Width * span);
            dc.DrawLine(new Pen(faint, 1) { DashStyle = DashStyles.Dash }, new Point(_hoverX, plot.Top), new Point(_hoverX, plot.Bottom));
            var rows = new List<(Color Color, string Text)> { (Colors.Transparent, at.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture)) };
            for (var i = 0; i < lines.Count; i++)
            {
                if (lines[i].Count == 0) continue;
                var nearest = lines[i].MinBy(p => Math.Abs((p.Time - at).TotalSeconds));
                var s = trend.Series[i];
                rows.Add((Palette[i % Palette.Length], $"{s.Label}: {nearest.Value.ToString("0.##", CultureInfo.InvariantCulture)}{(s.Units.Length == 0 ? "" : " " + s.Units)}"));
                dc.DrawEllipse(new SolidColorBrush(Palette[i % Palette.Length]), null, new Point(X(nearest.Time), Y(i, nearest.Value)), 3.5, 3.5);
            }
            var texts = rows.Select(r => Format(r.Text, text)).ToList();
            var boxW = texts.Max(t => t.Width) + 30;
            var boxH = texts.Sum(t => t.Height) + 10;
            var bx = Math.Clamp(_hoverX + 12, plot.Left, Math.Max(plot.Left, plot.Right - boxW));
            dc.DrawRoundedRectangle(ReadoutBackground(text), new Pen(faint, 1), new Rect(bx, plot.Top + 4, boxW, boxH), 4, 4);
            var y0 = plot.Top + 9;
            for (var r = 0; r < rows.Count; r++)
            {
                if (rows[r].Color != Colors.Transparent) dc.DrawRectangle(new SolidColorBrush(rows[r].Color), null, new Rect(bx + 8, y0 + 5, 10, 3));
                dc.DrawText(texts[r], new Point(bx + 22, y0));
                y0 += texts[r].Height;
            }
        }
    }

    private static SolidColorBrush ReadoutBackground(Brush text)
    {
        var textIsLight = text is not SolidColorBrush { Color: var c } || (0.299 * c.R + 0.587 * c.G + 0.114 * c.B) > 140;
        return new SolidColorBrush(textIsLight ? Color.FromRgb(0x2B, 0x2B, 0x2B) : Color.FromRgb(0xF6, 0xF6, 0xF6));
    }

    private FormattedText Format(string s, Brush brush) =>
        new(s, CultureInfo.CurrentCulture, FlowDirection.LeftToRight, new Typeface("Segoe UI"), 12, brush, VisualTreeHelper.GetDpi(this).PixelsPerDip);

    /// <param name="middle">Centre the text on the point vertically (value labels on their gridline); otherwise it hangs from it.</param>
    private void Label(DrawingContext dc, string s, Brush brush, Point at, bool right = false, bool center = false, bool middle = false)
    {
        var ft = Format(s, brush);
        var x = right ? at.X - ft.Width : center ? at.X - ft.Width / 2 : at.X;
        dc.DrawText(ft, new Point(x, middle ? at.Y - ft.Height / 2 : at.Y));
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        _hoverX = e.GetPosition(this).X;
        InvalidateVisual();
    }

    protected override void OnMouseLeave(MouseEventArgs e)
    {
        base.OnMouseLeave(e);
        _hoverX = -1;
        InvalidateVisual();
    }
}
