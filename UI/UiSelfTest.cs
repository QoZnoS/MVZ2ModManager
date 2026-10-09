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

        // ---------------- 数值输入：滑条 vs 输入框 ----------------
        {
            // 用例直接取自真实配置里的 range。端口是 1~65535 —— 滑条上一个像素跳好几百，
            // 谁都滑不准；浮点更糟，滑条只能给个近似值，存下去的还不是你想输的那个数。
            var cases = new (string Type, double Min, double Max, bool Box, string Why)[]
            {
                ("Int32",     1, 65535,  true,  "端口 1~65535"),
                ("Int32",   256, 200000, true,  "跨度远超阈值"),
                ("Int32",     0, 3000,   true,  "跨度远超阈值"),
                ("Int32",     0, 100000, true,  "跨度远超阈值"),
                ("Int32",     1, 100000, true,  "跨度远超阈值"),
                ("Byte",      0, 255,    true,  "255 也超过阈值"),
                ("Single",  0.1, 10,     true,  "浮点：滑条只能给近似值"),
                ("Double",    0, 1,      true,  "浮点"),
                ("Int32",     0, 10,     false, "窄整数区间，滑条正合适"),
                ("Int32",     0, 200,    false, "刚好等于阈值上限"),
                ("Int32",     0, 201,    true,  "刚过阈值就换成输入框"),
            };

            var bad = new List<string>();
            foreach (var c in cases)
            {
                bool box = ConfigPanel.WantsNumberBox(c.Type, c.Min, c.Max);
                if (box != c.Box)
                    bad.Add($"{c.Type} {c.Min}~{c.Max}（{c.Why}）：期望{(c.Box ? "输入框" : "滑条")}，实际{(box ? "输入框" : "滑条")}");
            }

            Check("范围超 200 的整数与全部浮点都改用输入框，窄整数区间仍用滑条", bad.Count == 0,
                bad.Count == 0
                    ? $"{cases.Length} 个用例（阈值 {ConfigPanel.SliderMaxSpan}）：{string.Join("、", cases.Select(c => c.Why))}"
                    : string.Join("\n", bad));

            // 数字的写法必须与机器区域设置无关：配置文件里的小数点永远是 "."。
            // 在"小数点是逗号"的系统上按当前文化格式化会写出 "0,35"，BepInEx 读回来就废了。
            var fmt = new (double Value, string Expect)[]
            {
                (0.35, "0.35"), (10, "10"), (0.1, "0.1"), (1250, "1250"), (2.5, "2.5"),
            };
            var fmtBad = fmt.Where(f => ConfigPanel.FormatNumber(f.Value) != f.Expect)
                            .Select(f => $"{f.Value} → \"{ConfigPanel.FormatNumber(f.Value)}\"（期望 \"{f.Expect}\"）").ToList();

            Check("数字写回配置一律用不变文化的小数点", fmtBad.Count == 0,
                fmtBad.Count == 0 ? string.Join("、", fmt.Select(f => $"{f.Value} → {f.Expect}")) : string.Join("\n", fmtBad));

            var parseCases = new (string Text, double? Expect, string Why)[]
            {
                ("8080", 8080, "整数"),
                ("0.35", 0.35, "小数点"),
                ("-1", -1, "负数"),
                (" 42 ", 42, "两边空白"),
                ("abc", null, "不是数字"),
                ("", null, "空"),
            };
            var parseBad = new List<string>();
            foreach (var p in parseCases)
            {
                bool ok = ConfigPanel.TryParseNumber(p.Text, out double v);
                if (p.Expect is null) { if (ok) parseBad.Add($"\"{p.Text}\"（{p.Why}）不该解析成功"); }
                else if (!ok || v != p.Expect) parseBad.Add($"\"{p.Text}\"（{p.Why}）：期望 {p.Expect}，实际 {(ok ? v.ToString("R", System.Globalization.CultureInfo.InvariantCulture) : "解析失败")}");
            }

            Check("数字解析：认小数点、拦非数字", parseBad.Count == 0,
                parseBad.Count == 0 ? $"{parseCases.Length} 个用例" : string.Join("\n", parseBad));

            // 校验规则 —— 这是"错误的值会不会被写进配置文件"的唯一闸门。
            var valCases = new (string Text, bool Int, double Min, double Max, bool Ok, string Why)[]
            {
                ("8080",  true,  1,   65535, true,  "端口正常值"),
                ("1",     true,  1,   65535, true,  "下界含在内"),
                ("65535", true,  1,   65535, true,  "上界含在内"),
                ("0",     true,  1,   65535, false, "小于下界"),
                ("65536", true,  1,   65535, false, "大于上界"),
                ("",      true,  1,   65535, false, "空"),
                ("   ",   true,  1,   65535, false, "只有空白"),
                ("abc",   true,  1,   65535, false, "不是数字"),
                ("3.5",   true,  0,   10,    false, "整数项不许带小数点"),
                ("3.5",   false, 0,   10,    true,  "浮点项可以带"),
                ("0.1",   false, 0.1, 10,    true,  "浮点下界含在内"),
                ("0.05",  false, 0.1, 10,    false, "浮点小于下界"),
                ("NaN",   false, 0,   10,    false, "NaN：TryParse 认它，可它跟 min/max 比永远为假"),
                ("Infinity", false, 0, 10,   false, "无穷大"),
            };

            var valBad = new List<string>();
            foreach (var c in valCases)
            {
                string? got = ConfigPanel.ValidateNumber(c.Text, c.Int, c.Min, c.Max);
                if ((got == null) != c.Ok)
                    valBad.Add($"\"{c.Text}\"（{(c.Int ? "整数" : "浮点")} {c.Min}~{c.Max}，{c.Why}）："
                               + $"期望{(c.Ok ? "通过" : "拦下")}，实际{(got == null ? "通过" : "拦下（" + got + "）")}");
            }

            Check("数值校验：端点含在内、整数项拒小数点、NaN / 无穷显式拦下", valBad.Count == 0,
                valBad.Count == 0 ? $"{valCases.Length} 个用例（含 NaN / Infinity）" : string.Join("\n", valBad));
        }

        // ---------------- 配置页：控件真的按上面的规则长出来 ----------------
        {
            // 上面测的是"判定函数说得对不对"，这里测"控件是不是真照它长的" ——
            // 判定对了但 BuildWidget 没接线，界面上照样是滑条。期望值直接从同一批
            // 配置文件里算出来，所以在任何一台机器上跑都成立，不写死"这台机器上有几个"。
            var fails = new List<string>();
            int files = 0, sliders = 0, boxes = 0;
            var themeBefore = AppState.Settings.Theme;
            bool probed = false;

            try
            {
                using var panel = new ConfigPanel();
                panel.CreateControl();
                _ = panel.Handle;

                foreach (var (name, path) in BepInExConfig.ListConfigFiles())
                {
                    // 面板里的控件树只对"当前选中的那一份"成立
                    if (!panel.SelectFile(path)) { fails.Add($"{name}：面板列表里选不中"); continue; }
                    panel.PerformLayout();
                    files++;

                    var ranged = BepInExConfig.Parse(path).Sections.SelectMany(s => s.Entries)
                                        .Where(e => e.Range != null).ToList();
                    int wantSliders = ranged.Count(e => !ConfigPanel.WantsNumberBox(e.SettingType, e.Range!.Value.Min, e.Range.Value.Max));
                    int wantBoxes = ranged.Count - wantSliders;

                    if (panel.SliderCount != wantSliders)
                        fails.Add($"{name}：滑条 {panel.SliderCount} 个，期望 {wantSliders} 个");
                    if (panel.NumberBoxCount != wantBoxes)
                        fails.Add($"{name}：数值输入框 {panel.NumberBoxCount} 个，期望 {wantBoxes} 个");

                    // 输入框旁边的范围提示得写着这一项真正的范围，不能是别的项的。
                    var wantHints = ranged.Where(e => ConfigPanel.WantsNumberBox(e.SettingType, e.Range!.Value.Min, e.Range.Value.Max))
                                          .Select(e => $"范围 {ConfigPanel.FormatNumber(e.Range!.Value.Min)} ~ {ConfigPanel.FormatNumber(e.Range.Value.Max)}")
                                          .OrderBy(s => s, StringComparer.Ordinal).ToList();
                    var gotHints = panel.RangeHints().Select(l => l.Text).OrderBy(s => s, StringComparer.Ordinal).ToList();
                    if (!wantHints.SequenceEqual(gotHints))
                        fails.Add($"{name}：范围提示 [{string.Join(" / ", gotHints)}]，期望 [{string.Join(" / ", wantHints)}]");

                    sliders += wantSliders;
                    boxes += wantBoxes;
                }

                // 提示文字要和输入框里的数字齐平 —— 差 2 像素看着就是"文字偏下"。
                foreach (var hint in panel.RangeHints())
                {
                    if (hint.Parent?.Controls.OfType<RTextBox>().FirstOrDefault() is not { } box) continue;
                    double hintMid = hint.Top + hint.Height / 2.0;
                    double boxMid = box.Top + box.Height / 2.0;
                    if (Math.Abs(hintMid - boxMid) > 1.0)
                        fails.Add($"范围提示与输入框不齐平：提示中心 {hintMid:0.#}、输入框中心 {boxMid:0.#}（\"{hint.Text}\"）");
                }

                // 切主题后再回到这一页：行是按当前主题重建的，提示色必须跟着走。
                // （配置页的行在 Refresh_ / 换文件时才重建 —— 不重建的话新加的输入框
                //   会留着旧主题的颜色，深色浅色一换就露馅。）
                var probe = themeBefore == ThemeMode.R2Modman ? ThemeMode.Black : ThemeMode.R2Modman;
                ThemeEngine.Apply(probe);
                probed = true;
                panel.Refresh_();
                panel.PerformLayout();

                var wantColor = ThemeEngine.Current.SubText;
                var stale = panel.RangeHints().Select(l => l.ForeColor).Where(c => c != wantColor).ToList();
                if (panel.RangeHints().Count > 0 && stale.Count > 0)
                    fails.Add($"切到 {probe} 后仍有 {stale.Count} 个范围提示没换色（期望 {wantColor.ToArgb():X8}，实际 {stale[0].ToArgb():X8}）");
            }
            catch (Exception ex) { fails.Add(ex.GetType().Name + ": " + ex.Message); }
            finally
            {
                if (probed) ThemeEngine.Apply(themeBefore, AppState.Settings.CustomBackground, AppState.Settings.CustomAccent);
            }

            Check("配置页真的按规则渲染（滑条 / 数值输入框 / 范围提示都对得上）", fails.Count == 0,
                fails.Count == 0
                    ? $"{files} 个配置文件合计：滑条 {sliders} 个、数值输入框 {boxes} 个；提示与输入框齐平、换主题后提示色跟随"
                    : string.Join("\n", fails));
        }

        // ---------------- 填错时的红字得真看得见 ----------------
        {
            // Danger 是为这些输入框新加的配色。照搬深色主题的亮红到浅色背景上、
            // 或者反过来，红字就糊进背景里了 —— 那跟不提示没区别。按 WCAG 算对比度。
            static double Lin(int v)
            {
                double s = v / 255.0;
                return s <= 0.03928 ? s / 12.92 : Math.Pow((s + 0.055) / 1.055, 2.4);
            }
            static double Lum(Color c) => 0.2126 * Lin(c.R) + 0.7152 * Lin(c.G) + 0.0722 * Lin(c.B);
            static double Ratio(Color a, Color b)
            {
                double la = Lum(a), lb = Lum(b);
                return (Math.Max(la, lb) + 0.05) / (Math.Min(la, lb) + 0.05);
            }

            var bad = new List<string>();
            var detail = new List<string>();
            var themes = new (string Name, ThemeColors T)[]
                { ("黑", ThemeEngine.Black()), ("白", ThemeEngine.White()), ("R2Modman", ThemeEngine.R2Modman()) };

            foreach (var (name, t) in themes)
            {
                double r = Ratio(t.Danger, t.SurfaceAlt);
                detail.Add($"{name} {r:0.00}:1");
                if (r < 3.0) bad.Add($"{name}：红字与卡片底只有 {r:0.00}:1");
            }

            Check("填错时的红字在三种主题上都看得见（与卡片底对比度 ≥ 3:1）", bad.Count == 0,
                bad.Count == 0 ? string.Join("、", detail) : string.Join("\n", bad));
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

            // 标题栏那个"游戏安装"菜单：列出的必须正好是已登记的安装，当前那套打勾。
            try
            {
                using var menu = form.BuildInstallMenu();

                var items = menu.Items.OfType<ToolStripMenuItem>().ToArray();
                var rows = items.Where(i => i.Text is not ("游戏安装" or "添加 / 管理安装…")).ToArray();

                Check("标题栏安装菜单列出了每一套已登记安装",
                    rows.Length == AppState.Installations.Count,
                    $"菜单 {rows.Length} 行 / 已登记 {AppState.Installations.Count} 套"
                    + (rows.Length > 0 ? "：" + string.Join("、", rows.Select(r => r.Text)) : ""));

                int ticked = rows.Count(i => i.Checked);
                Check("安装菜单里当前那套是唯一打勾的，并且有管理入口",
                    ticked == (AppState.CurrentInstallation != null ? 1 : 0)
                    && items.Any(i => i.Text == "添加 / 管理安装…" && i.Enabled),
                    $"打勾 {ticked} 行");
            }
            catch (Exception ex)
            {
                Check("标题栏安装菜单", false, ex.GetType().Name + ": " + ex.Message);
            }

            // 换安装时会**整套重建**面板：重建之后必须还能正常切页。
            // 这一步最容易漏接线（面板是新的，标签页/事件都挂在旧对象上）。
            try
            {
                int before = form.ActiveTabIndex;
                form.RebuildPanels();

                bool keptTab = form.ActiveTabIndex == before;
                form.SwitchToTab("设置");
                bool canSwitch = form.ActiveTabIndex == Array.IndexOf(MainForm.TabLabels, "设置");

                Check("整套重建面板之后标签页仍然正常",
                    keptTab && canSwitch,
                    $"重建前第 {before} 页 → 重建后第 {form.ActiveTabIndex} 页，能否切到设置页 = {canSwitch}");
            }
            catch (Exception ex)
            {
                Check("整套重建面板", false, ex.GetType().Name + ": " + ex.Message);
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

            // 关于页：作者主页 + 反馈入口。断言控件树而不是硬编码的字符串列表，
            // 所以改文案不会让这几条变成假绿灯。
            var aboutLinks = settings.AboutLinks;
            var aboutUrls = aboutLinks.Select(l => l.Url).ToArray();

            Check("关于页挂了作者主页与反馈入口两个链接",
                aboutUrls.Length == 2
                && aboutUrls.Contains(SettingsPanel.AuthorUrl)
                && aboutUrls.Contains(SettingsPanel.FeedbackUrl),
                $"实际 {aboutUrls.Length} 个：{string.Join("、", aboutUrls)}");

            Check("关于页链接是可打开的 https 绝对地址，且是手型光标",
                aboutLinks.Length == 2 && aboutLinks.All(l =>
                    Uri.TryCreate(l.Url, UriKind.Absolute, out var uri)
                    && uri.Scheme == Uri.UriSchemeHttps
                    && l.Cursor == Cursors.Hand),
                string.Join("、", aboutUrls));

            // 配色断言必须走一遍"换主题"的真实路径（ThemeChanged → 面板自己的 ApplyTheme）。
            // 只测构造那一刻的颜色会给出假绿灯：之后任何一次改色只要把它刷回正文色，
            // 界面上链接就长得跟普通文字一样，而自检照样全绿。
            //
            // 两个坑都在这一小段里踩过：
            //  1) 探针主题必须与"当前主题"不同，否则 Apply 只是原地踏步，断言自证成功；
            //  2) ForeColor 必须在**探针主题生效期间**读，还原之后再读就成了拿旧主题的颜色
            //     去比新主题的期望值 —— 在任何默认主题不是 R2Modman 的机器上都会假红。
            var themeBefore = AppState.Settings.Theme;
            var probeMode = themeBefore == ThemeMode.R2Modman ? ThemeMode.Black : ThemeMode.R2Modman;

            ThemeEngine.Apply(probeMode);

            var themedLinks = settings.AboutLinks;
            var expectedAccent = ThemeEngine.Current.Accent;
            var observed = themedLinks.Select(l => l.ForeColor).ToArray();

            ThemeEngine.Apply(themeBefore, AppState.Settings.CustomBackground, AppState.Settings.CustomAccent);

            Check("换过主题之后链接仍是强调色（没被当成普通 Label 刷掉）",
                observed.Length == 2 && observed.All(c => c == expectedAccent),
                observed.Length == 2
                    ? $"{themeBefore} → {probeMode} 之后链接色 {observed[0].ToArgb():X8} / 该主题强调色 {expectedAccent.ToArgb():X8}"
                    : "没找到链接");

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
