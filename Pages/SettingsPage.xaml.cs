using Microsoft.Maui.Controls;
using Procure.PageModels;

namespace Procure.Pages
{
    public partial class SettingsPage : ContentPage
    {
        private readonly SettingsPageModel _viewModel;

        public SettingsPage(SettingsPageModel viewModel)
        {
            InitializeComponent();
            BindingContext = _viewModel = viewModel;
        }

        protected override void OnAppearing()
        {
            base.OnAppearing();
#if WINDOWS
            Procure.Utilities.NativeTheme.ForceRepaintOnAppear(this);
#endif
        }
    }
}
