using System.Management;

namespace Freezer;

/// <summary>
/// Reads drive health indicators from Windows SMART data via WMI.
/// Surfaces reallocated sectors, pending sectors, uncorrectable errors, and
/// overall drive status so that failing drives are flagged before data loss.
///
/// Health is refreshed every 30 seconds (not on every 200ms tick) because
/// SMART data changes slowly and WMI SMART queries can be slow on some systems.
/// </summary>
public class DriveHealthMonitor : IDisposable
{
    private ManagementObjectSearcher? _smartSearcher;
    private ManagementObjectSearcher? _driveSearcher;
    private bool _disposed;

    private DriveHealthSummary _cached = DriveHealthSummary.Unknown;
    private DateTime _lastRefresh = DateTime.MinValue;
    private static readonly TimeSpan RefreshInterval = TimeSpan.FromSeconds(30);

    public void Initialize(List<string> warnings)
    {
        try
        {
            _smartSearcher = new ManagementObjectSearcher(
                "root\\WMI",
                "SELECT InstanceName, VendorSpecific FROM MSStorageDriver_ATAPISmartData");
        }
        catch
        {
            _smartSearcher = null;
        }

        try
        {
            _driveSearcher = new ManagementObjectSearcher(
                "root\\CIMV2",
                "SELECT Caption, Model, Status, Size FROM Win32_DiskDrive");
        }
        catch
        {
            _driveSearcher = null;
        }
    }

    /// <summary>
    /// Returns a (cached) drive health summary, refreshed every 30 seconds.
    /// </summary>
    public DriveHealthSummary GetHealth()
    {
        if (_disposed) return DriveHealthSummary.Unknown;
        if (DateTime.Now - _lastRefresh < RefreshInterval) return _cached;

        _lastRefresh = DateTime.Now;
        _cached = ReadHealth();
        return _cached;
    }

    private DriveHealthSummary ReadHealth()
    {
        var drives = new List<DriveHealthInfo>();

        // Base list from Win32_DiskDrive (model names + WMI status)
        if (_driveSearcher != null)
        {
            try
            {
                foreach (ManagementObject obj in _driveSearcher.Get())
                {
                    string model  = obj["Model"]?.ToString()
                                 ?? obj["Caption"]?.ToString()
                                 ?? "Unknown Drive";
                    string status = obj["Status"]?.ToString() ?? "Unknown";
                    drives.Add(new DriveHealthInfo { Model = model, WmiStatus = status });
                }
            }
            catch { }
        }

        // Overlay SMART attribute data
        if (_smartSearcher != null)
        {
            try
            {
                int idx = 0;
                foreach (ManagementObject obj in _smartSearcher.Get())
                {
                    if (obj["VendorSpecific"] is not byte[] data || data.Length < 14)
                    { idx++; continue; }

                    var attrs = ParseSmartAttributes(data);

                    var info = idx < drives.Count
                        ? drives[idx]
                        : new DriveHealthInfo { Model = $"Drive {idx}" };

                    if (attrs.TryGetValue(5,   out long realloc)) info.ReallocatedSectors      = realloc;
                    if (attrs.TryGetValue(187, out long uncorr))  info.ReportedUncorrectable   = uncorr;
                    if (attrs.TryGetValue(197, out long pending)) info.PendingSectors           = pending;
                    if (attrs.TryGetValue(198, out long offline)) info.OfflineUncorrectable     = offline;

                    if (idx >= drives.Count) drives.Add(info);
                    else drives[idx] = info;
                    idx++;
                }
            }
            catch { }
        }

        return new DriveHealthSummary(drives);
    }

    // ── SMART attribute parser ─────────────────────────────────────────────

    /// <summary>
    /// Parses the VendorSpecific SMART byte array into a map of attribute ID → raw value.
    /// Layout: 2-byte header, then up to 30 attribute records of 12 bytes each.
    /// </summary>
    private static Dictionary<int, long> ParseSmartAttributes(byte[] data)
    {
        var attrs = new Dictionary<int, long>();
        int maxAttrs = Math.Min(30, (data.Length - 2) / 12);

        for (int i = 0; i < maxAttrs; i++)
        {
            int offset = 2 + i * 12;
            byte id = data[offset];
            if (id == 0) break;

            // Raw value: 6 bytes at offset+5, little-endian 48-bit
            long raw = 0;
            for (int b = 0; b < 6; b++)
                raw |= (long)data[offset + 5 + b] << (b * 8);

            attrs[id] = raw;
        }
        return attrs;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _smartSearcher?.Dispose();
        _driveSearcher?.Dispose();
    }
}

// ── Data models ───────────────────────────────────────────────────────────────

/// <summary>SMART / WMI health data for a single physical drive.</summary>
public class DriveHealthInfo
{
    public string Model       { get; set; } = "Unknown";
    public string WmiStatus   { get; set; } = "Unknown";

    /// <summary>SMART attribute 5 — reallocated bad sectors (any > 0 is concerning).</summary>
    public long ReallocatedSectors    { get; set; } = -1;
    /// <summary>SMART attribute 197 — sectors waiting to be remapped.</summary>
    public long PendingSectors        { get; set; } = -1;
    /// <summary>SMART attribute 198 — sectors that could not be recovered.</summary>
    public long OfflineUncorrectable  { get; set; } = -1;
    /// <summary>SMART attribute 187 — uncorrectable errors reported by the controller.</summary>
    public long ReportedUncorrectable { get; set; } = -1;

    public bool HasErrors =>
        (ReallocatedSectors    >  0) ||
        (PendingSectors        >  0) ||
        (OfflineUncorrectable  >  0) ||
        (ReportedUncorrectable >  0) ||
        WmiStatus.Equals("Error", StringComparison.OrdinalIgnoreCase);

    public string HealthStatus
    {
        get
        {
            if (ReallocatedSectors > 10 || OfflineUncorrectable > 0 || ReportedUncorrectable > 0)
                return "⚠ CRITICAL";
            if (ReallocatedSectors > 0 || PendingSectors > 0)
                return "⚠ Warning";
            if (WmiStatus.Equals("Error", StringComparison.OrdinalIgnoreCase))
                return "⚠ Error";
            if (WmiStatus.Equals("OK", StringComparison.OrdinalIgnoreCase))
                return "✓ Good";
            return "?";
        }
    }

    public override string ToString()
    {
        var parts = new List<string> { $"{Model}: {HealthStatus}" };
        if (ReallocatedSectors    >= 0) parts.Add($"Realloc={ReallocatedSectors}");
        if (PendingSectors        >= 0) parts.Add($"Pending={PendingSectors}");
        if (OfflineUncorrectable  >= 0) parts.Add($"Offline={OfflineUncorrectable}");
        if (ReportedUncorrectable >= 0) parts.Add($"Uncorr={ReportedUncorrectable}");
        return string.Join(", ", parts);
    }
}

/// <summary>Aggregated health summary for all physical drives in the system.</summary>
public class DriveHealthSummary
{
    public static readonly DriveHealthSummary Unknown = new(new List<DriveHealthInfo>());

    public List<DriveHealthInfo> Drives    { get; }
    public bool             AnyErrors => Drives.Any(d => d.HasErrors);

    public DriveHealthSummary(List<DriveHealthInfo> drives) => Drives = drives;

    /// <summary>One-line display string for the status bar or current-values label.</summary>
    public string ToDisplayString()
    {
        if (Drives.Count == 0) return "Drive: N/A";
        if (AnyErrors)
            return string.Join(" | ", Drives.Where(d => d.HasErrors).Select(d => d.ToString()));
        return $"Drive: ✓ {Drives.Count} OK";
    }
}
