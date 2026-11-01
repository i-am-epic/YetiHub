using System.Windows;
using System.Windows.Input;
using System.Windows.Media;

namespace YetiHub.App.Controls;

/// <summary>
/// Rotary knob. Drag up/down or scroll to change, double-click to reset, arrow keys for fine steps.
/// </summary>
public sealed class Knob : FrameworkElement
{
    private const double StartAngle = 135, Sweep = 270;

    public static readonly DependencyProperty ValueProperty = DependencyProperty.Register(nameof(Value), typeof(double), typeof(Knob),
        new FrameworkPropertyMetadata(0.0, FrameworkPropertyMetadataOptions.BindsTwoWayByDefault | FrameworkPropertyMetadataOptions.AffectsRender, null, CoerceValue));

    public static readonly DependencyProperty MinimumProperty = DependencyProperty.Register(nameof(Minimum), typeof(double), typeof(Knob),
        new FrameworkPropertyMetadata(0.0, FrameworkPropertyMetadataOptions.AffectsRender, (d, _) => d.CoerceValue(ValueProperty)));

    // Set Minimum/Maximum before Value in XAML so the value is never clamped to the default range.
    public static readonly DependencyProperty MaximumProperty = DependencyProperty.Register(nameof(Maximum), typeof(double), typeof(Knob),
        new FrameworkPropertyMetadata(100.0, FrameworkPropertyMetadataOptions.AffectsRender, (d, _) => d.CoerceValue(ValueProperty)));

    public static readonly DependencyProperty DefaultValueProperty = DependencyProperty.Register(nameof(DefaultValue), typeof(double), typeof(Knob));

    /// <summary>When true the arc grows from the centre (for +/- values like EQ gain).</summary>
    public static readonly DependencyProperty BipolarProperty = DependencyProperty.Register(nameof(Bipolar), typeof(bool), typeof(Knob),
        new FrameworkPropertyMetadata(false, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty AccentProperty = DependencyProperty.Register(nameof(Accent), typeof(Brush), typeof(Knob),
        new FrameworkPropertyMetadata(new SolidColorBrush(Color.FromRgb(0x00, 0xB8, 0xFC)), FrameworkPropertyMetadataOptions.AffectsRender));

    private static readonly Brush Track = Frozen(Color.FromRgb(0x2A, 0x2F, 0x3A));
    private static readonly Brush Face = Frozen(Color.FromRgb(0x1C, 0x20, 0x28));
    private static readonly Pen FaceRim = new Pen(Frozen(Color.FromRgb(0x33, 0x39, 0x45)), 1).Frozen();

    private Point _dragStart;
    private double _dragValue;

    static Knob()
    {
        FocusableProperty.OverrideMetadata(typeof(Knob), new FrameworkPropertyMetadata(true));
        CursorProperty.OverrideMetadata(typeof(Knob), new FrameworkPropertyMetadata(Cursors.SizeNS));
    }

    public double Value { get => (double)GetValue(ValueProperty); set => SetValue(ValueProperty, value); }
    public double Minimum { get => (double)GetValue(MinimumProperty); set => SetValue(MinimumProperty, value); }
    public double Maximum { get => (double)GetValue(MaximumProperty); set => SetValue(MaximumProperty, value); }
    public double DefaultValue { get => (double)GetValue(DefaultValueProperty); set => SetValue(DefaultValueProperty, value); }
    public bool Bipolar { get => (bool)GetValue(BipolarProperty); set => SetValue(BipolarProperty, value); }
    public Brush Accent { get => (Brush)GetValue(AccentProperty); set => SetValue(AccentProperty, value); }

    private double Range => Math.Max(Maximum - Minimum, 1e-9);

    private static object CoerceValue(DependencyObject d, object value)
    {
        var k = (Knob)d;
        return Math.Clamp((double)value, k.Minimum, Math.Max(k.Minimum, k.Maximum));
    }

    protected override Size MeasureOverride(Size available)
    {
        var s = double.IsInfinity(available.Width) ? 72 : Math.Min(available.Width, double.IsInfinity(available.Height) ? available.Width : available.Height);
        return new Size(s, s);
    }

    protected override void OnRender(DrawingContext dc)
    {
        var size = Math.Min(ActualWidth, ActualHeight);
        if (size <= 0) return;
        var center = new Point(ActualWidth / 2, ActualHeight / 2);
        var stroke = Math.Max(3, size * 0.075);
        var radius = size / 2 - stroke / 2 - 1;

        var trackPen = new Pen(Track, stroke) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round };
        dc.DrawGeometry(null, trackPen, Arc(center, radius, StartAngle, StartAngle + Sweep));

        var t = (Value - Minimum) / Range;
        var from = Bipolar ? StartAngle + Sweep / 2 : StartAngle;
        var to = StartAngle + Sweep * t;
        if (Math.Abs(to - from) > 0.5)
        {
            var valuePen = new Pen(IsEnabled ? Accent : Track, stroke) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round };
            dc.DrawGeometry(null, valuePen, Arc(center, radius, Math.Min(from, to), Math.Max(from, to)));
        }

        var faceRadius = radius - stroke * 1.6;
        dc.DrawEllipse(Face, FaceRim, center, faceRadius, faceRadius);

        var a = to * Math.PI / 180;
        var dir = new Vector(Math.Cos(a), Math.Sin(a));
        dc.DrawLine(new Pen(Brushes.White, Math.Max(2, stroke * 0.5)) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round },
            center + dir * faceRadius * 0.45, center + dir * faceRadius * 0.85);
    }

    private static Geometry Arc(Point c, double r, double fromDeg, double toDeg)
    {
        static Point At(Point c, double r, double deg) => new(c.X + r * Math.Cos(deg * Math.PI / 180), c.Y + r * Math.Sin(deg * Math.PI / 180));
        var g = new StreamGeometry();
        using (var ctx = g.Open())
        {
            ctx.BeginFigure(At(c, r, fromDeg), false, false);
            ctx.ArcTo(At(c, r, toDeg), new Size(r, r), 0, toDeg - fromDeg > 180, SweepDirection.Clockwise, true, false);
        }
        g.Freeze();
        return g;
    }

    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        Focus();
        if (e.ClickCount == 2)
        {
            Value = DefaultValue;
            e.Handled = true;
            return;
        }
        _dragStart = e.GetPosition(this);
        _dragValue = Value;
        CaptureMouse();
        e.Handled = true;
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        if (!IsMouseCaptured) return;
        var dy = _dragStart.Y - e.GetPosition(this).Y;
        var fine = Keyboard.Modifiers.HasFlag(ModifierKeys.Shift) ? 0.2 : 1.0;
        Value = _dragValue + dy / 180 * Range * fine;
    }

    protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e) => ReleaseMouseCapture();

    protected override void OnMouseWheel(MouseWheelEventArgs e)
    {
        Value += Math.Sign(e.Delta) * Range / 50;
        e.Handled = true;
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        var step = e.Key switch { Key.Up or Key.Right => 1, Key.Down or Key.Left => -1, _ => 0 };
        if (step == 0) return;
        Value += step * Range / 100;
        e.Handled = true;
    }

    private static SolidColorBrush Frozen(Color c)
    {
        var b = new SolidColorBrush(c);
        b.Freeze();
        return b;
    }
}

internal static class FreezableExtensions
{
    public static T Frozen<T>(this T f) where T : Freezable
    {
        f.Freeze();
        return f;
    }
}
