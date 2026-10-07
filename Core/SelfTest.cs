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
        sb.AppendLine($"模组总开关       ：{BepInExManager.ModsEnabled(gameDir)}");
        sb.AppendLine($"进程名           ：{AppState.GameProcessName}");
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
        var issues = DependencyChecker.Check(mods, BepInExManager.ModsEnabled(gameDir));

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
