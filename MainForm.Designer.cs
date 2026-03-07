namespace Freezer;

#nullable enable

partial class MainForm
{
    private System.ComponentModel.IContainer? components = null;

    // ── Controls ────────────────────────────────────────────────────────────
    private TableLayoutPanel mainLayout = null!;
    private Panel graphPanel = null!;
    private Panel freezeLogPanel = null!;
    private Panel toolbarPanel = null!;
    private ListView listViewFreezes = null!;
    private Button btnStartStop = null!;
    private Button btnExport = null!;
    private Label lblStatus = null!;
    private Label lblCurrentValues = null!;
    private Label lblFreezeLogTitle = null!;
    private Label lblGraphsTitle = null!;

    protected override void Dispose(bool disposing)
    {
        if (disposing && (components != null))
            components.Dispose();
        base.Dispose(disposing);
    }

    private void InitializeComponent()
    {
        this.SuspendLayout();

        // ── Form ────────────────────────────────────────────────────────────
        Text = "Freezer — PC Freeze Diagnostic Dashboard";
        Size = new Size(1280, 800);
        MinimumSize = new Size(900, 600);
        StartPosition = FormStartPosition.CenterScreen;
        BackColor = Color.FromArgb(18, 18, 24);
        ForeColor = Color.FromArgb(220, 220, 230);
        Font = new Font("Segoe UI", 9f);

        // ── Main layout: 3 rows ─────────────────────────────────────────────
        mainLayout = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            RowCount = 4,
            ColumnCount = 1,
            BackColor = Color.FromArgb(18, 18, 24),
            Padding = new Padding(8),
        };
        mainLayout.RowStyles.Add(new RowStyle(SizeType.Absolute, 22));  // graphs title
        mainLayout.RowStyles.Add(new RowStyle(SizeType.Percent, 55));   // graphs area
        mainLayout.RowStyles.Add(new RowStyle(SizeType.Percent, 45));   // freeze log
        mainLayout.RowStyles.Add(new RowStyle(SizeType.Absolute, 54));  // toolbar

        // ── Graphs title ────────────────────────────────────────────────────
        lblGraphsTitle = new Label
        {
            Text = "📊  Live Performance Metrics (500ms refresh)",
            Dock = DockStyle.Fill,
            ForeColor = Color.FromArgb(180, 200, 255),
            Font = new Font("Segoe UI", 9f, FontStyle.Bold),
            TextAlign = ContentAlignment.MiddleLeft,
            BackColor = Color.Transparent,
        };
        mainLayout.Controls.Add(lblGraphsTitle, 0, 0);

        // ── Graph panel ─────────────────────────────────────────────────────
        graphPanel = new Panel
        {
            Dock = DockStyle.Fill,
            BackColor = Color.FromArgb(18, 18, 24),
            Padding = new Padding(0),
        };
        BuildGraphs();
        mainLayout.Controls.Add(graphPanel, 0, 1);

        // ── Freeze event log ─────────────────────────────────────────────────
        freezeLogPanel = new Panel
        {
            Dock = DockStyle.Fill,
            BackColor = Color.FromArgb(18, 18, 24),
            Padding = new Padding(0, 4, 0, 0),
        };
        BuildFreezeLog();
        mainLayout.Controls.Add(freezeLogPanel, 0, 2);

        // ── Toolbar ─────────────────────────────────────────────────────────
        toolbarPanel = new Panel
        {
            Dock = DockStyle.Fill,
            BackColor = Color.FromArgb(22, 22, 32),
            Padding = new Padding(4),
        };
        BuildToolbar();
        mainLayout.Controls.Add(toolbarPanel, 0, 3);

        Controls.Add(mainLayout);
        ResumeLayout(false);
    }

    // ── Graph sub-panel builder ────────────────────────────────────────────

    private void BuildGraphs()
    {
        var gridLayout = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            RowCount = 2,
            ColumnCount = 3,
            BackColor = Color.FromArgb(18, 18, 24),
            CellBorderStyle = TableLayoutPanelCellBorderStyle.Single,
        };
        for (int c = 0; c < 3; c++)
            gridLayout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 33.33f));
        gridLayout.RowStyles.Add(new RowStyle(SizeType.Percent, 50));
        gridLayout.RowStyles.Add(new RowStyle(SizeType.Percent, 50));

        for (int i = 0; i < GraphDefinitions.Length; i++)
        {
            var def = GraphDefinitions[i];
            var p = CreateGraphPanel(def.Metric, def.LineColor, def.Unit, def.MaxY);
            _graphPanels.Add(p);
            gridLayout.Controls.Add(p, i % 3, i / 3);
        }

        graphPanel.Controls.Add(gridLayout);
    }

    private Panel CreateGraphPanel(string metric, Color lineColor, string unit, double maxY)
    {
        var p = new Panel
        {
            Dock = DockStyle.Fill,
            BackColor = Color.FromArgb(26, 26, 36),
            Margin = new Padding(1),
        };
        p.Paint += (sender, e) => DrawGraph(e.Graphics, p, metric, lineColor, unit, maxY);
        return p;
    }

    // ── Freeze log builder ─────────────────────────────────────────────────

    private void BuildFreezeLog()
    {
        lblFreezeLogTitle = new Label
        {
            Text = "❄  Freeze, Crash & System Warning Log — double-click any row for full details",
            Dock = DockStyle.Top,
            Height = 22,
            ForeColor = Color.FromArgb(180, 220, 255),
            Font = new Font("Segoe UI", 9f, FontStyle.Bold),
            TextAlign = ContentAlignment.MiddleLeft,
            BackColor = Color.Transparent,
        };

        listViewFreezes = new ListView
        {
            Dock = DockStyle.Fill,
            View = View.Details,
            FullRowSelect = true,
            GridLines = false,
            BackColor = Color.FromArgb(22, 22, 32),
            ForeColor = Color.FromArgb(200, 210, 230),
            BorderStyle = BorderStyle.None,
            Font = new Font("Consolas", 8.5f),
        };
        listViewFreezes.Columns.Add("#", 36);
        listViewFreezes.Columns.Add("Timestamp", 148);
        listViewFreezes.Columns.Add("Duration", 70);
        listViewFreezes.Columns.Add("Most Likely Cause", 260);
        listViewFreezes.Columns.Add("Details", 600);
        listViewFreezes.DoubleClick += FreezeListView_DoubleClick;

        // Placeholder item
        var placeholder = new ListViewItem("—");
        placeholder.SubItems.Add("—");
        placeholder.SubItems.Add("—");
        placeholder.SubItems.Add("No freezes detected yet");
        placeholder.SubItems.Add("Start monitoring and wait for a freeze event.");
        placeholder.ForeColor = Color.FromArgb(100, 100, 120);
        placeholder.Tag = PlaceholderTag;
        listViewFreezes.Items.Add(placeholder);

        freezeLogPanel.Controls.Add(listViewFreezes);
        freezeLogPanel.Controls.Add(lblFreezeLogTitle);

        // Current values bar between graphs and freeze log
        lblCurrentValues = new Label
        {
            Text = "— not monitoring —",
            Dock = DockStyle.Top,
            Height = 20,
            ForeColor = Color.FromArgb(160, 200, 160),
            Font = new Font("Consolas", 8f),
            TextAlign = ContentAlignment.MiddleLeft,
            BackColor = Color.FromArgb(20, 24, 20),
            Padding = new Padding(4, 0, 0, 0),
        };
        freezeLogPanel.Controls.Add(lblCurrentValues);
    }

    // ── Toolbar builder ────────────────────────────────────────────────────

    private void BuildToolbar()
    {
        btnStartStop = new Button
        {
            Text = "▶ Start Monitoring",
            Size = new Size(160, 38),
            Location = new Point(4, 6),
            BackColor = Color.FromArgb(30, 80, 30),
            ForeColor = Color.FromArgb(150, 255, 150),
            FlatStyle = FlatStyle.Flat,
            Font = new Font("Segoe UI", 9f, FontStyle.Bold),
            Cursor = Cursors.Hand,
        };
        btnStartStop.FlatAppearance.BorderColor = Color.FromArgb(60, 120, 60);
        btnStartStop.Click += BtnStartStop_Click;

        btnExport = new Button
        {
            Text = "💾 Export Report",
            Size = new Size(140, 38),
            Location = new Point(172, 6),
            BackColor = Color.FromArgb(30, 50, 80),
            ForeColor = Color.FromArgb(150, 190, 255),
            FlatStyle = FlatStyle.Flat,
            Font = new Font("Segoe UI", 9f, FontStyle.Bold),
            Cursor = Cursors.Hand,
        };
        btnExport.FlatAppearance.BorderColor = Color.FromArgb(60, 100, 160);
        btnExport.Click += BtnExport_Click;

        lblStatus = new Label
        {
            Text = "Ready",
            Location = new Point(320, 13),
            Size = new Size(900, 22),
            ForeColor = Color.FromArgb(160, 180, 200),
            Font = new Font("Segoe UI", 8.5f),
            TextAlign = ContentAlignment.MiddleLeft,
            BackColor = Color.Transparent,
            AutoEllipsis = true,
        };

        toolbarPanel.Controls.AddRange(new Control[] { btnStartStop, btnExport, lblStatus });
    }
}
