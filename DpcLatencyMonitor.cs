using System.Diagnostics;

namespace Freezer;

/// <summary>
/// Monitors DPC (Deferred Procedure Call) latency via Windows Performance Counters.
/// Attempts multiple counter sources for best coverage.
/// </summary>
public class DpcLatencyMonitor : IDisposable
{
    private PerformanceCounter? _dpcTimeCounter;
    private PerformanceCounter? _dpcRateCounter;
    private bool _disposed;

    public void Initialize(List<string> warnings)
    {
        // Primary: % DPC Time (already tracked in SystemMonitor; this is supplemental)
        _dpcTimeCounter = TryCreate("Processor", "% DPC Time", "_Total", warnings);

        // Secondary: DPC Rate from Processor Information category
        _dpcRateCounter = TryCreate("Processor Information", "DPC Rate", "_Total", warnings);

        // Prime the counters
        try { _dpcTimeCounter?.NextValue(); } catch { }
        try { _dpcRateCounter?.NextValue(); } catch { }
    }

    private static PerformanceCounter? TryCreate(string category, string counter, string instance, List<string> warnings)
    {
        try
        {
            var pc = new PerformanceCounter(category, counter, instance, true);
            return pc;
        }
        catch (Exception ex)
        {
            warnings.Add($"DPC counter unavailable: {category}\\{counter} — {ex.Message}");
            return null;
        }
    }

    /// <summary>
    /// Reads the current DPC % time. Returns 0 if unavailable.
    /// </summary>
    public double ReadDpcPercent()
    {
        if (_disposed) return 0;
        try { return _dpcTimeCounter?.NextValue() ?? 0; }
        catch { return 0; }
    }

    /// <summary>
    /// Reads the DPC rate (DPCs/sec) if available. Returns -1 if unavailable.
    /// </summary>
    public double ReadDpcRate()
    {
        if (_disposed) return -1;
        try { return _dpcRateCounter?.NextValue() ?? -1; }
        catch { return -1; }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _dpcTimeCounter?.Dispose();
        _dpcRateCounter?.Dispose();
    }
}
