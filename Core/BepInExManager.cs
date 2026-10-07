namespace MVZ2ModManager.Core;

/// <summary>
/// BepInEx 环境的探测与「总开关」。
///
/// <para><b>本类刻意不含任何安装/更新/卸载逻辑。</b> MVZ2 这套环境是
/// <c>runtime\</c> 里手工维护的（BepInEx 6 be.788 + 由该游戏二进制生成的 interop），
/// 用通用包去"安装/更新 BepInEx"会把它换成 Mono 版或需要重新生成 interop，
/// 直接把环境搞坏。</para>
///
/// <para><b>总开关</b>：BepInEx 由 <c>winhttp.dll</c> 注入，把它改名成
/// <c>winhttp.dll.disabled</c> 就等于整套插件全部关闭 —— 这是唯一一个"关掉一切"的
/// 安全手段（不动任何 dll，可一键还原）。</para>
/// </summary>
internal static class BepInExManager
{
    public static bool IsInstalled(string gameDir) =>
        Directory.Exists(Path.Combine(gameDir, "BepInEx"));

    /// <summary>插件是否处于启用状态（即 <c>winhttp.dll</c> 还在原位）。</summary>
    public static bool ModsEnabled(string gameDir) =>
        File.Exists(Path.Combine(gameDir, "winhttp.dll"));

    public static bool HasDisabledMarker(string gameDir) =>
        File.Exists(Path.Combine(gameDir, "winhttp.dll.disabled"));

    /// <summary>切换总开关；返回切换后是否处于启用状态。文件被占用时抛异常（游戏在运行）。</summary>
    public static bool ToggleMods(string gameDir)
    {
        string active = Path.Combine(gameDir, "winhttp.dll");
        string disabled = Path.Combine(gameDir, "winhttp.dll.disabled");

        if (File.Exists(active))
        {
            if (File.Exists(disabled)) File.Delete(disabled);
            File.Move(active, disabled);
            return false;
        }

        if (File.Exists(disabled))
        {
            File.Move(disabled, active);
            return true;
        }

        return false;
    }

    /// <summary>日志文件是否已经存在（LogsPanel 用它决定要不要提示"先启动一次游戏"）。</summary>
    public static bool HasLog(string? logPath) =>
        !string.IsNullOrEmpty(logPath) && File.Exists(logPath);
}
