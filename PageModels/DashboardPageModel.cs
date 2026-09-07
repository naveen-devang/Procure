using System;
using System.Linq;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Maui.ApplicationModel;
using Microsoft.Maui.Controls;
using Procure.Data;
using Procure.Models;
using Procure.Services;

namespace Procure.PageModels
{
    public partial class DashboardPageModel : ObservableObject, IDisposable
    {
        private readonly IDashboardMetricsService _metricsService;
        private readonly ISettingsService _settingsService;
        private readonly IErrorHandler _errorHandler;

        [ObservableProperty]
        public partial DashboardMetrics Metrics { get; set; } = new();

        [ObservableProperty]
        public partial bool IsBusy { get; set; }

        public DashboardPageModel(
            IDashboardMetricsService metricsService,
            ISettingsService settingsService,
            IErrorHandler errorHandler)
        {
            _metricsService = metricsService;
            _settingsService = settingsService;
            _errorHandler = errorHandler;

            _settingsService.SettingsChanged += OnSettingsChanged;
            Utilities.DataChangeNotifier.Changed += OnDataChanged;
            if (Application.Current != null)
            {
                Application.Current.RequestedThemeChanged += OnAppRequestedThemeChanged;
            }
        }

        /// <summary>Set by the page as it appears and disappears. The metrics query is a full sweep
        /// of the database, so a hidden Dashboard must not run it on every write - OnAppearing
        /// already reloads, which covers everything that happened while it was away.</summary>
        public bool IsVisible { get; set; }

        private int _refreshGeneration;

        private void OnDataChanged(Utilities.ProcurementChange what)
        {
            if (!IsVisible) return;

            // Debounced, for two reasons. The metrics are a full sweep of the database, and a burst of
            // writes - a merge, a split, saving a PR that syncs into three quotes - fires this several
            // times in a second; unthrottled, that was one whole-table sweep per write, which at
            // 20,000 PRs starved the rest of the app. And LoadDataAsync drops a call outright while
            // one is already running, so the LAST write of a burst was the one most likely to be
            // discarded, leaving the figures stale exactly when they had just changed.
            //
            // Same generation-counter idiom as the board's search box: a superseded pass is retired by
            // the counter rather than by cancelling anything, and the callback is already on the UI
            // thread. 400ms because these are discrete saves, not keystrokes - long enough to collapse
            // a burst, short enough to feel immediate.
            var generation = ++_refreshGeneration;
            MainThread.BeginInvokeOnMainThread(() =>
                Microsoft.Maui.Dispatching.Dispatcher.GetForCurrentThread()
                    ?.DispatchDelayed(TimeSpan.FromMilliseconds(400), () =>
                    {
                        if (generation == _refreshGeneration && IsVisible) _ = LoadDataAsync();
                    }));
        }

        /// <summary>Test seam: how many refreshes the debounce has actually let through.</summary>
        internal int RefreshGenerationForTest => _refreshGeneration;

        // Registered as a DI singleton, so the container disposes it at shutdown - matches
        // PrListPageModel's identical pattern for the same two subscriptions.
        public void Dispose()
        {
            _settingsService.SettingsChanged -= OnSettingsChanged;
            Utilities.DataChangeNotifier.Changed -= OnDataChanged;
            if (Application.Current != null)
            {
                Application.Current.RequestedThemeChanged -= OnAppRequestedThemeChanged;
            }
        }

        // The Urgent priority badge and status badges on the Needs Attention widget go through
        // converters that read ThemeHelper.IsDark directly rather than AppThemeBinding, so nothing
        // re-evaluates them when the theme changes - they stay whatever color they first resolved to.
        // NotifyHierarchyChanged explicitly raises Status and Priority, which is what forces those
        // bindings to re-run; PrListPageModel.RefreshCardVisuals does the same thing for the PR Board's
        // own cards, but the two pages hold different PurchaseRequisition instances, so each needs its
        // own hookup.
        private void OnSettingsChanged(object? sender, SettingsChangedEventArgs e)
        {
            if (e.Key is nameof(ISettingsService.AppTheme) or nameof(ISettingsService.AccentTheme))
            {
                RefreshCardVisuals();
            }
        }

        // Application.RequestedThemeChanged fires a dispatcher turn AFTER SettingsChanged, so a
        // queue-flag guard cannot fold the two together. It only carries new information when the
        // theme follows the OS ("System"); pinned to Light/Dark, the only thing that can raise it
        // is this app's own AppTheme setter, which SettingsChanged has already handled.
        private void OnAppRequestedThemeChanged(object? sender, AppThemeChangedEventArgs e)
        {
            if (_settingsService.AppTheme is "Light" or "Dark") return;
            RefreshCardVisuals();
        }

        // Test seam for ThemeTransitionSelfCheck: one switch must repaint exactly once.
        internal int CardRepaintsForTest { get; private set; }

        private void RefreshCardVisuals()
        {
            MainThread.BeginInvokeOnMainThread(() =>
            {
                CardRepaintsForTest++;
                foreach (var pr in Metrics.NeedsAttentionPrs)
                {
                    pr.NotifyHierarchyChanged();
                }
            });
        }

        [RelayCommand]
        public async Task LoadDataAsync()
        {
            if (IsBusy) return;

            try
            {
                IsBusy = true;

                // Copy onto the bound instance instead of replacing it: assigning a new Metrics
                // object made the BindableLayout tear down and rebuild every widget row and re-bind
                // all five metric cards on every tab switch, changed or not.
                var fresh = await Task.Run(() => _metricsService.GetMetricsAsync());
                Metrics.TotalPrs = fresh.TotalPrs;
                Metrics.RfqsAwaitingQuote = fresh.RfqsAwaitingQuote;
                Metrics.PcrsAwaitingSignature = fresh.PcrsAwaitingSignature;
                Metrics.PosRaised = fresh.PosRaised;
                Metrics.TotalPoValue = fresh.TotalPoValue;
                Metrics.OverdueCount = fresh.OverdueCount;
                Metrics.UrgentCount = fresh.UrgentCount;

                var target = Metrics.NeedsAttentionPrs;
                if (target.Select(p => p.Id).SequenceEqual(fresh.NeedsAttentionPrs.Select(p => p.Id)))
                {
                    // Same rows: merge field changes into the live instances, no collection events.
                    for (var i = 0; i < target.Count; i++) target[i].MergeFrom(fresh.NeedsAttentionPrs[i]);
                }
                else
                {
                    target.Clear();
                    foreach (var pr in fresh.NeedsAttentionPrs) target.Add(pr);
                }
            }
            catch (Exception ex)
            {
                // Was silent otherwise: the dashboard just stayed at its all-zero construction
                // defaults with nothing anywhere recording why - indistinguishable from "reset to
                // defaults" to anyone watching. See Data.CrashLog.
                Procure.Utilities.CrashLog.Write("DashboardPageModel.LoadDataAsync failed", ex);
                _errorHandler.HandleError(ex);
            }
            finally
            {
                IsBusy = false;
            }
        }

        [RelayCommand]
        public async Task NavigateToPrBoardAsync(string? filter = null)
        {
            try
            {
                await Shell.Current.GoToAsync("//prboard");
            }
            catch (Exception ex)
            {
                _errorHandler.HandleError(ex);
            }
        }

        [RelayCommand]
        public async Task CreateNewPrAsync()
        {
            try
            {
                await Shell.Current.GoToAsync("//prboard?action=new");
            }
            catch (Exception ex)
            {
                _errorHandler.HandleError(ex);
            }
        }
    }
}
