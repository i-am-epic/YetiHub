using System.Runtime.InteropServices;

namespace YetiHub.Core.LowLatency;

public enum SampleKind { Float32, Int16, Int24, Int32 }

/// <summary>A PCM/float stream format, convertible to and from a native WAVEFORMATEXTENSIBLE.</summary>
public sealed record StreamFormat(int SampleRate, int Channels, SampleKind Kind)
{
    private static readonly Guid SubtypePcm = new("00000001-0000-0010-8000-00aa00389b71");
    private static readonly Guid SubtypeFloat = new("00000003-0000-0010-8000-00aa00389b71");

    public int BytesPerSample => Kind switch { SampleKind.Int16 => 2, SampleKind.Int24 => 3, _ => 4 };
    public int BlockAlign => BytesPerSample * Channels;

    public override string ToString() =>
        $"{SampleRate / 1000.0:0.#} kHz · {Channels} ch · {(Kind == SampleKind.Float32 ? "32-bit float" : $"{BytesPerSample * 8}-bit PCM")}";

    /// <summary>Reads a WAVEFORMATEX / WAVEFORMATEXTENSIBLE pointer.</summary>
    internal static StreamFormat FromNative(IntPtr p)
    {
        var tag = (ushort)Marshal.ReadInt16(p, 0);
        var channels = Marshal.ReadInt16(p, 2);
        var rate = Marshal.ReadInt32(p, 4);
        var bits = Marshal.ReadInt16(p, 14);
        var isFloat = tag == 3;
        if (tag == 0xFFFE)
        {
            var sub = Marshal.PtrToStructure<Guid>(p + 24);
            isFloat = sub == SubtypeFloat;
        }
        var kind = isFloat ? SampleKind.Float32 : bits switch { 16 => SampleKind.Int16, 24 => SampleKind.Int24, _ => SampleKind.Int32 };
        return new StreamFormat(rate, channels, kind);
    }

    /// <summary>Allocates a WAVEFORMATEXTENSIBLE; free with Marshal.FreeCoTaskMem.</summary>
    internal IntPtr ToNative()
    {
        var p = Marshal.AllocCoTaskMem(40);
        var bits = (short)(BytesPerSample * 8);
        Marshal.WriteInt16(p, 0, unchecked((short)0xFFFE));
        Marshal.WriteInt16(p, 2, (short)Channels);
        Marshal.WriteInt32(p, 4, SampleRate);
        Marshal.WriteInt32(p, 8, SampleRate * BlockAlign);
        Marshal.WriteInt16(p, 12, (short)BlockAlign);
        Marshal.WriteInt16(p, 14, bits);
        Marshal.WriteInt16(p, 16, 22);
        Marshal.WriteInt16(p, 18, bits);
        Marshal.WriteInt32(p, 20, Channels == 1 ? 0x4 : Channels == 2 ? 0x3 : 0);
        Marshal.StructureToPtr(Kind == SampleKind.Float32 ? SubtypeFloat : SubtypePcm, p + 24, false);
        return p;
    }

    /// <summary>Converts interleaved native samples to float.</summary>
    internal unsafe void ToFloat(IntPtr src, Span<float> dst)
    {
        var b = (byte*)src;
        switch (Kind)
        {
            case SampleKind.Float32:
                new ReadOnlySpan<float>(b, dst.Length).CopyTo(dst);
                break;
            case SampleKind.Int16:
                for (var i = 0; i < dst.Length; i++) dst[i] = ((short*)b)[i] / 32768f;
                break;
            case SampleKind.Int24:
                for (var i = 0; i < dst.Length; i++)
                {
                    var s = (b[i * 3] << 8) | (b[i * 3 + 1] << 16) | (b[i * 3 + 2] << 24);
                    dst[i] = s / 2147483648f;
                }
                break;
            case SampleKind.Int32:
                for (var i = 0; i < dst.Length; i++) dst[i] = ((int*)b)[i] / 2147483648f;
                break;
        }
    }
}
