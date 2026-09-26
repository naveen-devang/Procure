using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading.Tasks;
using Procure.Models;
using Procure.Utilities;

namespace Procure.Data.Repositories
{
    public enum SupplierSort { Recent, Name, Spend }

    /// <summary>The period and currency a price read works in. Every price is converted to
    /// <see cref="Local"/> at the Settings rates; a price in a currency without a rate is left out,
    /// never counted as if it were local.</summary>
    public sealed class PriceContext
    {
        public PriceContext(int? sinceMonth, int thisMonth, string local, IReadOnlyDictionary<string, decimal> toLocal)
        {
            SinceMonth = sinceMonth;
            ThisMonth = thisMonth;
            Local = local;
            ToLocal = toLocal;
            SinceKey = sinceMonth is { } m ? MonthIndex.Start(m).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) : string.Empty;
            RatesKey = local + "|" + string.Join(";", toLocal.OrderBy(r => r.Key, StringComparer.OrdinalIgnoreCase).Select(r => r.Key + "=" + r.Value));
        }

        /// <summary>Null for "All".</summary>
        public int? SinceMonth { get; }
        public int ThisMonth { get; }
        public string Local { get; }
        public IReadOnlyDictionary<string, decimal> ToLocal { get; }
        /// <summary>"yyyy-MM-dd", compared against stored dates as text; empty for "All".</summary>
        public string SinceKey { get; }
        /// <summary>Identifies these rates, so a cached market is not reused after Settings change.</summary>
        public string RatesKey { get; }

        public double? Rate(string currency) => ToLocal.TryGetValue(currency, out var r) ? (double)r : null;
    }

    /// <summary>The Suppliers &amp; Items page. Vendors come from VendorAggregate / VendorSpend and
    /// items from ItemAggregate, all kept by triggers; prices are read a page, an item or a supplier
    /// at a time and never all at once.</summary>
    public interface ISupplierRepository
    {
        /// <summary>One page of vendors whose name contains <paramref name="search"/>, optionally only
        /// those tagged <paramref name="tag"/>.</summary>
        Task<(List<SupplierListItem> Rows, int Total)> GetPageAsync(string search, SupplierSort sort, string tag,
            IReadOnlyDictionary<string, decimal> toLocal, int skip, int take);

        Task<SupplierSummary?> GetSummaryAsync(string vendorKey, PriceContext ctx);
        /// <summary>Every item the vendor quoted or sold in the period, most recent first.</summary>
        Task<List<string>> GetVendorItemKeysAsync(string vendorKey, PriceContext ctx);
        /// <summary>The rows for these items: their names; the rest is read when a row is opened.</summary>
        Task<List<VendorItemRow>> GetVendorItemRowsAsync(IReadOnlyList<string> itemKeys);
        /// <summary>An opened row: the vendor's own prices against the market, and the others' latest.</summary>
        Task<(PriceChartData Chart, List<OtherQuote> Others)> GetVendorItemDetailAsync(string vendorKey, string itemKey, PriceContext ctx);
        /// <summary>Contact details and tag. <paramref name="categories"/> null keeps what is stored;
        /// a list (even an empty one) replaces the guessed categories from now on.</summary>
        Task SaveContactAsync(string vendorKey, string email, string person, string phone, string notes, string tag,
            IReadOnlyList<string>? categories);

        Task<(List<ItemListItem> Rows, int Total)> GetItemPageAsync(string search, int skip, int take);
        Task<ItemDetail?> GetItemDetailAsync(string itemKey, PriceContext ctx);
        /// <summary>From now on <paramref name="aliasKey"/>'s lines count as <paramref name="canonicalKey"/>'s.</summary>
        Task TreatAsOneAsync(string aliasKey, string canonicalKey);
        Task SeparateAsync(string aliasKey);
        Task AddItemNoteAsync(string itemKey, int fromMonth, int toMonth, string text);
        Task DeleteItemNoteAsync(string id);
    }
}
