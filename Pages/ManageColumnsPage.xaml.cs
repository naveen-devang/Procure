using System;
using System.Threading.Tasks;
using Microsoft.Maui.Controls;
using Microsoft.Maui.Graphics;
using Procure.PageModels;

namespace Procure.Pages
{
    public partial class ManageColumnsPage : ContentPage
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
    }
}
