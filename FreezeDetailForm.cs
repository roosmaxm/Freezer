namespace Freezer;

/// <summary>
/// Detail popup showing the full snapshot of a detected freeze event.
/// </summary>
public class FreezeDetailForm : Form
{
    private static readonly Color DarkBg = Color.FromArgb(18, 18, 24);
    private static readonly Color PanelBg = Color.FromArgb(26, 26, 36);

    public FreezeDetailForm(FreezeEvent ev)
    {
        Text = $"Freeze Detail — #{ev.Index} at {ev.Timestamp:yyyy-MM-dd HH:mm:ss}";
        Size = new Size(760, 600);
        MinimumSize = new Size(600, 400);
        StartPosition = FormStartPosition.CenterParent;
        BackColor = DarkBg;
        ForeColor = Color.FromArgb(220, 220, 230);
        Font = new Font("Segoe UI", 9f);

        var scroll = new RichTextBox
        {
            Dock = DockStyle.Fill,
            BackColor = PanelBg,
            ForeColor = Color.FromArgb(200, 210, 230),
            Font = new Font("Consolas", 9f),
            ReadOnly = true,
            BorderStyle = BorderStyle.None,
            ScrollBars = RichTextBoxScrollBars.Vertical,
            WordWrap = false,
        };

        BuildDetailText(scroll, ev);

        var closeBtn = new Button
        {
            Text = "Close",
            Dock = DockStyle.Bottom,
            Height = 36,
            BackColor = Color.FromArgb(35, 35, 50),
            ForeColor = Color.FromArgb(180, 190, 210),
            FlatStyle = FlatStyle.Flat,
            Font = new Font("Segoe UI", 9f),
        };
        closeBtn.FlatAppearance.BorderColor = Color.FromArgb(60, 60, 90);
        closeBtn.Click += (_, _) => Close();

        Controls.Add(scroll);
        Controls.Add(closeBtn);
    }

    private static void BuildDetailText(RichTextBox tb, FreezeEvent ev)
    {
        void Header(string text)
        {
            tb.SelectionFont = new Font("Consolas", 10f, FontStyle.Bold);
            tb.SelectionColor = Color.FromArgb(100, 200, 255);
            tb.AppendText(text + "\n");
            tb.SelectionFont = new Font("Consolas", 9f);
            tb.SelectionColor = Color.FromArgb(200, 210, 230);
        }

        void Line(string label, string value, Color? color = null)
        {
            tb.SelectionFont = new Font("Consolas", 9f, FontStyle.Bold);
            tb.SelectionColor = Color.FromArgb(160, 175, 200);
            tb.AppendText($"  {label,-22}");
            tb.SelectionFont = new Font("Consolas", 9f);
            tb.SelectionColor = color ?? Color.FromArgb(220, 230, 220);
            tb.AppendText(value + "\n");
        }

        void Sep() { tb.AppendText("\n"); }

        Header("═══════════════════════════════════════════════════════════");
        Header($"  FREEZE EVENT #{ev.Index}");
        Header("═══════════════════════════════════════════════════════════");
        Sep();

        Line("Timestamp:", ev.Timestamp.ToString("yyyy-MM-dd HH:mm:ss.fff"));
        Line("Duration:", $"{ev.DurationSeconds:F2} seconds");
        Line("Most Likely Cause:", ev.MostLikelyCause, Color.FromArgb(255, 150, 100));
        Line("TDR Detected:", ev.TdrDetected ? "YES — GPU driver timeout!" : "No", ev.TdrDetected ? Color.FromArgb(255, 80, 80) : null);
        Sep();

        Header("─── Details ────────────────────────────────────────────────");
        tb.SelectionFont = new Font("Consolas", 9f);
        tb.SelectionColor = Color.FromArgb(200, 220, 200);
        // Word-wrap the details manually
        const int wrap = 90;
        var words = ev.Details.Split(' ');
        var line = new System.Text.StringBuilder();
        foreach (var w in words)
        {
            if (line.Length + w.Length + 1 > wrap)
            {
                tb.AppendText("  " + line.ToString() + "\n");
                line.Clear();
            }
            if (line.Length > 0) line.Append(' ');
            line.Append(w);
        }
        if (line.Length > 0) tb.AppendText("  " + line + "\n");
        Sep();

        if (ev.ConnectedUsbDevices.Count > 0)
        {
            Header("─── Connected USB Devices ──────────────────────────────────");
            foreach (var dev in ev.ConnectedUsbDevices)
            {
                tb.SelectionColor = Color.FromArgb(180, 200, 240);
                tb.AppendText($"  • {dev}\n");
            }
            Sep();
        }

        if (ev.TopProcessesAtFreezeTime.Count > 0)
        {
            Header("─── Top Processes at Freeze Time ───────────────────────────");
            foreach (var proc in ev.TopProcessesAtFreezeTime)
            {
                tb.SelectionColor = Color.FromArgb(200, 200, 180);
                tb.AppendText($"  • {proc}\n");
            }
            Sep();
        }

        if (ev.SystemEventLogEntries.Count > 0)
        {
            Header("─── Windows Event Log (System + Application) ───────────────");
            foreach (var entry in ev.SystemEventLogEntries)
            {
                tb.SelectionFont = new Font("Consolas", 8.5f);
                bool isError = entry.Contains("[System/ERR]") || entry.Contains("[Application/ERR]");
                tb.SelectionColor = isError
                    ? Color.FromArgb(255, 130, 130)
                    : Color.FromArgb(200, 210, 200);
                tb.AppendText($"  {entry}\n");
            }
            Sep();
        }

        if (ev.PreFreezeMetrics.Count > 0)
        {
            Header("─── Pre-Freeze Metric Snapshot (last 3s @ 200ms intervals) ─");
            foreach (var kvp in ev.PreFreezeMetrics)
            {
                var vals = kvp.Value.Select(v => v < 0 ? "N/A" : $"{v:F1}").ToArray();
                string valLine = string.Join(", ", vals);
                tb.SelectionFont = new Font("Consolas", 8.5f, FontStyle.Bold);
                tb.SelectionColor = Color.FromArgb(160, 190, 220);
                tb.AppendText($"  {kvp.Key,-22}");
                tb.SelectionFont = new Font("Consolas", 8.5f);
                tb.SelectionColor = Color.FromArgb(200, 210, 200);
                tb.AppendText($"[{valLine}]\n");
            }
            Sep();
        }

        Header("═══════════════════════════════════════════════════════════");
    }
}
