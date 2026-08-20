using System;
using System.Threading.Tasks;
using Microsoft.Maui.Controls;
using Microsoft.Maui.Graphics;
using Procure.PageModels;

namespace Procure.Pages
{
    public partial class ManageColumnsPage : ContentPage, IThemeTransitionable
    {
        private readonly ManageColumnsPageModel _viewModel;

        public ManageColumnsPage(ManageColumnsPageModel viewModel)
        {
            InitializeComponent();
            BindingContext = _viewModel = viewModel;
        }

        protected override async void OnAppearing()
        {
            base.OnAppearing();
            await _viewModel.LoadColumnsAsync();
        }

        public async Task AnimateThemeTransitionAsync(Action applyTheme, bool isGoingToDark)
        {
            if (ThemeCurtain == null)
            {
                applyTheme();
                return;
            }

            ThemeCurtain.Color = isGoingToDark ? Color.FromArgb("#202020") : Color.FromArgb("#F3F3F3");
            ThemeCurtain.IsVisible = true;
            ThemeCurtain.Opacity = 0;

            await ThemeCurtain.FadeToAsync(1.0, 160, Easing.CubicOut);

            applyTheme();
            await Task.Delay(30);

            await ThemeCurtain.FadeToAsync(0, 200, Easing.CubicIn);
            ThemeCurtain.IsVisible = false;
        }
    }
}
