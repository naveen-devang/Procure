using Procure.PageModels;

namespace Procure.Pages
{
    public partial class CallOffPage : ContentPage
    {
        private readonly CallOffPageModel _viewModel;
        private readonly Procure.Services.IKeyboardShortcutService _shortcuts;

        public CallOffPage(CallOffPageModel viewModel, Procure.Services.IKeyboardShortcutService shortcuts)
        {
            InitializeComponent();
            BindingContext = _viewModel = viewModel;
            _shortcuts = shortcuts;
        }

        protected override async void OnAppearing()
        {
            base.OnAppearing();
#if WINDOWS
            Procure.Utilities.NativeTheme.ForceRepaintOnAppear(this);
#endif
            _viewModel.IsVisible = true;
            await _viewModel.LoadAsync();
        }

        protected override void OnDisappearing()
        {
            base.OnDisappearing();
            _viewModel.IsVisible = false;
            // Drops every expanded group's rows; see CallOffPageModel.ReleaseLines.
            _viewModel.ReleaseLines();
        }

#if WINDOWS
        // Same page-level hook pattern as PrListPage.xaml.cs: subscribe/unsubscribe tied to the
        // handler lifetime, and claim programmatic focus on the page root so a shortcut works the
        // moment the tab opens, without needing a click first.
        protected override void OnHandlerChanged()
        {
            base.OnHandlerChanged();
            if (Handler?.PlatformView is Microsoft.UI.Xaml.FrameworkElement root)
            {
                root.PreviewKeyDown -= OnPagePreviewKeyDown;
                root.PreviewKeyDown += OnPagePreviewKeyDown;
                root.IsTabStop = true;
                root.Focus(Microsoft.UI.Xaml.FocusState.Programmatic);
            }
        }

        protected override void OnHandlerChanging(HandlerChangingEventArgs args)
        {
            base.OnHandlerChanging(args);
            if (args.NewHandler is null && args.OldHandler?.PlatformView is Microsoft.UI.Xaml.FrameworkElement root)
            {
                root.PreviewKeyDown -= OnPagePreviewKeyDown;
            }
        }

        private void OnPagePreviewKeyDown(object sender, Microsoft.UI.Xaml.Input.KeyRoutedEventArgs e)
        {
            if (e.Key == Windows.System.VirtualKey.Escape)
            {
                if (!string.IsNullOrEmpty(_viewModel.SearchText)) _viewModel.SearchText = string.Empty;
                else if (_viewModel.SelectedLine != null) _viewModel.SelectedLine.IsSelected = false;
                if (_viewModel.SelectedLine != null) _viewModel.SelectedLine = null;
                e.Handled = true;
                return;
            }

            // Reuses the PR Board's own shortcut IDs - same actions (focus the search box, force a
            // reload), just scoped to whichever page is actually on screen.
            if (Procure.Utilities.ShortcutInput.Matches(_shortcuts.GetCombo(Procure.Utilities.KeyboardShortcutIds.FocusSearch), e.Key))
            {
                SearchEntry.Focus();
                e.Handled = true;
            }
            else if (Procure.Utilities.ShortcutInput.Matches(_shortcuts.GetCombo(Procure.Utilities.KeyboardShortcutIds.RefreshBoard), e.Key))
            {
                _viewModel.RefreshCommand.Execute(null);
                e.Handled = true;
            }
        }
#endif
    }
}
