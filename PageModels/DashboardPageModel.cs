using System;
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
                Metrics = await _metricsService.GetMetricsAsync();
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
