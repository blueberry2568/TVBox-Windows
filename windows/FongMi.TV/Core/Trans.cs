using System.Runtime.InteropServices;

namespace FongMi.TV.Core;

/// <summary>繁简转换：使用 Win32 LCMapStringEx（等价于 Android 端 Trans 的 s2t/t2s）。</summary>
public static class Trans
{
    const uint LCMAP_SIMPLIFIED_CHINESE = 0x02000000;
    const uint LCMAP_TRADITIONAL_CHINESE = 0x04000000;

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    static extern int LCMapStringEx(string lpLocaleName, uint dwMapFlags, string lpSrcStr, int cchSrc, char[] lpDestStr, int cchDest, IntPtr lpVersionInformation, IntPtr lpReserved, IntPtr sortHandle);

    static string Map(string text, uint flag)
    {
        if (string.IsNullOrEmpty(text)) return text ?? "";
        try
        {
            var buffer = new char[text.Length * 2];
            int len = LCMapStringEx("zh-CN", flag, text, text.Length, buffer, buffer.Length, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero);
            return len > 0 ? new string(buffer, 0, len) : text;
        }
        catch { return text; }
    }

    /// <summary>是否跳过转换（跟随系统语言，简体环境显示不转换）。</summary>
    public static bool Pass() => !System.Globalization.CultureInfo.CurrentUICulture.Name.Contains("Hant") && !System.Globalization.CultureInfo.CurrentUICulture.Name.Contains("TW") && !System.Globalization.CultureInfo.CurrentUICulture.Name.Contains("HK");

    public static string S2T(string text) => Map(text, LCMAP_TRADITIONAL_CHINESE);
    public static string T2S(string text) => Map(text, LCMAP_SIMPLIFIED_CHINESE);
    public static string S2T(bool auto, string text) => auto && Pass() ? text : S2T(text);
    public static string T2S(bool auto, string text) => auto && Pass() ? text : T2S(text);
}
