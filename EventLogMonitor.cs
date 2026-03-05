using System.Diagnostics;

namespace Freezer;

/// <summary>
/// Scans the Windows System and Application Event Logs for interrupt-related,
/// crash, and hardware-failure events that may explain a system freeze.
/// </summary>
public static class EventLogMonitor
{
    /// <summary>
    /// Captures Windows Event Log entries from both the System and Application logs
    /// that were written within <paramref name="windowSeconds"/> seconds before now.
    /// Returns a list of human-readable strings describing each relevant event.
    /// </summary>
    public static List<string> GetRecentEvents(int windowSeconds = 15)
    {
        var results = new List<string>();
        var cutoff = DateTime.Now.AddSeconds(-windowSeconds);

        ScanLog("System", cutoff, results);
        ScanApplicationLog(cutoff, results);

        return results;
    }

    /// <summary>
    /// Returns a list of historically recent relevant events (last 24 hours)
    /// from the Windows event logs. Used to populate context at startup.
    /// </summary>
    public static List<string> GetRecentHistoricalEvents(int hours = 24)
    {
        var results = new List<string>();
        var cutoff = DateTime.Now.AddHours(-hours);

        ScanLog("System", cutoff, results);
        ScanApplicationLog(cutoff, results);

        return results;
    }

    private static void ScanLog(string logName, DateTime cutoff, List<string> results)
    {
        try
        {
            using var log = new EventLog(logName);
            int count = log.Entries.Count;

            for (int i = count - 1; i >= 0; i--)
            {
                EventLogEntry entry;
                try { entry = log.Entries[i]; }
                catch { continue; }

                if (entry.TimeGenerated < cutoff)
                    break;

                if (IsRelevantSystemEvent(entry))
                {
                    results.Add(FormatEntry(logName, entry));
                }
            }
        }
        catch { /* Event log access may be restricted */ }
    }

    private static void ScanApplicationLog(DateTime cutoff, List<string> results)
    {
        try
        {
            using var log = new EventLog("Application");
            int count = log.Entries.Count;

            for (int i = count - 1; i >= 0; i--)
            {
                EventLogEntry entry;
                try { entry = log.Entries[i]; }
                catch { continue; }

                if (entry.TimeGenerated < cutoff)
                    break;

                if (IsRelevantApplicationEvent(entry))
                {
                    results.Add(FormatEntry("Application", entry));
                }
            }
        }
        catch { /* Event log access may be restricted */ }
    }

    /// <summary>
    /// Returns true for System log events associated with interrupts, hardware errors,
    /// kernel power issues, driver failures, disk problems, BSODs, DCOM timeouts,
    /// COM permission failures, and ETL/performance logging failures.
    /// </summary>
    private static bool IsRelevantSystemEvent(EventLogEntry entry)
    {
        long id = entry.InstanceId & 0xFFFF; // strip facility/severity bits

        return entry.Source switch
        {
            // Kernel-Power: EventID 41 = unexpected shutdown/reboot; 6008 = dirty shutdown
            "Microsoft-Windows-Kernel-Power" or "Kernel-Power" =>
                id == 41 || id == 6008,

            // BugCheck / BSOD
            "BugCheck" or "Microsoft-Windows-WER-SystemErrorReporting" =>
                id == 1001 || id == 1002,

            // Disk and storage errors
            "disk" or "Disk" =>
                id == 7 || id == 11 || id == 15 || id == 51 || id == 52,

            // StorPort timeouts and resets
            "storahci" or "StorPort" or "stornvme" or "nvme" =>
                id == 129 || id == 130 || id == 133,

            // WHEA hardware errors (correctable / uncorrectable)
            "Microsoft-Windows-WHEA-Logger" =>
                id == 17 || id == 18 || id == 19 || id == 20 || id == 47,

            // NVIDIA GPU TDR and driver events
            "nvlddmkm" =>
                id == 13 || id == 14,

            // Generic TDR via display infrastructure
            "Microsoft-Windows-DisplayPort-UcmCx30" or "atikmpag" or
            "dxgkrnl" or "Microsoft-Windows-Kernel-Video" =>
                id == 4101 || id == 13 || id == 14,

            // USB host controller and hub errors
            "usbhub" or "usbhub3" or "usbport" or "USBHUB" or "USBHUB3" =>
                id == 25 || id == 26 || id == 34 || id == 43 || id == 44 || id == 45,

            // Service Control Manager: unexpected service termination
            "Service Control Manager" =>
                id == 7034 || id == 7031 || id == 7043,

            // DCOM: EventID 10010 = server did not register within timeout;
            //        EventID 10016 = COM permission denied (local/remote activation)
            "DistributedCOM" or "DCOM" =>
                id == 10010 || id == 10016,

            // ETL/Performance tracing session failures:
            //   EventID 2003 = session max file size reached (data loss possible)
            //   EventID 2004 = session stopped due to error (e.g. STATUS_LOG_FILE_FULL)
            //   EventID 2 = session failed to start (e.g. STATUS_OBJECT_NAME_COLLISION)
            "Microsoft-Windows-Kernel-EventTracing" =>
                id == 2 || id == 2003 || id == 2004,

            _ => false
        };
    }

    /// <summary>
    /// Returns true for Application log events indicating process crashes or hangs.
    /// </summary>
    private static bool IsRelevantApplicationEvent(EventLogEntry entry)
    {
        long id = entry.InstanceId & 0xFFFF;

        return entry.Source switch
        {
            // Application crash (EventID 1000) or hang (EventID 1002)
            "Application Error" => id == 1000 || id == 1001,
            "Application Hang" => id == 1002,

            // Windows Error Reporting
            "Windows Error Reporting" => id == 1001,

            _ => false
        };
    }

    private static string FormatEntry(string logName, EventLogEntry entry)
    {
        long id = entry.InstanceId & 0xFFFF;
        string time = entry.TimeGenerated.ToString("HH:mm:ss");
        string type = entry.EntryType switch
        {
            EventLogEntryType.Error => "ERR",
            EventLogEntryType.Warning => "WRN",
            EventLogEntryType.Information => "INF",
            _ => "   "
        };

        // Trim message to a single line
        string msg = (entry.Message ?? string.Empty)
            .Replace("\r\n", " ")
            .Replace("\n", " ")
            .Replace("\r", " ");
        if (msg.Length > 200) msg = msg[..197] + "...";

        return $"[{logName}/{type}] {time} {entry.Source} EventID={id} — {msg}";
    }
}
