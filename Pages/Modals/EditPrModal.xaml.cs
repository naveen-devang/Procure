using System;
using System.Threading.Tasks;
using Microsoft.Maui.Controls;
using Procure.Models;
using Procure.PageModels;

namespace Procure.Pages.Modals
{
    public partial class EditPrModal : ContentView
    {
        private PrListPageModel? ViewModel => BindingContext as PrListPageModel;

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

        private static Entry? GetEntryFromSender(object sender)
        {
            if (sender is Entry e) return e;
#if WINDOWS
            if (sender is Microsoft.UI.Xaml.Controls.TextBox tb && tb.Tag is Entry te) return te;
#endif
            return null;
        }

        private void OnEditingItemNameEntryLoaded(object? sender, EventArgs e)
        {
#if WINDOWS
            if (sender is Entry entry && entry.Handler?.PlatformView is Microsoft.UI.Xaml.Controls.TextBox textBox)
            {
                textBox.Tag = entry;
                textBox.Paste -= OnEditingItemNameTextBoxPaste;
                textBox.Paste += OnEditingItemNameTextBoxPaste;
                textBox.PreviewKeyDown -= OnEditingItemNameTextBoxPreviewKeyDown;
                textBox.PreviewKeyDown += OnEditingItemNameTextBoxPreviewKeyDown;
            }
#endif
        }

        private void OnEditingItemQuantityEntryLoaded(object? sender, EventArgs e)
        {
#if WINDOWS
            if (sender is Entry entry && entry.Handler?.PlatformView is Microsoft.UI.Xaml.Controls.TextBox textBox)
            {
                textBox.Tag = entry;
                textBox.Paste -= OnEditingItemQuantityTextBoxPaste;
                textBox.Paste += OnEditingItemQuantityTextBoxPaste;
                textBox.PreviewKeyDown -= OnEditingItemQuantityTextBoxPreviewKeyDown;
                textBox.PreviewKeyDown += OnEditingItemQuantityTextBoxPreviewKeyDown;
            }
#endif
        }

        private void OnEditingItemUnitEntryLoaded(object? sender, EventArgs e)
        {
#if WINDOWS
            if (sender is Entry entry && entry.Handler?.PlatformView is Microsoft.UI.Xaml.Controls.TextBox textBox)
            {
                textBox.Tag = entry;
                textBox.Paste -= OnEditingItemUnitTextBoxPaste;
                textBox.Paste += OnEditingItemUnitTextBoxPaste;
                textBox.PreviewKeyDown -= OnEditingItemUnitTextBoxPreviewKeyDown;
                textBox.PreviewKeyDown += OnEditingItemUnitTextBoxPreviewKeyDown;
            }
#endif
        }

#if WINDOWS
        private void OnEditingItemNameTextBoxPaste(object sender, Microsoft.UI.Xaml.Controls.TextControlPasteEventArgs e)
        {
            var dataPackage = Windows.ApplicationModel.DataTransfer.Clipboard.GetContent();
            if (dataPackage != null && dataPackage.Contains(Windows.ApplicationModel.DataTransfer.StandardDataFormats.Text))
            {
                e.Handled = true;
                _ = ExecuteEditingItemPasteAsync(sender);
            }
        }

        private void OnEditingItemNameTextBoxPreviewKeyDown(object sender, Microsoft.UI.Xaml.Input.KeyRoutedEventArgs e)
        {
            if (e.Key == Windows.System.VirtualKey.V &&
                Microsoft.UI.Input.InputKeyboardSource.GetKeyStateForCurrentThread(Windows.System.VirtualKey.Control).HasFlag(Windows.UI.Core.CoreVirtualKeyStates.Down))
            {
                var dataPackage = Windows.ApplicationModel.DataTransfer.Clipboard.GetContent();
                if (dataPackage != null && dataPackage.Contains(Windows.ApplicationModel.DataTransfer.StandardDataFormats.Text))
                {
                    e.Handled = true;
                    _ = ExecuteEditingItemPasteAsync(sender);
                }
            }
        }

        private async Task ExecuteEditingItemPasteAsync(object sender)
        {
            var entry = GetEntryFromSender(sender);
            if (entry?.BindingContext is PrItem item && ViewModel != null)
            {
                try
                {
                    var text = await Microsoft.Maui.ApplicationModel.DataTransfer.Clipboard.Default.GetTextAsync();
                    if (!string.IsNullOrEmpty(text))
                    {
                        ViewModel.HandleInlineItemPaste(item, text, ViewModel.EditingPrItems);
                    }
                }
                catch { }
            }
        }

        private void OnEditingItemQuantityTextBoxPaste(object sender, Microsoft.UI.Xaml.Controls.TextControlPasteEventArgs e)
        {
            var dataPackage = Windows.ApplicationModel.DataTransfer.Clipboard.GetContent();
            if (dataPackage != null && dataPackage.Contains(Windows.ApplicationModel.DataTransfer.StandardDataFormats.Text))
            {
                e.Handled = true;
                _ = ExecuteEditingQuantityPasteAsync(sender);
            }
        }

        private void OnEditingItemQuantityTextBoxPreviewKeyDown(object sender, Microsoft.UI.Xaml.Input.KeyRoutedEventArgs e)
        {
            if (e.Key == Windows.System.VirtualKey.V &&
                Microsoft.UI.Input.InputKeyboardSource.GetKeyStateForCurrentThread(Windows.System.VirtualKey.Control).HasFlag(Windows.UI.Core.CoreVirtualKeyStates.Down))
            {
                var dataPackage = Windows.ApplicationModel.DataTransfer.Clipboard.GetContent();
                if (dataPackage != null && dataPackage.Contains(Windows.ApplicationModel.DataTransfer.StandardDataFormats.Text))
                {
                    e.Handled = true;
                    _ = ExecuteEditingQuantityPasteAsync(sender);
                }
            }
        }

        private async Task ExecuteEditingQuantityPasteAsync(object sender)
        {
            var entry = GetEntryFromSender(sender);
            if (entry?.BindingContext is PrItem item && ViewModel != null)
            {
                try
                {
                    var text = await Microsoft.Maui.ApplicationModel.DataTransfer.Clipboard.Default.GetTextAsync();
                    if (!string.IsNullOrEmpty(text))
                    {
                        ViewModel.HandleInlineQuantityPaste(item, text, ViewModel.EditingPrItems);
                    }
                }
                catch { }
            }
        }

        private void OnEditingItemUnitTextBoxPaste(object sender, Microsoft.UI.Xaml.Controls.TextControlPasteEventArgs e)
        {
            var dataPackage = Windows.ApplicationModel.DataTransfer.Clipboard.GetContent();
            if (dataPackage != null && dataPackage.Contains(Windows.ApplicationModel.DataTransfer.StandardDataFormats.Text))
            {
                e.Handled = true;
                _ = ExecuteEditingUnitPasteAsync(sender);
            }
        }

        private void OnEditingItemUnitTextBoxPreviewKeyDown(object sender, Microsoft.UI.Xaml.Input.KeyRoutedEventArgs e)
        {
            if (e.Key == Windows.System.VirtualKey.V &&
                Microsoft.UI.Input.InputKeyboardSource.GetKeyStateForCurrentThread(Windows.System.VirtualKey.Control).HasFlag(Windows.UI.Core.CoreVirtualKeyStates.Down))
            {
                var dataPackage = Windows.ApplicationModel.DataTransfer.Clipboard.GetContent();
                if (dataPackage != null && dataPackage.Contains(Windows.ApplicationModel.DataTransfer.StandardDataFormats.Text))
                {
                    e.Handled = true;
                    _ = ExecuteEditingUnitPasteAsync(sender);
                }
            }
        }

        private async Task ExecuteEditingUnitPasteAsync(object sender)
        {
            var entry = GetEntryFromSender(sender);
            if (entry?.BindingContext is PrItem item && ViewModel != null)
            {
                try
                {
                    var text = await Microsoft.Maui.ApplicationModel.DataTransfer.Clipboard.Default.GetTextAsync();
                    if (!string.IsNullOrEmpty(text))
                    {
                        ViewModel.HandleInlineUnitPaste(item, text, ViewModel.EditingPrItems);
                    }
                }
                catch { }
            }
        }
#endif

        private void OnEditingItemNameTextChanged(object? sender, TextChangedEventArgs e)
        {
            if (string.IsNullOrWhiteSpace(e.NewTextValue) || ViewModel == null) return;
            if (sender is not Entry { IsFocused: true } entry) return; // see IsFocused note below
            if (e.NewTextValue.Contains('\n') || e.NewTextValue.Contains('\r') || e.NewTextValue.Contains('\t'))
            {
                if (entry.BindingContext is PrItem item)
                {
                    ViewModel.HandleInlineItemPaste(item, e.NewTextValue, ViewModel.EditingPrItems);
                }
            }
        }

        private void OnEditingItemQuantityTextChanged(object? sender, TextChangedEventArgs e)
        {
            if (string.IsNullOrWhiteSpace(e.NewTextValue) || ViewModel == null) return;
            if (sender is not Entry { IsFocused: true } entry) return;
            if (e.NewTextValue.Contains('\n') || e.NewTextValue.Contains('\r') || e.NewTextValue.Contains('\t'))
            {
                if (entry.BindingContext is PrItem item)
                {
                    ViewModel.HandleInlineQuantityPaste(item, e.NewTextValue, ViewModel.EditingPrItems);
                }
            }
        }

        // IsFocused guards all three handlers above: TextChanged fires for a value set
        // programmatically by data binding just as it does for real typing (there is no built-in way
        // to tell them apart - see Microsoft's own Entry docs), so without this guard, an item whose
        // stored ItemName/Quantity/Unit already contained an embedded \n/\r/\t (as a "Consolidated
        // Master Requisition" merge apparently produced for PR-2026-008) triggered this "paste"
        // handling the instant BindableLayoutController.CreateChildren() bound that row's Entry -
        // mutating EditingPrItems while CreateChildren() was still enumerating it, which threw
        // "Collection was modified" from inside a native TextChanged callback and crashed the whole
        // process instead of surfacing as a catchable .NET exception. A real paste can only happen
        // while the Entry has focus; a binding-driven initial value is applied before anything does.
        private void OnEditingItemUnitTextChanged(object? sender, TextChangedEventArgs e)
        {
            if (string.IsNullOrWhiteSpace(e.NewTextValue) || ViewModel == null) return;
            if (sender is not Entry { IsFocused: true } entry) return;
            if (e.NewTextValue.Contains('\n') || e.NewTextValue.Contains('\r') || e.NewTextValue.Contains('\t'))
            {
                if (entry.BindingContext is PrItem item)
                {
                    ViewModel.HandleInlineUnitPaste(item, e.NewTextValue, ViewModel.EditingPrItems);
                }
            }
        }
    }
}
