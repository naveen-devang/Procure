using System;
using System.Threading.Tasks;
using Microsoft.Maui.Controls;
using Procure.PageModels;

namespace Procure.Pages
{
    public partial class DashboardPage : ContentPage, IThemeTransitionable
    {
        private readonly DashboardPageModel _viewModel;

        public DashboardPage(DashboardPageModel viewModel)
        {
            InitializeComponent();
            BindingContext = _viewModel = viewModel;
        }

        protected override async void OnAppearing()
        {
            base.OnAppearing();
            await _viewModel.LoadDataAsync();
        }

        public async Task AnimateThemeTransitionAsync(Action applyTheme, bool isGoingToDark)
        {
            try
            {
                ThemeCurtain.Color = isGoingToDark ? Color.FromArgb("#202020") : Color.FromArgb("#F3F3F3");
                ThemeCurtain.Opacity = 0;
                ThemeCurtain.IsVisible = true;

                // Smooth GPU fade-in (160ms)
                await ThemeCurtain.FadeToAsync(1.0, 160, Easing.SinInOut);

                // Apply theme and metrics reload behind solid curtain
                applyTheme();
                await Task.Delay(40);

                // Smooth GPU reveal (200ms)
                await ThemeCurtain.FadeToAsync(0.0, 200, Easing.SinInOut);
            }
            catch
            {
                applyTheme();
            }
            finally
            {
                ThemeCurtain.IsVisible = false;
                ThemeCurtain.Opacity = 0;
            }
        }
    }
}
