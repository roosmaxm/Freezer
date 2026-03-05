using System.Diagnostics;
using System.Management;

namespace Freezer;

/// <summary>
/// Monitors GPU usage via WMI performance counters.
/// Falls back gracefully if counters are unavailable.
/// </summary>
public class GpuMonitor : IDisposable
{
    private PerformanceCounter? _gpuCounter;
    private bool _wmiAvailable;
    private ManagementObjectSearcher? _searcher;
    private bool _disposed;
    private readonly object _lock = new();

    public bool IsAvailable => _gpuCounter != null || _wmiAvailable;

    public void Initialize(List<string> warnings)
    {
        // Try WMI-based GPU performance counter first
        try
        {
            var cat = new PerformanceCounterCategory("GPU Engine");
            var instances = cat.GetInstanceNames()
                              .Where(n => n.Contains("engtype_3D", StringComparison.OrdinalIgnoreCase))
                              .ToArray();
            if (instances.Length > 0)
            {
                _gpuCounter = new PerformanceCounter("GPU Engine", "Utilization Percentage", instances[0], true);
                _gpuCounter.NextValue(); // prime
                return;
            }
        }
        catch { /* fall through */ }

        // Try WMI query
        try
        {
            _searcher = new ManagementObjectSearcher(
                "root\\CIMV2",
                "SELECT * FROM Win32_PerfFormattedData_GPUPerformanceCounters_GPUEngine WHERE Name LIKE '%engtype_3D%'");
            // Test query
            foreach (ManagementObject obj in _searcher.Get())
            {
                _ = obj; // just test it works
                _wmiAvailable = true;
                break;
            }
            if (!_wmiAvailable)
            {
                warnings.Add("GPU WMI counter returned no results — GPU usage will show N/A. Consider installing HWiNFO.");
                _searcher.Dispose();
                _searcher = null;
            }
        }
        catch (Exception ex)
        {
            warnings.Add($"GPU monitoring unavailable: {ex.Message} — GPU usage will show N/A.");
            _searcher?.Dispose();
            _searcher = null;
        }
    }

    /// <summary>
    /// Returns GPU utilization as a percentage, or -1 if unavailable.
    /// </summary>
    public double ReadGpuUsage()
    {
        if (_disposed) return -1;

        // Try performance counter
        if (_gpuCounter != null)
        {
            try { return _gpuCounter.NextValue(); }
            catch { _gpuCounter = null; }
        }

        // Try WMI
        if (_wmiAvailable && _searcher != null)
        {
            try
            {
                double total = 0;
                int count = 0;
                lock (_lock)
                {
                    foreach (ManagementObject obj in _searcher.Get())
                    {
                        if (obj["UtilizationPercentage"] is ulong val)
                        {
                            total += val;
                            count++;
                        }
                    }
                }
                return count > 0 ? total / count : -1;
            }
            catch { return -1; }
        }

        return -1;
    }

    /// <summary>
    /// Check Windows Event Log for GPU TDR events near the given time.
    /// Returns true if a TDR was found within the last <paramref name="windowSeconds"/> seconds.
    /// </summary>
    public static bool CheckForTdrEvent(int windowSeconds = 10)
    {
        try
        {
            var cutoff = DateTime.Now.AddSeconds(-windowSeconds);
            using var log = new EventLog("System");
            int count = log.Entries.Count;
            // Iterate from the most recent entries backwards; break as soon as entries are too old
            for (int i = count - 1; i >= 0; i--)
            {
                EventLogEntry entry = log.Entries[i];
                if (entry.TimeGenerated < cutoff) break;
                // EventID 4101 (TDR) or 13 from nvlddmkm
                if (entry.InstanceId == 4101 ||
                    (entry.InstanceId == 13 && entry.Source.Equals("nvlddmkm", StringComparison.OrdinalIgnoreCase)))
                {
                    return true;
                }
            }
        }
        catch { /* Event log access may fail */ }
        return false;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _gpuCounter?.Dispose();
        _searcher?.Dispose();
    }
}
