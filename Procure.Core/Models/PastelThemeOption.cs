using System.ComponentModel;

namespace Procure.Models
{
    /// <summary>One accent-colour choice on the Settings page. Colours are #AARRGGBB / #RRGGBB
    /// hex strings so this stays UI-framework-free; the host parses them into its own Color type.</summary>
    public class PastelThemeOption : INotifyPropertyChanged
    {
        public string Id { get; set; } = string.Empty;
        public string Name { get; set; } = string.Empty;
        public string LightHex { get; set; } = "#00000000";
        public string DarkHex { get; set; } = "#00000000";
        public string BgHex { get; set; } = "#00000000";

        private bool _isSelected;
        /// <summary>Drives the swatch fill directly. A binding survives a light/dark switch;
        /// a list control's selection visual state does not - it is re-applied only on the next
        /// interaction, which left the palette looking like nothing was chosen.</summary>
        public bool IsSelected
        {
            get => _isSelected;
            set
            {
                if (_isSelected == value) return;
                _isSelected = value;
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsSelected)));
            }
        }

        public event PropertyChangedEventHandler? PropertyChanged;
    }
}
