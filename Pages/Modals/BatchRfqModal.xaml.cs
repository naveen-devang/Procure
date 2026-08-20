using System;
using System.Globalization;
using System.Threading.Tasks;
using Microsoft.Maui.Controls;
using Procure.Models;
using Procure.PageModels;
using Procure.Utilities;

namespace Procure.Pages.Modals
{
    public partial class BatchRfqModal : ContentView
    {
        private PrListPageModel? ViewModel => BindingContext as PrListPageModel;

        public BatchRfqModal()
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

#if WINDOWS
        private void OnBatchRfqUnitPriceEntryLoaded(object? sender, EventArgs e) =>
            AttachBatchRfqEntryHandlers(sender, OnBatchRfqUnitPriceTextBoxPaste, OnBatchRfqUnitPriceTextBoxPreviewKeyDown);

        private void OnBatchRfqUnitPriceEntryUnloaded(object? sender, EventArgs e) =>
            DetachBatchRfqEntryHandlers(sender, OnBatchRfqUnitPriceTextBoxPaste, OnBatchRfqUnitPriceTextBoxPreviewKeyDown);

        private void OnBatchRfqDiscountEntryLoaded(object? sender, EventArgs e) =>
            AttachBatchRfqEntryHandlers(sender, OnBatchRfqDiscountTextBoxPaste, OnBatchRfqDiscountTextBoxPreviewKeyDown);

        private void OnBatchRfqDiscountEntryUnloaded(object? sender, EventArgs e) =>
            DetachBatchRfqEntryHandlers(sender, OnBatchRfqDiscountTextBoxPaste, OnBatchRfqDiscountTextBoxPreviewKeyDown);

        private void OnBatchRfqLastPriceEntryLoaded(object? sender, EventArgs e) =>
            AttachBatchRfqEntryHandlers(sender, OnBatchRfqLastPriceTextBoxPaste, OnBatchRfqLastPriceTextBoxPreviewKeyDown);

        private void OnBatchRfqLastPriceEntryUnloaded(object? sender, EventArgs e) =>
            DetachBatchRfqEntryHandlers(sender, OnBatchRfqLastPriceTextBoxPaste, OnBatchRfqLastPriceTextBoxPreviewKeyDown);

        private static void AttachBatchRfqEntryHandlers(
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

        private static void DetachBatchRfqEntryHandlers(
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

        private void OnBatchRfqUnitPriceTextBoxPaste(object sender, Microsoft.UI.Xaml.Controls.TextControlPasteEventArgs e)
        {
            if (IsClipboardTextAvailable())
            {
                e.Handled = true;
                _ = ExecuteBatchRfqPricingPasteAsync(sender, RfqPricingColumn.UnitPrice);
            }
        }

        private void OnBatchRfqUnitPriceTextBoxPreviewKeyDown(object sender, Microsoft.UI.Xaml.Input.KeyRoutedEventArgs e)
        {
            if (IsCtrlV(e) && IsClipboardTextAvailable())
            {
                e.Handled = true;
                _ = ExecuteBatchRfqPricingPasteAsync(sender, RfqPricingColumn.UnitPrice);
            }
        }

        private void OnBatchRfqDiscountTextBoxPaste(object sender, Microsoft.UI.Xaml.Controls.TextControlPasteEventArgs e)
        {
            if (IsClipboardTextAvailable())
            {
                e.Handled = true;
                _ = ExecuteBatchRfqPricingPasteAsync(sender, RfqPricingColumn.Discount);
            }
        }

        private void OnBatchRfqDiscountTextBoxPreviewKeyDown(object sender, Microsoft.UI.Xaml.Input.KeyRoutedEventArgs e)
        {
            if (IsCtrlV(e) && IsClipboardTextAvailable())
            {
                e.Handled = true;
                _ = ExecuteBatchRfqPricingPasteAsync(sender, RfqPricingColumn.Discount);
            }
        }

        private void OnBatchRfqLastPriceTextBoxPaste(object sender, Microsoft.UI.Xaml.Controls.TextControlPasteEventArgs e)
        {
            if (IsClipboardTextAvailable())
            {
                e.Handled = true;
                _ = ExecuteBatchRfqPricingPasteAsync(sender, RfqPricingColumn.LastPrice);
            }
        }

        private void OnBatchRfqLastPriceTextBoxPreviewKeyDown(object sender, Microsoft.UI.Xaml.Input.KeyRoutedEventArgs e)
        {
            if (IsCtrlV(e) && IsClipboardTextAvailable())
            {
                e.Handled = true;
                _ = ExecuteBatchRfqPricingPasteAsync(sender, RfqPricingColumn.LastPrice);
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

        private async Task ExecuteBatchRfqPricingPasteAsync(object sender, RfqPricingColumn column)
        {
            var entry = GetEntryFromSender(sender);
            if (entry?.BindingContext is RfqItem item && ViewModel != null)
            {
                try
                {
                    var text = await Microsoft.Maui.ApplicationModel.DataTransfer.Clipboard.Default.GetTextAsync();
                    if (!string.IsNullOrEmpty(text))
                    {
                        ViewModel.HandleBatchRfqPricingPaste(item, text, column);
                    }
                }
                catch { }
            }
        }
#else
        private void OnBatchRfqUnitPriceEntryLoaded(object? sender, EventArgs e) { }
        private void OnBatchRfqUnitPriceEntryUnloaded(object? sender, EventArgs e) { }
        private void OnBatchRfqDiscountEntryLoaded(object? sender, EventArgs e) { }
        private void OnBatchRfqDiscountEntryUnloaded(object? sender, EventArgs e) { }
        private void OnBatchRfqLastPriceEntryLoaded(object? sender, EventArgs e) { }
        private void OnBatchRfqLastPriceEntryUnloaded(object? sender, EventArgs e) { }
#endif

        private void OnBatchRfqUnitPriceTextChanged(object? sender, TextChangedEventArgs e)
        {
            if (sender is Entry entry && entry.BindingContext is RfqItem item)
            {
                if (string.IsNullOrWhiteSpace(e.NewTextValue))
                {
                    if (item.QuotedUnitPrice != null)
                    {
                        item.QuotedUnitPrice = null;
                        ViewModel?.RecalculateBatchRfqTotals();
                    }
                    return;
                }

                if (e.NewTextValue.Contains('\n') || e.NewTextValue.Contains('\r') || e.NewTextValue.Contains('\t'))
                {
                    ViewModel?.HandleBatchRfqPricingPaste(item, e.NewTextValue, RfqPricingColumn.UnitPrice);
                    return;
                }

                var cleanVal = e.NewTextValue.Replace(",", "").Replace("$", "").Replace("AED", "").Trim();
                if (decimal.TryParse(cleanVal, NumberStyles.Any, CultureInfo.InvariantCulture, out var price) ||
                    decimal.TryParse(cleanVal, NumberStyles.Any, CultureInfo.CurrentCulture, out price))
                {
                    if (item.QuotedUnitPrice != price)
                    {
                        item.QuotedUnitPrice = price;
                        ViewModel?.RecalculateBatchRfqTotals();
                    }
                }
            }
        }

        private void OnBatchRfqDiscountTextChanged(object? sender, TextChangedEventArgs e)
        {
            if (sender is Entry entry && entry.BindingContext is RfqItem item)
            {
                if (string.IsNullOrWhiteSpace(e.NewTextValue))
                {
                    if (item.Discount != null)
                    {
                        item.Discount = null;
                        ViewModel?.RecalculateBatchRfqTotals();
                    }
                    return;
                }

                if (e.NewTextValue.Contains('\n') || e.NewTextValue.Contains('\r') || e.NewTextValue.Contains('\t'))
                {
                    ViewModel?.HandleBatchRfqPricingPaste(item, e.NewTextValue, RfqPricingColumn.Discount);
                    return;
                }

                var cleanVal = e.NewTextValue.Replace(",", "").Replace("$", "").Replace("AED", "").Replace("%", "").Trim();
                if (decimal.TryParse(cleanVal, NumberStyles.Any, CultureInfo.InvariantCulture, out var discount) ||
                    decimal.TryParse(cleanVal, NumberStyles.Any, CultureInfo.CurrentCulture, out discount))
                {
                    if (item.Discount != discount)
                    {
                        item.Discount = discount;
                        ViewModel?.RecalculateBatchRfqTotals();
                    }
                }
            }
        }

        private void OnBatchRfqLastPriceTextChanged(object? sender, TextChangedEventArgs e)
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
                    ViewModel?.HandleBatchRfqPricingPaste(item, e.NewTextValue, RfqPricingColumn.LastPrice);
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

        private void OnBatchRfqItemCheckedChanged(object? sender, CheckedChangedEventArgs e)
        {
            if (sender is CheckBox cb && cb.BindingContext is RfqItem item)
            {
                item.IsQuoted = e.Value;
                ViewModel?.RecalculateBatchRfqTotals();
            }
        }

        private void OnBatchRfqQuoteAmountTextChanged(object? sender, TextChangedEventArgs e)
        {
            if (ViewModel == null) return;
            if (string.IsNullOrWhiteSpace(e.NewTextValue))
            {
                if (ViewModel.BatchRfqQuoteAmount != null)
                {
                    ViewModel.BatchRfqQuoteAmount = null;
                }
                return;
            }

            var cleanVal = e.NewTextValue.Replace(",", "").Replace("$", "").Replace("AED", "").Trim();
            if (decimal.TryParse(cleanVal, NumberStyles.Any, CultureInfo.InvariantCulture, out var amt) ||
                decimal.TryParse(cleanVal, NumberStyles.Any, CultureInfo.CurrentCulture, out amt))
            {
                if (ViewModel.BatchRfqQuoteAmount != amt)
                {
                    ViewModel.BatchRfqQuoteAmount = amt;
                }
            }
        }

        private void OnBatchRfqFreightTextChanged(object? sender, TextChangedEventArgs e)
        {
            if (ViewModel == null) return;
            if (string.IsNullOrWhiteSpace(e.NewTextValue))
            {
                if (ViewModel.BatchRfqFreight != null)
                {
                    ViewModel.BatchRfqFreight = null;
                }
                return;
            }

            var cleanVal = e.NewTextValue.Replace(",", "").Replace("$", "").Replace("AED", "").Trim();
            if (decimal.TryParse(cleanVal, NumberStyles.Any, CultureInfo.InvariantCulture, out var fr) ||
                decimal.TryParse(cleanVal, NumberStyles.Any, CultureInfo.CurrentCulture, out fr))
            {
                if (ViewModel.BatchRfqFreight != fr)
                {
                    ViewModel.BatchRfqFreight = fr;
                }
            }
        }

        private void OnBatchRfqOtherChargesTextChanged(object? sender, TextChangedEventArgs e)
        {
            if (ViewModel == null) return;
            if (string.IsNullOrWhiteSpace(e.NewTextValue))
            {
                if (ViewModel.BatchRfqOtherCharges != null)
                {
                    ViewModel.BatchRfqOtherCharges = null;
                }
                return;
            }

            var cleanVal = e.NewTextValue.Replace(",", "").Replace("$", "").Replace("AED", "").Trim();
            if (decimal.TryParse(cleanVal, NumberStyles.Any, CultureInfo.InvariantCulture, out var oth) ||
                decimal.TryParse(cleanVal, NumberStyles.Any, CultureInfo.CurrentCulture, out oth))
            {
                if (ViewModel.BatchRfqOtherCharges != oth)
                {
                    ViewModel.BatchRfqOtherCharges = oth;
                }
            }
        }

        private void OnBatchRfqTotalDiscountTextChanged(object? sender, TextChangedEventArgs e)
        {
            if (ViewModel == null) return;
            if (string.IsNullOrWhiteSpace(e.NewTextValue))
            {
                if (ViewModel.BatchRfqDiscount != null)
                {
                    ViewModel.BatchRfqDiscount = null;
                }
                return;
            }

            var cleanVal = e.NewTextValue.Replace(",", "").Replace("$", "").Replace("AED", "").Replace("%", "").Trim();
            if (decimal.TryParse(cleanVal, NumberStyles.Any, CultureInfo.InvariantCulture, out var disc) ||
                decimal.TryParse(cleanVal, NumberStyles.Any, CultureInfo.CurrentCulture, out disc))
            {
                if (ViewModel.BatchRfqDiscount != disc)
                {
                    ViewModel.BatchRfqDiscount = disc;
                }
            }
        }

        private void OnBatchRfqVatTypeSelectionChanged(object? sender, EventArgs e)
        {
            ViewModel?.RecalculateBatchRfqTotals();
        }

        private void OnBatchRfqCurrencySelectionChanged(object? sender, EventArgs e)
        {
            ViewModel?.RecalculateBatchRfqTotals();
        }
    }
}
