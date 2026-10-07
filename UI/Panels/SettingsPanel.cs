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
        ("Black", ThemeMode.Black),
        ("White", ThemeMode.White),
        ("R2Modman", ThemeMode.R2Modman),
        ("Custom", ThemeMode.Custom),
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
        var game = AddGroup("Game");

        _gamePathLabel = AddValue(game, "");
        AddRow(game, MakeBtn("Change game folder...", ChangeGame), MakeBtn("Open game folder", OpenGameFolder));

        _masterSwitch = new CheckBox
        {
            Text = "Enable mods (BepInEx injection via winhttp.dll)",
            AutoSize = true,
            Margin = new Padding(2, 6, 0, 0),
        };
        _masterSwitch.CheckedChanged += (_, __) => OnMasterSwitchChanged();
        AddRow(game, _masterSwitch);
        AddNote(game, "Turning this off renames winhttp.dll — nothing loads at all. "
                    + "It's the only safe \"disable everything\" switch, and it's reversible.");

        // ---------------- Appearance ----------------
        var appearance = AddGroup("Appearance");

        _themeDropdown = new RDropdown { Width = 160, Height = 30, CornerRadius = 8 };
        foreach (var (label, _) in Themes) _themeDropdown.Items.Add(label);
        _themeDropdown.SelectedIndexChanged += (_, __) => OnThemeChanged();
        AddRow(appearance, MakeInlineLabel("Theme"), _themeDropdown);

        _bgLabel = MakeInlineLabel("Background");
        _bgBox = new RTextBox { Width = 110, Height = 28, CornerRadius = 6, PlaceholderText = "#141414" };
        HookHexBox(_bgBox, () =>
        {
            AppState.Settings.CustomBackground = _bgBox.Text.Trim();
            ApplyCurrentTheme();
        });
        AddRow(appearance, _bgLabel, _bgBox);

        _accentLabel = MakeInlineLabel("Accent");
        _accentBox = new RTextBox { Width = 110, Height = 28, CornerRadius = 6, PlaceholderText = "#7C3AED" };
        HookHexBox(_accentBox, () =>
        {
            AppState.Settings.CustomAccent = _accentBox.Text.Trim();
            ApplyCurrentTheme();
        });
        AddRow(appearance, _accentLabel, _accentBox);

        // ---------------- Data ----------------
        var data = AddGroup("Mod manager data");

        _dataPathLabel = AddValue(data, "");
        AddRow(data,
            MakeBtn("Open data folder", OpenDataFolder),
            MakeBtn("Clear log history", ClearLogHistory));

        AddNote(data, "Loadouts, config snapshots and archived logs live inside the game folder, "
                    + "so they travel with the installation.");

        // ---------------- About ----------------
        var about = AddGroup("About");

        _versionLabel = AddValue(about, $"MVZ2 Mod Manager v{Program.CurrentVersion}");
        AddNote(about,
            "A pre-launch mod manager for Minecraft vs Zombies 2 (0.7.x, Windows x86 / IL2CPP / BepInEx 6).\n"
            + "Mods are enabled or disabled by renaming plugin DLLs — BepInEx has no switch of its own.\n\n"
            + "Derived from Sev's Mod Manager (a great general-purpose Unity mod manager) and licensed under GPL-3.0, "
            + "same as this project.");

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
            _gamePathLabel.Text = dir ?? "(no game selected)";

            bool hasGame = dir != null;
            _masterSwitch.Enabled = hasGame && BepInExManager.IsInstalled(dir!);
            _masterSwitch.Checked = hasGame && BepInExManager.ModsEnabled(dir!);

            int themeIdx = Array.FindIndex(Themes, t => t.Mode == AppState.Settings.Theme);
            _themeDropdown.SelectedIndex = themeIdx >= 0 ? themeIdx : 0;

            _bgBox.Text = AppState.Settings.CustomBackground;
            _accentBox.Text = AppState.Settings.CustomAccent;

            _dataPathLabel.Text = AppState.DataDir ?? "(no game selected)";

            bool custom = AppState.Settings.Theme == ThemeMode.Custom;
            _bgLabel.Visible = _bgBox.Visible = custom;
            _accentLabel.Visible = _accentBox.Visible = custom;

            _versionLabel.Text = $"MVZ2 Mod Manager v{Program.CurrentVersion}";

            SetStatus(dir == null ? "No game selected." : (BepInExManager.IsInstalled(dir) ? "Ready." : "BepInEx folder not found in the game directory."));
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
                ? "BepInEx enabled — mods will load on the next launch."
                : "BepInEx disabled — nothing will load until you switch it back on.");
        }
        catch (Exception ex)
        {
            MessageBox.Show("Couldn't change it: " + ex.Message, "Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }

        Reload();
        (FindForm() as MainForm)?.UpdateStatusBar();
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
            SetStatus("No archived logs yet.");
            return;
        }

        int count;
        try { count = Directory.GetFiles(dir, "*.log").Length; }
        catch { count = 0; }

        if (count == 0) { SetStatus("No archived logs yet."); return; }

        if (MessageBox.Show($"Delete {count} archived log file{(count == 1 ? "" : "s")}?",
                "Confirm", MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes)
            return;

        try
        {
            Directory.Delete(dir, recursive: true);
            SetStatus($"Deleted {count} archived log{(count == 1 ? "" : "s")}.");
        }
        catch (Exception ex) { SetStatus("Couldn't delete: " + ex.Message); }
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
            Font = new Font("Segoe UI", 10f, FontStyle.Bold),
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
            Font = new Font("Segoe UI", 9f),
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
            Font = new Font("Segoe UI", 8.5f),
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
        Font = new Font("Segoe UI", 9f),
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
