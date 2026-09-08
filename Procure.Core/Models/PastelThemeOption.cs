namespace Procure.Models
{
    /// <summary>One accent-colour choice on the Settings page. Colours are #AARRGGBB / #RRGGBB
    /// hex strings so this stays UI-framework-free; the host parses them into its own Color type.</summary>
    public class PastelThemeOption
    {
        public string Id { get; set; } = string.Empty;
        public string Name { get; set; } = string.Empty;
        public string LightHex { get; set; } = "#00000000";
        public string DarkHex { get; set; } = "#00000000";
        public string BgHex { get; set; } = "#00000000";
    }
}
