namespace MVZ2ModManager.Core;

/// <summary>
/// 模组的「装 / 卸 / 开关」。
///
/// <para><b>开关的实现方式</b>：BepInEx 的插件发现就是一句
/// <c>Directory.GetFiles(plugins, "*.dll", AllDirectories)</c>，没有任何开关或白名单，
/// 所以"禁用"只能是改名 —— <c>Foo.dll</c> ↔ <c>Foo.dll.disabled</c>。
/// 这不是官方约定，但它只依赖那个 glob，行为稳定且可逆。</para>
///
/// <para>文件夹式模组（整个目录塞进 <c>plugins/</c>）同样处理：递归把所有
/// <c>*.dll</c> 一起改名，避免出现"一半启用一半禁用"的怪状态。</para>
///
/// <para><b>卸载是软删除</b>：改名成 <c>*.delete</c> 而不是真删，误删可救。</para>
/// </summary>
internal static class ModInstaller
{
    private const string DisabledSuffix = ".disabled";
    private const string DeleteSuffix = ".delete";

    /// <summary>扫描已安装的模组（启用 + 禁用），按名字排序。</summary>
    public static List<InstalledMod> GetInstalled()
    {
        var result = new List<InstalledMod>();

        ScanDir(AppState.PluginsDir, result);
        ScanDir(AppState.PatchersDir, result);

        return result.OrderBy(m => m.Name, StringComparer.OrdinalIgnoreCase).ToList();
    }

    private static void ScanDir(string? dir, List<InstalledMod> result)
    {
        result.AddRange(ScanDirItems(dir));
    }

    /// <summary>扫描单个目录（<c>plugins</c> 或 <c>patchers</c>）。</summary>
    public static List<InstalledMod> ScanDirItems(string? dir)
    {
        var result = new List<InstalledMod>();
        if (string.IsNullOrEmpty(dir) || !Directory.Exists(dir)) return result;

        ScanFlatDlls(dir, result);
        ScanFolderMods(dir, result);
        return result;
    }

    private static void ScanFlatDlls(string dir, List<InstalledMod> result)
    {
        foreach (var f in Directory.GetFiles(dir, "*.dll"))
            result.Add(new InstalledMod
            {
                Name = Path.GetFileNameWithoutExtension(f),
                FilePath = f,
                Enabled = true,
                SizeBytes = SafeLength(f),
            });

        foreach (var f in Directory.GetFiles(dir, "*.dll" + DisabledSuffix))
        {
            string name = Path.GetFileNameWithoutExtension(f);
            if (name.EndsWith(".dll", StringComparison.OrdinalIgnoreCase))
                name = name[..^4];
            result.Add(new InstalledMod
            {
                Name = name,
                FilePath = f,
                Enabled = false,
                SizeBytes = SafeLength(f),
            });
        }
    }

    private static void ScanFolderMods(string dir, List<InstalledMod> result)
    {
        foreach (var sub in Directory.GetDirectories(dir))
        {
            string leaf = Path.GetFileName(sub);

            // 软删除的目录
            if (leaf.EndsWith(DeleteSuffix, StringComparison.OrdinalIgnoreCase)) continue;

            int active = SafeCount(sub, "*.dll");
            int disabled = SafeCount(sub, "*.dll" + DisabledSuffix);
            if (active + disabled == 0) continue;   // 不是模组（可能只是别的工具的杂物）

            result.Add(new InstalledMod
            {
                Name = leaf,
                FilePath = sub,
                Enabled = active > 0,
                IsFolder = true,
                SizeBytes = SafeTreeLength(sub),
            });
        }
    }

    // ------------------------------------------------------------- 装 / 卸

    /// <summary>把本地 dll 拷进 <c>plugins/</c>（拖拽安装走这里）。</summary>
    public static void InstallLocal(string sourceDllPath)
    {
        string? modsDir = AppState.ModsInstallDir ?? throw new InvalidOperationException("还没有选择游戏目录。");
        Directory.CreateDirectory(modsDir);

        string name = Path.GetFileNameWithoutExtension(sourceDllPath);
        string dest = Path.Combine(modsDir, name + ".dll");

        // 同名但处于禁用状态时，先清掉禁用副本，避免出现两份。
        string disabled = dest + DisabledSuffix;
        if (File.Exists(disabled)) File.Delete(disabled);

        File.Copy(sourceDllPath, dest, overwrite: true);
    }

    /// <summary>
    /// 软删除插件本体：改名成 <c>*.delete</c>（文件夹式则整个目录改名）。
    /// <para><b>不动资源目录</b> —— 那个由 <see cref="UninstallAssets"/> 单独负责，
    /// 因为"只要插件、丢掉资源"和"整套一起丢"是两个不同的意图。</para>
    /// </summary>
    public static void UninstallPluginFiles(string modName)
    {
        string? modsDir = AppState.ModsInstallDir;
        if (modsDir == null) return;

        string folderPath = Path.Combine(modsDir, modName);
        if (Directory.Exists(folderPath))
        {
            string folderTrash = folderPath + DeleteSuffix;
            if (Directory.Exists(folderTrash)) Directory.Delete(folderTrash, recursive: true);
            Directory.Move(folderPath, folderTrash);
            return;
        }

        string dll = Path.Combine(modsDir, modName + ".dll");
        string disabled = dll + DisabledSuffix;
        string target = File.Exists(dll) ? dll : File.Exists(disabled) ? disabled : "";
        if (target.Length == 0) return;

        string trash = target + DeleteSuffix;
        if (File.Exists(trash)) File.Delete(trash);
        File.Move(target, trash);
    }

    /// <summary>
    /// 卸载一个模组：插件文件 + （可选）它的资源目录。
    /// </summary>
    public static void Uninstall(InstalledMod mod, bool removeAssets)
    {
        UninstallPluginFiles(mod.Name);
        if (removeAssets) UninstallAssets(mod.NativeNamespace);
    }

    // --------------------------------------------------------- 资源目录

    /// <summary>
    /// 模组的资源目录：<c>&lt;游戏目录&gt;\MinecraftVSZombies2_Data\StreamingAssets\Mods\&lt;命名空间&gt;\</c>。
    /// 纯工具模组（没有原生命名空间）返回 null。
    /// </summary>
    public static string? AssetsDirFor(string? nativeNamespace) =>
        nativeNamespace is { Length: > 0 } && AppState.StreamingAssetsModsDir is { } mods
            ? Path.Combine(mods, nativeNamespace)
            : null;

    /// <summary>这套模组的磁盘上是否真有资源目录。</summary>
    public static bool HasAssets(string? nativeNamespace)
    {
        string? dir = AssetsDirFor(nativeNamespace);
        return dir != null && Directory.Exists(dir);
    }

    /// <summary>
    /// 软删除资源目录（<c>&lt;nsp&gt;</c> → <c>&lt;nsp&gt;.delete</c>）。
    /// 返回是否真的删了东西。
    /// </summary>
    public static bool UninstallAssets(string? nativeNamespace)
    {
        if (AssetsDirFor(nativeNamespace) is not { } dir || !Directory.Exists(dir)) return false;

        string trash = dir + DeleteSuffix;
        if (Directory.Exists(trash)) Directory.Delete(trash, recursive: true);
        Directory.Move(dir, trash);
        return true;
    }

    /// <summary>资源目录里一共有多少文件 / 多少字节（卸载确认框里给个数）。</summary>
    public static (int Files, long Bytes) MeasureAssets(string? nativeNamespace)
    {
        if (AssetsDirFor(nativeNamespace) is not { } dir || !Directory.Exists(dir)) return (0, 0);
        try
        {
            var files = Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories).ToList();
            return (files.Count, files.Sum(SafeLength));
        }
        catch { return (0, 0); }
    }

    // ---------------------------------------------------------------- 开关

    /// <summary>启用（<c>.dll.disabled</c> → <c>.dll</c>）。</summary>
    public static void Enable(string modName) => SetEnabled(modName, true);

    /// <summary>禁用（<c>.dll</c> → <c>.dll.disabled</c>）。</summary>
    public static void Disable(string modName) => SetEnabled(modName, false);

    /// <summary>
    /// 批量开关。任一项失败不影响其它项，返回失败的模组名。
    /// 游戏运行时 dll 被锁 → <see cref="IOException"/>，这里收集起来由调用方提示。
    /// </summary>
    public static List<string> SetEnabledMany(IEnumerable<string> modNames, bool enabled)
    {
        var failed = new List<string>();
        foreach (var name in modNames)
        {
            try { SetEnabled(name, enabled); }
            catch { failed.Add(name); }
        }
        return failed;
    }

    private static void SetEnabled(string modName, bool enabled)
    {
        string? modsDir = AppState.ModsInstallDir;
        if (modsDir == null) return;

        string folderPath = Path.Combine(modsDir, modName);
        if (Directory.Exists(folderPath)) { SetFolderEnabled(folderPath, enabled); return; }

        string dll = Path.Combine(modsDir, modName + ".dll");
        string disabled = dll + DisabledSuffix;

        if (enabled && File.Exists(disabled))
        {
            if (File.Exists(dll)) File.Delete(dll);
            File.Move(disabled, dll);
        }
        else if (!enabled && File.Exists(dll))
        {
            if (File.Exists(disabled)) File.Delete(disabled);
            File.Move(dll, disabled);
        }
    }

    private static void SetFolderEnabled(string folderPath, bool enabled)
    {
        string pattern = enabled ? "*.dll" + DisabledSuffix : "*.dll";

        // 先快照，避免边遍历边改名踩到自己的结果。
        var files = Directory.GetFiles(folderPath, pattern, SearchOption.AllDirectories);
        foreach (var f in files)
        {
            string target = enabled ? f[..^DisabledSuffix.Length] : f + DisabledSuffix;
            if (File.Exists(target)) File.Delete(target);
            File.Move(f, target);
        }
    }

    // ------------------------------------------------------------ 小工具

    private static long SafeLength(string path)
    {
        try { return new FileInfo(path).Length; } catch { return 0; }
    }

    private static int SafeCount(string dir, string pattern)
    {
        try { return Directory.EnumerateFiles(dir, pattern, SearchOption.AllDirectories).Count(); }
        catch { return 0; }
    }

    private static long SafeTreeLength(string dir)
    {
        try { return Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories).Sum(SafeLength); }
        catch { return 0; }
    }
}
