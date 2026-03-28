using System.Management;

namespace Freezer;

/// <summary>
/// Reads system temperatures from two sources:
/// 1. ACPI thermal zones via WMI root\WMI (CPU, GPU, platform zones)
/// 2. Drive SMART attribute 0xC2/0xBE via WMI root\WMI for NVMe/SSD temperatures
///
/// All temperatures are returned in degrees Celsius.
/// -1 indicates a value that could not be read on this system.
/// </summary>
public class ThermalMonitor : IDisposable
{
    private ManagementObjectSearcher? _thermalZoneSearcher;
    private ManagementObjectSearcher? _smartSearcher;
    private bool _disposed;

    // Thermal reads are cached so that the fast 200ms polling loop pays the
    // WMI cost only every N ticks.
    private ThermalReading _cached = new();
    private int _ticksSinceRefresh;
    private const int ThermalRefreshTicks = 10;  // every 10 × 200ms = 2 s
    private const int SmartRefreshTicks   = 25;  // every 25 × 200ms = 5 s
    private int _smartTicks;

    public void Initialize(List<string> warnings)
    {
        // ACPI thermal zones
        try
        {
            _thermalZoneSearcher = new ManagementObjectSearcher(
                "root\\WMI",
                "SELECT InstanceName, CurrentTemperature FROM MSAcpi_ThermalZoneTemperature");

            // Probe to confirm the class exists
            bool any = false;
            foreach (ManagementObject _ in _thermalZoneSearcher.Get()) { any = true; break; }

            if (!any)
                warnings.Add(
                    "No ACPI thermal zones found — CPU/GPU temps will show N/A. " +
                    "This is normal on custom-built PCs; use HWiNFO64 for precise core temps.");
        }
        catch (Exception ex)
        {
            warnings.Add($"Thermal zone monitoring unavailable: {ex.Message}");
            _thermalZoneSearcher?.Dispose();
            _thermalZoneSearcher = null;
        }

        // Drive SMART data (for NVMe temperature when no ACPI zone covers it)
        try
        {
            _smartSearcher = new ManagementObjectSearcher(
                "root\\WMI",
                "SELECT InstanceName, VendorSpecific FROM MSStorageDriver_ATAPISmartData");

            // Probe
            foreach (ManagementObject _ in _smartSearcher.Get()) break;
        }
        catch (Exception ex)
        {
            warnings.Add($"Drive SMART temperature monitoring unavailable: {ex.Message}");
            _smartSearcher?.Dispose();
            _smartSearcher = null;
        }
    }

    /// <summary>
    /// Returns the most recent temperature reading.
    /// Call this on every monitor tick (200ms); internally refreshes at a slower cadence.
    /// </summary>
    public ThermalReading ReadTemperatures()
    {
        if (_disposed) return _cached;

        _ticksSinceRefresh++;

        if (_ticksSinceRefresh >= ThermalRefreshTicks)
        {
            _ticksSinceRefresh = 0;

            var reading = ReadThermalZones();

            // If no NVMe temperature was obtained from ACPI zones, poll SMART
            _smartTicks++;
            if (reading.NvmeTempC < 0 && _smartTicks >= SmartRefreshTicks)
            {
                _smartTicks = 0;
                reading.NvmeTempC = ReadSmartTemperature();
            }
            else if (reading.NvmeTempC >= 0)
            {
                _smartTicks = 0; // reset — ACPI covered it
            }

            _cached = reading;
        }

        return _cached;
    }

    // ── ACPI thermal zones ─────────────────────────────────────────────────

    private ThermalReading ReadThermalZones()
    {
        var reading = new ThermalReading();
        if (_thermalZoneSearcher == null) return reading;

        try
        {
            foreach (ManagementObject obj in _thermalZoneSearcher.Get())
            {
                string instanceName = obj["InstanceName"]?.ToString() ?? string.Empty;
                if (obj["CurrentTemperature"] is not uint raw) continue;

                // ACPI reports temperature in tenths of Kelvin
                double tempC = raw / 10.0 - 273.15;
                if (tempC < 0 || tempC > 150) continue;    // sanity filter

                reading.AllZones[instanceName] = tempC;

                string lower = instanceName.ToLowerInvariant();

                if (IsCpuZone(lower))
                {
                    if (tempC > reading.CpuTempC)       // keep highest zone
                        reading.CpuTempC = tempC;
                }
                else if (IsGpuZone(lower))
                {
                    if (tempC > reading.GpuTempC)
                        reading.GpuTempC = tempC;
                }
                else if (IsDriveZone(lower))
                {
                    if (tempC > reading.NvmeTempC)
                        reading.NvmeTempC = tempC;
                }
            }
        }
        catch { /* WMI transient failure — return what we have */ }

        return reading;
    }

    private static bool IsCpuZone(string lower) =>
        lower.Contains("cpu") || lower.Contains("core") || lower.Contains("proc") ||
        lower.Contains("tz00") || lower.Contains("tz01") || lower.Contains("cput");

    private static bool IsGpuZone(string lower) =>
        lower.Contains("gpu") || lower.Contains("vga") || lower.Contains("gput") ||
        lower.Contains("disp") || lower.Contains("dGPU", StringComparison.OrdinalIgnoreCase);

    private static bool IsDriveZone(string lower) =>
        lower.Contains("nvme") || lower.Contains("ssd") ||
        lower.Contains("m.2") || lower.Contains("strg") ||
        lower.Contains("stor") || lower.Contains("disk");

    // ── SMART temperature ──────────────────────────────────────────────────

    /// <summary>
    /// Parses SMART attribute 0xC2 (Temperature_Celsius) or 0xBE (Airflow Temperature)
    /// from the first accessible drive. Returns the temperature in °C, or -1 if unavailable.
    /// </summary>
    private double ReadSmartTemperature()
    {
        if (_smartSearcher == null) return -1;
        try
        {
            double highest = -1;
            foreach (ManagementObject obj in _smartSearcher.Get())
            {
                if (obj["VendorSpecific"] is not byte[] data || data.Length < 14) continue;

                // SMART layout: 2-byte header, then up to 30 attribute records of 12 bytes each
                int maxAttrs = Math.Min(30, (data.Length - 2) / 12);
                for (int i = 0; i < maxAttrs; i++)
                {
                    int offset = 2 + i * 12;
                    byte attrId = data[offset];
                    if (attrId == 0) break; // end sentinel

                    // 0xC2 = Temperature_Celsius, 0xBE = Airflow_Temperature_Cel
                    if (attrId != 0xC2 && attrId != 0xBE) continue;

                    // Raw value starts at offset + 5 (6 bytes, LE 48-bit).
                    // For virtually all drives the lowest byte is the current temperature in °C.
                    double tempC = data[offset + 5];
                    if (tempC > 0 && tempC < 100)
                        highest = Math.Max(highest, tempC);
                }
            }
            return highest;
        }
        catch { return -1; }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _thermalZoneSearcher?.Dispose();
        _smartSearcher?.Dispose();
    }
}

/// <summary>
/// Snapshot of temperature readings from a single poll cycle.
/// All temperatures are in degrees Celsius; -1 means unavailable on this system.
/// </summary>
public class ThermalReading
{
    public double CpuTempC  { get; set; } = -1;
    public double GpuTempC  { get; set; } = -1;
    public double NvmeTempC { get; set; } = -1;

    /// <summary>All named ACPI thermal zones and their temperatures (°C).</summary>
    public Dictionary<string, double> AllZones { get; } = new();
}
