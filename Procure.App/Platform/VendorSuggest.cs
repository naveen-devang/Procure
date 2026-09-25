using System;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Procure.Models;
using Procure.PageModels;

namespace Procure.App.Platform;

/// <summary>Wires a Vendor box (Add RFQ, Batch RFQ, Batch PO) to the vendor suggestions: typing
/// asks the page model, and a suggestion that is clicked or confirmed with Enter is applied.
/// Applied on QuerySubmitted rather than SuggestionChosen - the latter also fires while arrowing
/// through the list, which would fill the terms from every vendor passed on the way.</summary>
internal static class VendorSuggest
{
    public static void Wire(AutoSuggestBox box, Action<PrListPageModel, VendorSuggestion> apply)
    {
        box.TextChanged += async (sender, args) =>
        {
            if (args.Reason != AutoSuggestionBoxTextChangeReason.UserInput) return;
            if (sender.DataContext is not PrListPageModel vm) return;
            var found = await vm.FindVendorsAsync(sender.Text);
            if (found is null) return;   // a newer keystroke is already on its way
            sender.ItemsSource = found;
        };

        box.QuerySubmitted += (sender, args) =>
        {
            if (args.ChosenSuggestion is VendorSuggestion vendor && sender.DataContext is PrListPageModel vm)
                apply(vm, vendor);
        };

        // Closing the form leaves nothing behind for the next open to show.
        box.Unloaded += (_, _) => box.ItemsSource = null;
    }
}
