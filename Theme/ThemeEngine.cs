using System.Runtime.InteropServices;

namespace MVZ2ModManager.Theme;

internal struct ThemeColors
{
    public Color Background;
    public Color Surface;
    public Color SurfaceAlt;
    public Color Border;
    public Color Text;
    public Color SubText;
    public Color Accent;
    public Color AccentText;

    public Color Highlight
    {
        get
        {
            bool dark = Background.R + Background.G + Background.B < 384;
            int amt = dark ? 22 : -22;
            return Color.FromArgb(
                Math.Clamp(SurfaceAlt.R + amt, 0, 255),
                Math.Clamp(SurfaceAlt.G + amt, 0, 255),
                Math.Clamp(SurfaceAlt.B + amt, 0, 255));
        }
    }

    /// <summary>
    /// 校验不通过时的文字色。深色背景上必须提亮、浅色背景上必须压暗，
    /// 否则红字会糊在背景里 —— 那跟没有提示没区别。
    /// </summary>
    public Color Danger
    {
        get
        {
            bool dark = Background.R + Background.G + Background.B < 384;
            return dark ? Color.FromArgb(240, 108, 108) : Color.FromArgb(178, 40, 40);
        }
    }
}

internal static class ThemeEngine
{
    public static ThemeColors Current { get; private set; } = Black();
    public static event Action? ThemeChanged;

    private static string? _uiFamily;

    /// <summary>
    /// UI 用的字体族。界面文案是中文，所以必须挑一个**真的带中日韩字形**的字体 ——
    /// 直接用 "Segoe UI" 在部分机器上会由 GDI+ 的字体链接兜底，但兜底结果不稳定
    /// （可能出方块或字形忽大忽小）。这里按优先级探测，探测失败才回退。
    /// </summary>
    public static string UiFontFamily => _uiFamily ??= ResolveUiFontFamily();

    /// <summary>新建一个 UI 字体。返回的对象归调用方所有（该 Dispose 的就 Dispose）。</summary>
    public static Font MakeFont(float size, FontStyle style = FontStyle.Regular)
        => new(UiFontFamily, size, style);

    private static string ResolveUiFontFamily()
    {
        string[] preferred = ["Microsoft YaHei UI", "Microsoft YaHei", "Microsoft JhengHei UI", "SimHei", "Segoe UI", "Tahoma"];

        try
        {
            var installed = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var f in FontFamily.Families) installed.Add(f.Name);
            foreach (var name in preferred)
                if (installed.Contains(name)) return name;
        }
        catch
        {
            // 枚举字体失败不该让程序起不来，交回默认值即可。
        }

        return "Segoe UI";
    }

    public static void Apply(Core.ThemeMode mode, string? customBg = null, string? customAccent = null)
    {
        Current = mode switch
        {
            Core.ThemeMode.White     => White(),
            Core.ThemeMode.Custom    => BuildCustom(customBg, customAccent),
            Core.ThemeMode.R2Modman  => R2Modman(),
            _                        => Black(),
        };
        ThemeChanged?.Invoke();
    }

    public static void Recolor(Control root)
    {
        ApplyControl(root);
        foreach (Control c in root.Controls)
            Recolor(c);
    }

    public static ThemeColors Black() => new()
    {
        Background = Color.FromArgb(20, 20, 20),
        Surface    = Color.FromArgb(30, 30, 30),
        SurfaceAlt = Color.FromArgb(62, 62, 62),
        Border     = Color.FromArgb(80, 80, 80),
        Text       = Color.FromArgb(232, 230, 239),
        SubText    = Color.FromArgb(160, 160, 160),
        Accent     = Color.FromArgb(124, 58, 237),
        AccentText = Color.White,
    };

    public static ThemeColors White() => new()
    {
        Background = Color.FromArgb(248, 248, 248),
        Surface    = Color.White,
        SurfaceAlt = Color.FromArgb(238, 238, 238),
        Border     = Color.FromArgb(210, 210, 210),
        Text       = Color.FromArgb(20, 20, 20),
        SubText    = Color.FromArgb(100, 100, 100),
        Accent     = Color.FromArgb(124, 58, 237),
        AccentText = Color.White,
    };

    public static ThemeColors R2Modman() => new()
    {
        Background = Color.FromArgb(27, 29, 39),
        Surface    = Color.FromArgb(35, 37, 47),
        SurfaceAlt = Color.FromArgb(46, 48, 61),
        Border     = Color.FromArgb(58, 61, 77),
        Text       = Color.FromArgb(228, 228, 232),
        SubText    = Color.FromArgb(154, 156, 173),
        Accent     = Color.FromArgb(88, 101, 242),
        AccentText = Color.White,
    };

    private static ThemeColors BuildCustom(string? bg, string? accent)
    {
        var base_ = Black();
        if (TryHex(bg, out var bgColor))
        {
            base_.Background = bgColor;
            base_.Surface    = Lighten(bgColor, 10);
            base_.SurfaceAlt = Lighten(bgColor, 20);
            base_.Border     = Lighten(bgColor, 35);
        }
        if (TryHex(accent, out var accentColor))
            base_.Accent = accentColor;
        return base_;
    }

    private static void ApplyControl(Control c)
    {

        if (c.Tag is string skip && skip == "swatch") return;

        Color parentBg = c.Parent?.BackColor ?? Current.Background;

        c.BackColor = c.Tag is string tag && tag == "surface"    ? Current.SurfaceAlt
                    : c.Tag is string tag2 && tag2 == "surfacealt" ? Current.SurfaceAlt
                    : c.Tag is string tag3 && tag3 == "accent"     ? Current.Accent
                    : parentBg;

        c.ForeColor = c.Tag is string tag4 && tag4 == "subtext"  ? Current.SubText
                    : c.Tag is string tag5 && tag5 == "accent"    ? Current.AccentText
                    : Current.Text;

        if (c is TextBox tb) { tb.BackColor = Current.SurfaceAlt; tb.ForeColor = Current.Text; tb.BorderStyle = BorderStyle.FixedSingle; }
        if (c is ListBox lb) { lb.BackColor = Current.Surface;    lb.ForeColor = Current.Text; }
        if (c is ListView lv){ lv.BackColor = Current.Surface;    lv.ForeColor = Current.Text; }
        if (c is ComboBox cb){ cb.BackColor = Current.SurfaceAlt; cb.ForeColor = Current.Text; }
        if (c is CheckBox chk) chk.ForeColor = Current.Text;
        if (c is RadioButton rb) rb.ForeColor = Current.Text;
        if (c is GroupBox gb) gb.ForeColor = Current.SubText;
        if (c is Panel p and not (UI.Controls.RPanel or UI.Controls.RFlowPanel))
            p.BackColor = c.Tag is string s && s == "surface" ? Current.SurfaceAlt : parentBg;

        if (c is UI.Controls.RButton rBtn) StyleRButton(rBtn);
        if (c is UI.Controls.RPanel rPanel)
        {
            rPanel.BackColor = c.Tag is string rs && rs == "surface" ? Current.SurfaceAlt : parentBg;
            rPanel.BorderColor = Color.Transparent;
        }
        if (c is UI.Controls.RFlowPanel rFlow)
        {
            rFlow.BackColor = c.Tag is string fs && fs == "surface" ? Current.SurfaceAlt : parentBg;
            rFlow.BorderColor = Color.Transparent;
        }
    }

    public static void StyleRButton(UI.Controls.RButton btn, bool accent = false)
    {
        accent = accent || btn.Tag is string t && t == "accent";
        if (accent)
        {
            btn.Style = UI.Controls.RButtonStyle.Solid;
            btn.FillColor = Current.Accent;
            btn.HoverFillColor = Lighten(Current.Accent, 18);
            btn.BorderColor = Current.Accent;
            btn.HoverBorderColor = Current.Accent;
            btn.ForeColor = Current.AccentText;
        }
        else
        {
            bool onDark = Current.Background.GetBrightness() < 0.5f;
            btn.Style = UI.Controls.RButtonStyle.Solid;
            btn.FillColor = onDark ? Lighten(Current.SurfaceAlt, 20) : Darken(Current.SurfaceAlt, 10);
            btn.HoverFillColor = onDark ? Lighten(Current.SurfaceAlt, 34) : Darken(Current.SurfaceAlt, 20);
            btn.BorderColor = Color.Transparent;
            btn.HoverBorderColor = Color.Transparent;
            btn.ForeColor = Current.Text;
        }
    }

    public static void StyleGhostButton(UI.Controls.RButton btn)
    {
        btn.Style = UI.Controls.RButtonStyle.Ghost;
        btn.FillColor = Color.Transparent;
        btn.HoverFillColor = Current.SurfaceAlt;
        btn.BorderColor = Color.Transparent;
        btn.HoverBorderColor = Color.Transparent;
        btn.ForeColor = Current.Text;
    }

    /// <summary>
    /// 给 <see cref="ContextMenuStrip"/> 上主题色。WinForms 的菜单默认走系统浅色配色，
    /// 在深色主题里白得刺眼，所以连渲染器一起换掉。
    ///
    /// <para><b>要在条目都加完之后再调用</b> —— 它会给已存在的条目逐条上色。</para>
    /// </summary>
    public static void StyleMenu(ContextMenuStrip menu)
    {
        var t = Current;

        menu.BackColor = t.Surface;
        menu.ForeColor = t.Text;
        menu.Renderer = new ToolStripProfessionalRenderer(new MenuColors(t)) { RoundedEdges = false };

        foreach (ToolStripItem item in menu.Items) StyleMenuItem(item, t);
    }

    private static void StyleMenuItem(ToolStripItem item, ThemeColors t)
    {
        item.BackColor = t.Surface;
        item.ForeColor = item.Enabled ? t.Text : t.SubText;

        if (item is ToolStripMenuItem mi)
            foreach (ToolStripItem child in mi.DropDownItems) StyleMenuItem(child, t);
    }

    /// <summary>菜单配色表：只覆盖真会露出来的那几项。</summary>
    private sealed class MenuColors : ProfessionalColorTable
    {
        private readonly ThemeColors _t;
        public MenuColors(ThemeColors t) => _t = t;

        public override Color ToolStripDropDownBackground => _t.Surface;
        public override Color ImageMarginGradientBegin => _t.Surface;
        public override Color ImageMarginGradientMiddle => _t.Surface;
        public override Color ImageMarginGradientEnd => _t.Surface;
        public override Color MenuBorder => _t.Border;
        public override Color MenuItemBorder => _t.Accent;
        public override Color MenuItemSelected => _t.SurfaceAlt;
        public override Color MenuItemSelectedGradientBegin => _t.SurfaceAlt;
        public override Color MenuItemSelectedGradientEnd => _t.SurfaceAlt;
        public override Color MenuItemPressedGradientBegin => _t.SurfaceAlt;
        public override Color MenuItemPressedGradientEnd => _t.SurfaceAlt;
        public override Color SeparatorDark => _t.Border;
        public override Color SeparatorLight => _t.Surface;
        public override Color CheckBackground => _t.Accent;
        public override Color CheckSelectedBackground => _t.Accent;
    }

    [DllImport("uxtheme.dll", CharSet = CharSet.Unicode)]
    private static extern int SetWindowTheme(IntPtr hwnd, string? pszSubAppName, string? pszSubIdList);
    public static void StripVisualStyle(Control c)
    {
        if (c.IsHandleCreated) SetWindowTheme(c.Handle, "", "");
    }

    public static void ApplyScrollTheme(Control root)
    {
        bool dark = Current.Background.GetBrightness() < 0.5f;
        ApplyScrollControl(root, dark);
        foreach (Control c in root.Controls)
            ApplyScrollTheme(c);
    }

    private static void ApplyScrollControl(Control c, bool dark)
    {
        if (!c.IsHandleCreated) return;

        bool scrollable = c is ListBox or TextBox { Multiline: true } or RichTextBox or TreeView or ListView
                       || (c is Panel { AutoScroll: true });
        if (!scrollable) return;
        SetWindowTheme(c.Handle, dark ? "DarkMode_Explorer" : "Explorer", null);
    }

    public static bool TryHex(string? hex, out Color color)
    {
        color = Color.Empty;
        if (string.IsNullOrEmpty(hex)) return false;
        hex = hex.TrimStart('#');
        if (hex.Length == 6 && int.TryParse(hex, System.Globalization.NumberStyles.HexNumber, null, out int rgb))
        {
            color = Color.FromArgb(0xFF, (rgb >> 16) & 0xFF, (rgb >> 8) & 0xFF, rgb & 0xFF);
            return true;
        }
        return false;
    }

    private static Color Lighten(Color c, int amount) =>
        Color.FromArgb(Math.Min(255, c.R + amount), Math.Min(255, c.G + amount), Math.Min(255, c.B + amount));

    private static Color Darken(Color c, int amount) =>
        Color.FromArgb(Math.Max(0, c.R - amount), Math.Max(0, c.G - amount), Math.Max(0, c.B - amount));

    public static string ToHex(Color c) => $"#{c.R:X2}{c.G:X2}{c.B:X2}";
}
