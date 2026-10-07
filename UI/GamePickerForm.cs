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
        Text = "选择游戏";
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
            Text = "《Minecraft vs Zombies 2》装在哪里？",
            Dock = DockStyle.Top,
            Height = 28,
            Font = ThemeEngine.MakeFont(11.5f, FontStyle.Bold),
            TextAlign = ContentAlignment.MiddleLeft,
        };

        _hint = new Label
        {
            Text = "请选择游戏根目录（也就是含有 MinecraftVSZombies2.exe "
                 + "和 MinecraftVSZombies2_Data 的那一层）。",
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
            Dock = DockStyle.Left,
            Width = 170,
            CornerRadius = 8,
            Tag = "accent",
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
        _browseBtn.Click += (_, __) => BrowseForFolder();
        cancelBtn.Click += (_, __) => { Confirmed = false; Close(); };

        bottom.Controls.Add(cancelBtn);
        bottom.Controls.Add(_browseBtn);

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
            Text = "选择游戏",
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

        // 1) 认出来的 MVZ2（环境变量 / 历史 / 上次选择）
        string? remembered = null;
        if (AppState.Settings.KnownGamePaths.TryGetValue(AppState.Presets[0].Name, out var known) && File.Exists(known))
            remembered = known;
        remembered ??= AutoDetect();

        if (remembered != null && File.Exists(remembered) && AppState.IsValidGameDir(Path.GetDirectoryName(remembered)))
        {
            var path = remembered;
            AddRow(AppState.Settings.GameName, Path.GetDirectoryName(path)!, () => Commit(path), accent: true);
        }
        else
        {
            var note = new Label
            {
                Text = "没能自动找到《Minecraft vs Zombies 2》的目录。\n"
                     + $"请手动选择，或设置 {AppState.GameDirEnvVar} 环境变量。",
                AutoSize = false,
                Width = _rows.ClientSize.Width - 24,
                Height = 52,
                Font = ThemeEngine.MakeFont(8.5f),
                TextAlign = ContentAlignment.MiddleLeft,
            };
            note.Tag = "subtext";
            _rows.Controls.Add(note);
        }

        // 2) 用户自己加过的其它目录
        foreach (var (name, path) in AppState.Settings.CustomGames)
        {
            if (!File.Exists(path)) continue;
            var p = path;
            AddRow(name, Path.GetDirectoryName(p) ?? p, () => Commit(p));
        }
    }

    private void AddRow(string title, string subtitle, Action onClick, bool accent = false)
    {
        int width = Math.Max(200, (_rows.ClientSize.Width > 0 ? _rows.ClientSize.Width : ClientSize.Width - 48) - 24);

        var row = new RPanel
        {
            Width = width,
            Height = RowHeight,
            CornerRadius = 8,
            Padding = new Padding(12, 6, 12, 6),
            Margin = new Padding(0, 0, 0, 6),
            Corners = Corners.All,
            Cursor = Cursors.Hand,
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
            Cursor = Cursors.Hand,
        };
        var pathLbl = new Label
        {
            Text = subtitle,
            Dock = DockStyle.Fill,
            Font = ThemeEngine.MakeFont(8f),
            TextAlign = ContentAlignment.TopLeft,
            AutoEllipsis = true,
            Cursor = Cursors.Hand,
            Tag = "subtext",
        };

        row.Controls.Add(pathLbl);
        row.Controls.Add(nameLbl);

        void Click(object? s, EventArgs e) => onClick();
        row.Click += Click;
        nameLbl.Click += Click;
        pathLbl.Click += Click;

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
        AppState.Settings.GamePath = exePath;
        AppState.Settings.GameName = AppState.Presets[0].Name;
        AppState.Settings.KnownGamePaths[AppState.Presets[0].Name] = exePath;
        AppState.Save();

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
