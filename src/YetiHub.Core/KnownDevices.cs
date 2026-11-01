using System.Globalization;
using System.Text.RegularExpressions;

namespace YetiHub.Core;

/// <summary>
/// USB identities of Blue microphones. Extend this table when adding support for another model.
/// </summary>
public static partial class KnownDevices
{
    // Blue has shipped under both vendor ids.
    public static readonly IReadOnlySet<ushort> BlueVendorIds = new HashSet<ushort> { 0xB58C, 0xB58E };

    private static readonly Dictionary<ushort, string> ModelsByProductId = new()
    {
        [0x9E84] = "Blue Yeti",
    };

    public static string ModelName(ushort productId) =>
        ModelsByProductId.TryGetValue(productId, out var name) ? name : $"Blue microphone (PID {productId:X4})";

    /// <summary>
    /// Parses a PnP hardware id such as <c>USB\VID_B58C&amp;PID_9E84&amp;REV_0100</c>.
    /// </summary>
    public static bool TryParseHardwareId(string hardwareId, out ushort vendorId, out ushort productId, out string? revision)
    {
        vendorId = productId = 0;
        revision = null;

        var match = HardwareIdPattern().Match(hardwareId);
        if (!match.Success)
            return false;

        vendorId = ushort.Parse(match.Groups["vid"].Value, NumberStyles.HexNumber, CultureInfo.InvariantCulture);
        productId = ushort.Parse(match.Groups["pid"].Value, NumberStyles.HexNumber, CultureInfo.InvariantCulture);
        if (match.Groups["rev"].Success)
            revision = FormatRevision(match.Groups["rev"].Value);
        return true;
    }

    /// <summary>USB bcdDevice "0100" reads as firmware "1.00".</summary>
    internal static string FormatRevision(string bcd) =>
        bcd.Length == 4 ? $"{int.Parse(bcd[..2], CultureInfo.InvariantCulture)}.{bcd[2..]}" : bcd;

    [GeneratedRegex(@"VID_(?<vid>[0-9A-F]{4})&PID_(?<pid>[0-9A-F]{4})(?:&REV_(?<rev>[0-9A-F]{4}))?", RegexOptions.IgnoreCase)]
    private static partial Regex HardwareIdPattern();
}
