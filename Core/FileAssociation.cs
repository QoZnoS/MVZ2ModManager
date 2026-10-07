using Microsoft.Win32;
using System.Runtime.InteropServices;

namespace MVZ2ModManager.Core;

/// <summary>
/// 把 <c>.mvz2pack</c> 关联到本程序，这样双击一份模组包就能直接导入。
/// 只写 <c>HKCU</c>（当前用户），注册表里内容一致时直接返回。
/// </summary>
internal static class FileAssociation
{
    private const string ProgId = "MVZ2ModManager.mvz2pack";

    /// <summary>模组包扩展名（含点）。</summary>
    public const string Extension = ".mvz2pack";

    public static void EnsureRegistered()
    {
        try
        {
            string exePath = Environment.ProcessPath
                ?? System.Diagnostics.Process.GetCurrentProcess().MainModule?.FileName
                ?? "";
            if (exePath.Length == 0) return;

            string desiredCommand = $"\"{exePath}\" \"%1\"";

            using var cmdKey = Registry.CurrentUser.CreateSubKey($@"Software\Classes\{ProgId}\shell\open\command");
            if (cmdKey.GetValue("") as string == desiredCommand) return;

            using var extKey = Registry.CurrentUser.CreateSubKey($@"Software\Classes\{Extension}");
            extKey.SetValue("", ProgId);

            using var progIdKey = Registry.CurrentUser.CreateSubKey($@"Software\Classes\{ProgId}");
            progIdKey.SetValue("", "MVZ2 模组包");

            using var iconKey = Registry.CurrentUser.CreateSubKey($@"Software\Classes\{ProgId}\DefaultIcon");
            iconKey.SetValue("", $"\"{exePath}\",0");

            cmdKey.SetValue("", desiredCommand);

            SHChangeNotify(0x08000000, 0x0000, IntPtr.Zero, IntPtr.Zero);
        }
        catch { /* 关联失败不该影响程序启动 */ }
    }

    [DllImport("shell32.dll")]
    private static extern void SHChangeNotify(int wEventId, int uFlags, IntPtr dwItem1, IntPtr dwItem2);
}
