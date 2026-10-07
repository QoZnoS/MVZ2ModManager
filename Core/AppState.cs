using System.Text.Json;
using System.Text.Json.Serialization;

namespace MVZ2ModManager.Core;

/// <summary>
/// 全局状态：游戏路径、应用设置，以及所有从「游戏目录」派生出来的路径。
///
/// <para><b>只支持 BepInEx + IL2CPP</b>：本工具专为《Minecraft vs Zombies 2》0.7.x
/// （Windows x86 / IL2CPP / BepInEx 6 be.788）而做，所以不再有 MelonLoader 之类的
/// 加载器抽象 —— 少一层判断就少一类误判（原版会在 <c>winhttp.dll</c> 缺失时把
/// IL2CPP 游戏误判成 MelonLoader）。</para>
/// </summary>
internal static class AppState
{
    /// <summary>游戏主程序名。</summary>
    public const string GameExeName = "MinecraftVSZombies2.exe";

    /// <summary>游戏数据目录名（用它判断一个目录是不是 MVZ2）。</summary>
    public const string GameDataDirName = "MinecraftVSZombies2_Data";

    /// <summary>
    /// 未在设置里指定游戏时用来找游戏的环境变量。
    /// 沿用仓库既有约定（<c>Directory.Build.props</c> 的 <c>MVZ2_GAME_DIR</c>）。
    /// </summary>
    public const string GameDirEnvVar = "MVZ2_GAME_DIR";

    private static readonly string _stateFile = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "MVZ2ModManager", "state.json");

    private static readonly JsonSerializerOptions _jsonOptions = new()
    {
        WriteIndented = true,
        IncludeFields = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.Never,
    };

    public static AppSettings Settings { get; private set; } = new();

    // ---------------------------------------------------------------- 游戏目录

    /// <summary>主程序完整路径（未设置 → null）。</summary>
    public static string? GameExePath =>
        Settings.GamePath.Length > 0 ? Settings.GamePath : null;

    /// <summary>游戏根目录（未设置 → null）。</summary>
    public static string? GameDir =>
        Settings.GamePath.Length > 0 ? Path.GetDirectoryName(Settings.GamePath) : null;

    /// <summary>结束游戏进程时用的进程名（不带扩展名）。</summary>
    public static string? GameProcessName =>
        Settings.GamePath.Length > 0 ? Path.GetFileNameWithoutExtension(Settings.GamePath) : null;

    // ---------------------------------------------------------------- 派生路径

    private static string? Sub(params string[] parts) =>
        GameDir is { } d ? Path.Combine(new[] { d }.Concat(parts).ToArray()) : null;

    /// <summary>本工具自己的数据目录：<c>&lt;游戏目录&gt;\BepInEx\MVZ2ModManager\</c>。</summary>
    public static string? DataDir => Sub("BepInEx", "MVZ2ModManager");

    public static string? BepInExDir => Sub("BepInEx");
    public static string? PluginsDir => Sub("BepInEx", "plugins");
    public static string? PatchersDir => Sub("BepInEx", "patchers");
    public static string? ConfigDir => Sub("BepInEx", "config");

    /// <summary>BepInEx 日志（普通日志 / 原生崩溃日志）。</summary>
    public static string? LogOutputPath => Sub("BepInEx", "LogOutput.log");
    public static string? ErrorLogPath => Sub("BepInEx", "ErrorLog.log");

    /// <summary>
    /// 游戏原生的模组资源根：<c>&lt;游戏目录&gt;\MinecraftVSZombies2_Data\StreamingAssets\Mods\</c>。
    /// 内容 mod（lab / curse / loadout / uig）的 assets 与 metas 都在这里，按命名空间分目录。
    /// </summary>
    public static string? StreamingAssetsModsDir =>
        Sub(GameDataDirName, "StreamingAssets", "Mods");

    /// <summary>模组安装位置 —— 即 <c>BepInEx\plugins</c>。</summary>
    public static string? ModsInstallDir => PluginsDir;

    // ---------------------------------------------------------------- 设置读写

    public static void Load()
    {
        if (!File.Exists(_stateFile)) return;
        try
        {
            Settings = JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(_stateFile), _jsonOptions) ?? new();
            Normalize();
        }
        catch { Settings = new(); }
    }

    public static void Save()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_stateFile)!);
            File.WriteAllText(_stateFile, JsonSerializer.Serialize(Settings, _jsonOptions));
        }
        catch { /* 设置写不进去不该让程序崩 */ }
    }

    /// <summary>反序列化后补齐 null 集合（旧版 state.json 可能缺字段）。</summary>
    private static void Normalize()
    {
        Settings.KnownGamePaths ??= new();
        Settings.CustomGames ??= new();
        Settings.HiddenPresets ??= new();
        if (Settings.GameName.Length == 0) Settings.GameName = "Minecraft vs Zombies 2";
    }

    /// <summary>确保数据目录存在（LoadoutsPanel / Modpacks 等在写文件前调用）。</summary>
    public static void EnsureDataDir()
    {
        if (DataDir is { } dir && !Directory.Exists(dir))
            Directory.CreateDirectory(dir);
    }

    // ---------------------------------------------------------------- 游戏预设

    /// <summary>
    /// 「自定义游戏」预设列表。MVZ2 不在任何商店里，所以不给硬编码路径 ——
    /// 由 <c>MVZ2_GAME_DIR</c> 环境变量、上次选择（<see cref="AppSettings.KnownGamePaths"/>）
    /// 或用户在文件夹选择器里手动指定。
    /// </summary>
    public static readonly GamePreset[] Presets =
    [
        new("Minecraft vs Zombies 2", "mvz2", []),
        new("Custom", "custom", []),
    ];

    public static GamePreset CurrentPreset =>
        Presets.FirstOrDefault(p => p.Name == Settings.GameName) ?? Presets[^1];

    public static string CurrentGameSlug =>
        CurrentPreset.Name != "Custom" ? CurrentPreset.Slug : DeriveSlug(Settings.GameName);

    public static string DeriveSlug(string name) =>
        new(name.ToLowerInvariant().Where(char.IsLetterOrDigit).ToArray());

    // ---------------------------------------------------------------- 游戏探测

    /// <summary>这个目录是不是一套 MVZ2 安装（认数据目录，不认 exe 名）。</summary>
    public static bool IsValidGameDir(string? dir) =>
        !string.IsNullOrWhiteSpace(dir) && Directory.Exists(Path.Combine(dir, GameDataDirName));

    /// <summary>在目录里找主程序（精确名优先，其次 <c>MinecraftVSZombies2*.exe</c>）。</summary>
    public static string? FindGameExe(string? dir)
    {
        if (string.IsNullOrWhiteSpace(dir) || !Directory.Exists(dir)) return null;

        string exact = Path.Combine(dir, GameExeName);
        if (File.Exists(exact)) return exact;

        try
        {
            return Directory.EnumerateFiles(dir, "MinecraftVSZombies2*.exe", SearchOption.TopDirectoryOnly)
                .OrderBy(f => f.Length)
                .FirstOrDefault();
        }
        catch { return null; }
    }

    /// <summary>自动找一个可用的游戏：环境变量 → 历史选择 → 内置预设。
    /// 全都落空时返回 null，由调用方弹目录选择器。
    /// </summary>
    public static string? AutoDetectGame()
    {
        if (Environment.GetEnvironmentVariable(GameDirEnvVar) is { Length: > 0 } env)
        {
            string? exe = FindGameExe(env);
            if (exe != null) return exe;
        }

        foreach (var known in Settings.KnownGamePaths.Values)
        {
            if (File.Exists(known)) return known;
            string? exe = FindGameExe(Path.GetDirectoryName(known));
            if (exe != null) return exe;
        }

        foreach (var preset in Presets)
            foreach (var p in preset.DefaultPaths)
                if (File.Exists(p)) return p;

        return null;
    }

    /// <summary>
    /// 游戏进程是否在跑。在跑的时候 <c>BepInEx\plugins\*.dll</c> 是被锁住的，
    /// 任何改名/删文件操作都会失败，所以调用方要先问一句。
    /// </summary>
    public static bool IsGameRunning()
    {
        string? name = GameProcessName;
        if (string.IsNullOrEmpty(name)) return false;
        try { return System.Diagnostics.Process.GetProcessesByName(name).Length > 0; }
        catch { return false; }
    }
}
