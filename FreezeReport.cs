using System.Diagnostics;

namespace Freezer;

/// <summary>
/// Exports freeze event data to a human-readable text report.
/// </summary>
public static class FreezeReport
{
    public static void ExportToText(List<FreezeEvent> events, string filePath)
    {
        using var writer = new StreamWriter(filePath, append: false, System.Text.Encoding.UTF8);

        writer.WriteLine("=============================================================");
        writer.WriteLine("        FREEZER — PC Freeze Diagnostic Report");
        writer.WriteLine($"        Generated: {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
        writer.WriteLine("=============================================================");
        writer.WriteLine();

        if (events.Count == 0)
        {
            writer.WriteLine("No freeze events were detected during this monitoring session.");
            return;
        }

        writer.WriteLine($"Total freeze events detected: {events.Count}");
        writer.WriteLine();

        for (int i = 0; i < events.Count; i++)
        {
            var e = events[i];
            writer.WriteLine($"-------------------------------------------------------------");
            writer.WriteLine($"Freeze #{i + 1}");
            writer.WriteLine($"  Timestamp   : {e.Timestamp:yyyy-MM-dd HH:mm:ss.fff}");
            writer.WriteLine($"  Duration    : {e.DurationSeconds:F2}s");
            writer.WriteLine($"  Cause       : {e.MostLikelyCause}");
            writer.WriteLine($"  Details     : {e.Details}");
            writer.WriteLine($"  TDR Detected: {(e.TdrDetected ? "YES" : "No")}");
            writer.WriteLine();

            if (e.ConnectedUsbDevices.Count > 0)
            {
                writer.WriteLine("  Connected USB Devices:");
                foreach (var dev in e.ConnectedUsbDevices)
                    writer.WriteLine($"    - {dev}");
                writer.WriteLine();
            }

            if (e.TopProcessesAtFreezeTime.Count > 0)
            {
                writer.WriteLine("  Top Processes at Freeze Time:");
                foreach (var proc in e.TopProcessesAtFreezeTime)
                    writer.WriteLine($"    - {proc}");
                writer.WriteLine();
            }

            if (e.PreFreezeMetrics.Count > 0)
            {
                writer.WriteLine("  Pre-Freeze Metric Snapshot (last ~15 samples, 200ms intervals):");
                foreach (var kvp in e.PreFreezeMetrics)
                {
                    var vals = string.Join(", ", kvp.Value.Select(v => v.ToString("F1")));
                    writer.WriteLine($"    {kvp.Key,-40}: [{vals}]");
                }
                writer.WriteLine();
            }
        }

        writer.WriteLine("=============================================================");
        writer.WriteLine("End of Report");
    }
}
