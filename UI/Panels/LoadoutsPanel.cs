using MVZ2ModManager.Theme;
using System.Drawing.Drawing2D;
using MVZ2ModManager.UI.Controls;
using MVZ2ModManager.Core;

namespace MVZ2ModManager.UI.Panels;

internal sealed class LoadoutsPanel : UserControl
{
    private List<ModLoadout> _loadouts = new();
    private ModLoadout? _selected;

    private readonly ListBox  _loadoutList;
    private readonly ListBox  _enabledList, _disabledList, _missingList;
    private readonly RButton  _newBtn, _applyBtn, _deleteBtn, _openFolderBtn, _refreshBtn;
    private readonly Label    _enabledLbl, _disabledLbl, _missingLbl;
    private readonly Label    _statusLabel;
    private readonly Panel    _toolbar;
    private readonly Panel    _bodyWrap;
    private readonly RPanel   _detailPanel;
    private readonly RPanel   _leftPanel;
    private readonly RTextBox _nameBox;
    private readonly Label    _nameLbl;
    private bool _nameChanging;

    public LoadoutsPanel()
    {
        Dock = DockStyle.Fill;
        DoubleBuffered = true;

        _toolbar = new Panel { Dock = DockStyle.Top, Height = 44, Padding = new Padding(8, 0, 8, 6) };
        _newBtn       = MakeBtn("+ 保存当前");
        _refreshBtn   = MakeBtn("↺ 刷新");
        _applyBtn     = MakeBtn("应用");
        _deleteBtn    = MakeBtn("删除");
        _openFolderBtn= MakeBtn("打开目录 ↗");

        _applyBtn.Enabled  = false;
        _deleteBtn.Enabled = false;

        _applyBtn.EnabledChanged  += (_, __) => ApplyTheme();
        _deleteBtn.EnabledChanged += (_, __) => ApplyTheme();

        _newBtn.Click        += (_, __) => DoSave();
        _refreshBtn.Click    += (_, __) => Refresh_();
        _applyBtn.Click      += (_, __) => DoApply();
        _deleteBtn.Click     += (_, __) => DoDelete();
        _openFolderBtn.Click += (_, __) => OpenFolder();

        var leftFlow = new FlowLayoutPanel
        {
            Dock = DockStyle.Left, AutoSize = true, FlowDirection = FlowDirection.LeftToRight,
            WrapContents = false, Padding = Padding.Empty,
        };
        foreach (var b in new[] { _newBtn, _refreshBtn, _applyBtn, _deleteBtn })
        { b.Margin = new Padding(0, 0, 6, 0); leftFlow.Controls.Add(b); }

        var rightFlow = new FlowLayoutPanel
        {
            Dock = DockStyle.Right, AutoSize = true, FlowDirection = FlowDirection.LeftToRight,
            WrapContents = false, Padding = Padding.Empty,
        };
        _openFolderBtn.Margin = new Padding(6, 0, 0, 0);
        rightFlow.Controls.Add(_openFolderBtn);

        _toolbar.Controls.Add(leftFlow);
        _toolbar.Controls.Add(rightFlow);

        _loadoutList = new ListBox { Dock = DockStyle.Fill, BorderStyle = BorderStyle.None, DrawMode = DrawMode.OwnerDrawFixed, ItemHeight = 52, IntegralHeight = false };
        _loadoutList.DrawItem += DrawLoadoutItem;
        _loadoutList.SelectedIndexChanged += (_, __) => ShowDetail();

        _leftPanel = new RPanel { Dock = DockStyle.Left, Width = 220, CornerRadius = 0 };
        _leftPanel.Controls.Add(_loadoutList);

        _detailPanel = new RPanel { Dock = DockStyle.Fill, Padding = new Padding(12), CornerRadius = 0 };
        _detailPanel.Tag = "surface";

        _nameLbl = new Label
        {
            Text = "名称", AutoSize = false, Height = 26,
            Font = ThemeEngine.MakeFont(8.5f), TextAlign = ContentAlignment.MiddleLeft,
            Dock = DockStyle.None, Width = 60,
        };
        _nameLbl.Tag = "subtext";

        _nameBox = new RTextBox
        {
            PlaceholderText = "自定义名称（可留空）",
            CornerRadius = 6,
            Width = 240, Height = 28,
        };
        _nameBox.Inner.Font = ThemeEngine.MakeFont(9.5f);
        _nameBox.Inner.Leave += (_, __) => SaveName();
        _nameBox.Inner.KeyDown += (_, e) => { if (e.KeyCode == Keys.Enter) { SaveName(); e.SuppressKeyPress = true; } };

        var nameRow = new Panel { Dock = DockStyle.Top, Height = 38, Padding = new Padding(0, 4, 0, 4) };
        _nameLbl.Location = new Point(0, 7);
        _nameBox.Location = new Point(64, 4);
        nameRow.Controls.Add(_nameLbl);
        nameRow.Controls.Add(_nameBox);

        _enabledLbl  = MakeLabel("已启用");
        _disabledLbl = MakeLabel("已禁用");
        _missingLbl  = MakeLabel("缺失");

        _enabledList  = MakeSubList();
        _disabledList = MakeSubList();
        _missingList  = MakeSubList();

        var layout = new TableLayoutPanel
        {
            Dock = DockStyle.Fill, ColumnCount = 3, RowCount = 2,
            Padding = Padding.Empty,
        };
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 33.3f));
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 33.3f));
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 33.3f));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        layout.Controls.Add(_enabledLbl,  0, 0);
        layout.Controls.Add(_disabledLbl, 1, 0);
        layout.Controls.Add(_missingLbl,  2, 0);
        layout.Controls.Add(_enabledList, 0, 1);
        layout.Controls.Add(_disabledList,1, 1);
        layout.Controls.Add(_missingList, 2, 1);

        _detailPanel.Controls.Add(layout);
        _detailPanel.Controls.Add(nameRow);

        _statusLabel = new Label { Dock = DockStyle.Bottom, Height = 22, Padding = new Padding(8, 0, 0, 0), TextAlign = ContentAlignment.MiddleLeft };

        var splitter = new Splitter { Dock = DockStyle.Left, Width = 1 };

        _bodyWrap = new Panel { Dock = DockStyle.Fill, Padding = Padding.Empty };
        _bodyWrap.Controls.Add(_detailPanel);
        _bodyWrap.Controls.Add(splitter);
        _bodyWrap.Controls.Add(_leftPanel);

        Controls.Add(_bodyWrap);
        Controls.Add(_toolbar);
        Controls.Add(_statusLabel);

        ThemeEngine.ThemeChanged += ApplyTheme;
        HandleCreated += (_, __) => ThemeEngine.ApplyScrollTheme(this);
        ApplyTheme();
        Refresh_();
    }

    public void Refresh_()
    {
        if (AppState.GameDir == null) { _statusLabel.Text = "未选择游戏。"; return; }
        AppState.EnsureDataDir();
        _loadouts = DataBridge.LoadLoadouts();
        _loadoutList.BeginUpdate();
        _loadoutList.Items.Clear();
        foreach (var l in _loadouts.OrderBy(l => l.Number)) _loadoutList.Items.Add(l);
        _loadoutList.EndUpdate();
        _statusLabel.Text = $"{_loadouts.Count} 个快照点";

        if (_selected != null && !_loadouts.Any(l => l.Number == _selected.Number))
            ShowDetail(null);
    }

    private void ShowDetail(ModLoadout? forced = null)
    {
        _selected = forced ?? (_loadoutList.SelectedItem as ModLoadout);
        _applyBtn.Enabled  = _selected != null;
        _deleteBtn.Enabled = _selected != null;

        if (_selected == null)
        {
            _enabledList.Items.Clear();
            _disabledList.Items.Clear();
            _missingList.Items.Clear();
            _enabledLbl.Text  = "已启用";
            _disabledLbl.Text = "已禁用";
            _missingLbl.Text  = "缺失";
            _nameChanging = true;
            _nameBox.Text = "";
            _nameChanging = false;
            _nameBox.Enabled = false;
            return;
        }

        _nameChanging = true;
        _nameBox.Text = _selected.Name;
        _nameChanging = false;
        _nameBox.Enabled = true;

        _enabledList.Items.Clear();
        _disabledList.Items.Clear();
        _missingList.Items.Clear();
        foreach (var m in _selected.EnabledMods)  _enabledList.Items.Add(m);
        foreach (var m in _selected.DisabledMods) _disabledList.Items.Add(m);
        foreach (var m in _selected.MissingMods)  _missingList.Items.Add(m);

        _enabledLbl.Text  = $"已启用 ({_selected.EnabledMods.Count})";
        _disabledLbl.Text = $"已禁用 ({_selected.DisabledMods.Count})";
        _missingLbl.Text  = $"缺失 ({_selected.MissingMods.Count})";
    }

    private void SaveName()
    {
        if (_nameChanging || _selected == null) return;
        string newName = _nameBox.Text.Trim();
        if (newName == _selected.Name) return;
        _selected.Name = newName;
        int idx = _loadouts.FindIndex(l => l.Number == _selected.Number);
        if (idx >= 0) _loadouts[idx].Name = newName;
        DataBridge.SaveLoadouts(_loadouts);
        _loadoutList.Invalidate();
    }

    private void DoSave()
    {
        if (AppState.GameDir == null) { MessageBox.Show("还没有设置游戏目录。", "错误"); return; }
        if (_loadouts.Count >= 20)   { MessageBox.Show("快照点上限是 20 个，请先删掉一个。", "已达上限"); return; }

        string? plugins = AppState.ModsInstallDir;
        if (plugins == null) { MessageBox.Show("还没有设置游戏目录。", "错误"); return; }

        var enabled  = Directory.Exists(plugins) ? Directory.GetFiles(plugins, "*.dll").Select(f => Path.GetFileNameWithoutExtension(f)).ToList() : new();
        var disabled = Directory.Exists(plugins) ? Directory.GetFiles(plugins, "*.dll.disabled").Select(f => Path.GetFileNameWithoutExtension(f).Replace(".dll", "")).ToList() : new();
        int next = _loadouts.Count == 0 ? 1 : _loadouts.Max(l => l.Number) + 1;

        _loadouts.Add(new ModLoadout { Number = next, EnabledMods = enabled, DisabledMods = disabled });
        DataBridge.SaveLoadouts(_loadouts);
        SaveConfigSnapshot(next);
        Refresh_();
        _statusLabel.Text = $"已保存为快照点 {next}。";
    }

    private void DoApply()
    {
        if (_selected == null) return;
        string? plugins = AppState.ModsInstallDir;
        if (plugins == null) { MessageBox.Show("还没有设置游戏目录。", "错误"); return; }

        var updated = new ModLoadout
        {
            Number      = _selected.Number,
            Name        = _selected.Name,
            EnabledMods = new(_selected.EnabledMods),
            DisabledMods= new(_selected.DisabledMods),
            MissingMods = new(_selected.MissingMods),
        };

        foreach (var name in _selected.EnabledMods)
        {
            string dll = Path.Combine(plugins, name + ".dll");
            string dis = dll + ".disabled";
            if (File.Exists(dis)) { try { if (File.Exists(dll)) File.Delete(dll); File.Move(dis, dll); } catch { } }
            else if (!File.Exists(dll)) { updated.MissingMods.Add(name); updated.EnabledMods.Remove(name); }
        }

        foreach (var name in _selected.DisabledMods)
        {
            string dll = Path.Combine(plugins, name + ".dll");
            string dis = dll + ".disabled";
            if (File.Exists(dll)) { try { if (File.Exists(dis)) File.Delete(dis); File.Move(dll, dis); } catch { } }
            else if (!File.Exists(dis)) { updated.MissingMods.Add(name); updated.DisabledMods.Remove(name); }
        }

        int idx = _loadouts.FindIndex(l => l.Number == _selected.Number);
        if (idx >= 0) _loadouts[idx] = updated;
        DataBridge.SaveLoadouts(_loadouts);
        RestoreConfigSnapshot(_selected.Number);
        _statusLabel.Text = $"已应用「{_selected.DisplayName}」。";
    }

    private void DoDelete()
    {
        if (_selected == null) return;
        if (MessageBox.Show($"要删除「{_selected.DisplayName}」吗？", "确认", MessageBoxButtons.YesNo) != DialogResult.Yes) return;
        _loadouts.RemoveAll(l => l.Number == _selected.Number);
        DataBridge.SaveLoadouts(_loadouts);
        DeleteConfigSnapshot(_selected.Number);
        _selected = null;
        Refresh_();
        ShowDetail(null);
    }

    private static string? ConfigSnapshotDir(int number) => DataBridge.ConfigSnapshotDir(number);

    /// <summary>
    /// 快照整份 <c>BepInEx/config</c>。快照点不只记"哪些模组开着"，
    /// 也记"它们当时是怎么配的" —— 否则切回去之后行为还是不一样。
    /// </summary>
    private static void SaveConfigSnapshot(int number)
    {
        if (ConfigSnapshotDir(number) is not { } dir) return;
        if (AppState.ConfigDir is not { } configDir || !Directory.Exists(configDir)) return;

        if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true);
        CopyDirectory(configDir, dir);
    }

    private static void RestoreConfigSnapshot(int number)
    {
        if (ConfigSnapshotDir(number) is not { } dir || !Directory.Exists(dir)) return;
        if (AppState.ConfigDir is not { } configDir) return;

        Directory.CreateDirectory(configDir);
        CopyDirectory(dir, configDir);
    }

    private static void DeleteConfigSnapshot(int number)
    {
        if (ConfigSnapshotDir(number) is { } dir && Directory.Exists(dir))
            Directory.Delete(dir, recursive: true);
    }

    private static void CopyDirectory(string source, string dest)
    {
        foreach (var file in Directory.GetFiles(source, "*", SearchOption.AllDirectories))
        {
            string rel = Path.GetRelativePath(source, file);
            string target = Path.Combine(dest, rel);
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.Copy(file, target, overwrite: true);
        }
    }

    private void OpenFolder()
    {
        if (AppState.DataDir is not { } dataDir) return;
        string path = Path.Combine(dataDir, "loadouts");
        if (!Directory.Exists(path)) Directory.CreateDirectory(path);
        System.Diagnostics.Process.Start("explorer.exe", path);
    }

    private void DrawLoadoutItem(object? sender, DrawItemEventArgs e)
    {
        if (e.Index < 0 || e.Index >= _loadoutList.Items.Count) return;
        var l = (ModLoadout)_loadoutList.Items[e.Index]!;
        bool sel = (e.State & DrawItemState.Selected) != 0;
        var t = ThemeEngine.Current;

        e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
        e.Graphics.FillRectangle(new SolidBrush(_loadoutList.BackColor), e.Bounds);

        var card = new Rectangle(e.Bounds.Left + 6, e.Bounds.Top + 3, e.Bounds.Width - 12, e.Bounds.Height - 6);
        using (var path = RoundedGraphics.RoundedRect(card, 8))
        using (var brush = new SolidBrush(sel ? t.Highlight : t.Surface))
            e.Graphics.FillPath(brush, path);

        using var titleFont = ThemeEngine.MakeFont(10f, FontStyle.Bold);
        using var subFont = ThemeEngine.MakeFont(8f);
        CardText.Draw(e.Graphics, card, 10, titleFont, l.DisplayName, t.Text, subFont,
            $"{l.EnabledMods.Count} 启用 · {l.DisabledMods.Count} 禁用 · {l.MissingMods.Count} 缺失", t.SubText);
    }

    private void ApplyTheme()
    {
        var t = ThemeEngine.Current;
        BackColor = t.Background;
        _toolbar.BackColor = t.Surface;
        _bodyWrap.BackColor = t.Background;
        _loadoutList.BackColor = t.Background;
        _loadoutList.ForeColor = t.Text;
        _leftPanel.BackColor = t.SurfaceAlt;
        _leftPanel.BorderColor = Color.Transparent;
        _detailPanel.BackColor = t.SurfaceAlt;
        _detailPanel.BorderColor = Color.Transparent;
        _enabledList.BackColor  = t.Background;
        _disabledList.BackColor = t.Background;
        _missingList.BackColor  = t.Background;
        _enabledList.ForeColor  = t.Text;
        _disabledList.ForeColor = t.Text;
        _missingList.ForeColor  = t.Text;
        _enabledLbl.BackColor   = t.SurfaceAlt;
        _enabledLbl.ForeColor   = t.SubText;
        _disabledLbl.BackColor  = t.SurfaceAlt;
        _disabledLbl.ForeColor  = t.SubText;
        _missingLbl.BackColor   = t.SurfaceAlt;
        _missingLbl.ForeColor   = t.SubText;
        _nameLbl.BackColor      = t.SurfaceAlt;
        _nameLbl.ForeColor      = t.SubText;
        _nameBox.BackColor      = t.Highlight;
        _nameBox.ForeColor      = t.Text;
        _statusLabel.BackColor  = t.Background;
        _statusLabel.ForeColor  = t.SubText;
        foreach (var b in new[] { _newBtn, _refreshBtn, _openFolderBtn })
            ThemeEngine.StyleGhostButton(b);

        if (_applyBtn.Enabled) ThemeEngine.StyleRButton(_applyBtn, accent: true);
        else ThemeEngine.StyleGhostButton(_applyBtn);
        ThemeEngine.StyleGhostButton(_deleteBtn);
        if (!_deleteBtn.Enabled) _deleteBtn.ForeColor = t.SubText;
        _loadoutList.Invalidate();
        ThemeEngine.ApplyScrollTheme(this);
    }

    private static ListBox MakeSubList() => new()
    {
        Dock = DockStyle.Fill, BorderStyle = BorderStyle.None,
        SelectionMode = SelectionMode.None,
    };

    private static Label MakeLabel(string text) => new()
    {
        Text = text, AutoSize = false, Dock = DockStyle.Top, Height = 20,
        Font = ThemeEngine.MakeFont(8.5f, FontStyle.Regular),
        Padding = new Padding(2, 2, 0, 2),
    };

    private static RButton MakeBtn(string text) => new()
    {
        Text = text, Style = RButtonStyle.Outline, CornerRadius = 8,
        RoundedCorners = Corners.BottomLeft | Corners.BottomRight,
        AutoSize = true, Padding = new Padding(8, 2, 8, 2), Height = 38,
    };

    protected override void Dispose(bool disposing)
    {
        if (disposing) ThemeEngine.ThemeChanged -= ApplyTheme;
        base.Dispose(disposing);
    }
}
