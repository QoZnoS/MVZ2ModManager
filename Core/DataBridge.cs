using System.Text.Json;

namespace MVZ2ModManager.Core;

/// <summary>
/// 落在「游戏目录」里的持久化数据（相对 <see cref="AppState.DataDir"/>）：
/// 快照点（loadout）与配置快照目录。
///
/// <para>放在游戏目录而不是 <c>%APPDATA%</c> 是刻意的：换机器、换安装目录时
/// 整套「哪套模组开着」跟着走，不用重新配。</para>
///
/// <para>注意与 <see cref="AppState"/> 的分工：<c>%APPDATA%</c> 存的是**本程序**的
/// 偏好（主题、上次选的游戏），这里存的是**针对这套游戏安装**的数据。</para>
/// </summary>
internal static class DataBridge
{
    private static readonly JsonSerializerOptions _json = new() { WriteIndented = true, IncludeFields = true };

    public static string? SettingsDir => AppState.DataDir is { } d ? Path.Combine(d, "settings") : null;
    public static string? LoadoutsDir => AppState.DataDir is { } d ? Path.Combine(d, "loadouts") : null;
    public static string? LoadoutsPath => LoadoutsDir is { } d ? Path.Combine(d, "loadouts.json") : null;

    /// <summary>某个快照点的配置快照目录（整份 <c>BepInEx/config</c> 的副本）。</summary>
    public static string? ConfigSnapshotDir(int number) =>
        LoadoutsDir is { } d ? Path.Combine(d, "config_snapshots", number.ToString()) : null;

    public static bool HasDataFolder => AppState.DataDir is { } d && Directory.Exists(d);

    // ------------------------------------------------------------- 快照点

    public static List<ModLoadout> LoadLoadouts()
    {
        if (LoadoutsPath is not { } path || !File.Exists(path)) return new();
        try
        {
            return JsonSerializer.Deserialize<List<ModLoadout>>(File.ReadAllText(path), _json) ?? new();
        }
        catch
        {
            // 坏文件不该让整个面板炸掉：改名留档，从空开始。
            try { File.Move(path, path + ".corrupt", overwrite: true); } catch { }
            return new();
        }
    }

    public static void SaveLoadouts(List<ModLoadout> loadouts)
    {
        if (LoadoutsPath is not { } path || LoadoutsDir is not { } dir) return;
        Directory.CreateDirectory(dir);
        WriteAtomic(path, JsonSerializer.Serialize(loadouts, _json));
    }

    /// <summary>先写临时文件再替换 —— 中途断电/被杀不会留下半个 JSON。</summary>
    private static void WriteAtomic(string path, string content)
    {
        string tmp = path + ".tmp";
        File.WriteAllText(tmp, content);
        if (File.Exists(path)) File.Delete(path);
        File.Move(tmp, path);
    }
}
