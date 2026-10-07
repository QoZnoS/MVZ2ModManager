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

    public InstalledPanel()
    {
        Dock = DockStyle.Fill;
        DoubleBuffered = true;

        // ------------------------------------------------ 工具栏
        _toolbar = new Panel { Dock = DockStyle.Top, Height = 44, Padding = new Padding(8, 0, 8, 6) };

        _refreshBtn = MakeBtn("↺ Refresh");
        _issuesBtn = MakeBtn("⚠ Issues");
        _openFolderBtn = MakeBtn("Open Plugins ↗");
        _enableAllBtn = MakeBtn("Enable All");
        _disableAllBtn = MakeBtn("Disable All");
        _uninstallAllBtn = MakeBtn("Uninstall All");

        _issuesBtn.Visible = false;

        _refreshBtn.Click += (_, __) => Refresh_();
        _issuesBtn.Click += (_, __) => ShowIssues();
        _openFolderBtn.Click += (_, __) => OpenFolder();
        _enableAllBtn.Click += (_, __) => SetAllEnabled(true);
        _disableAllBtn.Click += (_, __) => SetAllEnabled(false);
        _uninstallAllBtn.Click += (_, __) => UninstallAll();

        _search = new RTextBox
        {
            PlaceholderText = "Search installed...",
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
        _bannerBtn = new RButton { Text = "Details", Dock = DockStyle.Right, Width = 84, CornerRadius = 6 };
        _bannerBtn.Click += (_, __) => ShowIssues();
        _bannerLabel = new Label
        {
            Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleLeft,
            AutoEllipsis = true, Font = new Font("Segoe UI", 9f, FontStyle.Bold),
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
        _list.Columns.Add("Name", 210);
        _list.Columns.Add("Status", 82);
        _list.Columns.Add("Plugin GUID", 200);
        _list.Columns.Add("Size", 70);
        _list.Columns.Add("", 34);   // 开关
        _list.Columns.Add("", 34);   // 删除
        _list.DrawColumnHeader += DrawHeader;
        _list.DrawItem += DrawItem;
        _list.DrawSubItem += (_, e) => e.DrawDefault = false;
        _list.MouseDown += List_MouseDown;
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
        var usages = await Task.Run(() => SaveCompatibility.Summarize(SaveCompatibility.ScanAll()));

        if (IsDisposed) return;

        var owners = ModCatalog.NamespaceOwners(_mods);
        var enabledNs = new HashSet<string>(
            _mods.Where(m => m.Enabled && m.NativeNamespace != null).Select(m => m.NativeNamespace!),
            StringComparer.OrdinalIgnoreCase);

        _saveWarnings = SaveCompatibility.RequiredButDisabled(usages, owners, enabledNs);
        UpdateBanner();
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
                _conflicts.Add($"{file} is shipped by {string.Join(" and ", owners)} — only one will load.");
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
            item.SubItems.Add(m.Enabled ? "Enabled" : "Disabled");
            item.SubItems.Add(m.Guid ?? "—");
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
            $"{_mods.Count} mod{(_mods.Count == 1 ? "" : "s")}",
            $"{enabled} enabled, {_mods.Count - enabled} disabled",
            $"{FormatSize(total)}",
        };

        int blocked = _issues.Count(i => i.Level == IssueLevel.Error);
        if (blocked > 0) parts.Add($"{blocked} problem{(blocked == 1 ? "" : "s")}");
        if (_conflicts.Count > 0) parts.Add($"{_conflicts.Count} file conflict{(_conflicts.Count == 1 ? "" : "s")}");

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
                : $"{errors} problems will stop mods from loading (missing dependencies, duplicates, ...).";
        }
        else if (_saveWarnings.Count > 0)
        {
            int n = _saveWarnings.Count;
            int saves = _saveWarnings.Sum(w => w.SaveCount);
            text = $"{n} disabled mod{(n == 1 ? "" : "s")} {(n == 1 ? "is" : "are")} still needed by "
                 + $"{saves} existing save{(saves == 1 ? "" : "s")} — those won't load.";
        }
        else if (_conflicts.Count > 0)
        {
            text = $"{_conflicts.Count} file conflict{(_conflicts.Count == 1 ? "" : "s")} between installed mods.";
        }
        else if (warnings > 0)
        {
            text = $"{warnings} warning{(warnings == 1 ? "" : "s")}.";
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
            MessageBox.Show("The plugin DLL is locked — close the game first.", "Can't change it",
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
        if (disabledNs.Count > 0)
        {
            var usages = SaveCompatibility.Summarize(SaveCompatibility.ScanAll());
            var owners = ModCatalog.NamespaceOwners(_mods);
            var stillEnabled = new HashSet<string>(
                _mods.Where(m => m.Enabled && !list.Contains(m.Name, StringComparer.OrdinalIgnoreCase) && m.NativeNamespace != null)
                     .Select(m => m.NativeNamespace!),
                StringComparer.OrdinalIgnoreCase);

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
        sb.AppendLine(list.Count == 1 ? $"Disable {list[0]}?" : $"Disable {list.Count} mods?");
        sb.AppendLine();

        if (broken.Count > 0)
        {
            sb.AppendLine($"These enabled mods require something you're disabling and will stop working:");
            foreach (var b in broken) sb.AppendLine("  • " + b);
            sb.AppendLine();
        }

        if (riskySaves.Count > 0)
        {
            sb.AppendLine("These saved levels need a mod you're disabling, and will refuse to load:");
            foreach (var w in riskySaves)
                sb.AppendLine($"  • {w.SaveCount} save{(w.SaveCount == 1 ? "" : "s")} ({w.Namespace}@{w.VersionRange})"
                            + (w.Samples.Count > 0 ? "  e.g. " + string.Join(", ", w.Samples.Take(3)) : ""));
            sb.AppendLine();
            sb.AppendLine("The save data itself isn't deleted — turn the mod back on and they load again.");
            sb.AppendLine();
        }

        sb.Append("Continue?");

        return MessageBox.Show(sb.ToString(), "Before you disable this",
            MessageBoxButtons.YesNo,
            riskySaves.Count > 0 || broken.Count > 0 ? MessageBoxIcon.Warning : MessageBoxIcon.Question) == DialogResult.Yes;
    }

    private void UninstallMod(InstalledMod m)
    {
        var dependents = _mods
            .Where(x => x.Name != m.Name && x.HardDependencies.Contains(m.Guid ?? "\u0000", StringComparer.OrdinalIgnoreCase))
            .Select(x => x.Name)
            .ToList();

        string warning = dependents.Count > 0
            ? $"\n\n{string.Join(", ", dependents)} depend{(dependents.Count == 1 ? "s" : "")} on it."
            : "";

        if (MessageBox.Show(
                $"Uninstall {m.Name}?{warning}\n\nThe files are renamed to *.delete (not erased), so you can get them back.",
                "Confirm", MessageBoxButtons.YesNo,
                dependents.Count > 0 ? MessageBoxIcon.Warning : MessageBoxIcon.Question) != DialogResult.Yes)
            return;

        try
        {
            ModInstaller.Uninstall(m.Name);
        }
        catch (Exception ex)
        {
            MessageBox.Show("Couldn't uninstall: " + ex.Message, "Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
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
                $"Couldn't change {failed.Count} mod{(failed.Count == 1 ? "" : "s")} — the DLLs are locked:\n\n"
                + string.Join("\n", failed.Take(10))
                + "\n\nClose the game and try again.",
                "Locked", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
    }

    private void UninstallAll()
    {
        if (_mods.Count == 0) return;

        if (MessageBox.Show(
                $"Uninstall all {_mods.Count} mods?\n\n" +
                "This includes DSHCore. The files are renamed to *.delete, so nothing is erased — " +
                "but keep in mind that only BepInEx itself decides what loads: a *.delete file never loads.",
                "Uninstall everything", MessageBoxButtons.YesNo, MessageBoxIcon.Warning) != DialogResult.Yes)
            return;

        foreach (var m in _mods)
        {
            try { ModInstaller.Uninstall(m.Name); } catch { }
        }

        Refresh_();
        (FindForm() as MainForm)?.UpdateStatusBar();
    }

    private void ShowIssues()
    {
        var lines = new List<string>();

        if (_issues.Count > 0)
        {
            lines.Add("=== Loading problems ===");
            foreach (var i in _issues)
                lines.Add($"[{i.Level}] {i.Message}");
            lines.Add("");
        }

        if (_saveWarnings.Count > 0)
        {
            lines.Add("=== Saves that need a disabled mod ===");
            foreach (var w in _saveWarnings)
                lines.Add($"[{w.Namespace}@{w.VersionRange}] {w.SaveCount} save(s), owner: {w.Owner}"
                        + (w.Samples.Count > 0 ? "\n    " + string.Join("\n    ", w.Samples) : ""));
            lines.Add("");
        }

        if (_conflicts.Count > 0)
        {
            lines.Add("=== File conflicts ===");
            lines.AddRange(_conflicts);
            lines.Add("");
        }

        if (lines.Count == 0) { lines.Add("Nothing to report — everything looks fine."); }

        MessageBox.Show(string.Join("\n", lines), "Issues", MessageBoxButtons.OK, MessageBoxIcon.Information);
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
        if (nameWidth > _list.Columns[0].Width) _list.Columns[0].Width = nameWidth;
    }

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
        using var fg = new SolidBrush(t.SubText);
        e.Graphics.DrawString(e.Header!.Text, new Font("Segoe UI", 9f), fg,
            e.Bounds.Left + 6, e.Bounds.Top + (e.Bounds.Height - 14) / 2);
        using var pen = new Pen(t.Border);
        e.Graphics.DrawLine(pen, e.Bounds.Left, e.Bounds.Bottom - 1, e.Bounds.Right, e.Bounds.Bottom - 1);
    }

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

        var font = new Font("Segoe UI", 9f);
        int x = e.Bounds.Left;
        for (int col = 0; col < 4 && col < item.SubItems.Count; col++)
        {
            int width = _list.Columns[col].Width;
            string text = item.SubItems[col].Text;

            Color fg = col == 1 ? (text == "Enabled" ? Color.FromArgb(39, 201, 63) : Color.FromArgb(255, 95, 86))
                     : col == 2 ? t.SubText
                     : disabledMod ? t.SubText
                     : t.Text;

            var cell = new Rectangle(x, e.Bounds.Top, width, e.Bounds.Height);
            e.Graphics.DrawString(text, font, new SolidBrush(fg), cell.Left + 6, cell.Top + (cell.Height - 14) / 2);
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
