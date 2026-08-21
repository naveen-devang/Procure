using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Maui.ApplicationModel;
using Microsoft.Maui.Controls;
using Procure.Data.Repositories;
using Procure.Models;
using Procure.Services;
using Procure.Services.Export;
using Procure.Utilities;

namespace Procure.PageModels
{
    // Add / edit / delete of a purchase requisition, and its line-item editor.
    public partial class PrListPageModel
    {
        // Modal Form State (Add / Edit PR)
        [ObservableProperty]
        public partial bool IsEditModalVisible { get; set; }

        [ObservableProperty]
        public partial string EditModalTitle { get; set; } = "Edit Requisition";

        [ObservableProperty]
        public partial PurchaseRequisition? CurrentEditingPr { get; set; }

        [ObservableProperty]
        public partial ObservableCollection<CustomFieldValue> EditingCustomValues { get; set; } = new();

        [ObservableProperty]
        public partial ObservableCollection<PrItem> EditingPrItems { get; set; } = new();


        // ================= PR CRUD =================

        [RelayCommand]
        public void AddEditingPrItem()
        {
            EditingPrItems.Add(new PrItem
            {
                Id = Guid.NewGuid(),
                PrId = CurrentEditingPr?.Id ?? Guid.NewGuid(),
                ItemName = string.Empty,
                Quantity = 1,
                Unit = "pcs",
                SortOrder = EditingPrItems.Count
            });
        }

        [RelayCommand]
        public void RemoveEditingPrItem(PrItem item)
        {
            if (EditingPrItems.Contains(item))
            {
                EditingPrItems.Remove(item);
            }
        }

        [RelayCommand]
        public async Task PasteEditingPrItemsFromClipboardAsync()
        {
            try
            {
                if (!Clipboard.Default.HasText)
                {
                    if (Shell.Current != null)
                        await Shell.Current.DisplayAlertAsync("Clipboard Empty", "No text found on clipboard. Please copy lines from Excel first.", "OK");
                    return;
                }

                var text = await Clipboard.Default.GetTextAsync();
                if (string.IsNullOrWhiteSpace(text))
                {
                    if (Shell.Current != null)
                        await Shell.Current.DisplayAlertAsync("Clipboard Empty", "Clipboard text is empty.", "OK");
                    return;
                }

                var prId = CurrentEditingPr?.Id ?? Guid.NewGuid();
                var parsedItems = ClipboardItemParser.ParsePrItems(text, prId, EditingPrItems.Count);

                if (parsedItems.Count == 0)
                {
                    if (Shell.Current != null)
                        await Shell.Current.DisplayAlertAsync("No Items Detected", "Could not detect valid items from clipboard text.", "OK");
                    return;
                }

                // If only 1 placeholder blank row exists, replace it
                if (EditingPrItems.Count == 1 && string.IsNullOrWhiteSpace(EditingPrItems[0].ItemName))
                {
                    EditingPrItems.Clear();
                }

                foreach (var item in parsedItems)
                {
                    EditingPrItems.Add(item);
                }

                if (CurrentEditingPr != null && string.IsNullOrWhiteSpace(CurrentEditingPr.Description) && EditingPrItems.Count > 0)
                {
                    CurrentEditingPr.Description = EditingPrItems[0].ItemName;
                }
            }
            catch (Exception ex)
            {
                _errorHandler.HandleError(ex);
            }
        }

        [RelayCommand]
        public async Task OpenEditPrModalAsync(PurchaseRequisition pr)
        {
            var defs = await _customColumnRepo.GetAllDefinitionsAsync();
            CustomColumnDefinitions = new ObservableCollection<CustomColumnDefinition>(defs);

            EditModalTitle = $"Edit Requisition {pr.PrNo}";
            CurrentEditingPr = pr;

            // Prepare custom fields with existing values
            var vals = new List<CustomFieldValue>();
            foreach (var col in CustomColumnDefinitions)
            {
                var existing = pr.CustomValues.FirstOrDefault(v => v.ColumnId == col.Id);
                vals.Add(new CustomFieldValue
                {
                    Id = existing?.Id ?? Guid.NewGuid(),
                    PrId = pr.Id,
                    ColumnId = col.Id,
                    ColumnName = col.Name,
                    ColumnDataType = col.DataType,
                    SelectOptions = col.SelectOptions,
                    Value = existing?.Value ?? string.Empty
                });
            }
            EditingCustomValues = new ObservableCollection<CustomFieldValue>(vals);

            // Prepare line items
            if (pr.Items != null && pr.Items.Count > 0)
            {
                var copied = pr.Items.Select(i => new PrItem
                {
                    Id = i.Id,
                    PrId = i.PrId,
                    ItemName = i.ItemName,
                    Quantity = i.Quantity,
                    Unit = string.IsNullOrWhiteSpace(i.Unit) ? "pcs" : i.Unit,
                    EstimatedUnitPrice = i.EstimatedUnitPrice,
                    Notes = i.Notes,
                    SortOrder = i.SortOrder
                }).ToList();
                EditingPrItems = new ObservableCollection<PrItem>(copied);
            }
            else
            {
                EditingPrItems = new ObservableCollection<PrItem>
                {
                    new PrItem
                    {
                        Id = Guid.NewGuid(),
                        PrId = pr.Id,
                        ItemName = string.IsNullOrWhiteSpace(pr.Description) ? string.Empty : pr.Description,
                        Quantity = 1,
                        Unit = "pcs",
                        SortOrder = 0
                    }
                };
            }

            IsEditModalVisible = true;
        }

        [RelayCommand]
        public async Task SavePrModalAsync()
        {
            if (CurrentEditingPr == null) return;

            if (string.IsNullOrWhiteSpace(CurrentEditingPr.PrNo))
            {
                if (Shell.Current != null)
                    await Shell.Current.DisplayAlertAsync("Validation", "PR Number is required.", "OK");
                return;
            }

            if (string.IsNullOrWhiteSpace(CurrentEditingPr.PrType))
            {
                if (Shell.Current != null)
                    await Shell.Current.DisplayAlertAsync("Validation", "PR Type is required. Please select a PR Type.", "OK");
                return;
            }

            var validItems = EditingPrItems.Where(i => !string.IsNullOrWhiteSpace(i.ItemName)).ToList();
            CurrentEditingPr.Items = new ObservableCollection<PrItem>(validItems);

            if (string.IsNullOrWhiteSpace(CurrentEditingPr.Description) && validItems.Count > 0)
            {
                CurrentEditingPr.Description = CurrentEditingPr.ItemsSummary;
            }

            if (string.IsNullOrWhiteSpace(CurrentEditingPr.Description))
            {
                if (Shell.Current != null)
                    await Shell.Current.DisplayAlertAsync("Validation", "Description or at least one Line Item is required.", "OK");
                return;
            }

            try
            {
                CurrentEditingPr.CustomValues = new ObservableCollection<CustomFieldValue>(EditingCustomValues);
                await _prRepo.SaveAsync(CurrentEditingPr);
                CurrentEditingPr.NotifyHierarchyChanged();
                ApplyFilters();
                UpdateStatusBanner();

                IsEditModalVisible = false;
            }
            catch (Exception ex)
            {
                _errorHandler.HandleError(ex);
            }
        }

        [RelayCommand]
        public void CloseEditModal()
        {
            IsEditModalVisible = false;
        }

        [RelayCommand]
        public async Task DeletePrAsync(PurchaseRequisition pr)
        {
            if (Shell.Current == null) return;

            var confirm = await Shell.Current.DisplayAlertAsync(
                "Delete PR",
                $"Are you sure you want to delete {pr.PrNo} ({pr.Description}) and all associated RFQs, PCRs, and POs?",
                "Delete",
                "Cancel");

            if (!confirm) return;

            try
            {
                await _prRepo.DeleteAsync(pr.Id);
                pr.PropertyChanged -= OnPrItemPropertyChanged;
                _allPrs.Remove(pr);
                ApplyFilters();
                UpdateStatusBanner();
            }
            catch (Exception ex)
            {
                _errorHandler.HandleError(ex);
            }
        }

    }
}
