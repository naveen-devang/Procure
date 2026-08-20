using System;
using System.Globalization;
using Microsoft.Maui.Controls;
using Procure.Models;
using Procure.PageModels;

namespace Procure.Pages.Modals
{
    public partial class AddPoModal : ContentView
    {
        private PrListPageModel? ViewModel => BindingContext as PrListPageModel;

        public AddPoModal()
        {
            InitializeComponent();
        }

        private void OnPoItemQuantityTextChanged(object? sender, TextChangedEventArgs e)
        {
            if (sender is Entry entry && entry.BindingContext is PoRfqItemSelection item)
            {
                if (string.IsNullOrWhiteSpace(e.NewTextValue))
                {
                    if (item.Quantity != 0m)
                    {
                        item.Quantity = 0m;
                        item.OnPriceOrSelectionChanged?.Invoke();
                        ViewModel?.RecalculatePoModalTotals();
                    }
                    return;
                }

                var cleanVal = e.NewTextValue.Replace(",", "").Trim();
                if (decimal.TryParse(cleanVal, NumberStyles.Any, CultureInfo.InvariantCulture, out var qty) ||
                    decimal.TryParse(cleanVal, NumberStyles.Any, CultureInfo.CurrentCulture, out qty))
                {
                    if (item.Quantity != qty)
                    {
                        item.Quantity = qty;
                        item.OnPriceOrSelectionChanged?.Invoke();
                        ViewModel?.RecalculatePoModalTotals();
                    }
                }
            }
        }

        private void OnPoItemUnitPriceTextChanged(object? sender, TextChangedEventArgs e)
        {
            if (sender is Entry entry && entry.BindingContext is PoRfqItemSelection item)
            {
                if (string.IsNullOrWhiteSpace(e.NewTextValue))
                {
                    if (item.QuotedUnitPrice != null)
                    {
                        item.QuotedUnitPrice = null;
                        item.OnPriceOrSelectionChanged?.Invoke();
                        ViewModel?.RecalculatePoModalTotals();
                    }
                    return;
                }

                var cleanVal = e.NewTextValue.Replace(",", "").Replace("$", "").Replace("AED", "").Trim();
                if (decimal.TryParse(cleanVal, NumberStyles.Any, CultureInfo.InvariantCulture, out var price) ||
                    decimal.TryParse(cleanVal, NumberStyles.Any, CultureInfo.CurrentCulture, out price))
                {
                    if (item.QuotedUnitPrice != price)
                    {
                        item.QuotedUnitPrice = price;
                        item.OnPriceOrSelectionChanged?.Invoke();
                        ViewModel?.RecalculatePoModalTotals();
                    }
                }
            }
        }

        private void OnPoItemDiscountTextChanged(object? sender, TextChangedEventArgs e)
        {
            if (sender is Entry entry && entry.BindingContext is PoRfqItemSelection item)
            {
                if (string.IsNullOrWhiteSpace(e.NewTextValue))
                {
                    if (item.Discount != null)
                    {
                        item.Discount = null;
                        item.OnPriceOrSelectionChanged?.Invoke();
                        ViewModel?.RecalculatePoModalTotals();
                    }
                    return;
                }

                var cleanVal = e.NewTextValue.Replace(",", "").Replace("$", "").Replace("AED", "").Replace("%", "").Trim();
                if (decimal.TryParse(cleanVal, NumberStyles.Any, CultureInfo.InvariantCulture, out var discount) ||
                    decimal.TryParse(cleanVal, NumberStyles.Any, CultureInfo.CurrentCulture, out discount))
                {
                    if (item.Discount != discount)
                    {
                        item.Discount = discount;
                        item.OnPriceOrSelectionChanged?.Invoke();
                        ViewModel?.RecalculatePoModalTotals();
                    }
                }
            }
        }

        private void OnPoItemCheckedChanged(object? sender, CheckedChangedEventArgs e)
        {
            if (sender is CheckBox cb && cb.BindingContext is PoRfqItemSelection item)
            {
                item.OnPriceOrSelectionChanged?.Invoke();
                ViewModel?.RecalculatePoModalTotals();
            }
        }

        private void OnPoBaseAmountTextChanged(object? sender, TextChangedEventArgs e)
        {
            if (sender is Entry entry && entry.BindingContext is PoRfqSelection selection)
            {
                if (string.IsNullOrWhiteSpace(e.NewTextValue))
                {
                    if (selection.CustomBaseAmount != null)
                    {
                        selection.CustomBaseAmount = null;
                        selection.NotifyCalculationsChanged();
                        ViewModel?.RecalculatePoModalTotals();
                    }
                    return;
                }

                var cleanVal = e.NewTextValue.Replace(",", "").Replace("$", "").Replace("AED", "").Trim();
                if (decimal.TryParse(cleanVal, NumberStyles.Any, CultureInfo.InvariantCulture, out var baseAmt) ||
                    decimal.TryParse(cleanVal, NumberStyles.Any, CultureInfo.CurrentCulture, out baseAmt))
                {
                    if (selection.CustomBaseAmount != baseAmt)
                    {
                        selection.CustomBaseAmount = baseAmt;
                        selection.NotifyCalculationsChanged();
                        ViewModel?.RecalculatePoModalTotals();
                    }
                }
            }
        }

        private void OnPoFreightTextChanged(object? sender, TextChangedEventArgs e)
        {
            if (sender is Entry entry && entry.BindingContext is PoRfqSelection selection)
            {
                if (string.IsNullOrWhiteSpace(e.NewTextValue))
                {
                    if (selection.Freight != null)
                    {
                        selection.Freight = null;
                        selection.NotifyCalculationsChanged();
                        ViewModel?.RecalculatePoModalTotals();
                    }
                    return;
                }

                var cleanVal = e.NewTextValue.Replace(",", "").Replace("$", "").Replace("AED", "").Trim();
                if (decimal.TryParse(cleanVal, NumberStyles.Any, CultureInfo.InvariantCulture, out var freight) ||
                    decimal.TryParse(cleanVal, NumberStyles.Any, CultureInfo.CurrentCulture, out freight))
                {
                    if (selection.Freight != freight)
                    {
                        selection.Freight = freight;
                        selection.NotifyCalculationsChanged();
                        ViewModel?.RecalculatePoModalTotals();
                    }
                }
            }
        }

        private void OnPoOtherChargesTextChanged(object? sender, TextChangedEventArgs e)
        {
            if (sender is Entry entry && entry.BindingContext is PoRfqSelection selection)
            {
                if (string.IsNullOrWhiteSpace(e.NewTextValue))
                {
                    if (selection.OtherCharges != null)
                    {
                        selection.OtherCharges = null;
                        selection.NotifyCalculationsChanged();
                        ViewModel?.RecalculatePoModalTotals();
                    }
                    return;
                }

                var cleanVal = e.NewTextValue.Replace(",", "").Replace("$", "").Replace("AED", "").Trim();
                if (decimal.TryParse(cleanVal, NumberStyles.Any, CultureInfo.InvariantCulture, out var charges) ||
                    decimal.TryParse(cleanVal, NumberStyles.Any, CultureInfo.CurrentCulture, out charges))
                {
                    if (selection.OtherCharges != charges)
                    {
                        selection.OtherCharges = charges;
                        selection.NotifyCalculationsChanged();
                        ViewModel?.RecalculatePoModalTotals();
                    }
                }
            }
        }

        private void OnPoOverallDiscountTextChanged(object? sender, TextChangedEventArgs e)
        {
            if (sender is Entry entry && entry.BindingContext is PoRfqSelection selection)
            {
                if (string.IsNullOrWhiteSpace(e.NewTextValue))
                {
                    if (selection.OverallDiscount != null)
                    {
                        selection.OverallDiscount = null;
                        selection.NotifyCalculationsChanged();
                        ViewModel?.RecalculatePoModalTotals();
                    }
                    return;
                }

                var cleanVal = e.NewTextValue.Replace(",", "").Replace("$", "").Replace("AED", "").Replace("%", "").Trim();
                if (decimal.TryParse(cleanVal, NumberStyles.Any, CultureInfo.InvariantCulture, out var discount) ||
                    decimal.TryParse(cleanVal, NumberStyles.Any, CultureInfo.CurrentCulture, out discount))
                {
                    if (selection.OverallDiscount != discount)
                    {
                        selection.OverallDiscount = discount;
                        selection.NotifyCalculationsChanged();
                        ViewModel?.RecalculatePoModalTotals();
                    }
                }
            }
        }

        private void OnPoVatTypeSelectedIndexChanged(object? sender, EventArgs e)
        {
            if (sender is Picker picker && picker.BindingContext is PoRfqSelection selection)
            {
                if (picker.SelectedItem is string vatType && selection.VatType != vatType)
                {
                    selection.VatType = vatType;
                    selection.NotifyCalculationsChanged();
                    ViewModel?.RecalculatePoModalTotals();
                }
            }
        }
    }
}
