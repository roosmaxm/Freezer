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
}

/// <summary>
/// Represents an application crash or hang event sourced from the Windows Application
/// Event Log (e.g. Application Error EventID 1000 or Application Hang EventID 1002).
/// Displayed in the event list independently of freeze detection.
/// </summary>
public record AppCrashEvent(DateTime Time, string Message);
