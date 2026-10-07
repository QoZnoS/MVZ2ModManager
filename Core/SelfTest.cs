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

        sb.AppendLine("MVZ2 Mod Manager — self test");
        sb.AppendLine($"App version : {Program.CurrentVersion}");
        sb.AppendLine($"When        : {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
        sb.AppendLine();

        // ---------------------------------------------------------- 环境
        AppState.Load();

        if (!File.Exists(AppState.Settings.GamePath))
        {
            string? detected = AppState.AutoDetectGame();
            if (detected != null) AppState.Settings.GamePath = detected;
        }

        string gameDir = AppState.GameDir ?? "";
        sb.AppendLine("--- environment ---");
        sb.AppendLine($"Game path       : {(AppState.Settings.GamePath.Length > 0 ? AppState.Settings.GamePath : "(none)")}");
        sb.AppendLine($"Game dir        : {(gameDir.Length > 0 ? gameDir : "(none)")}");
        sb.AppendLine($"Data dir        : {AppState.DataDir ?? "(none)"}");
        sb.AppendLine($"Plugins dir     : {AppState.PluginsDir ?? "(none)"}");
        sb.AppendLine($"StreamingAssets : {AppState.StreamingAssetsModsDir ?? "(none)"}");
        sb.AppendLine($"Env {AppState.GameDirEnvVar,-12}: {Environment.GetEnvironmentVariable(AppState.GameDirEnvVar) ?? "(unset)"}");
        sb.AppendLine();

        Check("game folder looks like MVZ2 (MinecraftVSZombies2_Data present)",
            AppState.IsValidGameDir(gameDir),
            gameDir.Length > 0 ? gameDir : "no game path resolved");

        if (!AppState.IsValidGameDir(gameDir))
        {
            // 没有游戏就没得测了，直接落盘。
            return Finish(sb, outPath, passed, failed);
        }

        sb.AppendLine($"BepInEx installed : {BepInExManager.IsInstalled(gameDir)}");
        sb.AppendLine($"Mods switched on  : {BepInExManager.ModsEnabled(gameDir)}");
        sb.AppendLine($"Process name      : {AppState.GameProcessName}");
        sb.AppendLine();

        // ---------------------------------------------------------- 元数据
        var mods = ModCatalog.Load();

        sb.AppendLine("--- installed mods ---");
        foreach (var m in mods)
        {
            sb.AppendLine($"  {(m.Enabled ? "ON " : "off")}  {m.Name,-20} v{m.KnownVersion ?? "?",-10} guid={m.Guid ?? "(none)"}");
            sb.AppendLine($"        plugin name   : {m.PluginName ?? "(none)"}");
            if (m.NativeNamespace != null) sb.AppendLine($"        native ns     : {m.NativeNamespace}");
            if (m.HardDependencies.Count > 0) sb.AppendLine($"        hard deps     : {string.Join(", ", m.HardDependencies)}");
            if (m.SoftDependencies.Count > 0) sb.AppendLine($"        soft deps     : {string.Join(", ", m.SoftDependencies)}");
            if (m.Incompatibilities.Count > 0) sb.AppendLine($"        incompatible  : {string.Join(", ", m.Incompatibilities)}");
            if (m.ProcessFilter != null) sb.AppendLine($"        process filter: {m.ProcessFilter}");
        }
        sb.AppendLine();

        Check("at least one mod found in BepInEx/plugins", mods.Count > 0, $"{mods.Count} found");
        Check("PE metadata read for every mod (all have a [BepInPlugin] GUID)",
            mods.Count > 0 && mods.All(m => m.Guid is { Length: > 0 }),
            string.Join("\n", mods.Where(m => m.Guid is not { Length: > 0 }).Select(m => m.Name + " has no GUID")));

        var knownNs = mods.Where(m => m.NativeNamespace != null).Select(m => $"{m.Name} -> {m.NativeNamespace}").ToList();
        Check("native namespaces resolved for known MVZ2 mods",
            knownNs.Count > 0,
            knownNs.Count > 0 ? string.Join("\n", knownNs) : "none resolved (are these the repo's mods?)");

        // 依赖关系是否真的读出来了：这套仓库里所有内容 mod 都硬依赖 DSHCore。
        int withCoreDep = mods.Count(m => m.HardDependencies.Contains(Mvz2Catalog.CoreGuid, StringComparer.OrdinalIgnoreCase));
        Check("[BepInDependency] decoded as HARD dependencies (content mods depend on DSHCore)",
            withCoreDep > 0,
            $"{withCoreDep} mod(s) declare {Mvz2Catalog.CoreGuid} as a hard dependency"
            + "\n(soft deps seen: " + mods.Sum(m => m.SoftDependencies.Count) + ")");

        // 这条是"我们没在猜"：DependencyFlags 的实际取值是从 BepInEx.Core.dll 读的。
        if (AppState.BepInExDir is { } bepDir)
        {
            string core = Path.Combine(bepDir, "core", "BepInEx.Core.dll");
            if (File.Exists(core))
            {
                var flags = ModMetadata.ReadEnumConstants(core, "DependencyFlags");
                Check("DependencyFlags read from the installed BepInEx.Core.dll",
                    flags.Count > 0,
                    flags.Count > 0
                        ? string.Join(", ", flags.Select(kv => $"{kv.Key}={kv.Value}"))
                        : "couldn't read the enum (falling back to Hard=1/Soft=2)");
            }
        }

        // ---------------------------------------------------------- 依赖分析
        var issues = DependencyChecker.Check(mods, BepInExManager.ModsEnabled(gameDir));

        sb.AppendLine("--- dependency check ---");
        if (issues.Count == 0) sb.AppendLine("  (no issues)");
        foreach (var i in issues) sb.AppendLine($"  [{i.Level}] {i.Message}");
        sb.AppendLine();

        Check("dependency check runs and reports no false errors on a healthy install",
            !issues.Any(i => i.Level == IssueLevel.Error),
            string.Join("\n", issues.Where(i => i.Level == IssueLevel.Error).Select(i => i.Message)));

        // 关掉 DSHCore 必须能算出"会连带弄坏谁"。
        if (mods.Any(m => m.Guid == Mvz2Catalog.CoreGuid))
        {
            var coreName = mods.First(m => m.Guid == Mvz2Catalog.CoreGuid).Name;
            var broken = DependencyChecker.WouldBreak(mods, new[] { coreName });
            Check("cascade works: disabling DSHCore shows which mods break",
                broken.Count > 0,
                broken.Count > 0 ? string.Join(", ", broken) : "nothing reported as broken");
        }

        // ---------------------------------------------------------- 存档扫描
        sb.AppendLine("--- save scan ---");
        sb.AppendLine($"User data roots : {(SaveCompatibility.HasAnyUserData ? string.Join(" | ", SaveCompatibility.UserDataRoots()) : "(none found)")}");

        var refs = SaveCompatibility.ScanAll(force: true);
        var usages = SaveCompatibility.Summarize(refs);
        int levelFiles = 0;
        foreach (var root in SaveCompatibility.UserDataRoots())
        {
            try { levelFiles += Directory.EnumerateFiles(root, "*.lvl", SearchOption.AllDirectories).Count(); }
            catch { }
        }

        sb.AppendLine($"Level (.lvl) files : {levelFiles}");
        sb.AppendLine($"Parsed identifiers : {refs.Count} refs across {usages.Count} namespaces");
        foreach (var u in usages)
            sb.AppendLine($"  {u.Namespace,-16} v{u.VersionRange,-8} {u.SaveCount} save(s)  e.g. {string.Join(", ", u.Samples)}");
        sb.AppendLine();

        if (levelFiles > 0)
        {
            Check("every .lvl header parsed (gzip + SerializableLevelControllerHeader)",
                refs.Count > 0,
                $"{levelFiles} level files, {refs.Count} identifier refs");

            // 存档里出现的命名空间应该都能归属到某个已安装模组（或原版）。
            var owners = ModCatalog.NamespaceOwners(mods);
            var unknown = usages.Select(u => u.Namespace).Where(ns => !owners.ContainsKey(ns)).ToList();
            Check("every namespace found in saves maps to a mod or vanilla",
                unknown.Count == 0,
                unknown.Count == 0 ? "" : "unmapped: " + string.Join(", ", unknown));
        }
        else
        {
            sb.AppendLine("  (no saves on this machine — skipping save assertions)");
            sb.AppendLine();
        }

        // ---------------------------------------------------------- 结论
        return Finish(sb, outPath, passed, failed);
    }

    private static int Finish(StringBuilder sb, string outPath, int passed, int failed)
    {
        sb.AppendLine("--- result ---");
        sb.AppendLine($"{passed} passed, {failed} failed");

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
