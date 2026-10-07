namespace MVZ2ModManager.Core;

/// <summary>
/// 把「磁盘上的模组」变成「有意义的模组」的唯一入口。
///
/// <para>做三件事：扫盘 → 读 PE 元数据 → 用 <see cref="Mvz2Catalog"/> 补上命名空间。
/// 面板只调 <see cref="Load"/>，其余都是纯函数。</para>
/// </summary>
internal static class ModCatalog
{
    /// <summary>扫描并补全所有已安装模组（按名字排序）。</summary>
    public static List<InstalledMod> Load()
    {
        var mods = ModInstaller.GetInstalled();

        foreach (var m in mods)
        {
            ModMetadata.ReadInto(m);

            var fact = Mvz2Catalog.Find(m.Guid);
            if (fact != null)
            {
                m.NativeNamespace ??= fact.NativeNamespace;
                if (string.IsNullOrEmpty(m.PluginName)) m.PluginName = fact.DisplayName;
            }

            // GUID 不认识（或改了 GUID）时用程序集名兜底，至少能接上命名空间。
            m.NativeNamespace ??= NamespaceFromName(m.Name);

            // 有原生命名空间 = 会注册 ModInfo = 会往关卡存档头里写 identifier。
            // （是不是"真的写过"由存档扫描来回答，这里只标记"有可能"。）
            m.WritesLevelData = m.NativeNamespace != null;
        }

        return mods;
    }

    private static string? NamespaceFromName(string modName) =>
        Mvz2Catalog.NamespaceByAssemblyName.TryGetValue(modName, out var nsp) ? nsp : null;

    /// <summary>
    /// 这套安装里出现过的所有命名空间（含原版）。用来把存档里读到的命名空间
    /// 映射回"是哪个模组"。
    /// </summary>
    public static Dictionary<string, string> NamespaceOwners(IReadOnlyList<InstalledMod> mods)
    {
        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            [Mvz2Catalog.VanillaNamespace] = "Vanilla (game)",
        };

        foreach (var m in mods)
            if (m.NativeNamespace is { Length: > 0 } nsp)
                map[nsp] = m.PluginName ?? m.Name;

        return map;
    }

    /// <summary>人读的标签：优先插件显示名，其次目录名。</summary>
    public static string Label(InstalledMod m) =>
        string.IsNullOrEmpty(m.PluginName) ? m.Name : m.PluginName!;
}
