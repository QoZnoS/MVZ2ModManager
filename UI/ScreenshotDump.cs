using System.Drawing.Imaging;
using MVZ2ModManager.Core;
using MVZ2ModManager.Theme;

namespace MVZ2ModManager.UI;

/// <summary>
/// 开发用：把每一个标签页画成 PNG，方便**肉眼**核对版面。
///
/// <para>用法：<c>MVZ2ModManager.exe --screenshot [输出目录]</c>。</para>
///
/// <para>存在的理由：<see cref="UiSelfTest"/> 能证明"控件构造得出来、句柄建得起来"，
/// 但证明不了"文字有没有偏下、超宽有没有省略号、列宽拖起来对不对" —— 这些只有看图。
/// 窗口会被挪到屏幕外面再截，所以不会闪。</para>
/// </summary>
internal static class ScreenshotDump
{
    private static readonly (string Tab, string File)[] Tabs =
    [
        ("已安装", "01-installed"),
        ("快照点", "02-loadouts"),
        ("模组包", "03-modpacks"),
        ("配置", "04-config"),
        ("日志", "05-logs"),
        ("设置", "06-settings"),
    ];

    public static int Run(string outDir)
    {
        var log = new List<string>();
        try
        {
            Directory.CreateDirectory(outDir);
            log.Add($"界面截图：{DateTime.Now:yyyy-MM-dd HH:mm:ss}");
            log.Add($"游戏目录：{AppState.Settings.GamePath}");
            log.Add($"字体：{ThemeEngine.UiFontFamily}");
            log.Add("");

            using var form = new MainForm();
            form.StartPosition = FormStartPosition.Manual;
            form.Location = new Point(-8000, -8000);   // 挪到屏幕外，避免闪一下
            form.Show();

            Settle(1800);   // 存档扫描在后台线程上，第一张多等一会儿

            foreach (var (tab, file) in Tabs)
            {
                form.SwitchToTab(tab);
                Settle(500);
                log.Add(Capture(form, Path.Combine(outDir, file + ".png"), file));

                // 控件树：截图只能看"画出来什么样"，看不出"某个分组到底有没有进版面"。
                var tree = new List<string> { $"标签页 {tab} 的可见控件树（类型 [左,上 宽x高] 文本）", "" };
                foreach (Control page in form.Controls) DumpTree(page, tree);
                File.WriteAllLines(Path.Combine(outDir, $"controls-{file}.txt"), tree);
            }

            log.Add("已安装页列宽：" + form.InstalledPanelControl.ColumnWidthReport);

            form.SwitchToTab("已安装");
            Settle(300);
            form.StartTutorial();
            Settle(400);
            log.Add(Capture(form, Path.Combine(outDir, "07-tutorial.png"), "07-tutorial"));

            form.Close();
            File.WriteAllLines(Path.Combine(outDir, "screenshot.txt"), log);
            return 0;
        }
        catch (Exception ex)
        {
            log.Add("失败：" + ex);
            try
            {
                Directory.CreateDirectory(outDir);
                File.WriteAllLines(Path.Combine(outDir, "screenshot.txt"), log);
            }
            catch { /* 连日志都写不进去就没辙了 */ }
            return 1;
        }
    }

    /// <summary>把可见控件树写进日志 —— 截图看得见"画成什么样"，看不见"某个分组有没有进版面"。</summary>
    private static void DumpTree(Control root, List<string> log, int depth = 0)
    {
        if (depth > 8) return;

        foreach (Control c in root.Controls)
        {
            if (!c.Visible || c.Width <= 0 || c.Height <= 0) continue;

            string text = c.Text.Replace('\r', ' ').Replace('\n', ' ');
            if (text.Length > 64) text = text[..64] + "…";
            log.Add($"{new string(' ', depth * 2)}{c.GetType().Name} [{c.Left},{c.Top} {c.Width}x{c.Height}]"
                    + (text.Length > 0 ? "  \"" + text + "\"" : ""));

            DumpTree(c, log, depth + 1);
        }
    }

    /// <summary>一边抽消息队列一边等 —— 不抽队列的话控件根本不会重绘。</summary>
    private static void Settle(int ms)
    {
        long until = Environment.TickCount64 + ms;
        while (Environment.TickCount64 < until)
        {
            Application.DoEvents();
            Thread.Sleep(15);
        }
    }

    private static string Capture(Form form, string path, string label)
    {
        var size = form.ClientSize;
        if (size.Width <= 0 || size.Height <= 0) return $"{label}: 客户区尺寸为 0，跳过";

        using var bmp = new Bitmap(size.Width, size.Height);
        form.DrawToBitmap(bmp, new Rectangle(Point.Empty, size));
        bmp.Save(path, ImageFormat.Png);
        return $"{label}: {size.Width}×{size.Height} → {Path.GetFileName(path)}";
    }
}
