using System.IO.Compression;
using System.Text;

namespace MVZ2ModManager.Core;

/// <summary>
/// 一键导出「给作者看」的报告包：日志 + 系统信息 + 当前模组清单 + 存档风险摘要。
/// 目的只有一个 —— 省掉"你能把日志发我吗"那一轮来回。
/// </summary>
internal static class BugReportManager
{
    public static void Export(string destZipPath, IReadOnlyList<string> logLines)
    {
        string staging = Path.Combine(Path.GetTempPath(), "mvz_bugreport_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(staging);
        try
        {
            File.WriteAllLines(Path.Combine(staging, "log.txt"), logLines);
            File.WriteAllText(Path.Combine(staging, "system-info.txt"), BuildSystemInfo());
            File.WriteAllText(Path.Combine(staging, "installed-mods.txt"), BuildModList());

            if (File.Exists(destZipPath)) File.Delete(destZipPath);
            ZipFile.CreateFromDirectory(staging, destZipPath);
        }
        finally
        {
            try { Directory.Delete(staging, recursive: true); } catch { }
        }
    }

    private static string BuildSystemInfo()
    {
        var sb = new StringBuilder();
        sb.AppendLine("MVZ2 Mod Manager Bug Report");
        sb.AppendLine($"Generated: {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
        sb.AppendLine($"App build: {Program.CurrentVersion}");
        sb.AppendLine($"OS: {Environment.OSVersion}");
        sb.AppendLine($".NET runtime: {Environment.Version}");
        sb.AppendLine($"64-bit OS: {Environment.Is64BitOperatingSystem}");
        sb.AppendLine($"Game: {AppState.Settings.GameName}");
        sb.AppendLine($"Game path: {AppState.Settings.GamePath}");

        var gameDir = AppState.GameDir;
        if (gameDir != null)
        {
            sb.AppendLine($"BepInEx present: {BepInExManager.IsInstalled(gameDir)}");
            sb.AppendLine($"Mods enabled (winhttp.dll): {BepInExManager.ModsEnabled(gameDir)}");
        }

        if (AppState.LogOutputPath is { } log && File.Exists(log))
        {
            try
            {
                var fi = new FileInfo(log);
                sb.AppendLine($"LogOutput.log: {fi.Length} bytes, last write {fi.LastWriteTime:yyyy-MM-dd HH:mm:ss}");
            }
            catch { }
        }

        return sb.ToString();
    }

    private static string BuildModList()
    {
        var mods = ModInstaller.GetInstalled();
        if (mods.Count == 0) return "(no mods installed)";

        var sb = new StringBuilder();
        foreach (var m in mods)
        {
            sb.Append(m.Name)
              .Append(" - ")
              .Append(m.Enabled ? "enabled" : "disabled");

            if (!string.IsNullOrEmpty(m.Guid)) sb.Append(" - ").Append(m.Guid);
            if (!string.IsNullOrEmpty(m.KnownVersion)) sb.Append(" - v").Append(m.KnownVersion);
            if (m.WritesLevelData) sb.Append(" - writes level data (").Append(m.NativeNamespace).Append(')');

            sb.AppendLine();
        }
        return sb.ToString();
    }
}
