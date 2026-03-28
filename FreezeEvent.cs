namespace Freezer;

/// <summary>
/// Represents a single detected freeze event with all captured metrics.
/// </summary>
public class FreezeEvent
{
    public int Index { get; set; }
    public DateTime Timestamp { get; set; }
    public double DurationSeconds { get; set; }
    public string MostLikelyCause { get; set; } = "Unknown";
    public string Details { get; set; } = string.Empty;

    /// <summary>
    /// Metric name → array of last ~15 samples captured before the freeze.
    /// </summary>
    public Dictionary<string, double[]> PreFreezeMetrics { get; set; } = new();

    /// <summary>
    /// Top processes by CPU/memory at the time of the freeze.
    /// </summary>
    public List<string> TopProcessesAtFreezeTime { get; set; } = new();

    /// <summary>
    /// USB devices connected at the time of the freeze.
    /// </summary>
    public List<string> ConnectedUsbDevices { get; set; } = new();

    /// <summary>
    /// Whether a GPU TDR event was detected around the freeze time.
    /// </summary>
    public bool TdrDetected { get; set; }

    /// <summary>
    /// Relevant Windows Event Log entries (System + Application) captured within
    /// 15 seconds of the freeze. Each entry is a human-readable formatted string.
    /// </summary>
    public List<string> SystemEventLogEntries { get; set; } = new();

    // ── Thermal snapshot ──────────────────────────────────────────────────

    /// <summary>CPU temperature in °C at freeze time (-1 = unavailable).</summary>
    public double CpuTempCAtFreeze  { get; set; } = -1;

    /// <summary>GPU temperature in °C at freeze time (-1 = unavailable).</summary>
    public double GpuTempCAtFreeze  { get; set; } = -1;

    /// <summary>NVMe/SSD temperature in °C at freeze time (-1 = unavailable).</summary>
    public double NvmeTempCAtFreeze { get; set; } = -1;

    /// <summary>Drive health summary captured at freeze time.</summary>
    public string DriveHealthAtFreeze { get; set; } = string.Empty;
}

/// <summary>
/// A notable Windows Event Log entry surfaced in the main event list independently
/// of freeze detection (e.g. application crashes, kernel filter-driver warnings).
/// </summary>
/// <param name="Time">When the event was generated.</param>
/// <param name="Category">Human-readable category, e.g. "Application Crash" or "System Warning".</param>
/// <param name="Message">Full formatted event message.</param>
public record EventLogNotification(DateTime Time, string Category, string Message);
