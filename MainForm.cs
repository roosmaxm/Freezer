using System.Drawing.Drawing2D;

namespace Freezer;

/// <summary>
/// Main dashboard window for the Freezer PC Freeze Diagnostic application.
/// </summary>
public partial class MainForm : Form
{
    // ── Monitoring infrastructure ──────────────────────────────────────────
    private SystemMonitor? _monitor;
    private FreezeDetector? _detector;
    private readonly List<FreezeEvent> _freezeEvents = new();
    private List<string> _usbDevices = new();

    // Tracks the last time we polled the Application log for crash events
    private DateTime _lastCrashEventCheck;
    private static readonly TimeSpan CrashEventPollInterval = TimeSpan.FromSeconds(10);

    // Tag value set on the placeholder list-view item shown before any events arrive
    private const string PlaceholderTag = "placeholder";
    // ── UI timers ──────────────────────────────────────────────────────────
    private readonly System.Windows.Forms.Timer _uiRefreshTimer;
    private readonly System.Windows.Forms.Timer _titleRestoreTimer;

    // ── Graph state ────────────────────────────────────────────────────────
    private static readonly Color DarkBackground = Color.FromArgb(18, 18, 24);
    private static readonly Color PanelBackground = Color.FromArgb(26, 26, 36);
    private static readonly Color GridColor = Color.FromArgb(45, 45, 60);

    private static readonly (string Metric, Color LineColor, string Unit, double MaxY)[] GraphDefinitions =
    {
        // Row 1: CPU load, GPU load, CPU temperature, GPU temperature
        (MetricNames.Cpu,        Color.FromArgb(100, 220, 100), "%",    100),
        (MetricNames.Gpu,        Color.FromArgb(180, 100, 255), "%",    100),
        (MetricNames.CpuTempC,   Color.FromArgb(255, 140,  60), "°C",   120),
        (MetricNames.GpuTempC,   Color.FromArgb(255,  80, 160), "°C",   120),
        // Row 2: RAM, disk read, disk write, NVMe temperature
        (MetricNames.Ram,        Color.FromArgb( 80, 160, 255), "%",    100),
        (MetricNames.DiskRead,   Color.FromArgb(255, 200,  60), "ms",   100),
        (MetricNames.DiskWrite,  Color.FromArgb(255, 160,  40), "ms",   100),
        (MetricNames.NvmeTempC,  Color.FromArgb(255, 200, 100), "°C",   100),
        // Row 3: DPC, interrupt %, page faults, network in
        (MetricNames.Dpc,        Color.FromArgb(255, 100, 100), "%",    100),
        (MetricNames.Interrupt,  Color.FromArgb(255, 140,  50), "%",    100),
        (MetricNames.PageFaults, Color.FromArgb(160, 255, 200), "/s", 10000),
        (MetricNames.NetIn,      Color.FromArgb(100, 220, 240), "MB/s",  100),
    };

    public MainForm()
    {
        InitializeComponent();

        _uiRefreshTimer = new System.Windows.Forms.Timer { Interval = 500 };
        _uiRefreshTimer.Tick += OnUiRefresh;

        _titleRestoreTimer = new System.Windows.Forms.Timer { Interval = 5000 };
        _titleRestoreTimer.Tick += (_, _) =>
        {
            Text = "Freezer — PC Freeze Diagnostic Dashboard";
            _titleRestoreTimer.Stop();
        };

        ApplyDarkTheme();
        UpdateStatusBar($"Ready. Click ▶ Start Monitoring to begin. Auto-log: {FreezeLogger.LogFilePath}");
    }

    // ── Monitoring lifecycle ───────────────────────────────────────────────

    private void StartMonitoring()
    {
        if (_monitor != null) return;

        _usbDevices = UsbMonitor.GetConnectedUsbDevices();

        _monitor = new SystemMonitor();
        _detector = new FreezeDetector(_monitor);
        _detector.FreezeDetected += OnFreezeDetected;
        _monitor.SampleTaken += OnSampleTaken;

        // Subtract the poll interval so the first UI refresh triggers an immediate poll
        _lastCrashEventCheck = DateTime.Now.Subtract(CrashEventPollInterval);

        // Report any initialization warnings
        if (_monitor.InitWarnings.Count > 0)
            UpdateStatusBar("Warnings: " + string.Join(" | ", _monitor.InitWarnings.Take(2)));

        _monitor.Start();
        _uiRefreshTimer.Start();

        btnStartStop.Text = "⏹ Stop Monitoring";

        // Check for recent system events (last 24 hours) to warn the user
        var recentEvents = EventLogMonitor.GetRecentHistoricalEvents(24);
        if (recentEvents.Count > 0)
            UpdateStatusBar($"Monitoring started. {_usbDevices.Count} USB device(s) found. ⚠ {recentEvents.Count} relevant system event(s) in the last 24h — check Event Viewer.");
        else
            UpdateStatusBar($"Monitoring started. USB devices found: {_usbDevices.Count}");
    }

    private void StopMonitoring()
    {
        if (_monitor == null) return;

        _uiRefreshTimer.Stop();
        _monitor.Stop();
        _monitor.SampleTaken -= OnSampleTaken;

        if (_detector != null)
            _detector.FreezeDetected -= OnFreezeDetected;

        _monitor.Dispose();
        _monitor = null;
        _detector = null;

        btnStartStop.Text = "▶ Start Monitoring";
        UpdateStatusBar("Monitoring stopped.");
        RefreshGraphs();
    }

    // ── Event handlers ─────────────────────────────────────────────────────

    private void OnSampleTaken(object? sender, EventArgs e)
    {
        _detector?.Evaluate(_usbDevices, _freezeEvents.Count + 1);
    }

    private void OnFreezeDetected(object? sender, FreezeEvent freezeEvent)
    {
        if (InvokeRequired)
        {
            BeginInvoke(() => OnFreezeDetected(sender, freezeEvent));
            return;
        }

        _freezeEvents.Add(freezeEvent);
        AddFreezeEventToList(freezeEvent);

        // Auto-persist to log file
        FreezeLogger.Append(freezeEvent);

        // Flash title
        Text = "🧊 FREEZE DETECTED — Freezer";
        _titleRestoreTimer.Stop();
        _titleRestoreTimer.Start();

        // Flash border red briefly
        FlashBorderRed();

        UpdateStatusBar($"⚠ Freeze detected at {freezeEvent.Timestamp:HH:mm:ss} — {freezeEvent.MostLikelyCause}");
    }

    private void OnUiRefresh(object? sender, EventArgs e)
    {
        RefreshGraphs();
        RefreshCurrentValues();
        PollApplicationCrashEvents();
    }

    private void BtnStartStop_Click(object? sender, EventArgs e)
    {
        if (_monitor == null)
            StartMonitoring();
        else
            StopMonitoring();
    }

    private void BtnExport_Click(object? sender, EventArgs e)
    {
        using var dlg = new SaveFileDialog
        {
            Title = "Export Freeze Report",
            Filter = "Text Files (*.txt)|*.txt|All Files (*.*)|*.*",
            FileName = $"FreezeReport_{DateTime.Now:yyyyMMdd_HHmmss}.txt",
            DefaultExt = "txt"
        };

        if (dlg.ShowDialog(this) == DialogResult.OK)
        {
            try
            {
                FreezeReport.ExportToText(_freezeEvents, dlg.FileName);
                UpdateStatusBar($"Report saved: {dlg.FileName}");
                MessageBox.Show($"Report saved to:\n{dlg.FileName}", "Export Complete",
                    MessageBoxButtons.OK, MessageBoxIcon.Information);
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Failed to save report:\n{ex.Message}", "Export Error",
                    MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }
    }

    private void FreezeListView_DoubleClick(object? sender, EventArgs e)
    {
        if (listViewFreezes.SelectedItems.Count == 0) return;

        var tag = listViewFreezes.SelectedItems[0].Tag;

        if (tag is FreezeEvent freezeEvent)
        {
            ShowFreezeDetail(freezeEvent);
        }
        else if (tag is EventLogNotification notification)
        {
            MessageBox.Show(notification.Message, $"{notification.Category} — {notification.Time:yyyy-MM-dd HH:mm:ss}",
                MessageBoxButtons.OK, MessageBoxIcon.Information);
        }
    }

    // ── UI Helpers ─────────────────────────────────────────────────────────

    private void AddFreezeEventToList(FreezeEvent ev)
    {
        // Remove "No freezes detected yet" placeholder
        if (listViewFreezes.Items.Count == 1 &&
            listViewFreezes.Items[0].Tag is string tag && tag == PlaceholderTag)
            listViewFreezes.Items.Clear();

        var item = new ListViewItem((_freezeEvents.Count).ToString());
        item.SubItems.Add(ev.Timestamp.ToString("yyyy-MM-dd HH:mm:ss"));
        item.SubItems.Add($"{ev.DurationSeconds:F2}s");
        item.SubItems.Add(ev.MostLikelyCause);
        item.SubItems.Add(ev.Details.Length > 80 ? ev.Details[..77] + "..." : ev.Details);
        item.ForeColor = Color.FromArgb(255, 120, 120);
        item.BackColor = Color.FromArgb(40, 20, 20);
        item.Tag = ev;
        listViewFreezes.Items.Add(item);

        // Auto-scroll to the latest
        item.EnsureVisible();
    }

    private void AddEventLogNotificationToList(EventLogNotification notification)
    {
        // Remove placeholder if still present
        if (listViewFreezes.Items.Count == 1 &&
            listViewFreezes.Items[0].Tag is string t && t == PlaceholderTag)
            listViewFreezes.Items.Clear();

        bool isSystemWarning = notification.Category == "System Warning";

        var item = new ListViewItem("—");
        item.SubItems.Add(notification.Time.ToString("yyyy-MM-dd HH:mm:ss"));
        item.SubItems.Add("—");
        item.SubItems.Add(notification.Category);
        string msg = notification.Message;
        item.SubItems.Add(msg.Length > 80 ? msg[..77] + "..." : msg);

        if (isSystemWarning)
        {
            item.ForeColor = Color.FromArgb(100, 200, 255);  // cyan for system/driver warnings
            item.BackColor = Color.FromArgb(10, 25, 40);
        }
        else
        {
            item.ForeColor = Color.FromArgb(255, 200, 80);   // amber for application crashes
            item.BackColor = Color.FromArgb(35, 28, 10);
        }

        item.Tag = notification;
        listViewFreezes.Items.Add(item);
        item.EnsureVisible();
    }

    private void PollApplicationCrashEvents()
    {
        if (_monitor == null) return;
        if (DateTime.Now - _lastCrashEventCheck < CrashEventPollInterval) return;

        var since = _lastCrashEventCheck;
        _lastCrashEventCheck = DateTime.Now;

        foreach (var notification in EventLogMonitor.GetPolledEvents(since))
            AddEventLogNotificationToList(notification);
    }

    private void ShowFreezeDetail(FreezeEvent ev)
    {
        using var dlg = new FreezeDetailForm(ev);
        dlg.ShowDialog(this);
    }

    private void RefreshGraphs()
    {
        foreach (var panel in _graphPanels)
            panel.Invalidate();
    }

    private void RefreshCurrentValues()
    {
        if (_monitor == null)
        {
            lblCurrentValues.Text = "— not monitoring —";
            return;
        }

        string GpuStr(double v) => v < 0 ? "N/A" : $"{v:F0}%";
        string TempStr(double v) => v < 0 ? "N/A" : $"{v:F0}°C";

        string driveStatus = _monitor.LatestDriveHealth.AnyErrors
            ? "⚠ Drive Warning"
            : (_monitor.LatestDriveHealth.Drives.Count > 0 ? "Drive: ✓" : "Drive: N/A");

        lblCurrentValues.Text =
            $"CPU: {_monitor.LatestCpu:F0}% ({TempStr(_monitor.LatestCpuTempC)})  " +
            $"GPU: {GpuStr(_monitor.LatestGpuPercent)} ({TempStr(_monitor.LatestGpuTempC)})  " +
            $"RAM: {_monitor.LatestRam:F0}%  " +
            $"NVMe: {TempStr(_monitor.LatestNvmeTempC)}  " +
            $"Disk R: {_monitor.LatestDiskReadMs:F1}ms  W: {_monitor.LatestDiskWriteMs:F1}ms  " +
            $"DPC: {_monitor.LatestDpcPercent:F1}%  IRQ: {_monitor.LatestInterruptPercent:F1}%  " +
            $"PF: {_monitor.LatestPageFaultsSec:F0}/s  " +
            $"Net↓: {_monitor.LatestNetInMbps:F1}MB/s  " +
            $"{driveStatus}";
    }

    private void UpdateStatusBar(string message)
    {
        if (InvokeRequired)
        {
            BeginInvoke(() => UpdateStatusBar(message));
            return;
        }
        lblStatus.Text = message;
    }

    private void FlashBorderRed()
    {
        BackColor = Color.FromArgb(80, 20, 20);
        var restoreTimer = new System.Windows.Forms.Timer { Interval = 500 };
        restoreTimer.Tick += (_, _) =>
        {
            BackColor = DarkBackground;
            restoreTimer.Dispose();
        };
        restoreTimer.Start();
    }

    private void ApplyDarkTheme()
    {
        BackColor = DarkBackground;
        ForeColor = Color.FromArgb(220, 220, 230);
    }

    // ── Graph drawing ──────────────────────────────────────────────────────

    private readonly List<Panel> _graphPanels = new();

    private void DrawGraph(Graphics g, Panel panel, string metric, Color lineColor, string unit, double maxY)
    {
        int w = panel.Width;
        int h = panel.Height;

        // Background
        g.Clear(PanelBackground);

        // Grid lines
        using var gridPen = new Pen(GridColor, 1);
        for (int y = 0; y <= 4; y++)
        {
            int yPos = (int)(h * y / 4.0);
            g.DrawLine(gridPen, 0, yPos, w, yPos);
        }
        for (int x = 0; x <= 5; x++)
        {
            int xPos = (int)(w * x / 5.0);
            g.DrawLine(gridPen, xPos, 0, xPos, h);
        }

        // Y-axis labels
        using var labelFont = new Font("Consolas", 7f);
        using var labelBrush = new SolidBrush(Color.FromArgb(140, 140, 160));
        for (int y = 0; y <= 4; y++)
        {
            double val = maxY * (1.0 - y / 4.0);
            int yPos = (int)(h * y / 4.0);
            g.DrawString($"{val:F0}{unit}", labelFont, labelBrush, 2, yPos);
        }

        // Metric label
        using var titleFont = new Font("Segoe UI", 8f, FontStyle.Bold);
        using var titleBrush = new SolidBrush(lineColor);
        g.DrawString(metric, titleFont, titleBrush, w / 2 - 30, 2);

        // Data line
        if (_monitor == null) return;

        double[] samples = _monitor.GetAllSamples(metric);
        if (samples.Length < 2) return;

        // Show last `w` data points to fill the panel
        int displayCount = Math.Min(samples.Length, w);
        var displaySamples = samples.Skip(samples.Length - displayCount).ToArray();

        using var linePen = new Pen(lineColor, 1.5f);
        var points = new List<PointF>();

        for (int i = 0; i < displaySamples.Length; i++)
        {
            double val = displaySamples[i] < 0 ? 0 : displaySamples[i];
            float xPos = (float)(w * i / (double)(displayCount - 1));
            float yPos2 = (float)(h - h * Math.Min(val, maxY) / maxY);
            points.Add(new PointF(xPos, yPos2));
        }

        if (points.Count >= 2)
        {
            try { g.DrawLines(linePen, points.ToArray()); }
            catch { /* skip if points are degenerate */ }
        }

        // Current value overlay
        double current = samples[^1];
        string currentStr = current < 0 ? "N/A" : $"{current:F1}{unit}";
        using var valueBrush = new SolidBrush(Color.White);
        using var valueFont = new Font("Consolas", 9f, FontStyle.Bold);
        g.DrawString(currentStr, valueFont, valueBrush, w - 55, h - 18);
    }

    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        StopMonitoring();
        base.OnFormClosing(e);
    }
}
