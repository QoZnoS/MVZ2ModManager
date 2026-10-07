using MVZ2ModManager.Core;
using MVZ2ModManager.Theme;
using MVZ2ModManager.UI.Controls;

namespace MVZ2ModManager.UI;

/// <summary>
/// 首次使用引导：一条停在窗口底部的"教练卡"，一步一句，顺便把相关标签页真的翻给你看。
///
/// <para>刻意不做全屏遮罩：这个工具的价值在于"看到自己有哪些模组、各自什么状态"，
/// 盖住界面等于把最重要的信息藏起来。所以卡片只占底部一条，上面的页面照常可见可点。</para>
///
/// <para>做法上的一个细节：卡片插在 <c>Controls</c> 的 index 1（状态栏之后、内容之前），
/// 这样 WinForms 的停靠顺序会让它正好落在内容区底部、状态栏上面。</para>
/// </summary>
internal sealed class TutorialOverlay : IDisposable
{
    private readonly MainForm _form;
    private readonly RPanel _card;
    private readonly Label _stepLabel;
    private readonly Label _titleLabel;
    private readonly Label _bodyLabel;
    private readonly RButton _backBtn;
    private readonly RButton _nextBtn;
    private readonly RButton _skipBtn;

    private int _step;

    private bool _disposed;

    private sealed record Step(string? Tab, string Title, string Body);

    private static readonly Step[] Steps =
    [
        new(null, "欢迎！",
            "这个工具解决一件事：在开游戏之前决定启用哪些模组。\n"
            + "这里的「关闭」是把 dll 改名成 .dll.disabled。"),

        new("已安装", "开关模组",
            "列表右侧的复选框就是开关，删除图标是改名成 *.delete。\n"
            + "GUID 列会顺带显示这个模组占用的游戏原生命名空间，例如 [mvz2_lab]。\n"
            + "快捷键：空格 = 开关，Delete = 卸载，Ctrl+F = 搜索。"),

        new("已安装", "关于存档",
            "关卡存档头里记着「需要哪些命名空间及版本」，读档时逐个比对。\n"
            + "停用一个模组，用到它的关卡存档就会读不进去。通常而言，用户存档不受影响。\n"
            + "禁用前会告诉你具体是哪几份存档会受影响。"),

        new("快照点", "模组快照",
            "将当前启用的模组保存为快照，用于快捷切换。\n"
            + "快照会同时保存 BepInEx 的 config。"),

        new("配置", "模组配置项",
            "这里列出各个模组的 cfg 文件，并按它们自己声明的类型\n"
            + "（复选框 / 滑条 / 取值列表）渲染成对应的编辑控件。"),

        new("日志", "日志兼作加载进度",
            "这一页既是日志查看器，也顺带是加载进度看板。"),

        new("设置", "总开关",
            "设置页的「Enable mods」会改名 winhttp.dll ，等于一次性关掉全部模组。\n"
            + "这是唯一一个「全部关闭且完全可逆」的手段，不删任何 dll。"),

        new("模组包", "分享你的模组配置",
            "「从当前配置创建…」会把当前 setup 打成一个 .mvz2pack。\n"
            + "还原时只清空这份包确实覆盖的目录，绝不动 BepInEx\\core 与 interop。"),

        new(null, "结束了！",
            "随时可以从 设置 → 重新观看使用引导 再打开这段话。\n"
            + "另外：改开关之前请先关掉游戏，不然插件 dll 被锁住、改名会失败。"),
    ];

    public TutorialOverlay(MainForm form)
    {
        _form = form;
        var t = ThemeEngine.Current;

        _card = new RPanel
        {
            Dock = DockStyle.Bottom,
            Height = 164,
            CornerRadius = 0,
            Padding = new Padding(20, 12, 20, 12),
            Visible = false,
        };
        _card.Tag = "surface";

        _stepLabel = new Label
        {
            Dock = DockStyle.Top, Height = 18,
            Font = ThemeEngine.MakeFont(8f),
            TextAlign = ContentAlignment.MiddleLeft,
        };
        _stepLabel.Tag = "subtext";

        _titleLabel = new Label
        {
            Dock = DockStyle.Top, Height = 26,
            Font = ThemeEngine.MakeFont(11f, FontStyle.Bold),
            TextAlign = ContentAlignment.MiddleLeft,
        };

        _backBtn = MakeBtn("上一步", 84);
        _nextBtn = MakeBtn("下一步", 96);
        _skipBtn = MakeBtn("跳过", 72);

        _backBtn.Click += (_, __) => { if (_step > 0) { _step--; Render(); } };
        _nextBtn.Click += (_, __) =>
        {
            if (_step >= Steps.Length - 1) { Finish(); return; }
            _step++;
            Render();
        };
        _skipBtn.Click += (_, __) => Finish();

        var buttons = new FlowLayoutPanel
        {
            Dock = DockStyle.Bottom, Height = 34, FlowDirection = FlowDirection.RightToLeft,
            WrapContents = false, Padding = Padding.Empty,
        };
        buttons.Controls.Add(_nextBtn);
        buttons.Controls.Add(_backBtn);
        buttons.Controls.Add(_skipBtn);

        _bodyLabel = new Label
        {
            Dock = DockStyle.Fill,
            Font = ThemeEngine.MakeFont(9f),
            TextAlign = ContentAlignment.TopLeft,
        };

        // Dock 顺序：先加的通吃剩余空间 → 倒着加。
        _card.Controls.Add(_bodyLabel);
        _card.Controls.Add(buttons);
        _card.Controls.Add(_titleLabel);
        _card.Controls.Add(_stepLabel);

        _form.Controls.Add(_card);
        _form.Controls.SetChildIndex(_card, 1);   // 状态栏之后、内容之前 → 落在内容区底部

        ThemeEngine.ThemeChanged += ApplyTheme;
        ApplyTheme();
    }

    // ------------------------------------------------------------ 流程

    public void Start()
    {
        _step = 0;
        _card.Visible = true;
        _card.BringToFront();
        Render();
    }

    private void Render()
    {
        if (_disposed) return;

        var step = Steps[_step];

        _stepLabel.Text = $"使用引导  {_step + 1} / {Steps.Length}";
        _titleLabel.Text = step.Title;
        _bodyLabel.Text = step.Body;

        _backBtn.Enabled = _step > 0;
        _nextBtn.Text = _step >= Steps.Length - 1 ? "开始使用" : "下一步";
        _skipBtn.Visible = _step < Steps.Length - 1;

        if (step.Tab != null) _form.SwitchToTab(step.Tab);
    }

    private void Finish()
    {
        AppState.Settings.HasSeenTutorial = true;
        AppState.Save();
        Dispose();
    }

    /// <summary>收摊：退订事件、把卡片从窗口上摘掉。</summary>
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        _card.Visible = false;
        ThemeEngine.ThemeChanged -= ApplyTheme;

        if (_form.Controls.Contains(_card)) _form.Controls.Remove(_card);
        _card.Dispose();
    }

    /// <summary>自检用：从头到尾把每一步渲染一遍（会真的切标签页），然后收摊。</summary>
    internal void StepThroughAllForTest()
    {
        Start();
        for (int i = 0; i < Steps.Length; i++)
        {
            Render();
            if (_step < Steps.Length - 1) _step++;
        }
        Dispose();
    }

    // ------------------------------------------------------------ 外观

    private static RButton MakeBtn(string text, int width) => new()
    {
        Text = text, Width = width, Height = 28, CornerRadius = 6,
        Style = RButtonStyle.Outline, Margin = new Padding(6, 0, 0, 0),
    };

    private void ApplyTheme()
    {
        var t = ThemeEngine.Current;

        _card.BackColor = t.SurfaceAlt;
        _card.BorderColor = t.Border;
        _stepLabel.ForeColor = t.SubText;
        _stepLabel.BackColor = t.SurfaceAlt;
        _titleLabel.ForeColor = t.Text;
        _titleLabel.BackColor = t.SurfaceAlt;
        _bodyLabel.ForeColor = t.Text;
        _bodyLabel.BackColor = t.SurfaceAlt;

        ThemeEngine.StyleGhostButton(_backBtn);
        ThemeEngine.StyleGhostButton(_skipBtn);
        ThemeEngine.StyleRButton(_nextBtn, accent: true);
    }
}
