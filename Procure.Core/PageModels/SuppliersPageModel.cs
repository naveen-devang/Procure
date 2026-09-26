using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Procure.Abstractions;
using Procure.Data.Repositories;
using Procure.Models;
using Procure.Services;
using Procure.Utilities;

namespace Procure.PageModels
{
    // The Suppliers & Items tab. Left: every supplier, or every item. Right: one supplier - how their
    // prices compare with the others in the same round and with the market at the time - or one item,
    // with its market line, spikes, and who to ask next time.
    //
    // Nothing is read all at once: the lists come a page at a time as they scroll, a supplier's items
    // table 25 rows at a time, and an item row's chart only when it is opened (and let go when it is
    // closed again).
    public partial class SuppliersPageModel : ObservableObject
    {
        private const int ListPageSize = 60;
        private const int ItemRowPageSize = 25;

        private readonly ISupplierRepository _repo;
        private readonly ISettingsService _settings;
        private readonly IErrorHandler _errorHandler;
        private readonly IUiDispatcher _dispatcher;

        public SuppliersPageModel(ISupplierRepository repo, ISettingsService settings, IErrorHandler errorHandler, IUiDispatcher dispatcher)
        {
            _repo = repo;
            _settings = settings;
            _errorHandler = errorHandler;
            _dispatcher = dispatcher;

            // A DI singleton for the life of the app. RFQ and PO writes change what this page shows;
            // it reloads when next shown rather than on every write while it is hidden.
            DataChangeNotifier.Changed += change =>
            {
                _stale = true;
                if (IsVisible) _dispatcher.Post(() => _ = LoadAsync());
            };
        }

        public bool IsVisible { get; set; }
        private bool _stale = true;

        /// <summary>Called whenever the page is shown. Re-reads only when something changed.</summary>
        public async Task LoadAsync()
        {
            if (!_stale) return;
            _stale = false;
            await ReloadListAsync();
            await ReloadDetailAsync();
        }

        private Task ReloadDetailAsync() =>
            HasSupplier && Selected is { } s ? SelectSupplierKeyAsync(s.Key)
            : HasItem && SelectedItem is { } i ? SelectItemKeyAsync(i.Key)
            : Task.CompletedTask;

        // ---- period and currency ----------------------------------------------------------------

        public string[] PeriodOptions { get; } = { "6 months", "12 months", "All" };

        [ObservableProperty]
        public partial string Period { get; set; } = "12 months";

        partial void OnPeriodChanged(string value) => _ = ReloadDetailAsync();

        private string Local => string.IsNullOrWhiteSpace(_settings.LocalCurrency) ? "AED" : _settings.LocalCurrency;

        /// <summary>Prices compare in the local currency, at the rates set in Settings for PCR exports.</summary>
        private IReadOnlyDictionary<string, decimal> RatesToLocal()
        {
            var rates = new Dictionary<string, decimal>(_settings.CurrencyRates, StringComparer.OrdinalIgnoreCase);
            rates[Local] = 1m;
            return rates;
        }

        private PriceContext Context()
        {
            var now = MonthIndex.Of(DateTime.Today);
            int? since = Period switch { "6 months" => now - 5, "All" => null, _ => now - 11 };
            return new PriceContext(since, now, Local, RatesToLocal());
        }

        public string CurrencyHint => $"Prices are compared in {Local}, at the exchange rates in Settings. " +
                                      "A price in a currency with no rate there is left out.";

        // ---- the lists --------------------------------------------------------------------------

        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(IsSuppliersMode), nameof(SearchPlaceholder))]
        public partial bool IsItemsMode { get; set; }

        public bool IsSuppliersMode => !IsItemsMode;
        public string SearchPlaceholder => IsItemsMode ? "Search items" : "Search suppliers";

        partial void OnIsItemsModeChanged(bool value)
        {
            _searchText = string.Empty;
            OnPropertyChanged(nameof(SearchText));
            _ = ReloadListAsync();
        }

        public ObservableCollection<SupplierListItem> Suppliers { get; } = new();
        public ObservableCollection<ItemListItem> Items { get; } = new();

        public string[] SortOptions { get; } = { "Most recent", "Name", "Spend" };

        [ObservableProperty]
        public partial string SortMode { get; set; } = "Most recent";

        partial void OnSortModeChanged(string value) => _ = ReloadListAsync();

        /// <summary>"" (all), "Preferred" or "Avoid".</summary>
        [ObservableProperty]
        public partial string TagFilter { get; set; } = SupplierTag.None;

        partial void OnTagFilterChanged(string value) => _ = ReloadListAsync();

        private string _searchText = string.Empty;
        private int _searchGeneration;

        public string SearchText
        {
            get => _searchText;
            set
            {
                if (!SetProperty(ref _searchText, value ?? string.Empty)) return;
                var generation = ++_searchGeneration;
                _dispatcher.PostDelayed(TimeSpan.FromMilliseconds(250), () =>
                {
                    if (generation == _searchGeneration) _ = ReloadListAsync();
                });
            }
        }

        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(SuppliersTabText))]
        public partial int SupplierTotal { get; set; }

        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(ItemsTabText))]
        public partial int ItemTotal { get; set; }

        public string SuppliersTabText => $"Suppliers · {SupplierTotal:N0}";
        public string ItemsTabText => $"Items · {ItemTotal:N0}";

        [ObservableProperty]
        public partial bool IsListEmpty { get; set; }

        private int _listGeneration;
        private bool _loadingMore;
        private bool _hasMore;

        private SupplierSort Sort => SortMode switch
        {
            "Name" => SupplierSort.Name,
            "Spend" => SupplierSort.Spend,
            _ => SupplierSort.Recent,
        };

        private async Task ReloadListAsync()
        {
            var generation = ++_listGeneration;
            try
            {
                if (IsItemsMode)
                {
                    var (rows, total) = await _repo.GetItemPageAsync(SearchText, 0, ListPageSize);
                    if (generation != _listGeneration) return;
                    Items.Clear();
                    AddItems(rows);
                    ItemTotal = total;
                    _hasMore = rows.Count == ListPageSize;
                    IsListEmpty = total == 0;
                }
                else
                {
                    var (rows, total) = await _repo.GetPageAsync(SearchText, Sort, TagFilter, RatesToLocal(), 0, ListPageSize);
                    if (generation != _listGeneration) return;
                    Suppliers.Clear();
                    AddSuppliers(rows);
                    SupplierTotal = total;
                    _hasMore = rows.Count == ListPageSize;
                    IsListEmpty = total == 0;
                    // The other tab's count, so both labels are right from the first look.
                    if (ItemTotal == 0) ItemTotal = (await _repo.GetItemPageAsync(string.Empty, 0, 0)).Total;
                }
            }
            catch (Exception ex)
            {
                _errorHandler.HandleError(ex);
            }
        }

        /// <summary>The next page, when the list is scrolled near its end.</summary>
        public async Task LoadMoreAsync()
        {
            if (_loadingMore || !_hasMore) return;
            _loadingMore = true;
            var generation = _listGeneration;
            try
            {
                if (IsItemsMode)
                {
                    var (rows, _) = await _repo.GetItemPageAsync(SearchText, Items.Count, ListPageSize);
                    if (generation != _listGeneration) return;
                    AddItems(rows);
                    _hasMore = rows.Count == ListPageSize;
                }
                else
                {
                    var (rows, _) = await _repo.GetPageAsync(SearchText, Sort, TagFilter, RatesToLocal(), Suppliers.Count, ListPageSize);
                    if (generation != _listGeneration) return;
                    AddSuppliers(rows);
                    _hasMore = rows.Count == ListPageSize;
                }
            }
            catch (Exception ex)
            {
                _errorHandler.HandleError(ex);
            }
            finally
            {
                _loadingMore = false;
            }
        }

        private void AddSuppliers(List<SupplierListItem> rows)
        {
            foreach (var row in rows)
            {
                row.IsSelected = HasSupplier && row.Key == Selected?.Key;
                Suppliers.Add(row);
            }
        }

        private void AddItems(List<ItemListItem> rows)
        {
            foreach (var row in rows)
            {
                row.IsSelected = HasItem && row.Key == SelectedItem?.Key;
                Items.Add(row);
            }
        }

        // ---- the right-hand side: nothing, a supplier, or an item --------------------------------

        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(HasSupplier), nameof(HasItem), nameof(HasNothing))]
        public partial string DetailKind { get; set; } = string.Empty;

        public bool HasSupplier => DetailKind == "supplier";
        public bool HasItem => DetailKind == "item";
        public bool HasNothing => DetailKind.Length == 0;

        [ObservableProperty]
        public partial bool IsDetailLoading { get; set; }

        private int _detailGeneration;

        // ---- one supplier -----------------------------------------------------------------------

        [ObservableProperty]
        public partial SupplierSummary? Selected { get; set; }

        public ObservableCollection<VendorItemRow> VendorItems { get; } = new();

        private List<string> _vendorItemKeys = new();

        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(VendorItemsEmpty))]
        public partial int VendorItemCount { get; set; }

        public bool VendorItemsEmpty => VendorItemCount == 0;

        [RelayCommand]
        public Task SelectSupplierAsync(SupplierListItem? item) => item is null ? Task.CompletedTask : SelectSupplierKeyAsync(item.Key);

        private async Task SelectSupplierKeyAsync(string key)
        {
            var generation = ++_detailGeneration;
            foreach (var row in Suppliers) row.IsSelected = row.Key == key;
            IsDetailLoading = true;
            try
            {
                var ctx = Context();
                var summaryTask = _repo.GetSummaryAsync(key, ctx);
                var keysTask = _repo.GetVendorItemKeysAsync(key, ctx);
                await Task.WhenAll(summaryTask, keysTask);
                if (generation != _detailGeneration) return;   // another supplier was clicked meanwhile

                var keys = keysTask.Result;
                var firstRows = await _repo.GetVendorItemRowsAsync(keys.Take(ItemRowPageSize).ToList());
                if (generation != _detailGeneration) return;

                Selected = summaryTask.Result;
                VendorItems.Clear();
                if (Selected is null)
                {
                    DetailKind = string.Empty;   // no RFQ or PO names this supplier any more
                    return;
                }
                _vendorItemKeys = keys;
                foreach (var row in firstRows) VendorItems.Add(row);
                VendorItemCount = keys.Count;
                DetailKind = "supplier";
                LoadContactEditor(Selected);
            }
            catch (Exception ex)
            {
                _errorHandler.HandleError(ex);
            }
            finally
            {
                if (generation == _detailGeneration) IsDetailLoading = false;
            }
        }

        private bool _loadingVendorItems;

        /// <summary>The next 25 rows of the supplier's items table, as it scrolls.</summary>
        public async Task LoadMoreVendorItemsAsync()
        {
            if (_loadingVendorItems || Selected is not { } vendor || VendorItems.Count >= _vendorItemKeys.Count) return;
            _loadingVendorItems = true;
            var generation = _detailGeneration;
            try
            {
                var keys = _vendorItemKeys.Skip(VendorItems.Count).Take(ItemRowPageSize).ToList();
                var rows = await _repo.GetVendorItemRowsAsync(keys);
                if (generation != _detailGeneration) return;
                foreach (var row in rows) VendorItems.Add(row);
            }
            catch (Exception ex) { _errorHandler.HandleError(ex); }
            finally { _loadingVendorItems = false; }
        }

        /// <summary>Opens or closes a row. Its chart is read on opening and let go on closing, so a
        /// long table holds charts only for the rows that are open.</summary>
        public async Task ToggleVendorItemAsync(VendorItemRow row)
        {
            row.IsExpanded = !row.IsExpanded;
            if (!row.IsExpanded)
            {
                row.Chart = null;
                row.Others = Array.Empty<OtherQuote>();
                return;
            }
            if (Selected is not { } vendor) return;
            try
            {
                var (chart, others) = await _repo.GetVendorItemDetailAsync(vendor.Key, row.Key, Context());
                if (!row.IsExpanded || Selected?.Key != vendor.Key) return;
                row.Chart = chart;
                row.Others = others;
            }
            catch (Exception ex) { _errorHandler.HandleError(ex); }
        }

        /// <summary>"Open the item page" from a supplier's row.</summary>
        public Task OpenItemAsync(string itemKey)
        {
            if (!IsItemsMode) IsItemsMode = true;
            return SelectItemKeyAsync(itemKey);
        }

        // Contact details and the tag are edited in a flyout and saved together.
        [ObservableProperty] public partial string EditEmail { get; set; } = string.Empty;
        [ObservableProperty] public partial string EditPerson { get; set; } = string.Empty;
        [ObservableProperty] public partial string EditPhone { get; set; } = string.Empty;
        [ObservableProperty] public partial string EditNotes { get; set; } = string.Empty;

        /// <summary>What they sell, as edited in the flyout. Saved only once it has been changed, so an
        /// untouched supplier keeps following the guess from its item names.</summary>
        public ObservableCollection<string> EditCategories { get; } = new();
        private bool _categoriesEdited;

        private void LoadContactEditor(SupplierSummary s)
        {
            EditEmail = s.Email;
            EditPerson = s.Person;
            EditPhone = s.Phone;
            EditNotes = s.Notes;
            EditCategories.Clear();
            foreach (var c in s.Categories) EditCategories.Add(c);
            _categoriesEdited = false;
        }

        public void AddCategory(string? text)
        {
            var category = (text ?? string.Empty).Trim();
            if (category.Length == 0 || EditCategories.Any(c => string.Equals(c, category, StringComparison.OrdinalIgnoreCase))) return;
            EditCategories.Add(category);
            _categoriesEdited = true;
        }

        public void RemoveCategory(string category)
        {
            if (EditCategories.Remove(category)) _categoriesEdited = true;
        }

        [RelayCommand]
        public Task SaveContactAsync() => SaveSupplierAsync(Selected?.Tag ?? SupplierTag.None);

        /// <summary>Preferred / Avoid / neither. Kept with the contact details.</summary>
        public Task SetTagAsync(string tag) => SaveSupplierAsync(tag);

        private async Task SaveSupplierAsync(string tag)
        {
            if (Selected is not { } vendor) return;
            try
            {
                await _repo.SaveContactAsync(vendor.Key, EditEmail, EditPerson, EditPhone, EditNotes, tag,
                    _categoriesEdited ? EditCategories.ToList() : null);
                await SelectSupplierKeyAsync(vendor.Key);
                // The list shows tags, and a tag filter may now include or drop this supplier.
                await ReloadListAsync();
            }
            catch (Exception ex) { _errorHandler.HandleError(ex); }
        }

        // ---- one item ---------------------------------------------------------------------------

        [ObservableProperty]
        public partial ItemDetail? SelectedItem { get; set; }

        [RelayCommand]
        public Task SelectItemAsync(ItemListItem? item) => item is null ? Task.CompletedTask : SelectItemKeyAsync(item.Key);

        private async Task SelectItemKeyAsync(string key)
        {
            var generation = ++_detailGeneration;
            foreach (var row in Items) row.IsSelected = row.Key == key;
            IsDetailLoading = true;
            try
            {
                var detail = await _repo.GetItemDetailAsync(key, Context());
                if (generation != _detailGeneration) return;
                SelectedItem = detail;
                DetailKind = detail is null ? string.Empty : "item";
                // The supplier view's rows let go of their charts while the item is shown.
                if (detail is not null) CollapseVendorRows();
            }
            catch (Exception ex)
            {
                _errorHandler.HandleError(ex);
            }
            finally
            {
                if (generation == _detailGeneration) IsDetailLoading = false;
            }
        }

        private void CollapseVendorRows()
        {
            foreach (var row in VendorItems.Where(r => r.IsExpanded))
            {
                row.IsExpanded = false;
                row.Chart = null;
                row.Others = Array.Empty<OtherQuote>();
            }
        }

        /// <summary>"Treat as one item": the row's lines count as its look-alike's from now on.</summary>
        public async Task TreatAsOneAsync(ItemListItem row)
        {
            if (!row.HasTwin) return;
            try
            {
                await _repo.TreatAsOneAsync(row.Key, row.TwinKey);
                await ReloadListAsync();
                if (HasItem && (SelectedItem?.Key == row.Key || SelectedItem?.Key == row.TwinKey)) await SelectItemKeyAsync(row.TwinKey);
            }
            catch (Exception ex) { _errorHandler.HandleError(ex); }
        }

        /// <summary>Undoes "Treat as one item" for one spelling.</summary>
        public async Task SeparateAsync(string aliasKey)
        {
            if (SelectedItem is not { } item) return;
            try
            {
                await _repo.SeparateAsync(aliasKey);
                await ReloadListAsync();
                await SelectItemKeyAsync(item.Key);
            }
            catch (Exception ex) { _errorHandler.HandleError(ex); }
        }

        /// <summary>The months the note picker offers: the chart's, newest first.</summary>
        public IReadOnlyList<string> NoteMonths()
        {
            if (SelectedItem is not { } item) return Array.Empty<string>();
            var chart = item.Chart;
            var from = Math.Max(chart.FromMonth, chart.ToMonth - 59);
            return Enumerable.Range(from, chart.ToMonth - from + 1).Reverse().Select(MonthIndex.Long).ToList();
        }

        public async Task AddNoteAsync(int fromIndex, int toIndex, string text)
        {
            if (SelectedItem is not { } item || string.IsNullOrWhiteSpace(text)) return;
            // Indexes into NoteMonths(), which runs newest first.
            var newest = item.Chart.ToMonth;
            try
            {
                await _repo.AddItemNoteAsync(item.Key, newest - fromIndex, newest - toIndex, text);
                await SelectItemKeyAsync(item.Key);
            }
            catch (Exception ex) { _errorHandler.HandleError(ex); }
        }

        public async Task DeleteNoteAsync(string id)
        {
            if (SelectedItem is not { } item) return;
            try
            {
                await _repo.DeleteItemNoteAsync(id);
                await SelectItemKeyAsync(item.Key);
            }
            catch (Exception ex) { _errorHandler.HandleError(ex); }
        }

        /// <summary>The top three to ask, as "Name &lt;email&gt;" lines for an email's To box.</summary>
        public string TopThreeText() =>
            SelectedItem is not { } item ? string.Empty
            : string.Join("; ", item.Suppliers.Where(s => !s.IsAvoid).Take(3)
                .Select(s => s.Email.Length > 0 ? $"{s.Name} <{s.Email}>" : s.Name));
    }
}
