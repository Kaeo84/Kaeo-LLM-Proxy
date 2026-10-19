using System;
using System.Globalization;
using System.Windows.Data;

namespace Kaeo.LlmProxy.VSExtension.ToolWindow
{
    /// <summary>
    /// Inverts a bool. Used when a control should be enabled when IsBusy is false.
    /// </summary>
    internal sealed class InvertBoolConverter : IValueConverter
    {
        public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        {
            if (value is bool b)
                return !b;
            return true;
        }

        public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        {
            throw new NotSupportedException();
        }
    }
}