using Microsoft.Maui.Controls;

namespace Procure.Pages.Modals
{
    public partial class BatchCreateModal : ContentView
    {
        public BatchCreateModal()
        {
            InitializeComponent();
            // First inflation happens with IsVisible already true (LazyExpander builds on open);
            // reopens are covered by the IsVisible flip in OnPropertyChanged.
            Loaded += (_, _) => FocusFirstField();
        }

        protected override void OnPropertyChanged(string? propertyName = null)
        {
            base.OnPropertyChanged(propertyName);
            if (propertyName == nameof(IsVisible) && IsVisible) FocusFirstField();
        }

        private void FocusFirstField() => Dispatcher.Dispatch(() =>
        {
            if (IsVisible) DetailPrNoEntry.Focus();
        });

    }
}
