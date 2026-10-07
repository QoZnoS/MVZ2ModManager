using MVZ2ModManager.Theme;

namespace MVZ2ModManager.UI.Controls;

/// <summary>
/// 可点击的超链接文字。
///
/// <para>用 <see cref="Label"/> 自己实现而不是 <c>LinkLabel</c>：后者自带一套固定的
/// 蓝色/紫色状态色，会把主题引擎的强调色盖掉，深色主题下尤其难看。</para>
///
/// <para>配色由 <see cref="ApplyTheme"/> 负责（外层 <c>RecolorDeep</c> 会调用它），
/// 不去订阅 <see cref="ThemeEngine.ThemeChanged"/>，避免每个链接都留一份事件。</para>
/// </summary>
internal sealed class RLink : Label
{
    /// <summary>目标地址。自检会拿它核对"关于页真的挂了这两个链接"。</summary>
    public string Url { get; }

    public RLink(string text, string url)
    {
        Text = text;
        Url = url;

        AutoSize = true;
        Font = ThemeEngine.MakeFont(9f, FontStyle.Underline);
        Cursor = Cursors.Hand;
        Margin = new Padding(2, 6, 0, 0);
        Tag = "link";

        Click += (_, __) => Open();
    }

    /// <summary>用系统默认浏览器打开。<c>UseShellExecute</c> 必须为 true ——
    /// .NET Core 之后不带它的话 <c>Process.Start</c> 会把 URL 当成本地程序路径。</summary>
    public void Open()
    {
        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(Url)
            {
                UseShellExecute = true,
            });
        }
        catch (Exception ex)
        {
            MessageBox.Show("打开链接失败：" + ex.Message + "\n\n" + Url, "错误",
                MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }

    public void ApplyTheme(ThemeColors t)
    {
        ForeColor = t.Accent;
        BackColor = Parent?.BackColor ?? t.SurfaceAlt;
    }
}
