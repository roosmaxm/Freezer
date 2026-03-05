using System.Diagnostics;

namespace Freezer;

/// <summary>
/// Detects system freeze events by monitoring metric drops and analyzing pre-freeze data.
/// </summary>
public class FreezeDetector
{
    // Thresholds for freeze detection
    private const double CpuFreezeThreshold = 5.0;
    private const double GpuFreezeThreshold = 5.0;
    private const double DiskFreezeThreshold = 0.5; // ms

    // Cause analysis thresholds
    private const double DpcHighThreshold = 15.0;
    private const double InterruptHighThreshold = 20.0;
    private const double DiskHighLatencyThreshold = 50.0;

    // Time window (seconds) used when scanning event log for events correlated with a freeze
    private const int EventWindowSeconds = 15;

    private int _consecutiveFreezeCount;
    private DateTime _freezeStartTime;
    private bool _inFreeze;

    private readonly SystemMonitor _monitor;

    /// <summary>
    /// Fired when a new freeze event is detected and fully characterized.
    /// </summary>
    public event EventHandler<FreezeEvent>? FreezeDetected;

    public FreezeDetector(SystemMonitor monitor)
    {
        _monitor = monitor;
    }

    /// <summary>
    /// Called on each monitor sample tick. Evaluates current metrics for freeze conditions.
    /// </summary>
    public void Evaluate(List<string> usbDevices, int eventIndex)
    {
        double cpu = _monitor.LatestCpu;
        double gpu = _monitor.LatestGpuPercent;
        double diskRead = _monitor.LatestDiskReadMs;
        double diskWrite = _monitor.LatestDiskWriteMs;

        bool cpuFrozen = cpu < CpuFreezeThreshold;
        bool diskFrozen = diskRead < DiskFreezeThreshold && diskWrite < DiskFreezeThreshold;
        // If GPU data unavailable (−1), only use CPU and disk
        bool gpuFrozen = gpu < 0 || gpu < GpuFreezeThreshold;

        bool allFrozen = cpuFrozen && diskFrozen && gpuFrozen;

        if (allFrozen)
        {
            _consecutiveFreezeCount++;
            if (_consecutiveFreezeCount == 2 && !_inFreeze)
            {
                // Freeze just started
                _inFreeze = true;
                _freezeStartTime = DateTime.Now.AddMilliseconds(-200); // back-date by 1 sample
            }
        }
        else
        {
            if (_inFreeze)
            {
                // Freeze ended — characterize it
                double durationSeconds = (DateTime.Now - _freezeStartTime).TotalSeconds;
                var freezeEvent = BuildFreezeEvent(durationSeconds, usbDevices, eventIndex);
                _inFreeze = false;
                _consecutiveFreezeCount = 0;
                FreezeDetected?.Invoke(this, freezeEvent);
            }
            else
            {
                _consecutiveFreezeCount = 0;
            }
        }
    }

    private FreezeEvent BuildFreezeEvent(double durationSeconds, List<string> usbDevices, int eventIndex)
    {
        // Capture pre-freeze metric snapshots
        int sampleCount = SystemMonitor.PreFreezeWindowSamples;
        var preFreeze = new Dictionary<string, double[]>
        {
            [MetricNames.Cpu] = _monitor.GetLastSamples(MetricNames.Cpu, sampleCount),
            [MetricNames.Ram] = _monitor.GetLastSamples(MetricNames.Ram, sampleCount),
            [MetricNames.DiskRead] = _monitor.GetLastSamples(MetricNames.DiskRead, sampleCount),
            [MetricNames.DiskWrite] = _monitor.GetLastSamples(MetricNames.DiskWrite, sampleCount),
            [MetricNames.Dpc] = _monitor.GetLastSamples(MetricNames.Dpc, sampleCount),
            [MetricNames.Interrupt] = _monitor.GetLastSamples(MetricNames.Interrupt, sampleCount),
            [MetricNames.Gpu] = _monitor.GetLastSamples(MetricNames.Gpu, sampleCount),
        };

        bool tdrDetected = GpuMonitor.CheckForTdrEvent(10);

        var (cause, details) = AnalyzeCause(preFreeze, usbDevices, tdrDetected);

        var topProcesses = GetTopProcesses();

        return new FreezeEvent
        {
            Index = eventIndex,
            Timestamp = _freezeStartTime,
            DurationSeconds = durationSeconds,
            MostLikelyCause = cause,
            Details = details,
            PreFreezeMetrics = preFreeze,
            TopProcessesAtFreezeTime = topProcesses,
            ConnectedUsbDevices = new List<string>(usbDevices),
            TdrDetected = tdrDetected,
            SystemEventLogEntries = EventLogMonitor.GetRecentEvents(EventWindowSeconds)
        };
    }

    private static (string cause, string details) AnalyzeCause(
        Dictionary<string, double[]> preFreeze,
        List<string> usbDevices,
        bool tdrDetected)
    {
        // 1. DPC Latency spike
        if (preFreeze.TryGetValue(MetricNames.Dpc, out var dpcSamples) && dpcSamples.Length > 0)
        {
            double avgDpc = dpcSamples.Average();
            if (avgDpc > DpcHighThreshold)
                return ("High DPC Latency — likely a driver issue",
                        $"Average DPC % was {avgDpc:F1}% in the 3s before freeze. This usually indicates a misbehaving driver. " +
                        "Use LatencyMon to identify the offending driver. Common culprits: audio drivers, USB host controller drivers, NVIDIA drivers.");
        }

        // 2. Interrupt spike
        if (preFreeze.TryGetValue(MetricNames.Interrupt, out var intSamples) && intSamples.Length > 0)
        {
            double avgInt = intSamples.Average();
            if (avgInt > InterruptHighThreshold)
            {
                var usbList = usbDevices.Count > 0
                    ? string.Join("; ", usbDevices.Take(5))
                    : "none detected";
                return ("Interrupt storm — check USB devices",
                        $"Average interrupt % was {avgInt:F1}% before freeze. Connected USB devices: {usbList}. " +
                        "Try disconnecting Xbox controller, wireless mouse/keyboard, or wireless headphones one at a time.");
            }
        }

        // 3. NVMe disk stall
        if (preFreeze.TryGetValue(MetricNames.DiskRead, out var diskReadSamples) &&
            preFreeze.TryGetValue(MetricNames.DiskWrite, out var diskWriteSamples))
        {
            double maxDisk = 0;
            if (diskReadSamples.Length > 0) maxDisk = Math.Max(maxDisk, diskReadSamples.Max());
            if (diskWriteSamples.Length > 0) maxDisk = Math.Max(maxDisk, diskWriteSamples.Max());
            if (maxDisk > DiskHighLatencyThreshold)
                return ("NVMe I/O stall",
                        $"Peak disk latency was {maxDisk:F1}ms before freeze. This could indicate NVMe thermal throttling, " +
                        "driver issues, or background disk-intensive operations.");
        }

        // 4. GPU TDR event
        if (tdrDetected)
            return ("NVIDIA GPU TDR (Timeout Detection & Recovery) — update GPU drivers",
                    "A GPU TDR event (EventID 4101 or nvlddmkm Event 13) was detected in the System event log within 10 seconds " +
                    "of this freeze. Update your NVIDIA RTX 3070 drivers. If issue persists, check GPU stability with FurMark.");

        // 5. DCOM timeout or COM permission error
        var recentEvents = EventLogMonitor.GetRecentEvents(EventWindowSeconds);
        if (HasDcomEvent(recentEvents, 10010))
            return ("DCOM server registration timeout",
                    "A DistributedCOM EventID 10010 was logged near the freeze: a COM server did not register within the " +
                    "required timeout. This can stall threads waiting for a COM call to complete. Check Component Services " +
                    "and consider re-registering the offending server (see CLSID in the event message).");
        if (HasDcomEvent(recentEvents, 10016))
            return ("COM activation permission denied",
                    "A DistributedCOM EventID 10016 was logged near the freeze: an application was denied Local Activation " +
                    "permission for a COM server. This can cause a brief hang in any process that depends on that COM object. " +
                    "Grant activation permissions via Component Services (dcomcnfg.exe) for the CLSID listed in the event.");

        // 6. Background process burst
        var suspectProcesses = new[] { "MsMpEng", "SearchIndexer", "WmiPrvSE", "TiWorker", "NvContainerLocalSystem" };
        var topProcs = GetTopProcesses();
        foreach (var proc in topProcs)
        {
            foreach (var suspect in suspectProcesses)
            {
                if (proc.Contains(suspect, StringComparison.OrdinalIgnoreCase))
                    return ($"Background process burst — {suspect}",
                            $"Process '{proc}' was among the top CPU/memory consumers at freeze time. " +
                            "This may have caused a brief system stall.");
            }
        }

        // 7. Unknown
        return ("Unknown — no clear spike detected before freeze",
                "No significant DPC, interrupt, disk, or GPU anomaly was detected before this freeze. " +
                "Consider enabling Windows Performance Recorder (WPR) during next occurrence for ETW-level analysis.");
    }

    /// <summary>
    /// Returns true if any formatted event entry in <paramref name="events"/> matches the
    /// DistributedCOM source with the specified <paramref name="eventId"/>.
    /// </summary>
    private static bool HasDcomEvent(List<string> events, int eventId) =>
        events.Any(e =>
            e.Contains("DistributedCOM", StringComparison.OrdinalIgnoreCase) &&
            e.Contains($"EventID={eventId}", StringComparison.OrdinalIgnoreCase));

    private static List<string> GetTopProcesses()
    {
        var result = new List<string>();
        try
        {
            var procs = Process.GetProcesses()
                               .OrderByDescending(p =>
                               {
                                   try { return p.WorkingSet64; }
                                   catch { return 0L; }
                               })
                               .Take(10)
                               .ToList();

            foreach (var p in procs)
            {
                try
                {
                    string path = string.Empty;
                    try { path = p.MainModule?.FileName ?? string.Empty; }
                    catch { }
                    result.Add($"{p.ProcessName} (PID {p.Id}, RAM: {p.WorkingSet64 / 1024 / 1024}MB){(string.IsNullOrEmpty(path) ? "" : $" [{path}]")}");
                }
                catch { result.Add(p.ProcessName); }
            }
        }
        catch { /* Process access denied is common in production */ }
        return result;
    }
}
