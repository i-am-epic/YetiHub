using System.Runtime.InteropServices;
using Microsoft.Win32;

namespace YetiHub.Core;

public sealed record UsbDeviceInfo(
    ushort VendorId,
    ushort ProductId,
    string Model,
    string? FirmwareRevision,
    string InstanceId,
    string? AudioFunctionName,
    IReadOnlyList<DriverInfo> Drivers);

public sealed record DriverInfo(string Function, string Provider, string Version, string Inf);

/// <summary>
/// Finds Blue microphones on the USB bus and reports which drivers Windows bound to them.
/// Reads the PnP registry directly (a few milliseconds) instead of WMI (seconds).
/// </summary>
public static class UsbProbe
{
    private const string EnumUsb = @"SYSTEM\CurrentControlSet\Enum\USB";
    private const string ClassRoot = @"SYSTEM\CurrentControlSet\Control\Class\";

    public static IReadOnlyList<UsbDeviceInfo> FindBlueDevices()
    {
        using var usb = Registry.LocalMachine.OpenSubKey(EnumUsb);
        if (usb is null)
            return [];

        // Every USB function of a present Blue device: (hardware key name, instance id, values).
        var functions = new List<(string Key, string InstanceId, RegistryKey Values)>();
        foreach (var keyName in usb.GetSubKeyNames())
        {
            if (!KnownDevices.TryParseHardwareId(keyName, out var vid, out _, out _) || !KnownDevices.BlueVendorIds.Contains(vid))
                continue;
            using var hw = usb.OpenSubKey(keyName);
            foreach (var instance in hw?.GetSubKeyNames() ?? [])
            {
                var instanceId = $@"USB\{keyName}\{instance}";
                // The registry remembers every port a device was ever plugged into; keep only the ones present now.
                if (IsPresent(instanceId) && hw!.OpenSubKey(instance) is { } values)
                    functions.Add((keyName, instanceId, values));
            }
        }

        try
        {
            var results = new List<UsbDeviceInfo>();
            foreach (var parent in functions.Where(f => !f.Key.Contains("&MI_", StringComparison.OrdinalIgnoreCase)))
            {
                var hardwareIds = parent.Values.GetValue("HardwareID") as string[] ?? [];
                var withRev = hardwareIds.FirstOrDefault(id => id.Contains("&REV_", StringComparison.OrdinalIgnoreCase)) ?? parent.Key;
                if (!KnownDevices.TryParseHardwareId(withRev, out var vid, out var pid, out var rev))
                    continue;

                var prefix = $"VID_{vid:X4}&PID_{pid:X4}";
                var family = functions.Where(f => f.Key.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)).ToList();

                var audioName = family
                    .Where(f => f.Key.EndsWith("&MI_00", StringComparison.OrdinalIgnoreCase))
                    .Select(f => f.Values.GetValue("FriendlyName") as string)
                    .FirstOrDefault(n => !string.IsNullOrEmpty(n));

                results.Add(new UsbDeviceInfo(vid, pid, KnownDevices.ModelName(pid), rev, parent.InstanceId, audioName,
                    family.Select(f => ReadDriver(f.Values)).OfType<DriverInfo>().ToList()));
            }
            return results;
        }
        finally
        {
            foreach (var f in functions) f.Values.Dispose();
        }
    }

    private static DriverInfo? ReadDriver(RegistryKey device)
    {
        // DeviceDesc looks like "@usb.inf,%usb\composite.devicedesc%;USB Composite Device".
        var desc = device.GetValue("DeviceDesc") as string ?? "";
        var function = desc.Contains(';') ? desc[(desc.LastIndexOf(';') + 1)..] : desc;

        if (device.GetValue("Driver") is not string driverKey)
            return new DriverInfo(function, "—", "—", "—");
        using var cls = Registry.LocalMachine.OpenSubKey(ClassRoot + driverKey);
        return new DriverInfo(function,
            cls?.GetValue("ProviderName") as string ?? "—",
            cls?.GetValue("DriverVersion") as string ?? "—",
            cls?.GetValue("InfPath") as string ?? "—");
    }

    private static bool IsPresent(string instanceId) =>
        CM_Locate_DevNodeW(out _, instanceId, CM_LOCATE_DEVNODE_NORMAL) == CR_SUCCESS;

    private const int CM_LOCATE_DEVNODE_NORMAL = 0;
    private const int CR_SUCCESS = 0;

    [DllImport("cfgmgr32.dll", CharSet = CharSet.Unicode)]
    private static extern int CM_Locate_DevNodeW(out uint devInst, string deviceId, int flags);
}
