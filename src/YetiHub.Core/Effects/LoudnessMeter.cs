namespace YetiHub.Core.Effects;

/// <summary>
/// EBU R128 / ITU-R BS.1770 style loudness: K-weighting (head shelf + RLB high-pass), then mean square over a sliding
/// window. Short-term (3 s) is what broadcast and podcast targets like -16 LUFS refer to.
/// </summary>
public sealed class LoudnessMeter
{
    private readonly Biquad _shelf = new(), _rlb = new();
    private int _sampleRate, _channels;
    private double _windowSum;
    private float[] _window = [];
    private int _pos;

    /// <summary>Short-term loudness in LUFS (-70 when silent).</summary>
    public double ShortTermLufs { get; private set; } = -70;

    public void Process(ReadOnlySpan<float> samples, int sampleRate, int channels)
    {
        if (sampleRate != _sampleRate || channels != _channels)
        {
            _sampleRate = sampleRate;
            _channels = channels;
            _shelf.EnsureChannels(channels);
            _rlb.EnsureChannels(channels);
            _shelf.SetHighShelf(sampleRate, 1500, 4f);
            _rlb.SetHighPass(sampleRate, 38f, 0.5f);
            // One mean-square value per 10 ms block, 300 blocks = 3 s.
            _window = new float[300];
            _windowSum = 0;
            _pos = 0;
        }

        // Accumulate across calls: packets can be 3 ms (exclusive mode) or 10 ms.
        var blockFrames = sampleRate / 100;
        for (var i = 0; i < samples.Length; i++)
        {
            var c = i % channels;
            var k = _rlb.Process(_shelf.Process(samples[i], c), c);
            _blockSum += k * k;
            if (c == channels - 1 && ++_blockCount == blockFrames)
            {
                // BS.1770 sums channel powers (each channel weight 1 for L/R).
                var ms = (float)(_blockSum / blockFrames);
                _windowSum += ms - _window[_pos];
                _window[_pos] = ms;
                _pos = (_pos + 1) % _window.Length;
                _blockSum = 0;
                _blockCount = 0;

                var mean = Math.Max(_windowSum / _window.Length, 1e-12);
                ShortTermLufs = Math.Max(-70, -0.691 + 10 * Math.Log10(mean));
            }
        }
    }

    private double _blockSum;
    private int _blockCount;
}
