using NAudio.CoreAudioApi;
using NAudio.CoreAudioApi.Interfaces;

namespace YetiHub.Core;

/// <param name="Unavailable">Why Windows won't play to it ("unplugged", "disabled"), or null when active.</param>
public sealed record OutputDeviceInfo(string Id, string Name, bool IsVirtualCable, string? Unavailable)
{
    public bool IsAvailable => Unavailable is null;
}

/// <summary>
/// Finds the Yeti and tells listeners when audio devices are plugged, unplugged, or change default.
/// </summary>
public sealed class DeviceScanner : IMMNotificationClient, IDisposable
{
    private readonly MMDeviceEnumerator _enumerator = new();
    private readonly Timer _debounce;

    public DeviceScanner()
    {
        // Windows fires several notifications per plug event; collapse them into one rescan.
        _debounce = new Timer(_ => DevicesChanged?.Invoke(this, EventArgs.Empty));
        _enumerator.RegisterEndpointNotificationCallback(this);
    }

    /// <summary>Raised on a thread-pool thread.</summary>
    public event EventHandler? DevicesChanged;

    /// <param name="usbDevices">Result of <see cref="UsbProbe.FindBlueDevices"/>.</param>
    public YetiDevice? FindYeti(IEnumerable<UsbDeviceInfo> usbDevices)
    {
        foreach (var usb in usbDevices)
        {
            var capture = FindEndpoint(DataFlow.Capture, usb);
            if (capture is null)
                continue;
            return new YetiDevice(usb, capture, FindEndpoint(DataFlow.Render, usb));
        }
        return null;
    }

    /// <summary>
    /// Playback devices for the monitor and virtual-mic pickers. Unplugged and disabled ones are included (but not
    /// <see cref="OutputDeviceInfo.IsAvailable"/>) so users can see e.g. Realtek speakers whose jack isn't detected.
    /// </summary>
    public IReadOnlyList<OutputDeviceInfo> ListOutputs()
    {
        var list = new List<OutputDeviceInfo>();
        foreach (var d in _enumerator.EnumerateAudioEndPoints(DataFlow.Render, DeviceState.Active | DeviceState.Unplugged | DeviceState.Disabled))
        {
            using (d)
            {
                string name;
                try { name = d.FriendlyName; }
                catch (System.Runtime.InteropServices.COMException) { continue; }
                list.Add(new OutputDeviceInfo(d.ID, name, IsVirtualCable(name), d.State switch
                {
                    DeviceState.Active => null,
                    DeviceState.Unplugged => "unplugged",
                    _ => "disabled",
                }));
            }
        }
        return list;
    }

    /// <summary>Virtual audio cables whose playback side shows up to apps as a microphone.</summary>
    public static bool IsVirtualCable(string name) =>
        name.Contains("CABLE", StringComparison.OrdinalIgnoreCase) ||
        name.Contains("VB-Audio", StringComparison.OrdinalIgnoreCase) ||
        name.Contains("Voicemeeter", StringComparison.OrdinalIgnoreCase);

    private MMDevice? FindEndpoint(DataFlow flow, UsbDeviceInfo usb)
    {
        var endpoints = _enumerator.EnumerateAudioEndPoints(flow, DeviceState.Active).ToList();

        // DeviceFriendlyName is the USB audio function name ("Yeti Stereo Microphone"), which survives the
        // user renaming the endpoint in Sound settings.
        var match = endpoints.FirstOrDefault(e => usb.AudioFunctionName != null &&
                        string.Equals(e.DeviceFriendlyName, usb.AudioFunctionName, StringComparison.OrdinalIgnoreCase))
                    ?? endpoints.FirstOrDefault(e => e.DeviceFriendlyName.Contains("Yeti", StringComparison.OrdinalIgnoreCase));

        foreach (var e in endpoints.Where(e => e != match))
            e.Dispose();
        return match;
    }

    private void Poke() => _debounce.Change(400, Timeout.Infinite);

    void IMMNotificationClient.OnDeviceStateChanged(string deviceId, DeviceState newState) => Poke();
    void IMMNotificationClient.OnDeviceAdded(string pwstrDeviceId) => Poke();
    void IMMNotificationClient.OnDeviceRemoved(string deviceId) => Poke();
    void IMMNotificationClient.OnDefaultDeviceChanged(DataFlow flow, Role role, string defaultDeviceId) => Poke();
    void IMMNotificationClient.OnPropertyValueChanged(string pwstrDeviceId, PropertyKey key) { }

    public void Dispose()
    {
        _enumerator.UnregisterEndpointNotificationCallback(this);
        _enumerator.Dispose();
        _debounce.Dispose();
    }
}
