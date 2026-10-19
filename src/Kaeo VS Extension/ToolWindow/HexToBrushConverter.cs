using System;
using System.Globalization;
using System.Windows.Data;
using System.Windows.Media;

namespace Kaeo.LlmProxy.VSExtension.ToolWindow
{
    /// <summary>
    /// Converts a hex color string (#RRGGBB or #AARRGGBB) to a SolidColorBrush.
    /// Null/empty returns null (transparent). On invalid input returns Transparent.
    /// </summary>
    internal sealed class HexToBrushConverter : IValueConverter
    {
        public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        {
            if (value is not string hex || string.IsNullOrWhiteSpace(hex))
                return null;

            try
            {
                var color = (Color)ColorConverter.ConvertFromString(hex.Trim());
                return new SolidColorBrush(color);
            }
            catch
            {
                return null;
            }
        }

        public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        {
            throw new NotSupportedException();
        }
    }
}