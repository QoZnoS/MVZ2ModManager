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

    private static readonly string[] NavLabels = { "已安装", "快照点", "模组包", "配置", "日志", "设置" };

    private const int IndexInstalled = 0;
    private const int IndexModpacks = 2;
    private const int IndexLogs = 4;
    private const int IndexSettings = 5;

    // ---- 标题栏 ----
    private readonly Panel _titleBar;
    private readonly Label _titleLabel;
    private readonly Label _gameLabel;
    private readonly Panel _titleIconBox;
    private readonly Panel _titleGameIcon;

    /// <summary>标题栏那个游戏名上的悬浮提示（里面是完整路径）。</summary>
    private readonly ToolTip _tip = new();

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
    // 换游戏安装时整套**重建**（见 RebuildPanels），所以这里不能是 readonly。
    private InstalledPanel _installedPanel = null!;
    private LoadoutsPanel _loadoutsPanel = null!;
    private ModpacksPanel _modpacksPanel = null!;
    private ConfigPanel _configPanel = null!;
    private LogsPanel _logsPanel = null!;
    private SettingsPanel _settingsPanel = null!;
    private Control[] _navPanels = Array.Empty<Control>();
    private readonly Action?[] _navActivate;

    /// <summary>装面板的容器。重建面板时要往里加/取，所以留着引用。</summary>
    private readonly Panel _contentHost;

    private readonly string? _pendingPackPath;
    private TutorialOverlay? _tutorial;

    // ---- 状态栏 ----
    private readonly Panel _statusBar;
    private readonly Label _statusLabel;
    private readonly Panel _resizeHandle;

    private Point _dragStart;
    private bool _dragging;

    public MainForm(string? pendingPackPath = null)
    {
        _pendingPackPath = pendingPackPath;

        Icon = AppIcons.Icon;
        StartPosition = FormStartPosition.CenterScreen;
        Size = new Size(1100, 720);
        MinimumSize = new Size(880, 560);
        DoubleBuffered = true;
        Text = "MVZ2 Mod Manager";
        FormBorderStyle = FormBorderStyle.None;

        _navActivate = new Action?[]
        {
            () => _installedPanel.Refresh_(),
            () => _loadoutsPanel.Refresh_(),
            () => _modpacksPanel.Refresh_(),
            () => _configPanel.Refresh_(),
            () => _logsPanel.Refresh_(),
            () => _settingsPanel.Reload(),
        };

        _contentHost = new Panel { Dock = DockStyle.Fill };
        // 面板构造时各自会去读 AppState 里当前的安装路径 —— 所以"换安装"就是"重建面板"。
        CreatePanels();

        _statusBar = new Panel { Dock = DockStyle.Bottom, Height = 24, Padding = new Padding(8, 0, 8, 0) };
        _statusLabel = new Label
        {
            Dock = DockStyle.Fill, AutoSize = false,
            TextAlign = ContentAlignment.MiddleLeft,
            Font = ThemeEngine.MakeFont(8f),
        };
        _resizeHandle = MakeResizeHandle();
        _statusBar.Controls.Add(_statusLabel);
        _statusBar.Controls.Add(_resizeHandle);

        Controls.Add(_contentHost);
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
            Font = ThemeEngine.MakeFont(10f),
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
            Font = ThemeEngine.MakeFont(8.5f),
            Padding = new Padding(0, 0, 8, 0),
            Cursor = Cursors.Hand,
            AutoEllipsis = true,
        };
        _gameLabel.Click += (_, __) => ShowInstallMenu();
        _titleIconBox.Cursor = Cursors.Hand;
        _titleIconBox.Click += (_, __) => ShowInstallMenu();

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

            // 双击 .mvz2pack 启动时：直接落到模组包页并问要不要还原。
            if (_pendingPackPath is { Length: > 0 } pack && File.Exists(pack))
            {
                SwitchToIndex(IndexModpacks);
                _modpacksPanel.ImportAndPrompt(pack);
                return;
            }

            if (!AppState.Settings.HasSeenTutorial) StartTutorial();
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

    /// <summary>标签页名字。自检直接用它，避免自检里另抄一份、抄错了还当成通过。</summary>
    internal static string[] TabLabels => NavLabels;

    /// <summary>当前选中的标签页序号。</summary>
    internal int ActiveTabIndex => _activeNavIndex;

    internal bool GameIsRunning => _gameRunning;

    /// <summary>打开（或重开）首次使用引导。</summary>
    internal void StartTutorial()
    {
        _tutorial?.Dispose();
        _tutorial = new TutorialOverlay(this);
        _tutorial.Start();
    }

    /// <summary>自检用：把引导的每一步渲染一遍（会真的切标签页）然后收摊。</summary>
    internal void StopTutorialForTest()
    {
        _tutorial?.StepThroughAllForTest();
        _tutorial = null;
    }

    // ------------------------------------------------------------ 面板

    /// <summary>
    /// 造一套面板并挂到 <see cref="_contentHost"/> 上。
    /// 每个面板在构造时都会自己去读 <see cref="AppState"/> 里当前的安装路径，
    /// 所以"换安装"就等于"重建一整套"。
    /// </summary>
    private void CreatePanels()
    {
        _installedPanel = new InstalledPanel();
        _loadoutsPanel = new LoadoutsPanel();
        _modpacksPanel = new ModpacksPanel();
        _configPanel = new ConfigPanel();
        _logsPanel = new LogsPanel();
        _settingsPanel = new SettingsPanel();

        _navPanels = new Control[]
        {
            _installedPanel, _loadoutsPanel, _modpacksPanel, _configPanel, _logsPanel, _settingsPanel,
        };

        // Controls[0] 在最前；虽然 SwitchToIndex 里还会 BringToFront，但初始顺序也保持自然顺序。
        int active = Math.Clamp(_activeNavIndex, 0, _navPanels.Length - 1);
        for (int i = 0; i < _navPanels.Length; i++)
        {
            _navPanels[i].Visible = i == active;
            _contentHost.Controls.Add(_navPanels[i]);
        }
    }

    /// <summary>
    /// 换了游戏安装之后把面板**整套重建**。
    ///
    /// <para>刻意不做"逐个刷新"：面板各自还有内部缓存（存档扫描、日志路径、列宽…），
    /// 漏掉任何一处都会让界面继续显示上一套安装的数据 ——
    /// 最坏的结果是把模组装进错误的版本，所以这里宁可推倒重来。
    /// 代价是几十毫秒，换来的是"不可能不一致"。</para>
    /// </summary>
    internal void RebuildPanels()
    {
        int idx = _activeNavIndex;
        var old = _navPanels;

        foreach (var p in old) _contentHost.Controls.Remove(p);

        CreatePanels();
        SwitchToIndex(idx);

        foreach (var p in old) p.Dispose();

        // 存档扫描有 30 秒缓存，换安装后必须作废（否则拿着上一套的结论）。
        SaveCompatibility.InvalidateCache();

        UpdateGameLabel();
        CheckGameRunning();   // 顺手重算"这套安装的游戏在不在跑"
    }

    // ------------------------------------------------------------ 状态栏

    /// <summary>状态栏：模组计数 + 总开关状态 + 游戏是否在跑（DLL 会被锁）。</summary>
    internal void UpdateStatusBar()
    {
        var gameDir = AppState.GameDir;
        if (gameDir == null) { _statusLabel.Text = "未选择游戏。"; return; }

        var mods = ModInstaller.GetInstalled();
        int enabled = mods.Count(m => m.Enabled);
        int disabled = mods.Count - enabled;

        var parts = new List<string>
        {
            $"{enabled} 已启用",
            $"{disabled} 已禁用",
        };

        if (BepInExManager.Describe(BepInExManager.GetState(gameDir)) is { } bepInExNote)
            parts.Add(bepInExNote);

        if (_gameRunning)
            parts.Add("⚠ 游戏正在运行 — 插件 DLL 被占用");

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
            b.Font = ThemeEngine.MakeFont(9.5f, active ? FontStyle.Bold : FontStyle.Regular);
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
        var inst = AppState.CurrentInstallation;

        // 标题栏位置窄，只显示短名（别名或目录名）；完整路径挂在悬浮提示上。
        _gameLabel.Text = inst is { } i ? i.DisplayName
                        : AppState.Settings.GamePath.Length > 0 ? AppState.Settings.GamePath
                        : "未选择游戏";

        _tip.SetToolTip(_gameLabel, inst is { } t
            ? t.ExePath + "\n\n点这里切换/管理已登记的游戏安装。"
            : "点这里选择游戏目录。");

        bool hasGame = AppState.Settings.GamePath.Length > 0;
        _titleIconBox.Visible = hasGame;
        _titleGameIcon.Tag = hasGame ? AppIcons.TryExtractGameIcon(AppState.Settings.GamePath) ?? AppIcons.Png : null;
        _titleGameIcon.Invalidate();
    }

    // ------------------------------------------------------------ 安装（版本）切换

    /// <summary>
    /// 标题栏那个游戏名点下去弹的菜单：已登记的安装挨个列出来，打勾的是当前这套。
    /// 做成菜单是因为"换版本"是要来回切的动作，每次都弹一个对话框太重。
    /// </summary>
    private void ShowInstallMenu()
    {
        var menu = BuildInstallMenu();
        menu.Show(_gameLabel, new Point(Math.Max(0, _gameLabel.Width - menu.Width), _gameLabel.Height));
    }

    /// <summary>菜单内容单独一个方法，这样自检能直接构造它并核对条目（不用真弹出来）。</summary>
    internal ContextMenuStrip BuildInstallMenu()
    {
        var menu = new ContextMenuStrip { ShowImageMargin = false };

        menu.Items.Add(new ToolStripMenuItem("游戏安装") { Enabled = false });
        menu.Items.Add(new ToolStripSeparator());

        string current = AppState.Settings.GamePath;
        foreach (var inst in AppState.Installations)
        {
            var item = new ToolStripMenuItem(InstallMenuText(inst))
            {
                Checked = AppState.Find(current) == inst,
            };
            if (!inst.Exists)
            {
                item.Enabled = false;
                item.ToolTipText = "这个目录已经找不到了";
            }
            else
            {
                item.ToolTipText = inst.ExePath;
                var target = inst.ExePath;
                item.Click += (_, __) => SwitchInstallation(target);
            }
            menu.Items.Add(item);
        }

        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(new ToolStripMenuItem("添加 / 管理安装…", null, (_, __) => SwitchGame()));

        // 条目加完再上色（StyleMenu 只给"已经存在"的条目逐个上色）。
        ThemeEngine.StyleMenu(menu);

        return menu;
    }

    private static string InstallMenuText(GameInstallation inst)
    {
        string name = inst.DisplayName;
        // 两套安装的目录名可能一样（比如从压缩包解出来的两份），加上父目录名区分。
        string parent = Path.GetFileName(Path.GetDirectoryName(inst.Directory) ?? "");
        if (parent.Length > 0 && AppState.Installations.Count(i => i.DisplayName == name) > 1)
            name = parent + "\\" + name;

        return inst.Exists ? name : name + "（找不到）";
    }

    /// <summary>切换当前安装。只改设置、不动文件，所以随时能换回来。</summary>
    private void SwitchInstallation(string exePath)
    {
        if (string.Equals(exePath, AppState.Settings.GamePath, StringComparison.OrdinalIgnoreCase)) return;

        if (!AppState.SwitchTo(exePath))
        {
            MessageBox.Show(this,
                "这套安装现在不能用 —— 里面的 MinecraftVSZombies2_Data 或主程序不见了。",
                "打不开", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }

        RebuildPanels();
    }

    private void SwitchGame()
    {
        // 不再要求"先退出游戏"：进程是按完整路径认的，换到另一套安装既不会误判、
        // 也不会误杀正在跑的那个版本，而切换本身只是改一个设置项。
        using var picker = new GamePickerForm();
        picker.ShowDialog(this);
        if (!picker.Confirmed) return;

        RebuildPanels();
    }

    // ------------------------------------------------------------ 启动 / 停止

    private RButton MakePlayBtn()
    {
        var btn = new RButton
        {
            Text = "▶ 启动游戏", CornerRadius = 8,
            Font = ThemeEngine.MakeFont(9f, FontStyle.Bold),
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

    /// <summary>启动前的确认弹窗。决策逻辑在 <see cref="GameLauncher.ShouldStart"/> 里，
    /// 这里只管"怎么问"。</summary>
    private bool AskBeforeLaunch((string Text, string Caption, bool Warning) prompt) =>
        MessageBox.Show(this, prompt.Text, prompt.Caption, MessageBoxButtons.YesNo,
            prompt.Warning ? MessageBoxIcon.Warning : MessageBoxIcon.Information) == DialogResult.Yes;

    private void LaunchGame()
    {
        string path = AppState.Settings.GamePath;
        if (string.IsNullOrEmpty(path) || !File.Exists(path)) return;

        var gameDir = AppState.GameDir;
        if (gameDir == null) return;

        // 模组没开就先说清楚这次启动是什么状态。
        //
        // 注意这里**绝不替用户改总开关**：以前那版把"要不要重新打开 BepInEx"
        // 和"要不要启动游戏"问成一句话，答"否"之后两件事都没发生 ——
        // 于是"用管理器启动一次原版游戏"这条唯一的路被自己的弹窗堵死了。
        // 现在问的是"要不要在不启用模组的情况下开始游戏"，答"是"就照原样启动。
        if (!GameLauncher.ShouldStart(BepInExManager.GetState(gameDir), AskBeforeLaunch))
            return;

        try
        {
            GameLauncher.Start(path);

            _launchedAt = DateTime.UtcNow;
            _starting = true;
            UpdatePlayButton();

            // 启动会自动跳到日志页：BepInEx 从第一行就开始写，这里正好当加载看板。
            SwitchToIndex(IndexLogs);
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, "启动游戏失败：" + ex.Message, "错误",
                MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }

    private void StopGame()
    {
        // 只结束**当前这套安装**的进程：所有版本的 exe 同名，
        // 按名字杀会连带弄掉你正在玩的另一个版本。
        foreach (var p in AppState.RunningGameProcesses())
        {
            try { p.Kill(); }
            catch { }
            finally { p.Dispose(); }
        }
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
                "游戏启动后几秒就退出了 — 很可能崩溃了。\n\n要打开日志看看吗？",
                "游戏很快退出", MessageBoxButtons.YesNo, MessageBoxIcon.Warning);
            if (check == DialogResult.Yes) SwitchToIndex(IndexLogs);
        }
        _launchedAt = null;

        _gameRunning = running;
        UpdatePlayButton();
        UpdateStatusBar();
    }

    /// <summary>当前这套安装的游戏在不在跑。按**完整路径**匹配，见 <see cref="AppState.RunningGameProcesses"/>。</summary>
    private static bool IsGameProcessRunning() => AppState.IsGameRunning();

    private void UpdatePlayButton()
    {
        var t = ThemeEngine.Current;
        Color tint = _starting ? Color.FromArgb(52, 120, 246)
                   : _gameRunning ? Color.FromArgb(220, 60, 60)
                   : Color.FromArgb(60, 190, 100);

        _playBtn.Text = _starting ? "启动中…" : _gameRunning ? "■ 停止" : "▶ 启动游戏";
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
        if (AppState.GameDir == null) { SetStatus("未选择游戏，无法安装。"); return; }

        if (_gameRunning)
        {
            MessageBox.Show(this, "请先关闭游戏 — 游戏运行时插件 DLL 会被占用。",
                "游戏正在运行", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }

        int count = 0;
        foreach (var f in files.Where(f => f.EndsWith(".dll", StringComparison.OrdinalIgnoreCase)))
        {
            try { ModInstaller.InstallLocal(f); count++; } catch { }
        }

        if (count == 0) return;

        SetStatus($"已安装 {count} 个拖入的 .dll 文件。");
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
        Font = ThemeEngine.MakeFont(9.5f), Cursor = Cursors.Hand,
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
            _tutorial?.Dispose();
        }
        base.Dispose(disposing);
    }
}
