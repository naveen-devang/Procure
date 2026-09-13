using System.Collections.Generic;

namespace Procure.Models
{
    /// <summary>The eight accent choices offered on the Settings page. Colours are #RRGGBB /
    /// #AARRGGBB hex; the host parses them into its own Color type (see PastelThemeOption).</summary>
    public static class AccentPalettes
    {
        public static readonly IReadOnlyList<PastelThemeOption> All = new List<PastelThemeOption>
        {
            new() { Id = "Blue",   Name = "Pastel Blue",    LightHex = "#3A82EE", DarkHex = "#60CDFF", BgHex = "#243A82EE" },
            new() { Id = "Purple", Name = "Pastel Purple",  LightHex = "#8B6CE8", DarkHex = "#B198F0", BgHex = "#248B6CE8" },
            new() { Id = "Mint",   Name = "Pastel Mint",    LightHex = "#2E9E6D", DarkHex = "#6CCB5F", BgHex = "#242E9E6D" },
            new() { Id = "Coral",  Name = "Pastel Coral",   LightHex = "#E07238", DarkHex = "#FFA043", BgHex = "#24E07238" },
            new() { Id = "Pink",   Name = "Pastel Rose",    LightHex = "#D95382", DarkHex = "#FF99A4", BgHex = "#24D95382" },
            new() { Id = "Red",    Name = "Pastel Crimson", LightHex = "#C83E4D", DarkHex = "#FF7B7B", BgHex = "#24C83E4D" },
            new() { Id = "Yellow", Name = "Pastel Amber",   LightHex = "#D48B17", DarkHex = "#FCE100", BgHex = "#24D48B17" },
            new() { Id = "Teal",   Name = "Pastel Teal",    LightHex = "#1E989B", DarkHex = "#48CAE4", BgHex = "#241E989B" },
        };
    }
}
