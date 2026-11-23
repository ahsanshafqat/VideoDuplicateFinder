using System.Diagnostics;
using System.Globalization;
using System.Windows.Forms;

namespace VideoDuplicateFinder;

public sealed class MainForm : Form
{
    const int ThumbW = 176, ThumbH = 99;

    readonly AppSettings _settings = AppSettings.Load();

    // paths
    readonly TextBox _txtSource = new(), _txtTarget = new(), _txtDelete = new(), _txtFfmpeg = new();
    // options
    readonly NumericUpDown _numMatch = Num(1, 100), _numPossible = Num(1, 100), _numTol = Num(0, 32), _numDur = Num(0, 50), _numThreads = Num(1, 64);
    readonly CheckBox _chkSub = new() { Text = "Include subfolders", AutoSize = true };
    readonly CheckBox _chkPossible = new() { Text = "Show possible matches from", AutoSize = true };
    readonly CheckBox _chkNamed = new() { Text = "Name folders after video & keep best quality", AutoSize = true };
    // actions
    readonly Button _btnScan = new() { Text = "Scan (dry run)", AutoSize = true };
    readonly Button _btnCancel = new() { Text = "Cancel", AutoSize = true, Enabled = false };
    readonly Button _btnShowSrc = new() { Text = "Show source in folder", AutoSize = true };
    readonly Button _btnShowTgt = new() { Text = "Show target in folder", AutoSize = true };
    readonly Button _btnShowBoth = new() { Text = "Show both side-by-side", AutoSize = true };
    readonly Button _btnPlaySrc = new() { Text = "Play source", AutoSize = true };
    readonly Button _btnPlayTgt = new() { Text = "Play target", AutoSize = true };
    readonly Button _btnDelSrc = new() { Text = "Delete source...", AutoSize = true };
    readonly Button _btnDelTgt = new() { Text = "Delete target...", AutoSize = true };
    readonly Button _btnCheckAll = new() { Text = "Check all", AutoSize = true };
    readonly Button _btnUncheckAll = new() { Text = "Uncheck all", AutoSize = true };
    readonly Button _btnMove = new() { Text = "Move checked pairs to Deleted folder...", AutoSize = true };
    // progress
    readonly ProgressBar _bar = new() { Dock = DockStyle.Fill, Height = 22, Minimum = 0, Maximum = 1, Style = ProgressBarStyle.Continuous };
    readonly Label _lblPhase = new() { AutoSize = true, Text = "Ready. Choose the folders and click \"Scan (dry run)\".", Font = new Font("Segoe UI", 9.5f, FontStyle.Bold) };
    readonly Label _lblStats = new() { AutoSize = true, Text = "" };
    readonly Label _lblCurrent = new() { AutoSize = true, Text = "", ForeColor = Color.DimGray, MaximumSize = new Size(1500, 0) };
    // results
    readonly DataGridView _grid = new();
    readonly Label _lblSummary = new() { AutoSize = true, Text = "" };
    readonly TabControl _tabs = new() { Dock = DockStyle.Fill };
    readonly TabPage _tabSamples = new("Sample frames"), _tabLog = new("Log");
    readonly TextBox _txtLog = new() { Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Vertical, Dock = DockStyle.Fill, Font = new Font("Consolas", 9f), MaxLength = int.MaxValue };
    readonly Label _lblSrcInfo = new() { AutoSize = true, MaximumSize = new Size(1500, 0), Font = new Font("Segoe UI", 9f, FontStyle.Bold) };
    readonly Label _lblTgtInfo = new() { AutoSize = true, MaximumSize = new Size(1500, 0), Font = new Font("Segoe UI", 9f, FontStyle.Bold), Margin = new Padding(3, 12, 3, 3) };
    readonly FlowLayoutPanel _flpSrc = new() { AutoSize = true, WrapContents = false };
    readonly FlowLayoutPanel _flpTgt = new() { AutoSize = true, WrapContents = false };
    readonly SplitContainer _split = new() { Dock = DockStyle.Fill, Orientation = Orientation.Horizontal };

    readonly Font _bold;
    List<DuplicatePair> _all = new();
    CancellationTokenSource? _opCts, _thumbCts;
    bool _busy, _populating;

    static NumericUpDown Num(int min, int max) => new() { Minimum = min, Maximum = max, Width = 60 };

    public MainForm()
    {
        Text = "Video Duplicate Finder";
        Width = 1450; Height = 950;
        StartPosition = FormStartPosition.CenterScreen;
        AutoScaleMode = AutoScaleMode.Dpi;
        _bold = new Font(Font, FontStyle.Bold);

        BuildUi();
        LoadSettingsToUi();
        WireEvents();
        UpdateButtons();

        Load += (_, _) =>
        {
            try { _split.SplitterDistance = (int)(_split.Height * 0.52); } catch { }

            // Load results from last run
            try
            {
                var lastResults = DuplicatePair.LoadResults();
                if (lastResults.Count > 0)
                {
                    _all = lastResults;
                    Populate();
                    _lblPhase.Text = $"Loaded {_all.Count} duplicate pairs from last scan.";
                }
            }
            catch { }
        };
        FormClosing += (_, _) =>
        {
            _opCts?.Cancel();
            _thumbCts?.Cancel();
            SaveSettings();
        };
    }

    // =====================================================================
    //  UI construction
    // =====================================================================
    void BuildUi()
    {
        var root = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 5, Padding = new Padding(8) };
        for (int i = 0; i < 4; i++) root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        Controls.Add(root);

        // ---- paths ----
        var paths = new TableLayoutPanel { Dock = DockStyle.Fill, AutoSize = true, ColumnCount = 3, RowCount = 4 };
        paths.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        paths.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        paths.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        void PathRow(int r, string label, TextBox tb, string title)
        {
            paths.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            paths.Controls.Add(new Label { Text = label, AutoSize = true, Anchor = AnchorStyles.Left, Margin = new Padding(3, 6, 10, 3) }, 0, r);
            tb.Dock = DockStyle.Fill;
            paths.Controls.Add(tb, 1, r);
            var b = new Button { Text = "Browse...", AutoSize = true };
            b.Click += (_, _) => Browse(tb, title);
            paths.Controls.Add(b, 2, r);
        }
        PathRow(0, "Source folder", _txtSource, "Select the SOURCE folder");
        PathRow(1, "Target folder", _txtTarget, "Select the TARGET folder");
        PathRow(2, "Deleted folder (matched pairs are moved here)", _txtDelete, "Select the folder that receives duplicate pairs");
        PathRow(3, "FFmpeg folder (optional)", _txtFfmpeg, "Select the folder that contains ffmpeg.exe and ffprobe.exe");
        root.Controls.Add(paths, 0, 0);

        // ---- options ----
        var opts = new FlowLayoutPanel { Dock = DockStyle.Fill, AutoSize = true, WrapContents = true, Margin = new Padding(0, 6, 0, 0) };
        Label L(string t) => new() { Text = t, AutoSize = true, Margin = new Padding(8, 6, 2, 3) };
        opts.Controls.AddRange(new Control[]
        {
            L("Duplicate if match ≥"), _numMatch, L("%"),
            _chkPossible, _numPossible, L("%"),
            L("Frame tolerance (bits)"), _numTol,
            L("Duration tolerance"), _numDur, L("%"),
            L("Threads"), _numThreads,
            _chkSub,
        });
        _chkSub.Margin = new Padding(12, 6, 3, 3);
        _chkPossible.Margin = new Padding(12, 6, 3, 3);
        var optsRow2 = new FlowLayoutPanel { Dock = DockStyle.Fill, AutoSize = true, WrapContents = true };
        _chkNamed.Margin = new Padding(8, 3, 3, 3);
        optsRow2.Controls.Add(_chkNamed);
        var optsWrap = new FlowLayoutPanel { Dock = DockStyle.Fill, AutoSize = true, FlowDirection = FlowDirection.TopDown, WrapContents = false };
        optsWrap.Controls.Add(opts);
        optsWrap.Controls.Add(optsRow2);
        root.Controls.Add(optsWrap, 0, 1);

        // ---- buttons ----
        var actions = new FlowLayoutPanel { Dock = DockStyle.Fill, AutoSize = true, WrapContents = true, Margin = new Padding(0, 6, 0, 0) };
        _btnScan.Font = _bold;
        _btnMove.Margin = new Padding(24, 3, 3, 3);
        _btnShowSrc.Margin = new Padding(24, 3, 3, 3);
        _btnShowBoth.Margin = new Padding(3, 3, 3, 3);
        actions.Controls.AddRange(new Control[]
        {
            _btnScan, _btnCancel,
            _btnShowSrc, _btnShowTgt, _btnShowBoth, _btnPlaySrc, _btnPlayTgt, _btnDelSrc, _btnDelTgt,
            _btnCheckAll, _btnUncheckAll, _btnMove,
        });
        _btnCheckAll.Margin = new Padding(24, 3, 3, 3);
        root.Controls.Add(actions, 0, 2);

        // ---- progress ----
        var prog = new TableLayoutPanel { Dock = DockStyle.Fill, AutoSize = true, ColumnCount = 1, RowCount = 4, Margin = new Padding(0, 6, 0, 6) };
        prog.Controls.Add(_lblPhase, 0, 0);
        prog.Controls.Add(_bar, 0, 1);
        prog.Controls.Add(_lblStats, 0, 2);
        prog.Controls.Add(_lblCurrent, 0, 3);
        root.Controls.Add(prog, 0, 3);

        // ---- results grid + summary ----
        BuildGrid();
        var top = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 2 };
        top.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        top.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        top.Controls.Add(_grid, 0, 0);
        top.Controls.Add(_lblSummary, 0, 1);
        _split.Panel1.Controls.Add(top);

        // ---- samples + log tabs ----
        var sampleTable = new TableLayoutPanel { AutoSize = true, ColumnCount = 1, RowCount = 4, Location = new Point(0, 0) };
        sampleTable.Controls.Add(_lblSrcInfo, 0, 0);
        sampleTable.Controls.Add(_flpSrc, 0, 1);
        sampleTable.Controls.Add(_lblTgtInfo, 0, 2);
        sampleTable.Controls.Add(_flpTgt, 0, 3);
        var scroll = new Panel { Dock = DockStyle.Fill, AutoScroll = true };
        scroll.Controls.Add(sampleTable);
        _tabSamples.Controls.Add(scroll);
        _tabLog.Controls.Add(_txtLog);
        _tabs.TabPages.Add(_tabSamples);
        _tabs.TabPages.Add(_tabLog);
        _split.Panel2.Controls.Add(_tabs);

        root.Controls.Add(_split, 0, 4);
    }

    void BuildGrid()
    {
        _grid.Dock = DockStyle.Fill;
        _grid.AllowUserToAddRows = false;
        _grid.AllowUserToDeleteRows = false;
        _grid.AllowUserToResizeRows = false;
        _grid.AutoGenerateColumns = false;
        _grid.RowHeadersVisible = false;
        _grid.MultiSelect = false;
        _grid.SelectionMode = DataGridViewSelectionMode.FullRowSelect;
        _grid.BackgroundColor = SystemColors.Window;
        _grid.BorderStyle = BorderStyle.FixedSingle;
        _grid.EnableHeadersVisualStyles = true;
        _grid.ColumnHeadersHeightSizeMode = DataGridViewColumnHeadersHeightSizeMode.AutoSize;

        _grid.Columns.Add(new DataGridViewCheckBoxColumn { Name = "move", HeaderText = "Move", Width = 50 });
        DataGridViewTextBoxColumn C(string name, string header, int width, DataGridViewContentAlignment align = DataGridViewContentAlignment.MiddleLeft)
            => new() { Name = name, HeaderText = header, Width = width, ReadOnly = true, SortMode = DataGridViewColumnSortMode.Automatic, DefaultCellStyle = { Alignment = align } };
        var right = DataGridViewContentAlignment.MiddleRight;
        _grid.Columns.AddRange(
            C("status", "Status", 75),
            C("score", "Match", 55, right),
            C("srcName", "Source file", 230),
            C("srcSize", "Source size", 85, right),
            C("srcRes", "Source res", 80),
            C("tgtName", "Target file", 230),
            C("tgtSize", "Target size", 85, right),
            C("tgtRes", "Target res", 80),
            C("sizeDiff", "Size diff (src − tgt)", 120, right),
            C("durDiff", "Duration diff", 80, right),
            C("srcDir", "Source location", 300),
            C("tgtDir", "Target location", 300));
    }

    // =====================================================================
    //  events
    // =====================================================================
    void WireEvents()
    {
        _btnScan.Click += async (_, _) => await RunScan();
        _btnCancel.Click += (_, _) => { _opCts?.Cancel(); Log("Cancel requested..."); };
        _btnMove.Click += async (_, _) => await MoveChecked();

        _btnShowSrc.Click += (_, _) => { if (CurrentPair() is { } p) ShowInExplorer(p.Source); };
        _btnShowTgt.Click += (_, _) => { if (CurrentPair() is { } p) ShowInExplorer(p.Target); };
        _btnShowBoth.Click += (_, _) => { if (CurrentPair() is { } p) ShowBothSideBySide(p.Source, p.Target); };
        _btnPlaySrc.Click += (_, _) => { if (CurrentPair() is { } p) PlayFile(p.Source); };
        _btnPlayTgt.Click += (_, _) => { if (CurrentPair() is { } p) PlayFile(p.Target); };
        _btnDelSrc.Click += (_, _) => { if (CurrentPair() is { } p) DeleteOne(p.Source, p.SourceSize); };
        _btnDelTgt.Click += (_, _) => { if (CurrentPair() is { } p) DeleteOne(p.Target, p.TargetSize); };
        _btnCheckAll.Click += (_, _) => SetAllChecked(true);
        _btnUncheckAll.Click += (_, _) => SetAllChecked(false);

        _numMatch.ValueChanged += (_, _) => { if (_all.Count > 0) Populate(); };
        _chkPossible.CheckedChanged += (_, _) => { _numPossible.Enabled = _chkPossible.Checked; if (_all.Count > 0) Populate(); };

        _grid.CellFormatting += Grid_CellFormatting;
        _grid.SelectionChanged += (_, _) => OnSelectionChanged();
        _grid.CurrentCellDirtyStateChanged += (_, _) =>
        {
            if (_grid.IsCurrentCellDirty) _grid.CommitEdit(DataGridViewDataErrorContexts.Commit);
        };
        _grid.CellValueChanged += (_, e) => { if (e.ColumnIndex == 0 && !_populating) UpdateSummary(); };
        _grid.CellDoubleClick += (_, e) => { if (e.RowIndex >= 0 && e.ColumnIndex > 0 && CurrentPair() is { } p) ShowInExplorer(p.Source); };
        _grid.CellMouseDown += (_, e) =>
        {
            if (e.Button == MouseButtons.Right && e.RowIndex >= 0)
            {
                _grid.ClearSelection();
                _grid.Rows[e.RowIndex].Selected = true;
                _grid.CurrentCell = _grid.Rows[e.RowIndex].Cells[1];
            }
        };

        var menu = new ContextMenuStrip();
        menu.Items.Add("Show source in folder", null, (_, _) => _btnShowSrc.PerformClick());
        menu.Items.Add("Show target in folder", null, (_, _) => _btnShowTgt.PerformClick());
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("Play source", null, (_, _) => _btnPlaySrc.PerformClick());
        menu.Items.Add("Play target", null, (_, _) => _btnPlayTgt.PerformClick());
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("Delete source...", null, (_, _) => _btnDelSrc.PerformClick());
        menu.Items.Add("Delete target...", null, (_, _) => _btnDelTgt.PerformClick());
        _grid.ContextMenuStrip = menu;

        var tip = new ToolTip();
        tip.SetToolTip(_btnScan, "Fingerprints all videos and lists duplicates. Nothing is moved or deleted. Run it again after you delete files - already analysed videos are cached, so it is fast.");
        tip.SetToolTip(_btnDelSrc, "Sends the SOURCE file to the Recycle Bin and removes it from the list.");
        tip.SetToolTip(_btnDelTgt, "Sends the TARGET file to the Recycle Bin and removes it from the list.");
        tip.SetToolTip(_numTol, "How many of the 63 hash bits may differ for two frames to count as the same picture. Higher = more lenient.");
        tip.SetToolTip(_numDur, "Videos are only compared if their lengths are within max(2 seconds, this percentage).");
        tip.SetToolTip(_numMatch, "Share of sampled frames that must have a look-alike in the other video.");
    }

    // =====================================================================
    //  settings
    // =====================================================================
    void LoadSettingsToUi()
    {
        var s = _settings;
        _txtSource.Text = s.SourceFolder;
        _txtTarget.Text = s.TargetFolder;
        _txtDelete.Text = s.DeleteRoot;
        _txtFfmpeg.Text = s.FfmpegFolder;
        _numMatch.Value = Math.Clamp(s.MatchScorePercent, 1, 100);
        _numPossible.Value = Math.Clamp(s.PossibleScorePercent, 1, 100);
        _numTol.Value = Math.Clamp(s.FrameTolerance, 0, 32);
        _numDur.Value = Math.Clamp(s.DurationTolerancePercent, 0, 50);
        _numThreads.Value = Math.Clamp(s.Threads > 0 ? s.Threads : Environment.ProcessorCount, 1, 64);
        _chkSub.Checked = s.IncludeSubfolders;
        _chkPossible.Checked = s.ShowPossible;
        _numPossible.Enabled = s.ShowPossible;
        _chkNamed.Checked = s.FolderNamedAfterVideo;
    }

    AppSettings ReadSettings() => new()
    {
        SourceFolder = _txtSource.Text.Trim(),
        TargetFolder = _txtTarget.Text.Trim(),
        DeleteRoot = _txtDelete.Text.Trim(),
        FfmpegFolder = _txtFfmpeg.Text.Trim(),
        MatchScorePercent = (int)_numMatch.Value,
        PossibleScorePercent = (int)_numPossible.Value,
        FrameTolerance = (int)_numTol.Value,
        DurationTolerancePercent = (int)_numDur.Value,
        Threads = (int)_numThreads.Value,
        IncludeSubfolders = _chkSub.Checked,
        ShowPossible = _chkPossible.Checked,
        FolderNamedAfterVideo = _chkNamed.Checked,
    };

    void SaveSettings() => ReadSettings().Save();

    void Browse(TextBox tb, string title)
    {
        using var dlg = new FolderBrowserDialog { Description = title, UseDescriptionForTitle = true, ShowNewFolderButton = true };
        if (Directory.Exists(tb.Text)) dlg.InitialDirectory = tb.Text;
        if (dlg.ShowDialog(this) == DialogResult.OK)
        {
            tb.Text = dlg.SelectedPath;
            SaveSettings();
        }
    }

    // =====================================================================
    //  scan
    // =====================================================================
    async Task RunScan()
    {
        var s = ReadSettings();
        if (!Directory.Exists(s.SourceFolder)) { MessageBox.Show(this, "The source folder does not exist.", "Scan", MessageBoxButtons.OK, MessageBoxIcon.Warning); return; }
        if (!Directory.Exists(s.TargetFolder)) { MessageBox.Show(this, "The target folder does not exist.", "Scan", MessageBoxButtons.OK, MessageBoxIcon.Warning); return; }
        s.Save();

        SetBusy(true);
        _tabs.SelectedTab = _tabLog;
        _thumbCts?.Cancel();
        _all = new List<DuplicatePair>();
        Populate();
        Log("==== Scan started (dry run, nothing will be moved or deleted) ====");

        _opCts = new CancellationTokenSource();
        var ct = _opCts.Token;
        var progress = new Progress<ScanProgress>(UpdateProgress);
        var log = new Progress<string>(Log);
        var sw = Stopwatch.StartNew();
        try
        {
            var tools = new FfTools(s.FfmpegFolder);
            var result = await Task.Run(() => new Scanner(tools).Scan(s, progress, log, ct), ct);
            _all = result;
            // Save results for later retrieval
            DuplicatePair.SaveResults(_all);
            Populate();
            _lblPhase.Text = $"Scan finished in {Scanner.FormatDuration(sw.Elapsed.TotalSeconds)}.";
            _lblStats.Text = "";
            _lblCurrent.Text = "";
            _bar.Value = _bar.Maximum;
            _tabs.SelectedTab = _tabSamples;
            if (_grid.Rows.Count == 0)
                Log("No duplicates found. Try lowering the match %, raising the frame tolerance or enabling possible matches, then scan again.");
        }
        catch (OperationCanceledException)
        {
            Log("Scan cancelled.");
            _lblPhase.Text = "Scan cancelled.";
        }
        catch (Exception ex)
        {
            Log("ERROR: " + ex.Message);
            _lblPhase.Text = "Scan failed.";
            MessageBox.Show(this, ex.Message, "Scan", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
        finally
        {
            SetBusy(false);
        }
    }

    void UpdateProgress(ScanProgress p)
    {
        _lblPhase.Text = p.Phase;
        _bar.Maximum = Math.Max(1, p.Total);
        _bar.Value = Math.Clamp(p.Done, 0, _bar.Maximum);
        int left = Math.Max(0, p.Total - p.Done);
        string pct = p.Total > 0 ? $"{100.0 * p.Done / p.Total:0}%" : "";
        _lblStats.Text = $"{p.Done} of {p.Total} done ({pct})  |  {left} left  |  elapsed {Scanner.FormatDuration(p.Elapsed.TotalSeconds)}"
                         + (p.Eta is { } eta ? $"  |  time left ≈ {Scanner.FormatDuration(eta.TotalSeconds)}" : "");
        _lblCurrent.Text = string.IsNullOrEmpty(p.Current) ? "" : "Current: " + p.Current;
    }

    // =====================================================================
    //  results grid
    // =====================================================================
    void Populate()
    {
        _populating = true;
        _grid.SuspendLayout();
        _grid.Rows.Clear();
        double match = (double)_numMatch.Value / 100.0;
        foreach (var p in _all.OrderByDescending(x => x.Score))
        {
            bool dup = p.Score >= match;
            if (!dup && !_chkPossible.Checked) continue;
            int i = _grid.Rows.Add(dup, dup ? "Duplicate" : "Possible", p.Score,
                Path.GetFileName(p.Source), p.SourceSize, Res(p.SourceWidth, p.SourceHeight),
                Path.GetFileName(p.Target), p.TargetSize, Res(p.TargetWidth, p.TargetHeight),
                p.SourceSize - p.TargetSize, p.SourceDuration - p.TargetDuration,
                Path.GetDirectoryName(p.Source), Path.GetDirectoryName(p.Target));
            var row = _grid.Rows[i];
            row.Tag = p;
            if (!dup) row.DefaultCellStyle.ForeColor = Color.DimGray;
        }
        _grid.ResumeLayout();
        _populating = false;
        UpdateSummary();
        OnSelectionChanged();
    }

    void Grid_CellFormatting(object? sender, DataGridViewCellFormattingEventArgs e)
    {
        if (e.RowIndex < 0 || e.Value == null) return;
        var name = _grid.Columns[e.ColumnIndex].Name;
        switch (name)
        {
            case "score":
                e.Value = ((double)e.Value).ToString("P0", CultureInfo.CurrentCulture);
                e.FormattingApplied = true;
                break;
            case "srcSize":
            case "tgtSize":
                e.Value = FormatBytes((long)e.Value);
                e.FormattingApplied = true;
                if (_grid.Rows[e.RowIndex].Tag is DuplicatePair p)
                {
                    bool larger = name == "srcSize" ? p.SourceSize > p.TargetSize : p.TargetSize > p.SourceSize;
                    if (larger) e.CellStyle!.Font = _bold;
                }
                break;
            case "sizeDiff":
                {
                    long d = (long)e.Value;
                    e.Value = (d > 0 ? "+" : d < 0 ? "−" : "") + FormatBytes(Math.Abs(d));
                    e.FormattingApplied = true;
                    break;
                }
            case "durDiff":
                e.Value = ((double)e.Value).ToString("+0.0;-0.0;0.0", CultureInfo.InvariantCulture) + " s";
                e.FormattingApplied = true;
                break;
        }
    }

    DuplicatePair? CurrentPair() => _grid.CurrentRow?.Tag as DuplicatePair;

    void SetAllChecked(bool value)
    {
        _populating = true;
        foreach (DataGridViewRow r in _grid.Rows) r.Cells[0].Value = value;
        _populating = false;
        UpdateSummary();
    }

    void UpdateSummary()
    {
        int dup = 0, poss = 0, chk = 0;
        foreach (DataGridViewRow r in _grid.Rows)
        {
            if ((string?)r.Cells["status"].Value == "Duplicate") dup++; else poss++;
            if (r.Cells[0].Value is true) chk++;
        }
        _lblSummary.Text = _grid.Rows.Count == 0
            ? "No results yet."
            : $"{dup} duplicate pair(s), {poss} possible.  {chk} checked for moving.  Larger file of each pair is shown in bold.  " +
              "Tip: delete files yourself (buttons / right-click / Explorer), then click Scan again - deleted files will no longer be listed.";
        UpdateButtons();
    }

    void RemoveFiles(IEnumerable<string> paths)
    {
        var set = new HashSet<string>(paths, StringComparer.OrdinalIgnoreCase);
        _all.RemoveAll(p => set.Contains(p.Source) || set.Contains(p.Target));
        _populating = true;
        for (int i = _grid.Rows.Count - 1; i >= 0; i--)
            if (_grid.Rows[i].Tag is DuplicatePair p && (set.Contains(p.Source) || set.Contains(p.Target)))
                _grid.Rows.RemoveAt(i);
        _populating = false;
        UpdateSummary();
        OnSelectionChanged();
    }

    // =====================================================================
    //  per-pair actions
    // =====================================================================
    void ShowInExplorer(string path)
    {
        try
        {
            string targetFolder = path;
            bool selectFile = false;

            if (File.Exists(path))
            {
                targetFolder = Path.GetDirectoryName(path) ?? path;
                selectFile = true;
            }
            else if (Directory.Exists(Path.GetDirectoryName(path)))
            {
                MessageBox.Show(this, "That file no longer exists. Opening its folder instead.\nClick Scan again to refresh the list.", "File missing", MessageBoxButtons.OK, MessageBoxIcon.Information);
                targetFolder = Path.GetDirectoryName(path) ?? path;
            }
            else
            {
                return;
            }

            // Normalize the path for comparison
            string normalizedTarget = Path.GetFullPath(targetFolder).TrimEnd(Path.DirectorySeparatorChar);

            // Try to find an existing explorer window for this folder
            var existingProcess = Process.GetProcessesByName("explorer")
                .FirstOrDefault(p => IsExplorerAtPath(p, normalizedTarget));

            if (existingProcess != null && existingProcess.MainWindowHandle != IntPtr.Zero)
            {
                // Focus the existing window
                SetForegroundWindow(existingProcess.MainWindowHandle);
                if (selectFile)
                {
                    // If we want to select a specific file, we still need to open a new instance
                    // since we can't reliably send the selection command to an existing window
                    Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{path}\"") { UseShellExecute = true });
                }
            }
            else
            {
                // Open new explorer window
                if (selectFile)
                    Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{path}\"") { UseShellExecute = true });
                else
                    Process.Start(new ProcessStartInfo("explorer.exe", $"\"{targetFolder}\"") { UseShellExecute = true });
            }
        }
        catch (Exception ex) { MessageBox.Show(this, ex.Message, "Explorer", MessageBoxButtons.OK, MessageBoxIcon.Error); }
    }

    static bool IsExplorerAtPath(Process process, string targetPath)
    {
        try
        {
            // Look for explorer processes and check environment/command line
            // This is a heuristic - check if the process is still alive
            if (process.HasExited) return false;

            // Check the MainWindowTitle which often contains the folder path
            string title = process.MainWindowTitle;
            if (string.IsNullOrEmpty(title)) return false;

            // Common heuristic: window titles often end with folder name or contain the path
            // For better accuracy, you would need to use COM interfaces (IShellWindows)
            // This simplified version uses the window title
            return title.IndexOf(targetPath, StringComparison.OrdinalIgnoreCase) >= 0 ||
                   title.EndsWith(Path.GetFileName(targetPath), StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return false;
        }
    }

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    static extern bool SetForegroundWindow(IntPtr hWnd);

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    static extern bool GetWindowPlacement(IntPtr hWnd, ref WINDOWPLACEMENT lpwndpl);

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    static extern bool SetWindowPlacement(IntPtr hWnd, [System.Runtime.InteropServices.In] ref WINDOWPLACEMENT lpwndpl);

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    static extern IntPtr MonitorFromWindow(IntPtr hwnd, uint dwFlags);

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    static extern bool GetMonitorInfo(IntPtr hMonitor, ref MONITORINFO lpmi);

    struct WINDOWPLACEMENT
    {
        public uint length;
        public uint flags;
        public uint showCmd;
        public System.Drawing.Point ptMinPosition;
        public System.Drawing.Point ptMaxPosition;
        public System.Drawing.Rectangle rcNormalPosition;
    }

    struct MONITORINFO
    {
        public uint cbSize;
        public System.Drawing.Rectangle rcMonitor;
        public System.Drawing.Rectangle rcWork;
        public uint dwFlags;
    }

    async void ShowBothSideBySide(string sourcePath, string targetPath)
    {
        try
        {
            if (!File.Exists(sourcePath) && !Directory.Exists(sourcePath))
                return;

            if (!File.Exists(targetPath) && !Directory.Exists(targetPath))
                return;

            var result = await ExplorerHelper.OpenAndArrangeSideBySideAsync(sourcePath, targetPath, this);

            if (result.success)
            {
                // Runs on UI thread so Windows allows Explorer windows to rise above your app
                ExplorerHelper.BringExplorersToFront(result.srcRoot, result.tgtRoot);
            }
            else
            {
                MessageBox.Show(this, "Could not find both Explorer windows. Please arrange windows manually.", "Info", MessageBoxButtons.OK, MessageBoxIcon.Information);
            }
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.Message, "Show Both Side-by-Side", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }

    IntPtr FindExplorerWindow(string folderPath)
    {
        // Try to find an Explorer window showing this folder
        var explorerProcesses = Process.GetProcessesByName("explorer");

        foreach (var proc in explorerProcesses)
        {
            try
            {
                // Check if window title contains the folder name
                if (proc.MainWindowHandle != IntPtr.Zero && proc.MainWindowTitle.Contains(Path.GetFileName(folderPath)))
                {
                    return proc.MainWindowHandle;
                }
            }
            catch { }
        }

        // If no match found, get the most recent Explorer window
        if (explorerProcesses.Length > 0)
        {
            var recent = explorerProcesses.OrderByDescending(p => p.StartTime).FirstOrDefault();
            if (recent != null && recent.MainWindowHandle != IntPtr.Zero)
            {
                return recent.MainWindowHandle;
            }
        }

        return IntPtr.Zero;
    }

    static bool IsWindows11()
    {
        try
        {
            var version = Environment.OSVersion.Version;
            // Windows 11 is version 10.0.22000 or higher
            return version.Major == 10 && version.Minor == 0 && version.Build >= 22000;
        }
        catch
        {
            return false;
        }
    }

    [System.Runtime.InteropServices.DllImport("user32.dll", SetLastError = true)]
    static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter, int X, int Y, int cx, int cy, uint uFlags);

    [System.Runtime.InteropServices.DllImport("user32.dll", SetLastError = true)]
    static extern bool PostMessage(IntPtr hWnd, int Msg, IntPtr wParam, IntPtr lParam);

    const uint SWP_NOZORDER = 0x0004;
    const uint SWP_SHOWWINDOW = 0x0040;

    void ManualSideBySideLayout(IntPtr srcHandle, IntPtr tgtHandle)
    {
        try
        {
            // Get monitor info
            IntPtr hMonitor = MonitorFromWindow(srcHandle, 0);
            var monitorInfo = new MONITORINFO { cbSize = (uint)System.Runtime.InteropServices.Marshal.SizeOf(typeof(MONITORINFO)) };

            if (GetMonitorInfo(hMonitor, ref monitorInfo))
            {
                var workArea = monitorInfo.rcWork;
                int halfWidth = (workArea.Width - 2) / 2; // -2 for window border

                // Arrange windows side by side
                var srcPlacement = new WINDOWPLACEMENT 
                { 
                    length = (uint)System.Runtime.InteropServices.Marshal.SizeOf(typeof(WINDOWPLACEMENT)),
                    flags = 0,
                    showCmd = 1, // SW_SHOWNORMAL
                    rcNormalPosition = new System.Drawing.Rectangle(workArea.X, workArea.Y, workArea.X + halfWidth, workArea.Y + workArea.Height)
                };

                var tgtPlacement = new WINDOWPLACEMENT
                { 
                    length = (uint)System.Runtime.InteropServices.Marshal.SizeOf(typeof(WINDOWPLACEMENT)),
                    flags = 0,
                    showCmd = 1, // SW_SHOWNORMAL
                    rcNormalPosition = new System.Drawing.Rectangle(workArea.X + halfWidth + 1, workArea.Y, workArea.X + workArea.Width, workArea.Y + workArea.Height)
                };

                SetWindowPlacement(srcHandle, ref srcPlacement);
                SetWindowPlacement(tgtHandle, ref tgtPlacement);
            }

            // Bring source window to front
            SetForegroundWindow(srcHandle);
        }
        catch
        {
            // If all else fails, just bring windows to front
            SetForegroundWindow(srcHandle);
        }
    }

    void PlayFile(string path)
    {
        try
        {
            if (!File.Exists(path)) { MessageBox.Show(this, "That file no longer exists.", "Play", MessageBoxButtons.OK, MessageBoxIcon.Information); return; }
            Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
        }
        catch (Exception ex) { MessageBox.Show(this, ex.Message, "Play", MessageBoxButtons.OK, MessageBoxIcon.Error); }
    }

    void DeleteOne(string path, long size)
    {
        if (!File.Exists(path))
        {
            RemoveFiles(new[] { path });
            return;
        }
        var answer = MessageBox.Show(this,
            $"Send this file to the Recycle Bin?\n\n{path}\n({FormatBytes(size)})\n\n" +
            "If the drive has no Recycle Bin, Windows may delete it permanently.",
            "Delete file", MessageBoxButtons.YesNo, MessageBoxIcon.Warning, MessageBoxDefaultButton.Button2);
        if (answer != DialogResult.Yes) return;
        try
        {
            Microsoft.VisualBasic.FileIO.FileSystem.DeleteFile(path,
                Microsoft.VisualBasic.FileIO.UIOption.OnlyErrorDialogs,
                Microsoft.VisualBasic.FileIO.RecycleOption.SendToRecycleBin);
            Log($"DELETED (Recycle Bin): {path}");
            RemoveFiles(new[] { path });
        }
        catch (Exception ex)
        {
            Log($"ERROR deleting {path}: {ex.Message}");
            MessageBox.Show(this, ex.Message, "Delete file", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }

    async Task MoveChecked()
    {
        var items = _grid.Rows.Cast<DataGridViewRow>()
            .Where(r => r.Cells[0].Value is true && r.Tag is DuplicatePair)
            .Select(r => (DuplicatePair)r.Tag!).ToList();
        if (items.Count == 0) { MessageBox.Show(this, "No pairs are checked.", "Move", MessageBoxButtons.OK, MessageBoxIcon.Information); return; }

        var s = ReadSettings();
        if (string.IsNullOrWhiteSpace(s.DeleteRoot)) { MessageBox.Show(this, "Choose the Deleted folder first.", "Move", MessageBoxButtons.OK, MessageBoxIcon.Warning); return; }
        var answer = MessageBox.Show(this,
            $"Move {items.Count} pair(s) - both the source and the target file of each - into:\n\n{s.DeleteRoot}\n\n" +
            (s.FolderNamedAfterVideo ? "one sub-folder named after each video." : "numbered sub-folders 1, 2, 3...") + "\n\nContinue?",
            "Move duplicates", MessageBoxButtons.YesNo, MessageBoxIcon.Question);
        if (answer != DialogResult.Yes) return;
        s.Save();

        SetBusy(true);
        _tabs.SelectedTab = _tabLog;
        _opCts = new CancellationTokenSource();
        var ct = _opCts.Token;
        IProgress<ScanProgress> progress = new Progress<ScanProgress>(UpdateProgress);
        IProgress<string> log = new Progress<string>(Log);
        var movedFiles = new List<string>();
        int ok = 0, failed = 0;
        Log($"==== Moving {items.Count} pair(s) to {s.DeleteRoot} ====");
        try
        {
            await Task.Run(() =>
            {
                var sw = Stopwatch.StartNew();
                int i = 0;
                foreach (var p in items)
                {
                    ct.ThrowIfCancellationRequested();
                    var eta = i == 0 ? (TimeSpan?)null : TimeSpan.FromSeconds(sw.Elapsed.TotalSeconds / i * (items.Count - i));
                    progress.Report(new ScanProgress("Moving duplicate pairs", i, items.Count, Path.GetFileName(p.Source), sw.Elapsed, eta));
                    if (PairMover.Move(p.Source, p.Target, s.DeleteRoot, s.FolderNamedAfterVideo, out var dest, out var msg))
                    {
                        ok++;
                        movedFiles.Add(p.Source); movedFiles.Add(p.Target);
                        log.Report($"MOVED  {Path.GetFileName(p.Source)}  +  {Path.GetFileName(p.Target)}  ->  {dest}");
                    }
                    else
                    {
                        failed++;
                        log.Report("FAILED " + msg);
                    }
                    i++;
                }
                progress.Report(new ScanProgress("Moving duplicate pairs", items.Count, items.Count, "", sw.Elapsed, TimeSpan.Zero));
            }, ct);
        }
        catch (OperationCanceledException) { Log("Move cancelled."); }
        catch (Exception ex) { Log("ERROR: " + ex.Message); MessageBox.Show(this, ex.Message, "Move", MessageBoxButtons.OK, MessageBoxIcon.Error); }
        finally
        {
            RemoveFiles(movedFiles);
            _lblPhase.Text = $"Move finished: {ok} pair(s) moved and verified, {failed} failed.";
            Log(_lblPhase.Text);
            SetBusy(false);
        }
    }

    // =====================================================================
    //  sample frames
    // =====================================================================
    async void OnSelectionChanged()
    {
        UpdateButtons();
        if (_populating) return;

        _thumbCts?.Cancel();
        ClearSamples();
        var pair = CurrentPair();
        if (pair == null)
        {
            _lblSrcInfo.Text = _lblTgtInfo.Text = "";
            return;
        }
        _lblSrcInfo.Text = Describe("SOURCE", pair.Source, pair.SourceSize, pair.SourceWidth, pair.SourceHeight, pair.SourceDuration) + "   (loading frames...)";
        _lblTgtInfo.Text = Describe("TARGET", pair.Target, pair.TargetSize, pair.TargetWidth, pair.TargetHeight, pair.TargetDuration) + "   (loading frames...)";

        var cts = _thumbCts = new CancellationTokenSource();
        var ct = cts.Token;
        try
        {
            await Task.Delay(200, ct);   // don't load while the user is scrolling through rows
            var tools = new FfTools(_txtFfmpeg.Text.Trim());
            var tSrc = Task.Run(() => tools.Thumbnails(pair.Source, pair.SourceDuration, Scanner.SamplePoints, ThumbW * 2, ct), ct);
            var tTgt = Task.Run(() => tools.Thumbnails(pair.Target, pair.TargetDuration, Scanner.SamplePoints, ThumbW * 2, ct), ct);
            var src = await tSrc;
            ct.ThrowIfCancellationRequested();
            FillThumbs(_flpSrc, src, pair.SourceDuration);
            _lblSrcInfo.Text = Describe("SOURCE", pair.Source, pair.SourceSize, pair.SourceWidth, pair.SourceHeight, pair.SourceDuration);
            var tgt = await tTgt;
            ct.ThrowIfCancellationRequested();
            FillThumbs(_flpTgt, tgt, pair.TargetDuration);
            _lblTgtInfo.Text = Describe("TARGET", pair.Target, pair.TargetSize, pair.TargetWidth, pair.TargetHeight, pair.TargetDuration);
        }
        catch (OperationCanceledException) { }
        catch (ObjectDisposedException) { }
    }

    static string Describe(string role, string path, long size, int w, int h, double dur)
        => $"{role}:  {Path.GetFileName(path)}   |   {FormatBytes(size)}   |   {Res(w, h)}   |   {Scanner.FormatDuration(dur)}\n{Path.GetDirectoryName(path)}";

    void FillThumbs(FlowLayoutPanel panel, byte[]?[] data, double duration)
    {
        for (int i = 0; i < data.Length; i++)
        {
            var t = duration * Scanner.SamplePoints[i];
            panel.Controls.Add(MakeThumb(ToImage(data[i]), $"{Scanner.SamplePoints[i]:P0}  ·  {Scanner.FormatDuration(t)}"));
        }
    }

    static Image? ToImage(byte[]? bytes)
    {
        if (bytes == null) return null;
        try
        {
            using var ms = new MemoryStream(bytes);
            using var img = Image.FromStream(ms);
            return new Bitmap(img);
        }
        catch { return null; }
    }

    static Control MakeThumb(Image? img, string caption)
    {
        var p = new Panel { Width = ThumbW + 6, Height = ThumbH + 24, Margin = new Padding(2) };
        var pb = new PictureBox { Left = 3, Top = 0, Width = ThumbW, Height = ThumbH, SizeMode = PictureBoxSizeMode.Zoom, BackColor = Color.Black, Image = img };
        var l = new Label { Left = 0, Top = ThumbH + 4, Width = p.Width, Height = 18, TextAlign = ContentAlignment.MiddleCenter, Text = img == null ? caption + " (no frame)" : caption };
        p.Controls.Add(pb);
        p.Controls.Add(l);
        return p;
    }

    void ClearSamples()
    {
        foreach (var flp in new[] { _flpSrc, _flpTgt })
        {
            foreach (Control c in flp.Controls.Cast<Control>().ToList())
            {
                foreach (var pb in c.Controls.OfType<PictureBox>()) pb.Image?.Dispose();
                flp.Controls.Remove(c);
                c.Dispose();
            }
        }
    }

    // =====================================================================
    //  helpers
    // =====================================================================
    void SetBusy(bool busy)
    {
        _busy = busy;
        UpdateButtons();
        foreach (Control c in new Control[] { _txtSource, _txtTarget, _txtDelete, _txtFfmpeg, _numPossible, _numTol, _numDur, _numThreads, _chkSub, _chkNamed })
            c.Enabled = !busy;
        _numPossible.Enabled = !busy && _chkPossible.Checked;
        if (busy) { _bar.Value = 0; _lblStats.Text = ""; _lblCurrent.Text = ""; }
    }

    void UpdateButtons()
    {
        bool hasRow = CurrentPair() != null;
        _btnScan.Enabled = !_busy;
        _btnCancel.Enabled = _busy;
        foreach (var b in new[] { _btnShowSrc, _btnShowTgt, _btnShowBoth, _btnPlaySrc, _btnPlayTgt, _btnDelSrc, _btnDelTgt })
            b.Enabled = hasRow && !_busy;
        _btnCheckAll.Enabled = _btnUncheckAll.Enabled = _grid.Rows.Count > 0 && !_busy;
        _btnMove.Enabled = _grid.Rows.Count > 0 && !_busy;
    }

    void Log(string msg)
    {
        _txtLog.AppendText($"[{DateTime.Now:HH:mm:ss}] {msg}{Environment.NewLine}");
    }

    static string Res(int w, int h) => w > 0 && h > 0 ? $"{w}x{h}" : "?";

    static string FormatBytes(long bytes)
    {
        string[] u = { "B", "KB", "MB", "GB", "TB" };
        double v = bytes; int i = 0;
        while (v >= 1024 && i < u.Length - 1) { v /= 1024; i++; }
        return i == 0 ? $"{v:0} {u[i]}" : $"{v:0.##} {u[i]}";
    }
}