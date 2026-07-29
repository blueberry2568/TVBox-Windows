using System.Runtime.InteropServices;
using Microsoft.UI.Xaml;

namespace FongMi.TV.Core;

/// <summary>Keeps the native window frame from showing through the WinUI compositor during playback transitions.</summary>
internal static partial class WindowFrameStyle
{
    const int DwmwaWindowCornerPreference = 33;
    const int DwmwaBorderColor = 34;
    const int DwmwaCaptionColor = 35;
    const int DwmWindowCornerPreferenceDefault = 0;
    const int DwmWindowCornerPreferenceDoNotRound = 1;
    const uint DwmColorDefault = 0xFFFFFFFF;
    const uint DwmColorBlack = 0x00000000;

    const int GwlStyle = -16;
    const int GwlExStyle = -20;
    const int GclpBackgroundBrush = -10;
    const long WsBorder = 0x00800000L;
    const long WsDlgFrame = 0x00400000L;
    const long WsThickFrame = 0x00040000L;
    const long WsExDlgModalFrame = 0x00000001L;
    const long WsExWindowEdge = 0x00000100L;
    const long WsExClientEdge = 0x00000200L;
    const long WsExStaticEdge = 0x00020000L;
    const int BlackBrush = 4;

    const uint WmEraseBackground = 0x0014;
    const uint SwpNoSize = 0x0001;
    const uint SwpNoMove = 0x0002;
    const uint SwpNoZOrder = 0x0004;
    const uint SwpNoActivate = 0x0010;
    const uint SwpFrameChanged = 0x0020;
    const uint RdwInvalidate = 0x0001;
    const uint RdwErase = 0x0004;
    const uint RdwFrame = 0x0400;
    static readonly nuint SubclassId = 0x5456424F; // "TVBO"

    static readonly SubclassProc WindowSubclassProc = OnWindowMessage;
    static nint _hwnd;
    static nint _defaultBackgroundBrush;
    static nint _normalStyle;
    static nint _normalExStyle;
    static bool _subclassAttached;
    static bool _backgroundCaptured;
    static bool _normalStyleCaptured;
    static bool _borderlessStyleActive;
    static bool _immersive;

    public static void Attach(Window window)
    {
        if (_subclassAttached) return;
        var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(window);
        if (hwnd == 0) return;

        _hwnd = hwnd;
        _defaultBackgroundBrush = GetClassLongPtr(hwnd, GclpBackgroundBrush);
        _backgroundCaptured = true;
        _subclassAttached = SetWindowSubclass(hwnd, WindowSubclassProc, SubclassId, 0);
    }

    public static void Detach(Window window)
    {
        try
        {
            var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(window);
            RestoreBorderlessStyle(hwnd);
            SetResizeBackground(hwnd, false);
            if (_subclassAttached) _ = RemoveWindowSubclass(hwnd, WindowSubclassProc, SubclassId);
        }
        catch { }
        finally
        {
            _hwnd = 0;
            _subclassAttached = false;
            _backgroundCaptured = false;
            _normalStyleCaptured = false;
            _immersive = false;
        }
    }

    public static void SetImmersive(Window window, bool immersive, bool borderless = false)
    {
        try
        {
            Attach(window);
            var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(window);
            if (immersive && !_immersive) CaptureNormalStyle(hwnd);
            _immersive = immersive;

            // WM_ERASEBKGND covers the interval in which WinUI has accepted a resize
            // but its composition surface has not produced the next frame yet.
            SetResizeBackground(hwnd, immersive);
            SetDwmFrame(hwnd, immersive);
            SetBorderlessStyle(hwnd, immersive && borderless);
            if (!immersive) _normalStyleCaptured = false;

            // Changing the presenter/style can recreate the non-client frame. Apply
            // the DWM colors once more after SWP_FRAMECHANGED and request a repaint.
            SetDwmFrame(hwnd, immersive);
            _ = RedrawWindow(hwnd, 0, 0, RdwInvalidate | RdwErase | RdwFrame);
        }
        catch
        {
            // Frame color/corner preferences are best-effort on older Windows builds.
        }
    }

    static void SetDwmFrame(nint hwnd, bool immersive)
    {
        var color = immersive ? DwmColorBlack : DwmColorDefault;
        _ = DwmSetWindowAttributeColor(hwnd, DwmwaBorderColor, ref color, sizeof(uint));
        _ = DwmSetWindowAttributeColor(hwnd, DwmwaCaptionColor, ref color, sizeof(uint));

        var corner = immersive ? DwmWindowCornerPreferenceDoNotRound : DwmWindowCornerPreferenceDefault;
        _ = DwmSetWindowAttributeInt(hwnd, DwmwaWindowCornerPreference, ref corner, sizeof(int));
    }

    static void SetBorderlessStyle(nint hwnd, bool borderless)
    {
        if (!borderless)
        {
            RestoreBorderlessStyle(hwnd);
            return;
        }

        var style = GetWindowLongPtr(hwnd, GwlStyle);
        var exStyle = GetWindowLongPtr(hwnd, GwlExStyle);
        if (!_borderlessStyleActive)
        {
            if (!_normalStyleCaptured) CaptureNormalStyle(hwnd);
            _borderlessStyleActive = true;
        }

        var borderlessStyle = new nint(style.ToInt64() & ~(WsBorder | WsDlgFrame | WsThickFrame));
        var borderlessExStyle = new nint(exStyle.ToInt64() &
            ~(WsExDlgModalFrame | WsExWindowEdge | WsExClientEdge | WsExStaticEdge));
        if (borderlessStyle == style && borderlessExStyle == exStyle) return;

        _ = SetWindowLongPtr(hwnd, GwlStyle, borderlessStyle);
        _ = SetWindowLongPtr(hwnd, GwlExStyle, borderlessExStyle);
        ApplyFrameChange(hwnd);
    }

    static void CaptureNormalStyle(nint hwnd)
    {
        _normalStyle = GetWindowLongPtr(hwnd, GwlStyle);
        _normalExStyle = GetWindowLongPtr(hwnd, GwlExStyle);
        _normalStyleCaptured = true;
    }

    static void RestoreBorderlessStyle(nint hwnd)
    {
        if (!_borderlessStyleActive || hwnd == 0) return;
        _ = SetWindowLongPtr(hwnd, GwlStyle, _normalStyle);
        _ = SetWindowLongPtr(hwnd, GwlExStyle, _normalExStyle);
        _borderlessStyleActive = false;
        ApplyFrameChange(hwnd);
    }

    static void ApplyFrameChange(nint hwnd) => SetWindowPos(
        hwnd, 0, 0, 0, 0, 0,
        SwpNoSize | SwpNoMove | SwpNoZOrder | SwpNoActivate | SwpFrameChanged);

    static void SetResizeBackground(nint hwnd, bool immersive)
    {
        if (!_backgroundCaptured)
        {
            _defaultBackgroundBrush = GetClassLongPtr(hwnd, GclpBackgroundBrush);
            _backgroundCaptured = true;
        }

        var brush = immersive ? GetStockObject(BlackBrush) : _defaultBackgroundBrush;
        _ = SetClassLongPtr(hwnd, GclpBackgroundBrush, brush);
    }

    static nint OnWindowMessage(
        nint hwnd,
        uint message,
        nint wParam,
        nint lParam,
        nuint subclassId,
        nuint referenceData)
    {
        if (_immersive && hwnd == _hwnd && message == WmEraseBackground && wParam != 0)
        {
            if (GetClientRect(hwnd, out var rect))
            {
                _ = FillRect(wParam, ref rect, GetStockObject(BlackBrush));
                return 1;
            }
        }
        return DefSubclassProc(hwnd, message, wParam, lParam);
    }

    [StructLayout(LayoutKind.Sequential)]
    struct Rect
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    delegate nint SubclassProc(
        nint hwnd,
        uint message,
        nint wParam,
        nint lParam,
        nuint subclassId,
        nuint referenceData);

    [LibraryImport("dwmapi.dll", EntryPoint = "DwmSetWindowAttribute")]
    private static partial int DwmSetWindowAttributeColor(
        nint hwnd,
        int attribute,
        ref uint value,
        int valueSize);

    [LibraryImport("dwmapi.dll", EntryPoint = "DwmSetWindowAttribute")]
    private static partial int DwmSetWindowAttributeInt(
        nint hwnd,
        int attribute,
        ref int value,
        int valueSize);

    [LibraryImport("user32.dll", EntryPoint = "GetClassLongPtrW")]
    private static partial nint GetClassLongPtr(nint hwnd, int index);

    [LibraryImport("user32.dll", EntryPoint = "SetClassLongPtrW")]
    private static partial nint SetClassLongPtr(nint hwnd, int index, nint value);

    [LibraryImport("user32.dll", EntryPoint = "GetWindowLongPtrW")]
    private static partial nint GetWindowLongPtr(nint hwnd, int index);

    [LibraryImport("user32.dll", EntryPoint = "SetWindowLongPtrW")]
    private static partial nint SetWindowLongPtr(nint hwnd, int index, nint value);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool SetWindowPos(
        nint hwnd,
        nint insertAfter,
        int x,
        int y,
        int width,
        int height,
        uint flags);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool RedrawWindow(nint hwnd, nint updateRect, nint updateRegion, uint flags);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetClientRect(nint hwnd, out Rect rect);

    [LibraryImport("user32.dll")]
    private static partial int FillRect(nint deviceContext, ref Rect rect, nint brush);

    [LibraryImport("gdi32.dll")]
    private static partial nint GetStockObject(int objectType);

    [DllImport("comctl32.dll", ExactSpelling = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    static extern bool SetWindowSubclass(
        nint hwnd,
        SubclassProc callback,
        nuint subclassId,
        nuint referenceData);

    [DllImport("comctl32.dll", ExactSpelling = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    static extern bool RemoveWindowSubclass(nint hwnd, SubclassProc callback, nuint subclassId);

    [DllImport("comctl32.dll", ExactSpelling = true)]
    static extern nint DefSubclassProc(nint hwnd, uint message, nint wParam, nint lParam);
}
