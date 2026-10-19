using System.Collections.Generic;
using System.Windows.Media;

namespace Kaeo.LlmProxy.VSExtension.Settings
{
    /// <summary>
    /// A color choice in the Preferences color-picker ComboBoxes. Exposes a Name, a Hex code,
    /// and a SolidColorBrush so the XAML ItemTemplate can render a swatch.
    /// </summary>
    internal sealed class ColorOption
    {
        public string Name { get; init; } = string.Empty;
        public string? Hex { get; init; }
        public Brush Brush { get; init; } = Brushes.Transparent;

        /// <summary>Common named colors that are legible across VS light/dark themes.</summary>
        public static readonly IReadOnlyList<ColorOption> Presets = new[]
        {
            new ColorOption { Name = "Default (theme)", Hex = null, Brush = Brushes.Transparent },
            new ColorOption { Name = "Light Gray", Hex = "#E0E0E0", Brush = new SolidColorBrush(Color.FromRgb(0xE0, 0xE0, 0xE0)) },
            new ColorOption { Name = "Dark Gray", Hex = "#404040", Brush = new SolidColorBrush(Color.FromRgb(0x40, 0x40, 0x40)) },
            new ColorOption { Name = "Soft Blue", Hex = "#D6E8FA", Brush = new SolidColorBrush(Color.FromRgb(0xD6, 0xE8, 0xFA)) },
            new ColorOption { Name = "Soft Green", Hex = "#D8F0D8", Brush = new SolidColorBrush(Color.FromRgb(0xD8, 0xF0, 0xD8)) },
            new ColorOption { Name = "Soft Yellow", Hex = "#FFF8D6", Brush = new SolidColorBrush(Color.FromRgb(0xFF, 0xF8, 0xD6)) },
            new ColorOption { Name = "Soft Red", Hex = "#F8D6D6", Brush = new SolidColorBrush(Color.FromRgb(0xF8, 0xD6, 0xD6)) },
            new ColorOption { Name = "Soft Purple", Hex = "#E8D6F8", Brush = new SolidColorBrush(Color.FromRgb(0xE8, 0xD6, 0xF8)) },
            new ColorOption { Name = "Soft Orange", Hex = "#FFE8D0", Brush = new SolidColorBrush(Color.FromRgb(0xFF, 0xE8, 0xD0)) },
            new ColorOption { Name = "Muted Teal", Hex = "#C8E8E0", Brush = new SolidColorBrush(Color.FromRgb(0xC8, 0xE8, 0xE0)) },
            new ColorOption { Name = "Muted Rose", Hex = "#F0D8E0", Brush = new SolidColorBrush(Color.FromRgb(0xF0, 0xD8, 0xE0)) },
        };

        public static readonly IReadOnlyList<ColorOption> TextPresets = new[]
        {
            new ColorOption { Name = "Default (theme)", Hex = null, Brush = Brushes.Transparent },
            new ColorOption { Name = "Black", Hex = "#000000", Brush = new SolidColorBrush(Colors.Black) },
            new ColorOption { Name = "White", Hex = "#FFFFFF", Brush = new SolidColorBrush(Colors.White) },
            new ColorOption { Name = "Dark Blue", Hex = "#1A3A6E", Brush = new SolidColorBrush(Color.FromRgb(0x1A, 0x3A, 0x6E)) },
            new ColorOption { Name = "Dark Green", Hex = "#1E5C1E", Brush = new SolidColorBrush(Color.FromRgb(0x1E, 0x5C, 0x1E)) },
            new ColorOption { Name = "Dark Red", Hex = "#8E1E1E", Brush = new SolidColorBrush(Color.FromRgb(0x8E, 0x1E, 0x1E)) },
            new ColorOption { Name = "Dark Purple", Hex = "#5C2E8E", Brush = new SolidColorBrush(Color.FromRgb(0x5C, 0x2E, 0x8E)) },
            new ColorOption { Name = "Dark Teal", Hex = "#1E5C5C", Brush = new SolidColorBrush(Color.FromRgb(0x1E, 0x5C, 0x5C)) },
            new ColorOption { Name = "Gray", Hex = "#888888", Brush = new SolidColorBrush(Color.FromRgb(0x88, 0x88, 0x88)) },
            new ColorOption { Name = "Soft White", Hex = "#F0F0F0", Brush = new SolidColorBrush(Color.FromRgb(0xF0, 0xF0, 0xF0)) },
        };

        /// <summary>Finds the preset that matches a hex value, or returns Default.</summary>
        public static ColorOption FromHex(IReadOnlyList<ColorOption> presets, string? hex)
        {
            if (string.IsNullOrWhiteSpace(hex))
                return presets[0];
            hex = hex!.Trim();
            foreach (var p in presets)
            {
                if (string.Equals(p.Hex, hex, System.StringComparison.OrdinalIgnoreCase))
                    return p;
            }
            // Return a custom entry for unrecognized hex values (e.g. imported settings).
            try
            {
                var c = (Color)ColorConverter.ConvertFromString(hex);
                return new ColorOption { Name = $"Custom ({hex})", Hex = hex, Brush = new SolidColorBrush(c) };
            }
            catch
            {
                return presets[0];
            }
        }
    }
}