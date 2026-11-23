using System;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;

namespace Kaeo.LlmProxy.VSExtension.ToolWindow;

/// <summary>
/// Makes a WPF window's OS-drawn title bar follow the Visual Studio color scheme. VS darkens the
/// captions of its own windows via the DWM "immersive dark mode" attribute, but a WPF window an
/// extension opens keeps the light OS caption even in a dark theme. This applies that attribute to
/// the window's HWND and re-applies it whenever the VS theme changes, so the caption matches the
/// rest of the IDE in both light and dark schemes.
/// </summary>
internal static class WindowCaptionTheme
{
    // DWMWA_USE_IMMERSIVE_DARK_MODE: 20 on Win10 20H1+/Win11, 19 on earlier Win10 builds.
    private const int DwmUseImmersiveDarkMode = 20;
    private const int DwmUseImmersiveDarkModeLegacy = 19;

    [DllImport("dwmapi.dll", PreserveSig = true)]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);

    /// <summary>
    /// Themes <paramref name="window"/>'s caption to the current VS scheme and keeps it in sync.
    /// Safe to call before the HWND exists (applies on SourceInitialized); unsubscribes on Closed
    /// so the static theme event never keeps a closed window alive.
    /// </summary>
    public static void FollowVsTheme(Window window)
    {
        if (window is null)
            return;

        void Apply() => SetDarkMode(window, ThemeColors.IsDarkTheme);

        void onSourceInitialized(object _1, EventArgs _2) => Apply();
        void onThemeChanged(object _1, EventArgs _2) => Apply();

        window.SourceInitialized += onSourceInitialized;
        ThemeColors.ThemeChanged += onThemeChanged;
        window.Closed += (_, _) =>
        {
            window.SourceInitialized -= onSourceInitialized;
            ThemeColors.ThemeChanged -= onThemeChanged;
        };

        // If the window was already realized before this call, theme it now.
        if (new WindowInteropHelper(window).Handle != IntPtr.Zero)
            Apply();
    }

    private static void SetDarkMode(Window window, bool dark)
    {
        try
        {
            var hwnd = new WindowInteropHelper(window).Handle;
            if (hwnd == IntPtr.Zero)
                return;

            int value = dark ? 1 : 0;
            // Try the modern attribute first; fall back to the pre-20H1 value. On systems with
            // neither (pre-1809) both fail and the caption simply stays as-is — graceful no-op.
            if (DwmSetWindowAttribute(hwnd, DwmUseImmersiveDarkMode, ref value, sizeof(int)) != 0)
                DwmSetWindowAttribute(hwnd, DwmUseImmersiveDarkModeLegacy, ref value, sizeof(int));
        }
        catch (Exception ex)
        {
            Core.DebugLog.Warn($"Could not theme window caption: {ex.Message}");
        }
    }
}