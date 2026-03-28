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

    private int _consecutiveFreezeCount;
    private DateTime _freezeStartTime;
    private bool _inFreeze;

    // Disk-stall detection state (high disk latency while CPU is not fully idle)
    private bool _inDiskStall;
    private DateTime _diskStallStartTime;

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

        // ── Disk-stall detection ────────────────────────────────────────────
        // A disk latency spike above the threshold causes a perceived freeze even
        // when the CPU is still active (e.g. game stutter, UI hang during I/O).
        // Guarded by !_inFreeze because during a hard freeze disk latency is near
        // zero, so the two detectors are mutually exclusive by nature; the guard
        // also avoids emitting duplicate events for the same stall period.
        bool hasDiskLatencySpike = diskRead >= DiskHighLatencyThreshold || diskWrite >= DiskHighLatencyThreshold;

        if (hasDiskLatencySpike)
        {
            if (!_inDiskStall && !_inFreeze)
            {
                _inDiskStall = true;
                _diskStallStartTime = DateTime.Now;
            }
        }
        else
        {
            if (_inDiskStall)
            {
                double durationSeconds = (DateTime.Now - _diskStallStartTime).TotalSeconds;
                var freezeEvent = BuildFreezeEvent(durationSeconds, usbDevices, eventIndex);
                _inDiskStall = false;
                FreezeDetected?.Invoke(this, freezeEvent);
            }
        }
    }

    private FreezeEvent BuildFreezeEvent(double durationSeconds, List<string> usbDevices, int eventIndex)
    {
        // Capture pre-freeze metric snapshots
        int sampleCount = SystemMonitor.PreFreezeWindowSamples;
        var preFreeze = new Dictionary<string, double[]>
        {
            [MetricNames.Cpu]        = _monitor.GetLastSamples(MetricNames.Cpu,        sampleCount),
            [MetricNames.Ram]        = _monitor.GetLastSamples(MetricNames.Ram,        sampleCount),
            [MetricNames.DiskRead]   = _monitor.GetLastSamples(MetricNames.DiskRead,   sampleCount),
            [MetricNames.DiskWrite]  = _monitor.GetLastSamples(MetricNames.DiskWrite,  sampleCount),
            [MetricNames.Dpc]        = _monitor.GetLastSamples(MetricNames.Dpc,        sampleCount),
            [MetricNames.Interrupt]  = _monitor.GetLastSamples(MetricNames.Interrupt,  sampleCount),
            [MetricNames.Gpu]        = _monitor.GetLastSamples(MetricNames.Gpu,        sampleCount),
            [MetricNames.CpuTempC]   = _monitor.GetLastSamples(MetricNames.CpuTempC,   sampleCount),
            [MetricNames.GpuTempC]   = _monitor.GetLastSamples(MetricNames.GpuTempC,   sampleCount),
            [MetricNames.NvmeTempC]  = _monitor.GetLastSamples(MetricNames.NvmeTempC,  sampleCount),
            [MetricNames.PageFaults] = _monitor.GetLastSamples(MetricNames.PageFaults, sampleCount),
            [MetricNames.NetIn]      = _monitor.GetLastSamples(MetricNames.NetIn,      sampleCount),
        };

        bool tdrDetected = GpuMonitor.CheckForTdrEvent(10);

        // Snapshot current thermal and health readings at freeze time
        double cpuTempAtFreeze  = _monitor.LatestCpuTempC;
        double gpuTempAtFreeze  = _monitor.LatestGpuTempC;
        double nvmeTempAtFreeze = _monitor.LatestNvmeTempC;
        string driveHealth      = _monitor.LatestDriveHealth.ToDisplayString();

        var (cause, details) = AnalyzeCause(
            preFreeze, usbDevices, tdrDetected,
            cpuTempAtFreeze, gpuTempAtFreeze, nvmeTempAtFreeze);

        var topProcesses = GetTopProcesses();

        return new FreezeEvent
        {
            Index                  = eventIndex,
            Timestamp              = _freezeStartTime,
            DurationSeconds        = durationSeconds,
            MostLikelyCause        = cause,
            Details                = details,
            PreFreezeMetrics       = preFreeze,
            TopProcessesAtFreezeTime = topProcesses,
            ConnectedUsbDevices    = new List<string>(usbDevices),
            TdrDetected            = tdrDetected,
            SystemEventLogEntries  = EventLogMonitor.GetRecentEvents(15),
            CpuTempCAtFreeze       = cpuTempAtFreeze,
            GpuTempCAtFreeze       = gpuTempAtFreeze,
            NvmeTempCAtFreeze      = nvmeTempAtFreeze,
            DriveHealthAtFreeze    = driveHealth,
        };
    }

    private static (string cause, string details) AnalyzeCause(
        Dictionary<string, double[]> preFreeze,
        List<string> usbDevices,
        bool tdrDetected,
        double cpuTempC,
        double gpuTempC,
        double nvmeTempC)
    {
        // ── 1. CPU thermal throttling ─────────────────────────────────────────
        if (cpuTempC >= 95.0)
            return ("CPU thermal throttling",
                    $"CPU temperature was {cpuTempC:F0}°C at freeze time (throttle threshold ~95°C). " +
                    "Check that the CPU cooler is seated correctly, thermal paste has not dried out, " +
                    "and case airflow is adequate. Clean dust filters and heatsink fins.");

        // ── 2. GPU thermal throttling ──────────────────────────────────────────
        if (gpuTempC >= 83.0)
            return ("GPU thermal throttling",
                    $"GPU temperature was {gpuTempC:F0}°C at freeze time (typical throttle threshold ~83°C). " +
                    "Improve GPU cooling: replace thermal pads/paste, increase fan curve, or improve case airflow. " +
                    "Use MSI Afterburner to monitor GPU core temp over time.");

        // ── 3. DPC Latency spike ───────────────────────────────────────────────
        if (preFreeze.TryGetValue(MetricNames.Dpc, out var dpcSamples) && dpcSamples.Length > 0)
        {
            double avgDpc = dpcSamples.Average();
            if (avgDpc > DpcHighThreshold)
                return ("High DPC Latency — likely a driver issue",
                        $"Average DPC % was {avgDpc:F1}% in the 3s before freeze. This usually indicates a misbehaving driver. " +
                        "Use LatencyMon to identify the offending driver. Common culprits: audio drivers, USB host controller drivers, NVIDIA drivers.");
        }

        // ── 4. Interrupt spike ────────────────────────────────────────────────
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

        // ── 5. NVMe thermal throttling (disk stall + elevated NVMe temp) ──────
        double maxDisk = 0;
        if (preFreeze.TryGetValue(MetricNames.DiskRead,  out var dr) && dr.Length  > 0) maxDisk = Math.Max(maxDisk, dr.Max());
        if (preFreeze.TryGetValue(MetricNames.DiskWrite, out var dw) && dw.Length > 0) maxDisk = Math.Max(maxDisk, dw.Max());

        if (maxDisk > DiskHighLatencyThreshold)
        {
            if (nvmeTempC >= 65.0)
                return ("NVMe thermal throttling",
                        $"Peak disk latency was {maxDisk:F1}ms and NVMe temperature was {nvmeTempC:F0}°C " +
                        "(drives begin throttling in the 65-70°C range). " +
                        "Add an NVMe heatsink, improve case airflow, or move the drive to a cooler M.2 slot. " +
                        "Sustained sequential workloads (large file transfers, game installs) commonly trigger this.");

            return ("NVMe I/O stall",
                    $"Peak disk latency was {maxDisk:F1}ms before freeze. This could indicate NVMe thermal throttling " +
                    "(check NVMe temp in the graph above), driver issues, or background disk-intensive operations. " +
                    "Also check CrystalDiskInfo for SMART errors.");
        }

        // ── 6. Memory pressure (high RAM + high page faults) ──────────────────
        if (preFreeze.TryGetValue(MetricNames.Ram, out var ramSamples) && ramSamples.Length > 0 &&
            preFreeze.TryGetValue(MetricNames.PageFaults, out var pfSamples) && pfSamples.Length > 0)
        {
            double avgRam = ramSamples.Average();
            double avgPf  = pfSamples.Average();
            if (avgRam > 90.0 && avgPf > 5000)
                return ("Memory pressure — system paging to disk",
                        $"RAM utilisation was {avgRam:F0}% with {avgPf:F0} page faults/sec before freeze. " +
                        "The system is heavily swapping to the pagefile. Close memory-hungry applications, " +
                        "add more RAM, or ensure the pagefile is on a fast NVMe drive.");
        }

        // ── 7. GPU TDR event ──────────────────────────────────────────────────
        if (tdrDetected)
            return ("NVIDIA GPU TDR (Timeout Detection & Recovery) — update GPU drivers",
                    "A GPU TDR event (EventID 4101 or nvlddmkm Event 13) was detected in the System event log within 10 seconds " +
                    "of this freeze. Update your NVIDIA RTX 3070 drivers. If issue persists, check GPU stability with FurMark.");

        // ── 8. Background process burst ────────────────────────────────────────
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

        // ── 9. Unknown ────────────────────────────────────────────────────────
        return ("Unknown — no clear spike detected before freeze",
                "No significant DPC, interrupt, disk, thermal, or GPU anomaly was detected before this freeze. " +
                "Consider enabling Windows Performance Recorder (WPR) during next occurrence for ETW-level analysis.");
    }

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
