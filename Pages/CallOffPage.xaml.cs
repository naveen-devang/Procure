using Procure.PageModels;

namespace Procure.Pages
{
    public partial class CallOffPage : ContentPage
    {
        private readonly CallOffPageModel _viewModel;

        public CallOffPage(CallOffPageModel viewModel)
        {
            InitializeComponent();
            BindingContext = _viewModel = viewModel;
        }

        protected override async void OnAppearing()
        {
            base.OnAppearing();
            _viewModel.IsVisible = true;
            await _viewModel.LoadAsync();
        }

        protected override void OnDisappearing()
        {
            base.OnDisappearing();
            _viewModel.IsVisible = false;
        }
    }
}
