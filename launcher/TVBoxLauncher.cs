using System;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;

internal static class TVBoxLauncher
{
    const string AppUserModelId = "TVBox.Windows";
    const uint ErrorIcon = 0x10;

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    static extern int SetCurrentProcessExplicitAppUserModelID(string appId);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    static extern int MessageBoxW(IntPtr owner, string text, string caption, uint type);

    [STAThread]
    static void Main()
    {
        try { SetCurrentProcessExplicitAppUserModelID(AppUserModelId); } catch { }

        var appDirectory = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "app");
        var executable = Path.Combine(appDirectory, "TVBox.exe");
        if (!File.Exists(executable))
        {
            ShowError("TVBox 主程序不完整。请重新下载并完整解压发布包，不要单独移动 TVBox.exe。");
            return;
        }

        try
        {
            var process = Process.Start(new ProcessStartInfo
            {
                FileName = executable,
                WorkingDirectory = appDirectory,
                UseShellExecute = true,
            });
            if (process == null) ShowError("无法启动 TVBox 主程序。");
        }
        catch (Exception e)
        {
            ShowError("无法启动 TVBox 主程序。\r\n\r\n" + e.Message);
        }
    }

    static void ShowError(string message)
    {
        MessageBoxW(IntPtr.Zero, message, "TVBox", ErrorIcon);
    }
}
