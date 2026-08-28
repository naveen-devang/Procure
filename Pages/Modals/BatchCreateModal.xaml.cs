using System;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.Maui.Controls;
using Procure.Models;
using Procure.PageModels;

namespace Procure.Pages.Modals
{
    public partial class BatchCreateModal : ContentView
    {
        private PrListPageModel? ViewModel => BindingContext as PrListPageModel;

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

        private static Entry? GetEntryFromSender(object sender)
        {
            if (sender is Entry e) return e;
#if WINDOWS
            if (sender is Microsoft.UI.Xaml.Controls.TextBox tb && tb.Tag is Entry te) return te;
#endif
            return null;
        }

        private void OnBatchPrNoEntryLoaded(object? sender, EventArgs e)
        {
#if WINDOWS
            if (sender is Entry entry && entry.Handler?.PlatformView is Microsoft.UI.Xaml.Controls.TextBox textBox)
            {
                textBox.Tag = entry;
                textBox.Paste -= OnBatchPrNoTextBoxPaste;
                textBox.Paste += OnBatchPrNoTextBoxPaste;
                textBox.PreviewKeyDown -= OnBatchPrNoTextBoxPreviewKeyDown;
                textBox.PreviewKeyDown += OnBatchPrNoTextBoxPreviewKeyDown;
            }
#endif
        }

        private void OnBatchDescriptionEntryLoaded(object? sender, EventArgs e)
        {
#if WINDOWS
            if (sender is Entry entry && entry.Handler?.PlatformView is Microsoft.UI.Xaml.Controls.TextBox textBox)
            {
                textBox.Tag = entry;
                textBox.Paste -= OnBatchDescriptionTextBoxPaste;
                textBox.Paste += OnBatchDescriptionTextBoxPaste;
                textBox.PreviewKeyDown -= OnBatchDescriptionTextBoxPreviewKeyDown;
                textBox.PreviewKeyDown += OnBatchDescriptionTextBoxPreviewKeyDown;
            }
#endif
        }

        private void OnBatchItemNameEntryLoaded(object? sender, EventArgs e)
        {
#if WINDOWS
            if (sender is Entry entry && entry.Handler?.PlatformView is Microsoft.UI.Xaml.Controls.TextBox textBox)
            {
                textBox.Tag = entry;
                textBox.Paste -= OnBatchItemNameTextBoxPaste;
                textBox.Paste += OnBatchItemNameTextBoxPaste;
                textBox.PreviewKeyDown -= OnBatchItemNameTextBoxPreviewKeyDown;
                textBox.PreviewKeyDown += OnBatchItemNameTextBoxPreviewKeyDown;
            }
#endif
        }

        private void OnBatchItemQuantityEntryLoaded(object? sender, EventArgs e)
        {
#if WINDOWS
            if (sender is Entry entry && entry.Handler?.PlatformView is Microsoft.UI.Xaml.Controls.TextBox textBox)
            {
                textBox.Tag = entry;
                textBox.Paste -= OnBatchItemQuantityTextBoxPaste;
                textBox.Paste += OnBatchItemQuantityTextBoxPaste;
                textBox.PreviewKeyDown -= OnBatchItemQuantityTextBoxPreviewKeyDown;
                textBox.PreviewKeyDown += OnBatchItemQuantityTextBoxPreviewKeyDown;
            }
#endif
        }

        private void OnBatchItemUnitEntryLoaded(object? sender, EventArgs e)
        {
#if WINDOWS
            if (sender is Entry entry && entry.Handler?.PlatformView is Microsoft.UI.Xaml.Controls.TextBox textBox)
            {
                textBox.Tag = entry;
                textBox.Paste -= OnBatchItemUnitTextBoxPaste;
                textBox.Paste += OnBatchItemUnitTextBoxPaste;
                textBox.PreviewKeyDown -= OnBatchItemUnitTextBoxPreviewKeyDown;
                textBox.PreviewKeyDown += OnBatchItemUnitTextBoxPreviewKeyDown;
            }
#endif
        }

#if WINDOWS
        private void OnBatchPrNoTextBoxPaste(object sender, Microsoft.UI.Xaml.Controls.TextControlPasteEventArgs e)
        {
            var dataPackage = Windows.ApplicationModel.DataTransfer.Clipboard.GetContent();
            if (dataPackage != null && dataPackage.Contains(Windows.ApplicationModel.DataTransfer.StandardDataFormats.Text))
            {
                e.Handled = true;
                _ = ExecuteBatchPrNoPasteAsync(sender);
            }
        }

        private void OnBatchPrNoTextBoxPreviewKeyDown(object sender, Microsoft.UI.Xaml.Input.KeyRoutedEventArgs e)
        {
            if (e.Key == Windows.System.VirtualKey.V &&
                Microsoft.UI.Input.InputKeyboardSource.GetKeyStateForCurrentThread(Windows.System.VirtualKey.Control).HasFlag(Windows.UI.Core.CoreVirtualKeyStates.Down))
            {
                var dataPackage = Windows.ApplicationModel.DataTransfer.Clipboard.GetContent();
                if (dataPackage != null && dataPackage.Contains(Windows.ApplicationModel.DataTransfer.StandardDataFormats.Text))
                {
                    e.Handled = true;
                    _ = ExecuteBatchPrNoPasteAsync(sender);
                }
            }
        }

        private async Task ExecuteBatchPrNoPasteAsync(object sender)
        {
            var entry = GetEntryFromSender(sender);
            if (entry?.BindingContext is BatchPrEntry batchEntry && ViewModel != null)
            {
                try
                {
                    var text = await Microsoft.Maui.ApplicationModel.DataTransfer.Clipboard.Default.GetTextAsync();
                    if (!string.IsNullOrEmpty(text))
                    {
                        ViewModel.HandleBatchPrRowPaste(batchEntry, text, isPrNoColumn: true);
                    }
                }
                catch { }
            }
        }

        private void OnBatchDescriptionTextBoxPaste(object sender, Microsoft.UI.Xaml.Controls.TextControlPasteEventArgs e)
        {
            var dataPackage = Windows.ApplicationModel.DataTransfer.Clipboard.GetContent();
            if (dataPackage != null && dataPackage.Contains(Windows.ApplicationModel.DataTransfer.StandardDataFormats.Text))
            {
                e.Handled = true;
                _ = ExecuteBatchDescriptionPasteAsync(sender);
            }
        }

        private void OnBatchDescriptionTextBoxPreviewKeyDown(object sender, Microsoft.UI.Xaml.Input.KeyRoutedEventArgs e)
        {
            if (e.Key == Windows.System.VirtualKey.V &&
                Microsoft.UI.Input.InputKeyboardSource.GetKeyStateForCurrentThread(Windows.System.VirtualKey.Control).HasFlag(Windows.UI.Core.CoreVirtualKeyStates.Down))
            {
                var dataPackage = Windows.ApplicationModel.DataTransfer.Clipboard.GetContent();
                if (dataPackage != null && dataPackage.Contains(Windows.ApplicationModel.DataTransfer.StandardDataFormats.Text))
                {
                    e.Handled = true;
                    _ = ExecuteBatchDescriptionPasteAsync(sender);
                }
            }
        }

        private async Task ExecuteBatchDescriptionPasteAsync(object sender)
        {
            var entry = GetEntryFromSender(sender);
            if (entry?.BindingContext is BatchPrEntry batchEntry && ViewModel != null)
            {
                try
                {
                    var text = await Microsoft.Maui.ApplicationModel.DataTransfer.Clipboard.Default.GetTextAsync();
                    if (!string.IsNullOrEmpty(text))
                    {
                        ViewModel.HandleBatchPrRowPaste(batchEntry, text, isPrNoColumn: false);
                    }
                }
                catch { }
            }
        }

        private void OnBatchItemNameTextBoxPaste(object sender, Microsoft.UI.Xaml.Controls.TextControlPasteEventArgs e)
        {
            var dataPackage = Windows.ApplicationModel.DataTransfer.Clipboard.GetContent();
            if (dataPackage != null && dataPackage.Contains(Windows.ApplicationModel.DataTransfer.StandardDataFormats.Text))
            {
                e.Handled = true;
                _ = ExecuteBatchItemPasteAsync(sender);
            }
        }

        private void OnBatchItemNameTextBoxPreviewKeyDown(object sender, Microsoft.UI.Xaml.Input.KeyRoutedEventArgs e)
        {
            if (e.Key == Windows.System.VirtualKey.V &&
                Microsoft.UI.Input.InputKeyboardSource.GetKeyStateForCurrentThread(Windows.System.VirtualKey.Control).HasFlag(Windows.UI.Core.CoreVirtualKeyStates.Down))
            {
                var dataPackage = Windows.ApplicationModel.DataTransfer.Clipboard.GetContent();
                if (dataPackage != null && dataPackage.Contains(Windows.ApplicationModel.DataTransfer.StandardDataFormats.Text))
                {
                    e.Handled = true;
                    _ = ExecuteBatchItemPasteAsync(sender);
                }
            }
        }

        private async Task ExecuteBatchItemPasteAsync(object sender)
        {
            var entry = GetEntryFromSender(sender);
            if (entry?.BindingContext is PrItem item && ViewModel != null)
            {
                try
                {
                    var text = await Microsoft.Maui.ApplicationModel.DataTransfer.Clipboard.Default.GetTextAsync();
                    if (!string.IsNullOrEmpty(text))
                    {
                        var parentBatchPr = ViewModel.BatchPrEntries.FirstOrDefault(b => b.Items.Contains(item));
                        if (parentBatchPr != null)
                        {
                            ViewModel.HandleInlineItemPaste(item, text, parentBatchPr.Items);
                            parentBatchPr.NotifyItemsChanged();
                        }
                    }
                }
                catch { }
            }
        }

        private void OnBatchItemQuantityTextBoxPaste(object sender, Microsoft.UI.Xaml.Controls.TextControlPasteEventArgs e)
        {
            var dataPackage = Windows.ApplicationModel.DataTransfer.Clipboard.GetContent();
            if (dataPackage != null && dataPackage.Contains(Windows.ApplicationModel.DataTransfer.StandardDataFormats.Text))
            {
                e.Handled = true;
                _ = ExecuteBatchQuantityPasteAsync(sender);
            }
        }

        private void OnBatchItemQuantityTextBoxPreviewKeyDown(object sender, Microsoft.UI.Xaml.Input.KeyRoutedEventArgs e)
        {
            if (e.Key == Windows.System.VirtualKey.V &&
                Microsoft.UI.Input.InputKeyboardSource.GetKeyStateForCurrentThread(Windows.System.VirtualKey.Control).HasFlag(Windows.UI.Core.CoreVirtualKeyStates.Down))
            {
                var dataPackage = Windows.ApplicationModel.DataTransfer.Clipboard.GetContent();
                if (dataPackage != null && dataPackage.Contains(Windows.ApplicationModel.DataTransfer.StandardDataFormats.Text))
                {
                    e.Handled = true;
                    _ = ExecuteBatchQuantityPasteAsync(sender);
                }
            }
        }

        private async Task ExecuteBatchQuantityPasteAsync(object sender)
        {
            var entry = GetEntryFromSender(sender);
            if (entry?.BindingContext is PrItem item && ViewModel != null)
            {
                try
                {
                    var text = await Microsoft.Maui.ApplicationModel.DataTransfer.Clipboard.Default.GetTextAsync();
                    if (!string.IsNullOrEmpty(text))
                    {
                        var parentBatchPr = ViewModel.BatchPrEntries.FirstOrDefault(b => b.Items.Contains(item));
                        if (parentBatchPr != null)
                        {
                            ViewModel.HandleInlineQuantityPaste(item, text, parentBatchPr.Items);
                            parentBatchPr.NotifyItemsChanged();
                        }
                    }
                }
                catch { }
            }
        }

        private void OnBatchItemUnitTextBoxPaste(object sender, Microsoft.UI.Xaml.Controls.TextControlPasteEventArgs e)
        {
            var dataPackage = Windows.ApplicationModel.DataTransfer.Clipboard.GetContent();
            if (dataPackage != null && dataPackage.Contains(Windows.ApplicationModel.DataTransfer.StandardDataFormats.Text))
            {
                e.Handled = true;
                _ = ExecuteBatchUnitPasteAsync(sender);
            }
        }

        private void OnBatchItemUnitTextBoxPreviewKeyDown(object sender, Microsoft.UI.Xaml.Input.KeyRoutedEventArgs e)
        {
            if (e.Key == Windows.System.VirtualKey.V &&
                Microsoft.UI.Input.InputKeyboardSource.GetKeyStateForCurrentThread(Windows.System.VirtualKey.Control).HasFlag(Windows.UI.Core.CoreVirtualKeyStates.Down))
            {
                var dataPackage = Windows.ApplicationModel.DataTransfer.Clipboard.GetContent();
                if (dataPackage != null && dataPackage.Contains(Windows.ApplicationModel.DataTransfer.StandardDataFormats.Text))
                {
                    e.Handled = true;
                    _ = ExecuteBatchUnitPasteAsync(sender);
                }
            }
        }

        private async Task ExecuteBatchUnitPasteAsync(object sender)
        {
            var entry = GetEntryFromSender(sender);
            if (entry?.BindingContext is PrItem item && ViewModel != null)
            {
                try
                {
                    var text = await Microsoft.Maui.ApplicationModel.DataTransfer.Clipboard.Default.GetTextAsync();
                    if (!string.IsNullOrEmpty(text))
                    {
                        var parentBatchPr = ViewModel.BatchPrEntries.FirstOrDefault(b => b.Items.Contains(item));
                        if (parentBatchPr != null)
                        {
                            ViewModel.HandleInlineUnitPaste(item, text, parentBatchPr.Items);
                            parentBatchPr.NotifyItemsChanged();
                        }
                    }
                }
                catch { }
            }
        }
#endif

        // IsFocused guards all five handlers below: TextChanged fires for a value set
        // programmatically by data binding just as it does for real typing (there's no built-in way
        // to tell them apart), so without this guard, an item whose stored text already contained an
        // embedded \n/\r/\t would trigger this "paste" handling the instant BindableLayoutController
        // bound that row's Entry - mutating the same collection it was still enumerating. Same crash
        // class already found and fixed in EditPrModal.xaml.cs. A real paste can only happen while
        // the Entry has focus; a binding-driven initial value is applied before anything does.
        private void OnBatchItemNameTextChanged(object? sender, TextChangedEventArgs e)
        {
            if (string.IsNullOrWhiteSpace(e.NewTextValue) || ViewModel == null) return;
            if (sender is not Entry { IsFocused: true } entry) return;
            if (e.NewTextValue.Contains('\n') || e.NewTextValue.Contains('\r') || e.NewTextValue.Contains('\t'))
            {
                if (entry.BindingContext is PrItem item)
                {
                    var parentBatchPr = ViewModel.BatchPrEntries.FirstOrDefault(b => b.Items.Contains(item));
                    if (parentBatchPr != null)
                    {
                        ViewModel.HandleInlineItemPaste(item, e.NewTextValue, parentBatchPr.Items);
                        parentBatchPr.NotifyItemsChanged();
                    }
                }
            }
        }

        private void OnBatchItemQuantityTextChanged(object? sender, TextChangedEventArgs e)
        {
            if (string.IsNullOrWhiteSpace(e.NewTextValue) || ViewModel == null) return;
            if (sender is not Entry { IsFocused: true } entry) return;
            if (e.NewTextValue.Contains('\n') || e.NewTextValue.Contains('\r') || e.NewTextValue.Contains('\t'))
            {
                if (entry.BindingContext is PrItem item)
                {
                    var parentBatchPr = ViewModel.BatchPrEntries.FirstOrDefault(b => b.Items.Contains(item));
                    if (parentBatchPr != null)
                    {
                        ViewModel.HandleInlineQuantityPaste(item, e.NewTextValue, parentBatchPr.Items);
                        parentBatchPr.NotifyItemsChanged();
                    }
                }
            }
        }

        private void OnBatchItemUnitTextChanged(object? sender, TextChangedEventArgs e)
        {
            if (string.IsNullOrWhiteSpace(e.NewTextValue) || ViewModel == null) return;
            if (sender is not Entry { IsFocused: true } entry) return;
            if (e.NewTextValue.Contains('\n') || e.NewTextValue.Contains('\r') || e.NewTextValue.Contains('\t'))
            {
                if (entry.BindingContext is PrItem item)
                {
                    var parentBatchPr = ViewModel.BatchPrEntries.FirstOrDefault(b => b.Items.Contains(item));
                    if (parentBatchPr != null)
                    {
                        ViewModel.HandleInlineUnitPaste(item, e.NewTextValue, parentBatchPr.Items);
                        parentBatchPr.NotifyItemsChanged();
                    }
                }
            }
        }

        private void OnBatchPrNoTextChanged(object? sender, TextChangedEventArgs e)
        {
            if (string.IsNullOrWhiteSpace(e.NewTextValue) || ViewModel == null) return;
            if (sender is not Entry { IsFocused: true } entry) return;
            if (e.NewTextValue.Contains('\n') || e.NewTextValue.Contains('\r') || e.NewTextValue.Contains('\t'))
            {
                if (entry.BindingContext is BatchPrEntry batchEntry)
                {
                    ViewModel.HandleBatchPrRowPaste(batchEntry, e.NewTextValue, isPrNoColumn: true);
                }
            }
        }

        private void OnBatchDescriptionTextChanged(object? sender, TextChangedEventArgs e)
        {
            if (string.IsNullOrWhiteSpace(e.NewTextValue) || ViewModel == null) return;
            if (sender is not Entry { IsFocused: true } entry) return;
            if (e.NewTextValue.Contains('\n') || e.NewTextValue.Contains('\r') || e.NewTextValue.Contains('\t'))
            {
                if (entry.BindingContext is BatchPrEntry batchEntry)
                {
                    ViewModel.HandleBatchPrRowPaste(batchEntry, e.NewTextValue, isPrNoColumn: false);
                }
            }
        }
    }
}
