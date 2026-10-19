using System;
using System.Globalization;
using System.Windows;
using System.Windows.Data;

namespace Kaeo.LlmProxy.VSExtension.ToolWindow
{
    /// <summary>
    /// Converts "Left" or "Right" strings to HorizontalAlignment enum values.
    /// </summary>
    internal sealed class StringToHorizontalAlignmentConverter : IValueConverter
    {
        public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        {
            if (value is string s && s == "Right")
                return HorizontalAlignment.Right;
            return HorizontalAlignment.Left;
        }

        public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        {
            throw new NotSupportedException();
        }
    }
}