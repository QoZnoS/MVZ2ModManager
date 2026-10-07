using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;
using MVZ2ModManager.Core;
using MVZ2ModManager.Theme;
using MVZ2ModManager.UI.Controls;
using MVZ2ModManager.UI.Panels;

// ImplicitUsings 把 System.Threading 也带进来了，Timer 必须消歧义。
using Timer = System.Windows.Forms.Timer;

namespace MVZ2ModManager.UI;

/// <summary>
/// 主窗口：自绘标题栏 + 标签条 + 状态栏。
///
/// <para>标签页：<b>Installed</b>（开关模组）/ <b>Loadouts</b>（整套配置）/ <b>Config</b>（改 cfg）/
/// <b>Logs</b>（跟日志，兼作加载进度看板）/ <b>Settings</b>。</para>
///
/// <para>没有 <c>Mods</c>（在线浏览下载）与 <c>Modpacks</c>（打包分享）页 —— 前者依赖
/// 上游的模组站点，后者依赖下载器，留待后续按需补回。</para>
/// </summary>
internal sealed class MainForm : Form
{
    private const int WindowRadius = 14;
    private const int TabRadius = 8;

    private static readonly string[] NavLabels = { "Installed", "Loadouts", "Config", "Logs", "Settings" };

    private const int IndexInstalled = 0;
    private const int IndexLogs = 3;
    private const int IndexSettings = 4;

    // ---- 标题栏 ----
    private readonly Panel _titleBar;
    private readonly Label _titleLabel;
    private readonly Label _gameLabel;
    private readonly Panel _titleIconBox;
    private readonly Panel _titleGameIcon;

    // ---- 标签条 ----
    private readonly Panel _tabStrip;
    private readonly RButton[] _navButtons;
    private int _activeNavIndex;
    private readonly Timer _underlineTimer;
    private float _underlineX, _underlineW, _targetX, _targetW;

    // ---- Play ----
    private readonly RButton _playBtn;
    private readonly Timer _gameRunningTimer;
    private bool _gameRunning;
    private bool _starting;
    private DateTime? _launchedAt;
    private const int StartingTimeoutSeconds = 30;

    // ---- 面板 ----
    private readonly InstalledPanel _installedPanel;
    private readonly LoadoutsPanel _loadoutsPanel;
    private readonly ConfigPanel _configPanel;
    private readonly LogsPanel _logsPanel;
    private readonly SettingsPanel _settingsPanel;
    private readonly Control[] _navPanels;
    private readonly Action?[] _navActivate;

    // ---- 状态栏 ----
    private readonly Panel _statusBar;
    private readonly Label _statusLabel;
    private readonly Panel _resizeHandle;

    private Point _dragStart;
    private bool _dragging;

    public MainForm()
    {
        Icon = AppIcons.Icon;
        StartPosition = FormStartPosition.CenterScreen;
        Size = new Size(1100, 720);
        MinimumSize = new Size(880, 560);
        DoubleBuffered = true;
        Text = "MVZ2 Mod Manager";
        FormBorderStyle = FormBorderStyle.None;

        _installedPanel = new InstalledPanel { Visible = true };
        _loadoutsPanel = new LoadoutsPanel { Visible = false };
        _configPanel = new ConfigPanel { Visible = false };
        _logsPanel = new LogsPanel { Visible = false };
        _settingsPanel = new SettingsPanel { Visible = false };

        _navPanels = new Control[] { _installedPanel, _loadoutsPanel, _configPanel, _logsPanel, _settingsPanel };
        _navActivate = new Action?[]
        {
            () => _installedPanel.Refresh_(),
            () => _loadoutsPanel.Refresh_(),
            () => _configPanel.Refresh_(),
            () => _logsPanel.Refresh_(),
            () => _settingsPanel.Reload(),
        };

        var content = new Panel { Dock = DockStyle.Fill };
        // Controls[0] 在最前；虽然 SwitchToIndex 里还会 BringToFront，但初始顺序也保持自然顺序。
        foreach (var p in _navPanels) content.Controls.Add(p);

        _statusBar = new Panel { Dock = DockStyle.Bottom, Height = 24, Padding = new Padding(8, 0, 8, 0) };
        _statusLabel = new Label
        {
            Dock = DockStyle.Fill, AutoSize = false,
            TextAlign = ContentAlignment.MiddleLeft,
            Font = new Font("Segoe UI", 8f),
        };
        _resizeHandle = MakeResizeHandle();
        _statusBar.Controls.Add(_statusLabel);
        _statusBar.Controls.Add(_resizeHandle);

        Controls.Add(content);
        Controls.Add(_statusBar);

        // ---------------- 标题栏 ----------------
        _titleBar = new Panel { Dock = DockStyle.Top, Height = 40 };
        _titleBar.MouseDown += TitleBar_MouseDown;
        _titleBar.MouseMove += TitleBar_MouseMove;
        _titleBar.MouseUp += (_, __) => _dragging = false;

        var closeBtn = MakeLight(Color.FromArgb(255, 95, 86));
        var minBtn = MakeLight(Color.FromArgb(255, 189, 46));
        var maxBtn = MakeLight(Color.FromArgb(39, 201, 63));

        var lights = new Panel { Width = 78, Height = 40, Dock = DockStyle.Left };
        lights.MouseDown += TitleBar_MouseDown;
        lights.MouseMove += TitleBar_MouseMove;
        lights.MouseUp += (_, __) => _dragging = false;

        int lx = 14;
        foreach (var btn in new[] { closeBtn, minBtn, maxBtn })
        {
            btn.Location = new Point(lx, 13);
            btn.Cursor = Cursors.Hand;
            lights.Controls.Add(btn);
            lx += 22;
        }

        closeBtn.Click += (_, __) => Close();
        minBtn.Click += (_, __) => WindowState = FormWindowState.Minimized;
        maxBtn.Click += (_, __) =>
            WindowState = WindowState == FormWindowState.Maximized ? FormWindowState.Normal : FormWindowState.Maximized;

        _titleLabel = new Label
        {
            Text = "MVZ2 Mod Manager",
            Dock = DockStyle.Fill,
            TextAlign = ContentAlignment.MiddleCenter,
            Font = new Font("Segoe UI", 10f),
        };
        _titleLabel.MouseDown += TitleBar_MouseDown;
        _titleLabel.MouseMove += TitleBar_MouseMove;
        _titleLabel.MouseUp += (_, __) => _dragging = false;

        var gameBox = new Panel { Dock = DockStyle.Right, Width = 220 };
        const int titleIconSize = 20;
        _titleIconBox = new Panel { Dock = DockStyle.Right, Width = 32 };
        _titleGameIcon = MakeCircularIcon(titleIconSize);
        _titleGameIcon.Location = new Point((32 - titleIconSize) / 2, (40 - titleIconSize) / 2);
        _titleIconBox.Controls.Add(_titleGameIcon);

        _gameLabel = new Label
        {
            Text = "",
            Dock = DockStyle.Fill,
            TextAlign = ContentAlignment.MiddleRight,
            Font = new Font("Segoe UI", 8.5f),
            Padding = new Padding(0, 0, 8, 0),
            Cursor = Cursors.Hand,
            AutoEllipsis = true,
        };
        _gameLabel.Click += (_, __) => SwitchGame();
        _titleIconBox.Cursor = Cursors.Hand;
        _titleIconBox.Click += (_, __) => SwitchGame();

        gameBox.Controls.Add(_gameLabel);
        gameBox.Controls.Add(_titleIconBox);

        _titleBar.Controls.Add(gameBox);
        _titleBar.Controls.Add(_titleLabel);
        _titleBar.Controls.Add(lights);

        // ---------------- 标签条 ----------------
        _tabStrip = new Panel { Dock = DockStyle.Top, Height = 38 };
        _tabStrip.Paint += TabStrip_Paint;

        _navButtons = NavLabels.Select(MakeTabBtn).ToArray();
        for (int i = 0; i < _navButtons.Length; i++)
        {
            int idx = i;
            _navButtons[i].Click += (_, __) => SwitchToIndex(idx);
        }

        var tabFlow = new FlowLayoutPanel
        {
            Dock = DockStyle.Left, AutoSize = true, FlowDirection = FlowDirection.LeftToRight,
            WrapContents = false, Padding = new Padding(8, 0, 0, 0),
        };
        foreach (var b in _navButtons) tabFlow.Controls.Add(b);
        _tabStrip.Controls.Add(tabFlow);

        var playWrap = new Panel { Dock = DockStyle.Right, Width = 130, Padding = new Padding(8, 3, 8, 3) };
        _playBtn = MakePlayBtn();
        _playBtn.Dock = DockStyle.Fill;
        playWrap.Controls.Add(_playBtn);
        _tabStrip.Controls.Add(playWrap);

        var tabSep = new Panel { Dock = DockStyle.Top, Height = 1, Tag = "border_panel" };
        var titleSep = new Panel { Dock = DockStyle.Top, Height = 1, Tag = "border_panel" };

        Controls.Add(tabSep);
        Controls.Add(_tabStrip);
        Controls.Add(titleSep);
        Controls.Add(_titleBar);

        _underlineTimer = new Timer { Interval = 12 };
        _underlineTimer.Tick += (_, __) => StepUnderlineAnim();

        // ---------------- 游戏进程监视 ----------------
        _gameRunningTimer = new Timer { Interval = 1000 };
        _gameRunningTimer.Tick += (_, __) => CheckGameRunning();
        _gameRunningTimer.Start();

        AllowDrop = true;
        DragEnter += MainForm_DragEnter;
        DragDrop += MainForm_DragDrop;

        ThemeEngine.ThemeChanged += ApplyTheme;
        ApplyTheme();

        Load += (_, __) =>
        {
            UpdateGameLabel();
            CheckGameRunning();
            HighlightNav(0, animate: false);
            _navActivate[0]?.Invoke();
            UpdateStatusBar();
            ApplyRoundedRegion();
        };
        Resize += (_, __) => ApplyRoundedRegion();
        SizeChanged += (_, __) => ApplyRoundedRegion();
    }

    // ------------------------------------------------------------ 对外接口

    internal void SetStatus(string msg) => _statusLabel.Text = msg;

    /// <summary>按标签名切页（面板想跳转时用）。</summary>
    internal void SwitchToTab(string label)
    {
        int idx = Array.IndexOf(NavLabels, label);
        if (idx >= 0) SwitchToIndex(idx);
    }

    internal InstalledPanel InstalledPanelControl => _installedPanel;

    internal bool GameIsRunning => _gameRunning;

    // ------------------------------------------------------------ 状态栏

    /// <summary>状态栏：模组计数 + 总开关状态 + 游戏是否在跑（DLL 会被锁）。</summary>
    internal void UpdateStatusBar()
    {
        var gameDir = AppState.GameDir;
        if (gameDir == null) { _statusLabel.Text = "No game selected."; return; }

        var mods = ModInstaller.GetInstalled();
        int enabled = mods.Count(m => m.Enabled);
        int disabled = mods.Count - enabled;

        var parts = new List<string>
        {
            $"{enabled} enabled",
            $"{disabled} disabled",
        };

        if (!BepInExManager.ModsEnabled(gameDir))
            parts.Add("⚠ BepInEx is OFF (winhttp.dll renamed) — no mods will load");

        if (_gameRunning)
            parts.Add("⚠ game is running — plugin DLLs are locked");

        _statusLabel.Text = string.Join("  ·  ", parts);
    }

    // ------------------------------------------------------------ 标签切换

    private void SwitchToIndex(int idx)
    {
        if (idx < 0 || idx >= _navPanels.Length) return;

        for (int i = 0; i < _navPanels.Length; i++)
            _navPanels[i].Visible = i == idx;

        _navPanels[idx].BringToFront();
        _navActivate[idx]?.Invoke();
        HighlightNav(idx);
        UpdateStatusBar();
    }

    private void HighlightNav(int idx, bool animate = true)
    {
        _activeNavIndex = idx;
        foreach (var b in _navButtons)
        {
            bool active = b == _navButtons[idx];
            b.Font = new Font("Segoe UI", 9.5f, active ? FontStyle.Bold : FontStyle.Regular);
        }

        _targetX = _navButtons[idx].Bounds.Left + 6;
        _targetW = _navButtons[idx].Bounds.Width - 12;

        if (!animate) { _underlineX = _targetX; _underlineW = _targetW; }
        _underlineTimer.Start();
        _tabStrip.Invalidate();
    }

    private void StepUnderlineAnim()
    {
        const float step = 0.25f;
        bool doneX = Math.Abs(_underlineX - _targetX) < 0.5f;
        bool doneW = Math.Abs(_underlineW - _targetW) < 0.5f;
        _underlineX += (_targetX - _underlineX) * step;
        _underlineW += (_targetW - _underlineW) * step;
        if (doneX && doneW) { _underlineX = _targetX; _underlineW = _targetW; _underlineTimer.Stop(); }
        _tabStrip.Invalidate();
    }

    private void TabStrip_Paint(object? sender, PaintEventArgs e)
    {
        if (_underlineW <= 0) return;
        var t = ThemeEngine.Current;
        e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
        var rect = new RectangleF(_underlineX, _tabStrip.Height - 4, _underlineW, 3);
        using var path = RoundedGraphics.RoundedRect(Rectangle.Round(rect), 2);
        using var brush = new SolidBrush(t.Accent);
        e.Graphics.FillPath(brush, path);
    }

    // ------------------------------------------------------------ 标题栏

    private void TitleBar_MouseDown(object? s, MouseEventArgs e)
    {
        if (e.Button != MouseButtons.Left) return;
        _dragging = true;
        _dragStart = e.Location;
    }

    private void TitleBar_MouseMove(object? s, MouseEventArgs e)
    {
        if (!_dragging || e.Button != MouseButtons.Left) return;
        if (WindowState == FormWindowState.Maximized) WindowState = FormWindowState.Normal;
        Location = new Point(Location.X + e.X - _dragStart.X, Location.Y + e.Y - _dragStart.Y);
    }

    private void UpdateGameLabel()
    {
        _gameLabel.Text = AppState.Settings.GamePath.Length > 0
            ? Path.GetDirectoryName(AppState.Settings.GamePath) ?? AppState.Settings.GameName
            : "No game selected";

        bool hasGame = AppState.Settings.GamePath.Length > 0;
        _titleIconBox.Visible = hasGame;
        _titleGameIcon.Tag = hasGame ? AppIcons.TryExtractGameIcon(AppState.Settings.GamePath) ?? AppIcons.Png : null;
        _titleGameIcon.Invalidate();
    }

    private void SwitchGame()
    {
        if (_gameRunning)
        {
            MessageBox.Show(this, "Close the game first.", "Game is running",
                MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }

        using var picker = new GamePickerForm();
        picker.ShowDialog(this);
        if (!picker.Confirmed) return;

        UpdateGameLabel();
        _navActivate[_activeNavIndex]?.Invoke();
        UpdateStatusBar();
    }

    // ------------------------------------------------------------ 启动 / 停止

    private RButton MakePlayBtn()
    {
        var btn = new RButton
        {
            Text = "▶ Play", CornerRadius = 8,
            Font = new Font("Segoe UI", 9f, FontStyle.Bold),
            Cursor = Cursors.Hand, Style = RButtonStyle.Solid,
        };
        btn.Click += (_, __) => OnPlayClick();
        return btn;
    }

    private void OnPlayClick()
    {
        if (_starting) return;
        if (_gameRunning) StopGame();
        else LaunchGame();
    }

    private void LaunchGame()
    {
        string path = AppState.Settings.GamePath;
        if (string.IsNullOrEmpty(path) || !File.Exists(path)) return;

        var gameDir = AppState.GameDir;
        if (gameDir == null) return;

        // 总开关关着就直接提醒 —— 否则用户会以为模组坏了。
        if (!BepInExManager.ModsEnabled(gameDir))
        {
            string question = BepInExManager.HasDisabledMarker(gameDir)
                ? "BepInEx is currently switched OFF (winhttp.dll.disabled).\n\nTurn it back on and launch?"
                : "winhttp.dll was not found, so BepInEx won't load any mods.\n\nLaunch the game anyway?";

            if (BepInExManager.HasDisabledMarker(gameDir))
            {
                if (MessageBox.Show(this, question, "BepInEx is off",
                        MessageBoxButtons.YesNo, MessageBoxIcon.Warning) != DialogResult.Yes)
                    return;
                try { BepInExManager.ToggleMods(gameDir); }
                catch (Exception ex)
                {
                    MessageBox.Show(this, "Couldn't re-enable BepInEx: " + ex.Message, "Error",
                        MessageBoxButtons.OK, MessageBoxIcon.Error);
                    return;
                }
            }
            else if (MessageBox.Show(this, question, "BepInEx missing",
                         MessageBoxButtons.YesNo, MessageBoxIcon.Warning) != DialogResult.Yes)
            {
                return;
            }
        }

        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(path)
            {
                UseShellExecute = true,
                WorkingDirectory = Path.GetDirectoryName(path),
            });

            _launchedAt = DateTime.UtcNow;
            _starting = true;
            UpdatePlayButton();

            // 启动会自动跳到日志页：BepInEx 从第一行就开始写，这里正好当加载看板。
            SwitchToIndex(IndexLogs);
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, "Couldn't launch the game: " + ex.Message, "Error",
                MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }

    private void StopGame()
    {
        string? procName = AppState.GameProcessName;
        if (string.IsNullOrEmpty(procName)) return;

        foreach (var p in System.Diagnostics.Process.GetProcessesByName(procName))
            try { p.Kill(); } catch { }
    }

    private void CheckGameRunning()
    {
        bool running = IsGameProcessRunning();

        if (_starting)
        {
            if (running) _starting = false;
            else if (_launchedAt is { } startedAt && DateTime.UtcNow - startedAt > TimeSpan.FromSeconds(StartingTimeoutSeconds))
            {
                _starting = false;
                _launchedAt = null;
                UpdatePlayButton();
            }
        }

        if (running == _gameRunning) return;

        // 启动后几秒就没了 → 很可能崩了，直接把人送到日志页。
        if (!running && _gameRunning && _launchedAt is { } launched && DateTime.UtcNow - launched < TimeSpan.FromSeconds(10))
        {
            var check = MessageBox.Show(this,
                $"The game closed a few seconds after launching — it probably crashed.\n\nOpen the log?",
                "Game closed quickly", MessageBoxButtons.YesNo, MessageBoxIcon.Warning);
            if (check == DialogResult.Yes) SwitchToIndex(IndexLogs);
        }
        _launchedAt = null;

        _gameRunning = running;
        UpdatePlayButton();
        UpdateStatusBar();
    }

    private static bool IsGameProcessRunning()
    {
        string? procName = AppState.GameProcessName;
        if (string.IsNullOrEmpty(procName)) return false;
        try { return System.Diagnostics.Process.GetProcessesByName(procName).Length > 0; }
        catch { return false; }
    }

    private void UpdatePlayButton()
    {
        var t = ThemeEngine.Current;
        Color tint = _starting ? Color.FromArgb(52, 120, 246)
                   : _gameRunning ? Color.FromArgb(220, 60, 60)
                   : Color.FromArgb(60, 190, 100);

        _playBtn.Text = _starting ? "Starting..." : _gameRunning ? "■ Stop" : "▶ Play";
        _playBtn.Style = RButtonStyle.Solid;
        _playBtn.FillColor = RoundedGraphics.Lerp(t.SurfaceAlt, tint, 0.3f);
        _playBtn.HoverFillColor = RoundedGraphics.Lerp(t.SurfaceAlt, tint, 0.45f);
        _playBtn.BorderColor = Color.Transparent;
        _playBtn.HoverBorderColor = Color.Transparent;
        _playBtn.ForeColor = t.Text;
    }

    // ------------------------------------------------------------ 拖拽安装

    private void MainForm_DragEnter(object? sender, DragEventArgs e)
    {
        bool hasDll = e.Data?.GetDataPresent(DataFormats.FileDrop) == true &&
            ((string[])e.Data.GetData(DataFormats.FileDrop)!).Any(f => f.EndsWith(".dll", StringComparison.OrdinalIgnoreCase));
        e.Effect = hasDll ? DragDropEffects.Copy : DragDropEffects.None;
    }

    private void MainForm_DragDrop(object? sender, DragEventArgs e)
    {
        if (e.Data?.GetData(DataFormats.FileDrop) is not string[] files) return;
        if (AppState.GameDir == null) { SetStatus("No game selected, can't install."); return; }

        if (_gameRunning)
        {
            MessageBox.Show(this, "Close the game first — plugin DLLs are locked while it runs.",
                "Game is running", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }

        int count = 0;
        foreach (var f in files.Where(f => f.EndsWith(".dll", StringComparison.OrdinalIgnoreCase)))
        {
            try { ModInstaller.InstallLocal(f); count++; } catch { }
        }

        if (count == 0) return;

        SetStatus($"Installed {count} dropped .dll file{(count == 1 ? "" : "s")}.");
        _installedPanel.Refresh_();
        UpdateStatusBar();
    }

    // ------------------------------------------------------------ 主题

    private void ApplyTheme()
    {
        var t = ThemeEngine.Current;
        BackColor = t.Background;

        _titleBar.BackColor = t.Surface;
        _titleLabel.BackColor = t.Surface;
        _titleLabel.ForeColor = t.Text;
        _titleIconBox.BackColor = t.Surface;
        _gameLabel.BackColor = t.Surface;
        _gameLabel.ForeColor = t.SubText;

        _tabStrip.BackColor = t.Surface;
        foreach (var b in _navButtons)
        {
            b.Style = RButtonStyle.Ghost;
            b.FillColor = Color.Transparent;
            b.HoverFillColor = t.SurfaceAlt;
            b.BorderColor = Color.Transparent;
            b.HoverBorderColor = Color.Transparent;
            b.ForeColor = t.Text;
        }

        _statusBar.BackColor = t.SurfaceAlt;
        _statusLabel.BackColor = t.SurfaceAlt;
        _statusLabel.ForeColor = t.SubText;
        _resizeHandle.Invalidate();

        foreach (Control c in Controls)
            if (c.Tag is "border_panel") c.BackColor = t.Border;

        UpdatePlayButton();
        Invalidate(true);
    }

    // ------------------------------------------------------------ 自绘零件

    private static RButton MakeTabBtn(string text) => new()
    {
        Text = text, Style = RButtonStyle.Ghost,
        AutoSize = false, Width = 92, Height = 32,
        Font = new Font("Segoe UI", 9.5f), Cursor = Cursors.Hand,
        CornerRadius = TabRadius,
        Margin = new Padding(0),
    };

    private static Panel MakeCircularIcon(int size)
    {
        var p = new Panel { Width = size, Height = size, BackColor = Color.Transparent };
        p.Paint += (s, e) =>
        {
            if (p.Tag is not Image img) return;
            e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
            using var path = new GraphicsPath();
            path.AddEllipse(0, 0, size - 1, size - 1);
            var oldClip = e.Graphics.Clip;
            e.Graphics.SetClip(path, CombineMode.Intersect);
            e.Graphics.DrawImage(img, 0, 0, size, size);
            e.Graphics.Clip = oldClip;
        };
        return p;
    }

    private static Panel MakeLight(Color c)
    {
        bool hovering = false;
        float glow = 0f;
        var timer = new Timer { Interval = 15 };

        var p = new Panel { Width = 13, Height = 13, BackColor = Color.Transparent };
        p.Paint += (s, e) =>
        {
            e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
            if (glow > 0.01f)
            {
                int r = (int)(2 * glow);
                using var glowBrush = new SolidBrush(Color.FromArgb((int)(60 * glow), Color.White));
                e.Graphics.FillEllipse(glowBrush, -r, -r, 12 + r * 2, 12 + r * 2);
            }
            using var b = new SolidBrush(c);
            e.Graphics.FillEllipse(b, 0, 0, 12, 12);
        };
        timer.Tick += (_, __) =>
        {
            float target = hovering ? 1f : 0f;
            if (Math.Abs(glow - target) < 0.08f) { glow = target; timer.Stop(); }
            else glow += target > glow ? 0.2f : -0.2f;
            p.Invalidate();
        };
        p.MouseEnter += (_, __) => { hovering = true; timer.Start(); };
        p.MouseLeave += (_, __) => { hovering = false; timer.Start(); };
        return p;
    }

    private static Panel MakeResizeHandle()
    {
        var p = new Panel { Width = 20, Dock = DockStyle.Right, Cursor = Cursors.SizeNWSE, BackColor = Color.Transparent };
        p.Paint += (s, e) =>
        {
            e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
            using var pen = new Pen(ThemeEngine.Current.Border, 1.4f);
            const int m = 6;
            e.Graphics.DrawLine(pen, p.Width - m, p.Height - m - 8, p.Width - m - 8, p.Height - m);
        };
        return p;
    }

    // ------------------------------------------------------------ 边框 / 命中测试

    private void ApplyRoundedRegion()
    {
        if (FormBorderStyle != FormBorderStyle.None) return;
        if (WindowState == FormWindowState.Maximized) { Region = null; return; }
        if (ClientSize.Width <= 0 || ClientSize.Height <= 0) return;
        using var path = RoundedGraphics.RoundedRect(new Rectangle(0, 0, ClientSize.Width, ClientSize.Height), WindowRadius);
        Region = new Region(path);
        Invalidate(true);
    }

    protected override CreateParams CreateParams
    {
        get
        {
            var cp = base.CreateParams;
            if (FormBorderStyle == FormBorderStyle.None) cp.ClassStyle |= 0x20000;
            return cp;
        }
    }

    protected override void WndProc(ref Message m)
    {
        const int WM_NCHITTEST = 0x84;
        const int grip = 8;
        if (FormBorderStyle == FormBorderStyle.None && m.Msg == WM_NCHITTEST && WindowState == FormWindowState.Normal)
        {
            base.WndProc(ref m);
            if ((int)m.Result == 1)
            {
                var p = PointToClient(Cursor.Position);
                bool r = p.X >= ClientSize.Width - grip;
                bool b = p.Y >= ClientSize.Height - grip;
                bool l = p.X < grip;
                bool t = p.Y < grip;
                if (b && r) m.Result = (IntPtr)17;      // HTBOTTOMRIGHT
                else if (b && l) m.Result = (IntPtr)16; // HTBOTTOMLEFT
                else if (t && r) m.Result = (IntPtr)14; // HTTOPRIGHT
                else if (t && l) m.Result = (IntPtr)13; // HTTOPLEFT
                else if (r) m.Result = (IntPtr)11;      // HTRIGHT
                else if (b) m.Result = (IntPtr)15;      // HTBOTTOM
                else if (l) m.Result = (IntPtr)10;      // HTLEFT
            }
            return;
        }
        base.WndProc(ref m);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            ThemeEngine.ThemeChanged -= ApplyTheme;
            _gameRunningTimer?.Dispose();
            _underlineTimer?.Dispose();
        }
        base.Dispose(disposing);
    }
}
