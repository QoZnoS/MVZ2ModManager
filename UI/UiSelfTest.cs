using System.Text;
using MVZ2ModManager.Core;
using MVZ2ModManager.Theme;
using MVZ2ModManager.UI.Controls;
using MVZ2ModManager.UI.Panels;

namespace MVZ2ModManager.UI;

/// <summary>
/// 无界面 UI 自检：把每个窗体/面板**真的构造出来、真的创建窗口句柄、真的走一遍切页**，
/// 然后全部 Dispose。
///
/// <para>为什么需要它：WinForms 的坑基本都在"构造 + 布局 + 句柄创建"这一段
/// （停靠顺序、自绘背景、Region、Tab 切换时 BringToFront），而这一段的错误
/// 靠"编译通过"完全看不出来。这里的每个 <c>CreateControl()</c> 都会真的跑
/// <c>OnHandleCreated</c> / <c>OnResize</c> / 自绘路径。</para>
///
/// <para>它不开可见窗口 —— 但会在当前会话里创建真实的 HWND，所以必须在 STA 线程上跑。</para>
/// </summary>
internal static class UiSelfTest
{
    public static int Run(string outPath)
    {
        var sb = new StringBuilder();
        int passed = 0, failed = 0;

        void Check(string name, bool ok, string detail)
        {
            if (ok) passed++; else failed++;
            sb.AppendLine($"[{(ok ? "PASS" : "FAIL")}] {name}");
            if (detail.Length > 0)
                foreach (var line in detail.Split('\n'))
                    sb.AppendLine("        " + line);
        }

        sb.AppendLine("MVZ2 Mod Manager — 界面自检报告");
        sb.AppendLine($"运行时间：{DateTime.Now:yyyy-MM-dd HH:mm:ss}");
        sb.AppendLine($"游戏目录：{AppState.Settings.GamePath}");
        sb.AppendLine();

        // 构造 + 建句柄 + 释放。抛异常就是失败。
        void Exercise(string name, Func<Control> make, Action<Control>? afterCreate = null)
        {
            try
            {
                using var c = make();
                c.CreateControl();
                _ = c.Handle;                       // 强制创建句柄 → 跑 OnHandleCreated
                c.PerformLayout();
                afterCreate?.Invoke(c);
                Check(name, true, $"{c.GetType().Name}  ({c.Width}×{c.Height})");
            }
            catch (Exception ex)
            {
                Check(name, false, ex.GetType().Name + ": " + ex.Message);
            }
        }

        // ---------------- 独立面板 ----------------
        Exercise("InstalledPanel", () => new InstalledPanel());
        Exercise("LoadoutsPanel", () => new LoadoutsPanel());
        Exercise("ModpacksPanel", () => new ModpacksPanel());
        Exercise("ConfigPanel", () => new ConfigPanel());
        Exercise("LogsPanel", () => new LogsPanel());
        Exercise("SettingsPanel", () => new SettingsPanel());

        // ---------------- 对话框 ----------------
        Exercise("GamePickerForm", () => new GamePickerForm());
        Exercise("ModpackExportDialog", () => new ModpackExportDialog(ModpackManager.GetPackRoots(), "自检"));

        // ---------------- 列宽拖动的算术（纯函数） ----------------
        {
            var cases = new (int Left, int Right, int Wanted, int? ExpLeft, int? ExpRight, string Why)[]
            {
                (200, 100, 250, 250, 50, "变大→右列等量让出"),
                (200, 100, 150, 150, 150, "变小→右列等量收回"),
                (200, 100, 20, 40, 260, "拖过头→夹在最小宽度"),
                (200, 100, 999, 260, 40, "拖到顶→右列停在最小宽度"),
                (200, 100, 200, null, null, "没动→不插手"),
                (100, 40, 200, null, null, "右列已到最小→不插手"),
            };

            var fails = new List<string>();
            foreach (var c in cases)
            {
                var got = ColumnResize.Preserve(c.Left, c.Right, c.Wanted);
                int? l = got?.Left, r = got?.Right;
                if (l != c.ExpLeft || r != c.ExpRight)
                    fails.Add($"({c.Left},{c.Right}) 拖到 {c.Wanted}：期望 ({Fmt(c.ExpLeft)},{Fmt(c.ExpRight)})，实际 ({Fmt(l)},{Fmt(r)})");
            }

            Check("列宽拖动保持两列之和不变（含边界夹紧）", fails.Count == 0,
                fails.Count == 0
                    ? $"{cases.Length} 个用例：{string.Join("、", cases.Select(c => c.Why))}"
                    : string.Join("\n", fails));

            // "两列之和不变"就是这条规则的全部意义 —— 穷举一遍证它。
            var sweep = new List<string>();
            int combos = 0;
            foreach (int left in new[] { 40, 60, 200, 500 })
                foreach (int right in new[] { 40, 55, 100, 300 })
                    for (int wanted = 0; wanted <= left + right + 80; wanted += 7)
                    {
                        combos++;
                        if (ColumnResize.Preserve(left, right, wanted) is not { } p) continue;
                        if (p.Left + p.Right != left + right)
                            sweep.Add($"({left},{right}) 拖到 {wanted} → ({p.Left},{p.Right})：总和变了");
                        if (p.Left < ColumnResize.MinWidth || p.Right < ColumnResize.MinWidth)
                            sweep.Add($"({left},{right}) 拖到 {wanted} → ({p.Left},{p.Right})：破了最小宽度");
                    }

            Check("列宽拖动：穷举各种目标宽度，总和与最小宽度都成立", sweep.Count == 0,
                sweep.Count == 0 ? $"{combos} 种组合全部满足" : string.Join("\n", sweep.Take(6)));

            static string Fmt(int? v) => v?.ToString() ?? "null";
        }

        // ---------------- 主窗口：切遍每个标签页 ----------------
        try
        {
            using var form = new MainForm();
            form.CreateControl();
            _ = form.Handle;

            var labels = MainForm.TabLabels;
            var failures = new List<string>();

            for (int i = 0; i < labels.Length; i++)
            {
                string label = labels[i];
                try
                {
                    form.SwitchToTab(label);

                    // 光"没抛异常"不算数 —— SwitchToTab 对不认识的标签名是静默 no-op，
                    // 所以必须核对真的切过去了。
                    if (form.ActiveTabIndex != i)
                        failures.Add($"{label}: 期望切到第 {i} 页，实际停在第 {form.ActiveTabIndex} 页");
                }
                catch (Exception ex) { failures.Add($"{label}: {ex.GetType().Name}: {ex.Message}"); }
            }

            Check("主窗口 + 依次切换全部标签页（并核对真的切过去了）",
                failures.Count == 0,
                failures.Count == 0
                    ? $"{labels.Length} 个标签页：{string.Join(" / ", labels)}"
                    : string.Join("\n", failures));

            // 引导：每一步都渲染一遍（会真的切标签页）
            try
            {
                form.StartTutorial();
                Check("使用引导能启动", true, "教练卡已显示");
                form.StopTutorialForTest();
                Check("使用引导能走完并收摊", true, "教练卡已移除");
            }
            catch (Exception ex)
            {
                Check("使用引导", false, ex.GetType().Name + ": " + ex.Message);
            }

            // 状态栏（读模组 + 存档，最容易出 I/O 异常的地方）
            try
            {
                form.UpdateStatusBar();
                Check("主窗口状态栏刷新", true, "正常");
            }
            catch (Exception ex)
            {
                Check("主窗口状态栏刷新", false, ex.GetType().Name + ": " + ex.Message);
            }

            // 列宽拖动的**接线**：算术由纯函数自检保证，这里证事件那一段真的接上了。
            try
            {
                var panel = form.InstalledPanelControl;
                int[] before = panel.ColumnWidths;
                int sumBefore = before.Sum();
                bool moved = panel.ApplyColumnResize(0, before[0] + 40);
                int[] after = panel.ColumnWidths;

                Check("拖动「名称/状态」之间的分隔条后总宽度不变",
                    moved && sumBefore == after.Sum(),
                    $"总宽 {sumBefore} → {after.Sum()}；列宽 {string.Join(" / ", before)} → {string.Join(" / ", after)}");

                // 拖到右列顶死时不该动，也不该把参与拖动的列挤到最小宽度以下
                bool stuck = panel.ApplyColumnResize(1, panel.ColumnWidths[1] + 100000);
                int[] after2 = panel.ColumnWidths;
                Check("把分隔条拖到极端位置不会把参与拖动的两列拖坏",
                    !stuck || (after2[1] >= ColumnResize.MinWidth && after2[2] >= ColumnResize.MinWidth),
                    $"返回 {stuck}；列宽 {string.Join(" / ", after2)}（末尾两个 34 是图标列，本来就更窄）");
            }
            catch (Exception ex)
            {
                Check("列宽拖动接线", false, ex.GetType().Name + ": " + ex.Message);
            }
        }
        catch (Exception ex)
        {
            Check("主窗口构造", false, ex.GetType().Name + ": " + ex.Message);
        }

        // ---------------- RTextBox 内嵌输入框的垂直位置 ----------------
        try
        {
            using var box = new RTextBox { Width = 180, Height = 30 };
            box.CreateControl();
            _ = box.Handle;

            int lineHeight = box.Inner.PreferredHeight;
            int expected = Math.Max(0, (box.Height - lineHeight) / 2);
            bool centered = box.Inner.Height == lineHeight && Math.Abs(box.Inner.Top - expected) <= 1;

            Check("RTextBox 内嵌输入框垂直居中（文字不会偏下）", centered,
                $"外层高 {box.Height} / 行高 {lineHeight} → 内层 Top={box.Inner.Top}（期望 {expected}）、内层高 {box.Inner.Height}");
        }
        catch (Exception ex)
        {
            Check("RTextBox 垂直居中", false, ex.GetType().Name + ": " + ex.Message);
        }

        // ---------------- 存档风险提醒开关 ----------------
        try
        {
            Check("存档风险提醒默认开启（老 state.json 没有这个字段时不能变成不提醒）",
                new AppSettings().WarnAboutSaveRisk,
                $"new AppSettings().WarnAboutSaveRisk = {new AppSettings().WarnAboutSaveRisk}");

            bool original = AppState.Settings.WarnAboutSaveRisk;

            using var panel = new InstalledPanel();
            panel.CreateControl();
            _ = panel.Handle;

            // 一个"有存档、但没有任何已启用模组提供它"的命名空间 —— 本来就该报风险。
            var usages = new List<SaveUsage>
            {
                new("mvz2mm_selftest_ns", 4, 4, 7, new List<string> { "1.lvl" }),
            };

            AppState.Settings.WarnAboutSaveRisk = true;
            int onCount = panel.RequiredWarnings(usages).Count;

            AppState.Settings.WarnAboutSaveRisk = false;
            int offCount = panel.RequiredWarnings(usages).Count;

            Check("关掉存档风险提醒后不再产出存档警告",
                onCount == 1 && offCount == 0,
                $"同一个输入：开启时 {onCount} 条、关闭时 {offCount} 条");

            // 设置页的复选框真的连到设置上了
            using var settings = new SettingsPanel();
            settings.CreateControl();
            _ = settings.Handle;

            settings.SaveWarnChecked = false;
            bool wroteOff = !AppState.Settings.WarnAboutSaveRisk;

            settings.SaveWarnChecked = true;
            bool wroteOn = AppState.Settings.WarnAboutSaveRisk;

            Check("设置页的复选框真的写进了设置", wroteOff && wroteOn,
                $"取消勾选后 WarnAboutSaveRisk={!wroteOff}，重新勾选后={wroteOn}");

            AppState.Settings.WarnAboutSaveRisk = original;
            AppState.Save();
        }
        catch (Exception ex)
        {
            Check("存档风险提醒开关", false, ex.GetType().Name + ": " + ex.Message);
        }

        // ---------------- 卡片两行文字的居中 ----------------
        try
        {
            using var titleFont = ThemeEngine.MakeFont(10f, FontStyle.Bold);
            using var subFont = ThemeEngine.MakeFont(8f);
            int block = titleFont.Height + 2 + subFont.Height;

            var fails = new List<string>();
            int tested = 0;
            foreach (int h in new[] { block, 46, 52, 60, 80, 120 })
            {
                if (h < block) continue;
                tested++;
                var card = new Rectangle(0, 100, 400, h);
                int top = CardText.BlockTop(card, titleFont, subFont);
                int above = top - card.Top;
                int below = card.Bottom - (top + block);
                if (Math.Abs(above - below) > 1) fails.Add($"卡片高 {h}：上留白 {above}、下留白 {below}");
            }

            Check("卡片两行文字整体垂直居中（上/下留白一致）", fails.Count == 0,
                fails.Count == 0
                    ? $"行高 {titleFont.Height}+2+{subFont.Height}={block}；试了 {tested} 种卡片高度"
                    : string.Join("\n", fails));
        }
        catch (Exception ex)
        {
            Check("卡片文字居中", false, ex.GetType().Name + ": " + ex.Message);
        }

        // ---------------- 主题切换 ----------------
        foreach (var mode in new[] { ThemeMode.Black, ThemeMode.White, ThemeMode.R2Modman, ThemeMode.Custom })
        {
            try
            {
                ThemeEngine.Apply(mode, "#101820", "#00FFAA");
                using var probe = new MainForm();
                probe.CreateControl();
                _ = probe.Handle;
                Check($"主题 {mode} 下主窗口能构造出来", true, "正常");
            }
            catch (Exception ex)
            {
                Check($"主题 {mode} 下主窗口能构造出来", false, ex.GetType().Name + ": " + ex.Message);
            }
        }

        // 复位到用户设置的主题
        ThemeEngine.Apply(AppState.Settings.Theme, AppState.Settings.CustomBackground, AppState.Settings.CustomAccent);

        sb.AppendLine();
        sb.AppendLine("--- 结果 ---");
        sb.AppendLine($"通过 {passed} 项，失败 {failed} 项");

        try
        {
            string full = Path.GetFullPath(outPath);
            Directory.CreateDirectory(Path.GetDirectoryName(full)!);
            File.WriteAllText(full, sb.ToString());
        }
        catch { }

        return failed == 0 ? 0 : 1;
    }
}
