using YetiHub.Core;
using YetiHub.Core.Effects;

namespace YetiHub.App.Controls;

/// <summary>
/// Turns the newest samples of an <see cref="AnalyzerTap"/> into log-spaced band levels (dBFS) for drawing.
/// 2048-point FFT, Hann window; bands 40 Hz–16 kHz, each band = loudest bin it covers.
/// </summary>
public sealed class SpectrumAnalyzer(int bands)
{
    private const int N = 2048;
    private static readonly float[] Window = Enumerable.Range(0, N).Select(i => 0.5f - 0.5f * MathF.Cos(2 * MathF.PI * i / N)).ToArray();

    private readonly float[] _samples = new float[N], _re = new float[N], _im = new float[N];
    private int[] _bandStart = [], _bandEnd = [];
    private int _sampleRate;

    public int Bands { get; } = bands;
    public const float MinFreq = 40, MaxFreq = 16000;

    /// <summary>Writes Bands values in dBFS into <paramref name="levels"/>.</summary>
    public void Analyze(AnalyzerTap tap, int sampleRate, Span<float> levels)
    {
        if (sampleRate != _sampleRate) BuildBands(sampleRate);
        tap.Read(_samples);
        for (var i = 0; i < N; i++) { _re[i] = _samples[i] * Window[i]; _im[i] = 0; }
        Fft.Transform(_re, _im, inverse: false);

        // Full-scale sine → |X| = N/4 with a Hann window.
        const float norm = 4f / N;
        for (var b = 0; b < Bands; b++)
        {
            var peak = 0f;
            for (var k = _bandStart[b]; k <= _bandEnd[b]; k++)
                peak = MathF.Max(peak, _re[k] * _re[k] + _im[k] * _im[k]);
            levels[b] = 20 * MathF.Log10(MathF.Sqrt(peak) * norm + 1e-7f);
        }
    }

    private void BuildBands(int sampleRate)
    {
        _sampleRate = sampleRate;
        _bandStart = new int[Bands];
        _bandEnd = new int[Bands];
        var binHz = (float)sampleRate / N;
        for (var b = 0; b < Bands; b++)
        {
            var f0 = MinFreq * MathF.Pow(MaxFreq / MinFreq, (float)b / Bands);
            var f1 = MinFreq * MathF.Pow(MaxFreq / MinFreq, (float)(b + 1) / Bands);
            _bandStart[b] = Math.Clamp((int)(f0 / binHz), 1, N / 2 - 1);
            _bandEnd[b] = Math.Clamp((int)(f1 / binHz), _bandStart[b], N / 2 - 1);
        }
    }
}
