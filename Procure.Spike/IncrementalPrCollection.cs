using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml.Data;
using Procure.Data.Repositories;
using Procure.Models;
using Windows.Foundation;

namespace Procure.Spike;

/// <summary>
/// The WinUI equivalent of the MAUI board's RemainingItemsThreshold + LoadMoreCommand:
/// ListView calls LoadMoreItemsAsync as the user nears the end, we pull the next page
/// from the real repository. Same query shape (PrQuery) the MAUI board uses.
/// </summary>
public sealed class IncrementalPrCollection : ObservableCollection<PurchaseRequisition>, ISupportIncrementalLoading
{
    private const int PageSize = 50;

    private readonly IPurchaseRequisitionRepository _repo;
    private readonly string? _search;
    private readonly DispatcherQueue _dispatcher;
    private int _loaded;
    private int _total = int.MaxValue;
    private bool _busy;

    public IncrementalPrCollection(IPurchaseRequisitionRepository repo, string? search, DispatcherQueue dispatcher)
    {
        _repo = repo;
        _search = string.IsNullOrWhiteSpace(search) ? null : search.Trim();
        _dispatcher = dispatcher;
    }

    public bool HasMoreItems => _loaded < _total;

    public IAsyncOperation<LoadMoreItemsResult> LoadMoreItemsAsync(uint count)
        => LoadPageAsync().AsAsyncOperation();

    private async Task<LoadMoreItemsResult> LoadPageAsync()
    {
        if (_busy || !HasMoreItems) return new LoadMoreItemsResult { Count = 0 };
        _busy = true;
        try
        {
            var query = new PrQuery(
                Search: _search,
                Status: null,
                OverdueOnly: false,
                PcrPendingOnly: false,
                UrgentOnly: false,
                NormalOverdueDays: 10,
                UrgentOverdueDays: 5,
                Skip: _loaded,
                Take: PageSize);

            var page = await _repo.GetPageAsync(query).ConfigureAwait(false);
            _total = page.TotalCount;

            var tcs = new TaskCompletionSource();
            _dispatcher.TryEnqueue(() =>
            {
                foreach (var row in page.Rows) Add(row);
                tcs.SetResult();
            });
            await tcs.Task.ConfigureAwait(false);

            _loaded += page.Rows.Count;
            if (page.Rows.Count == 0) _total = _loaded;
            return new LoadMoreItemsResult { Count = (uint)page.Rows.Count };
        }
        finally
        {
            _busy = false;
        }
    }
}
