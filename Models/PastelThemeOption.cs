using Microsoft.Maui.Graphics;

namespace Procure.Models
{
    public class PastelThemeOption
    {
        public string Id { get; set; } = string.Empty;
        public string Name { get; set; } = string.Empty;
        public Color LightColor { get; set; } = Colors.Transparent;
        public Color DarkColor { get; set; } = Colors.Transparent;
        public Color BgColor { get; set; } = Colors.Transparent;
    }
}
