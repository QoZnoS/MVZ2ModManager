using System.Drawing.Drawing2D;
using MVZ2ModManager.Core;
using MVZ2ModManager.Theme;
using MVZ2ModManager.UI.Controls;

namespace MVZ2ModManager.UI.Panels;

/// <summary>
/// **已安装**页：开关模组、看依赖、看存档影响。
///
/// <para>版面沿用上游的"工具栏 + 自绘列表 + 状态栏"，但底层逻辑全部换成 MVZ2 的：
/// 列表里的 GUID / 依赖关系来自 DLL 元数据（<see cref="ModMetadata"/>），
/// 禁用前的拦截来自 <see cref="DependencyChecker"/>（会连带弄坏谁）与
/// <see cref="SaveCompatibility"/>（会让哪些存档读不进去）。</para>
/// </summary>
internal sealed class InstalledPanel : UserControl
{
    private List<InstalledMod> _mods = new();
    private List<InstalledMod> _filtered = new();
    private List<ModIssue> _issues = new();
    private List<SaveWarning> _saveWarnings = new();
    private readonly List<string> _conflicts = new();

    private readonly Panel _toolbar;
    private readonly Panel _banner;
    private readonly Label _bannerLabel;
    private readonly RButton _bannerBtn;
    private readonly Panel _bodyWrap;
    private readonly RPanel _listCard;
    private readonly ListView _list;
    private readonly Label _statusLabel;

    private readonly RButton _refreshBtn, _issuesBtn, _enableAllBtn, _disableAllBtn, _uninstallAllBtn, _openFolderBtn;
    private readonly RTextBox _search;

    /// <summary>我们自己改列宽时置位，避免和用户的拖动互相触发。</summary>
    private bool _adjustingColumns;

    public InstalledPanel()
    {
        Dock = DockStyle.Fill;
        DoubleBuffered = true;

        // ------------------------------------------------ 工具栏
        _toolbar = new Panel { Dock = DockStyle.Top, Height = 44, Padding = new Padding(8, 0, 8, 6) };

        _refreshBtn = MakeBtn("↺ 刷新");
        _issuesBtn = MakeBtn("⚠ 问题");
        _openFolderBtn = MakeBtn("打开插件目录 ↗");
        _enableAllBtn = MakeBtn("全部启用");
        _disableAllBtn = MakeBtn("全部禁用");
        _uninstallAllBtn = MakeBtn("全部卸载");

        _issuesBtn.Visible = false;

        _refreshBtn.Click += (_, __) => Refresh_();
        _issuesBtn.Click += (_, __) => ShowIssues();
        _openFolderBtn.Click += (_, __) => OpenFolder();
        _enableAllBtn.Click += (_, __) => SetAllEnabled(true);
        _disableAllBtn.Click += (_, __) => SetAllEnabled(false);
        _uninstallAllBtn.Click += (_, __) => UninstallAll();

        _search = new RTextBox
        {
            PlaceholderText = "搜索已安装的模组…",
            Width = 180,
            Height = 38,
            CornerRadius = 8,
            RoundedCorners = Corners.BottomLeft | Corners.BottomRight,
        };
        _search.TextChanged += (_, __) => PopulateList();

        var leftFlow = new FlowLayoutPanel
        {
            Dock = DockStyle.Left, AutoSize = true, FlowDirection = FlowDirection.LeftToRight,
            WrapContents = false, Padding = Padding.Empty,
        };
        foreach (var b in new[] { _refreshBtn, _issuesBtn, _openFolderBtn })
        {
            b.Margin = new Padding(0, 0, 6, 0);
            leftFlow.Controls.Add(b);
        }

        var rightFlow = new FlowLayoutPanel
        {
            Dock = DockStyle.Right, AutoSize = true, FlowDirection = FlowDirection.LeftToRight,
            WrapContents = false, Padding = Padding.Empty,
        };
        foreach (var b in new[] { _enableAllBtn, _disableAllBtn, _uninstallAllBtn })
        {
            b.Margin = new Padding(6, 0, 0, 0);
            rightFlow.Controls.Add(b);
        }
        _search.Margin = new Padding(6, 0, 0, 0);
        rightFlow.Controls.Add(_search);

        _toolbar.Controls.Add(leftFlow);
        _toolbar.Controls.Add(rightFlow);

        // ------------------------------------------------ 警告条
        _banner = new Panel { Dock = DockStyle.Top, Height = 34, Visible = false, Padding = new Padding(10, 0, 6, 0) };
        _bannerBtn = new RButton { Text = "详情", Dock = DockStyle.Right, Width = 84, CornerRadius = 6 };
        _bannerBtn.Click += (_, __) => ShowIssues();
        _bannerLabel = new Label
        {
            Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleLeft,
            AutoEllipsis = true, Font = ThemeEngine.MakeFont(9f, FontStyle.Bold),
        };
        _banner.Controls.Add(_bannerBtn);
        _banner.Controls.Add(_bannerLabel);

        // ------------------------------------------------ 列表
        _list = new ListView
        {
            Dock = DockStyle.Fill,
            View = View.Details,
            FullRowSelect = true,
            GridLines = false,
            BorderStyle = BorderStyle.None,
            OwnerDraw = true,
            MultiSelect = true,
        };
        _list.Columns.Add("名称", 210);
        _list.Columns.Add("状态", 82);
        _list.Columns.Add("插件 GUID", 200);
        _list.Columns.Add("大小", 70);
        _list.Columns.Add("", 34);   // 开关
        _list.Columns.Add("", 34);   // 删除
        _list.DrawColumnHeader += DrawHeader;
        _list.DrawItem += DrawItem;
        _list.DrawSubItem += (_, e) => e.DrawDefault = false;
        _list.MouseDown += List_MouseDown;
        _list.ColumnWidthChanging += List_ColumnWidthChanging;
        _list.HandleCreated += (_, __) => ThemeEngine.StripVisualStyle(_list);
        _list.Resize += (_, __) => StretchNameColumn();

        _statusLabel = new Label
        {
            Dock = DockStyle.Bottom, Height = 22, TextAlign = ContentAlignment.MiddleLeft,
            Padding = new Padding(8, 0, 0, 0),
        };
        _statusLabel.Tag = "subtext";

        _listCard = new RPanel { Dock = DockStyle.Fill, CornerRadius = 0, Padding = new Padding(2) };
        _listCard.Controls.Add(_list);

        _bodyWrap = new Panel { Dock = DockStyle.Fill, Padding = Padding.Empty };
        _bodyWrap.Controls.Add(_listCard);

        Controls.Add(_bodyWrap);
        Controls.Add(_banner);
        Controls.Add(_toolbar);
        Controls.Add(_statusLabel);

        ThemeEngine.ThemeChanged += ApplyTheme;
        HandleCreated += (_, __) => { ThemeEngine.ApplyScrollTheme(this); StretchNameColumn(); };
        ApplyTheme();
        Refresh_();
    }

    // ------------------------------------------------------------ 刷新

    public void Refresh_()
    {
        _mods = ModCatalog.Load();
        _issues = DependencyChecker.Check(_mods, AppState.GameDir is { } d && BepInExManager.ModsEnabled(d));
        FindConflicts();

        PopulateList();
        UpdateStatusText();
        UpdateBanner();

        // 存档扫描是磁盘 I/O，丢到后台，别卡住界面。
        _ = RefreshSavesAsync();
    }

    private async Task RefreshSavesAsync()
    {
        if (!AppState.Settings.WarnAboutSaveRisk)
        {
            // 设置里关掉了存档提醒 —— 连扫描都省了（那是一次磁盘 I/O）。
            _saveWarnings = new();
            UpdateBanner();
            return;
        }

        var usages = await Task.Run(() => SaveCompatibility.Summarize(SaveCompatibility.ScanAll()));

        if (IsDisposed) return;

        _saveWarnings = RequiredWarnings(usages);
        UpdateBanner();
    }

    /// <summary>当前启用中的模组覆盖了哪些命名空间（<paramref name="excluding"/> 里的名字当作已禁用）。</summary>
    private HashSet<string> EnabledNamespaces(IEnumerable<string>? excluding = null)
        => new(_mods
                .Where(m => m.Enabled && m.NativeNamespace != null
                         && (excluding == null || !excluding.Contains(m.Name, StringComparer.OrdinalIgnoreCase)))
                .Select(m => m.NativeNamespace!),
            StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// 从存档使用情况里挑出"会读不进去"的那部分。
    ///
    /// <para>设置里关掉存档提醒时**直接返回空** —— 门就守在这一处：
    /// 让调用方各判各的，迟早会漏掉一处，然后提醒又从别的路弹回来。</para>
    /// </summary>
    internal List<SaveWarning> RequiredWarnings(IReadOnlyList<SaveUsage> usages)
    {
        if (!AppState.Settings.WarnAboutSaveRisk) return new();
        return SaveCompatibility.RequiredButDisabled(usages, ModCatalog.NamespaceOwners(_mods), EnabledNamespaces());
    }

    private void FindConflicts()
    {
        _conflicts.Clear();
        var byFile = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);

        foreach (var m in _mods)
        {
            IEnumerable<string> dlls;
            try
            {
                dlls = m.IsFolder
                    ? Directory.EnumerateFiles(m.FilePath, "*.dll", SearchOption.AllDirectories)
                    : new[] { m.FilePath };
            }
            catch { continue; }

            foreach (var f in dlls)
            {
                string name = Path.GetFileName(f);
                if (!byFile.TryGetValue(name, out var owners)) byFile[name] = owners = new List<string>();
                if (!owners.Contains(m.Name)) owners.Add(m.Name);
            }
        }

        foreach (var (file, owners) in byFile)
            if (owners.Count > 1)
                _conflicts.Add($"{file} 被 {string.Join("、", owners)} 同时提供 — 只会加载其中一个。");
    }

    private void PopulateList()
    {
        string filter = _search.Text.Trim();
        _filtered = filter.Length == 0
            ? _mods
            : _mods.Where(m => m.Name.Contains(filter, StringComparison.OrdinalIgnoreCase)
                            || (m.PluginName?.Contains(filter, StringComparison.OrdinalIgnoreCase) ?? false)
                            || (m.Guid?.Contains(filter, StringComparison.OrdinalIgnoreCase) ?? false)).ToList();

        _list.BeginUpdate();
        _list.Items.Clear();
        foreach (var m in _filtered)
        {
            var item = new ListViewItem(m.Name) { Tag = m };
            item.SubItems.Add(m.Enabled ? "已启用" : "已禁用");
            item.SubItems.Add(m.Guid is { Length: > 0 } g
                ? (m.NativeNamespace is { Length: > 0 } nsp ? $"{g}  [{nsp}]" : g)
                : "—");
            item.SubItems.Add(FormatSize(m.SizeBytes));
            item.SubItems.Add("");
            item.SubItems.Add("");
            item.ForeColor = m.Enabled ? ThemeEngine.Current.Text : ThemeEngine.Current.SubText;
            _list.Items.Add(item);
        }
        _list.EndUpdate();
    }

    private void UpdateStatusText()
    {
        int enabled = _mods.Count(m => m.Enabled);
        long total = _mods.Sum(m => m.SizeBytes);

        var parts = new List<string>
        {
            $"{_mods.Count} 个模组",
            $"{enabled} 已启用 · {_mods.Count - enabled} 已禁用",
            $"{FormatSize(total)}",
        };

        int blocked = _issues.Count(i => i.Level == IssueLevel.Error);
        if (blocked > 0) parts.Add($"{blocked} 个问题");
        if (_conflicts.Count > 0) parts.Add($"{_conflicts.Count} 个文件冲突");

        _statusLabel.Text = string.Join("  ·  ", parts);
    }

    /// <summary>顶部警告条：只有真有事才出现，一条话说清楚。</summary>
    private void UpdateBanner()
    {
        int errors = _issues.Count(i => i.Level == IssueLevel.Error);
        int warnings = _issues.Count(i => i.Level == IssueLevel.Warning);

        string? text = null;
        Color tint = Color.FromArgb(255, 189, 46);

        if (errors > 0)
        {
            text = errors == 1
                ? _issues.First(i => i.Level == IssueLevel.Error).Message
                : $"{errors} 个问题会导致模组无法加载（缺少前置、GUID 重复……）。";
        }
        else if (_saveWarnings.Count > 0)
        {
            int n = _saveWarnings.Count;
            int saves = _saveWarnings.Sum(w => w.SaveCount);
            text = $"{n} 个已禁用的模组仍被 "
                 + $"{saves} 份现有存档依赖 — 这些存档将无法读取。";
        }
        else if (_conflicts.Count > 0)
        {
            text = $"已安装的模组之间有 {_conflicts.Count} 个文件冲突。";
        }
        else if (warnings > 0)
        {
            text = $"{warnings} 条提醒。";
            tint = ThemeEngine.Current.SubText;
        }

        _banner.Visible = text != null;
        if (text == null) return;

        _bannerLabel.Text = text;
        _banner.BackColor = RoundedGraphics.Lerp(ThemeEngine.Current.Surface, tint, 0.25f);
        _bannerLabel.BackColor = _banner.BackColor;
        _bannerLabel.ForeColor = ThemeEngine.Current.Text;
        _bannerBtn.Visible = errors > 0 || _conflicts.Count > 0 || _issues.Count > 1;
    }

    // ------------------------------------------------------------ 交互

    private void List_MouseDown(object? sender, MouseEventArgs e)
    {
        var hit = _list.HitTest(e.Location);
        if (hit.Item?.Tag is not InstalledMod m) return;

        int toggleLeft = 0;
        for (int i = 0; i < _list.Columns.Count - 2; i++) toggleLeft += _list.Columns[i].Width;
        int toggleRight = toggleLeft + _list.Columns[_list.Columns.Count - 2].Width;
        int deleteRight = toggleRight + _list.Columns[_list.Columns.Count - 1].Width;

        if (e.X >= toggleLeft && e.X < toggleRight) ToggleMod(m);
        else if (e.X >= toggleRight && e.X < deleteRight) UninstallMod(m);
    }

    /// <summary>
    /// 开关一个模组。**禁用前**跑两道拦截：硬依赖会被弄坏谁、有哪些存档会读不进去。
    /// 这两件事一旦发生，游戏里的表现都是"功能静默消失"，很难倒查，所以宁可多问一句。
    /// </summary>
    private void ToggleMod(InstalledMod m)
    {
        if (m.Enabled && !ConfirmDisable(new[] { m.Name })) return;

        try
        {
            if (m.Enabled) ModInstaller.Disable(m.Name);
            else ModInstaller.Enable(m.Name);
        }
        catch (IOException)
        {
            MessageBox.Show("插件 DLL 被占用 — 请先关闭游戏。", "无法修改",
                MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }

        Refresh_();
        (FindForm() as MainForm)?.UpdateStatusBar();
    }

    /// <summary>禁用前确认：列出会被连带弄坏的插件、以及会读不进去的存档。</summary>
    private bool ConfirmDisable(IEnumerable<string> names)
    {
        var list = names.ToList();
        var broken = DependencyChecker.WouldBreak(_mods, list);

        var disabledNs = _mods
            .Where(m => list.Contains(m.Name, StringComparer.OrdinalIgnoreCase) && m.NativeNamespace != null)
            .Select(m => m.NativeNamespace!)
            .ToList();

        var riskySaves = new List<SaveWarning>();
        if (AppState.Settings.WarnAboutSaveRisk && disabledNs.Count > 0)
        {
            var usages = SaveCompatibility.Summarize(SaveCompatibility.ScanAll());
            var owners = ModCatalog.NamespaceOwners(_mods);
            var stillEnabled = EnabledNamespaces(list);

            riskySaves = usages
                .Where(u => disabledNs.Contains(u.Namespace, StringComparer.OrdinalIgnoreCase))
                .Where(u => !stillEnabled.Contains(u.Namespace))
                .Select(u => new SaveWarning(u.Namespace,
                    owners.TryGetValue(u.Namespace, out var o) ? o : u.Namespace,
                    u.SaveCount, u.VersionRange, u.Samples))
                .ToList();
        }

        if (broken.Count == 0 && riskySaves.Count == 0) return true;

        var sb = new System.Text.StringBuilder();
        sb.AppendLine(list.Count == 1 ? $"要禁用「{list[0]}」吗？" : $"要禁用 {list.Count} 个模组吗？");
        sb.AppendLine();

        if (broken.Count > 0)
        {
            sb.AppendLine($"以下已启用的模组依赖你正在禁用的东西，禁用后它们会失效：");
            foreach (var b in broken) sb.AppendLine("  • " + b);
            sb.AppendLine();
        }

        if (riskySaves.Count > 0)
        {
            sb.AppendLine("以下存档需要你正在禁用的模组，禁用后将无法读取：");
            foreach (var w in riskySaves)
                sb.AppendLine($"  • {w.SaveCount} 份存档（{w.Namespace}@{w.VersionRange}）"
                            + (w.Samples.Count > 0 ? "  例如：" + string.Join("、", w.Samples.Take(3)) : ""));
            sb.AppendLine();
            sb.AppendLine("存档数据本身不会被删除 — 重新启用该模组后它们又能读了。");
            sb.AppendLine();
        }

        sb.Append("要继续吗？");

        return MessageBox.Show(sb.ToString(), "禁用前请确认",
            MessageBoxButtons.YesNo,
            riskySaves.Count > 0 || broken.Count > 0 ? MessageBoxIcon.Warning : MessageBoxIcon.Question) == DialogResult.Yes;
    }

    /// <summary>
    /// 卸载一个模组。**资源目录单独问一次** —— MVZ2 的模组往往是"插件 dll + 一整套
    /// StreamingAssets 资源"两部分，只丢 dll 会留下一堆孤儿资源，一起丢又可能误删。
    /// </summary>
    private void UninstallMod(InstalledMod m)
    {
        var dependents = _mods
            .Where(x => x.Name != m.Name && x.Guid is { Length: > 0 } &&
                        x.HardDependencies.Contains(m.Guid!, StringComparer.OrdinalIgnoreCase))
            .Select(x => x.Name)
            .ToList();

        var sb = new System.Text.StringBuilder();
        sb.AppendLine($"卸载 {m.Name}？");
        sb.AppendLine();

        if (dependents.Count > 0)
        {
            sb.AppendLine($"⚠ {string.Join("、", dependents)} 依赖它，卸载后这些模组将无法加载。");
            sb.AppendLine();
        }

        sb.Append("插件文件会被改名成 *.delete（不是删除），可以手动恢复。");

        var (files, bytes) = ModInstaller.MeasureAssets(m.NativeNamespace);
        string? checkText = files > 0
            ? $"同时移除资源目录 StreamingAssets\\Mods\\{m.NativeNamespace}\\（{files} 个文件，{FormatSize(bytes)}）"
            : null;

        if (!CheckboxConfirmDialog.Show(this, "确认卸载", sb.ToString(), checkText, out bool removeAssets,
                checkDefault: true))
            return;

        try
        {
            ModInstaller.Uninstall(m, removeAssets);
        }
        catch (Exception ex)
        {
            MessageBox.Show("卸载失败：" + ex.Message, "错误", MessageBoxButtons.OK, MessageBoxIcon.Error);
            return;
        }

        Refresh_();
        (FindForm() as MainForm)?.UpdateStatusBar();
    }

    private void SetAllEnabled(bool enabled)
    {
        var targets = _mods.Where(m => m.Enabled != enabled).Select(m => m.Name).ToList();
        if (targets.Count == 0) return;

        if (!enabled && !ConfirmDisable(targets)) return;

        var failed = ModInstaller.SetEnabledMany(targets, enabled);

        Refresh_();
        (FindForm() as MainForm)?.UpdateStatusBar();

        if (failed.Count > 0)
        {
            MessageBox.Show(
                $"有 {failed.Count} 个模组没能改成功 — 这些 DLL 被占用了：\n\n"
                + string.Join("\n", failed.Take(10))
                + "\n\n请关闭游戏后重试。",
                "被占用", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
    }

    private void UninstallAll()
    {
        if (_mods.Count == 0) return;

        int assetMods = _mods.Count(m => ModInstaller.HasAssets(m.NativeNamespace));

        var sb = new System.Text.StringBuilder();
        sb.AppendLine($"卸载全部 {_mods.Count} 个模组？");
        sb.AppendLine();
        sb.AppendLine("包括 DSHCore —— 之后游戏会回到「完全没有模组」的状态。");
        sb.AppendLine();
        sb.Append("插件文件会被改名成 *.delete，什么都没有真正删除。");

        string? checkText = assetMods > 0
            ? $"同时移除 {assetMods} 个模组的资源目录（StreamingAssets\\Mods\\ 下）"
            : null;

        if (!CheckboxConfirmDialog.Show(this, "卸载全部", sb.ToString(), checkText, out bool removeAssets,
                checkDefault: false))
            return;

        foreach (var m in _mods)
        {
            try { ModInstaller.Uninstall(m, removeAssets); } catch { }
        }

        Refresh_();
        (FindForm() as MainForm)?.UpdateStatusBar();
    }

    private void ShowIssues()
    {
        var lines = new List<string>();

        if (_issues.Count > 0)
        {
            lines.Add("=== 加载问题 ===");
            foreach (var i in _issues)
                lines.Add($"[{IssueLevelText.Of(i.Level)}] {i.Message}");
            lines.Add("");
        }

        if (_saveWarnings.Count > 0)
        {
            lines.Add("=== 需要已禁用模组的存档 ===");
            foreach (var w in _saveWarnings)
                lines.Add($"[{w.Namespace}@{w.VersionRange}] {w.SaveCount} 份存档，提供者：{w.Owner}"
                        + (w.Samples.Count > 0 ? "\n    " + string.Join("\n    ", w.Samples) : ""));
            lines.Add("");
        }

        if (_conflicts.Count > 0)
        {
            lines.Add("=== 文件冲突 ===");
            lines.AddRange(_conflicts);
            lines.Add("");
        }

        if (lines.Count == 0) { lines.Add("一切正常，没有问题。"); }

        MessageBox.Show(string.Join("\n", lines), "问题", MessageBoxButtons.OK, MessageBoxIcon.Information);
    }

    private void OpenFolder()
    {
        string? path = AppState.PluginsDir;
        if (path == null) return;
        if (!Directory.Exists(path)) Directory.CreateDirectory(path);
        System.Diagnostics.Process.Start("explorer.exe", path);
    }

    // ------------------------------------------------------------ 排版

    private void StretchNameColumn()
    {
        if (_list.Columns.Count == 0) return;

        int used = 0;
        for (int i = 1; i < _list.Columns.Count; i++) used += _list.Columns[i].Width;
        int nameWidth = _list.ClientSize.Width - used;
        if (nameWidth <= _list.Columns[0].Width) return;

        _adjustingColumns = true;
        try { _list.Columns[0].Width = nameWidth; }
        finally { _adjustingColumns = false; }
    }

    /// <summary>
    /// 拖动分隔条时让两列之和保持不变（见 <see cref="ColumnResize"/>）。
    ///
    /// <para>只改**右边**那一列，被拖的那列交给原生逻辑 —— 在这里连着改它，
    /// 会被原生逻辑在多拖一拍之后按自己的算法再盖回去，总宽度又开始漂。</para>
    /// </summary>
    private void List_ColumnWidthChanging(object? sender, ColumnWidthChangingEventArgs e)
        => ApplyColumnResize(e.ColumnIndex, e.NewWidth);

    /// <summary>
    /// 列宽调整的实际动作。抽出来是为了让自检能**直接调它** ——
    /// 拖拽这个手势没法自动测，但"拖完之后总宽度有没有变"可以。
    /// </summary>
    /// <returns>真的动了列宽就返回 true。</returns>
    internal bool ApplyColumnResize(int columnIndex, int newWidth)
    {
        if (_adjustingColumns) return false;

        int i = columnIndex;
        if (i < 0 || i + 1 >= _list.Columns.Count) return false;

        // 此刻被拖的列还没被改，Columns[i] 仍是"拖动前"的宽度。
        var moved = ColumnResize.Preserve(_list.Columns[i].Width, _list.Columns[i + 1].Width, newWidth);
        if (moved is not { } pair) return false;

        _adjustingColumns = true;
        try
        {
            _list.Columns[i].Width = pair.Left;
            _list.Columns[i + 1].Width = pair.Right;
        }
        finally { _adjustingColumns = false; }

        return true;
    }

    /// <summary>开发用：当前各列宽度（自检与截图日志会读它）。</summary>
    internal int[] ColumnWidths => _list.Columns.Cast<ColumnHeader>().Select(c => c.Width).ToArray();

    /// <summary>开发用：当前列宽的文本形式。</summary>
    internal string ColumnWidthReport => string.Join(" / ", ColumnWidths);

    private static string FormatSize(long bytes)
    {
        if (bytes >= 1024 * 1024) return $"{bytes / (1024.0 * 1024.0):0.#} MB";
        if (bytes >= 1024) return $"{bytes / 1024.0:0.#} KB";
        return $"{bytes} B";
    }

    // ------------------------------------------------------------ 自绘

    private void DrawHeader(object? sender, DrawListViewColumnHeaderEventArgs e)
    {
        var t = ThemeEngine.Current;
        using var bg = new SolidBrush(t.SurfaceAlt);
        e.Graphics.FillRectangle(bg, e.Bounds);

        using var font = ThemeEngine.MakeFont(9f);
        TextRenderer.DrawText(e.Graphics, e.Header!.Text, font, TextCellRect(e.Bounds, 6), t.SubText, CellTextFlags);

        using var pen = new Pen(t.Border);
        e.Graphics.DrawLine(pen, e.Bounds.Left, e.Bounds.Bottom - 1, e.Bounds.Right, e.Bounds.Bottom - 1);
    }

    /// <summary>
    /// 单元格文字的绘制方式：**垂直居中 + 超宽省略号**。
    ///
    /// <para>以前是 DrawString 配一个写死 14 当行高去算居中位置 —— 9pt 字体的实际行高
    /// 约 15~17px，比 14 大，于是文字整体偏下、底部还被行高切掉一截。
    /// 改用 TextRenderer 的 VerticalCenter 让它按真实行高算；EndEllipsis 顺手解决
    /// 长 GUID 和右侧列挤在一起的问题（GDI 按矩形宽度裁剪，不会画出格子）。</para>
    /// </summary>
    private const TextFormatFlags CellTextFlags =
        TextFormatFlags.Left | TextFormatFlags.SingleLine | TextFormatFlags.VerticalCenter |
        TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix;

    /// <summary>文字落在单元格内部：左右各留 <paramref name="inset"/>。</summary>
    private static Rectangle TextCellRect(Rectangle cell, int inset)
        => new(cell.Left + inset, cell.Top, Math.Max(0, cell.Width - inset * 2), cell.Height);

    private void DrawItem(object? sender, DrawListViewItemEventArgs e)
    {
        e.DrawDefault = false;

        var t = ThemeEngine.Current;
        var item = e.Item!;
        e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;

        bool disabledMod = item.Tag is InstalledMod { Enabled: false };
        using (var bg = new SolidBrush(e.ItemIndex % 2 == 0 ? t.Surface : t.Background))
            e.Graphics.FillRectangle(bg, e.Bounds);

        if (item.Selected)
        {
            var hi = new Rectangle(e.Bounds.Left + 2, e.Bounds.Top + 1, e.Bounds.Width - 4, e.Bounds.Height - 2);
            using var path = RoundedGraphics.RoundedRect(hi, 6);
            using var brush = new SolidBrush(t.Highlight);
            e.Graphics.FillPath(brush, path);
        }

        using var font = ThemeEngine.MakeFont(9f);
        int x = e.Bounds.Left;
        for (int col = 0; col < 4 && col < item.SubItems.Count; col++)
        {
            int width = _list.Columns[col].Width;
            string text = item.SubItems[col].Text;

            bool rowEnabled = item.Tag is InstalledMod rm && rm.Enabled;
            Color fg = col == 1 ? (rowEnabled ? Color.FromArgb(39, 201, 63) : Color.FromArgb(255, 95, 86))
                     : col == 2 ? t.SubText
                     : disabledMod ? t.SubText
                     : t.Text;

            var cell = new Rectangle(x, e.Bounds.Top, width, e.Bounds.Height);
            TextRenderer.DrawText(e.Graphics, text, font, TextCellRect(cell, 6), fg, CellTextFlags);
            x += width;
        }

        if (item.Tag is InstalledMod m)
        {
            int toggleIdx = _list.Columns.Count - 2;
            int deleteIdx = _list.Columns.Count - 1;
            var toggleRect = new Rectangle(x, e.Bounds.Top, _list.Columns[toggleIdx].Width, e.Bounds.Height);
            var deleteRect = new Rectangle(x + _list.Columns[toggleIdx].Width, e.Bounds.Top, _list.Columns[deleteIdx].Width, e.Bounds.Height);
            DrawCheckbox(e.Graphics, toggleRect, m.Enabled, t);
            DrawTrashIcon(e.Graphics, deleteRect, Color.FromArgb(255, 95, 86));
        }

        using (var pen = new Pen(t.Border))
            e.Graphics.DrawLine(pen, e.Bounds.Left, e.Bounds.Bottom - 1, e.Bounds.Right, e.Bounds.Bottom - 1);
    }

    private static void DrawCheckbox(Graphics g, Rectangle area, bool isChecked, ThemeColors t)
    {
        const int size = 16;
        var rect = new Rectangle(area.Left + (area.Width - size) / 2, area.Top + (area.Height - size) / 2, size, size);
        using var path = RoundedGraphics.RoundedRect(rect, 4);

        if (isChecked)
        {
            using (var brush = new SolidBrush(t.Accent)) g.FillPath(brush, path);
            using var pen = new Pen(Color.White, 1.6f) { StartCap = LineCap.Round, EndCap = LineCap.Round, LineJoin = LineJoin.Round };
            g.DrawLines(pen, new[]
            {
                new Point(rect.Left + 3, rect.Top + 8),
                new Point(rect.Left + 7, rect.Top + 12),
                new Point(rect.Right - 3, rect.Top + 4),
            });
        }
        else
        {
            using var pen = new Pen(t.SubText, 1.4f);
            g.DrawPath(pen, path);
        }
    }

    private static void DrawTrashIcon(Graphics g, Rectangle area, Color color)
    {
        const int w = 12, h = 11;
        int left = area.Left + (area.Width - w) / 2;
        int top = area.Top + (area.Height - h) / 2 + 2;
        using var pen = new Pen(color, 1.3f) { LineJoin = LineJoin.Round };

        g.DrawLine(pen, left - 1, top, left + w + 1, top);
        g.DrawLine(pen, left + 3, top, left + 3, top - 2);
        g.DrawLine(pen, left + w - 3, top, left + w - 3, top - 2);
        g.DrawLine(pen, left + 3, top - 2, left + w - 3, top - 2);

        using var body = RoundedGraphics.RoundedRect(new Rectangle(left, top + 2, w, h - 2), 2);
        g.DrawPath(pen, body);

        g.DrawLine(pen, left + w / 3, top + 4, left + w / 3, top + h - 2);
        g.DrawLine(pen, left + w - w / 3, top + 4, left + w - w / 3, top + h - 2);
    }

    // ------------------------------------------------------------ 主题

    private void ApplyTheme()
    {
        var t = ThemeEngine.Current;
        BackColor = t.Background;
        _toolbar.BackColor = t.Surface;
        _bodyWrap.BackColor = t.Background;
        _listCard.BackColor = t.SurfaceAlt;
        _listCard.BorderColor = Color.Transparent;
        _list.BackColor = t.Surface;
        _list.ForeColor = t.Text;
        _statusLabel.BackColor = t.Background;
        _statusLabel.ForeColor = t.SubText;

        foreach (var b in new[] { _refreshBtn, _openFolderBtn, _enableAllBtn, _disableAllBtn, _uninstallAllBtn })
            ThemeEngine.StyleGhostButton(b);

        ThemeEngine.StyleGhostButton(_issuesBtn);
        _issuesBtn.ForeColor = Color.FromArgb(255, 189, 46);

        _search.BackColor = t.SurfaceAlt;
        _search.ForeColor = t.Text;

        _list.Invalidate();
        ThemeEngine.ApplyScrollTheme(this);
        UpdateBanner();
    }

    private static RButton MakeBtn(string text) => new()
    {
        Text = text, Style = RButtonStyle.Outline, CornerRadius = 8,
        RoundedCorners = Corners.BottomLeft | Corners.BottomRight,
        AutoSize = true, Padding = new Padding(8, 2, 8, 2), Height = 38,
    };

    protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
    {
        if (keyData == (Keys.Control | Keys.F)) { _search.Inner.Focus(); return true; }

        if (keyData == Keys.Delete && _list.SelectedItems.Count > 0 && _list.SelectedItems[0].Tag is InstalledMod m)
        {
            UninstallMod(m);
            return true;
        }

        if (keyData == Keys.Space && _list.SelectedItems.Count > 0 && _list.SelectedItems[0].Tag is InstalledMod s)
        {
            ToggleMod(s);
            return true;
        }

        return base.ProcessCmdKey(ref msg, keyData);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) ThemeEngine.ThemeChanged -= ApplyTheme;
        base.Dispose(disposing);
    }
}
