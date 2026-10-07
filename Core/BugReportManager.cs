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
        sb.AppendLine("MVZ2 Mod Manager 问题报告");
        sb.AppendLine($"生成时间：{DateTime.Now:yyyy-MM-dd HH:mm:ss}");
        sb.AppendLine($"程序版本：{Program.VersionString}");
        sb.AppendLine($"操作系统：{Environment.OSVersion}");
        sb.AppendLine($".NET 运行时：{Environment.Version}");
        sb.AppendLine($"64 位系统：{Environment.Is64BitOperatingSystem}");
        sb.AppendLine($"游戏：{AppState.Settings.GameName}（{AppState.CurrentDisplayName}）");
        sb.AppendLine($"游戏路径：{AppState.Settings.GamePath}");

        var gameDir = AppState.GameDir;
        if (gameDir != null)
        {
            sb.AppendLine($"BepInEx 是否存在：{BepInExManager.IsInstalled(gameDir)}");
            sb.AppendLine($"BepInEx 状态    ：{BepInExManager.GetState(gameDir)}");
            sb.AppendLine($"模组总开关（winhttp.dll）：{BepInExManager.ModsEnabled(gameDir)}");
        }

        if (AppState.LogOutputPath is { } log && File.Exists(log))
        {
            try
            {
                var fi = new FileInfo(log);
                sb.AppendLine($"LogOutput.log：{fi.Length} 字节，最后写入 {fi.LastWriteTime:yyyy-MM-dd HH:mm:ss}");
            }
            catch { }
        }

        return sb.ToString();
    }

    private static string BuildModList()
    {
        var mods = ModInstaller.GetInstalled();
        if (mods.Count == 0) return "（没有安装任何模组）";

        var sb = new StringBuilder();
        foreach (var m in mods)
        {
            sb.Append(m.Name)
              .Append(" - ")
              .Append(m.Enabled ? "已启用" : "已禁用");

            if (!string.IsNullOrEmpty(m.Guid)) sb.Append(" - ").Append(m.Guid);
            if (!string.IsNullOrEmpty(m.KnownVersion)) sb.Append(" - v").Append(m.KnownVersion);
            if (m.WritesLevelData) sb.Append(" - 会写入关卡数据 (").Append(m.NativeNamespace).Append(')');

            sb.AppendLine();
        }
        return sb.ToString();
    }
}
