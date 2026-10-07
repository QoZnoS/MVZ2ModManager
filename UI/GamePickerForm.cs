using System.Drawing.Drawing2D;
using MVZ2ModManager.Core;
using MVZ2ModManager.Theme;
using MVZ2ModManager.UI.Controls;

namespace MVZ2ModManager.UI;

/// <summary>
/// 选游戏目录。
///
/// <para>MVZ2 不在任何商店里（也没有注册表项可查），所以这里只有两条路：
/// 认出来的历史路径 / <c>MVZ2_GAME_DIR</c> 环境变量，或者手动挑文件夹。</para>
///
/// <para>挑完之后**必须校验**：目录里得有 <c>MinecraftVSZombies2_Data</c>，
/// 否则提示缺什么 —— 让用户自己找目录的人最容易被"选错一层"绊住。</para>
/// </summary>
internal sealed class GamePickerForm : Form
{
    private const int WindowRadius = 16;
    private const int RowHeight = 56;

    public bool Confirmed { get; private set; }

    private readonly Panel _titleBar;
    private readonly Label _titleLabel;
    private readonly RPanel _content;
    private readonly Label _hint;
    private readonly FlowLayoutPanel _rows;
    private readonly RButton _browseBtn;

    private Point _dragStart;
    private bool _dragging;

    public GamePickerForm()
    {
        Icon = AppIcons.Icon;
        StartPosition = FormStartPosition.CenterScreen;
        DoubleBuffered = true;
        Text = "游戏安装";
        FormBorderStyle = FormBorderStyle.None;
        ClientSize = new Size(520, 430);

        _content = new RPanel
        {
            Dock = DockStyle.Fill,
            Padding = new Padding(24, 10, 24, 16),
            CornerRadius = 14,
            Corners = Corners.BottomLeft | Corners.BottomRight,
        };

        var heading = new Label
        {
            Text = "选择游戏安装",
            Dock = DockStyle.Top,
            Height = 28,
            Font = ThemeEngine.MakeFont(11.5f, FontStyle.Bold),
            TextAlign = ContentAlignment.MiddleLeft,
        };

        _hint = new Label
        {
            Text = "每个 MVZ2 版本都是一套独立安装（各有自己的 MinecraftVSZombies2_Data）。"
                 + "点一行就切过去；还没登记的话用「扫描父目录…」把一整包版本收进来。",
            Dock = DockStyle.Top,
            Height = 46,
            Font = ThemeEngine.MakeFont(8.5f),
            TextAlign = ContentAlignment.TopLeft,
        };
        _hint.Tag = "subtext";

        var bottom = new Panel { Dock = DockStyle.Bottom, Height = 46, Padding = new Padding(0, 8, 0, 0) };
        _browseBtn = new RButton
        {
            Text = "浏览文件夹…",
            AutoSize = true,
            Padding = new Padding(14, 0, 14, 0),
            CornerRadius = 8,
            Tag = "accent",
            Cursor = Cursors.Hand,
            Margin = new Padding(0, 0, 8, 0),
        };
        var scanBtn = new RButton
        {
            Text = "扫描父目录…",
            AutoSize = true,
            Padding = new Padding(14, 0, 14, 0),
            CornerRadius = 8,
            Cursor = Cursors.Hand,
        };
        var cancelBtn = new RButton
        {
            Text = "取消",
            Dock = DockStyle.Right,
            Width = 100,
            CornerRadius = 8,
            Cursor = Cursors.Hand,
        };

        // 两个左对齐按钮放进 FlowLayoutPanel —— 直接给它们都设 Dock=Left 的话，
        // 谁在外侧取决于 z 序，读代码根本看不出来。
        var leftFlow = new FlowLayoutPanel
        {
            Dock = DockStyle.Left,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            FlowDirection = FlowDirection.LeftToRight,
            WrapContents = false,
            Padding = new Padding(0, 4, 0, 0),
            Margin = Padding.Empty,
        };
        leftFlow.Controls.Add(_browseBtn);
        leftFlow.Controls.Add(scanBtn);

        _browseBtn.Click += (_, __) => BrowseForFolder();
        scanBtn.Click += (_, __) => ScanParentFolder();
        cancelBtn.Click += (_, __) => { Confirmed = false; Close(); };

        bottom.Controls.Add(leftFlow);
        bottom.Controls.Add(cancelBtn);

        _rows = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill,
            FlowDirection = FlowDirection.TopDown,
            WrapContents = false,
            AutoScroll = true,
            Padding = Padding.Empty,
        };

        _content.Controls.Add(_rows);
        _content.Controls.Add(bottom);
        _content.Controls.Add(_hint);
        _content.Controls.Add(heading);

        // ---------------- 标题栏 ----------------
        _titleBar = new Panel { Dock = DockStyle.Top, Height = 38 };
        _titleBar.MouseDown += TitleBarMouseDown;
        _titleBar.MouseMove += TitleBarMouseMove;
        _titleBar.MouseUp += (_, __) => _dragging = false;

        var closeDot = MakeCloseDot();
        closeDot.Location = new Point(12, 13);
        closeDot.Cursor = Cursors.Hand;
        closeDot.Click += (_, __) => { Confirmed = false; Close(); };

        _titleLabel = new Label
        {
            Text = "游戏安装",
            Dock = DockStyle.Fill,
            TextAlign = ContentAlignment.MiddleCenter,
            Font = ThemeEngine.MakeFont(10f),
        };
        _titleLabel.MouseDown += TitleBarMouseDown;
        _titleLabel.MouseMove += TitleBarMouseMove;
        _titleLabel.MouseUp += (_, __) => _dragging = false;

        _titleBar.Controls.Add(_titleLabel);
        _titleBar.Controls.Add(closeDot);

        Controls.Add(_content);
        Controls.Add(_titleBar);

        BuildRows();

        ThemeEngine.ThemeChanged += ApplyTheme;
        ApplyTheme();

        Load += (_, __) => ApplyRoundedRegion();
        SizeChanged += (_, __) => ApplyRoundedRegion();
    }

    // ------------------------------------------------------------ 列表

    private void BuildRows()
    {
        _rows.Controls.Clear();

        var installs = AppState.Installations;

        if (installs.Count == 0)
        {
            var note = new Label
            {
                Text = "还没登记过任何游戏目录。\n"
                     + "用「扫描父目录…」选一个装着各版本的文件夹（里面每个 MVZ2 目录都会被收进来），"
                     + "或者用「浏览文件夹…」单独选一套。",
                AutoSize = false,
                Width = Math.Max(240, _rows.ClientSize.Width - 24),
                Height = 64,
                Font = ThemeEngine.MakeFont(8.5f),
                TextAlign = ContentAlignment.MiddleLeft,
            };
            note.Tag = "subtext";
            _rows.Controls.Add(note);
            return;
        }

        // 一行一套安装 —— 同一游戏的不同版本在这里就是一视同仁的"安装"。
        var current = AppState.CurrentInstallation;
        foreach (var inst in installs)
        {
            bool isCurrent = ReferenceEquals(inst, current);
            string subtitle = inst.Directory
                            + (isCurrent ? "　· 当前" : "")
                            + (inst.Exists ? "" : "　· 找不到");

            var target = inst;
            AddRow(target.DisplayName, subtitle, () => Commit(target.ExePath),
                accent: isCurrent, enabled: target.Exists);
        }
    }

    /// <summary>
    /// 让用户指一个"装着各版本的文件夹"，把扫到的安装一次全登记进来。
    /// 例：<c>E:\Game\PVZ\MVZ2</c> 下面是 0.5.0 / 0.6.0 / 0.7.0 test-1 / test-4 … 十几套。
    /// </summary>
    private void ScanParentFolder()
    {
        using var dlg = new FolderBrowserDialog
        {
            Description = "选择装着各版本 MVZ2 的文件夹（会扫描它下面两层）",
            UseDescriptionForTitle = true,
            ShowNewFolderButton = false,
        };

        string start = AppState.GameDir is { } dir ? Path.GetDirectoryName(dir) ?? dir : "";
        if (start.Length > 0 && Directory.Exists(start)) dlg.SelectedPath = start;

        if (dlg.ShowDialog(this) != DialogResult.OK) return;

        var found = AppState.ScanForInstallations(dlg.SelectedPath);
        if (found.Count == 0)
        {
            MessageBox.Show(this,
                "这个文件夹下面没找到 MVZ2 安装。\n\n"
                + "认的是 MinecraftVSZombies2_Data 目录 —— 也正是靠它排除 GameMaker 那些版本。\n\n"
                + dlg.SelectedPath,
                "没找到 MVZ2", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }

        int added = found.Count(exe => AppState.Find(exe) == null && AppState.Remember(exe) != null);
        AppState.Save();
        BuildRows();

        _hint.Text = added == found.Count
            ? $"扫到 {found.Count} 套安装，已全部登记 —— 点一行就切过去。"
            : $"扫到 {found.Count} 套安装，其中新登记 {added} 套 —— 点一行就切过去。";
    }

    private void AddRow(string title, string subtitle, Action onClick, bool accent = false, bool enabled = true)
    {
        int width = Math.Max(200, (_rows.ClientSize.Width > 0 ? _rows.ClientSize.Width : ClientSize.Width - 48) - 24);
        var cursor = enabled ? Cursors.Hand : Cursors.Default;

        var row = new RPanel
        {
            Width = width,
            Height = RowHeight,
            CornerRadius = 8,
            Padding = new Padding(12, 6, 12, 6),
            Margin = new Padding(0, 0, 0, 6),
            Corners = Corners.All,
            Cursor = cursor,
        };
        row.Tag = accent ? "accentrow" : "row";

        var nameLbl = new Label
        {
            Text = title,
            Dock = DockStyle.Top,
            Height = 22,
            Font = ThemeEngine.MakeFont(10f, FontStyle.Bold),
            TextAlign = ContentAlignment.MiddleLeft,
            AutoEllipsis = true,
            Cursor = cursor,
        };
        var pathLbl = new Label
        {
            Text = subtitle,
            Dock = DockStyle.Fill,
            Font = ThemeEngine.MakeFont(8f),
            TextAlign = ContentAlignment.TopLeft,
            AutoEllipsis = true,
            Cursor = cursor,
            Tag = "subtext",
        };

        row.Controls.Add(pathLbl);
        row.Controls.Add(nameLbl);

        if (enabled)
        {
            void Click(object? s, EventArgs e) => onClick();
            row.Click += Click;
            nameLbl.Click += Click;
            pathLbl.Click += Click;
        }

        _rows.Controls.Add(row);
    }

    // ------------------------------------------------------------ 选择

    private void BrowseForFolder()
    {
        using var dlg = new FolderBrowserDialog
        {
            Description = "选择《Minecraft vs Zombies 2》的根目录",
            UseDescriptionForTitle = true,
            ShowNewFolderButton = false,
        };

        string? start = AppState.GameDir ?? Path.GetDirectoryName(AutoDetect() ?? "");
        if (!string.IsNullOrEmpty(start) && Directory.Exists(start)) dlg.SelectedPath = start;

        if (dlg.ShowDialog(this) != DialogResult.OK) return;

        string dir = dlg.SelectedPath;
        string? exe = AppState.FindGameExe(dir);

        if (exe == null)
        {
            // 可能选到了下一层（比如 MinecraftVSZombies2_Data 里），往上找一层。
            string? parent = Path.GetDirectoryName(dir);
            if (parent != null)
            {
                string? parentExe = AppState.FindGameExe(parent);
                if (parentExe != null) { exe = parentExe; dir = parent; }
            }
        }

        if (exe == null)
        {
            MessageBox.Show(this,
                "这个目录里没有 MinecraftVSZombies2.exe：\n" + dir +
                "\n\n请选择游戏根目录（也就是 MinecraftVSZombies2_Data 所在的那一层）。",
                "不是游戏目录", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }

        if (!AppState.IsValidGameDir(dir))
        {
            MessageBox.Show(this,
                "找到了 exe，但这个目录里缺少 MinecraftVSZombies2_Data：\n" + dir +
                "\n\n这看起来不像是一个《Minecraft vs Zombies 2》的安装目录。",
                "不是游戏目录", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }

        Commit(exe);
    }

    private static string? AutoDetect() => AppState.AutoDetectGame();

    private void Commit(string exePath)
    {
        if (!AppState.SwitchTo(exePath))
        {
            MessageBox.Show(this,
                "这套安装现在用不了 —— 主程序或 MinecraftVSZombies2_Data 不见了。",
                "打不开", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            BuildRows();
            return;
        }

        AppState.EnsureDataDir();
        Confirmed = true;
        Close();
    }

    // ------------------------------------------------------------ 外观

    private void TitleBarMouseDown(object? s, MouseEventArgs e)
    {
        if (e.Button == MouseButtons.Left) { _dragging = true; _dragStart = e.Location; }
    }

    private void TitleBarMouseMove(object? s, MouseEventArgs e)
    {
        if (!_dragging || e.Button != MouseButtons.Left) return;
        Location = new Point(Location.X + e.X - _dragStart.X, Location.Y + e.Y - _dragStart.Y);
    }

    private void ApplyRoundedRegion()
    {
        if (ClientSize.Width <= 0 || ClientSize.Height <= 0) return;
        using var path = RoundedGraphics.RoundedRect(new Rectangle(0, 0, ClientSize.Width, ClientSize.Height), WindowRadius);
        Region = new Region(path);
        Invalidate(true);
    }

    private void ApplyTheme()
    {
        var t = ThemeEngine.Current;
        BackColor = t.Background;

        _titleBar.BackColor = t.Surface;
        _titleLabel.BackColor = t.Surface;
        _titleLabel.ForeColor = t.Text;

        _content.BackColor = t.Surface;
        _content.BorderColor = Color.Transparent;

        _hint.ForeColor = t.SubText;

        foreach (Control row in _rows.Controls)
        {
            bool accent = row.Tag is "accentrow";
            row.BackColor = accent ? RoundedGraphics.Lerp(t.Surface, t.Accent, 0.22f) : t.SurfaceAlt;
            foreach (Control child in row.Controls)
                child.BackColor = row.BackColor;
        }

        RecolorDeep(_content, t);
        ThemeEngine.StyleRButton(_browseBtn, accent: true);
        Invalidate(true);
    }

    private static void RecolorDeep(Control c, ThemeColors t)
    {
        foreach (Control child in c.Controls)
        {
            if (child is RButton rb)
            {
                if (rb.Tag is not "accent") ThemeEngine.StyleRButton(rb);
                else ThemeEngine.StyleRButton(rb, accent: true);
            }
            else if (child is Label lbl)
            {
                lbl.ForeColor = lbl.Tag is "subtext" ? t.SubText : t.Text;
                lbl.BackColor = child.Parent?.BackColor ?? t.Surface;
            }
            RecolorDeep(child, t);
        }
    }

    private static Panel MakeCloseDot()
    {
        var p = new Panel { Width = 13, Height = 13, BackColor = Color.Transparent };
        p.Paint += (s, e) =>
        {
            e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
            using var b = new SolidBrush(Color.FromArgb(255, 95, 86));
            e.Graphics.FillEllipse(b, 0, 0, 12, 12);
        };
        return p;
    }

    protected override void WndProc(ref Message m)
    {
        const int WM_NCHITTEST = 0x84;
        if (m.Msg == WM_NCHITTEST)
        {
            base.WndProc(ref m);
            if ((int)m.Result == 1)
            {
                var p = PointToClient(Cursor.Position);
                const int grip = 8;
                bool r = p.X >= ClientSize.Width - grip;
                bool b = p.Y >= ClientSize.Height - grip;
                bool l = p.X < grip;
                bool t = p.Y < grip;
                if (b && r) m.Result = (IntPtr)17;
                else if (b && l) m.Result = (IntPtr)16;
                else if (t && r) m.Result = (IntPtr)14;
                else if (t && l) m.Result = (IntPtr)13;
                else if (r) m.Result = (IntPtr)11;
                else if (b) m.Result = (IntPtr)15;
                else if (l) m.Result = (IntPtr)10;
            }
            return;
        }
        base.WndProc(ref m);
    }

    protected override CreateParams CreateParams
    {
        get
        {
            var cp = base.CreateParams;
            cp.ClassStyle |= 0x20000;
            return cp;
        }
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) ThemeEngine.ThemeChanged -= ApplyTheme;
        base.Dispose(disposing);
    }
}
