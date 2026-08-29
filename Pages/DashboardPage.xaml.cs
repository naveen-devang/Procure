using System;
using System.Threading.Tasks;
using Microsoft.Maui.Controls;
using Procure.PageModels;

namespace Procure.Pages
{
    public partial class DashboardPage : ContentPage
    {
        private readonly DashboardPageModel _viewModel;

        public DashboardPage(DashboardPageModel viewModel)
        {
            InitializeComponent();
            BindingContext = _viewModel = viewModel;
        }

        protected override async void OnAppearing()
        {
            // Reload every time: edits made on the PR Board change these metrics, and this page is a
            // singleton, so a first-run latch would leave them stale for the whole session.
            // LoadDataAsync self-guards on IsBusy; the seed check is a no-op after the first call.
            base.OnAppearing();
            await _viewModel.LoadDataAsync();
        }
    }
}
