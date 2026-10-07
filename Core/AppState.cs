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

    // ---------------------------------------------------------------- 安装（版本）列表

    /// <summary>
    /// 当前安装在界面上怎么称呼：登记过就用别名/目录名（"MVZ2 0.7.0 test-10"），
    /// 否则退回游戏名。装了多套版本时，日志和报告里必须能分清是哪一套。
    /// </summary>
    public static string CurrentDisplayName =>
        CurrentInstallation?.DisplayName ?? Settings.GameName;

    /// <summary>已登记的安装，按登记顺序。</summary>
    public static IReadOnlyList<GameInstallation> Installations => Settings.Installations;

    /// <summary>当前安装。没登记过就返回 null（例如 GamePath 被手工改过）。</summary>
    public static GameInstallation? CurrentInstallation => Find(Settings.GamePath);

    public static GameInstallation? Find(string exePath)
    {
        if (string.IsNullOrWhiteSpace(exePath)) return null;
        return Settings.Installations.FirstOrDefault(i => PathsEqual(i.ExePath, exePath));
    }

    /// <summary>
    /// 路径比较：忽略大小写，并先把分隔符统一。
    ///
    /// <para>必须这么做 —— 我们会从好几个地方拿到同一个文件的路径
    /// （<c>FolderBrowserDialog</c>、<c>Directory.EnumerateDirectories</c>、
    /// <c>Process.MainModule.FileName</c>），它们的分隔符不一定一样，
    /// 纯字符串比会把同一个文件判成两个不同的安装。</para>
    /// </summary>
    private static bool PathsEqual(string a, string b)
    {
        if (string.IsNullOrEmpty(a) || string.IsNullOrEmpty(b)) return false;

        return string.Equals(
            Path.TrimEndingDirectorySeparator(a).Replace('/', '\\'),
            Path.TrimEndingDirectorySeparator(b).Replace('/', '\\'),
            StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// 登记一套安装（已登记就只更新别名）。返回被登记的那一项。
    ///
    /// <para><b>不改当前选择</b> —— 那是 <see cref="SwitchTo"/> 的事。</para>
    /// </summary>
    public static GameInstallation? Remember(string exePath, string? alias = null)
    {
        if (string.IsNullOrWhiteSpace(exePath)) return null;
        exePath = Path.GetFullPath(exePath);

        var found = Find(exePath);
        if (found != null)
        {
            if (!string.IsNullOrWhiteSpace(alias)) found.Alias = alias;
            return found;
        }

        var item = new GameInstallation { ExePath = exePath, Alias = alias ?? "" };
        Settings.Installations.Add(item);
        return item;
    }

    /// <summary>忘掉一套安装。忘的正好是当前那套时，当前选择一起清掉。</summary>
    public static void Forget(GameInstallation item)
    {
        Settings.Installations.Remove(item);
        if (PathsEqual(item.ExePath, Settings.GamePath)) Settings.GamePath = "";
    }

    /// <summary>
    /// 切换当前安装。切换**只改设置、不动任何文件**，所以随时可以换回来。
    /// 返回 false = 这个目录已经不是一套像样的 MVZ2（数据目录不见了）。
    /// </summary>
    public static bool SwitchTo(string exePath)
    {
        if (!File.Exists(exePath) || !IsValidGameDir(Path.GetDirectoryName(exePath))) return false;

        Settings.GamePath = Path.GetFullPath(exePath);
        Remember(exePath);
        Save();
        return true;
    }

    /// <summary>
    /// 在一个父目录下找所有 MVZ2 安装。
    ///
    /// <para>认 <c>..._Data</c> 目录（<see cref="IsValidGameDir"/>），所以 GMS2 那类
    /// 没有数据目录、只有 <c>data.win</c> 的版本会被自动排除，不需要专门写规则。</para>
    ///
    /// <para>深度 2 层是为了"指哪一层都行"：<c>E:\Game\PVZ\MVZ2\MVZ2 0.7.0 test-10</c>
    /// 指到 <c>MVZ2</c> 是 1 层、指到 <c>PVZ</c> 是 2 层。</para>
    /// </summary>
    public static List<string> ScanForInstallations(string root, int maxDepth = 2)
    {
        var found = new List<string>();
        var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        void Visit(string dir, int depth)
        {
            if (!visited.Add(dir)) return;
            if (!Directory.Exists(dir)) return;

            if (IsValidGameDir(dir) && FindGameExe(dir) is { } exe)
            {
                found.Add(exe);
                return;   // 找到安装就不用再往它里面翻了
            }

            if (depth <= 0) return;

            IEnumerable<string> subs;
            try { subs = Directory.EnumerateDirectories(dir); }
            catch { return; }

            foreach (var sub in subs)
            {
                // 这两类目录里不可能有独立安装，翻了纯属浪费时间。
                string name = Path.GetFileName(sub);
                if (name.Equals("BepInEx", StringComparison.OrdinalIgnoreCase)) continue;
                if (name.EndsWith("_Data", StringComparison.OrdinalIgnoreCase)) continue;
                Visit(sub, depth - 1);
            }
        }

        Visit(root, maxDepth);
        return found;
    }

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

    /// <summary>反序列化后补齐 null 集合，并做一次旧格式迁移（旧版 state.json 可能缺字段）。</summary>
    private static void Normalize()
    {
        Settings.KnownGamePaths ??= new();
        Settings.CustomGames ??= new();
        Settings.HiddenPresets ??= new();
        Settings.Installations ??= new();
        if (Settings.GameName.Length == 0) Settings.GameName = "Minecraft vs Zombies 2";

        // 旧版只记一个 GamePath，另外还有上游留下的两个"名字 → 路径"字典。
        // 全部并进安装列表：老用户的当前选择、以及以前选过的目录都不会丢。
        // 这里**不校验文件是否存在** —— 盘符拔了、目录改名了也先留着，
        // 界面上标一句"找不到"比默默消失好得多。
        Remember(Settings.GamePath);
        foreach (var p in Settings.KnownGamePaths.Values) Remember(p);
        foreach (var p in Settings.CustomGames.Values) Remember(p);

        // 并完就没用了：安装列表已经是唯一的真相。
        Settings.KnownGamePaths.Clear();
        Settings.CustomGames.Clear();
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
    /// 由 <c>MVZ2_GAME_DIR</c> 环境变量、已登记的安装（<see cref="AppSettings.Installations"/>）
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
            var loose = Directory.EnumerateFiles(dir, "MinecraftVSZombies2*.exe", SearchOption.TopDirectoryOnly)
                .ToList();

            // 通配符可能命中别的 exe（某个版本的调试副本、改名后的管理器…）。
            // 先只认**与 _Data 目录同名**的那个，实在没有才退回"最小的那个"（老行为）。
            foreach (var f in loose)
            {
                string stem = Path.GetFileNameWithoutExtension(f);
                if (Directory.Exists(Path.Combine(dir, stem + "_Data"))) return f;
            }

            return loose.OrderBy(f => f.Length).FirstOrDefault();
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

        // 已登记的安装（按登记顺序）—— 缺了 exe 就现找一次，路径挪过位置也还能认出来。
        foreach (var inst in Settings.Installations)
        {
            if (inst.Exists) return inst.ExePath;
            if (FindGameExe(inst.Directory) is { } exe) return exe;
        }

        foreach (var preset in Presets)
            foreach (var p in preset.DefaultPaths)
                if (File.Exists(p)) return p;

        return null;
    }

    /// <summary>
    /// 当前安装正在运行的游戏进程。
    ///
    /// <para><b>必须按完整路径匹配</b>：所有版本的主程序都叫
    /// <c>MinecraftVSZombies2.exe</c>，只按进程名匹配会命中**别的版本** ——
    /// 那意味着"停止游戏"会杀掉你正在玩的另一套安装。
    /// 先用名字筛（便宜），再逐个查主模块路径（贵，但只剩几个）。</para>
    /// </summary>
    public static List<System.Diagnostics.Process> RunningGameProcesses()
    {
        var result = new List<System.Diagnostics.Process>();

        string? name = GameProcessName;
        string? exe = GameExePath;
        if (string.IsNullOrEmpty(name) || string.IsNullOrEmpty(exe)) return result;

        try
        {
            foreach (var p in System.Diagnostics.Process.GetProcessesByName(name))
            {
                try
                {
                    string? path = p.MainModule?.FileName;
                    if (path != null && PathsEqual(path, exe)) result.Add(p);
                    else p.Dispose();
                }
                // 权限不够读不到路径，或进程刚好退出 —— 一律当作"不是它"。
                catch { p.Dispose(); }
            }
        }
        catch { /* 枚举失败就当没在跑 */ }

        return result;
    }

    /// <summary>
    /// 当前安装的游戏是否在跑。在跑的时候 <c>BepInEx\plugins\*.dll</c> 是被锁住的，
    /// 任何改名/删文件操作都会失败，所以调用方要先问一句。
    /// </summary>
    public static bool IsGameRunning()
    {
        var running = RunningGameProcesses();
        foreach (var p in running) p.Dispose();
        return running.Count > 0;
    }
}
