using NAudio.CoreAudioApi;
using YetiHub.Mods;

namespace YetiHub.Core;

/// <summary>
/// A connected Yeti: its USB identity plus the Windows audio endpoints that belong to it.
/// </summary>
public sealed class YetiDevice : IMicState, IDisposable
{
    private float _peak;

    internal YetiDevice(UsbDeviceInfo usb, MMDevice capture, MMDevice? headphones)
    {
        Usb = usb;
        Capture = capture;
        Headphones = headphones;
        Capture.AudioEndpointVolume.OnVolumeNotification += _ => StateChanged?.Invoke(this, EventArgs.Empty);
    }

    public UsbDeviceInfo Usb { get; }
    public MMDevice Capture { get; }

    /// <summary>The Yeti's own 3.5 mm headphone output, exposed by Windows as a playback device.</summary>
    public MMDevice? Headphones { get; }

    /// <summary>Raised when gain or mute changes, including changes made outside YetiHub.</summary>
    public event EventHandler? StateChanged;

    public string Name => Usb.Model;
    public string EndpointName => Capture.FriendlyName;

    /// <summary>Windows input level for the mic, 0..1. This is the software gain G HUB used to drive.</summary>
    public float Gain
    {
        get => Capture.AudioEndpointVolume.MasterVolumeLevelScalar;
        set => Capture.AudioEndpointVolume.MasterVolumeLevelScalar = Math.Clamp(value, 0f, 1f);
    }

    public float GainDecibels => Capture.AudioEndpointVolume.MasterVolumeLevel;

    public bool IsMuted
    {
        get => Capture.AudioEndpointVolume.Mute;
        set => Capture.AudioEndpointVolume.Mute = value;
    }

    public float HeadphoneVolume
    {
        get => Headphones?.AudioEndpointVolume.MasterVolumeLevelScalar ?? 0f;
        set { if (Headphones != null) Headphones.AudioEndpointVolume.MasterVolumeLevelScalar = Math.Clamp(value, 0f, 1f); }
    }

    /// <summary>Most recent peak, 0..1, fed by <see cref="AudioEngine"/>.</summary>
    public float PeakLevel => Volatile.Read(ref _peak);

    internal void ReportPeak(float peak) => Volatile.Write(ref _peak, peak);

    public string? CaptureFormat
    {
        get
        {
            try { return Capture.AudioClient.MixFormat.ToString(); }
            catch { return null; }
        }
    }

    public bool IsDefaultFor(Role role)
    {
        using var enumerator = new MMDeviceEnumerator();
        if (!enumerator.HasDefaultAudioEndpoint(DataFlow.Capture, role))
            return false;
        return enumerator.GetDefaultAudioEndpoint(DataFlow.Capture, role).ID == Capture.ID;
    }

    /// <summary>Makes the Yeti the Windows default microphone for every role.</summary>
    public void MakeDefault()
    {
        PolicyConfig.SetDefaultEndpoint(Capture.ID);
        if (Headphones != null)
            PolicyConfig.SetDefaultEndpoint(Headphones.ID);
    }

    public void Dispose()
    {
        Capture.Dispose();
        Headphones?.Dispose();
    }
}
