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
        private readonly SeedDataService _seedDataService;
        private readonly ISettingsService _settingsService;
        private readonly IErrorHandler _errorHandler;

        [ObservableProperty]
        public partial DashboardMetrics Metrics { get; set; } = new();

        [ObservableProperty]
        public partial bool IsBusy { get; set; }

        public DashboardPageModel(
            IDashboardMetricsService metricsService,
            SeedDataService seedDataService,
            ISettingsService settingsService,
            IErrorHandler errorHandler)
        {
            _metricsService = metricsService;
            _seedDataService = seedDataService;
            _settingsService = settingsService;
            _errorHandler = errorHandler;

            _settingsService.SettingsChanged += OnSettingsChanged;
            if (Application.Current != null)
            {
                Application.Current.RequestedThemeChanged += OnAppRequestedThemeChanged;
            }
        }

        // Registered as a DI singleton, so the container disposes it at shutdown - matches
        // PrListPageModel's identical pattern for the same two subscriptions.
        public void Dispose()
        {
            _settingsService.SettingsChanged -= OnSettingsChanged;
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

        private void OnAppRequestedThemeChanged(object? sender, AppThemeChangedEventArgs e) => RefreshCardVisuals();

        private void RefreshCardVisuals()
        {
            MainThread.BeginInvokeOnMainThread(() =>
            {
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

                // Off the UI thread: Microsoft.Data.Sqlite's "async" calls are not true async I/O for
                // SQLite (the C API is synchronous), so without Task.Run this runs the seed check and
                // the dashboard aggregate queries directly on the UI thread. Invisible on a small
                // database; measured at ~600ms combined against the 20,000-row capacity test database
                // - and DashboardPage.OnAppearing re-runs this on every visit, so it blocked every
                // return to the Dashboard, not just the first. Same Task.Run wrap every repository
                // call elsewhere in the app already uses (see PrListPageModel.LoadCoreAsync).
                await Task.Run(() => _seedDataService.EnsureDataSeededAsync());

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
