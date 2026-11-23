using System;
using System.Globalization;
using System.Windows.Media;
using Microsoft.VisualStudio.PlatformUI;

namespace Kaeo.LlmProxy.VSExtension.ToolWindow;

/// <summary>
/// Theme-aware resolution of user color overrides (project theming rule: GUI content must
/// stay legible across VS light/dark schemes and repaint on theme change). Overrides are a
/// user escape hatch: they are re-evaluated whenever the VS theme changes and fall back to
/// the theme brush (null → Binding.DoNothing → the DynamicResource default) when contrast
/// against the effective background/foreground fails.
/// </summary>
internal static class ThemeColors
{
    /// <summary>Minimum WCAG contrast ratio for a user override to be applied.</summary>
    private const double MinContrast = 3.0;

    private static bool s_hooked;
    private static bool s_isDark;
    private static Color s_themeBackground = Color.FromRgb(0xFF, 0xFF, 0xFF);
    private static Color s_themeForeground = Color.FromRgb(0x1E, 0x1E, 0x1E);

    /// <summary>Raised on the UI thread after the VS color scheme changes and colors were re-read.</summary>
    public static event EventHandler? ThemeChanged;

    /// <summary>True when the current VS theme has a dark tool-window background.</summary>
    public static bool IsDarkTheme
    {
        get
        {
            EnsureHooked();
            return s_isDark;
        }
    }

    private static void EnsureHooked()
    {
        if (s_hooked)
            return;
        s_hooked = true;
        Refresh();
        try
        {
            VSColorTheme.ThemeChanged += _ =>
            {
                Refresh();
                ThemeChanged?.Invoke(null, EventArgs.Empty);
            };
        }
        catch (Exception ex)
        {
            // Theme service unavailable (e.g. designer): overrides still get contrast-checked
            // against the light defaults, but no live repaint notifications can be delivered.
            Core.DebugLog.Warn($"Could not subscribe to VS theme changes: {ex.Message}");
        }
    }

    private static void Refresh()
    {
        try
        {
            var bg = VSColorTheme.GetThemedColor(EnvironmentColors.ToolWindowBackgroundColorKey);
            var fg = VSColorTheme.GetThemedColor(EnvironmentColors.ToolWindowTextColorKey);
            s_themeBackground = Color.FromArgb(bg.A, bg.R, bg.G, bg.B);
            s_themeForeground = Color.FromArgb(fg.A, fg.R, fg.G, fg.B);
            s_isDark = Luminance(s_themeBackground) < 0.35;
        }
        catch (Exception)
        {
            // Keep the previous (or light-default) theme colors.
        }
    }

    /// <summary>
    /// Parses "#RRGGBB"/"#AARRGGBB" (invariant culture) or a named color; null on any failure.
    /// </summary>
    public static Color? Parse(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return null;
        try
        {
            var s = value!.Trim();
            if (s.StartsWith("#", StringComparison.Ordinal))
            {
                var digits = s.Substring(1);
                if (digits.Length != 6 && digits.Length != 8)
                    return null;

                byte Component(int offset) => byte.Parse(
                    digits.Substring(offset, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture);

                return digits.Length == 6
                    ? Color.FromRgb(Component(0), Component(2), Component(4))
                    : Color.FromArgb(Component(0), Component(2), Component(4), Component(6));
            }

            return (Color)ColorConverter.ConvertFromString(s);
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// Validates a text-color override against the effective background (the paired
    /// background override when set, otherwise the theme background). Returns the override
    /// when legible, or null to fall back to the theme brush.
    /// </summary>
    public static string? ResolveForeground(string? overrideHex, string? pairedBackgroundHex)
    {
        if (string.IsNullOrWhiteSpace(overrideHex))
            return null;
        EnsureHooked();
        var fg = Parse(overrideHex);
        if (fg is null)
            return null;
        var bg = Parse(pairedBackgroundHex) ?? s_themeBackground;
        return Contrast(fg.Value, bg) >= MinContrast ? overrideHex : null;
    }

    /// <summary>
    /// Validates a background-color override against the effective foreground (the paired
    /// text override when set, otherwise the theme text color). Returns the override when
    /// legible, or null to fall back to the theme brush.
    /// </summary>
    public static string? ResolveBackground(string? overrideHex, string? pairedForegroundHex)
    {
        if (string.IsNullOrWhiteSpace(overrideHex))
            return null;
        EnsureHooked();
        var bg = Parse(overrideHex);
        if (bg is null)
            return null;
        var fg = Parse(pairedForegroundHex) ?? s_themeForeground;
        return Contrast(fg, bg.Value) >= MinContrast ? overrideHex : null;
    }

    private static double Luminance(Color c)
    {
        static double Channel(byte v)
        {
            var s = v / 255.0;
            return s <= 0.03928 ? s / 12.92 : Math.Pow((s + 0.055) / 1.055, 2.4);
        }

        return 0.2126 * Channel(c.R) + 0.7152 * Channel(c.G) + 0.0722 * Channel(c.B);
    }

    private static double Contrast(Color a, Color b)
    {
        var la = Luminance(a);
        var lb = Luminance(b);
        var hi = Math.Max(la, lb);
        var lo = Math.Min(la, lb);
        return (hi + 0.05) / (lo + 0.05);
    }
}