using MVZ2ModManager.Theme;
using MVZ2ModManager.Core;
using MVZ2ModManager.UI.Controls;

namespace MVZ2ModManager.UI.Panels;

internal sealed class LogsPanel : UserControl
{
    private readonly RichTextBox _logBox;
    private readonly FlowLayoutPanel _toolbar;
    private readonly RDropdown _sessionDropdown;
    private readonly RTextBox _searchBox;
    private readonly RDropdown _severityDropdown;
    private readonly RButton _exportBugReportBtn;
    private readonly RButton _clearBtn;
    private readonly Panel _culpritBanner;
    private readonly Label _culpritLabel;
    private readonly RButton _culpritDismissBtn;
    private readonly System.Windows.Forms.Timer _timer;

    private FileStream? _logStream;
    private StreamReader? _logReader;
    private string? _currentLogPath;
    private bool _wasRunning;
    private bool _reportedMissingLog;
    private bool _reportedOpenError;

    private readonly List<string> _liveLines = new();
    private List<string> _archivedLines = new();
    private List<LogSession> _sessions = new();
    private bool _viewingLive = true;
    private int _liveClearIndex;
    private int _archivedClearIndex;
    private DateTime _sessionStartedAt;

    public LogsPanel()
    {
        Dock = DockStyle.Fill;
        DoubleBuffered = true;

        _logBox = new RichTextBox
        {
            Dock = DockStyle.Fill, ReadOnly = true, BorderStyle = BorderStyle.None,
            Font = new Font("Consolas", 9f), WordWrap = true, DetectUrls = false,
        };

        _culpritBanner = new Panel { Dock = DockStyle.Top, Height = 34, Visible = false, Padding = new Padding(10, 0, 6, 0) };
        _culpritDismissBtn = new RButton { Text = "✕", Dock = DockStyle.Right, Width = 30, CornerRadius = 6 };
        _culpritDismissBtn.Click += (_, __) => _culpritBanner.Visible = false;
        _culpritLabel = new Label
        {
            Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleLeft, AutoEllipsis = true,
            Font = ThemeEngine.MakeFont(9.5f, FontStyle.Bold),
        };
        _culpritBanner.Controls.Add(_culpritDismissBtn);
        _culpritBanner.Controls.Add(_culpritLabel);

        _toolbar = new FlowLayoutPanel
        {
            Dock = DockStyle.Bottom, AutoSize = true, WrapContents = true,
            FlowDirection = FlowDirection.LeftToRight, Padding = new Padding(8),
        };

        _sessionDropdown = new RDropdown { Width = 180, Height = 30, CornerRadius = 8, Margin = new Padding(0, 0, 8, 8) };

        _searchBox = new RTextBox { Width = 200, Height = 30, CornerRadius = 8, PlaceholderText = "搜索日志…", Margin = new Padding(0, 0, 8, 8) };
        _searchBox.TextChanged += (_, __) => RenderAll();

        _severityDropdown = new RDropdown { Width = 150, Height = 30, CornerRadius = 8, Margin = new Padding(0, 0, 8, 8) };
        _severityDropdown.Items.Add("全部行");
        _severityDropdown.Items.Add("仅错误");
        _severityDropdown.Items.Add("仅警告");
        _severityDropdown.Items.Add("错误 + 警告");
        _severityDropdown.SelectedIndex = 0;
        _severityDropdown.SelectedIndexChanged += (_, __) => RenderAll();

        _exportBugReportBtn = new RButton { Text = "导出问题报告", CornerRadius = 8, AutoSize = true, Padding = new Padding(10, 3, 10, 3), Margin = new Padding(0, 0, 8, 8) };
        _exportBugReportBtn.Click += (_, __) => ExportBugReport();

        _clearBtn = new RButton { Text = "清空", CornerRadius = 8, AutoSize = true, Padding = new Padding(10, 3, 10, 3), Margin = new Padding(0, 0, 0, 8) };
        _clearBtn.Click += (_, __) =>
        {
            if (_viewingLive) _liveClearIndex = _liveLines.Count;
            else _archivedClearIndex = _archivedLines.Count;
            RenderAll();
        };

        _sessionDropdown.SelectedIndexChanged += (_, __) =>
        {
            if (_sessionDropdown.SelectedIndex <= 0) { ViewLive(); return; }
            int idx = _sessionDropdown.SelectedIndex - 1;
            if (idx >= 0 && idx < _sessions.Count) ViewArchived(_sessions[idx]);
        };

        _toolbar.Controls.Add(_sessionDropdown);
        _toolbar.Controls.Add(_searchBox);
        _toolbar.Controls.Add(_severityDropdown);
        _toolbar.Controls.Add(_exportBugReportBtn);
        _toolbar.Controls.Add(_clearBtn);

        Controls.Add(_logBox);
        Controls.Add(_culpritBanner);
        Controls.Add(_toolbar);

        ThemeEngine.ThemeChanged += ApplyTheme;
        HandleCreated += (_, __) => ThemeEngine.ApplyScrollTheme(this);
        ApplyTheme();

        _timer = new System.Windows.Forms.Timer { Interval = 1000 };
        _timer.Tick += (_, __) => Tick();
        _timer.Start();

        RefreshSessionDropdown(preserveSelection: false);
    }

    public void Refresh_() { }

    private void Tick()
    {
        bool running = IsGameRunning();
        if (running && !_wasRunning) OnGameStarted();
        else if (!running && _wasRunning) OnGameStopped();
        else if (running) PollLog();
        _wasRunning = running;
    }

    /// <summary>当前这套安装的游戏在不在跑。按**完整路径**匹配 ——
    /// 所有版本的 exe 同名，只按名字匹配会误判成"别的版本在跑"。</summary>
    private static bool IsGameRunning() => AppState.IsGameRunning();

    /// <summary>
    /// BepInEx 的日志文件。它从**游戏启动**就开始写，所以进程一起来我们就能跟上，
    /// 不需要任何注入 —— 这也是本工具能当"加载进度看板"的原因。
    /// </summary>
    private static string? ResolveLogPath() => AppState.LogOutputPath;

    private void OnGameStarted()
    {
        _currentLogPath = ResolveLogPath();
        _sessionStartedAt = DateTime.Now;
        _liveLines.Clear();
        _liveClearIndex = 0;
        _reportedMissingLog = false;
        _reportedOpenError = false;
        _culpritBanner.Visible = false;

        if (_sessionDropdown.SelectedIndex != 0) _sessionDropdown.SelectedIndex = 0;
        else { _viewingLive = true; RenderAll(); }

        AddLine($"{AppState.CurrentDisplayName} 正在运行，正在跟随它的日志文件。");
        AddLine(_currentLogPath != null ? $"日志路径：{_currentLogPath}" : "没能推断出这个游戏的日志文件应该在哪里。");
        AddLine("");
        PollLog();
    }

    private void OnGameStopped()
    {
        AddLine("");
        AddLine("游戏已关闭。");
        UpdateCulpritBanner(_liveLines);
        LogHistoryManager.ArchiveSession(_sessionStartedAt, _liveLines);
        RefreshSessionDropdown(preserveSelection: true);
        CloseLogStream();
    }

    private void PollLog()
    {
        if (_logReader == null)
        {
            if (_currentLogPath == null) return;

            if (!File.Exists(_currentLogPath))
            {
                if (!_reportedMissingLog)
                {
                    AddLine($"仍在等待 {Path.GetFileName(_currentLogPath)} 出现。加载器会在游戏启动完成后创建它。");
                    _reportedMissingLog = true;
                }
                return;
            }

            try
            {
                _logStream = new FileStream(_currentLogPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                _logReader = new StreamReader(_logStream);
            }
            catch (Exception ex)
            {
                if (!_reportedOpenError)
                {
                    AddLine($"打开日志文件失败：{ex.Message}");
                    _reportedOpenError = true;
                }
                return;
            }
        }

        string? line;
        while ((line = _logReader.ReadLine()) != null) AddLine(line);
    }

    private void CloseLogStream()
    {
        _logReader?.Dispose();
        _logStream?.Dispose();
        _logReader = null;
        _logStream = null;
        _currentLogPath = null;
    }

    private void AddLine(string line)
    {
        _liveLines.Add(line);
        if (_viewingLive && PassesFilter(line)) AppendStyledLine(line);
    }

    private void ViewLive()
    {
        _viewingLive = true;
        RenderAll();
        if (_logBox.TextLength == 0 && !IsGameRunning())
            AppendStyledLine($"当前没有游戏在运行，所以没有内容可显示。启动 {AppState.CurrentDisplayName} 后，这个标签页就会实时跟随它的日志。");
        UpdateCulpritBanner(_liveLines);
    }

    private void ViewArchived(LogSession session)
    {
        _viewingLive = false;
        _archivedClearIndex = 0;
        try { _archivedLines = File.ReadAllLines(session.FilePath).ToList(); }
        catch { _archivedLines = new List<string> { "（无法读取本次会话的日志文件）" }; }
        RenderAll();
        UpdateCulpritBanner(_archivedLines);
    }

    private void RefreshSessionDropdown(bool preserveSelection)
    {
        string? keepText = preserveSelection && _sessionDropdown.SelectedIndex >= 0 && _sessionDropdown.SelectedIndex < _sessionDropdown.Items.Count
            ? _sessionDropdown.Items[_sessionDropdown.SelectedIndex]
            : null;

        _sessions = LogHistoryManager.ListSessions();
        _sessionDropdown.Items.Clear();
        _sessionDropdown.Items.Add("本次会话");
        foreach (var s in _sessions) _sessionDropdown.Items.Add(s.DisplayName);

        int targetIndex = keepText != null ? _sessionDropdown.Items.IndexOf(keepText) : 0;
        if (targetIndex < 0) targetIndex = 0;

        if (_sessionDropdown.SelectedIndex == targetIndex)
        {
            if (targetIndex == 0) ViewLive();
        }
        else
        {
            _sessionDropdown.SelectedIndex = targetIndex;
        }
    }

    private List<string> ActiveLines() => _viewingLive ? _liveLines : _archivedLines;

    private int ActiveClearIndex() => _viewingLive ? _liveClearIndex : _archivedClearIndex;

    private void RenderAll()
    {
        _logBox.Clear();
        foreach (var line in ActiveLines().Skip(ActiveClearIndex()))
            if (PassesFilter(line)) AppendStyledLine(line);
    }

    private bool PassesFilter(string line)
    {
        string search = _searchBox.Text.Trim();
        if (search.Length > 0 && !line.Contains(search, StringComparison.OrdinalIgnoreCase)) return false;

        return _severityDropdown.SelectedIndex switch
        {
            1 => IsError(line),
            2 => IsWarning(line),
            3 => IsError(line) || IsWarning(line),
            _ => true,
        };
    }

    private static bool IsError(string line) =>
        line.Contains("Exception", StringComparison.OrdinalIgnoreCase) ||
        line.Contains("Error", StringComparison.OrdinalIgnoreCase) ||
        line.Contains("Fatal", StringComparison.OrdinalIgnoreCase);

    private static bool IsWarning(string line) =>
        line.Contains("Warn", StringComparison.OrdinalIgnoreCase);

    private Color ClassifyColor(string line)
    {
        if (IsError(line)) return Color.FromArgb(255, 95, 86);
        if (IsWarning(line)) return Color.FromArgb(255, 193, 7);
        return ThemeEngine.Current.Text;
    }

    private void AppendStyledLine(string line)
    {
        _logBox.SelectionStart = _logBox.TextLength;
        _logBox.SelectionLength = 0;
        _logBox.SelectionColor = ClassifyColor(line);
        _logBox.AppendText(line + Environment.NewLine);
        _logBox.SelectionStart = _logBox.TextLength;
        _logBox.ScrollToCaret();
    }

    private void UpdateCulpritBanner(List<string> lines)
    {
        var suspect = CrashAnalyzer.Analyze(lines);
        if (suspect == null) { _culpritBanner.Visible = false; return; }
        _culpritLabel.Text = $"⚠ 可疑元凶：{suspect.ModName}。{suspect.Reason}";
        _culpritBanner.Visible = true;
    }

    private void ExportBugReport()
    {
        using var dlg = new SaveFileDialog
        {
            Title = "导出问题报告",
            Filter = "Zip 压缩包|*.zip",
            FileName = $"MVZ2ModManager-BugReport-{DateTime.Now:yyyy-MM-dd_HH-mm-ss}.zip",
            InitialDirectory = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory),
        };
        if (dlg.ShowDialog() != DialogResult.OK) return;

        try
        {
            BugReportManager.Export(dlg.FileName, ActiveLines());
            MessageBox.Show($"问题报告已保存到：\n{dlg.FileName}", "问题报告", MessageBoxButtons.OK, MessageBoxIcon.Information);
        }
        catch (Exception ex)
        {
            MessageBox.Show("导出问题报告失败：" + ex.Message, "问题报告", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }

    private void ApplyTheme()
    {
        var t = ThemeEngine.Current;
        BackColor = t.Background;
        _toolbar.BackColor = t.Surface;
        _logBox.BackColor = t.Background;
        _logBox.ForeColor = t.Text;

        _culpritBanner.BackColor = t.SurfaceAlt;
        _culpritLabel.ForeColor = Color.FromArgb(255, 159, 10);
        ThemeEngine.StyleGhostButton(_culpritDismissBtn);

        ThemeEngine.StyleGhostButton(_clearBtn);
        ThemeEngine.StyleGhostButton(_exportBugReportBtn);

        foreach (var dd in new[] { _sessionDropdown, _severityDropdown })
        {
            dd.FillColor = t.SurfaceAlt;
            dd.HoverFillColor = t.Border;
            dd.BorderColor = Color.Transparent;
            dd.ForeColor = t.Text;
        }

        _searchBox.BackColor = t.SurfaceAlt;
        _searchBox.ForeColor = t.Text;

        ThemeEngine.ApplyScrollTheme(this);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            ThemeEngine.ThemeChanged -= ApplyTheme;
            _timer.Dispose();
            CloseLogStream();
        }
        base.Dispose(disposing);
    }
}
