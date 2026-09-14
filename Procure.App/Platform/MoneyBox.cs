using System;
using System.Globalization;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace Procure.App.Platform;

/// <summary>
/// Money boxes in the RFQ and PO dialogs.
///
/// <c>MoneyBox.PercentOf</c> on a discount box: typing a number followed by % turns it into that share of
/// the given amount ("5%" of a 250.00 unit price becomes 12.50), there and then. Resolved once, like
/// MAUI: it does not re-scale if the price changes afterwards.
///
/// Ctrl+V into these boxes is a plain paste: taking over a paste to spread an Excel column across the
/// rows is only done by the dialogs' "Paste ... from Excel" buttons (the user's rule).
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
}

/// <summary>Puts the cursor in a dialog's first box when it opens, as the MAUI dialogs did (so typing can
/// start straight away). Queued behind the open so the dialog's own layout and focus handling finish first.</summary>
public static class FocusOnOpen
{
    public static void Apply(Control first) =>
        first.DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low,
            () => first.Focus(FocusState.Programmatic));
}
