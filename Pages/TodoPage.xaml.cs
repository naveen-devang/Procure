using System;
using System.Globalization;
using Microsoft.Maui.Controls.Shapes;
using Microsoft.Maui.Graphics;
using Procure.Models;
using Procure.PageModels;
using Procure.Utilities;

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
            _viewModel.WeekRebuilt += () => MainThread.BeginInvokeOnMainThread(RebuildWeekGrid);
        }

        // The Week (7-col) grid is built in pure C#: going through a DataTemplate + CreateContent()
        // and setting BindingContext by hand does NOT wire up the template's compiled bindings, so
        // cells came out empty. Colours use SetAppThemeColor so they never depend on ThemeHelper
        // being ready when the grid is built.
        private static void AppColor(VisualElement e, BindableProperty p, string light, string dark) =>
            e.SetAppThemeColor(p, Color.FromArgb(light), Color.FromArgb(dark));

        private static void AppBrush(VisualElement e, BindableProperty p, string light, string dark) =>
            e.SetAppTheme(p, new SolidColorBrush(Color.FromArgb(light)), new SolidColorBrush(Color.FromArgb(dark)));

        private void RebuildWeekGrid()
        {
            if (WeekGrid is null) return;
            WeekGrid.Children.Clear();
            var chip = (DataTemplate)Resources["WeekTaskChipTemplate"];

            foreach (var col in _viewModel.WeekColumns)
            {
                var header = new VerticalStackLayout { Spacing = 0, HorizontalOptions = LayoutOptions.Center };
                header.Add(new Label
                {
                    Text = col.DayName, FontSize = 9, FontFamily = "SegoeSemibold",
                    HorizontalOptions = LayoutOptions.Center, CharacterSpacing = 0.4,
                    TextColor = Color.FromArgb("#8A8A8A"),
                });
                var dayNum = new Label
                {
                    Text = col.Day.ToString(), FontSize = 15, FontFamily = "SegoeSemibold",
                    HorizontalOptions = LayoutOptions.Center,
                };
                if (col.IsToday) AppColor(dayNum, Label.TextColorProperty, "#4F6D8F", "#8FB3D6");
                else AppColor(dayNum, Label.TextColorProperty, "#1A1A1A", "#E7E9EE");
                header.Add(dayNum);

                var list = new VerticalStackLayout { Spacing = 0 };
                BindableLayout.SetItemTemplate(list, chip);
                BindableLayout.SetItemsSource(list, col.Tasks);

                var entry = new Entry { Placeholder = "+ add", FontSize = 10.5, HeightRequest = 30, BindingContext = col };
                entry.SetBinding(Entry.TextProperty, new Binding(nameof(WeekDayColumn.NewTaskTitle), BindingMode.TwoWay));
                entry.Completed += OnWeekColumnAdd;

                var grid = new Grid { RowSpacing = 4 };
                grid.RowDefinitions.Add(new RowDefinition(GridLength.Auto));
                grid.RowDefinitions.Add(new RowDefinition(GridLength.Star));
                grid.RowDefinitions.Add(new RowDefinition(GridLength.Auto));
                grid.Add(header, 0, 0);
                grid.Add(new ScrollView { Content = list }, 0, 1);
                grid.Add(entry, 0, 2);

                var border = new Border
                {
                    Padding = 6,
                    StrokeThickness = 1,
                    StrokeShape = new RoundRectangle { CornerRadius = 8 },
                    Content = grid,
                };
                AppBrush(border, Border.StrokeProperty, "#E1DFDD", "#3B3A39");
                AppColor(border, Border.BackgroundColorProperty, "#FFFFFF", "#1E1E20");
                WeekGrid.Add(border, col.ColIndex, 0);
            }
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

        private async void OnWeekColumnAdd(object? sender, EventArgs e)
        {
            if (sender is Entry entry && entry.BindingContext is WeekDayColumn col)
                await _viewModel.AddWeekColumnTaskAsync(col);
        }

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
