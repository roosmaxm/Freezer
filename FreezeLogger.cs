namespace Freezer;

/// <summary>
/// Automatically appends each detected freeze event to a persistent log file in
/// %APPDATA%\Freezer\FreezeLog.txt so that events survive application restarts.
/// </summary>
public static class FreezeLogger
{
    private static readonly string LogDirectory =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Freezer");

    public static string LogFilePath { get; } = Path.Combine(LogDirectory, "FreezeLog.txt");

    /// <summary>
    /// Appends a single freeze event to the persistent log file.
    /// Creates the log directory and file header if they do not yet exist.
    /// Safe to call from any thread; the file write is synchronised.
    /// </summary>
    public static void Append(FreezeEvent ev)
    {
        try
        {
            Directory.CreateDirectory(LogDirectory);

            bool writeHeader = !File.Exists(LogFilePath);

            using var writer = new StreamWriter(LogFilePath, append: true, System.Text.Encoding.UTF8);

            if (writeHeader)
            {
                writer.WriteLine("=============================================================");
                writer.WriteLine("        FREEZER — Persistent Freeze Log");
                writer.WriteLine("        Each entry is appended at detection time.");
                writer.WriteLine("=============================================================");
                writer.WriteLine();
            }

            writer.WriteLine($"-------------------------------------------------------------");
            writer.WriteLine($"Freeze #{ev.Index}   Logged: {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
            writer.WriteLine($"  Timestamp   : {ev.Timestamp:yyyy-MM-dd HH:mm:ss.fff}");
            writer.WriteLine($"  Duration    : {ev.DurationSeconds:F2}s");
            writer.WriteLine($"  Cause       : {ev.MostLikelyCause}");
            writer.WriteLine($"  Details     : {ev.Details}");
            writer.WriteLine($"  TDR Detected: {(ev.TdrDetected ? "YES" : "No")}");

            if (ev.SystemEventLogEntries.Count > 0)
            {
                writer.WriteLine($"  Event Log Entries ({ev.SystemEventLogEntries.Count}):");
                foreach (var entry in ev.SystemEventLogEntries)
                    writer.WriteLine($"    {entry}");
            }

            if (ev.ConnectedUsbDevices.Count > 0)
            {
                writer.WriteLine($"  USB Devices ({ev.ConnectedUsbDevices.Count}):");
                foreach (var dev in ev.ConnectedUsbDevices.Take(5))
                    writer.WriteLine($"    - {dev}");
            }

            if (ev.TopProcessesAtFreezeTime.Count > 0)
            {
                writer.WriteLine($"  Top Processes:");
                foreach (var proc in ev.TopProcessesAtFreezeTime.Take(5))
                    writer.WriteLine($"    - {proc}");
            }

            writer.WriteLine();
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[FreezeLogger] Failed to write log entry: {ex.Message}");
            /* Logging failures must not surface to the user */
        }
    }
}
