using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using YetiHub.Core;

namespace YetiHub.App.Controls;

/// <summary>
/// Base for graphs that redraw every display frame while visible. Frame callbacks stop automatically when the
/// control is hidden or unloaded, so an unseen graph costs nothing.
/// </summary>
public abstract class LiveGraph : FrameworkElement
{
    public static readonly DependencyProperty EngineProperty = DependencyProperty.Register(nameof(Engine), typeof(IAnalyzerSource), typeof(LiveGraph));

    private bool _hooked;

    protected LiveGraph()
    {
        IsVisibleChanged += (_, _) => Hook(IsVisible);
        Unloaded += (_, _) => Hook(false);
        Loaded += (_, _) => Hook(IsVisible);
    }

    public IAnalyzerSource? Engine { get => (IAnalyzerSource?)GetValue(EngineProperty); set => SetValue(EngineProperty, value); }

    /// <summary>Set YETIHUB_NO_GRAPHS=1 to disable live graphs (troubleshooting slow GPUs / remote desktop).</summary>
    private static readonly bool Disabled = Environment.GetEnvironmentVariable("YETIHUB_NO_GRAPHS") == "1";

    private void Hook(bool on)
    {
        if (Disabled) on = false;
        if (on == _hooked) return;
        _hooked = on;
        if (on) UiClock.Tick += OnTick;
        else UiClock.Tick -= OnTick;
    }

    private void OnTick(double dt)
    {
        try
        {
            Tick(dt);
            InvalidateVisual();
        }
        catch (Exception ex)
        {
            // A visualisation must never take the app down: stop this graph and record why.
            Hook(false);
            App.LogError(ex, GetType().Name);
        }
    }

    protected abstract void Tick(double dt);

    protected static SolidColorBrush Freeze(Color c)
    {
        var b = new SolidColorBrush(c);
        b.Freeze();
        return b;
    }
}

/// <summary>Spectrum: raw mic as a faint outline, processed voice as a glowing gradient fill, with peak caps.</summary>
public sealed class SpectrumView : LiveGraph
{
    private const int Bands = 96;
    private const float FloorDb = -90, CeilDb = -6;

    private readonly SpectrumAnalyzer _analyzer = new(Bands);
    private readonly float[] _raw = new float[Bands], _in = new float[Bands], _out = new float[Bands], _caps = new float[Bands];
    private static readonly Pen GridPen = new Pen(Freeze(Color.FromArgb(0x22, 0xFF, 0xFF, 0xFF)), 1).Frozen();
    private static readonly Pen InPen = new Pen(Freeze(Color.FromArgb(0x88, 0x9A, 0xA3, 0xB5)), 1.2).Frozen();
    private static readonly Pen CapPen = new Pen(Freeze(Color.FromRgb(0xFF, 0xFF, 0xFF)), 1.5).Frozen();
    private static readonly Brush LabelBrush = Freeze(Color.FromRgb(0x5B, 0x63, 0x72));
    private static readonly Typeface Face = new("Segoe UI");
    private readonly LinearGradientBrush _fill;

    public SpectrumView()
    {
        Array.Fill(_in, FloorDb);
        Array.Fill(_out, FloorDb);
        Array.Fill(_caps, FloorDb);
        _fill = new LinearGradientBrush(
            [
                new GradientStop(Color.FromArgb(0xDD, 0x2E, 0xE6, 0xA6), 0),
                new GradientStop(Color.FromArgb(0xDD, 0x00, 0xB8, 0xFC), 0.45),
                new GradientStop(Color.FromArgb(0xDD, 0x7B, 0x61, 0xFF), 0.8),
                new GradientStop(Color.FromArgb(0xDD, 0xFF, 0x7A, 0xC6), 1),
            ], new Point(0, 0), new Point(1, 0));
        _fill.Freeze();
    }

    protected override void Tick(double dt)
    {
        var engine = Engine;
        var fall = (float)(dt * 40);   // dB/s release, like an analog analyzer
        var capFall = (float)(dt * 12);
        if (engine is null)
        {
            for (var b = 0; b < Bands; b++) { _in[b] = MathF.Max(FloorDb, _in[b] - fall); _out[b] = MathF.Max(FloorDb, _out[b] - fall); }
            return;
        }
        _analyzer.Analyze(engine.InputTap, engine.SampleRate, _raw);
        for (var b = 0; b < Bands; b++) _in[b] = _raw[b] > _in[b] ? _raw[b] : MathF.Max(_raw[b], _in[b] - fall);
        _analyzer.Analyze(engine.OutputTap, engine.SampleRate, _raw);
        for (var b = 0; b < Bands; b++)
        {
            _out[b] = _raw[b] > _out[b] ? _raw[b] : MathF.Max(_raw[b], _out[b] - fall);
            _caps[b] = _out[b] > _caps[b] ? _out[b] : _caps[b] - capFall;
        }
    }

    private double Y(float db, double h) => h - Math.Clamp((db - FloorDb) / (CeilDb - FloorDb), 0, 1) * h;

    protected override void OnRender(DrawingContext dc)
    {
        double w = ActualWidth, h = ActualHeight - 14;
        if (w <= 0 || h <= 0) return;

        // Frequency grid.
        foreach (var hz in new[] { 100, 300, 1000, 3000, 10000 })
        {
            var x = Math.Log(hz / SpectrumAnalyzer.MinFreq) / Math.Log(SpectrumAnalyzer.MaxFreq / SpectrumAnalyzer.MinFreq) * w;
            dc.DrawLine(GridPen, new Point(x, 0), new Point(x, h));
            var label = hz >= 1000 ? $"{hz / 1000}k" : hz.ToString();
            dc.DrawText(new FormattedText(label, System.Globalization.CultureInfo.InvariantCulture, FlowDirection.LeftToRight, Face, 10, LabelBrush, 1.25), new Point(x + 3, h + 1));
        }

        var bw = w / Bands;
        var fill = new StreamGeometry();
        using (var ctx = fill.Open())
        {
            ctx.BeginFigure(new Point(0, h), true, true);
            for (var b = 0; b < Bands; b++)
                ctx.LineTo(new Point(b * bw + bw / 2, Y(_out[b], h)), true, true);
            ctx.LineTo(new Point(w, h), true, false);
        }
        fill.Freeze();
        dc.DrawGeometry(_fill, null, fill);

        var line = new StreamGeometry();
        using (var ctx = line.Open())
        {
            ctx.BeginFigure(new Point(bw / 2, Y(_in[0], h)), false, false);
            for (var b = 1; b < Bands; b++)
                ctx.LineTo(new Point(b * bw + bw / 2, Y(_in[b], h)), true, true);
        }
        line.Freeze();
        dc.DrawGeometry(null, InPen, line);

        for (var b = 0; b < Bands; b += 2)
        {
            var y = Y(_caps[b], h);
            if (y < h - 2) dc.DrawLine(CapPen, new Point(b * bw + 1, y), new Point(b * bw + bw * 2 - 1, y));
        }
    }
}

/// <summary>Scrolling "voiceprint" spectrogram of the processed voice: every word leaves a colourful trace.</summary>
public sealed class SpectrogramView : LiveGraph
{
    private const int Rows = 120, Columns = 420;
    private const float FloorDb = -95, CeilDb = -15;

    private readonly SpectrumAnalyzer _analyzer = new(Rows);
    private readonly float[] _levels = new float[Rows];
    private readonly int[] _pixels = new int[Rows * Columns];
    private readonly WriteableBitmap _bitmap = new(Columns, Rows, 96, 96, PixelFormats.Bgra32, null);
    private static readonly int[] Palette = BuildPalette();
    private double _accum;

    protected override void Tick(double dt)
    {
        var engine = Engine;
        _accum += dt;
        // ~70 columns per second regardless of refresh rate.
        while (_accum >= 1 / 70.0)
        {
            _accum -= 1 / 70.0;
            if (engine != null) _analyzer.Analyze(engine.OutputTap, engine.SampleRate, _levels);
            else Array.Fill(_levels, FloorDb);
            Scroll();
        }
        _bitmap.WritePixels(new Int32Rect(0, 0, Columns, Rows), _pixels, Columns * 4, 0);
    }

    private void Scroll()
    {
        for (var r = 0; r < Rows; r++)
        {
            var row = r * Columns;
            Array.Copy(_pixels, row + 1, _pixels, row, Columns - 1);
            // Row 0 = top = highest frequency.
            var level = _levels[Rows - 1 - r];
            var t = Math.Clamp((level - FloorDb) / (CeilDb - FloorDb), 0, 1);
            _pixels[row + Columns - 1] = Palette[(int)(t * (Palette.Length - 1))];
        }
    }

    protected override void OnRender(DrawingContext dc)
    {
        if (ActualWidth <= 0 || ActualHeight <= 0) return;
        dc.DrawImage(_bitmap, new Rect(0, 0, ActualWidth, ActualHeight));
    }

    /// <summary>Night-sky palette: deep navy → indigo → cyan → mint → white.</summary>
    private static int[] BuildPalette()
    {
        (double T, Color C)[] stops =
        [
            (0.00, Color.FromRgb(0x0B, 0x0D, 0x11)),
            (0.30, Color.FromRgb(0x1D, 0x16, 0x4A)),
            (0.55, Color.FromRgb(0x7B, 0x61, 0xFF)),
            (0.75, Color.FromRgb(0x00, 0xB8, 0xFC)),
            (0.90, Color.FromRgb(0x2E, 0xE6, 0xA6)),
            (1.00, Color.FromRgb(0xFF, 0xFF, 0xFF)),
        ];
        var palette = new int[256];
        for (var i = 0; i < palette.Length; i++)
        {
            var t = i / 255.0;
            var k = 0;
            while (k < stops.Length - 2 && t > stops[k + 1].T) k++;
            var (t0, c0) = stops[k];
            var (t1, c1) = stops[k + 1];
            var f = (t - t0) / (t1 - t0);
            byte L(byte a, byte b) => (byte)(a + (b - a) * f);
            palette[i] = (255 << 24) | (L(c0.R, c1.R) << 16) | (L(c0.G, c1.G) << 8) | L(c0.B, c1.B);
        }
        return palette;
    }
}

/// <summary>Scrolling mirrored waveform ribbon of your voice, coloured by loudness.</summary>
public sealed class WaveformView : LiveGraph
{
    private const int Columns = 160;
    private readonly float[] _peaks = new float[Columns];
    private readonly float[] _scratch = new float[AnalyzerTap.Size];
    private int _head;
    private double _accum;
    private static readonly Brush Quiet = Freeze(Color.FromRgb(0x2A, 0x3A, 0x55));
    private readonly LinearGradientBrush _loud;

    public WaveformView()
    {
        _loud = new LinearGradientBrush(
            [
                new GradientStop(Color.FromRgb(0xFF, 0x7A, 0xC6), 0),
                new GradientStop(Color.FromRgb(0x7B, 0x61, 0xFF), 0.3),
                new GradientStop(Color.FromRgb(0x00, 0xB8, 0xFC), 0.5),
                new GradientStop(Color.FromRgb(0x7B, 0x61, 0xFF), 0.7),
                new GradientStop(Color.FromRgb(0xFF, 0x7A, 0xC6), 1),
            ], new Point(0, 0), new Point(0, 1));
        _loud.Freeze();
        _loudFades = Enumerable.Range(0, Fades).Select(i => Faded(_loud, 0.25 + 0.75 * i / (Fades - 1))).ToArray();
        _quietFades = Enumerable.Range(0, Fades).Select(i => Faded(Quiet, 0.25 + 0.75 * i / (Fades - 1))).ToArray();
    }

    private const int Fades = 6;
    private readonly Brush[] _loudFades, _quietFades;

    private static Brush Faded(Brush b, double opacity)
    {
        var c = b.CloneCurrentValue();
        c.Opacity = opacity;
        c.Freeze();
        return c;
    }

    protected override void Tick(double dt)
    {
        var engine = Engine;
        _accum += dt;
        while (_accum >= 1 / 40.0)
        {
            _accum -= 1 / 40.0;
            var peak = 0f;
            if (engine != null)
            {
                var span = _scratch.AsSpan(0, Math.Min(engine.SampleRate / 40, _scratch.Length));
                engine.OutputTap.Read(span);
                foreach (var v in span) peak = MathF.Max(peak, MathF.Abs(v));
            }
            _peaks[_head] = LevelMeter.ToPositionF(peak);
            _head = (_head + 1) % Columns;
        }
    }

    protected override void OnRender(DrawingContext dc)
    {
        double w = ActualWidth, h = ActualHeight;
        if (w <= 0 || h <= 0) return;
        var bw = w / Columns;
        var mid = h / 2;
        for (var i = 0; i < Columns; i++)
        {
            var v = _peaks[(_head + i) % Columns];
            var half = Math.Max(1.5, v * (h / 2 - 2));
            // Newest columns on the right are brightest (6 pre-built fade steps instead of an opacity layer per bar).
            var step = Math.Min(Fades - 1, i * Fades / Columns);
            dc.DrawRoundedRectangle(v > 0.12 ? _loudFades[step] : _quietFades[step], null, new Rect(i * bw + 1, mid - half, Math.Max(1, bw - 2), half * 2), 1.5, 1.5);
        }
    }
}
