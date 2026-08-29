using System;
using System.Globalization;
using Microsoft.Maui.Controls;
using Procure.Models;
using Procure.PageModels;

namespace Procure.Pages.Modals
{
    // Handlers only parse and assign. Each model setter already fans out through
    // OnPriceOrSelectionChanged / NotifyCalculationsChanged / OnTotalsRecalculated, so the
    // explicit re-invocations that used to follow every assignment ran the full wizard
    // recalculation three times per keystroke.
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
                    item.Quantity = 0m;
                    return;
                }

                var cleanVal = e.NewTextValue.Replace(",", "").Trim();
                if (decimal.TryParse(cleanVal, NumberStyles.Any, CultureInfo.InvariantCulture, out var qty) ||
                    decimal.TryParse(cleanVal, NumberStyles.Any, CultureInfo.CurrentCulture, out qty))
                {
                    item.Quantity = qty;
                }
            }
        }

        private void OnPoItemUnitPriceTextChanged(object? sender, TextChangedEventArgs e)
        {
            if (sender is Entry entry && entry.BindingContext is PoRfqItemSelection item)
            {
                if (string.IsNullOrWhiteSpace(e.NewTextValue))
                {
                    item.QuotedUnitPrice = null;
                    return;
                }

                var cleanVal = e.NewTextValue.Replace(",", "").Replace("$", "").Replace("AED", "").Trim();
                if (decimal.TryParse(cleanVal, NumberStyles.Any, CultureInfo.InvariantCulture, out var price) ||
                    decimal.TryParse(cleanVal, NumberStyles.Any, CultureInfo.CurrentCulture, out price))
                {
                    item.QuotedUnitPrice = price;
                }
            }
        }

        private void OnPoItemDiscountTextChanged(object? sender, TextChangedEventArgs e)
        {
            if (sender is Entry entry && entry.BindingContext is PoRfqItemSelection item)
            {
                if (string.IsNullOrWhiteSpace(e.NewTextValue))
                {
                    item.Discount = null;
                    return;
                }

                var cleanVal = e.NewTextValue.Replace(",", "").Replace("$", "").Replace("AED", "").Replace("%", "").Trim();
                if (decimal.TryParse(cleanVal, NumberStyles.Any, CultureInfo.InvariantCulture, out var discount) ||
                    decimal.TryParse(cleanVal, NumberStyles.Any, CultureInfo.CurrentCulture, out discount))
                {
                    item.Discount = discount;
                }
            }
        }

        private void OnPoBaseAmountTextChanged(object? sender, TextChangedEventArgs e)
        {
            if (sender is Entry entry && entry.BindingContext is PoRfqSelection selection)
            {
                if (string.IsNullOrWhiteSpace(e.NewTextValue))
                {
                    selection.CustomBaseAmount = null;
                    return;
                }

                var cleanVal = e.NewTextValue.Replace(",", "").Replace("$", "").Replace("AED", "").Trim();
                if (decimal.TryParse(cleanVal, NumberStyles.Any, CultureInfo.InvariantCulture, out var baseAmt) ||
                    decimal.TryParse(cleanVal, NumberStyles.Any, CultureInfo.CurrentCulture, out baseAmt))
                {
                    selection.CustomBaseAmount = baseAmt;
                }
            }
        }

        private void OnPoFreightTextChanged(object? sender, TextChangedEventArgs e)
        {
            if (sender is Entry entry && entry.BindingContext is PoRfqSelection selection)
            {
                if (string.IsNullOrWhiteSpace(e.NewTextValue))
                {
                    selection.Freight = null;
                    return;
                }

                var cleanVal = e.NewTextValue.Replace(",", "").Replace("$", "").Replace("AED", "").Trim();
                if (decimal.TryParse(cleanVal, NumberStyles.Any, CultureInfo.InvariantCulture, out var freight) ||
                    decimal.TryParse(cleanVal, NumberStyles.Any, CultureInfo.CurrentCulture, out freight))
                {
                    selection.Freight = freight;
                }
            }
        }

        private void OnPoTransportRateTextChanged(object? sender, TextChangedEventArgs e)
        {
            if (sender is Entry entry && entry.BindingContext is PoRfqSelection selection)
            {
                if (string.IsNullOrWhiteSpace(e.NewTextValue))
                {
                    selection.TransportRatePerUnit = null;
                    return;
                }

                var cleanVal = e.NewTextValue.Replace(",", "").Replace("$", "").Replace("AED", "").Trim();
                if (decimal.TryParse(cleanVal, NumberStyles.Any, CultureInfo.InvariantCulture, out var rate) ||
                    decimal.TryParse(cleanVal, NumberStyles.Any, CultureInfo.CurrentCulture, out rate))
                {
                    selection.TransportRatePerUnit = rate;
                }
            }
        }

        private void OnPoOtherChargesTextChanged(object? sender, TextChangedEventArgs e)
        {
            if (sender is Entry entry && entry.BindingContext is PoRfqSelection selection)
            {
                if (string.IsNullOrWhiteSpace(e.NewTextValue))
                {
                    selection.OtherCharges = null;
                    return;
                }

                var cleanVal = e.NewTextValue.Replace(",", "").Replace("$", "").Replace("AED", "").Trim();
                if (decimal.TryParse(cleanVal, NumberStyles.Any, CultureInfo.InvariantCulture, out var charges) ||
                    decimal.TryParse(cleanVal, NumberStyles.Any, CultureInfo.CurrentCulture, out charges))
                {
                    selection.OtherCharges = charges;
                }
            }
        }

        private void OnPoOverallDiscountTextChanged(object? sender, TextChangedEventArgs e)
        {
            if (sender is Entry entry && entry.BindingContext is PoRfqSelection selection)
            {
                if (string.IsNullOrWhiteSpace(e.NewTextValue))
                {
                    selection.OverallDiscount = null;
                    return;
                }

                var cleanVal = e.NewTextValue.Replace(",", "").Replace("$", "").Replace("AED", "").Replace("%", "").Trim();
                if (decimal.TryParse(cleanVal, NumberStyles.Any, CultureInfo.InvariantCulture, out var discount) ||
                    decimal.TryParse(cleanVal, NumberStyles.Any, CultureInfo.CurrentCulture, out discount))
                {
                    selection.OverallDiscount = discount;
                }
            }
        }
    }
}
