using NAudio.CoreAudioApi;
using NAudio.CoreAudioApi.Interfaces;

namespace YetiHub.Core;

public sealed record OutputDeviceInfo(string Id, string Name, bool IsVirtualCable);

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

    /// <param name="usbDevices">Result of <see cref="UsbProbe.FindBlueDevices"/>, which is slow (WMI); run it off the UI thread.</param>
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

    /// <summary>Active playback devices, for the monitor and virtual-mic pickers.</summary>
    public IReadOnlyList<OutputDeviceInfo> ListOutputs()
    {
        var list = new List<OutputDeviceInfo>();
        foreach (var d in _enumerator.EnumerateAudioEndPoints(DataFlow.Render, DeviceState.Active))
        {
            using (d)
                list.Add(new OutputDeviceInfo(d.ID, d.FriendlyName, IsVirtualCable(d.FriendlyName)));
        }
        return list;
    }

    public MMDevice? GetDevice(string id)
    {
        try { return _enumerator.GetDevice(id); }
        catch (System.Runtime.InteropServices.COMException) { return null; }
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
