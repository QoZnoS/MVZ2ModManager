using System.Drawing.Drawing2D;
using MVZ2ModManager.Core;
using MVZ2ModManager.Theme;
using MVZ2ModManager.UI.Controls;

namespace MVZ2ModManager.UI.Panels;

/// <summary>
/// **模组包**页：把"当前这套 setup"存成一份 <c>.mvz2pack</c>，随时一键还原。
///
/// <para>包 = zip（清单 <c>mvz2pack.json</c> + 文件树）。落地目录是自建的
/// <c>%APPDATA%\MVZ2ModManager\Modpacks\mvz2\</c>，和游戏目录解耦，
/// 所以换游戏安装目录也不丢。</para>
///
/// <para>还原时会**先清空这份包覆盖到的根**再解压。哪些根被覆盖由包内路径前缀决定，
/// 所以"只含 plugins 的包"不会顺手清掉资源目录 —— 细节见 <see cref="ModpackManager"/>。</para>
/// </summary>
internal sealed class ModpacksPanel : UserControl
{
    private List<ModpackInfo> _packs = new();
    private ModpackInfo? _selected;
    private bool _busy;

    private readonly Panel _toolbar;
    private readonly Panel _bodyWrap;
    private readonly Panel _leftPanel;
    private readonly Panel _detailPanel;
    private readonly ListBox _packList;
    private readonly ListBox _modList;
    private readonly TreeView _fileTree;
    private readonly Label _detailName;
    private readonly Label _detailMeta;
    private readonly Label _modsLbl;
    private readonly Label _filesLbl;
    private readonly Label _statusLabel;

    private readonly RButton _createBtn, _refreshBtn, _applyBtn, _deleteBtn, _exportBtn, _importBtn, _openFolderBtn;

    public ModpacksPanel()
    {
        Dock = DockStyle.Fill;
        DoubleBuffered = true;

        // ---------------------------------------------------- 工具栏
        _toolbar = new Panel { Dock = DockStyle.Top, Height = 44, Padding = new Padding(8, 0, 8, 6) };

        _createBtn = MakeBtn("＋ 从当前配置创建…");
        _refreshBtn = MakeBtn("↺ 刷新");
        _applyBtn = MakeBtn("应用");
        _deleteBtn = MakeBtn("删除");
        _importBtn = MakeBtn("导入…");
        _exportBtn = MakeBtn("导出…");
        _openFolderBtn = MakeBtn("打开目录 ↗");

        _applyBtn.Enabled = false;
        _deleteBtn.Enabled = false;
        _exportBtn.Enabled = false;

        _createBtn.Click += async (_, __) => await DoCreateAsync();
        _refreshBtn.Click += (_, __) => Refresh_();
        _applyBtn.Click += async (_, __) => await DoApplyAsync();
        _deleteBtn.Click += (_, __) => DoDelete();
        _importBtn.Click += (_, __) => DoImport();
        _exportBtn.Click += (_, __) => DoExport();
        _openFolderBtn.Click += (_, __) => OpenFolder();

        var leftFlow = new FlowLayoutPanel
        {
            Dock = DockStyle.Left, AutoSize = true, FlowDirection = FlowDirection.LeftToRight,
            WrapContents = false, Padding = Padding.Empty,
        };
        foreach (var b in new[] { _createBtn, _refreshBtn, _applyBtn, _deleteBtn })
        {
            b.Margin = new Padding(0, 0, 6, 0);
            leftFlow.Controls.Add(b);
        }

        var rightFlow = new FlowLayoutPanel
        {
            Dock = DockStyle.Right, AutoSize = true, FlowDirection = FlowDirection.LeftToRight,
            WrapContents = false, Padding = Padding.Empty,
        };
        foreach (var b in new[] { _importBtn, _exportBtn, _openFolderBtn })
        {
            b.Margin = new Padding(6, 0, 0, 0);
            rightFlow.Controls.Add(b);
        }

        _toolbar.Controls.Add(leftFlow);
        _toolbar.Controls.Add(rightFlow);

        // ---------------------------------------------------- 左栏：包列表
        _packList = new ListBox
        {
            Dock = DockStyle.Fill, BorderStyle = BorderStyle.None,
            DrawMode = DrawMode.OwnerDrawFixed, ItemHeight = 52, IntegralHeight = false,
        };
        _packList.DrawItem += DrawPackItem;
        _packList.SelectedIndexChanged += (_, __) => ShowDetail();

        var left = new RPanel { Dock = DockStyle.Left, Width = 240, CornerRadius = 0 };
        left.Controls.Add(_packList);
        _leftPanel = left;

        // ---------------------------------------------------- 右栏：详情
        _detailPanel = new Panel { Dock = DockStyle.Fill, Padding = new Padding(12, 8, 12, 8) };

        _detailName = new Label
        {
            Dock = DockStyle.Top, Height = 28,
            Font = ThemeEngine.MakeFont(13f, FontStyle.Bold),
            TextAlign = ContentAlignment.MiddleLeft,
            AutoEllipsis = true,
        };
        _detailMeta = new Label
        {
            Dock = DockStyle.Top, Height = 40,
            Font = ThemeEngine.MakeFont(9f),
            TextAlign = ContentAlignment.TopLeft,
        };
        _detailMeta.Tag = "subtext";

        _modsLbl = MakeCaption("包含的模组");
        _modList = new ListBox
        {
            Dock = DockStyle.Fill, BorderStyle = BorderStyle.None,
            SelectionMode = SelectionMode.None, IntegralHeight = false,
        };

        var modsWrap = new Panel { Dock = DockStyle.Top, Height = 150, Padding = new Padding(0, 4, 0, 8) };
        modsWrap.Controls.Add(_modList);
        modsWrap.Controls.Add(_modsLbl);

        _filesLbl = MakeCaption("包含的文件");
        _fileTree = new TreeView { Dock = DockStyle.Fill, BorderStyle = BorderStyle.None };
        _fileTree.HandleCreated += (_, __) => ThemeEngine.ApplyScrollTheme(_fileTree);

        var filesWrap = new Panel { Dock = DockStyle.Fill, Padding = new Padding(0, 4, 0, 0) };
        filesWrap.Controls.Add(_fileTree);
        filesWrap.Controls.Add(_filesLbl);

        // Dock 顺序：先加的通吃剩余空间，所以倒着加。
        _detailPanel.Controls.Add(filesWrap);
        _detailPanel.Controls.Add(modsWrap);
        _detailPanel.Controls.Add(_detailMeta);
        _detailPanel.Controls.Add(_detailName);

        _bodyWrap = new Panel { Dock = DockStyle.Fill };
        _bodyWrap.Controls.Add(_detailPanel);
        _bodyWrap.Controls.Add(left);

        _statusLabel = new Label
        {
            Dock = DockStyle.Bottom, Height = 22, Padding = new Padding(8, 0, 0, 0),
            TextAlign = ContentAlignment.MiddleLeft,
        };
        _statusLabel.Tag = "subtext";

        Controls.Add(_bodyWrap);
        Controls.Add(_toolbar);
        Controls.Add(_statusLabel);

        AllowDrop = true;
        DragEnter += OnDragEnter;
        DragDrop += OnDragDrop;

        ThemeEngine.ThemeChanged += ApplyTheme;
        HandleCreated += (_, __) => ThemeEngine.ApplyScrollTheme(this);
        ApplyTheme();
        Refresh_();
    }

    // ------------------------------------------------------------ 刷新

    public void Refresh_()
    {
        string? keep = _selected?.FilePath;

        _packs = ModpackManager.GetSavedPacks();

        _packList.BeginUpdate();
        _packList.Items.Clear();
        foreach (var p in _packs) _packList.Items.Add(p);
        _packList.EndUpdate();

        int idx = keep != null ? _packs.FindIndex(p => p.FilePath == keep) : -1;
        _packList.SelectedIndex = idx >= 0 ? idx : (_packs.Count > 0 ? 0 : -1);
        if (_packs.Count == 0) ShowDetail();

        UpdateStatus();
    }

    private void UpdateStatus()
    {
        if (_packs.Count == 0)
        {
            _statusLabel.Text = "还没有模组包。用「从当前配置创建…」把现在这套 setup 存下来。";
            return;
        }

        string match = ModpackManager.CurrentSetupMatchesSavedPack() ? "  ·  当前配置与其中一份包一致" : "";
        _statusLabel.Text = $"{_packs.Count} 份模组包{match}";
    }

    private void ShowDetail()
    {
        _selected = _packList.SelectedItem as ModpackInfo;
        bool has = _selected != null;

        _applyBtn.Enabled = has && !_busy;
        _deleteBtn.Enabled = has && !_busy;
        _exportBtn.Enabled = has && !_busy;

        _detailName.Text = has ? _selected!.DisplayName : "";
        _modList.Items.Clear();
        _fileTree.Nodes.Clear();

        if (!has)
        {
            _detailMeta.Text = "";
            _modsLbl.Text = "包含的模组";
            _filesLbl.Text = "包含的文件";
            return;
        }

        var m = _selected!.Manifest;
        long size = 0;
        try { size = new FileInfo(_selected.FilePath).Length; } catch { }

        _detailMeta.Text =
            $"作者：{(m.Author.Length > 0 ? m.Author : "(未填)")}     "
            + $"创建：{m.CreatedUtc.ToLocalTime():yyyy-MM-dd HH:mm}     "
            + $"大小：{FormatSize(size)}";

        foreach (var mod in m.Mods) _modList.Items.Add(mod);
        _modsLbl.Text = $"包含的模组（{m.Mods.Count}）";

        var entries = ModpackManager.GetPackEntries(_selected.FilePath);
        _filesLbl.Text = $"包含的文件（{entries.Count}）";
        BuildFileTree(entries);
    }

    private void BuildFileTree(List<string> entries)
    {
        _fileTree.BeginUpdate();
        _fileTree.Nodes.Clear();

        var lookup = new Dictionary<string, TreeNode>(StringComparer.OrdinalIgnoreCase);

        foreach (var entry in entries)
        {
            var parts = entry.Split('/');
            TreeNodeCollection parent = _fileTree.Nodes;
            string path = "";

            for (int i = 0; i < parts.Length; i++)
            {
                path = path.Length == 0 ? parts[i] : path + "/" + parts[i];

                if (!lookup.TryGetValue(path, out var node))
                {
                    node = new TreeNode(parts[i]);
                    parent.Add(node);
                    lookup[path] = node;
                }

                parent = node.Nodes;
            }
        }

        _fileTree.EndUpdate();
    }

    private static string FormatSize(long bytes)
    {
        if (bytes >= 1024 * 1024) return $"{bytes / (1024.0 * 1024.0):0.#} MB";
        if (bytes >= 1024) return $"{bytes / 1024.0:0.#} KB";
        return $"{bytes} B";
    }

    // ------------------------------------------------------------ 操作

    private async Task DoCreateAsync()
    {
        if (_busy) return;

        var roots = ModpackManager.GetPackRoots();
        if (roots.Count == 0)
        {
            MessageBox.Show(this, "还没有选择游戏目录。", "错误", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }

        string defaultName = $"我的配置 {DateTime.Now:yyyy-MM-dd HHmm}";
        using var dlg = new ModpackExportDialog(roots, defaultName);
        if (dlg.ShowDialog(this) != DialogResult.OK) return;

        await RunBusyAsync("正在创建模组包…", () =>
            ModpackManager.SaveCurrentAsPackSelective(dlg.PackName, "", dlg.IncludedPaths, Progress));

        Refresh_();
        SetStatus("模组包已创建。");
    }

    private async Task DoApplyAsync()
    {
        if (_busy || _selected == null) return;

        if (AppState.IsGameRunning())
        {
            MessageBox.Show(this,
                "游戏正在运行，插件 DLL 被锁住，还原会失败。\n\n请先关掉游戏。",
                "游戏在运行", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }

        var covered = DescribeCoveredRoots(_selected.FilePath);

        var msg = $"要用「{_selected.DisplayName}」替换当前配置吗？\n\n"
                + $"会先清空这些目录，再按包里的内容重建：\n{covered}\n\n"
                + "BepInEx\\core、BepInEx\\interop 与 BepInEx.cfg 不会被碰。";

        if (MessageBox.Show(this, msg, "还原模组包", MessageBoxButtons.YesNo, MessageBoxIcon.Warning) != DialogResult.Yes)
            return;

        await RunBusyAsync("正在还原模组包…", () => ModpackManager.ApplyPack(_selected.FilePath, Progress));

        Refresh_();
        (FindForm() as MainForm)?.UpdateStatusBar();
        SetStatus("已还原模组包 — 需要重启游戏才生效。");
    }

    /// <summary>这份包会清空哪些根（给确认框用，让人看清代价）。</summary>
    private static string DescribeCoveredRoots(string packPath)
    {
        var entries = ModpackManager.GetPackEntries(packPath);
        var roots = ModpackManager.GetPackRoots()
            .Where(r => entries.Any(e => e.StartsWith(r.Prefix, StringComparison.OrdinalIgnoreCase)))
            .Select(r => "  ·  " + r.Name)
            .ToList();

        return roots.Count > 0 ? string.Join("\n", roots) : "  （包里没有可识别的路径）";
    }

    private void DoDelete()
    {
        if (_selected == null) return;

        if (MessageBox.Show(this, $"删除模组包「{_selected.DisplayName}」？\n\n只是删掉 %APPDATA% 里这份包文件，不影响当前已装的模组。",
                "确认删除", MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes)
            return;

        try { ModpackManager.DeletePack(_selected.FilePath); }
        catch (Exception ex)
        {
            MessageBox.Show(this, "删除失败：" + ex.Message, "错误", MessageBoxButtons.OK, MessageBoxIcon.Error);
            return;
        }

        _selected = null;
        Refresh_();
    }

    private void DoExport()
    {
        if (_selected == null) return;

        using var dlg = new SaveFileDialog
        {
            Title = "导出模组包",
            Filter = $"MVZ2 模组包|*{FileAssociation.Extension}",
            FileName = Path.GetFileName(_selected.FilePath),
        };
        if (dlg.ShowDialog(this) != DialogResult.OK) return;

        try
        {
            ModpackManager.ExportPack(_selected.FilePath, dlg.FileName);
            SetStatus("已导出：" + dlg.FileName);
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, "导出失败：" + ex.Message, "错误", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }

    private void DoImport()
    {
        using var dlg = new OpenFileDialog
        {
            Title = "导入模组包",
            Filter = $"MVZ2 模组包|*{FileAssociation.Extension}|所有文件|*.*",
        };
        if (dlg.ShowDialog(this) != DialogResult.OK) return;

        ImportAndPrompt(dlg.FileName);
    }

    // ------------------------------------------------------------ 拖拽

    private void OnDragEnter(object? sender, DragEventArgs e)
    {
        bool ok = TryGetDroppedPack(e, out _);
        e.Effect = ok ? DragDropEffects.Copy : DragDropEffects.None;
    }

    private void OnDragDrop(object? sender, DragEventArgs e)
    {
        if (!TryGetDroppedPack(e, out var path)) return;
        ImportAndPrompt(path);
    }

    private static bool TryGetDroppedPack(DragEventArgs e, out string path)
    {
        path = "";
        if (e.Data?.GetData(DataFormats.FileDrop) is not string[] files) return false;

        var found = files.FirstOrDefault(f =>
            f.EndsWith(FileAssociation.Extension, StringComparison.OrdinalIgnoreCase));
        if (found == null) return false;

        path = found;
        return true;
    }

    /// <summary>导入一份外部包，然后问要不要立刻还原（命令行双击进来的也走这条路）。</summary>
    internal void ImportAndPrompt(string sourcePath)
    {
        string imported;
        try { imported = ModpackManager.ImportPack(sourcePath); }
        catch (Exception ex)
        {
            MessageBox.Show(this, "导入失败：" + ex.Message, "错误", MessageBoxButtons.OK, MessageBoxIcon.Error);
            return;
        }

        Refresh_();

        int idx = _packs.FindIndex(p => string.Equals(p.FilePath, imported, StringComparison.OrdinalIgnoreCase));
        if (idx >= 0) _packList.SelectedIndex = idx;

        SetStatus("已导入：" + Path.GetFileName(imported));

        if (_selected == null) return;

        if (MessageBox.Show(this,
                $"已导入「{_selected.DisplayName}」。\n\n现在就还原它吗？",
                "导入完成", MessageBoxButtons.YesNo, MessageBoxIcon.Question) == DialogResult.Yes)
        {
            _ = DoApplyAsync();
        }
    }

    private void OpenFolder()
    {
        Directory.CreateDirectory(ModpackManager.PacksDir);
        System.Diagnostics.Process.Start("explorer.exe", ModpackManager.PacksDir);
    }

    // ------------------------------------------------------------ 忙碌态

    private IProgress<(int Percent, string Status)> Progress =>
        new Progress<(int Percent, string Status)>(p => SetStatus($"{p.Status}  {p.Percent}%"));

    /// <summary>
    /// 把耗时操作丢到后台并锁住工具栏。这些操作会动几十 MB 的文件，
    /// 同步跑会把窗口冻住，Windows 还会给你盖上"未响应"。
    /// </summary>
    private async Task RunBusyAsync(string what, Action action)
    {
        SetBusy(true);
        SetStatus(what);

        try
        {
            await Task.Run(action);
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, "操作失败：" + ex.Message, "错误", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
        finally
        {
            SetBusy(false);
        }
    }

    private void SetBusy(bool busy)
    {
        _busy = busy;
        foreach (var b in new[] { _createBtn, _refreshBtn, _importBtn, _openFolderBtn })
            b.Enabled = !busy;
        ShowDetail();
    }

    private void SetStatus(string text) => _statusLabel.Text = text;

    // ------------------------------------------------------------ 自绘

    private void DrawPackItem(object? sender, DrawItemEventArgs e)
    {
        if (e.Index < 0 || e.Index >= _packList.Items.Count) return;

        var pack = (ModpackInfo)_packList.Items[e.Index]!;
        bool selected = (e.State & DrawItemState.Selected) != 0;
        var t = ThemeEngine.Current;

        e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
        using (var bg = new SolidBrush(_packList.BackColor))
            e.Graphics.FillRectangle(bg, e.Bounds);

        var card = new Rectangle(e.Bounds.Left + 6, e.Bounds.Top + 3, e.Bounds.Width - 12, e.Bounds.Height - 6);
        using (var path = RoundedGraphics.RoundedRect(card, 8))
        using (var brush = new SolidBrush(selected ? t.Highlight : t.Surface))
            e.Graphics.FillPath(brush, path);

        using var titleFont = ThemeEngine.MakeFont(10f, FontStyle.Bold);
        using var subFont = ThemeEngine.MakeFont(8f);

        var m = pack.Manifest;
        string sub = $"{m.Mods.Count} 个模组";
        if (m.CreatedUtc != default) sub += $"  ·  {m.CreatedUtc.ToLocalTime():MM-dd HH:mm}";

        CardText.Draw(e.Graphics, card, 10, titleFont, pack.DisplayName, t.Text, subFont, sub, t.SubText);
    }

    // ------------------------------------------------------------ 主题

    private void ApplyTheme()
    {
        var t = ThemeEngine.Current;
        BackColor = t.Background;
        _toolbar.BackColor = t.Surface;
        _bodyWrap.BackColor = t.Background;
        _leftPanel.BackColor = t.SurfaceAlt;
        _detailPanel.BackColor = t.Background;
        _packList.BackColor = t.Background;
        _packList.ForeColor = t.Text;
        _modList.BackColor = t.Background;
        _modList.ForeColor = t.Text;
        _fileTree.BackColor = t.Background;
        _fileTree.ForeColor = t.Text;
        _detailName.ForeColor = t.Text;
        _detailMeta.ForeColor = t.SubText;
        _modsLbl.ForeColor = t.SubText;
        _modsLbl.BackColor = t.Background;
        _filesLbl.ForeColor = t.SubText;
        _filesLbl.BackColor = t.Background;
        _statusLabel.BackColor = t.Background;
        _statusLabel.ForeColor = t.SubText;

        foreach (var b in new[] { _refreshBtn, _deleteBtn, _importBtn, _exportBtn, _openFolderBtn })
            ThemeEngine.StyleGhostButton(b);
        ThemeEngine.StyleRButton(_createBtn, accent: true);
        ThemeEngine.StyleGhostButton(_applyBtn);

        _packList.Invalidate();
        ThemeEngine.ApplyScrollTheme(this);
    }

    private static Label MakeCaption(string text) => new()
    {
        Text = text, Dock = DockStyle.Top, Height = 20,
        Font = ThemeEngine.MakeFont(8.5f), TextAlign = ContentAlignment.MiddleLeft,
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
