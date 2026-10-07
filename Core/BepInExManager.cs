namespace MVZ2ModManager.Core;

/// <summary>BepInEx 注入环境所处的状态。</summary>
/// <remarks>
/// 把「没装」和「装了但被改名」分开是必须的：两者都会让模组静默消失，
/// 但只有后者是本工具造成的、也只有后者能在设置页一键恢复。
/// 混成一句话会让没装 BepInEx 的人以为自己的 winhttp.dll 被人动过。
/// </remarks>
internal enum BepInExState
{
    /// <summary>游戏目录里没有 <c>BepInEx</c> 文件夹 —— 这套安装上根本没有模组加载器。</summary>
    NotInstalled,

    /// <summary>装了，但注入器 <c>winhttp.dll</c> 既不在原位也没有 <c>.disabled</c> —— 环境不完整。</summary>
    InjectorMissing,

    /// <summary>装了且被关闭（<c>winhttp.dll.disabled</c>）。</summary>
    Disabled,

    /// <summary>装了且启用（<c>winhttp.dll</c> 在原位）。</summary>
    Enabled,
}

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

    /// <summary>判定当前状态。<b>注意先看有没有装、再看注入器</b> —— 顺序反了就会把
    /// "没装"说成"被改名"。</summary>
    public static BepInExState GetState(string gameDir)
    {
        if (File.Exists(Path.Combine(gameDir, "winhttp.dll"))) return BepInExState.Enabled;
        if (File.Exists(Path.Combine(gameDir, "winhttp.dll.disabled"))) return BepInExState.Disabled;
        return IsInstalled(gameDir) ? BepInExState.InjectorMissing : BepInExState.NotInstalled;
    }

    /// <summary>总开关此刻是否有意义 —— 只有装了、且注入器还在（无论哪一侧）才谈得上切换。</summary>
    public static bool CanToggle(string gameDir) =>
        GetState(gameDir) is BepInExState.Enabled or BepInExState.Disabled;

    /// <summary>
    /// 状态栏/报告里的一句话。<b>启用时返回 null</b>（正常状态不值得占位置）。
    /// </summary>
    public static string? Describe(BepInExState state) => state switch
    {
        BepInExState.Enabled => null,
        BepInExState.Disabled => "⚠ BepInEx 已关闭（winhttp.dll.disabled）— 不会加载任何模组",
        BepInExState.InjectorMissing => "⚠ BepInEx 已安装但缺少 winhttp.dll 注入器 — 不会加载任何模组",
        _ => "未安装 BepInEx — 不能加载任何模组",
    };

    /// <summary>
    /// 启动游戏前的确认文案。<b>返回 null 表示不必问，直接启动。</b>
    ///
    /// <para>这里刻意<b>没有</b>"顺便帮你重新打开 BepInEx"这个选项：以前那版把
    /// "重新打开？"和"启动游戏？"揉成一问，用户答"否"之后既没重新打开、游戏也没启动 ——
    /// 于是"用管理器启动一次原版游戏"这条唯一的路被自己的弹窗堵死了。
    /// 重新打开是设置页总开关的职责，启动按钮只管启动。</para>
    /// </summary>
    public static (string Text, string Caption, bool Warning)? LaunchPrompt(BepInExState state) => state switch
    {
        BepInExState.Enabled => null,

        BepInExState.Disabled => (
            "BepInEx 处于关闭状态（winhttp.dll.disabled），这次启动不会加载任何模组。\n\n"
            + "要在不启用模组的情况下开始游戏吗？",
            "模组已关闭", true),

        BepInExState.InjectorMissing => (
            "BepInEx 已安装，但缺少注入器 winhttp.dll，这次启动不会加载任何模组。\n\n"
            + "要在不启用模组的情况下开始游戏吗？",
            "缺少 winhttp.dll", true),

        _ => (
            "这个游戏目录里没有安装 BepInEx，游戏会以原版方式启动，不会加载任何模组。\n\n"
            + "要开始游戏吗？",
            "未安装 BepInEx", false),
    };

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
