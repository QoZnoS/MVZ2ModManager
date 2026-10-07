using System.IO.Compression;
using System.Text.Json;

namespace MVZ2ModManager.Core;

/// <summary>
/// 一个可以被打进模组包的根目录。
/// </summary>
/// <param name="Name">人读的名字（也是导出对话框里的顶层节点）。</param>
/// <param name="Prefix">包内路径前缀，末尾带 <c>/</c>。</param>
/// <param name="AbsolutePath">磁盘上的绝对路径。</param>
/// <param name="KeepNames">
/// 清空这个根时**必须保留**的文件/目录名。用来防止把 BepInEx 自己的基础设施
/// （<c>BepInEx.cfg</c>）当成模组配置删掉。
/// </param>
internal readonly record struct PackRoot(string Name, string Prefix, string AbsolutePath, string[] KeepNames)
{
    public PackRoot(string name, string prefix, string absolutePath) : this(name, prefix, absolutePath, []) { }
}

/// <summary>
/// 模组包：把「当前这套 setup」打包成一个 zip，换机器/换人时一键还原。
///
/// <para><b>与上游最大的区别：这里不碰 mod 加载器。</b> 上游的 Apply 会先删掉整个
/// <c>BepInEx</c> 再重新下载安装 —— 对 MVZ2 是灾难性的：BepInEx 6 与
/// <c>interop</c> 是跟这份游戏二进制精确匹配、手工维护的，重装会把它换坏。</para>
///
/// <para><b>两个硬约束</b>：
/// <list type="number">
///   <item>根目录只覆盖**模组自己的东西**：<c>BepInEx/plugins</c>、
///         <c>BepInEx/config</c>、<c>StreamingAssets/Mods</c>。
///         <c>core/</c>、<c>interop/</c>、<c>patchers/</c>、<c>BepInEx.cfg</c> 一概不碰。</item>
///   <item>还原时**只清空这份包确实覆盖到的根**。所以一份"只含 plugins"的包
///         绝不会顺手把别人的资源目录删掉。</item>
/// </list></para>
/// </summary>
internal static class ModpackManager
{
    public const string ManifestEntry = "mvz2pack.json";

    private static readonly JsonSerializerOptions _json = new()
    {
        WriteIndented = true,
        IncludeFields = true,
    };

    /// <summary>自建模组包的仓库目录。</summary>
    public static string PacksDir => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "MVZ2ModManager", "Modpacks", "mvz2");

    // ------------------------------------------------------------- 根目录

    public static List<PackRoot> GetPackRoots()
    {
        var list = new List<PackRoot>();

        if (AppState.PluginsDir is { } plugins)
            list.Add(new PackRoot("plugins", "BepInEx/plugins/", plugins));

        if (AppState.ConfigDir is { } config)
            list.Add(new PackRoot("config", "BepInEx/config/", config, new[] { "BepInEx.cfg" }));

        if (AppState.StreamingAssetsModsDir is { } assets)
            list.Add(new PackRoot("StreamingAssets/Mods", "StreamingAssets/Mods/", assets));

        return list;
    }

    /// <summary>导出时默认勾上的根（模组本体；config 是个人调参，默认不勾）。</summary>
    public static HashSet<string> DefaultRootNames =>
        new(StringComparer.OrdinalIgnoreCase) { "plugins", "StreamingAssets/Mods" };

    // ------------------------------------------------------------- 读

    public static ModpackManifest? TryReadManifest(string packPath)
    {
        try
        {
            using var zip = ZipFile.OpenRead(packPath);
            var entry = zip.GetEntry(ManifestEntry);
            if (entry == null) return null;
            using var stream = entry.Open();
            return JsonSerializer.Deserialize<ModpackManifest>(stream, _json);
        }
        catch { return null; }
    }

    public static List<ModpackInfo> GetSavedPacks()
    {
        var result = new List<ModpackInfo>();
        if (!Directory.Exists(PacksDir)) return result;

        foreach (var f in Directory.GetFiles(PacksDir, "*" + FileAssociation.Extension))
        {
            var manifest = TryReadManifest(f);
            if (manifest != null) result.Add(new ModpackInfo { FilePath = f, Manifest = manifest });
        }
        return result.OrderBy(p => p.DisplayName, StringComparer.OrdinalIgnoreCase).ToList();
    }

    /// <summary>包内的文件清单（排除清单自身）。</summary>
    public static List<string> GetPackEntries(string packPath)
    {
        try
        {
            using var zip = ZipFile.OpenRead(packPath);
            return zip.Entries
                .Where(e => e.Name.Length > 0 && e.FullName != ManifestEntry)
                .Select(e => e.FullName)
                .OrderBy(n => n, StringComparer.OrdinalIgnoreCase)
                .ToList();
        }
        catch { return new List<string>(); }
    }

    /// <summary>当前启用中的模组名（这份包代表的"一套 setup"）。</summary>
    public static List<string> GetCurrentModNames() =>
        ModInstaller.GetInstalled()
            .Where(m => m.Enabled)
            .Select(m => m.Name)
            .OrderBy(n => n, StringComparer.OrdinalIgnoreCase)
            .ToList();

    public static bool CurrentSetupMatchesSavedPack()
    {
        var current = new HashSet<string>(GetCurrentModNames(), StringComparer.OrdinalIgnoreCase);
        return GetSavedPacks().Any(p => current.SetEquals(p.Manifest.Mods));
    }

    // ------------------------------------------------------------- 写

    /// <summary>把整个当前 setup 存成包（所有根、所有文件）。</summary>
    public static string SaveCurrentAsPack(string name, string author, IProgress<(int Percent, string Status)>? progress = null)
    {
        var roots = GetPackRoots();
        var files = roots
            .Where(r => Directory.Exists(r.AbsolutePath))
            .SelectMany(r => Directory.GetFiles(r.AbsolutePath, "*", SearchOption.AllDirectories))
            .ToList();

        return WritePack(name, author, roots, files, progress);
    }

    /// <summary>只把指定的文件存成包（导出对话框里勾选的结果）。</summary>
    public static string SaveCurrentAsPackSelective(
        string name, string author, IEnumerable<string> includedAbsolutePaths,
        IProgress<(int Percent, string Status)>? progress = null)
    {
        var roots = GetPackRoots();
        var files = includedAbsolutePaths.Where(File.Exists).ToList();
        return WritePack(name, author, roots, files, progress);
    }

    private static string WritePack(
        string name, string author, List<PackRoot> roots, List<string> files,
        IProgress<(int Percent, string Status)>? progress)
    {
        if (roots.Count == 0) throw new InvalidOperationException("还没有选择游戏目录。");

        Directory.CreateDirectory(PacksDir);
        string safeName = string.Join("_", name.Split(Path.GetInvalidFileNameChars()));
        if (safeName.Length == 0) safeName = "未命名模组包";
        string dest = Path.Combine(PacksDir, safeName + FileAssociation.Extension);
        if (File.Exists(dest)) File.Delete(dest);

        var manifest = new ModpackManifest
        {
            FormatVersion = ModpackManifest.CurrentFormat,
            Name = name,
            Author = author,
            GameSlug = AppState.CurrentGameSlug,
            CreatedUtc = DateTime.UtcNow,
            Mods = GetCurrentModNames(),
        };

        progress?.Report((0, "正在扫描文件…"));

        using var zip = ZipFile.Open(dest, ZipArchiveMode.Create);

        var manifestEntry = zip.CreateEntry(ManifestEntry);
        using (var w = new StreamWriter(manifestEntry.Open()))
            w.Write(JsonSerializer.Serialize(manifest, _json));

        for (int i = 0; i < files.Count; i++)
        {
            string file = files[i];

            var root = roots.FirstOrDefault(r =>
                file.StartsWith(r.AbsolutePath + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase));
            if (root.AbsolutePath == null) continue;   // 不在任何根里，跳过

            string rel = root.Prefix + Path.GetRelativePath(root.AbsolutePath, file).Replace('\\', '/');
            zip.CreateEntryFromFile(file, rel, CompressionLevel.Optimal);
            progress?.Report((files.Count == 0 ? 100 : (i + 1) * 100 / files.Count, $"正在打包 {Path.GetFileName(file)}…"));
        }

        progress?.Report((100, "打包完成。"));
        return dest;
    }

    // ------------------------------------------------------------- 还原

    /// <summary>
    /// 还原一份包。**先清空"这份包覆盖到的根"，再解压** ——
    /// 这样结果一定等于包里的内容，而不是和现有文件混在一起。
    /// </summary>
    public static void ApplyPack(string packPath, IProgress<(int Percent, string Status)>? progress = null)
    {
        var roots = GetPackRoots();
        if (roots.Count == 0) throw new InvalidOperationException("还没有选择游戏目录。");

        using var zip = ZipFile.OpenRead(packPath);
        var entries = zip.Entries.Where(e => e.Name.Length > 0 && e.FullName != ManifestEntry).ToList();

        // 只清理包确实覆盖到的根。
        var covered = roots
            .Where(r => entries.Any(e => e.FullName.StartsWith(r.Prefix, StringComparison.OrdinalIgnoreCase)))
            .ToList();

        foreach (var root in covered)
        {
            progress?.Report((0, $"正在清空 {root.Name}…"));
            ClearRoot(root);
        }

        for (int i = 0; i < entries.Count; i++)
        {
            var entry = entries[i];

            var root = roots.FirstOrDefault(r => entry.FullName.StartsWith(r.Prefix, StringComparison.OrdinalIgnoreCase));
            if (root.AbsolutePath == null) continue;

            string rel = entry.FullName[root.Prefix.Length..];
            if (rel.Length == 0) continue;

            string dest = Path.Combine(root.AbsolutePath, rel.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
            entry.ExtractToFile(dest, overwrite: true);

            progress?.Report(((i + 1) * 100 / Math.Max(1, entries.Count), $"正在解压 {entry.Name}…"));
        }

        progress?.Report((100, "已还原模组包。"));
    }

    /// <summary>清空一个根（保留 <see cref="PackRoot.KeepNames"/> 里的名字）。</summary>
    private static void ClearRoot(PackRoot root)
    {
        if (!Directory.Exists(root.AbsolutePath)) return;

        foreach (var dir in Directory.GetDirectories(root.AbsolutePath))
        {
            if (IsKept(root, Path.GetFileName(dir))) continue;
            try { Directory.Delete(dir, recursive: true); } catch { }
        }

        foreach (var file in Directory.GetFiles(root.AbsolutePath))
        {
            if (IsKept(root, Path.GetFileName(file))) continue;
            try { File.Delete(file); } catch { }
        }
    }

    private static bool IsKept(PackRoot root, string name) =>
        root.KeepNames.Any(k => string.Equals(k, name, StringComparison.OrdinalIgnoreCase));

    // ------------------------------------------------------------- 文件操作

    public static void DeletePack(string packPath)
    {
        if (File.Exists(packPath)) File.Delete(packPath);
    }

    public static void ExportPack(string packPath, string destPath) =>
        File.Copy(packPath, destPath, overwrite: true);

    /// <summary>把外部的包拷进自建仓库，返回落地路径。</summary>
    public static string ImportPack(string sourcePath)
    {
        Directory.CreateDirectory(PacksDir);
        string dest = Path.Combine(PacksDir, Path.GetFileName(sourcePath));
        if (File.Exists(dest))
            dest = Path.Combine(PacksDir,
                $"{Path.GetFileNameWithoutExtension(sourcePath)}_{DateTime.Now:HHmmss}{FileAssociation.Extension}");
        File.Copy(sourcePath, dest, overwrite: false);
        return dest;
    }
}
