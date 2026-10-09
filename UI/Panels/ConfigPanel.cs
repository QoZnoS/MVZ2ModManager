using System.Globalization;
using MVZ2ModManager.UI.Controls;
using MVZ2ModManager.Theme;
using MVZ2ModManager.Core;

namespace MVZ2ModManager.UI.Panels;

internal sealed class ConfigPanel : UserControl
{
    private readonly ListBox _fileList;
    private readonly RPanel  _leftPanel;
    private readonly Panel   _contentHost;
    private readonly Panel   _bodyWrap;
    private readonly Panel   _toolbar;
    private readonly RButton _saveBtn, _reloadBtn, _resetAllBtn;
    private readonly Label   _statusLabel, _titleLabel;

    private List<(string DisplayName, string FilePath)> _files = new();
    private ModConfigFile? _current;

    /// <summary>
    /// 当前页面上"能改值"的控件。每项带一个 <c>Problem</c>：返回 null = 填得对，
    /// 返回文案 = 填错了（保存前会整份拦下来）。
    /// </summary>
    private readonly List<(ConfigEntry Entry, Func<string> Read, Func<string?> Problem)> _liveWidgets = new();

    public ConfigPanel()
    {
        Dock = DockStyle.Fill;
        DoubleBuffered = true;

        _toolbar = new Panel { Dock = DockStyle.Top, Height = 44, Padding = new Padding(8, 0, 8, 6) };
        _saveBtn     = MakeBtn("保存");
        _reloadBtn   = MakeBtn("↺ 重新载入");
        _resetAllBtn = MakeBtn("全部恢复默认");
        _saveBtn.Enabled = false;
        _saveBtn.Click     += (_, __) => DoSave();
        _reloadBtn.Click   += (_, __) => Refresh_();
        _resetAllBtn.Click += (_, __) => DoResetAllToDefault();

        var toolFlow = new FlowLayoutPanel { Dock = DockStyle.Left, AutoSize = true, FlowDirection = FlowDirection.LeftToRight, WrapContents = false };
        foreach (var b in new[] { _saveBtn, _reloadBtn, _resetAllBtn }) { b.Margin = new Padding(0, 0, 6, 0); toolFlow.Controls.Add(b); }
        _toolbar.Controls.Add(toolFlow);

        _fileList = new ListBox { Dock = DockStyle.Fill, BorderStyle = BorderStyle.None };
        _fileList.SelectedIndexChanged += (_, __) => ShowSelectedFile();

        _leftPanel = new RPanel { Dock = DockStyle.Left, Width = 260, CornerRadius = 0, Padding = new Padding(2) };
        _leftPanel.Controls.Add(_fileList);

        _titleLabel = new Label
        {
            Dock = DockStyle.Top, Height = 30, Text = "",
            Font = ThemeEngine.MakeFont(12f, FontStyle.Bold), Padding = new Padding(4, 4, 0, 0),
        };

        _contentHost = new Panel { Dock = DockStyle.Fill, AutoScroll = true, Padding = new Padding(12, 4, 12, 12) };

        var detailWrap = new Panel { Dock = DockStyle.Fill };
        detailWrap.Controls.Add(_contentHost);
        detailWrap.Controls.Add(_titleLabel);

        var splitter = new Splitter { Dock = DockStyle.Left, Width = 1 };

        _bodyWrap = new Panel { Dock = DockStyle.Fill };
        _bodyWrap.Controls.Add(detailWrap);
        _bodyWrap.Controls.Add(splitter);
        _bodyWrap.Controls.Add(_leftPanel);

        _statusLabel = new Label { Dock = DockStyle.Bottom, Height = 22, Padding = new Padding(8, 0, 0, 0), TextAlign = ContentAlignment.MiddleLeft };
        _statusLabel.Tag = "subtext";

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

        string? keepPath = _current?.FilePath;
        _files = BepInExConfig.ListConfigFiles();

        _fileList.BeginUpdate();
        _fileList.Items.Clear();
        foreach (var f in _files) _fileList.Items.Add(f.DisplayName);
        _fileList.EndUpdate();

        _statusLabel.Text = $"{_files.Count} 个配置文件";
        _resetAllBtn.Enabled = _files.Count > 0;

        int idx = keepPath != null ? _files.FindIndex(f => f.FilePath == keepPath) : -1;
        _fileList.SelectedIndex = idx >= 0 ? idx : (_files.Count > 0 ? 0 : -1);
        if (_files.Count == 0) ShowSelectedFile();
    }

    private void DoResetAllToDefault()
    {
        if (_files.Count == 0) return;

        var confirm = MessageBox.Show(
            $"这会把全部 {_files.Count} 个配置文件里的所有设置都恢复成默认值，且无法撤销。\n\n要继续吗？",
            "全部恢复默认", MessageBoxButtons.YesNo, MessageBoxIcon.Warning);
        if (confirm != DialogResult.Yes) return;

        int changed = 0;
        foreach (var (_, filePath) in _files)
        {
            var file = BepInExConfig.Parse(filePath);
            bool dirty = false;
            foreach (var section in file.Sections)
                foreach (var entry in section.Entries)
                    if (entry.DefaultValue != null && !entry.Value.Equals(entry.DefaultValue, StringComparison.OrdinalIgnoreCase))
                    { entry.Value = entry.DefaultValue; dirty = true; changed++; }

            if (dirty) BepInExConfig.Save(file);
        }

        Refresh_();
        _statusLabel.Text = $"已在 {_files.Count} 个配置文件里把 {changed} 项设置恢复为默认值。";
    }

    private void ShowSelectedFile()
    {
        _contentHost.Controls.Clear();
        _liveWidgets.Clear();
        _current = null;
        _saveBtn.Enabled = false;

        int idx = _fileList.SelectedIndex;
        if (idx < 0 || idx >= _files.Count)
        {
            _titleLabel.Text = "";
            if (_files.Count == 0)
            {
                var theme = ThemeEngine.Current;
                var msg = new Label
                {
                    Text = "没有找到配置文件，先装几个模组、启动一次游戏试试！",
                    Dock = DockStyle.Top, AutoSize = false, Height = 60,
                    Font = ThemeEngine.MakeFont(10f), Padding = new Padding(4, 8, 4, 0),
                    ForeColor = theme.SubText, BackColor = theme.SurfaceAlt,
                };
                _contentHost.Controls.Add(msg);
            }
            return;
        }

        var (displayName, filePath) = _files[idx];
        _titleLabel.Text = displayName;

        try
        {
            _current = BepInExConfig.Parse(filePath);
        }
        catch (Exception ex)
        {
            _statusLabel.Text = "解析配置失败：" + ex.Message;
            return;
        }

        var t = ThemeEngine.Current;
        for (int i = _current.Sections.Count - 1; i >= 0; i--)
            _contentHost.Controls.Add(MakeSectionGroup(_current.Sections[i], t));

        _saveBtn.Enabled = _current.Sections.Any(s => s.Entries.Count > 0);
        ThemeEngine.ApplyScrollTheme(this);
    }

    private const int RowHeight = 54;
    private const int SectionTitleHeight = 30;

    private Control MakeSectionGroup(ConfigSection section, ThemeColors t)
    {
        var group = new Panel
        {
            Dock = DockStyle.Top,
            Height = SectionTitleHeight + section.Entries.Count * RowHeight + 10,
            BackColor = t.SurfaceAlt,
        };

        for (int i = section.Entries.Count - 1; i >= 0; i--)
            group.Controls.Add(MakeEntryRow(section.Entries[i], t));

        var title = new Label
        {
            Text = section.Name, Dock = DockStyle.Top, Height = SectionTitleHeight,
            Font = ThemeEngine.MakeFont(10f, FontStyle.Bold), Padding = new Padding(0, 6, 0, 0),
            ForeColor = t.Text, BackColor = t.SurfaceAlt,
        };
        group.Controls.Add(title);

        return group;
    }

    private Control MakeEntryRow(ConfigEntry entry, ThemeColors t)
    {
        var row = new Panel { Dock = DockStyle.Top, Height = RowHeight, Padding = new Padding(0, 4, 0, 4), BackColor = t.SurfaceAlt };

        var label = new Label
        {
            Text = entry.Key, Dock = DockStyle.Top, Height = 18, AutoSize = false,
            Font = ThemeEngine.MakeFont(9.5f, FontStyle.Bold), ForeColor = t.Text, BackColor = t.SurfaceAlt,
        };
        string? desc = entry.Description.Count > 0 ? string.Join(" ", entry.Description) : null;
        bool changedFromDefault = entry.DefaultValue != null && !entry.Value.Equals(entry.DefaultValue, StringComparison.OrdinalIgnoreCase);
        if (changedFromDefault) label.Text += "  （已修改）";

        string? tooltipText = desc;
        if (entry.DefaultValue != null)
            tooltipText = (tooltipText != null ? tooltipText + "\n\n" : "") + $"默认值：{entry.DefaultValue}";
        if (tooltipText != null) new ToolTip().SetToolTip(label, tooltipText);

        row.Controls.Add(BuildWidget(entry, t));
        row.Controls.Add(label);
        return row;
    }

    private Control BuildWidget(ConfigEntry entry, ThemeColors t)
    {
        if (entry.SettingType.Equals("Boolean", StringComparison.OrdinalIgnoreCase))
        {
            var chk = new CheckBox
            {
                Dock = DockStyle.Top, Height = 26, AutoSize = false,
                Text = entry.Value,
                Checked = entry.Value.Equals("true", StringComparison.OrdinalIgnoreCase),
                ForeColor = t.Text, BackColor = t.SurfaceAlt,
            };
            chk.CheckedChanged += (_, __) => chk.Text = chk.Checked ? "true" : "false";
            _liveWidgets.Add((entry, () => chk.Checked ? "true" : "false", () => (string?)null));
            return chk;
        }

        if (entry.Range != null)
        {
            var (min, max) = entry.Range.Value;

            // 滑条只留给「整数 + 窄区间」。端口是 1~65535、浮点是 0.1~10，这两种滑条根本给不准：
            // 六万多个位置挤在几百像素里，一个像素跳好几百；浮点还得先折成 0~1000 的整数，
            // 滑出来的值跟你想输的值根本不是一回事。
            if (WantsNumberBox(entry.SettingType, min, max))
                return BuildNumberBox(entry, t, min, max);

            int lo = (int)Math.Round(min), hi = Math.Max(lo, (int)Math.Round(max));

            var wrap = new Panel { Dock = DockStyle.Top, Height = 30, BackColor = t.SurfaceAlt };
            var valueLbl = new Label { Dock = DockStyle.Right, Width = 60, TextAlign = ContentAlignment.MiddleRight, ForeColor = t.Text, BackColor = t.SurfaceAlt };
            var slider = new TrackBar
            {
                Dock = DockStyle.Fill, TickStyle = TickStyle.None,
                Minimum = lo, Maximum = hi,
            };

            int startVal = int.TryParse(entry.Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var sv) ? sv : lo;
            slider.Value = Math.Clamp(startVal, slider.Minimum, slider.Maximum);
            valueLbl.Text = FormatNumber(slider.Value);
            slider.ValueChanged += (_, __) => valueLbl.Text = FormatNumber(slider.Value);

            wrap.Controls.Add(slider);
            wrap.Controls.Add(valueLbl);

            _liveWidgets.Add((entry, () => FormatNumber(slider.Value), () => (string?)null));
            return wrap;
        }

        if (entry.AcceptableValues is { Count: > 0 })
        {
            var dd = new RDropdown
            {
                Dock = DockStyle.Top, Height = 30, CornerRadius = 6,
                ForeColor = t.Text, FillColor = t.Surface, HoverFillColor = t.Border, BorderColor = Color.Transparent,
            };
            foreach (var v in entry.AcceptableValues) dd.Items.Add(v);
            int sel = entry.AcceptableValues.FindIndex(v => v.Equals(entry.Value, StringComparison.OrdinalIgnoreCase));
            dd.SelectedIndex = sel >= 0 ? sel : 0;
            _liveWidgets.Add((entry, () => dd.SelectedIndex >= 0 ? entry.AcceptableValues[dd.SelectedIndex] : entry.Value, () => (string?)null));
            return dd;
        }

        var txt = new RTextBox
        {
            Dock = DockStyle.Top, Height = 30, CornerRadius = 6, Text = entry.Value,
            ForeColor = t.Text, BackColor = t.Surface,
        };
        _liveWidgets.Add((entry, () => txt.Text, () => (string?)null));
        return txt;
    }

    /// <summary>滑条能"滑得准"的最大区间跨度。再宽就只能让用户敲数字了。</summary>
    internal const int SliderMaxSpan = 200;

    /// <summary>
    /// 数值输入框的 <c>Tag</c> 标记。自检靠它把"数值输入框"和"普通文本框"分开 ——
    /// 两者都是 <see cref="RTextBox"/>，从类型上分不出来。
    /// </summary>
    internal const string NumericBoxTag = "numberbox";

    /// <summary>数值输入框旁边那行"范围 …"提示的 <c>Tag</c> 标记（自检用）。</summary>
    internal const string RangeHintTag = "rangehint";

    /// <summary>当前页面上有几个滑条（自检/截图用）。</summary>
    internal int SliderCount => CountDeep(_contentHost, c => c is TrackBar);

    /// <summary>当前页面上有几个数值输入框（自检/截图用）。</summary>
    internal int NumberBoxCount => CountDeep(_contentHost, c => c is RTextBox { Tag: NumericBoxTag });

    /// <summary>当前页面上数值输入框旁边的范围提示（自检核对文案与配色用）。</summary>
    internal List<Label> RangeHints()
    {
        var found = new List<Label>();
        Walk(_contentHost);
        return found;

        void Walk(Control parent)
        {
            foreach (Control c in parent.Controls)
            {
                if (c is Label l && l.Tag is RangeHintTag) found.Add(l);
                Walk(c);
            }
        }
    }

    private static int CountDeep(Control root, Func<Control, bool> match)
    {
        int n = 0;
        foreach (Control c in root.Controls)
        {
            if (match(c)) n++;
            n += CountDeep(c, match);
        }
        return n;
    }

    /// <summary>
    /// 选中某个配置文件（列表里没有就返回 false）。给截图工具用：
    /// 配置页默认拍的是列表里第一份文件，而端口那类数值项只在某几份里有。
    /// </summary>
    internal bool SelectFile(string filePath)
    {
        int idx = _files.FindIndex(f => f.FilePath == filePath);
        if (idx < 0) return false;
        _fileList.SelectedIndex = idx;
        return true;
    }

    /// <summary>明确是整数的设置类型。不在表里的按浮点对待 —— 浮点给输入框总是安全的。</summary>
    private static readonly HashSet<string> IntegerTypes = new(StringComparer.OrdinalIgnoreCase)
        { "Byte", "SByte", "Int16", "UInt16", "Int32", "UInt32", "Int64", "UInt64" };

    private static bool IsIntegerType(string settingType) => IntegerTypes.Contains(settingType.Trim());

    /// <summary>
    /// 这一项该给输入框还是滑条：<b>浮点一律输入框；整数只有区间跨度不超过
    /// <see cref="SliderMaxSpan"/> 时才给滑条。</b>
    ///
    /// <para>抽成 <c>internal static</c> 是给界面自检当锚点用的 ——
    /// "端口 1~65535 不许用滑条"这条规则得能被钉住，而不是只靠肉眼扫一遍截图。</para>
    /// </summary>
    internal static bool WantsNumberBox(string settingType, double min, double max)
        => !IsIntegerType(settingType) || max - min > SliderMaxSpan;

    /// <summary>
    /// 数值输入框。写回配置时一律用不变文化的小数点 ——
    /// 在"小数点是逗号"的系统上按当前文化格式化会写出 <c>0,35</c>，BepInEx 读回来就废了。
    /// </summary>
    private Control BuildNumberBox(ConfigEntry entry, ThemeColors t, double min, double max)
    {
        bool isInt = IsIntegerType(entry.SettingType);

        var box = new RTextBox
        {
            Width = 150, Height = 30, CornerRadius = 6, Text = entry.Value,
            ForeColor = t.Text, BackColor = t.Surface, Margin = new Padding(0),
            Tag = NumericBoxTag,
        };

        var hint = new Label
        {
            Text = $"范围 {FormatNumber(min)} ~ {FormatNumber(max)}",
            AutoSize = true, ForeColor = t.SubText, BackColor = t.SurfaceAlt,
            Font = ThemeEngine.MakeFont(8.5f), Margin = new Padding(8, 5, 0, 0),
            Tag = RangeHintTag,
        };

        string? Problem() => ValidateNumber(box.Text, isInt, min, max);

        void Revalidate() => box.ForeColor = Problem() == null ? t.Text : t.Danger;
        box.TextChanged += (_, __) => Revalidate();
        Revalidate();

        // 输入框靠左、范围提示紧跟其后。用 FlowLayoutPanel 定序，不去赌 Dock 的先后顺序。
        var wrap = new FlowLayoutPanel
        {
            Dock = DockStyle.Top, Height = 30, BackColor = t.SurfaceAlt,
            FlowDirection = FlowDirection.LeftToRight, WrapContents = false,
        };
        wrap.Controls.Add(box);
        wrap.Controls.Add(hint);

        _liveWidgets.Add((entry,
            () => TryParseNumber(box.Text.Trim(), out double v) ? FormatNumber(v) : box.Text.Trim(),
            Problem));
        return wrap;
    }

    /// <summary>
    /// 校验一个数值输入框的内容：返回 null = 填得对，否则是给人看的出错原因。
    ///
    /// <para>抽成纯函数是为了让自检能钉住"哪些输入算非法"。这条规则漏了，
    /// 用户敲进去的值就会以错误的形式写进配置文件，BepInEx 那边跟着坏。</para>
    /// </summary>
    internal static string? ValidateNumber(string text, bool isInt, double min, double max)
    {
        string s = text.Trim();
        if (s.Length == 0) return "不能为空";

        // 得显式拦 NaN / Infinity：TryParse 认它们，而它们跟任何 min/max 比大小
        // 都返回 false —— 光靠范围检查拦不住，会一路写进配置文件。
        if (!TryParseNumber(s, out double v) || double.IsNaN(v) || double.IsInfinity(v)) return "不是数字";
        if (isInt && Math.Abs(v - Math.Round(v)) > 1e-9) return "必须是整数";
        if (v < min || v > max) return $"要在 {FormatNumber(min)} ~ {FormatNumber(max)} 之间";
        return null;
    }

    /// <summary>写回配置文件的数字格式：不变文化，小数点永远是 "."。</summary>
    internal static string FormatNumber(double v)
    {
        if (v == Math.Floor(v) && Math.Abs(v) < 1e15) return ((long)v).ToString(CultureInfo.InvariantCulture);
        return v.ToString("0.######", CultureInfo.InvariantCulture);
    }

    /// <summary>
    /// 解析用户敲进来的数字：先按不变文化（配置文件里的小数点就是 "."），
    /// 失败且含逗号时再用当前文化兜一次 —— 否则"小数点是逗号"的系统上，
    /// 用户照自己的习惯敲 <c>0,35</c> 会被判成非法。
    /// </summary>
    internal static bool TryParseNumber(string text, out double value)
    {
        if (double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out value)) return true;
        return text.Contains(',') && double.TryParse(text, NumberStyles.Float, CultureInfo.CurrentCulture, out value);
    }

    private void DoSave()
    {
        if (_current == null) return;

        // 先把整份校验一遍再动手：有一项填错就整份不存。
        // 只校验一半、存一半的话，会留下半新半旧的配置，还让人以为存成功了。
        var problems = new List<string>();
        foreach (var (entry, _, problem) in _liveWidgets)
        {
            string? p = problem();
            if (p != null) problems.Add($"  · {entry.Key}：{p}");
        }

        if (problems.Count > 0)
        {
            _statusLabel.Text = $"{problems.Count} 项填写有误，未保存。";
            MessageBox.Show(
                "下面这些设置有误，已取消保存：\n\n" + string.Join("\n", problems) +
                "\n\n改好之后再点一次「保存」。",
                "设置有误", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }

        foreach (var (entry, read, _) in _liveWidgets) entry.Value = read();

        try
        {
            BepInExConfig.Save(_current);
            _statusLabel.Text = $"已保存 {_current.DisplayName}。";
        }
        catch (Exception ex) { _statusLabel.Text = "保存失败：" + ex.Message; }
    }

    private void ApplyTheme()
    {
        var t = ThemeEngine.Current;
        BackColor = t.Background;
        _toolbar.BackColor = t.Surface;
        _bodyWrap.BackColor = t.Background;
        _fileList.BackColor = t.Background;
        _fileList.ForeColor = t.Text;
        _leftPanel.BackColor = t.SurfaceAlt;
        _leftPanel.BorderColor = Color.Transparent;
        _contentHost.BackColor = t.SurfaceAlt;
        _titleLabel.BackColor = t.SurfaceAlt;
        _titleLabel.ForeColor = t.Text;
        _statusLabel.BackColor = t.Background;
        _statusLabel.ForeColor = t.SubText;

        ThemeEngine.StyleRButton(_saveBtn, accent: true);
        ThemeEngine.StyleGhostButton(_reloadBtn);
        ThemeEngine.StyleGhostButton(_resetAllBtn);
        ThemeEngine.ApplyScrollTheme(this);
    }

    private static RButton MakeBtn(string text) => new()
    {
        Text = text, Style = RButtonStyle.Outline, CornerRadius = 8,
        RoundedCorners = Corners.BottomLeft | Corners.BottomRight,
        AutoSize = true, Padding = new Padding(10, 2, 10, 2), Height = 38,
    };

    protected override void Dispose(bool disposing)
    {
        if (disposing) ThemeEngine.ThemeChanged -= ApplyTheme;
        base.Dispose(disposing);
    }
}
