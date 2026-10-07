using MVZ2ModManager.Core;
using MVZ2ModManager.Theme;
using MVZ2ModManager.UI.Controls;

namespace MVZ2ModManager.UI.Panels;

/// <summary>
/// **设置**页：游戏目录、总开关、外观、数据目录、关于。
///
/// <para>刻意把上游那些"安装/更新 mod 加载器"的入口全删了 —— 见
/// <see cref="BepInExManager"/> 的说明：这套 BepInEx + interop 是
/// 手工维护的，用通用包去装会把它换坏。</para>
/// </summary>
internal sealed class SettingsPanel : UserControl
{
    private readonly Panel _scroll;
    private readonly FlowLayoutPanel _stack;
    private readonly Label _statusLabel;

    private readonly Label _gamePathLabel;
    private readonly CheckBox _masterSwitch;
    private readonly CheckBox _saveWarnSwitch;
    private readonly RDropdown _themeDropdown;
    private readonly RTextBox _bgBox;
    private readonly RTextBox _accentBox;
    private readonly Label _bgLabel;
    private readonly Label _accentLabel;
    private readonly Label _dataPathLabel;
    private readonly Label _versionLabel;

    private bool _loading;

    private static readonly (string Label, ThemeMode Mode)[] Themes =
    {
        ("深色", ThemeMode.Black),
        ("浅色", ThemeMode.White),
        ("R2Modman", ThemeMode.R2Modman),
        ("自定义", ThemeMode.Custom),
    };

    private const string LinkRepo = "https://github.com/";

    public SettingsPanel()
    {
        Dock = DockStyle.Fill;
        DoubleBuffered = true;

        _stack = new FlowLayoutPanel
        {
            Dock = DockStyle.Top,
            FlowDirection = FlowDirection.TopDown,
            WrapContents = false,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            Padding = new Padding(16, 12, 16, 16),
        };

        _scroll = new Panel { Dock = DockStyle.Fill, AutoScroll = true };
        _scroll.Controls.Add(_stack);

        _statusLabel = new Label
        {
            Dock = DockStyle.Bottom, Height = 22, TextAlign = ContentAlignment.MiddleLeft,
            Padding = new Padding(10, 0, 0, 0),
        };
        _statusLabel.Tag = "subtext";

        // ---------------- Game ----------------
        var game = AddGroup("游戏");

        _gamePathLabel = AddValue(game, "");
        AddRow(game, MakeBtn("更改游戏目录…", ChangeGame), MakeBtn("打开游戏目录", OpenGameFolder));

        _masterSwitch = new CheckBox
        {
            Text = "启用模组（通过 winhttp.dll 注入 BepInEx）",
            AutoSize = true,
            Margin = new Padding(2, 6, 0, 0),
        };
        _masterSwitch.CheckedChanged += (_, __) => OnMasterSwitchChanged();
        AddRow(game, _masterSwitch);
        AddNote(game, "关闭后会把 winhttp.dll 改名 — 任何模组都不会加载。"
                    + "这是唯一安全的「全部禁用」总开关，而且随时可以恢复。");

        // ---------------- Warnings ----------------
        var warnings = AddGroup("提醒");

        _saveWarnSwitch = new CheckBox
        {
            Text = "禁用/卸载模组前，提醒关卡存档失效风险",
            AutoSize = true,
            Margin = new Padding(2, 6, 0, 0),
        };
        _saveWarnSwitch.CheckedChanged += (_, __) => OnSaveWarnChanged();
        AddRow(warnings, _saveWarnSwitch);
        AddNote(warnings, "关卡存档头里记着「需要哪个命名空间、哪个版本」，"
                        + "所以关掉一个内容模组会让用到它的存档读不进去。\n"
                        + "关掉这个提醒后不会再扫描存档，也会跳过禁用前的存档拦截；"
                        + "依赖关系的拦截不受影响。");

        // ---------------- Appearance ----------------
        var appearance = AddGroup("外观");

        _themeDropdown = new RDropdown { Width = 160, Height = 30, CornerRadius = 8 };
        foreach (var (label, _) in Themes) _themeDropdown.Items.Add(label);
        _themeDropdown.SelectedIndexChanged += (_, __) => OnThemeChanged();
        AddRow(appearance, MakeInlineLabel("主题"), _themeDropdown);

        _bgLabel = MakeInlineLabel("背景色");
        _bgBox = new RTextBox { Width = 110, Height = 28, CornerRadius = 6, PlaceholderText = "#141414" };
        HookHexBox(_bgBox, () =>
        {
            AppState.Settings.CustomBackground = _bgBox.Text.Trim();
            ApplyCurrentTheme();
        });
        AddRow(appearance, _bgLabel, _bgBox);

        _accentLabel = MakeInlineLabel("强调色");
        _accentBox = new RTextBox { Width = 110, Height = 28, CornerRadius = 6, PlaceholderText = "#7C3AED" };
        HookHexBox(_accentBox, () =>
        {
            AppState.Settings.CustomAccent = _accentBox.Text.Trim();
            ApplyCurrentTheme();
        });
        AddRow(appearance, _accentLabel, _accentBox);

        // ---------------- Data ----------------
        var data = AddGroup("管理器数据");

        _dataPathLabel = AddValue(data, "");
        AddRow(data,
            MakeBtn("打开数据目录", OpenDataFolder),
            MakeBtn("清空日志归档", ClearLogHistory),
            MakeBtn("重新观看使用引导", ReplayTutorial));

        AddNote(data, "快照点、配置快照和历史日志都放在游戏目录里，"
                    + "所以它们会跟着游戏安装目录一起走。");

        // ---------------- About ----------------
        var about = AddGroup("关于");

        _versionLabel = AddValue(about, $"MVZ2 Mod Manager v{Program.VersionString}");
        AddNote(about,
            "《Minecraft vs Zombies 2》(0.7.x / Windows x86 / IL2CPP / BepInEx 6) 的启动前模组管理器。\n"
            + "启用与禁用的做法是给插件 DLL 改名 — BepInEx 本身没有这个开关。\n\n"
            + "派生自 Sev's Mod Manager（一款很棒的通用 Unity 模组管理器），"
            + "与本项目同为 GPL-3.0 许可。");

        Controls.Add(_scroll);
        Controls.Add(_statusLabel);

        ThemeEngine.ThemeChanged += ApplyTheme;
        ApplyTheme();
        Reload();
    }

    // ------------------------------------------------------------ 数据

    /// <summary>重新读一遍设置并刷新显示。</summary>
    public void Reload()
    {
        _loading = true;
        try
        {
            var dir = AppState.GameDir;
            _gamePathLabel.Text = dir ?? "（未选择游戏）";

            bool hasGame = dir != null;
            _masterSwitch.Enabled = hasGame && BepInExManager.IsInstalled(dir!);
            _masterSwitch.Checked = hasGame && BepInExManager.ModsEnabled(dir!);

            int themeIdx = Array.FindIndex(Themes, t => t.Mode == AppState.Settings.Theme);
            _themeDropdown.SelectedIndex = themeIdx >= 0 ? themeIdx : 0;

            _bgBox.Text = AppState.Settings.CustomBackground;
            _accentBox.Text = AppState.Settings.CustomAccent;

            _dataPathLabel.Text = AppState.DataDir ?? "（未选择游戏）";

            _saveWarnSwitch.Checked = AppState.Settings.WarnAboutSaveRisk;

            bool custom = AppState.Settings.Theme == ThemeMode.Custom;
            _bgLabel.Visible = _bgBox.Visible = custom;
            _accentLabel.Visible = _accentBox.Visible = custom;

            _versionLabel.Text = $"MVZ2 Mod Manager v{Program.VersionString}";

            SetStatus(dir == null ? "未选择游戏。" : (BepInExManager.IsInstalled(dir) ? "就绪。" : "游戏目录里没有找到 BepInEx 文件夹。"));
        }
        finally { _loading = false; }
    }

    private void SetStatus(string msg) => _statusLabel.Text = msg;

    // ------------------------------------------------------------ 交互

    private void ChangeGame()
    {
        using var picker = new GamePickerForm();
        picker.ShowDialog(this);
        if (!picker.Confirmed) return;

        Reload();
        (FindForm() as MainForm)?.UpdateStatusBar();
    }

    private void OpenGameFolder() => OpenInExplorer(AppState.GameDir);

    private void ReplayTutorial() => (FindForm() as MainForm)?.StartTutorial();

    private void OpenDataFolder()
    {
        AppState.EnsureDataDir();
        OpenInExplorer(AppState.DataDir);
    }

    private static void OpenInExplorer(string? path)
    {
        if (string.IsNullOrEmpty(path) || !Directory.Exists(path)) return;
        System.Diagnostics.Process.Start("explorer.exe", path);
    }

    private void OnMasterSwitchChanged()
    {
        if (_loading || AppState.GameDir is not { } dir) return;

        try
        {
            bool nowEnabled = BepInExManager.ToggleMods(dir);
            SetStatus(nowEnabled
                ? "已启用 BepInEx — 下次启动游戏时模组会加载。"
                : "已禁用 BepInEx — 在重新打开之前不会加载任何模组。");
        }
        catch (Exception ex)
        {
            MessageBox.Show("修改失败：" + ex.Message, "错误", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }

        Reload();
        (FindForm() as MainForm)?.UpdateStatusBar();
    }

    /// <summary>自检用：直接拨这个复选框，验证它真的连到了设置上。</summary>
    internal bool SaveWarnChecked
    {
        get => _saveWarnSwitch.Checked;
        set => _saveWarnSwitch.Checked = value;
    }

    private void OnSaveWarnChanged()
    {
        if (_loading) return;

        AppState.Settings.WarnAboutSaveRisk = _saveWarnSwitch.Checked;
        AppState.Save();

        // 立刻让已安装页按新设置刷新（关掉之后顶部警告条上的存档提示要马上消失）。
        (FindForm() as MainForm)?.InstalledPanelControl.Refresh_();

        SetStatus(AppState.Settings.WarnAboutSaveRisk
            ? "已开启存档风险提醒。"
            : "已关闭存档风险提醒 — 之后不再扫描存档，也不再拦截。");
    }

    private void OnThemeChanged()
    {
        if (_loading) return;
        int idx = _themeDropdown.SelectedIndex;
        if (idx < 0 || idx >= Themes.Length) return;

        AppState.Settings.Theme = Themes[idx].Mode;
        AppState.Save();

        bool custom = AppState.Settings.Theme == ThemeMode.Custom;
        _bgLabel.Visible = _bgBox.Visible = custom;
        _accentLabel.Visible = _accentBox.Visible = custom;

        ApplyCurrentTheme();
    }

    private void HookHexBox(RTextBox box, Action onCommit)
    {
        box.Inner.Leave += (_, __) => onCommit();
        box.Inner.KeyDown += (_, e) =>
        {
            if (e.KeyCode != Keys.Enter) return;
            e.SuppressKeyPress = true;
            onCommit();
        };
    }

    private void ApplyCurrentTheme()
    {
        AppState.Save();
        ThemeEngine.Apply(AppState.Settings.Theme, AppState.Settings.CustomBackground, AppState.Settings.CustomAccent);
    }

    private void ClearLogHistory()
    {
        if (LogHistoryManager.HistoryDir is not { } dir || !Directory.Exists(dir))
        {
            SetStatus("还没有历史日志。");
            return;
        }

        int count;
        try { count = Directory.GetFiles(dir, "*.log").Length; }
        catch { count = 0; }

        if (count == 0) { SetStatus("还没有历史日志。"); return; }

        if (MessageBox.Show($"要删除 {count} 个历史日志文件吗？",
                "确认", MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes)
            return;

        try
        {
            Directory.Delete(dir, recursive: true);
            SetStatus($"已删除 {count} 个历史日志。");
        }
        catch (Exception ex) { SetStatus("删除失败：" + ex.Message); }
    }

    // ------------------------------------------------------------ 排版小工具

    private RPanel AddGroup(string title)
    {
        var card = new RPanel
        {
            Width = Math.Max(360, ClientSize.Width - 56),
            CornerRadius = 10,
            Padding = new Padding(14, 10, 14, 12),
            Margin = new Padding(0, 0, 0, 12),
        };
        card.Tag = "surface";

        var header = new Label
        {
            Text = title,
            Dock = DockStyle.Top,
            Height = 24,
            Font = ThemeEngine.MakeFont(10f, FontStyle.Bold),
            TextAlign = ContentAlignment.MiddleLeft,
        };

        var body = new FlowLayoutPanel
        {
            Dock = DockStyle.Top,
            FlowDirection = FlowDirection.TopDown,
            WrapContents = false,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            Padding = Padding.Empty,
            Margin = Padding.Empty,
        };

        card.Controls.Add(body);
        card.Controls.Add(header);

        // 高度跟着内容走：FlowLayoutPanel 自动算高，卡片自己再加 header。
        body.SizeChanged += (_, __) => card.Height = body.Height + header.Height + card.Padding.Vertical;

        _stack.Controls.Add(card);
        _stack.SizeChanged += (_, __) => card.Width = Math.Max(360, _stack.ClientSize.Width - 40);

        return card;
    }

    private static void AddRow(RPanel card, params Control[] controls)
    {
        var body = card.Controls.OfType<FlowLayoutPanel>().FirstOrDefault();
        if (body == null) return;

        var row = new FlowLayoutPanel
        {
            FlowDirection = FlowDirection.LeftToRight,
            WrapContents = false,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            Margin = new Padding(0, 4, 0, 0),
            Padding = Padding.Empty,
        };
        foreach (var c in controls) row.Controls.Add(c);
        body.Controls.Add(row);
    }

    private static Label AddValue(RPanel card, string text)
    {
        var lbl = new Label
        {
            Text = text,
            AutoSize = true,
            Font = ThemeEngine.MakeFont(9f),
            Margin = new Padding(2, 6, 0, 0),
            MaximumSize = new Size(760, 0),
        };
        lbl.Tag = "subtext";
        AddRow(card, lbl);
        return lbl;
    }

    private static void AddNote(RPanel card, string text)
    {
        var lbl = new Label
        {
            Text = text,
            AutoSize = true,
            Font = ThemeEngine.MakeFont(8.5f),
            Margin = new Padding(2, 8, 0, 0),
            MaximumSize = new Size(760, 0),
        };
        lbl.Tag = "subtext";
        AddRow(card, lbl);
    }

    private static Label MakeInlineLabel(string text) => new()
    {
        Text = text,
        AutoSize = false,
        Width = 100,
        Height = 30,
        Font = ThemeEngine.MakeFont(9f),
        TextAlign = ContentAlignment.MiddleLeft,
        Margin = new Padding(2, 0, 6, 0),
    };

    private static RButton MakeBtn(string text, Action onClick)
    {
        var btn = new RButton
        {
            Text = text,
            Style = RButtonStyle.Outline,
            CornerRadius = 8,
            AutoSize = true,
            Padding = new Padding(10, 3, 10, 3),
            Height = 32,
            Margin = new Padding(0, 0, 8, 0),
        };
        btn.Click += (_, __) => onClick();
        return btn;
    }

    // ------------------------------------------------------------ 主题

    private void ApplyTheme()
    {
        var t = ThemeEngine.Current;
        BackColor = t.Background;
        _scroll.BackColor = t.Background;
        _stack.BackColor = t.Background;
        _statusLabel.BackColor = t.Background;
        _statusLabel.ForeColor = t.SubText;

        foreach (var card in _stack.Controls.OfType<RPanel>())
        {
            card.BackColor = t.SurfaceAlt;
            card.BorderColor = Color.Transparent;
        }

        RecolorDeep(_stack, t);

        _themeDropdown.FillColor = t.Surface;
        _themeDropdown.HoverFillColor = t.Border;
        _themeDropdown.BorderColor = Color.Transparent;
        _themeDropdown.ForeColor = t.Text;

        _bgBox.BackColor = t.Surface;
        _bgBox.ForeColor = t.Text;
        _accentBox.BackColor = t.Surface;
        _accentBox.ForeColor = t.Text;

        ThemeEngine.ApplyScrollTheme(this);
        Invalidate(true);
    }

    private static void RecolorDeep(Control parent, ThemeColors t)
    {
        foreach (Control c in parent.Controls)
        {
            switch (c)
            {
                case RButton rb:
                    if (rb.Tag is "accent") ThemeEngine.StyleRButton(rb, accent: true);
                    else ThemeEngine.StyleGhostButton(rb);
                    break;
                case Label lbl:
                    lbl.ForeColor = lbl.Tag is "subtext" ? t.SubText : t.Text;
                    lbl.BackColor = lbl.Parent?.BackColor ?? t.SurfaceAlt;
                    break;
                case CheckBox chk:
                    chk.ForeColor = t.Text;
                    chk.BackColor = chk.Parent?.BackColor ?? t.SurfaceAlt;
                    break;
                case FlowLayoutPanel or RPanel or Panel:
                    c.BackColor = c.Parent?.BackColor ?? t.SurfaceAlt;
                    break;
            }
            RecolorDeep(c, t);
        }
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) ThemeEngine.ThemeChanged -= ApplyTheme;
        base.Dispose(disposing);
    }
}
