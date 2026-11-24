using System.Diagnostics;
using System.Globalization;
using System.Windows.Forms;

namespace VideoDuplicateFinder;

public sealed class MainForm : Form
{
    const int ThumbW = 176, ThumbH = 99;
    const string ColSelectSource = "selectSource";
    const string ColSelectTarget = "selectTarget";

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
    readonly Button _btnSelectSourceAll = new() { Text = "Select source", AutoSize = true };
    readonly Button _btnSelectTargetAll = new() { Text = "Select target", AutoSize = true };
    readonly Button _btnUncheckAll = new() { Text = "Uncheck all", AutoSize = true };
    readonly Button _btnHighQuality = new() { Text = "High Quality", AutoSize = true };
    readonly Button _btnMoveSelected = new() { Text = "Move selected to Deleted folder...", AutoSize = true };
    readonly Button _btnDeleteSelected = new()
    {
        Text = "Delete selected (Recycle Bin)",
        AutoSize = true,
        BackColor = Color.Firebrick,
        ForeColor = Color.White,
        FlatStyle = FlatStyle.Flat
    };
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
        _btnDeleteSelected.FlatAppearance.BorderColor = Color.DarkRed;

        BuildUi();
        LoadSettingsToUi();
        WireEvents();
        UpdateButtons();

        Load += (_, _) =>
        {
            try { _split.SplitterDistance = (int)(_split.Height * 0.52); } catch { }

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
        PathRow(2, "Deleted folder (selected videos are moved here)", _txtDelete, "Select the folder that receives selected duplicate videos");
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
        _btnMoveSelected.Margin = new Padding(24, 3, 3, 3);
        _btnDeleteSelected.Margin = new Padding(3, 3, 3, 3);
        _btnShowSrc.Margin = new Padding(24, 3, 3, 3);
        _btnShowBoth.Margin = new Padding(3, 3, 3, 3);
        actions.Controls.AddRange(new Control[]
        {
            _btnScan, _btnCancel,
            _btnShowSrc, _btnShowTgt, _btnShowBoth, _btnPlaySrc, _btnPlayTgt, _btnDelSrc, _btnDelTgt,
            _btnSelectSourceAll, _btnSelectTargetAll, _btnUncheckAll, _btnHighQuality, _btnMoveSelected, _btnDeleteSelected,
        });
        _btnSelectSourceAll.Margin = new Padding(24, 3, 3, 3);
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

        _grid.Columns.Add(new DataGridViewCheckBoxColumn { Name = ColSelectSource, HeaderText = "Select source", Width = 85 });
        _grid.Columns.Add(new DataGridViewCheckBoxColumn { Name = ColSelectTarget, HeaderText = "Select target", Width = 85 });

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
        _btnMoveSelected.Click += async (_, _) => await MoveSelected();
        _btnDeleteSelected.Click += (_, _) => DeleteSelected();

        _btnShowSrc.Click += (_, _) => { if (CurrentPair() is { } p) ShowInExplorer(p.Source); };
        _btnShowTgt.Click += (_, _) => { if (CurrentPair() is { } p) ShowInExplorer(p.Target); };
        _btnShowBoth.Click += (_, _) => { if (CurrentPair() is { } p) ShowBothSideBySide(p.Source, p.Target); };
        _btnPlaySrc.Click += (_, _) => { if (CurrentPair() is { } p) PlayFile(p.Source); };
        _btnPlayTgt.Click += (_, _) => { if (CurrentPair() is { } p) PlayFile(p.Target); };
        _btnDelSrc.Click += (_, _) => { if (CurrentPair() is { } p) DeleteOne(p.Source, p.SourceSize); };
        _btnDelTgt.Click += (_, _) => { if (CurrentPair() is { } p) DeleteOne(p.Target, p.TargetSize); };
        _btnSelectSourceAll.Click += (_, _) => SetColumnAllChecked(selectSource: true);
        _btnSelectTargetAll.Click += (_, _) => SetColumnAllChecked(selectSource: false);
        _btnUncheckAll.Click += (_, _) => ClearAllChecked();
        _btnHighQuality.Click += (_, _) => ApplyHighQualitySelection();

        _numMatch.ValueChanged += (_, _) => { if (_all.Count > 0) Populate(); };
        _chkPossible.CheckedChanged += (_, _) => { _numPossible.Enabled = _chkPossible.Checked; if (_all.Count > 0) Populate(); };

        _grid.CellFormatting += Grid_CellFormatting;
        _grid.SelectionChanged += (_, _) => OnSelectionChanged();
        _grid.CurrentCellDirtyStateChanged += (_, _) =>
        {
            if (_grid.IsCurrentCellDirty) _grid.CommitEdit(DataGridViewDataErrorContexts.Commit);
        };
        _grid.CellValueChanged += (_, e) =>
        {
            if (_populating || e.RowIndex < 0) return;

            string colName = _grid.Columns[e.ColumnIndex].Name;
            if (colName == ColSelectSource || colName == ColSelectTarget)
            {
                _populating = true;
                try
                {
                    var row = _grid.Rows[e.RowIndex];
                    if (colName == ColSelectSource && row.Cells[ColSelectSource].Value is true)
                        row.Cells[ColSelectTarget].Value = false;
                    else if (colName == ColSelectTarget && row.Cells[ColSelectTarget].Value is true)
                        row.Cells[ColSelectSource].Value = false;
                }
                finally
                {
                    _populating = false;
                }
                UpdateSummary();
            }
        };
        _grid.CellDoubleClick += (_, e) => { if (e.RowIndex >= 0 && e.ColumnIndex > 1 && CurrentPair() is { } p) ShowInExplorer(p.Source); };
        _grid.CellMouseDown += (_, e) =>
        {
            if (e.Button == MouseButtons.Right && e.RowIndex >= 0)
            {
                _grid.ClearSelection();
                _grid.Rows[e.RowIndex].Selected = true;
                _grid.CurrentCell = _grid.Rows[e.RowIndex].Cells[2];
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
        tip.SetToolTip(_btnSelectSourceAll, "Checks Select source for all rows and unchecks Select target.");
        tip.SetToolTip(_btnSelectTargetAll, "Checks Select target for all rows and unchecks Select source.");
        tip.SetToolTip(_btnUncheckAll, "Unchecks both Select source and Select target across all rows.");
        tip.SetToolTip(_btnHighQuality, "Selects source or target based on size and resolution (if source size & resolution > target, selects target; if target > source, selects source; if equal, selects target).");
        tip.SetToolTip(_btnMoveSelected, "Moves the selected source/target video in each checked row to the Deleted folder.");
        tip.SetToolTip(_btnDeleteSelected, "Sends the selected source/target video in each checked row directly to the Recycle Bin.");
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
    static (bool selectSource, bool selectTarget) GetDefaultSelection(DuplicatePair p)
    {
        long srcRes = (long)Math.Max(0, p.SourceWidth) * Math.Max(0, p.SourceHeight);
        long tgtRes = (long)Math.Max(0, p.TargetWidth) * Math.Max(0, p.TargetHeight);

        bool srcGreater = p.SourceSize > p.TargetSize && srcRes > tgtRes;
        bool tgtGreater = p.TargetSize > p.SourceSize && tgtRes > srcRes;
        bool bothEqual = p.SourceSize == p.TargetSize && srcRes == tgtRes;

        if (srcGreater)
            return (false, true);

        if (tgtGreater)
            return (true, false);

        if (bothEqual)
            return (false, true);

        return (false, true);
    }

    void ApplyHighQualitySelection()
    {
        _populating = true;
        foreach (DataGridViewRow r in _grid.Rows)
        {
            if (r.Tag is not DuplicatePair p) continue;
            var (selSrc, selTgt) = GetDefaultSelection(p);
            r.Cells[ColSelectSource].Value = selSrc;
            r.Cells[ColSelectTarget].Value = selTgt;
        }
        _populating = false;
        UpdateSummary();
    }

    void ClearAllChecked()
    {
        _populating = true;
        foreach (DataGridViewRow r in _grid.Rows)
        {
            if (r.Tag is not DuplicatePair) continue;
            r.Cells[ColSelectSource].Value = false;
            r.Cells[ColSelectTarget].Value = false;
        }
        _populating = false;
        UpdateSummary();
    }

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

            var (selSrc, selTgt) = GetDefaultSelection(p);

            int i = _grid.Rows.Add(
                selSrc, selTgt,
                dup ? "Duplicate" : "Possible", p.Score,
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
                    bool isSource = name == "srcSize";
                    bool larger = isSource ? p.SourceSize > p.TargetSize : p.TargetSize > p.SourceSize;
                    bool smaller = isSource ? p.SourceSize < p.TargetSize : p.TargetSize < p.SourceSize;
                    if (larger)
                        e.CellStyle!.Font = _bold;
                    else if (smaller)
                        e.CellStyle!.ForeColor = Color.Firebrick;
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

    void SetColumnAllChecked(bool selectSource)
    {
        _populating = true;
        foreach (DataGridViewRow r in _grid.Rows)
        {
            if (r.Tag is not DuplicatePair) continue;
            r.Cells[ColSelectSource].Value = selectSource;
            r.Cells[ColSelectTarget].Value = !selectSource;
        }
        _populating = false;
        UpdateSummary();
    }

    void UpdateSummary()
    {
        int dup = 0, poss = 0, selSrcCount = 0, selTgtCount = 0;
        foreach (DataGridViewRow r in _grid.Rows)
        {
            if ((string?)r.Cells["status"].Value == "Duplicate") dup++; else poss++;
            if (r.Cells[ColSelectSource].Value is true) selSrcCount++;
            if (r.Cells[ColSelectTarget].Value is true) selTgtCount++;
        }
        int totalSelected = selSrcCount + selTgtCount;
        _lblSummary.Text = _grid.Rows.Count == 0
            ? "No results yet."
            : $"{dup} duplicate pair(s), {poss} possible.  {totalSelected} video(s) selected ({selSrcCount} source, {selTgtCount} target).  Larger file of each pair is shown in bold.";
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

            string normalizedTarget = Path.GetFullPath(targetFolder).TrimEnd(Path.DirectorySeparatorChar);

            var existingProcess = Process.GetProcessesByName("explorer")
                .FirstOrDefault(p => ExplorerHelper.IsExplorerAtPath(p, normalizedTarget));

            if (existingProcess != null && existingProcess.MainWindowHandle != IntPtr.Zero)
            {
                ExplorerHelper.SetForegroundWindow(existingProcess.MainWindowHandle);
                if (selectFile)
                {
                    Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{path}\"") { UseShellExecute = true });
                }
            }
            else
            {
                if (selectFile)
                    Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{path}\"") { UseShellExecute = true });
                else
                    Process.Start(new ProcessStartInfo("explorer.exe", $"\"{targetFolder}\"") { UseShellExecute = true });
            }
        }
        catch (Exception ex) { MessageBox.Show(this, ex.Message, "Explorer", MessageBoxButtons.OK, MessageBoxIcon.Error); }
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

    void DeleteSelected()
    {
        var items = new List<(string Role, string FilePath)>();
        foreach (DataGridViewRow r in _grid.Rows)
        {
            if (r.Tag is not DuplicatePair p) continue;
            if (r.Cells[ColSelectSource].Value is true)
                items.Add(("Source", p.Source));
            if (r.Cells[ColSelectTarget].Value is true)
                items.Add(("Target", p.Target));
        }

        if (items.Count == 0)
        {
            MessageBox.Show(this, "No source or target videos are selected.", "Delete selected", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }

        var answer = MessageBox.Show(this,
            $"Send {items.Count} selected video(s) to the Recycle Bin?\n\n" +
            "If a drive has no Recycle Bin, Windows may delete it permanently.",
            "Delete selected videos", MessageBoxButtons.YesNo, MessageBoxIcon.Warning, MessageBoxDefaultButton.Button2);
        if (answer != DialogResult.Yes) return;

        var deletedFiles = new List<string>();
        int ok = 0, failed = 0;

        Log($"==== Deleting {items.Count} selected video(s) to Recycle Bin ====");
        foreach (var item in items)
        {
            if (!File.Exists(item.FilePath))
            {
                deletedFiles.Add(item.FilePath);
                continue;
            }

            try
            {
                Microsoft.VisualBasic.FileIO.FileSystem.DeleteFile(item.FilePath,
                    Microsoft.VisualBasic.FileIO.UIOption.OnlyErrorDialogs,
                    Microsoft.VisualBasic.FileIO.RecycleOption.SendToRecycleBin);
                ok++;
                deletedFiles.Add(item.FilePath);
                Log($"DELETED ({item.Role}) {item.FilePath}");
            }
            catch (Exception ex)
            {
                failed++;
                Log($"FAILED ({item.Role}) {item.FilePath}: {ex.Message}");
            }
        }

        RemoveFiles(deletedFiles);
        _lblPhase.Text = $"Delete finished: {ok} video(s) sent to Recycle Bin, {failed} failed.";
        Log(_lblPhase.Text);
    }

    async Task MoveSelected()
    {
        var items = new List<(string Role, string FilePath, DuplicatePair Pair)>();
        foreach (DataGridViewRow r in _grid.Rows)
        {
            if (r.Tag is not DuplicatePair p) continue;
            if (r.Cells[ColSelectSource].Value is true)
                items.Add(("Source", p.Source, p));
            if (r.Cells[ColSelectTarget].Value is true)
                items.Add(("Target", p.Target, p));
        }

        if (items.Count == 0)
        {
            MessageBox.Show(this, "No source or target videos are selected.", "Move", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }

        var s = ReadSettings();
        if (string.IsNullOrWhiteSpace(s.DeleteRoot))
        {
            MessageBox.Show(this, "Choose the Deleted folder first.", "Move", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }

        var answer = MessageBox.Show(this,
            $"Move {items.Count} selected video(s) into:\n\n{s.DeleteRoot}\n\nContinue?",
            "Move selected videos", MessageBoxButtons.YesNo, MessageBoxIcon.Question);
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

        Log($"==== Moving {items.Count} selected video(s) to {s.DeleteRoot} ====");
        try
        {
            await Task.Run(() =>
            {
                var sw = Stopwatch.StartNew();
                int i = 0;
                foreach (var item in items)
                {
                    ct.ThrowIfCancellationRequested();
                    var eta = i == 0 ? (TimeSpan?)null : TimeSpan.FromSeconds(sw.Elapsed.TotalSeconds / i * (items.Count - i));
                    progress.Report(new ScanProgress("Moving selected videos", i, items.Count, Path.GetFileName(item.FilePath), sw.Elapsed, eta));

                    if (PairMover.Move(item.FilePath, item.FilePath, s.DeleteRoot, s.FolderNamedAfterVideo, out var dest, out var msg))
                    {
                        ok++;
                        movedFiles.Add(item.FilePath);
                        log.Report($"MOVED ({item.Role}) {Path.GetFileName(item.FilePath)} -> {dest}");
                    }
                    else
                    {
                        failed++;
                        log.Report($"FAILED ({item.Role}) {msg}");
                    }
                    i++;
                }
                progress.Report(new ScanProgress("Moving selected videos", items.Count, items.Count, "", sw.Elapsed, TimeSpan.Zero));
            }, ct);
        }
        catch (OperationCanceledException) { Log("Move cancelled."); }
        catch (Exception ex) { Log("ERROR: " + ex.Message); MessageBox.Show(this, ex.Message, "Move", MessageBoxButtons.OK, MessageBoxIcon.Error); }
        finally
        {
            RemoveFiles(movedFiles);
            _lblPhase.Text = $"Move finished: {ok} video(s) moved, {failed} failed.";
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
            await Task.Delay(200, ct);
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
        _btnSelectSourceAll.Enabled = _btnSelectTargetAll.Enabled = _btnUncheckAll.Enabled = _btnHighQuality.Enabled = _grid.Rows.Count > 0 && !_busy;
        _btnMoveSelected.Enabled = _btnDeleteSelected.Enabled = _grid.Rows.Count > 0 && !_busy;
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