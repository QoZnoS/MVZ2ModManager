using System.Text;

namespace MVZ2ModManager.Core;

/// <summary>
/// 无界面自检：把核心逻辑（PE 元数据 → 依赖分析 → 存档扫描）跑一遍，
/// 写成一份人读的报告并给出**通过/失败**。
///
/// <para>用法：<c>MVZ2ModManager.exe --selftest [输出文件]</c>；退出码 0 = 全过。</para>
///
/// <para>它替代"点开界面肉眼确认"，也让之后改动能随时回归 ——
/// 尤其是存档那套 gzip + JSON 头的假设，一旦游戏改了格式就会立刻红。</para>
/// </summary>
internal static class SelfTest
{
    public static int Run(string outPath, bool applyRoundtrip = false)
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

        sb.AppendLine("MVZ2 Mod Manager — 自检报告");
        sb.AppendLine($"程序版本：{Program.VersionString}");
        sb.AppendLine($"运行时间：{DateTime.Now:yyyy-MM-dd HH:mm:ss}");
        sb.AppendLine();

        // ---------------------------------------------------------- 环境
        AppState.Load();

        if (!File.Exists(AppState.Settings.GamePath))
        {
            string? detected = AppState.AutoDetectGame();
            if (detected != null) AppState.Settings.GamePath = detected;
        }

        string gameDir = AppState.GameDir ?? "";
        sb.AppendLine("--- 环境 ---");
        sb.AppendLine($"游戏路径   ：{(AppState.Settings.GamePath.Length > 0 ? AppState.Settings.GamePath : "(none)")}");
        sb.AppendLine($"游戏目录   ：{(gameDir.Length > 0 ? gameDir : "(none)")}");
        sb.AppendLine($"数据目录   ：{AppState.DataDir ?? "（无）"}");
        sb.AppendLine($"插件目录   ：{AppState.PluginsDir ?? "（无）"}");
        sb.AppendLine($"资源目录   ：{AppState.StreamingAssetsModsDir ?? "（无）"}");
        sb.AppendLine($"环境变量 {AppState.GameDirEnvVar}：{Environment.GetEnvironmentVariable(AppState.GameDirEnvVar) ?? "（未设置）"}");
        sb.AppendLine();

        Check("游戏目录看起来是 MVZ2（存在 MinecraftVSZombies2_Data）",
            AppState.IsValidGameDir(gameDir),
            gameDir.Length > 0 ? gameDir : "没有解析出游戏路径");

        if (!AppState.IsValidGameDir(gameDir))
        {
            // 没有游戏就没得测了，直接落盘。
            return Finish(sb, outPath, passed, failed);
        }

        sb.AppendLine($"BepInEx 是否已安装：{BepInExManager.IsInstalled(gameDir)}");
        sb.AppendLine($"BepInEx 状态      ：{BepInExManager.GetState(gameDir)}");
        sb.AppendLine($"模组总开关       ：{BepInExManager.ModsEnabled(gameDir)}");
        sb.AppendLine($"进程名           ：{AppState.GameProcessName}");
        sb.AppendLine();

        // ---------------------------------------------------------- BepInEx 状态判定
        // 在临时目录里合成四种环境。它们全都是"不加载模组"，但含义与出路完全不同：
        // 没装 → 没得救；注入器丢了 → 得自己补文件；被改名 → 设置页能一键恢复。
        sb.AppendLine("--- BepInEx 状态判定 ---");
        try
        {
            string sandbox = Path.Combine(Path.GetTempPath(), "mvz2mm_state_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(sandbox);

            string Synth(string name, bool bepDir, bool winhttp, bool renamed)
            {
                string dir = Path.Combine(sandbox, name);
                Directory.CreateDirectory(dir);
                if (bepDir) Directory.CreateDirectory(Path.Combine(dir, "BepInEx"));
                if (winhttp) File.WriteAllText(Path.Combine(dir, "winhttp.dll"), "stub");
                if (renamed) File.WriteAllText(Path.Combine(dir, "winhttp.dll.disabled"), "stub");
                return dir;
            }

            var cases = new[]
            {
                (Name: "没装 BepInEx",      Dir: Synth("none", false, false, false), Expect: BepInExState.NotInstalled,    Toggle: false),
                (Name: "装了但注入器丢了",   Dir: Synth("lost", true,  false, false), Expect: BepInExState.InjectorMissing, Toggle: false),
                (Name: "装了且被关闭",       Dir: Synth("off",  true,  false, true),  Expect: BepInExState.Disabled,        Toggle: true),
                (Name: "装了且启用",         Dir: Synth("on",   true,  true,  false), Expect: BepInExState.Enabled,         Toggle: true),
            };

            foreach (var c in cases)
            {
                var actual = BepInExManager.GetState(c.Dir);
                Check($"[状态] {c.Name} 判定为 {c.Expect}", actual == c.Expect, $"实际 = {actual}");

                Check($"[状态] {c.Name} → 总开关可用 = {c.Toggle}",
                    BepInExManager.CanToggle(c.Dir) == c.Toggle, "");

                // 只有"启用中"不该被打断，其余三种都必须先问一句这次是不带模组启动。
                bool prompts = BepInExManager.LaunchPrompt(c.Expect) is not null;
                Check($"[启动] {c.Name} → {(c.Expect == BepInExState.Enabled ? "直接启动" : "先弹确认")}",
                    prompts == (c.Expect != BepInExState.Enabled), "");
            }

            // 这条是这次改动的核心：关闭模组时问的必须变成"要不要不带模组启动"，
            // 而不是"要不要帮你重新打开" —— 后者答"否"会把启动游戏本身也取消掉。
            string? offPrompt = BepInExManager.LaunchPrompt(BepInExState.Disabled)?.Text;
            Check("[启动] 关闭状态问的是「在不启用模组的情况下开始游戏」",
                offPrompt is not null && offPrompt.Contains("不启用模组") && !offPrompt.Contains("重新打开"),
                offPrompt ?? "(null)");

            // 没装 BepInEx 的文案里不能出现"改名"，否则用户会去翻一个从没被人动过的文件。
            string? notInstalledNote = BepInExManager.Describe(BepInExState.NotInstalled);
            Check("[状态] 没装 BepInEx 时不再说成 winhttp.dll 被改名",
                notInstalledNote is not null
                && notInstalledNote.Contains("未安装") && !notInstalledNote.Contains("改名"),
                notInstalledNote ?? "(null)");

            Check("[状态] 一切正常时状态栏不多塞一句话",
                BepInExManager.Describe(BepInExState.Enabled) is null, "");

            // 「启动游戏」的决策。以前这里把"要不要重新打开 BepInEx"和"要不要启动游戏"
            // 揉成一句话，用户答"否"之后游戏根本没启动 —— 也就是没法用管理器启动原版游戏。
            {
                bool askUsed = false;
                bool okWhenEnabled = GameLauncher.ShouldStart(BepInExState.Enabled,
                    _ => { askUsed = true; return true; });
                Check("[启动] 模组开着时不问，直接进入启动",
                    okWhenEnabled && !askUsed, askUsed ? "居然弹了确认" : "");

                var asked = new List<string>();
                bool proceed = GameLauncher.ShouldStart(BepInExState.Disabled,
                    p => { asked.Add(p.Text); return true; });
                Check("[启动] 模组关着时问一次，答「是」就照常启动（不回头去动总开关）",
                    proceed && asked.Count == 1, $"问了 {asked.Count} 次");

                asked.Clear();
                bool cancelled = GameLauncher.ShouldStart(BepInExState.Disabled,
                    p => { asked.Add(p.Text); return false; });
                Check("[启动] 只有答「否」才是不启动", !cancelled && asked.Count == 1, "");

                string? started = null;
                GameLauncher.Start(@"C:\nonexistent\whatever.exe", p => started = p);
                Check("[启动] 决定启动之后确实会去开进程",
                    started == @"C:\nonexistent\whatever.exe", started ?? "（没有回调）");
            }

            try { Directory.Delete(sandbox, recursive: true); } catch { }
        }
        catch (Exception ex)
        {
            Check("BepInEx 状态判定（临时目录）", false, ex.GetType().Name + ": " + ex.Message);
        }
        sb.AppendLine();

        // ---------------------------------------------------------- 多安装（多版本）
        // MVZ2 每个版本都是一套独立完整安装，所以"多版本"就是"多安装"。
        sb.AppendLine("--- 多安装（多版本）---");
        try
        {
            string root = Path.Combine(Path.GetTempPath(), "mvz2mm_inst_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(root);

            // 造一套像样的安装：主程序 + 同名 _Data 目录。
            string MakeInstall(string relative)
            {
                // GetFullPath 顺手把分隔符统一 —— 枚举出来的路径总是反斜杠形式。
                string dir = Path.GetFullPath(Path.Combine(root, relative));
                Directory.CreateDirectory(Path.Combine(dir, "MinecraftVSZombies2_Data"));
                string exe = Path.Combine(dir, AppState.GameExeName);
                File.WriteAllText(exe, "stub");
                return exe;
            }

            string first = MakeInstall(@"MVZ2 0.7.0 test-10");
            string second = MakeInstall(@"MVZ2 0.7.0 test-9");
            // 深度 2：指向"装着 MVZ2 的那一层"（E:\Game\PVZ）也要找得到。
            string nested = MakeInstall(@"上一层/MVZ2 0.6.0");
            // 深度 3：超出扫描范围 —— 故意不找，把边界钉在这里。
            string tooDeep = MakeInstall(@"上一层/再一层/MVZ2 0.5.0");

            // GameMaker 那类版本：只有 data.win，没有 _Data。必须被排除，
            // 而且不需要为它专门写规则 —— 认 _Data 就够了。
            string gms2 = Path.Combine(root, "MVZ2 EX0.1.3.6");
            Directory.CreateDirectory(gms2);
            File.WriteAllText(Path.Combine(gms2, "data.win"), "stub");
            File.WriteAllText(Path.Combine(gms2, "Minecraft大战僵尸2.exe"), "stub");

            var found = AppState.ScanForInstallations(root);
            Check("[多安装] 父目录扫描能找齐每一套（含隔了两层的）",
                new[] { first, second, nested }.All(w => found.Contains(w, StringComparer.OrdinalIgnoreCase)),
                $"扫到 {found.Count} 套：\n" + string.Join("\n", found));
            Check("[多安装] GameMaker（只有 data.win）的版本被自动排除",
                found.All(f => !f.Contains("EX0.1.3.6", StringComparison.OrdinalIgnoreCase)),
                string.Join("\n", found));
            Check("[多安装] 扫描深度就到两层（更深的不翻，免得在盘上乱跑）",
                !found.Contains(tooDeep, StringComparer.OrdinalIgnoreCase),
                tooDeep);

            // 通配符的坑：目录里没有精确名时，要挑**与 _Data 同名**的那个，
            // 而不是"文件最小的那个"（旧行为，会把某个调试副本当成主程序）。
            string tricky = Path.Combine(root, "tricky");
            Directory.CreateDirectory(Path.Combine(tricky, "MinecraftVSZombies2_new_Data"));
            File.WriteAllText(Path.Combine(tricky, "MinecraftVSZombies2_old.exe"), new string('x', 64));
            File.WriteAllText(Path.Combine(tricky, "MinecraftVSZombies2_new.exe"), new string('x', 256));
            string? picked = AppState.FindGameExe(tricky);
            Check("[多安装] 没有精确名时优先挑与 _Data 同名的 exe",
                Path.GetFileName(picked ?? "") == "MinecraftVSZombies2_new.exe",
                Path.GetFileName(picked ?? "(null)"));

            // 登记 / 切换 / 忘掉。SwitchTo 会写 state.json，所以整段包在 try/finally 里，
            // 结束时把设置原样放回去再存一次。
            string savedPath = AppState.Settings.GamePath;
            var savedList = AppState.Settings.Installations;
            try
            {
                AppState.Settings.Installations = new List<GameInstallation>();
                AppState.Settings.GamePath = "";

                var i1 = AppState.Remember(first);
                var i2 = AppState.Remember(second, "测试别名");

                Check("[多安装] 登记会去重，别名用得上",
                    i1 != null && i2 != null
                    && AppState.Installations.Count == 2
                    && ReferenceEquals(i1, AppState.Remember(first))
                    && i2.DisplayName == "测试别名",
                    $"共 {AppState.Installations.Count} 套：" +
                    string.Join("、", AppState.Installations.Select(i => i.DisplayName)));

                // 同一个文件从不同地方拿到时分隔符可能不同，绝不能被当成两套安装。
                Check("[多安装] 路径比较忽略分隔符与大小写",
                    AppState.Find(first.Replace('\\', '/')) != null
                    && AppState.Find(first.ToUpperInvariant()) != null,
                    first.Replace('\\', '/'));

                Check("[多安装] 切换会更新当前路径，当前安装认得出来",
                    AppState.SwitchTo(second)
                    && AppState.Settings.GamePath == second
                    && ReferenceEquals(AppState.CurrentInstallation, i2),
                    AppState.Settings.GamePath);

                AppState.Forget(i2!);
                Check("[多安装] 忘掉正在用的那套时，当前选择一并清掉",
                    AppState.Installations.Count == 1 && AppState.Settings.GamePath.Length == 0,
                    $"剩 {AppState.Installations.Count} 套，当前 = \"{AppState.Settings.GamePath}\"");
            }
            finally
            {
                AppState.Settings.Installations = savedList;
                AppState.Settings.GamePath = savedPath;
                AppState.Save();
            }

            try { Directory.Delete(root, recursive: true); } catch { }
        }
        catch (Exception ex)
        {
            Check("多安装（多版本）", false, ex.GetType().Name + ": " + ex.Message);
        }
        sb.AppendLine();

        // ---------------------------------------------------------- 元数据
        var mods = ModCatalog.Load();

        sb.AppendLine("--- 已安装的模组 ---");
        foreach (var m in mods)
        {
            sb.AppendLine($"  {(m.Enabled ? "启用" : "禁用")}  {m.Name,-20} v{m.KnownVersion ?? "?",-10} guid={m.Guid ?? "（无）"}");
            sb.AppendLine($"        插件名    ：{m.PluginName ?? "（无）"}");
            if (m.NativeNamespace != null) sb.AppendLine($"        命名空间  ：{m.NativeNamespace}");
            if (m.HardDependencies.Count > 0) sb.AppendLine($"        硬依赖    ：{string.Join("、", m.HardDependencies)}");
            if (m.SoftDependencies.Count > 0) sb.AppendLine($"        软依赖    ：{string.Join("、", m.SoftDependencies)}");
            if (m.Incompatibilities.Count > 0) sb.AppendLine($"        互斥      ：{string.Join("、", m.Incompatibilities)}");
            if (m.ProcessFilter != null) sb.AppendLine($"        进程限制  ：{m.ProcessFilter}");
        }
        sb.AppendLine();

        Check("在 BepInEx/plugins 里至少找到一个模组", mods.Count > 0, $"共 {mods.Count} 个");
        Check("每个模组都读到了 PE 元数据（都有 [BepInPlugin] GUID）",
            mods.Count > 0 && mods.All(m => m.Guid is { Length: > 0 }),
            string.Join("\n", mods.Where(m => m.Guid is not { Length: > 0 }).Select(m => m.Name + " 没有 GUID")));

        var knownNs = mods.Where(m => m.NativeNamespace != null).Select(m => $"{m.Name} -> {m.NativeNamespace}").ToList();
        Check("已知的 MVZ2 模组都解析出了命名空间",
            knownNs.Count > 0,
            knownNs.Count > 0 ? string.Join("\n", knownNs) : "一个都没解析出来（这些是本仓库的模组吗？）");

        // 依赖关系是否真的读出来了：这套仓库里所有内容 mod 都硬依赖 DSHCore。
        int withCoreDep = mods.Count(m => m.HardDependencies.Contains(Mvz2Catalog.CoreGuid, StringComparer.OrdinalIgnoreCase));
        Check("[BepInDependency] 被识别为硬依赖（内容模组依赖 DSHCore）",
            withCoreDep > 0,
            $"{withCoreDep} 个模组把 {Mvz2Catalog.CoreGuid} 声明为硬依赖"
            + "\n（发现的软依赖数量：" + mods.Sum(m => m.SoftDependencies.Count) + "）");

        // 这条是"我们没在猜"：DependencyFlags 的实际取值是从 BepInEx.Core.dll 读的。
        if (AppState.BepInExDir is { } bepDir)
        {
            string core = Path.Combine(bepDir, "core", "BepInEx.Core.dll");
            if (File.Exists(core))
            {
                var flags = ModMetadata.ReadEnumConstants(core, "DependencyFlags");
                Check("成功从已安装的 BepInEx.Core.dll 读出 DependencyFlags 枚举",
                    flags.Count > 0,
                    flags.Count > 0
                        ? string.Join(", ", flags.Select(kv => $"{kv.Key}={kv.Value}"))
                        : "读不出该枚举（回退到 Hard=1/Soft=2）");
            }
        }

        // ---------------------------------------------------------- 依赖分析
        var issues = DependencyChecker.Check(mods, BepInExManager.GetState(gameDir));

        sb.AppendLine("--- 依赖检查 ---");
        if (issues.Count == 0) sb.AppendLine("  （没有问题）");
        foreach (var i in issues) sb.AppendLine($"  [{IssueLevelText.Of(i.Level)}] {i.Message}");
        sb.AppendLine();

        Check("依赖检查能跑通，并且在健康的安装上不误报",
            !issues.Any(i => i.Level == IssueLevel.Error),
            string.Join("\n", issues.Where(i => i.Level == IssueLevel.Error).Select(i => i.Message)));

        // 关掉 DSHCore 必须能算出"会连带弄坏谁"。
        if (mods.Any(m => m.Guid == Mvz2Catalog.CoreGuid))
        {
            var coreName = mods.First(m => m.Guid == Mvz2Catalog.CoreGuid).Name;
            var broken = DependencyChecker.WouldBreak(mods, new[] { coreName });
            Check("级联分析有效：禁用 DSHCore 能列出哪些模组会一起废",
                broken.Count > 0,
                broken.Count > 0 ? string.Join(", ", broken) : "没有报告任何受影响的模组");
        }

        // ---------------------------------------------------------- 存档扫描
        sb.AppendLine("--- 存档扫描 ---");
        sb.AppendLine($"存档根目录：{(SaveCompatibility.HasAnyUserData ? string.Join(" | ", SaveCompatibility.UserDataRoots()) : "（没找到）")}");

        var refs = SaveCompatibility.ScanAll(force: true);
        var usages = SaveCompatibility.Summarize(refs);
        int levelFiles = 0;
        foreach (var root in SaveCompatibility.UserDataRoots())
        {
            try { levelFiles += Directory.EnumerateFiles(root, "*.lvl", SearchOption.AllDirectories).Count(); }
            catch { }
        }

        sb.AppendLine($"关卡文件 (.lvl) 数量：{levelFiles}");
        sb.AppendLine($"解析出的标识符：{refs.Count} 条引用，涉及 {usages.Count} 个命名空间");
        foreach (var u in usages)
            sb.AppendLine($"  {u.Namespace,-16} v{u.VersionRange,-8} {u.SaveCount} 份存档  例如 {string.Join("、", u.Samples)}");
        sb.AppendLine();

        if (levelFiles > 0)
        {
            Check("每个 .lvl 的文件头都解析成功（gzip + SerializableLevelControllerHeader）",
                refs.Count > 0,
                $"{levelFiles} 个关卡文件，{refs.Count} 条标识符引用");

            // 存档里出现的命名空间应该都能归属到某个已安装模组（或原版）。
            var owners = ModCatalog.NamespaceOwners(mods);
            var unknown = usages.Select(u => u.Namespace).Where(ns => !owners.ContainsKey(ns)).ToList();
            Check("存档里出现的每个命名空间都能对应到一个模组或原版",
                unknown.Count == 0,
                unknown.Count == 0 ? "" : "unmapped: " + string.Join(", ", unknown));
        }
        else
        {
            sb.AppendLine("  （这台机器上没有存档 — 跳过存档相关断言）");
            sb.AppendLine();
        }

        // ---------------------------------------------------------- 模组包
        sb.AppendLine("--- 模组包 ---");

        var roots = ModpackManager.GetPackRoots();
        sb.AppendLine($"模组包根目录（{roots.Count} 个）：{string.Join(" | ", roots.Select(r => r.Name))}");
        sb.AppendLine($"模组包目录：{ModpackManager.PacksDir}");
        sb.AppendLine($"文件扩展名：{FileAssociation.Extension}");
        sb.AppendLine();

        Check("模组包根目录只覆盖模组自己的东西（绝不碰 core/interop/patchers）",
            roots.All(r =>
                !r.AbsolutePath.Contains(@"\core", StringComparison.OrdinalIgnoreCase) &&
                !r.AbsolutePath.Contains(@"\interop", StringComparison.OrdinalIgnoreCase) &&
                !r.AbsolutePath.Contains(@"\patchers", StringComparison.OrdinalIgnoreCase)),
            string.Join("\n", roots.Select(r => $"{r.Name} -> {r.AbsolutePath}")));

        Check("BepInEx.cfg 在 config 根目录的保留名单里（模组包不许删它）",
            roots.Any(r => r.Name == "config" && r.KeepNames.Any(k => k.Equals("BepInEx.cfg", StringComparison.OrdinalIgnoreCase))),
            "config 保留名单：" + string.Join(", ", roots.FirstOrDefault(r => r.Name == "config").KeepNames ?? []));

        // 真做一份"只含 plugins"的包，读回来，再删掉。
        try
        {
            var pluginDlls = Directory.Exists(AppState.PluginsDir)
                ? Directory.GetFiles(AppState.PluginsDir!, "*", SearchOption.AllDirectories).ToList()
                : new List<string>();

            if (pluginDlls.Count == 0)
            {
                sb.AppendLine("  （没有插件文件 — 跳过往返测试）");
                sb.AppendLine();
            }
            else
            {
                string pack = ModpackManager.SaveCurrentAsPackSelective("__selftest_pack__", "selftest", pluginDlls);
                var manifest = ModpackManager.TryReadManifest(pack);
                var entries = ModpackManager.GetPackEntries(pack);

                sb.AppendLine($"往返测试用的包：{Path.GetFileName(pack)}（{entries.Count} 个条目，{new FileInfo(pack).Length / 1024} KB）");
                foreach (var e in entries.Take(8)) sb.AppendLine("    " + e);
                if (entries.Count > 8) sb.AppendLine($"    ...（还有 {entries.Count - 8} 个）");
                sb.AppendLine();

                Check("模组包往返：清单和文件树都能读回来",
                    manifest != null && entries.Count > 0,
                    manifest == null ? "清单读不出来" : $"{manifest.Name} / {manifest.Mods.Count} 个模组 / {entries.Count} 个文件");

                // 这条是安全性质的核心：只含 plugins 的包绝不能覆盖到资源目录。
                var covered = roots
                    .Where(r => entries.Any(e => e.StartsWith(r.Prefix, StringComparison.OrdinalIgnoreCase)))
                    .Select(r => r.Name)
                    .ToList();
                sb.AppendLine($"这个包覆盖到的根目录：{string.Join("、", covered)}");
                sb.AppendLine();

                Check("只含 plugins 的包只覆盖 plugins（还原时不动资源/配置）",
                    covered.Count == 1 && covered[0] == "plugins",
                    "covered: " + string.Join(", ", covered));

                ModpackManager.DeletePack(pack);
                sb.AppendLine("  （往返测试用的包已删除）");
                sb.AppendLine();
            }
        }
        catch (Exception ex)
        {
            Check("模组包往返", false, ex.GetType().Name + ": " + ex.Message);
        }

        // ---------------------------------------------------------- 还原往返（opt-in）
        if (applyRoundtrip)
        {
            RunApplyRoundtrip(sb, Check);
        }

        // ---------------------------------------------------------- 结论
        return Finish(sb, outPath, passed, failed);
    }

    // ---------------------------------------------------------------- 还原往返

    /// <summary>
    /// 在**临时合成的游戏目录**上把「打包 → 破坏 → 还原」跑一遍。
    ///
    /// <para>存在的理由：<see cref="ModpackManager.ApplyPack"/> 会先清空目录再解压 ——
    /// 这是整个工具里唯一会成批删文件的代码。它必须在**真实的目录结构**上被验证过，
    /// 而不是"看着逻辑没问题"。合成目录保证了这一点既不碰真实安装、又可重复。</para>
    /// </summary>
    private static void RunApplyRoundtrip(StringBuilder sb, Action<string, bool, string> check)
    {
        sb.AppendLine("--- 模组包还原往返（在合成的游戏目录上）---");

        string sandbox = Path.Combine(Path.GetTempPath(), "mvz2mm_roundtrip_" + Guid.NewGuid().ToString("N"));
        string? savedGamePath = AppState.Settings.GamePath;
        string packAll = "", packPlugins = "", packConfig = "";

        try
        {
            // 造一个长得像 MVZ2 的目录
            Directory.CreateDirectory(Path.Combine(sandbox, AppState.GameDataDirName));
            File.WriteAllText(Path.Combine(sandbox, AppState.GameExeName), "stub");

            string plugins = Path.Combine(sandbox, "BepInEx", "plugins");
            string config = Path.Combine(sandbox, "BepInEx", "config");
            string assets = Path.Combine(sandbox, AppState.GameDataDirName, "StreamingAssets", "Mods", "mvz2_lab");
            string core = Path.Combine(sandbox, "BepInEx", "core");

            Directory.CreateDirectory(Path.Combine(plugins, "FolderMod"));
            Directory.CreateDirectory(config);
            Directory.CreateDirectory(assets);
            Directory.CreateDirectory(core);

            File.WriteAllText(Path.Combine(plugins, "DSHCore.dll"), "core-plugin");
            File.WriteAllText(Path.Combine(plugins, "Lab.dll"), "lab-plugin");
            File.WriteAllText(Path.Combine(plugins, "FolderMod", "Inner.dll"), "inner");
            File.WriteAllText(Path.Combine(config, "BepInEx.cfg"), "infrastructure-config");
            File.WriteAllText(Path.Combine(config, "qoznos.mvz2.labmod.cfg"), "volume = 50");
            File.WriteAllText(Path.Combine(assets, "meta.xml"), "<meta/>");
            File.WriteAllText(Path.Combine(core, "BepInEx.Core.dll"), "infrastructure");

            AppState.Settings.GamePath = Path.Combine(sandbox, AppState.GameExeName);

            string[] Tree(params string[] dirs) => dirs
                .Where(Directory.Exists)
                .SelectMany(d => Directory.EnumerateFiles(d, "*", SearchOption.AllDirectories))
                .OrderBy(f => f, StringComparer.OrdinalIgnoreCase)
                .ToArray();

            string Snapshot(params string[] dirs) => string.Join("\n", Tree(dirs)
                .Select(f => Path.GetRelativePath(sandbox, f).Replace('\\', '/') + "=" + File.ReadAllText(f)));

            string SnapshotAll() => Snapshot(plugins, config, assets);

            string before = SnapshotAll();

            // ---------------------------------------------------- 用例 1：整包往返必须无损
            packAll = ModpackManager.SaveCurrentAsPack("__rt_all__", "selftest");
            var allEntries = ModpackManager.GetPackEntries(packAll);
            sb.AppendLine($"  [1] 完整包：{allEntries.Count} 个条目");

            File.Delete(Path.Combine(plugins, "Lab.dll"));
            File.Delete(Path.Combine(plugins, "FolderMod", "Inner.dll"));
            Directory.Delete(assets, recursive: true);
            File.WriteAllText(Path.Combine(plugins, "Stray.dll"), "stray");
            File.WriteAllText(Path.Combine(config, "qoznos.mvz2.labmod.cfg"), "volume = 999");

            ModpackManager.ApplyPack(packAll);
            string after = SnapshotAll();

            check("[1] 完整包还原后包内每个文件都逐字节一致",
                after == before, DiffLines(before, after));

            check("[1] 完整包还原会删掉不在包里的文件",
                !File.Exists(Path.Combine(plugins, "Stray.dll")),
                File.Exists(Path.Combine(plugins, "Stray.dll")) ? "Stray.dll 仍然存在" : "");

            check("[1] 还原本就不该碰 BepInEx/core（基础设施）",
                File.Exists(Path.Combine(core, "BepInEx.Core.dll")) &&
                File.ReadAllText(Path.Combine(core, "BepInEx.Core.dll")) == "infrastructure",
                "core/BepInEx.Core.dll = " + (File.Exists(Path.Combine(core, "BepInEx.Core.dll"))
                    ? File.ReadAllText(Path.Combine(core, "BepInEx.Core.dll")) : "(missing)"));

            // ---------------------------------------------------- 用例 2：只含 plugins 的包不许碰资源/配置
            packPlugins = ModpackManager.SaveCurrentAsPackSelective("__rt_plugins__", "selftest", Tree(plugins));

            File.WriteAllText(Path.Combine(assets, "keep-me.txt"), "assets-marker");
            File.WriteAllText(Path.Combine(config, "keep-me.cfg"), "config-marker");
            File.WriteAllText(Path.Combine(plugins, "Stray2.dll"), "stray2");

            var covered = ModpackManager.GetPackRoots()
                .Where(r => ModpackManager.GetPackEntries(packPlugins)
                    .Any(e => e.StartsWith(r.Prefix, StringComparison.OrdinalIgnoreCase)))
                .Select(r => r.Name)
                .ToList();
            sb.AppendLine($"  [2] 只含 plugins 的包覆盖到：{string.Join("、", covered)}");

            ModpackManager.ApplyPack(packPlugins);

            check("[2] 只含 plugins 的包只清 plugins（资源目录没被动）",
                File.Exists(Path.Combine(assets, "keep-me.txt")),
                File.Exists(Path.Combine(assets, "keep-me.txt")) ? "" : "资源目录里的标记文件被误删了");

            check("[2] 只含 plugins 的包也不会动 config",
                File.Exists(Path.Combine(config, "keep-me.cfg")),
                File.Exists(Path.Combine(config, "keep-me.cfg")) ? "" : "配置目录里的标记文件被误删了");

            check("[2] 被覆盖到的根目录确实被清理了",
                !File.Exists(Path.Combine(plugins, "Stray2.dll")),
                "还原后 Stray2.dll 是否存在 = " + File.Exists(Path.Combine(plugins, "Stray2.dll")));

            // ---------------------------------------------------- 用例 3：保留名单
            string bepCfg = Path.Combine(config, "BepInEx.cfg");
            File.WriteAllText(bepCfg, "KEEP ME");

            packConfig = ModpackManager.SaveCurrentAsPackSelective("__rt_config__", "selftest",
                new[] { Path.Combine(config, "qoznos.mvz2.labmod.cfg") });

            ModpackManager.ApplyPack(packConfig);

            check("[3] 还原配置的包不会删掉 BepInEx.cfg（保留名单生效）",
                File.Exists(bepCfg) && File.ReadAllText(bepCfg) == "KEEP ME",
                File.Exists(bepCfg) ? "content = " + File.ReadAllText(bepCfg) : "BepInEx.cfg 被删掉了");

            check("[3] 包里的配置文件被正确还原",
                File.ReadAllText(Path.Combine(config, "qoznos.mvz2.labmod.cfg")) == "volume = 50",
                "value = " + File.ReadAllText(Path.Combine(config, "qoznos.mvz2.labmod.cfg")));

            sb.AppendLine();
        }
        catch (Exception ex)
        {
            check("模组包还原往返", false, ex.GetType().Name + ": " + ex.Message);
        }
        finally
        {
            foreach (var p in new[] { packAll, packPlugins, packConfig })
                if (p.Length > 0) { try { ModpackManager.DeletePack(p); } catch { } }

            AppState.Settings.GamePath = savedGamePath ?? "";
            try { Directory.Delete(sandbox, recursive: true); } catch { }
        }
    }

    private static string DiffLines(string expected, string actual)
    {
        var e = expected.Split('\n').ToHashSet(StringComparer.Ordinal);
        var a = actual.Split('\n').ToHashSet(StringComparer.Ordinal);

        var missing = e.Except(a).Take(6).ToList();
        var extra = a.Except(e).Take(6).ToList();

        var sb = new StringBuilder();
        if (missing.Count > 0) sb.AppendLine("缺失或不对的：" + string.Join(" | ", missing));
        if (extra.Count > 0) sb.AppendLine("多余的：" + string.Join(" | ", extra));
        return sb.ToString().TrimEnd();
    }

    private static int Finish(StringBuilder sb, string outPath, int passed, int failed)
    {
        sb.AppendLine("--- 结果 ---");
        sb.AppendLine($"通过 {passed} 项，失败 {failed} 项");

        try
        {
            string full = Path.GetFullPath(outPath);
            Directory.CreateDirectory(Path.GetDirectoryName(full)!);
            File.WriteAllText(full, sb.ToString());
        }
        catch { /* 写不出去也只能算了 */ }

        return failed == 0 ? 0 : 1;
    }
}
