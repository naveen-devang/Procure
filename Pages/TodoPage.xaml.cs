using Procure.PageModels;

namespace Procure.Pages
{
    public partial class TodoPage : ContentPage
    {
        private readonly TodoPageModel _viewModel;
        private readonly Procure.Services.IKeyboardShortcutService _shortcuts;

        public TodoPage(TodoPageModel viewModel, Procure.Services.IKeyboardShortcutService shortcuts)
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
        }

        // Focusing the quick-add field opens the priority / due / notes options.
        private void OnComposerFocused(object? sender, FocusEventArgs e) => _viewModel.ComposerOpen = true;

#if WINDOWS
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
            // Esc: clear the filter, else drop the selection.
            if (e.Key == Windows.System.VirtualKey.Escape)
            {
                if (!string.IsNullOrEmpty(_viewModel.FilterText)) _viewModel.FilterText = string.Empty;
                else _viewModel.SelectCommand.Execute(null);
                e.Handled = true;
                return;
            }

            // Delete: remove the selected task - but not while a text field is being edited.
            if (e.Key == Windows.System.VirtualKey.Delete && _viewModel.SelectedTask != null && !IsTextInputFocused(sender))
            {
                _viewModel.DeleteCommand.Execute(_viewModel.SelectedTask);
                e.Handled = true;
                return;
            }

            if (Procure.Utilities.ShortcutInput.Matches(_shortcuts.GetCombo(Procure.Utilities.KeyboardShortcutIds.TasksNew), e.Key))
            {
                _viewModel.NewTaskCommand.Execute(null);
                e.Handled = true;
            }
            else if (Procure.Utilities.ShortcutInput.Matches(_shortcuts.GetCombo(Procure.Utilities.KeyboardShortcutIds.TasksRefresh), e.Key))
            {
                _viewModel.RefreshCommand.Execute(null);
                e.Handled = true;
            }
        }

        private static bool IsTextInputFocused(object sender)
        {
            var xamlRoot = (sender as Microsoft.UI.Xaml.FrameworkElement)?.XamlRoot;
            var focused = xamlRoot != null
                ? Microsoft.UI.Xaml.Input.FocusManager.GetFocusedElement(xamlRoot)
                : Microsoft.UI.Xaml.Input.FocusManager.GetFocusedElement();
            return focused is Microsoft.UI.Xaml.Controls.TextBox or Microsoft.UI.Xaml.Controls.RichEditBox;
        }
#endif
    }
}
