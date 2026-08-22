using System;
using System.Linq;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Maui.Controls;
using Procure.Data;
using Procure.Models;
using Procure.Services;

namespace Procure.PageModels
{
    public partial class DashboardPageModel : ObservableObject
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
        }

        [RelayCommand]
        public async Task LoadDataAsync()
        {
            if (IsBusy) return;

            try
            {
                IsBusy = true;
                await _seedDataService.EnsureDataSeededAsync();

                // Copy onto the bound instance instead of replacing it: assigning a new Metrics
                // object made the BindableLayout tear down and rebuild every widget row and re-bind
                // all five metric cards on every tab switch, changed or not.
                var fresh = await _metricsService.GetMetricsAsync();
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
