using System;
using System.Globalization;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Procure.Models;
using Procure.PageModels;
using Procure.Utilities;

namespace Procure.App.Platform;

/// <summary>
/// Two behaviours for the money boxes in the RFQ and PO dialogs, ported from the MAUI modals' code-behind.
///
/// <c>MoneyBox.PercentOf</c> on a discount box: typing a number followed by % turns it into that share of
/// the given amount ("5%" of a 250.00 unit price becomes 12.50), there and then. Resolved once, like
/// MAUI: it does not re-scale if the price changes afterwards.
///
/// <c>MoneyBox.PasteColumn</c> on an RFQ line's price, discount or last-price box: pasting (Ctrl+V or the
/// context menu) runs the page model's Excel paste, which fills this row and the rows below it from a
/// copied column. The same parser MAUI used; a single value just fills this row.
/// </summary>
public static class MoneyBox
{
    // ---- percent discounts ----

    public static readonly DependencyProperty PercentOfProperty = DependencyProperty.RegisterAttached(
        "PercentOf", typeof(object), typeof(MoneyBox), new PropertyMetadata(null, OnPercentOfChanged));

    public static object? GetPercentOf(DependencyObject d) => d.GetValue(PercentOfProperty);
    public static void SetPercentOf(DependencyObject d, object? value) => d.SetValue(PercentOfProperty, value);

    private static readonly DependencyProperty HookedProperty = DependencyProperty.RegisterAttached(
        "Hooked", typeof(bool), typeof(MoneyBox), new PropertyMetadata(false));

    private static void OnPercentOfChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not TextBox box || (bool)box.GetValue(HookedProperty)) return;
        box.SetValue(HookedProperty, true);
        box.TextChanged += (_, _) => ResolvePercent(box);
        ToolTipService.SetToolTip(box, "An amount, or a percentage like 5%");
    }

    private static void ResolvePercent(TextBox box)
    {
        var text = box.Text?.Trim();
        if (string.IsNullOrEmpty(text) || !text.EndsWith('%')) return;
        if (!TryParseAmount(text[..^1], out var percent)) return;

        var basis = GetPercentOf(box) switch
        {
            decimal m => m,
            double f => (decimal)f,
            int i => i,
            _ => 0m,
        };
        var amount = Math.Round(basis * percent / 100m, 2);
        box.Text = amount.ToString(CultureInfo.CurrentCulture);
        box.SelectionStart = box.Text.Length;
    }

    /// <summary>Reads "1,200.50", "AED 1200" or "$50" as a number - what people type or copy from a quote.</summary>
    public static bool TryParseAmount(string? raw, out decimal value)
    {
        value = 0;
        if (string.IsNullOrWhiteSpace(raw)) return false;
        var chars = new System.Text.StringBuilder(raw.Length);
        foreach (var c in raw)
            if (char.IsDigit(c) || c is '.' or '-') chars.Append(c);
        return decimal.TryParse(chars.ToString(), NumberStyles.Number, CultureInfo.InvariantCulture, out value);
    }

    // ---- Excel column paste ----

    public static readonly DependencyProperty PasteColumnProperty = DependencyProperty.RegisterAttached(
        "PasteColumn", typeof(string), typeof(MoneyBox), new PropertyMetadata(null, OnPasteColumnChanged));

    public static string? GetPasteColumn(DependencyObject d) => (string?)d.GetValue(PasteColumnProperty);
    public static void SetPasteColumn(DependencyObject d, string? value) => d.SetValue(PasteColumnProperty, value);

    private static void OnPasteColumnChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is TextBox box && e.OldValue is null) box.Paste += OnPaste;
    }

    private static async void OnPaste(object sender, TextControlPasteEventArgs e)
    {
        if (sender is not TextBox { DataContext: RfqItem item } box
            || PrListPageModel.Current is not { } vm
            || !Enum.TryParse<RfqPricingColumn>(GetPasteColumn(box), out var column)) return;

        Windows.ApplicationModel.DataTransfer.DataPackageView content;
        try
        {
            content = Windows.ApplicationModel.DataTransfer.Clipboard.GetContent();
            if (!content.Contains(Windows.ApplicationModel.DataTransfer.StandardDataFormats.Text)) return;
        }
        catch
        {
            return;   // clipboard busy - let the box paste normally
        }

        e.Handled = true;   // the page model writes the values; the boxes show them through their bindings
        try
        {
            var text = await content.GetTextAsync();
            if (string.IsNullOrEmpty(text)) return;
            if (vm.IsBatchRfqModalVisible) vm.HandleBatchRfqPricingPaste(item, text, column);
            else vm.HandleRfqPricingPaste(item, text, column);
        }
        catch (Exception ex)
        {
            CrashLog.Write("RFQ price paste failed", ex);
        }
    }
}

/// <summary>Puts the cursor in a dialog's first box when it opens, as the MAUI dialogs did (so typing can
/// start straight away). Queued behind the open so the dialog's own layout and focus handling finish first.</summary>
public static class FocusOnOpen
{
    public static void Apply(Control first) =>
        first.DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low,
            () => first.Focus(FocusState.Programmatic));
}
