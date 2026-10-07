namespace MVZ2ModManager.Core;

/// <summary>磁盘上的一个已安装模组（<c>.dll</c> = 启用，<c>.dll.disabled</c> = 禁用）。</summary>
internal class InstalledMod
{
    public string Name = "";

    /// <summary>启用时的 DLL 路径，或文件夹式模组的目录路径。</summary>
    public string FilePath = "";
    public bool Enabled;
    public string? KnownVersion;
    public bool IsFolder;
    public long SizeBytes;

    // ---- 以下由 PE 元数据读取填入（见 ModMetadataReader）----

    /// <summary>BepInEx 的 <c>[BepInPlugin]</c> GUID。</summary>
    public string? Guid;

    /// <summary><c>[BepInPlugin]</c> 显示名。</summary>
    public string? PluginName;

    /// <summary>硬依赖的 GUID（<c>[BepInDependency(HardDependency)]</c>）—— 缺了这些插件不会加载。</summary>
    public List<string> HardDependencies = new();

    /// <summary>软依赖的 GUID。</summary>
    public List<string> SoftDependencies = new();

    /// <summary>本模组声明的不兼容 GUID（<c>[BepInIncompatibility]</c>）。</summary>
    public List<string> Incompatibilities = new();

    /// <summary>游戏原生命名空间（<c>mvz2_lab</c> 等）；纯工具模组为 null。</summary>
    public string? NativeNamespace;

    /// <summary>会往关卡存档头写命名空间 —— 禁用前必须警告（存档可能读不进去）。</summary>
    public bool WritesLevelData;

    /// <summary>BepInEx 的 <c>[BepInProcess]</c> 值（非空则只在指定进程名里加载）。</summary>
    public string? ProcessFilter;
}

/// <summary>一套「哪些模组开着」的快照。</summary>
internal class ModLoadout
{
    public int Number;
    public string Name = "";
    public List<string> EnabledMods = new();
    public List<string> DisabledMods = new();
    public List<string> MissingMods = new();

    public string DisplayName => string.IsNullOrWhiteSpace(Name) ? $"Loadout {Number}" : Name;
}

internal class ModpackManifest
{
    public string Name { get; set; } = "";
    public string Author { get; set; } = "";
    public string GameSlug { get; set; } = "";
    public DateTime CreatedUtc { get; set; }
    public List<string> Mods { get; set; } = new();
}

internal class ModpackInfo
{
    public string FilePath = "";
    public ModpackManifest Manifest = new();
    public string DisplayName => string.IsNullOrWhiteSpace(Manifest.Name) ? Path.GetFileNameWithoutExtension(FilePath) : Manifest.Name;
}

internal enum ThemeMode { Black, White, Custom, R2Modman }

internal enum GamePickerDisplay { Both, Text, Icons }

internal class AppSettings
{
    /// <summary>游戏主程序（<c>MinecraftVSZombies2.exe</c>）的完整路径。</summary>
    public string GamePath = "";
    public string GameName = "Minecraft vs Zombies 2";

    public ThemeMode Theme = ThemeMode.Black;
    public string CustomBackground = "#141414";
    public string CustomAccent = "#7C3AED";

    /// <summary>游戏选择器里怎么显示游戏（文字 / 图标 / 都要）。</summary>
    public GamePickerDisplay GamePickerDisplay = GamePickerDisplay.Both;

    /// <summary>游戏名 → 主程序路径（用过一次就记住）。</summary>
    public Dictionary<string, string> KnownGamePaths = new();

    /// <summary>游戏名 → 主程序路径（用户手动添加的自定义游戏）。</summary>
    public Dictionary<string, string> CustomGames = new();

    /// <summary>游戏选择器里被隐藏的内置预设。</summary>
    public HashSet<string> HiddenPresets = new();
}

internal record GamePreset(string Name, string Slug, string[] DefaultPaths);
