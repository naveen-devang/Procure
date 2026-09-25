using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Procure.Models;

namespace Procure.PageModels
{
    // Vendor suggestions for the Vendor box on Add RFQ, Batch RFQ and Batch PO. Picking a vendor on
    // a new RFQ also fills its currency, payment terms, Incoterms and VAT from that vendor's last
    // RFQ - but only fields nobody has changed since the form opened. Typed values always win.
    public partial class PrListPageModel
    {
        private int _vendorSearchGeneration;

        /// <summary>Suggestions for what is typed, from the 2nd character. Debounced; null when a
        /// newer keystroke has superseded this one, so the caller keeps what it is showing.</summary>
        public async Task<IReadOnlyList<VendorSuggestion>?> FindVendorsAsync(string? text)
        {
            var generation = ++_vendorSearchGeneration;
            text = text?.Trim() ?? string.Empty;
            if (text.Length < 2) return Array.Empty<VendorSuggestion>();

            await Task.Delay(150);
            if (generation != _vendorSearchGeneration) return null;
            try
            {
                var found = await Task.Run(() => _prRepo.SearchVendorsAsync(text));
                return generation == _vendorSearchGeneration ? found : null;
            }
            catch (Exception ex)
            {
                _errorHandler.HandleError(ex);
                return null;
            }
        }

        // ---- which term fields the user has set since the form opened ----------------------------

        private readonly HashSet<string> _rfqTermsTouched = new();
        private readonly HashSet<string> _batchRfqTermsTouched = new();
        private bool _fillingVendorTerms;

        private void MarkRfqTermTouched(string field)
        {
            if (!_fillingVendorTerms) _rfqTermsTouched.Add(field);
        }

        private void MarkBatchRfqTermTouched(string field)
        {
            if (!_fillingVendorTerms) _batchRfqTermsTouched.Add(field);
        }

        partial void OnNewRfqPaymentTermsChanged(string value) => MarkRfqTermTouched(nameof(NewRfqPaymentTerms));
        partial void OnNewRfqIncotermsChanged(string value) => MarkRfqTermTouched(nameof(NewRfqIncoterms));
        partial void OnBatchRfqPaymentTermsChanged(string value) => MarkBatchRfqTermTouched(nameof(BatchRfqPaymentTerms));
        partial void OnBatchRfqIncotermsChanged(string value) => MarkBatchRfqTermTouched(nameof(BatchRfqIncoterms));

        /// <summary>Called once a form has set its own starting values, so those don't count as the
        /// user's.</summary>
        internal void ResetRfqTermsTouched() => _rfqTermsTouched.Clear();
        private void ResetBatchRfqTermsTouched() => _batchRfqTermsTouched.Clear();

        // ---- applying a chosen vendor -------------------------------------------------------------

        public void ApplyRfqVendor(VendorSuggestion vendor)
        {
            NewRfqVendor = vendor.Name;
            // Editing an existing quote keeps its own terms: they are what that vendor quoted.
            if (IsEditingRfq) return;
            FillTerms(vendor, _rfqTermsTouched,
                nameof(NewRfqCurrency), v => NewRfqCurrency = v,
                nameof(NewRfqPaymentTerms), v => NewRfqPaymentTerms = v,
                nameof(NewRfqIncoterms), v => NewRfqIncoterms = v,
                nameof(NewRfqVatType), v => NewRfqVatType = v);
        }

        public void ApplyBatchRfqVendor(VendorSuggestion vendor)
        {
            BatchRfqVendor = vendor.Name;
            FillTerms(vendor, _batchRfqTermsTouched,
                nameof(BatchRfqCurrency), v => BatchRfqCurrency = v,
                nameof(BatchRfqPaymentTerms), v => BatchRfqPaymentTerms = v,
                nameof(BatchRfqIncoterms), v => BatchRfqIncoterms = v,
                nameof(BatchRfqVatType), v => BatchRfqVatType = v);
        }

        /// <summary>Batch PO takes the name only: its currency is already worked out from the quotes
        /// being ordered, which is more exact than what this vendor used last time.</summary>
        public void ApplyBatchPoVendor(VendorSuggestion vendor) => BatchPoVendor = vendor.Name;

        private void FillTerms(VendorSuggestion vendor, HashSet<string> touched,
            string currencyField, Action<string> setCurrency,
            string termsField, Action<string> setTerms,
            string incotermsField, Action<string> setIncoterms,
            string vatField, Action<string> setVat)
        {
            _fillingVendorTerms = true;
            try
            {
                // Only values the dropdowns can show: anything else would leave a box looking empty.
                if (!touched.Contains(currencyField) && AvailableCurrencies.Contains(vendor.Currency)) setCurrency(vendor.Currency);
                if (!touched.Contains(termsField) && vendor.PaymentTerms.Length > 0) setTerms(vendor.PaymentTerms);
                if (!touched.Contains(incotermsField) && AvailableIncoterms.Contains(vendor.Incoterms)) setIncoterms(vendor.Incoterms);
                if (!touched.Contains(vatField) && AvailableVatTypes.Contains(vendor.VatType)) setVat(vendor.VatType);
            }
            finally
            {
                _fillingVendorTerms = false;
            }
        }
    }
}
