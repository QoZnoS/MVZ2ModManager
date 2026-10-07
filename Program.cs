using MVZ2ModManager.Core;
using MVZ2ModManager.Theme;
using MVZ2ModManager.UI;

namespace MVZ2ModManager;

internal static class Program
{
    /// <summary>本工具自己的版本号（写进 state.json，用来判断要不要弹"更新了什么"）。</summary>
    internal const int CurrentVersion = 1;

    [STAThread]
    private static void Main(string[] args)
    {
        // 自检模式：不开窗，把核心逻辑（元数据 / 依赖 / 存档扫描）跑一遍并落盘。
        // 用法：MVZ2ModManager.exe --selftest [输出文件]
        if (args.Any(a => a.Equals("--selftest", StringComparison.OrdinalIgnoreCase)))
        {
            string outPath = args.SkipWhile(a => !a.Equals("--selftest", StringComparison.OrdinalIgnoreCase))
                                 .Skip(1).FirstOrDefault() ?? "mvz2mm-selftest.txt";
            int code = SelfTest.Run(outPath);
            Environment.Exit(code);
            return;
        }

        ApplicationConfiguration.Initialize();
        Application.SetHighDpiMode(HighDpiMode.SystemAware);

        AppState.Load();

        ThemeEngine.Apply(AppState.Settings.Theme, AppState.Settings.CustomBackground, AppState.Settings.CustomAccent);

        // 先看设置里记的路径，再看环境变量 / 历史选择。
        if (!IsUsableGamePath(AppState.Settings.GamePath))
        {
            string? detected = AppState.AutoDetectGame();
            if (detected != null)
            {
                AppState.Settings.GamePath = detected;
                AppState.Save();
            }
        }

        if (!IsUsableGamePath(AppState.Settings.GamePath))
        {
            using var picker = new GamePickerForm();
            picker.ShowDialog();
            if (!picker.Confirmed) return;
        }

        if (!IsUsableGamePath(AppState.Settings.GamePath))
            return;

        Application.Run(new MainForm());
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
