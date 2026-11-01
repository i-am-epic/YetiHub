using System.Windows;
using System.Windows.Media;

namespace YetiHub.App.Controls;

/// <summary>
/// Segmented LED meter on a -60..0 dBFS scale with a peak-hold marker. Draws itself; no layout per frame.
/// </summary>
public sealed class LevelMeter : FrameworkElement
{
    private const int Segments = 48;
    private const double FloorDb = -60;

    public static readonly DependencyProperty LevelProperty = DependencyProperty.Register(nameof(Level), typeof(double), typeof(LevelMeter),
        new FrameworkPropertyMetadata(0.0, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty PeakProperty = DependencyProperty.Register(nameof(Peak), typeof(double), typeof(LevelMeter),
        new FrameworkPropertyMetadata(0.0, FrameworkPropertyMetadataOptions.AffectsRender));

    private static readonly Brush Off = Frozen(Color.FromRgb(0x23, 0x28, 0x31));
    private static readonly Brush Green = Frozen(Color.FromRgb(0x2E, 0xE6, 0xA6));
    private static readonly Brush Yellow = Frozen(Color.FromRgb(0xFF, 0xC8, 0x3D));
    private static readonly Brush Red = Frozen(Color.FromRgb(0xFF, 0x4D, 0x6A));

    /// <summary>Linear amplitude 0..1.</summary>
    public double Level { get => (double)GetValue(LevelProperty); set => SetValue(LevelProperty, value); }

    /// <summary>Linear amplitude 0..1 for the hold marker.</summary>
    public double Peak { get => (double)GetValue(PeakProperty); set => SetValue(PeakProperty, value); }

    public static float ToPositionF(float linear) => (float)ToPosition(linear);

    public static double ToPosition(double linear)
    {
        if (linear <= 0) return 0;
        var db = 20 * Math.Log10(linear);
        return Math.Clamp((db - FloorDb) / -FloorDb, 0, 1);
    }

    protected override void OnRender(DrawingContext dc)
    {
        var w = ActualWidth;
        var h = ActualHeight;
        if (w <= 0 || h <= 0) return;

        const double gap = 3;
        var seg = (w - gap * (Segments - 1)) / Segments;
        var lit = (int)Math.Round(ToPosition(Level) * Segments);
        var hold = (int)Math.Round(ToPosition(Peak) * Segments) - 1;

        for (var i = 0; i < Segments; i++)
        {
            var t = (double)i / Segments;
            var on = i < lit || i == hold;
            var brush = !on ? Off : t > 0.9 ? Red : t > 0.75 ? Yellow : Green;
            dc.DrawRoundedRectangle(brush, null, new Rect(i * (seg + gap), 0, seg, h), 2, 2);
        }
    }

    private static SolidColorBrush Frozen(Color c)
    {
        var b = new SolidColorBrush(c);
        b.Freeze();
        return b;
    }
}
