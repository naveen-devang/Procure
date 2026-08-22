using System;
using System.Globalization;
using System.Threading.Tasks;
using Microsoft.Maui.Controls;
using Procure.Models;
using Procure.PageModels;
using Procure.Utilities;

namespace Procure.Pages.Modals
{
    public partial class AddRfqModal : ContentView
    {
        private PrListPageModel? ViewModel => BindingContext as PrListPageModel;

        public AddRfqModal()
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
            if (IsVisible) RfqNoEntry.Focus();
        });

        private static Entry? GetEntryFromSender(object sender)
        {
            if (sender is Entry e) return e;
#if WINDOWS
            if (sender is Microsoft.UI.Xaml.Controls.TextBox tb && tb.Tag is Entry te) return te;
#endif
            return null;
        }

#if WINDOWS
        private void OnRfqUnitPriceEntryLoaded(object? sender, EventArgs e) =>
            AttachRfqEntryHandlers(sender, OnRfqUnitPriceTextBoxPaste, OnRfqUnitPriceTextBoxPreviewKeyDown);

        private void OnRfqUnitPriceEntryUnloaded(object? sender, EventArgs e) =>
            DetachRfqEntryHandlers(sender, OnRfqUnitPriceTextBoxPaste, OnRfqUnitPriceTextBoxPreviewKeyDown);

        private void OnRfqDiscountEntryLoaded(object? sender, EventArgs e) =>
            AttachRfqEntryHandlers(sender, OnRfqDiscountTextBoxPaste, OnRfqDiscountTextBoxPreviewKeyDown);

        private void OnRfqDiscountEntryUnloaded(object? sender, EventArgs e) =>
            DetachRfqEntryHandlers(sender, OnRfqDiscountTextBoxPaste, OnRfqDiscountTextBoxPreviewKeyDown);

        private void OnRfqLastPriceEntryLoaded(object? sender, EventArgs e) =>
            AttachRfqEntryHandlers(sender, OnRfqLastPriceTextBoxPaste, OnRfqLastPriceTextBoxPreviewKeyDown);

        private void OnRfqLastPriceEntryUnloaded(object? sender, EventArgs e) =>
            DetachRfqEntryHandlers(sender, OnRfqLastPriceTextBoxPaste, OnRfqLastPriceTextBoxPreviewKeyDown);

        private static void AttachRfqEntryHandlers(
            object? sender,
            Microsoft.UI.Xaml.Controls.TextControlPasteEventHandler pasteHandler,
            Microsoft.UI.Xaml.Input.KeyEventHandler previewKeyDownHandler)
        {
            if (sender is Entry entry && entry.Handler?.PlatformView is Microsoft.UI.Xaml.Controls.TextBox textBox)
            {
                textBox.Tag = entry;
                textBox.Paste -= pasteHandler;
                textBox.Paste += pasteHandler;
                textBox.PreviewKeyDown -= previewKeyDownHandler;
                textBox.PreviewKeyDown += previewKeyDownHandler;
            }
        }

        private static void DetachRfqEntryHandlers(
            object? sender,
            Microsoft.UI.Xaml.Controls.TextControlPasteEventHandler pasteHandler,
            Microsoft.UI.Xaml.Input.KeyEventHandler previewKeyDownHandler)
        {
            if (sender is Entry entry && entry.Handler?.PlatformView is Microsoft.UI.Xaml.Controls.TextBox textBox)
            {
                textBox.Paste -= pasteHandler;
                textBox.PreviewKeyDown -= previewKeyDownHandler;
                textBox.Tag = null;
            }
        }

        private void OnRfqUnitPriceTextBoxPaste(object sender, Microsoft.UI.Xaml.Controls.TextControlPasteEventArgs e)
        {
            if (IsClipboardTextAvailable())
            {
                e.Handled = true;
                _ = ExecuteRfqPricingPasteAsync(sender, RfqPricingColumn.UnitPrice);
            }
        }

        private void OnRfqUnitPriceTextBoxPreviewKeyDown(object sender, Microsoft.UI.Xaml.Input.KeyRoutedEventArgs e)
        {
            if (IsCtrlV(e) && IsClipboardTextAvailable())
            {
                e.Handled = true;
                _ = ExecuteRfqPricingPasteAsync(sender, RfqPricingColumn.UnitPrice);
            }
        }

        private void OnRfqDiscountTextBoxPaste(object sender, Microsoft.UI.Xaml.Controls.TextControlPasteEventArgs e)
        {
            if (IsClipboardTextAvailable())
            {
                e.Handled = true;
                _ = ExecuteRfqPricingPasteAsync(sender, RfqPricingColumn.Discount);
            }
        }

        private void OnRfqDiscountTextBoxPreviewKeyDown(object sender, Microsoft.UI.Xaml.Input.KeyRoutedEventArgs e)
        {
            if (IsCtrlV(e) && IsClipboardTextAvailable())
            {
                e.Handled = true;
                _ = ExecuteRfqPricingPasteAsync(sender, RfqPricingColumn.Discount);
            }
        }

        private void OnRfqLastPriceTextBoxPaste(object sender, Microsoft.UI.Xaml.Controls.TextControlPasteEventArgs e)
        {
            if (IsClipboardTextAvailable())
            {
                e.Handled = true;
                _ = ExecuteRfqPricingPasteAsync(sender, RfqPricingColumn.LastPrice);
            }
        }

        private void OnRfqLastPriceTextBoxPreviewKeyDown(object sender, Microsoft.UI.Xaml.Input.KeyRoutedEventArgs e)
        {
            if (IsCtrlV(e) && IsClipboardTextAvailable())
            {
                e.Handled = true;
                _ = ExecuteRfqPricingPasteAsync(sender, RfqPricingColumn.LastPrice);
            }
        }

        private static bool IsClipboardTextAvailable()
        {
            try
            {
                var dataPackage = Windows.ApplicationModel.DataTransfer.Clipboard.GetContent();
                return dataPackage != null && dataPackage.Contains(Windows.ApplicationModel.DataTransfer.StandardDataFormats.Text);
            }
            catch
            {
                return false;
            }
        }

        private static bool IsCtrlV(Microsoft.UI.Xaml.Input.KeyRoutedEventArgs e)
        {
            return e.Key == Windows.System.VirtualKey.V &&
                Microsoft.UI.Input.InputKeyboardSource.GetKeyStateForCurrentThread(Windows.System.VirtualKey.Control).HasFlag(Windows.UI.Core.CoreVirtualKeyStates.Down);
        }

        private async Task ExecuteRfqPricingPasteAsync(object sender, RfqPricingColumn column)
        {
            var entry = GetEntryFromSender(sender);
            if (entry?.BindingContext is RfqItem item && ViewModel != null)
            {
                try
                {
                    var text = await Microsoft.Maui.ApplicationModel.DataTransfer.Clipboard.Default.GetTextAsync();
                    if (!string.IsNullOrEmpty(text))
                    {
                        ViewModel.HandleRfqPricingPaste(item, text, column);
                    }
                }
                catch { }
            }
        }
#else
        private void OnRfqUnitPriceEntryLoaded(object? sender, EventArgs e) { }
        private void OnRfqUnitPriceEntryUnloaded(object? sender, EventArgs e) { }
        private void OnRfqDiscountEntryLoaded(object? sender, EventArgs e) { }
        private void OnRfqDiscountEntryUnloaded(object? sender, EventArgs e) { }
        private void OnRfqLastPriceEntryLoaded(object? sender, EventArgs e) { }
        private void OnRfqLastPriceEntryUnloaded(object? sender, EventArgs e) { }
#endif

        private void OnRfqUnitPriceTextChanged(object? sender, TextChangedEventArgs e)
        {
            if (sender is Entry entry && entry.BindingContext is RfqItem item)
            {
                if (string.IsNullOrWhiteSpace(e.NewTextValue))
                {
                    if (item.QuotedUnitPrice != null)
                    {
                        item.QuotedUnitPrice = null;
                        ViewModel?.RecalculateRfqTotals();
                    }
                    return;
                }

                if (e.NewTextValue.Contains('\n') || e.NewTextValue.Contains('\r') || e.NewTextValue.Contains('\t'))
                {
                    ViewModel?.HandleRfqPricingPaste(item, e.NewTextValue, RfqPricingColumn.UnitPrice);
                    return;
                }

                var cleanVal = e.NewTextValue.Replace(",", "").Replace("$", "").Replace("AED", "").Trim();
                if (decimal.TryParse(cleanVal, NumberStyles.Any, CultureInfo.InvariantCulture, out var price) ||
                    decimal.TryParse(cleanVal, NumberStyles.Any, CultureInfo.CurrentCulture, out price))
                {
                    if (item.QuotedUnitPrice != price)
                    {
                        item.QuotedUnitPrice = price;
                        ViewModel?.RecalculateRfqTotals();
                    }
                }
            }
        }

        private void OnRfqDiscountTextChanged(object? sender, TextChangedEventArgs e)
        {
            if (sender is Entry entry && entry.BindingContext is RfqItem item)
            {
                if (string.IsNullOrWhiteSpace(e.NewTextValue))
                {
                    if (item.Discount != null)
                    {
                        item.Discount = null;
                        ViewModel?.RecalculateRfqTotals();
                    }
                    return;
                }

                if (e.NewTextValue.Contains('\n') || e.NewTextValue.Contains('\r') || e.NewTextValue.Contains('\t'))
                {
                    ViewModel?.HandleRfqPricingPaste(item, e.NewTextValue, RfqPricingColumn.Discount);
                    return;
                }

                var cleanVal = e.NewTextValue.Replace(",", "").Replace("$", "").Replace("AED", "").Replace("%", "").Trim();
                if (decimal.TryParse(cleanVal, NumberStyles.Any, CultureInfo.InvariantCulture, out var discount) ||
                    decimal.TryParse(cleanVal, NumberStyles.Any, CultureInfo.CurrentCulture, out discount))
                {
                    if (item.Discount != discount)
                    {
                        item.Discount = discount;
                        ViewModel?.RecalculateRfqTotals();
                    }
                }
            }
        }

        private void OnRfqLastPriceTextChanged(object? sender, TextChangedEventArgs e)
        {
            if (sender is Entry entry && entry.BindingContext is RfqItem item)
            {
                if (string.IsNullOrWhiteSpace(e.NewTextValue))
                {
                    if (item.LastPrice != null)
                    {
                        item.LastPrice = null;
                    }
                    return;
                }

                if (e.NewTextValue.Contains('\n') || e.NewTextValue.Contains('\r') || e.NewTextValue.Contains('\t'))
                {
                    ViewModel?.HandleRfqPricingPaste(item, e.NewTextValue, RfqPricingColumn.LastPrice);
                    return;
                }

                var cleanVal = e.NewTextValue.Replace(",", "").Replace("$", "").Replace("AED", "").Trim();
                if (decimal.TryParse(cleanVal, NumberStyles.Any, CultureInfo.InvariantCulture, out var lastPrice) ||
                    decimal.TryParse(cleanVal, NumberStyles.Any, CultureInfo.CurrentCulture, out lastPrice))
                {
                    if (item.LastPrice != lastPrice)
                    {
                        item.LastPrice = lastPrice;
                    }
                }
            }
        }

        private void OnRfqItemCheckedChanged(object? sender, CheckedChangedEventArgs e)
        {
            if (sender is CheckBox cb && cb.BindingContext is RfqItem item)
            {
                item.IsQuoted = e.Value;
                ViewModel?.RecalculateRfqTotals();
            }
        }

        private void OnRfqQuoteAmountTextChanged(object? sender, TextChangedEventArgs e)
        {
            if (ViewModel == null) return;
            if (string.IsNullOrWhiteSpace(e.NewTextValue))
            {
                if (ViewModel.NewRfqQuoteAmount != null)
                {
                    ViewModel.NewRfqQuoteAmount = null;
                }
                return;
            }

            var cleanVal = e.NewTextValue.Replace(",", "").Replace("$", "").Replace("AED", "").Trim();
            if (decimal.TryParse(cleanVal, NumberStyles.Any, CultureInfo.InvariantCulture, out var amt) ||
                decimal.TryParse(cleanVal, NumberStyles.Any, CultureInfo.CurrentCulture, out amt))
            {
                if (ViewModel.NewRfqQuoteAmount != amt)
                {
                    ViewModel.NewRfqQuoteAmount = amt;
                }
            }
        }

        private void OnRfqFreightTextChanged(object? sender, TextChangedEventArgs e)
        {
            if (ViewModel == null) return;
            if (string.IsNullOrWhiteSpace(e.NewTextValue))
            {
                if (ViewModel.NewRfqFreight != null)
                {
                    ViewModel.NewRfqFreight = null;
                }
                return;
            }

            var cleanVal = e.NewTextValue.Replace(",", "").Replace("$", "").Replace("AED", "").Trim();
            if (decimal.TryParse(cleanVal, NumberStyles.Any, CultureInfo.InvariantCulture, out var fr) ||
                decimal.TryParse(cleanVal, NumberStyles.Any, CultureInfo.CurrentCulture, out fr))
            {
                if (ViewModel.NewRfqFreight != fr)
                {
                    ViewModel.NewRfqFreight = fr;
                }
            }
        }

        private void OnRfqOtherChargesTextChanged(object? sender, TextChangedEventArgs e)
        {
            if (ViewModel == null) return;
            if (string.IsNullOrWhiteSpace(e.NewTextValue))
            {
                if (ViewModel.NewRfqOtherCharges != null)
                {
                    ViewModel.NewRfqOtherCharges = null;
                }
                return;
            }

            var cleanVal = e.NewTextValue.Replace(",", "").Replace("$", "").Replace("AED", "").Trim();
            if (decimal.TryParse(cleanVal, NumberStyles.Any, CultureInfo.InvariantCulture, out var oth) ||
                decimal.TryParse(cleanVal, NumberStyles.Any, CultureInfo.CurrentCulture, out oth))
            {
                if (ViewModel.NewRfqOtherCharges != oth)
                {
                    ViewModel.NewRfqOtherCharges = oth;
                }
            }
        }

        private void OnRfqTotalDiscountTextChanged(object? sender, TextChangedEventArgs e)
        {
            if (ViewModel == null) return;
            if (string.IsNullOrWhiteSpace(e.NewTextValue))
            {
                if (ViewModel.NewRfqDiscount != null)
                {
                    ViewModel.NewRfqDiscount = null;
                }
                return;
            }

            var cleanVal = e.NewTextValue.Replace(",", "").Replace("$", "").Replace("AED", "").Replace("%", "").Trim();
            if (decimal.TryParse(cleanVal, NumberStyles.Any, CultureInfo.InvariantCulture, out var disc) ||
                decimal.TryParse(cleanVal, NumberStyles.Any, CultureInfo.CurrentCulture, out disc))
            {
                if (ViewModel.NewRfqDiscount != disc)
                {
                    ViewModel.NewRfqDiscount = disc;
                }
            }
        }

        private void OnRfqVatTypeSelectionChanged(object? sender, EventArgs e)
        {
            ViewModel?.RecalculateRfqTotals();
        }

        private void OnRfqCurrencySelectionChanged(object? sender, EventArgs e)
        {
            ViewModel?.RecalculateRfqTotals();
        }
    }
}
