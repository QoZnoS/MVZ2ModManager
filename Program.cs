using System.Reflection;
using MVZ2ModManager.Core;
using MVZ2ModManager.Theme;
using MVZ2ModManager.UI;

namespace MVZ2ModManager;

internal static class Program
{
    /// <summary>
    /// 界面和报告里显示的版本号。取自程序集（也就是 csproj 的 &lt;Version&gt;），
    /// 不在代码里另写一份 —— 两处版本号迟早会对不上。
    /// </summary>
    internal static string VersionString { get; } = ResolveVersion();

    private static string ResolveVersion()
    {
        var asm = typeof(Program).Assembly;

        string? v = asm.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
        if (string.IsNullOrWhiteSpace(v)) v = asm.GetName().Version?.ToString();
        if (string.IsNullOrWhiteSpace(v)) return "1.0.0";

        int plus = v.IndexOf('+');   // 去掉 SourceLink 追加的 +<commit>
        return plus > 0 ? v[..plus] : v;
    }

    [STAThread]
    private static void Main(string[] args)
    {
        // 自检模式：不开窗，把核心逻辑（元数据 / 依赖 / 存档扫描）跑一遍并落盘。
        // 用法：MVZ2ModManager.exe --selftest [输出文件]
        if (args.Any(a => a.Equals("--selftest", StringComparison.OrdinalIgnoreCase)))
        {
            bool roundtrip = args.Any(a => a.Equals("--apply-roundtrip", StringComparison.OrdinalIgnoreCase));

            string outPath = args.SkipWhile(a => !a.Equals("--selftest", StringComparison.OrdinalIgnoreCase))
                                 .Skip(1)
                                 .FirstOrDefault(a => !a.StartsWith("--", StringComparison.Ordinal))
                          ?? "mvz2mm-selftest.txt";

            int code = SelfTest.Run(outPath, roundtrip);
            Environment.Exit(code);
            return;
        }

        // UI 自检：把所有窗体/面板都构造出来并切一遍标签页，然后退出。
        // 用法：MVZ2ModManager.exe --ui-selftest [输出文件]
        if (args.Any(a => a.Equals("--ui-selftest", StringComparison.OrdinalIgnoreCase)))
        {
            ApplicationConfiguration.Initialize();
            Application.SetHighDpiMode(HighDpiMode.SystemAware);

            string uiOutPath = args.SkipWhile(a => !a.Equals("--ui-selftest", StringComparison.OrdinalIgnoreCase))
                                   .Skip(1)
                                   .FirstOrDefault(a => !a.StartsWith("--", StringComparison.Ordinal))
                            ?? "mvz2mm-ui-selftest.txt";

            AppState.Load();
            if (!File.Exists(AppState.Settings.GamePath))
            {
                string? detected = AppState.AutoDetectGame();
                if (detected != null) AppState.Settings.GamePath = detected;
            }

            ThemeEngine.Apply(AppState.Settings.Theme, AppState.Settings.CustomBackground, AppState.Settings.CustomAccent);

            Environment.Exit(UiSelfTest.Run(uiOutPath));
            return;
        }

        // 截图模式（开发用）：把每个标签页画成 PNG，用来肉眼核对版面。
        // 用法：MVZ2ModManager.exe --screenshot [输出目录]
        if (args.Any(a => a.Equals("--screenshot", StringComparison.OrdinalIgnoreCase)))
        {
            ApplicationConfiguration.Initialize();
            Application.SetHighDpiMode(HighDpiMode.SystemAware);

            string shotDir = args.SkipWhile(a => !a.Equals("--screenshot", StringComparison.OrdinalIgnoreCase))
                                .Skip(1)
                                .FirstOrDefault(a => !a.StartsWith("--", StringComparison.Ordinal))
                             ?? "screenshots";

            AppState.Load();
            if (!File.Exists(AppState.Settings.GamePath))
            {
                string? detected = AppState.AutoDetectGame();
                if (detected != null) AppState.Settings.GamePath = detected;
            }

            ThemeEngine.Apply(AppState.Settings.Theme, AppState.Settings.CustomBackground, AppState.Settings.CustomAccent);

            Environment.Exit(ScreenshotDump.Run(shotDir));
            return;
        }

        ApplicationConfiguration.Initialize();
        Application.SetHighDpiMode(HighDpiMode.SystemAware);

        // 双击 .mvz2pack 时把关联写到当前用户（内容一致时直接返回）。
        FileAssociation.EnsureRegistered();

        bool skipPicker = args.Any(a => a.Equals("--skip-picker", StringComparison.OrdinalIgnoreCase));

        string? pendingPackPath = args.Length > 0 && File.Exists(args[0]) &&
            Path.GetExtension(args[0]).Equals(FileAssociation.Extension, StringComparison.OrdinalIgnoreCase)
            ? args[0] : null;

        AppState.Load();

        ThemeEngine.Apply(AppState.Settings.Theme, AppState.Settings.CustomBackground, AppState.Settings.CustomAccent);

        // 先看设置里记的路径，再看环境变量 / 历史选择。
        if (!skipPicker && !IsUsableGamePath(AppState.Settings.GamePath))
        {
            string? detected = AppState.AutoDetectGame();
            if (detected != null)
            {
                AppState.Settings.GamePath = detected;
                AppState.Save();
            }
        }

        if (!skipPicker && !IsUsableGamePath(AppState.Settings.GamePath))
        {
            using var picker = new GamePickerForm();
            picker.ShowDialog();
            if (!picker.Confirmed) return;
        }

        if (!IsUsableGamePath(AppState.Settings.GamePath))
            return;

        Application.Run(new MainForm(pendingPackPath));
    }

    /// <summary>路径存在、且游戏根目录看起来真是一套 MVZ2。</summary>
    private static bool IsUsableGamePath(string path) =>
        path.Length > 0 && File.Exists(path) &&
        AppState.IsValidGameDir(Path.GetDirectoryName(path));

    internal static void RestartApp(bool skipPicker)
    {
        string? exe = Environment.ProcessPath
            ?? System.Diagnostics.Process.GetCurrentProcess().MainModule?.FileName
            ?? Application.ExecutablePath;

        if (string.IsNullOrEmpty(exe)) return;

        var psi = new System.Diagnostics.ProcessStartInfo(exe);
        if (skipPicker) psi.ArgumentList.Add("--skip-picker");
        System.Diagnostics.Process.Start(psi);
        Environment.Exit(0);
    }
}
