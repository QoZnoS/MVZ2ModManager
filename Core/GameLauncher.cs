namespace MVZ2ModManager.Core;

/// <summary>
/// 「启动游戏」这一步的决策与动作。
///
/// <para>拆出来只有一个理由：让自检能覆盖它。弹窗和 <c>Process.Start</c> 留在界面层，
/// 这里只保留"要不要问、问完算不算数"这段逻辑。</para>
///
/// <para>这段逻辑写错过一次：以前它问的是"要不要重新打开 BepInEx 并启动游戏"，
/// 把两件事揉成一句话，用户答"否"之后既没重新打开、游戏也没启动 ——
/// 于是"用管理器启动一次原版游戏"这条唯一的路被弹窗堵死了。
/// 这种错误看代码不容易发现，但一个断言就能钉死。</para>
/// </summary>
internal static class GameLauncher
{
    /// <summary>
    /// 启动前是否该继续。<paramref name="ask"/> 由界面层提供（真的弹窗），
    /// **只在确实需要问的时候才调用**；返回 false = 用户取消了启动。
    /// </summary>
    public static bool ShouldStart(
        BepInExState state, Func<(string Text, string Caption, bool Warning), bool> ask)
    {
        if (BepInExManager.LaunchPrompt(state) is not { } prompt) return true;
        return ask(prompt);
    }

    /// <summary>启动主程序。<paramref name="start"/> 不传就是真开进程（自检会传一个假的）。</summary>
    public static void Start(string exePath, Action<string>? start = null)
    {
        if (start is not null) { start(exePath); return; }

        System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(exePath)
        {
            UseShellExecute = true,
            WorkingDirectory = Path.GetDirectoryName(exePath),
        });
    }
}
