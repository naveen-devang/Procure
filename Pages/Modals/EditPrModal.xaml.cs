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
        }

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
            if (e.NewTextValue.Contains('\n') || e.NewTextValue.Contains('\r') || e.NewTextValue.Contains('\t'))
            {
                if (sender is Entry entry && entry.BindingContext is PrItem item)
                {
                    ViewModel.HandleInlineItemPaste(item, e.NewTextValue, ViewModel.EditingPrItems);
                }
            }
        }

        private void OnEditingItemQuantityTextChanged(object? sender, TextChangedEventArgs e)
        {
            if (string.IsNullOrWhiteSpace(e.NewTextValue) || ViewModel == null) return;
            if (e.NewTextValue.Contains('\n') || e.NewTextValue.Contains('\r') || e.NewTextValue.Contains('\t'))
            {
                if (sender is Entry entry && entry.BindingContext is PrItem item)
                {
                    ViewModel.HandleInlineQuantityPaste(item, e.NewTextValue, ViewModel.EditingPrItems);
                }
            }
        }

        private void OnEditingItemUnitTextChanged(object? sender, TextChangedEventArgs e)
        {
            if (string.IsNullOrWhiteSpace(e.NewTextValue) || ViewModel == null) return;
            if (e.NewTextValue.Contains('\n') || e.NewTextValue.Contains('\r') || e.NewTextValue.Contains('\t'))
            {
                if (sender is Entry entry && entry.BindingContext is PrItem item)
                {
                    ViewModel.HandleInlineUnitPaste(item, e.NewTextValue, ViewModel.EditingPrItems);
                }
            }
        }
    }
}
