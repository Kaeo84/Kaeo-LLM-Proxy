using System;
using System.Globalization;
using System.Windows.Data;
using System.Windows.Media;

namespace Kaeo.LlmProxy.VSExtension.ToolWindow
{
    /// <summary>
    /// Renders a theme-validated hex color override as a frozen SolidColorBrush. The bound
    /// values are pre-validated by <see cref="ThemeColors"/> (contrast-checked against the
    /// current VS theme, preset hexes mapped to their light/dark variant) and the source
    /// properties re-raise change notifications on VSColorTheme.ThemeChanged, so overrides
    /// repaint on theme change and fall back to the theme when illegible.
    /// Null/empty/invalid returns Binding.DoNothing so the themed DynamicResource default
    /// (style setter or trigger) wins instead of setting the property to null.
    /// </summary>
    internal sealed class HexToBrushConverter : IValueConverter
    {
        public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        {
            var color = ThemeColors.Parse(value as string);
            if (color is null)
                return Binding.DoNothing;

            var brush = new SolidColorBrush(color.Value);
            brush.Freeze();
            return brush;
        }

        public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        {
            throw new NotSupportedException();
        }
    }
}