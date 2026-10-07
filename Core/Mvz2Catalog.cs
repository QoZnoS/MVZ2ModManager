namespace MVZ2ModManager.Core;

/// <summary>一个 MVZ2 侧模组的静态事实。</summary>
/// <param name="Guid">BepInEx 插件 GUID（<c>[BepInPlugin]</c> 的第一个参数）。</param>
/// <param name="NativeNamespace">
/// 游戏原生命名空间，同时也等于
/// <c>MinecraftVSZombies2_Data\StreamingAssets\Mods\&lt;这就是目录名&gt;</c>。
/// 纯工具模组（不注册 <c>ModInfo</c>）为 null。
/// </param>
/// <param name="DisplayName">人读的名字。</param>
internal sealed record Mvz2ModFact(string Guid, string? NativeNamespace, string DisplayName);

/// <summary>
/// 本仓库这套模组的「GUID ↔ 原生命名空间」对照表。
///
/// <para>为什么要这张表：BepInEx 的 GUID 和游戏的 <c>ModInfo</c> 命名空间是两个世界，
/// 而"禁用这个模组会不会让存档读不进去"取决于**命名空间**，
/// 存档里的 <c>identifiers</c> 记的也是命名空间（见 <see cref="SaveCompatibility"/>）。
/// 两者之间没有任何自动联系，只能靠对照表接起来。</para>
///
/// <para>加了新模组就在下面补一行；<see cref="ModCatalog"/> 还会用 DLL 名兜底，
/// 所以漏了也只会退化成"知道是模组、但不知道命名空间"。</para>
/// </summary>
internal static class Mvz2Catalog
{
    public static readonly Mvz2ModFact[] Known =
    [
        new("qoznos.mvz2.core",         null,            "DSHCore"),
        new("qoznos.mvz2.labmod",       "mvz2_lab",      "MVZ2 Laboratory Content"),
        new("qoznos.mvz2.modularcurse", "modular_curse", "MVZ2 Modular Curse"),
        new("qoznos.mvz2.loadout",      "loadout",       "MVZ2 Loadout"),
        new("qoznos.mvz2.uigallery",    "uig",           "MVZ2 UI Gallery"),
        new("qoznos.mvz2.replay",       null,            "MVZ2 Replay"),
    ];

    /// <summary>DLL / 目录名 → 命名空间。GUID 对不上时兜底用（改了 GUID 也还能认出命名空间）。</summary>
    public static readonly Dictionary<string, string> NamespaceByAssemblyName = new(StringComparer.OrdinalIgnoreCase)
    {
        ["LaboratoryMod"] = "mvz2_lab",
        ["ModularCurse"] = "modular_curse",
        ["Loadout"] = "loadout",
        ["UiGallery"] = "uig",
    };

    public static Mvz2ModFact? Find(string? guid)
    {
        if (string.IsNullOrEmpty(guid)) return null;
        foreach (var fact in Known)
            if (string.Equals(fact.Guid, guid, StringComparison.OrdinalIgnoreCase))
                return fact;
        return null;
    }

    /// <summary>游戏原生的存档命名空间（不是模组）。</summary>
    public const string VanillaNamespace = "mvz2";

    /// <summary>DSHCore 是其它所有内容模组的硬前置。</summary>
    public static string CoreGuid => Known[0].Guid;
}
