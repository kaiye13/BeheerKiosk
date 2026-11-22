using System.Globalization;
using System.Windows;
using System.Windows.Media;

namespace Boekhouding.Client.Views;

/// <summary>Ringdiagram voor het streamdoel: Shopify + TikTok als segmenten, rest grijs, percentage in het midden.</summary>
public class DonutChart : FrameworkElement
{
    public static readonly Brush ShopifyBrush = Freeze(new SolidColorBrush(Color.FromRgb(0x5E, 0x8E, 0x3E)));
    public static readonly Brush TikTokBrush = Freeze(new SolidColorBrush(Color.FromRgb(0xE1, 0x1D, 0x48)));
    private static readonly Brush TrackBrush = Freeze(new SolidColorBrush(Color.FromRgb(0xE5, 0xE7, 0xEB)));
    private static readonly Brush TextBrush = Freeze(new SolidColorBrush(Color.FromRgb(0x11, 0x18, 0x27)));
    private static readonly Brush MutedBrush = Freeze(new SolidColorBrush(Color.FromRgb(0x6B, 0x72, 0x80)));

    private decimal _shopify, _tiktok, _goal = 1;

    public void SetValues(decimal shopify, decimal tiktok, decimal goal)
    {
        (_shopify, _tiktok, _goal) = (shopify, tiktok, goal <= 0 ? 1 : goal);
        InvalidateVisual();
    }

    protected override void OnRender(DrawingContext dc)
    {
        var size = Math.Min(ActualWidth, ActualHeight);
        if (size <= 0) return;
        var thickness = size * 0.13;
        var radius = (size - thickness) / 2;
        var center = new Point(ActualWidth / 2, ActualHeight / 2);

        dc.DrawEllipse(null, new Pen(TrackBrush, thickness), center, radius, radius);

        var total = _shopify + _tiktok;
        // Boven het doel: segmenten vullen de hele ring in verhouding.
        var scale = total > _goal ? total : _goal;
        var start = 0.0;
        foreach (var (value, brush) in new[] { (_shopify, ShopifyBrush), (_tiktok, TikTokBrush) })
        {
            var sweep = (double)(value / scale) * 360;
            if (sweep > 0.2) DrawArc(dc, center, radius, thickness, start, sweep, brush);
            start += sweep;
        }

        var pct = (int)Math.Floor(total / _goal * 100);
        var dpi = VisualTreeHelper.GetDpi(this).PixelsPerDip;
        var big = Text($"{pct}%", size * 0.2, FontWeights.Bold, TextBrush, dpi);
        var small = Text(pct >= 100 ? "doel gehaald!" : $"van {_goal.ToString("C0", Ui.Nl)}", size * 0.075, FontWeights.Normal, MutedBrush, dpi);
        var amount = Text(total.ToString("C0", Ui.Nl), size * 0.09, FontWeights.SemiBold, TextBrush, dpi);
        var blockHeight = big.Height + amount.Height + small.Height;
        var y = center.Y - blockHeight / 2;
        dc.DrawText(big, new Point(center.X - big.Width / 2, y));
        dc.DrawText(amount, new Point(center.X - amount.Width / 2, y + big.Height));
        dc.DrawText(small, new Point(center.X - small.Width / 2, y + big.Height + amount.Height));
    }

    private static void DrawArc(DrawingContext dc, Point c, double r, double thickness, double startDeg, double sweepDeg, Brush brush)
    {
        if (sweepDeg >= 359.9)
        {
            dc.DrawEllipse(null, new Pen(brush, thickness), c, r, r);
            return;
        }
        Point At(double deg)
        {
            var rad = (deg - 90) * Math.PI / 180;
            return new Point(c.X + r * Math.Cos(rad), c.Y + r * Math.Sin(rad));
        }
        var geo = new StreamGeometry();
        using (var ctx = geo.Open())
        {
            ctx.BeginFigure(At(startDeg), false, false);
            ctx.ArcTo(At(startDeg + sweepDeg), new Size(r, r), 0, sweepDeg > 180, SweepDirection.Clockwise, true, false);
        }
        geo.Freeze();
        dc.DrawGeometry(null, new Pen(brush, thickness) { StartLineCap = PenLineCap.Flat, EndLineCap = PenLineCap.Flat }, geo);
    }

    private static FormattedText Text(string s, double size, FontWeight weight, Brush brush, double dpi) =>
        new(s, CultureInfo.CurrentCulture, FlowDirection.LeftToRight, new Typeface(new FontFamily("Segoe UI"), FontStyles.Normal, weight, FontStretches.Normal),
            Math.Max(1, size), brush, dpi);

    private static Brush Freeze(Brush b)
    {
        b.Freeze();
        return b;
    }
}
