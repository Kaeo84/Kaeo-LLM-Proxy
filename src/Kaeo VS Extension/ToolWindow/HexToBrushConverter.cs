using System;
using System.Globalization;
using System.Windows.Data;
using System.Windows.Media;

namespace Kaeo.LlmProxy.VSExtension.ToolWindow
{
    /// <summary>
    /// Converts a hex color string (#RRGGBB or #AARRGGBB) to a SolidColorBrush.
    /// Null/empty returns Binding.DoNothing so WPF uses the inherited or default
    /// value instead of setting the property to null (which renders invisible text
    /// when used as Foreground). On invalid input also returns Binding.DoNothing.
    /// </summary>
    internal sealed class HexToBrushConverter : IValueConverter
    {
        public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        {
            if (value is not string hex || string.IsNullOrWhiteSpace(hex))
                return Binding.DoNothing;

            try
            {
                string s = hex.Trim();
                Color color;
                if (s.StartsWith("#", StringComparison.Ordinal))
                {
                    // Parse the digits with an explicit invariant culture: ColorConverter's
                    // string path is culture-sensitive and hex colors must not depend on the
                    // thread culture (e.g. digit grouping or number formats).
                    string digits = s.Substring(1);
                    if (digits.Length != 6 && digits.Length != 8)
                            return Binding.DoNothing;

                    byte Component(int offset) => byte.Parse(
                        digits.Substring(offset, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture);

                    color = digits.Length == 6
                        ? Color.FromRgb(Component(0), Component(2), Component(4))
                        : Color.FromArgb(Component(0), Component(2), Component(4), Component(6));
                }
                else
                {
                    // Named colors ("Red", "Transparent", ...) are ASCII identifiers; the
                    // converter resolves them case-insensitively and culture-invariantly.
                    color = (Color)ColorConverter.ConvertFromString(s);
                }

                return new SolidColorBrush(color);
            }
            catch
            {
                return Binding.DoNothing;
            }
        }

        public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        {
            throw new NotSupportedException();
        }
    }
}