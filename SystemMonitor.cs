using System.Diagnostics;

namespace Freezer;

/// <summary>
/// Polls Windows Performance Counters every 200ms and maintains a rolling 30-second buffer.
/// </summary>
public class SystemMonitor : IDisposable
{
    // Number of samples to retain (30s / 200ms = 150 samples)
    private const int BufferSize = 150;
    // Samples used for pre-freeze snapshot (3s / 200ms = 15 samples)
    public const int PreFreezeWindowSamples = 15;

    private readonly System.Threading.Timer _timer;
    private readonly object _lock = new();
    private bool _disposed;

    // Performance counters — nullable; may fail on some systems
    private PerformanceCounter? _cpuCounter;
    private PerformanceCounter? _ramCounter;
    private PerformanceCounter? _diskReadCounter;
    private PerformanceCounter? _diskWriteCounter;
    private PerformanceCounter? _dpcCounter;
    private PerformanceCounter? _interruptCounter;
    private PerformanceCounter? _pageFaultsCounter;
    private readonly List<PerformanceCounter> _netInCounters  = new();
    private readonly List<PerformanceCounter> _netOutCounters = new();

    // Rolling buffers
    private readonly Queue<double> _cpuBuffer       = new();
    private readonly Queue<double> _ramBuffer       = new();
    private readonly Queue<double> _diskReadBuffer  = new();
    private readonly Queue<double> _diskWriteBuffer = new();
    private readonly Queue<double> _dpcBuffer       = new();
    private readonly Queue<double> _interruptBuffer = new();
    private readonly Queue<double> _gpuBuffer       = new();
    private readonly Queue<double> _cpuTempBuffer   = new();
    private readonly Queue<double> _gpuTempBuffer   = new();
    private readonly Queue<double> _nvmeTempBuffer  = new();
    private readonly Queue<double> _pageFaultsBuffer = new();
    private readonly Queue<double> _netInBuffer     = new();
    private readonly Queue<double> _netOutBuffer    = new();

    // Latest values (thread-safe via _lock)
    public double LatestCpu              { get; private set; }
    public double LatestRam              { get; private set; }
    public double LatestDiskReadMs       { get; private set; }
    public double LatestDiskWriteMs      { get; private set; }
    public double LatestDpcPercent       { get; private set; }
    public double LatestInterruptPercent { get; private set; }
    public double LatestGpuPercent       { get; private set; }
    public double LatestCpuTempC         { get; private set; } = -1;
    public double LatestGpuTempC         { get; private set; } = -1;
    public double LatestNvmeTempC        { get; private set; } = -1;
    public double LatestPageFaultsSec    { get; private set; }
    public double LatestNetInMbps        { get; private set; }
    public double LatestNetOutMbps       { get; private set; }
    public DriveHealthSummary LatestDriveHealth { get; private set; } = DriveHealthSummary.Unknown;

    public bool IsRunning { get; private set; }

    // Warnings for missing counters
    public List<string> InitWarnings { get; } = new();

    // Fired on each sample (on thread-pool thread)
    public event EventHandler? SampleTaken;

    private GpuMonitor?          _gpuMonitor;
    private DpcLatencyMonitor?   _dpcLatencyMonitor;
    private ThermalMonitor?      _thermalMonitor;
    private DriveHealthMonitor?  _driveHealthMonitor;

    public SystemMonitor()
    {
        _timer = new System.Threading.Timer(OnTick, null, Timeout.Infinite, Timeout.Infinite);
        InitCounters();
    }

    private void InitCounters()
    {
        _cpuCounter       = TryCreate("Processor",    "% Processor Time",    "_Total");
        _ramCounter       = TryCreate("Memory",       "% Committed Bytes In Use", null);
        _diskReadCounter  = TryCreate("PhysicalDisk", "Avg. Disk sec/Read",   "_Total");
        _diskWriteCounter = TryCreate("PhysicalDisk", "Avg. Disk sec/Write",  "_Total");
        _dpcCounter       = TryCreate("Processor",    "% DPC Time",           "_Total");
        _interruptCounter = TryCreate("Processor",    "% Interrupt Time",     "_Total");
        _pageFaultsCounter = TryCreate("Memory",      "Page Faults/sec",      null);

        // Network I/O — enumerate all interface instances and sum them
        try
        {
            var cat = new PerformanceCounterCategory("Network Interface");
            foreach (string inst in cat.GetInstanceNames())
            {
                var inC = TryCreate("Network Interface", "Bytes Received/sec", inst);
                if (inC != null) _netInCounters.Add(inC);
                var outC = TryCreate("Network Interface", "Bytes Sent/sec", inst);
                if (outC != null) _netOutCounters.Add(outC);
            }
        }
        catch { /* Network counters unavailable — silently omit */ }

        _gpuMonitor = new GpuMonitor();
        _gpuMonitor.Initialize(InitWarnings);

        _dpcLatencyMonitor = new DpcLatencyMonitor();
        _dpcLatencyMonitor.Initialize(InitWarnings);

        _thermalMonitor = new ThermalMonitor();
        _thermalMonitor.Initialize(InitWarnings);

        _driveHealthMonitor = new DriveHealthMonitor();
        _driveHealthMonitor.Initialize(InitWarnings);

        // Prime the counters (first call returns 0 or garbage)
        try { _cpuCounter?.NextValue();       } catch { }
        try { _ramCounter?.NextValue();       } catch { }
        try { _diskReadCounter?.NextValue();  } catch { }
        try { _diskWriteCounter?.NextValue(); } catch { }
        try { _dpcCounter?.NextValue();       } catch { }
        try { _interruptCounter?.NextValue(); } catch { }
        try { _pageFaultsCounter?.NextValue(); } catch { }
        foreach (var c in _netInCounters)  try { c.NextValue(); } catch { }
        foreach (var c in _netOutCounters) try { c.NextValue(); } catch { }
    }

    private PerformanceCounter? TryCreate(string category, string counterName, string? instance)
    {
        try
        {
            var counter = instance != null
                ? new PerformanceCounter(category, counterName, instance, true)
                : new PerformanceCounter(category, counterName, true);
            return counter;
        }
        catch (Exception ex)
        {
            InitWarnings.Add($"Counter unavailable: {category}\\{counterName} — {ex.Message}");
            return null;
        }
    }

    public void Start()
    {
        if (IsRunning) return;
        IsRunning = true;
        _timer.Change(200, 200);
    }

    public void Stop()
    {
        if (!IsRunning) return;
        IsRunning = false;
        _timer.Change(Timeout.Infinite, Timeout.Infinite);
    }

    private void OnTick(object? state)
    {
        double cpu        = SafeRead(_cpuCounter, 0);
        double ram        = SafeRead(_ramCounter, 0);
        // Disk counters return seconds; convert to milliseconds
        double diskRead   = SafeRead(_diskReadCounter,  0) * 1000.0;
        double diskWrite  = SafeRead(_diskWriteCounter, 0) * 1000.0;
        double dpc        = _dpcLatencyMonitor?.ReadDpcPercent() ?? SafeRead(_dpcCounter, 0);
        double interrupt  = SafeRead(_interruptCounter, 0);
        double gpu        = _gpuMonitor?.ReadGpuUsage() ?? -1;
        double pageFaults = SafeRead(_pageFaultsCounter, 0);

        // Network I/O — sum all interfaces, convert bytes/sec → MB/s
        double netInBytes = 0, netOutBytes = 0;
        foreach (var c in _netInCounters)  try { netInBytes  += c.NextValue(); } catch { }
        foreach (var c in _netOutCounters) try { netOutBytes += c.NextValue(); } catch { }
        double netInMbps  = netInBytes  / (1024.0 * 1024.0);
        double netOutMbps = netOutBytes / (1024.0 * 1024.0);

        // Temperatures (rate-limited internally by ThermalMonitor)
        var thermal = _thermalMonitor?.ReadTemperatures() ?? new ThermalReading();

        // Drive health (cached at 30 s cadence inside DriveHealthMonitor)
        var health = _driveHealthMonitor?.GetHealth() ?? DriveHealthSummary.Unknown;

        lock (_lock)
        {
            LatestCpu              = cpu;
            LatestRam              = ram;
            LatestDiskReadMs       = diskRead;
            LatestDiskWriteMs      = diskWrite;
            LatestDpcPercent       = dpc;
            LatestInterruptPercent = interrupt;
            LatestGpuPercent       = gpu;
            LatestCpuTempC         = thermal.CpuTempC;
            LatestGpuTempC         = thermal.GpuTempC;
            LatestNvmeTempC        = thermal.NvmeTempC;
            LatestPageFaultsSec    = pageFaults;
            LatestNetInMbps        = netInMbps;
            LatestNetOutMbps       = netOutMbps;
            LatestDriveHealth      = health;

            Enqueue(_cpuBuffer,        cpu);
            Enqueue(_ramBuffer,        ram);
            Enqueue(_diskReadBuffer,   diskRead);
            Enqueue(_diskWriteBuffer,  diskWrite);
            Enqueue(_dpcBuffer,        dpc);
            Enqueue(_interruptBuffer,  interrupt);
            Enqueue(_gpuBuffer,        gpu);
            Enqueue(_cpuTempBuffer,    thermal.CpuTempC);
            Enqueue(_gpuTempBuffer,    thermal.GpuTempC);
            Enqueue(_nvmeTempBuffer,   thermal.NvmeTempC);
            Enqueue(_pageFaultsBuffer, pageFaults);
            Enqueue(_netInBuffer,      netInMbps);
            Enqueue(_netOutBuffer,     netOutMbps);
        }

        SampleTaken?.Invoke(this, EventArgs.Empty);
    }

    private static double SafeRead(PerformanceCounter? counter, double fallback)
    {
        if (counter == null) return fallback;
        try { return counter.NextValue(); }
        catch { return fallback; }
    }

    private static void Enqueue(Queue<double> queue, double value)
    {
        queue.Enqueue(value);
        while (queue.Count > BufferSize)
            queue.Dequeue();
    }

    /// <summary>
    /// Returns a snapshot of the last N samples for the given metric.
    /// </summary>
    public double[] GetLastSamples(string metric, int count)
    {
        lock (_lock)
        {
            var buf = GetBuffer(metric);
            if (buf == null) return Array.Empty<double>();
            var arr = buf.ToArray();
            int take = Math.Min(count, arr.Length);
            return arr.Skip(arr.Length - take).ToArray();
        }
    }

    /// <summary>
    /// Returns all buffered samples for graphing (up to BufferSize).
    /// </summary>
    public double[] GetAllSamples(string metric)
    {
        lock (_lock)
        {
            return GetBuffer(metric)?.ToArray() ?? Array.Empty<double>();
        }
    }

    private Queue<double>? GetBuffer(string metric) => metric switch
    {
        MetricNames.Cpu        => _cpuBuffer,
        MetricNames.Ram        => _ramBuffer,
        MetricNames.DiskRead   => _diskReadBuffer,
        MetricNames.DiskWrite  => _diskWriteBuffer,
        MetricNames.Dpc        => _dpcBuffer,
        MetricNames.Interrupt  => _interruptBuffer,
        MetricNames.Gpu        => _gpuBuffer,
        MetricNames.CpuTempC   => _cpuTempBuffer,
        MetricNames.GpuTempC   => _gpuTempBuffer,
        MetricNames.NvmeTempC  => _nvmeTempBuffer,
        MetricNames.PageFaults => _pageFaultsBuffer,
        MetricNames.NetIn      => _netInBuffer,
        MetricNames.NetOut     => _netOutBuffer,
        _ => null
    };

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        Stop();
        _timer.Dispose();
        _cpuCounter?.Dispose();
        _ramCounter?.Dispose();
        _diskReadCounter?.Dispose();
        _diskWriteCounter?.Dispose();
        _dpcCounter?.Dispose();
        _interruptCounter?.Dispose();
        _pageFaultsCounter?.Dispose();
        foreach (var c in _netInCounters)  c.Dispose();
        foreach (var c in _netOutCounters) c.Dispose();
        _gpuMonitor?.Dispose();
        _dpcLatencyMonitor?.Dispose();
        _thermalMonitor?.Dispose();
        _driveHealthMonitor?.Dispose();
    }
}

/// <summary>
/// Metric name constants shared across the application.
/// </summary>
public static class MetricNames
{
    public const string Cpu        = "CPU %";
    public const string Ram        = "RAM %";
    public const string DiskRead   = "Disk Read (ms)";
    public const string DiskWrite  = "Disk Write (ms)";
    public const string Dpc        = "DPC %";
    public const string Interrupt  = "Interrupt %";
    public const string Gpu        = "GPU %";
    public const string CpuTempC   = "CPU Temp (°C)";
    public const string GpuTempC   = "GPU Temp (°C)";
    public const string NvmeTempC  = "NVMe Temp (°C)";
    public const string PageFaults = "Page Faults/s";
    public const string NetIn      = "Net In (MB/s)";
    public const string NetOut     = "Net Out (MB/s)";
}
