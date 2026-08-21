using Microsoft.Maui.Controls;

namespace Procure.Pages.Controls
{
    /// <summary>
    /// Static placeholder rows shown while the PR board loads. No bindings of its own -
    /// PrListPage toggles IsVisible and pulses Opacity on the instance.
    /// </summary>
    public partial class PrListSkeleton : ContentView
    {
        public PrListSkeleton()
        {
            InitializeComponent();
        }
    }
}
