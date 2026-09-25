using System;
using System.Globalization;

namespace Procure.Models
{
    /// <summary>One entry in the Vendor box's drop-down: a vendor used before, with the terms of its
    /// most recent RFQ. Read from VendorAggregate.</summary>
    public sealed class VendorSuggestion
    {
        public string Name { get; init; } = string.Empty;
        public string Currency { get; init; } = string.Empty;
        public string PaymentTerms { get; init; } = string.Empty;
        public string Incoterms { get; init; } = string.Empty;
        public string VatType { get; init; } = string.Empty;
        public DateTime? LastUsed { get; init; }

        /// <summary>The grey line under the name: "AED · 30 Days Net · last used 3 Sep 2026".</summary>
        public string Detail
        {
            get
            {
                var parts = new System.Collections.Generic.List<string>(3);
                if (Currency.Length > 0) parts.Add(Currency);
                if (PaymentTerms.Length > 0) parts.Add(PaymentTerms);
                if (LastUsed is { } d) parts.Add("last used " + d.ToString("d MMM yyyy", CultureInfo.CurrentCulture));
                return string.Join("  ·  ", parts);
            }
        }

        // The AutoSuggestBox writes this into its text box when a suggestion is chosen.
        public override string ToString() => Name;
    }
}
