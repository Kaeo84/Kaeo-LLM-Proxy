using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Windows.Media;
using Kaeo.LlmProxy.VSExtension.ToolWindow;

namespace Kaeo.LlmProxy.VSExtension.Settings
{
    /// <summary>
    /// A color choice in the Preferences color-picker ComboBoxes. Each preset is a
    /// theme-conditional light/dark pair (project theming rule): <see cref="Hex"/> and
    /// <see cref="Brush"/> resolve to the variant for the CURRENT VS theme and re-raise
    /// change notifications when the theme changes, so swatches and persisted values follow
    /// the scheme. "Default (theme)" (null) is the only unconditionally-safe choice; custom
    /// hex values are kept as-is and contrast-validated at the consumption site (ThemeColors).
    /// </summary>
    internal sealed class ColorOption : INotifyPropertyChanged
    {
        public string Name { get; init; } = string.Empty;

        /// <summary>Hex applied in light themes; null = follow the theme brush.</summary>
        public string? LightHex { get; init; }

        /// <summary>Hex applied in dark themes; null = follow the theme brush.</summary>
        public string? DarkHex { get; init; }

        /// <summary>Hex values persisted by older builds that map onto this preset.</summary>
        private string[] LegacyHexes { get; init; } = [];

        /// <summary>The hex for the current VS theme; null = follow the theme brush.</summary>
        public string? Hex => ThemeColors.IsDarkTheme ? DarkHex : LightHex;

        /// <summary>Frozen swatch brush for the current variant (transparent for the theme default).</summary>
        public Brush Brush
        {
            get
            {
                var c = ThemeColors.Parse(Hex);
                if (c is null)
                    return Brushes.Transparent;
                var b = new SolidColorBrush(c.Value);
                b.Freeze();
                return b;
            }
        }

        public event PropertyChangedEventHandler? PropertyChanged;

        static ColorOption()
        {
            // Repaint swatches (and re-resolve Hex for saves) whenever the VS scheme changes.
            ThemeColors.ThemeChanged += (_, _) =>
            {
                foreach (var p in Presets.Concat(TextPresets))
                {
                    p.PropertyChanged?.Invoke(p, new PropertyChangedEventArgs(nameof(Hex)));
                    p.PropertyChanged?.Invoke(p, new PropertyChangedEventArgs(nameof(Brush)));
                }
            };
        }

        private static ColorOption Pair(string name, string lightHex, string darkHex, params string[] legacyHexes)
            => new()
            { Name = name, LightHex = lightHex, DarkHex = darkHex, LegacyHexes = legacyHexes };

        /// <summary>True when the hex matches either theme variant or a legacy preset value.</summary>
        public bool Matches(string? hex)
        {
            if (string.IsNullOrWhiteSpace(hex))
                return false;
            hex = hex!.Trim();
            return Eq(LightHex, hex) || Eq(DarkHex, hex) || LegacyHexes.Any(l => Eq(l, hex));

            static bool Eq(string? a, string b) => string.Equals(a, b, StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>
        /// Message/reasoning background presets: subtle tints in light themes, deep shades in
        /// dark themes. Legacy fixed values (including pure White/Black) map onto the nearest
        /// theme-conditional pair instead of freezing the element to one scheme.
        /// </summary>
        public static readonly IReadOnlyList<ColorOption> Presets =
        [
            new ColorOption { Name = "Default (theme)" },
            Pair("Soft Gray", "#E0E0E0", "#3F3F46", "#FFFFFF", "#F0F0F0"),
            Pair("Strong Gray", "#404040", "#C8C8C8", "#000000"),
            Pair("Blue", "#D6E8FA", "#1A3A6E", "#2B6CB0"),
            Pair("Green", "#D8F0D8", "#1E5C1E", "#2F855A"),
            Pair("Red", "#F8D6D6", "#8E1E1E", "#C53030"),
            Pair("Purple", "#E8D6F8", "#5C2E8E", "#6B46C1"),
            Pair("Orange", "#FFE8D0", "#7A4A10", "#DD6B20"),
            Pair("Teal", "#C8E8E0", "#1E5C5C", "#2C7A7B"),
            Pair("Yellow", "#FFF8D6", "#6E5A1E", "#D69E2E"),
            Pair("Rose", "#F0D8E0", "#6E2E4A"),
        ];

        /// <summary>
        /// Text-color presets: dark-enough inks for light themes, light-enough inks for dark
        /// themes. Pure Black/White are gone; legacy values map onto the Contrast/Gray pairs.
        /// </summary>
        public static readonly IReadOnlyList<ColorOption> TextPresets =
        [
            new ColorOption { Name = "Default (theme)" },
            Pair("Contrast", "#1E1E1E", "#F0F0F0", "#000000", "#FFFFFF"),
            Pair("Gray", "#6E6E6E", "#A8A8A8", "#888888"),
            Pair("Blue", "#1A56DB", "#7EB3FF", "#1A3A6E"),
            Pair("Green", "#057A55", "#57D9A3", "#1E5C1E"),
            Pair("Red", "#C81E1E", "#FF8A8A", "#8E1E1E"),
            Pair("Purple", "#6C2BD9", "#C4A5FF", "#5C2E8E"),
            Pair("Orange", "#C27803", "#FFC24B"),
            Pair("Teal", "#047481", "#6FE3E1", "#1E5C5C"),
        ];

        /// <summary>Finds the preset that matches a hex value (either variant or legacy), or a custom entry.</summary>
        public static ColorOption FromHex(IReadOnlyList<ColorOption> presets, string? hex)
        {
            if (string.IsNullOrWhiteSpace(hex))
                return presets[0];
            hex = hex!.Trim();
            foreach (var p in presets)
            {
                if (p.Matches(hex))
                    return p;
            }
            // Custom entry for unrecognized hex values (e.g. imported settings). Kept fixed;
            // ThemeColors contrast-validates it wherever it is applied.
            if (ThemeColors.Parse(hex) is not null)
                return new ColorOption { Name = $"Custom ({hex})", LightHex = hex, DarkHex = hex };
            return presets[0];
        }

        /// <summary>
        /// Maps a persisted hex onto the current-theme variant of its preset within the given
        /// list (background vs text), so a value saved under one scheme adapts to the other.
        /// Unrecognized (custom) values pass through unchanged.
        /// </summary>
        public static string? MapToCurrentTheme(string? hex, IReadOnlyList<ColorOption> presets)
        {
            if (string.IsNullOrWhiteSpace(hex))
                return null;
            foreach (var p in presets)
            {
                if (p.Matches(hex))
                    return p.Hex;
            }
            return hex;
        }
    }
}