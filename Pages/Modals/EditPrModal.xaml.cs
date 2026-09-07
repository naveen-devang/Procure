using Microsoft.Maui.Controls;

namespace Procure.Pages.Modals
{
    public partial class EditPrModal : ContentView
    {
        public EditPrModal()
        {
            InitializeComponent();
            // First inflation happens with IsVisible already true (LazyExpander builds on open), so
            // the visibility change alone cannot cover the first open.
            Loaded += (_, _) => FocusFirstField();
        }

        // Reopens: the LazyExpander keeps the built modal, and its root IsVisible flips with the flag.
        protected override void OnPropertyChanged(string? propertyName = null)
        {
            base.OnPropertyChanged(propertyName);
            if (propertyName == nameof(IsVisible) && IsVisible) FocusFirstField();
        }

        private void FocusFirstField() => Dispatcher.Dispatch(() =>
        {
            if (IsVisible) PrNoEntry.Focus();
        });

    }
}
