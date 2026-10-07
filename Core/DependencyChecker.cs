namespace MVZ2ModManager.Core;

internal enum IssueLevel { Error, Warning, Info }

internal sealed record ModIssue(IssueLevel Level, string Message, string? ModName);

/// <summary>
/// 依赖分析。全部是纯函数 —— 输入一批 <see cref="InstalledMod"/>，输出问题清单。
///
/// <para>这里的判断标准必须**和 BepInEx 自己一致**，否则会出现"工具说没事、
/// 游戏里插件却默默不加载"这种最难查的情况：
/// <list type="bullet">
///   <item>缺硬依赖 → BepInEx 会跳过这个插件，并在日志里报错（不会崩，但功能静默消失）；</item>
///   <item>不缺硬依赖但被显式禁用 → 同上，只是原因是我们自己造成的；</item>
///   <item><c>[BepInIncompatibility]</c> → 插件与其依赖者一起被丢弃；</item>
///   <item><c>[BepInProcess]</c> 与进程名不符 → 插件根本不加载。</item>
/// </list></para>
/// </summary>
internal static class DependencyChecker
{
    public static List<ModIssue> Check(IReadOnlyList<InstalledMod> mods, bool bepInExOn)
    {
        var issues = new List<ModIssue>();

        if (!bepInExOn)
        {
            issues.Add(new ModIssue(IssueLevel.Error,
                "BepInEx is switched off (winhttp.dll renamed). No mod will load until it's turned back on.",
                null));
        }

        // GUID → 模组。同一 GUID 出现多次要单独报（典型的"old + new 两份 dll 并存"）。
        var byGuid = new Dictionary<string, InstalledMod>(StringComparer.OrdinalIgnoreCase);
        foreach (var m in mods)
        {
            if (m.Guid is not { Length: > 0 } guid) continue;

            if (byGuid.TryGetValue(guid, out var existing))
            {
                issues.Add(new ModIssue(IssueLevel.Error,
                    $"\"{existing.Name}\" and \"{m.Name}\" both declare the same plugin GUID ({guid}). " +
                    "BepInEx will only load one of them — remove or rename the stale copy.",
                    m.Name));
                continue;
            }
            byGuid[guid] = m;
        }

        string? processName = AppState.GameProcessName;

        foreach (var m in mods.Where(x => x.Enabled))
        {
            // [BepInProcess] 与实际进程名不符 → 该插件永远不会加载。
            if (m.ProcessFilter is { Length: > 0 } filter &&
                processName is { Length: > 0 } &&
                !string.Equals(filter, processName + ".exe", StringComparison.OrdinalIgnoreCase) &&
                !string.Equals(filter, processName, StringComparison.OrdinalIgnoreCase))
            {
                issues.Add(new ModIssue(IssueLevel.Warning,
                    $"\"{m.Name}\" is limited to process \"{filter}\", so it won't load in this game ({processName}.exe).",
                    m.Name));
            }

            foreach (var depGuid in m.HardDependencies)
            {
                if (!byGuid.TryGetValue(depGuid, out var dep))
                {
                    issues.Add(new ModIssue(IssueLevel.Error,
                        $"\"{m.Name}\" requires {Describe(depGuid)}, which is not installed. " +
                        "BepInEx will skip this plugin.",
                        m.Name));
                }
                else if (!dep.Enabled)
                {
                    issues.Add(new ModIssue(IssueLevel.Error,
                        $"\"{m.Name}\" requires \"{dep.Name}\", which is disabled. Enable it, or this plugin won't load.",
                        m.Name));
                }
            }

            foreach (var badGuid in m.Incompatibilities)
            {
                if (byGuid.TryGetValue(badGuid, out var bad) && bad.Enabled)
                {
                    issues.Add(new ModIssue(IssueLevel.Error,
                        $"\"{m.Name}\" declares itself incompatible with \"{bad.Name}\" (both are enabled).",
                        m.Name));
                }
            }
        }

        // 反向提示：硬依赖没人用的模组（多半是前置库）—— 只在它被禁用时才提。
        foreach (var m in mods.Where(x => !x.Enabled && x.Guid is { Length: > 0 }))
        {
            int dependents = mods.Count(x => x.Enabled && x.HardDependencies.Contains(m.Guid!, StringComparer.OrdinalIgnoreCase));
            if (dependents > 0)
            {
                issues.Add(new ModIssue(IssueLevel.Error,
                    $"\"{m.Name}\" is disabled, but {dependents} enabled mod{(dependents == 1 ? "" : "s")} depend on it. " +
                    "Those will not load.",
                    m.Name));
            }
        }

        return issues;
    }

    /// <summary>
    /// 关掉这几个模组会连带影响哪些**目前启用**的模组（含传递依赖）。
    /// 用于禁用前的二次确认。
    /// </summary>
    public static List<string> WouldBreak(IReadOnlyList<InstalledMod> mods, IEnumerable<string> toDisable)
    {
        var byGuid = new Dictionary<string, InstalledMod>(StringComparer.OrdinalIgnoreCase);
        foreach (var m in mods)
            if (m.Guid is { Length: > 0 }) byGuid.TryAdd(m.Guid, m);

        var doomed = new HashSet<string>(toDisable, StringComparer.OrdinalIgnoreCase);
        var broken = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        // 反复扫到稳定为止：A 依赖被禁的 B，C 又依赖 A。
        bool changed = true;
        while (changed)
        {
            changed = false;
            foreach (var m in mods)
            {
                if (!m.Enabled || doomed.Contains(m.Name) || broken.Contains(m.Name)) continue;

                foreach (var depGuid in m.HardDependencies)
                {
                    if (!byGuid.TryGetValue(depGuid, out var dep)) continue;

                    if (doomed.Contains(dep.Name) || broken.Contains(dep.Name))
                    {
                        broken.Add(m.Name);
                        changed = true;
                        break;
                    }
                }
            }
        }

        return broken.OrderBy(n => n, StringComparer.OrdinalIgnoreCase).ToList();
    }

    private static string Describe(string guid) =>
        Mvz2Catalog.Find(guid) is { } fact ? $"{fact.DisplayName} ({guid})" : guid;
}
