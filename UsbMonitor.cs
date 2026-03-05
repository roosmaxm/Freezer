using System.Management;

namespace Freezer;

/// <summary>
/// Enumerates USB devices connected to the system via WMI.
/// </summary>
public static class UsbMonitor
{
    /// <summary>
    /// Returns a list of connected USB device names.
    /// </summary>
    public static List<string> GetConnectedUsbDevices()
    {
        var devices = new List<string>();
        try
        {
            using var searcher = new ManagementObjectSearcher(
                "root\\CIMV2",
                "SELECT * FROM Win32_PnPEntity WHERE DeviceID LIKE 'USB%'");

            foreach (ManagementObject obj in searcher.Get())
            {
                string? rawName = obj["Name"]?.ToString();
                string? rawDeviceId = obj["DeviceID"]?.ToString();
                string name = rawName != null ? System.Net.WebUtility.HtmlDecode(rawName) : "Unknown Device";
                string deviceId = rawDeviceId != null ? System.Net.WebUtility.HtmlDecode(rawDeviceId) : string.Empty;
                if (!string.IsNullOrWhiteSpace(name))
                    devices.Add($"{name} [{deviceId}]");
            }
        }
        catch (Exception ex)
        {
            devices.Add($"[Error enumerating USB devices: {ex.Message}]");
        }

        return devices;
    }

    /// <summary>
    /// Checks if any USB device name matches known high-interrupt-rate device categories.
    /// </summary>
    public static List<string> FlagHighInterruptDevices(List<string> devices)
    {
        var flagged = new List<string>();
        var keywords = new[]
        {
            "Xbox", "Controller", "Gamepad",
            "Mouse", "Keyboard",
            "Headphone", "Headset", "Audio", "Sound",
            "Wireless", "Bluetooth", "Dongle", "Receiver"
        };

        foreach (var dev in devices)
        {
            foreach (var kw in keywords)
            {
                if (dev.Contains(kw, StringComparison.OrdinalIgnoreCase))
                {
                    flagged.Add(dev);
                    break;
                }
            }
        }

        return flagged;
    }
}
