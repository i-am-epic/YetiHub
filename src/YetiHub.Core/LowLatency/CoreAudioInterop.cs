using System.Runtime.InteropServices;

namespace YetiHub.Core.LowLatency;

/// <summary>
/// Minimal raw WASAPI interop. NAudio does not expose IAudioClient3, raw stream mode, or a way to write to a render
/// client from the capture thread, which is what the low-latency path needs.
/// </summary>
internal static class CoreAudio
{
    public const int ShareModeShared = 0;
    public const int ShareModeExclusive = 1;
    public const uint StreamFlagsEventCallback = 0x00040000;
    public const uint StreamFlagsAutoConvertPcm = 0x80000000;
    public const uint StreamFlagsSrcDefaultQuality = 0x08000000;
    public const uint BufferFlagsSilent = 0x2;
    public const int ErrBufferSizeNotAligned = unchecked((int)0x88890019);
    private const int ClsCtxAll = 23;

    private static readonly Guid EnumeratorClsid = new("BCDE0395-E52F-467C-8E3D-C4579291692E");

    public static IAudioClient3 Activate(string deviceId)
    {
        // Created through the CLSID rather than a [ComImport] class so it cannot clash with NAudio's own RCW type.
        var enumerator = (IMMDeviceEnumerator)Activator.CreateInstance(Type.GetTypeFromCLSID(EnumeratorClsid)!)!;
        try
        {
            Check(enumerator.GetDevice(deviceId, out var device));
            try
            {
                var iid = typeof(IAudioClient3).GUID;
                Check(device.Activate(ref iid, ClsCtxAll, IntPtr.Zero, out var client));
                return (IAudioClient3)client;
            }
            finally { Marshal.ReleaseComObject(device); }
        }
        finally { Marshal.ReleaseComObject(enumerator); }
    }

    public static T GetService<T>(IAudioClient3 client)
    {
        var iid = typeof(T).GUID;
        Check(client.GetService(ref iid, out var service));
        return (T)service;
    }

    /// <summary>Requests raw mode (no Windows effects/APOs). Silently ignored by devices that don't support it.</summary>
    public static void TrySetRaw(IAudioClient3 client)
    {
        // AudioClientProperties { UINT32 cbSize; BOOL bIsOffload; AUDIO_STREAM_CATEGORY eCategory; AUDCLNT_STREAMOPTIONS Options; }
        var props = Marshal.AllocCoTaskMem(16);
        try
        {
            Marshal.WriteInt32(props, 0, 16);
            Marshal.WriteInt32(props, 4, 0);
            Marshal.WriteInt32(props, 8, 0);  // AudioCategory_Other
            Marshal.WriteInt32(props, 12, 1); // AUDCLNT_STREAMOPTIONS_RAW
            client.SetClientProperties(props);
        }
        finally { Marshal.FreeCoTaskMem(props); }
    }

    public static void Check(int hr)
    {
        if (hr < 0) Marshal.ThrowExceptionForHR(hr);
    }

    public static void Release(object? com)
    {
        if (com != null && Marshal.IsComObject(com)) Marshal.ReleaseComObject(com);
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern IntPtr CreateEventW(IntPtr attributes, bool manualReset, bool initialState, IntPtr name);

    [DllImport("kernel32.dll")]
    public static extern uint WaitForSingleObject(IntPtr handle, uint milliseconds);

    [DllImport("kernel32.dll")]
    public static extern bool CloseHandle(IntPtr handle);

    /// <summary>Puts the thread in the Windows "Pro Audio" scheduling class (MMCSS).</summary>
    [DllImport("avrt.dll", CharSet = CharSet.Unicode)]
    public static extern IntPtr AvSetMmThreadCharacteristicsW(string taskName, ref uint taskIndex);

    [DllImport("avrt.dll")]
    public static extern bool AvRevertMmThreadCharacteristics(IntPtr handle);
}

[ComImport, Guid("A95664D2-9614-4F35-A746-DE8DB63617E6"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IMMDeviceEnumerator
{
    [PreserveSig] int EnumAudioEndpoints();
    [PreserveSig] int GetDefaultAudioEndpoint();
    [PreserveSig] int GetDevice([MarshalAs(UnmanagedType.LPWStr)] string id, out IMMDevice device);
}

[ComImport, Guid("D666063F-1587-4E43-81F1-B948E807363F"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IMMDevice
{
    [PreserveSig] int Activate(ref Guid iid, int clsCtx, IntPtr activationParams, [MarshalAs(UnmanagedType.IUnknown)] out object instance);
}

// IAudioClient → IAudioClient2 → IAudioClient3, flattened in vtable order.
[ComImport, Guid("7ED4EE07-8E67-4CD4-8C1A-2B7A5987AD42"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IAudioClient3
{
    [PreserveSig] int Initialize(int shareMode, uint streamFlags, long bufferDuration, long periodicity, IntPtr format, IntPtr sessionGuid);
    [PreserveSig] int GetBufferSize(out uint frames);
    [PreserveSig] int GetStreamLatency(out long latency);
    [PreserveSig] int GetCurrentPadding(out uint frames);
    [PreserveSig] int IsFormatSupported(int shareMode, IntPtr format, out IntPtr closestMatch);
    [PreserveSig] int GetMixFormat(out IntPtr format);
    [PreserveSig] int GetDevicePeriod(out long defaultPeriod, out long minimumPeriod);
    [PreserveSig] int Start();
    [PreserveSig] int Stop();
    [PreserveSig] int Reset();
    [PreserveSig] int SetEventHandle(IntPtr handle);
    [PreserveSig] int GetService(ref Guid iid, [MarshalAs(UnmanagedType.IUnknown)] out object service);
    [PreserveSig] int IsOffloadCapable(int category, out int capable);
    [PreserveSig] int SetClientProperties(IntPtr properties);
    [PreserveSig] int GetBufferSizeLimits(IntPtr format, int eventDriven, out long min, out long max);
    [PreserveSig] int GetSharedModeEnginePeriod(IntPtr format, out uint defaultFrames, out uint fundamentalFrames, out uint minFrames, out uint maxFrames);
    [PreserveSig] int GetCurrentSharedModeEnginePeriod(out IntPtr format, out uint currentFrames);
    [PreserveSig] int InitializeSharedAudioStream(uint streamFlags, uint periodFrames, IntPtr format, IntPtr sessionGuid);
}

[ComImport, Guid("C8ADBD64-E71E-48a0-A4DE-185C395CD317"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IAudioCaptureClient
{
    [PreserveSig] int GetBuffer(out IntPtr data, out uint frames, out uint flags, out ulong devicePosition, out ulong qpcPosition);
    [PreserveSig] int ReleaseBuffer(uint frames);
    [PreserveSig] int GetNextPacketSize(out uint frames);
}

[ComImport, Guid("F294ACFC-3146-4483-A7BF-ADDCA7C260E2"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IAudioRenderClient
{
    [PreserveSig] int GetBuffer(uint frames, out IntPtr data);
    [PreserveSig] int ReleaseBuffer(uint frames, uint flags);
}
