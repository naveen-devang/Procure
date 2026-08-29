using System;
using System.Collections.ObjectModel;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Maui.Controls;
using Procure.Data.Repositories;
using Procure.Models;
using Procure.Services;

namespace Procure.PageModels
{
    public partial class ManageColumnsPageModel : ObservableObject
    {
        private readonly ICustomColumnRepository _columnRepo;
        private readonly IErrorHandler _errorHandler;

        [ObservableProperty]
        public partial ObservableCollection<CustomColumnDefinition> Columns { get; set; } = new();

        [ObservableProperty]
        public partial string NewColumnName { get; set; } = string.Empty;

        [ObservableProperty]
        public partial string NewColumnDataType { get; set; } = CustomFieldDataType.Text;

        [ObservableProperty]
        public partial string NewColumnOptions { get; set; } = string.Empty;

        [ObservableProperty]
        public partial bool IsBusy { get; set; }

        [ObservableProperty]
        public partial bool ShowOptionsField { get; set; }

        public string[] AvailableDataTypes => CustomFieldDataType.All;

        /// <summary>The one instance DI ever constructs (AddSingleton). The Custom Columns section now
        /// lives inside the Settings page, realised in a LazyExpander template whose BindingContext is
        /// this model; the per-row delete button reaches its command through this static, the same
        /// pattern SettingsPageModel.Current and PrListPageModel.Current use.</summary>
        public static ManageColumnsPageModel? Current { get; private set; }

        public ManageColumnsPageModel(
            ICustomColumnRepository columnRepo,
            IErrorHandler errorHandler)
        {
            _columnRepo = columnRepo;
            _errorHandler = errorHandler;
            Current = this;
        }

        partial void OnNewColumnDataTypeChanged(string value)
        {
            ShowOptionsField = value == CustomFieldDataType.Select;
        }

        [RelayCommand]
        public async Task LoadColumnsAsync()
        {
            if (IsBusy) return;

            try
            {
                IsBusy = true;
                var list = await _columnRepo.GetAllDefinitionsAsync();
                Columns = new ObservableCollection<CustomColumnDefinition>(list);
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
        public async Task AddColumnAsync()
        {
            if (string.IsNullOrWhiteSpace(NewColumnName))
            {
                if (Shell.Current != null)
                    await Shell.Current.DisplayAlertAsync("Validation", "Please enter a column name.", "OK");
                return;
            }

            try
            {
                var col = new CustomColumnDefinition
                {
                    Id = Guid.NewGuid(),
                    Name = NewColumnName.Trim(),
                    DataType = NewColumnDataType,
                    SelectOptions = NewColumnDataType == CustomFieldDataType.Select ? NewColumnOptions.Trim() : null,
                    SortOrder = Columns.Count + 1
                };

                await _columnRepo.SaveDefinitionAsync(col);
                Columns.Add(col);

                NewColumnName = string.Empty;
                NewColumnDataType = CustomFieldDataType.Text;
                NewColumnOptions = string.Empty;
                ShowOptionsField = false;
                // No success dialog: the new row appearing in the list is the confirmation.
            }
            catch (Exception ex)
            {
                _errorHandler.HandleError(ex);
            }
        }

        [RelayCommand]
        public async Task DeleteColumnAsync(CustomColumnDefinition col)
        {
            if (Shell.Current == null) return;

            var confirm = await Shell.Current.DisplayAlertAsync(
                "Delete Column",
                $"Are you sure you want to delete column '{col.Name}'? This will also remove any values saved under this column across all PRs.",
                "Delete",
                "Cancel");

            if (!confirm) return;

            try
            {
                await _columnRepo.DeleteDefinitionAsync(col.Id);
                Columns.Remove(col);
            }
            catch (Exception ex)
            {
                _errorHandler.HandleError(ex);
            }
        }
    }
}
