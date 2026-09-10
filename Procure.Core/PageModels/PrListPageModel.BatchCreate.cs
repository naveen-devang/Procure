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
using Procure.Abstractions;
using Procure.Data.Repositories;
using Procure.Models;
using Procure.Services;
using Procure.Services.Export;
using Procure.Utilities;

namespace Procure.PageModels
{
    // Bulk creation of many requisitions at once from the batch-create modal.
    public partial class PrListPageModel
    {
        // Bulk / Multi-PR Creation State
        [ObservableProperty]
        public partial bool IsBatchCreateModalVisible { get; set; }

        [ObservableProperty]
        public partial string BatchSharedRequestor { get; set; } = string.Empty;

        [ObservableProperty]
        public partial string BatchSharedPlant { get; set; } = ProcurementPlant.RW01;

        [ObservableProperty]
        public partial string BatchSharedPrType { get; set; } = ProcurementPrType.StoresAndSpares;

        [ObservableProperty]
        public partial string BatchSharedPriority { get; set; } = ProcurementPriority.Normal;

        [ObservableProperty]
        public partial string BatchSharedStatus { get; set; } = ProcurementStatus.PrRaised;

        [ObservableProperty]
        public partial string BatchSharedNotes { get; set; } = string.Empty;

        [ObservableProperty]
        public partial ObservableCollection<CustomFieldValue> BatchSharedCustomValues { get; set; } = new();

        [ObservableProperty]
        public partial ObservableCollection<BatchPrEntry> BatchPrEntries { get; set; } = new();

        [ObservableProperty]
        public partial string BatchEntriesSummary { get; set; } = "3 requisitions ready";

        // Presentation only - derived from BatchPrEntries.Count so the modal reads as "Add PR"
        // for the common one-row case and switches to "Add PRs" framing (row numbers, per-row
        // duplicate/remove, pluralized copy) once a second row shows up via "+ Add Another PR"
        // or a multi-row paste. Recomputed everywhere BatchPrEntries changes - see
        // UpdateBatchEntriesSummary, the one place all of those paths already funnel through.
        [ObservableProperty]
        public partial bool IsBulkMode { get; set; }

        [ObservableProperty]
        public partial string ModalTitle { get; set; } = "Add PR";

        [ObservableProperty]
        public partial string ModalSubtitle { get; set; } = "Fill in the requisition details below.";

        [ObservableProperty]
        public partial string SaveButtonText { get; set; } = "Add PR";

        [ObservableProperty]
        public partial string RequisitionsSectionLabel { get; set; } = "Requisition Details";

        // The row currently shown in the detail pane. Null only when BatchPrEntries is empty
        // (never true while the modal is open - there's always at least one row). Selection is
        // driven by an explicit TapGestureRecognizer + command on each row, not CollectionView's
        // own SelectionMode - the native selection VisualStateManager "Selected" state is
        // unreliable on WinUI, so the row's highlight is a plain identity check instead.
        [ObservableProperty]
        public partial BatchPrEntry? SelectedBatchEntry { get; set; }

        [RelayCommand]
        public void SelectBatchPrRow(BatchPrEntry row) => SelectedBatchEntry = row;

        // ================= BULK / MULTI-PR CREATION OPERATIONS =================

        [RelayCommand]
        public async Task OpenBatchCreateModalAsync()
        {
            var defs = await _customColumnRepo.GetAllDefinitionsAsync();
            CustomColumnDefinitions = new ObservableCollection<CustomColumnDefinition>(defs);

            var mostRecentRequestor = _loadedPrs.FirstOrDefault(p => !string.IsNullOrWhiteSpace(p.Requestor))?.Requestor ?? string.Empty;
            BatchSharedRequestor = mostRecentRequestor;
            BatchSharedPlant = ProcurementPlant.RW01;
            BatchSharedPrType = ProcurementPrType.StoresAndSpares;
            BatchSharedPriority = ProcurementPriority.Normal;
            BatchSharedStatus = ProcurementStatus.PrRaised;
            BatchSharedNotes = string.Empty;

            // Prepare shared custom fields
            var sharedVals = new List<CustomFieldValue>();
            foreach (var col in CustomColumnDefinitions)
            {
                sharedVals.Add(new CustomFieldValue
                {
                    Id = Guid.NewGuid(),
                    ColumnId = col.Id,
                    ColumnName = col.Name,
                    ColumnDataType = col.DataType,
                    SelectOptions = col.SelectOptions,
                    Value = string.Empty
                });
            }
            BatchSharedCustomValues = new ObservableCollection<CustomFieldValue>(sharedVals);

            // One row by default - the common case is a single PR. "+ Add Row" or pasting multiple
            // PRs from Excel (PasteBatchPrRowsFromClipboardAsync) both still grow this as needed.
            BatchPrEntries = new ObservableCollection<BatchPrEntry>
            {
                CreateNewBatchPrEntry(BatchSharedRequestor, BatchSharedPriority, BatchSharedPlant, BatchSharedPrType)
            };
            SelectedBatchEntry = BatchPrEntries[0];
            UpdateBatchEntriesSummary();
            IsBatchCreateModalVisible = true;
        }

        private BatchPrEntry CreateNewBatchPrEntry(string requestor, string priority, string plant = ProcurementPlant.RW01, string prType = ProcurementPrType.StoresAndSpares)
        {
            var entryId = Guid.NewGuid();
            var rowVals = new ObservableCollection<CustomFieldValue>();
            foreach (var sv in BatchSharedCustomValues)
            {
                rowVals.Add(new CustomFieldValue
                {
                    Id = Guid.NewGuid(),
                    PrId = entryId,
                    ColumnId = sv.ColumnId,
                    ColumnName = sv.ColumnName,
                    ColumnDataType = sv.ColumnDataType,
                    SelectOptions = sv.SelectOptions,
                    Value = sv.Value ?? string.Empty
                });
            }

            var entry = new BatchPrEntry
            {
                Id = entryId,
                PrNo = string.Empty,
                Requestor = requestor,
                Plant = plant,
                PrType = prType,
                Priority = priority,
                Status = ProcurementStatus.PrRaised,
                Notes = BatchSharedNotes,
                CustomValues = rowVals,
                Items = new ObservableCollection<PrItem>
                {
                    new PrItem
                    {
                        Id = Guid.NewGuid(),
                        PrId = entryId,
                        ItemName = string.Empty,
                        Quantity = 1,
                        Unit = "pcs",
                        SortOrder = 0
                    }
                }
            };
            return entry;
        }

        [RelayCommand]
        public void AddBatchPrRow()
        {
            var entry = CreateNewBatchPrEntry(BatchSharedRequestor, BatchSharedPriority, BatchSharedPlant, BatchSharedPrType);
            BatchPrEntries.Add(entry);
            SelectedBatchEntry = entry;
            UpdateBatchEntriesSummary();
        }

        // The New Row Defaults bar only pre-fills new rows and blank cells on paste. This button
        // is the explicit "make every row match the defaults" action - it overwrites Plant, PR
        // Type, Priority, Requestor and Notes on ALL rows (including ones pasted from Excel), so a
        // 50-row paste can be set to one plant / type / priority in a click. Blank defaults are
        // skipped so an empty Notes box does not wipe everyone's notes. PR Number, Description and
        // line items are per-row and never touched.
        [RelayCommand]
        public async Task ApplyDefaultsToAllRowsAsync()
        {
            if (BatchPrEntries.Count == 0) return;

            {
                var confirm = await _dialogs.DisplayAlertAsync(
                    "Apply Defaults to All Rows",
                    $"Set Plant, PR Type, Priority, Requestor and Notes to the current New Row Defaults on all {BatchPrEntries.Count} row(s)? This overwrites those fields on every row.",
                    "Apply", "Cancel");
                if (!confirm) return;
            }

            foreach (var entry in BatchPrEntries)
            {
                if (!string.IsNullOrWhiteSpace(BatchSharedPlant)) entry.Plant = BatchSharedPlant;
                if (!string.IsNullOrWhiteSpace(BatchSharedPrType)) entry.PrType = BatchSharedPrType;
                if (!string.IsNullOrWhiteSpace(BatchSharedPriority)) entry.Priority = BatchSharedPriority;
                if (!string.IsNullOrWhiteSpace(BatchSharedRequestor)) entry.Requestor = BatchSharedRequestor;
                if (!string.IsNullOrWhiteSpace(BatchSharedNotes)) entry.Notes = BatchSharedNotes;
            }

            UpdateBatchEntriesSummary();
        }

        // Copies the source row's custom-field values onto every other row, but only into a
        // field that row hasn't already got a value for - it never overwrites a tag someone
        // already typed on another PR. Lives on each row's Custom Fields section, not as one
        // global "apply defaults" button, since it's this row's tags being fanned out, not a
        // separate defaults panel's.
        [RelayCommand]
        public void CopyCustomFieldsToAllRows(BatchPrEntry source)
        {
            foreach (var entry in BatchPrEntries)
            {
                if (entry == source) continue;

                foreach (var sourceVal in source.CustomValues)
                {
                    if (string.IsNullOrWhiteSpace(sourceVal.Value)) continue;

                    var target = entry.CustomValues.FirstOrDefault(v => v.ColumnId == sourceVal.ColumnId);
                    if (target != null && string.IsNullOrWhiteSpace(target.Value))
                    {
                        target.Value = sourceVal.Value;
                    }
                }
            }
        }

        [RelayCommand]
        public void AddBatchItemToPr(BatchPrEntry entry)
        {
            entry.Items.Add(new PrItem
            {
                Id = Guid.NewGuid(),
                PrId = entry.Id,
                ItemName = string.Empty,
                Quantity = 1,
                Unit = "pcs",
                SortOrder = entry.Items.Count
            });
            entry.NotifyItemsChanged();
        }

        [RelayCommand]
        public void RemoveBatchItemFromPr(PrItem item)
        {
            foreach (var entry in BatchPrEntries)
            {
                if (entry.Items.Contains(item))
                {
                    if (entry.Items.Count > 1)
                    {
                        entry.Items.Remove(item);
                        entry.NotifyItemsChanged();
                    }
                    break;
                }
            }
        }

        private void UpdateBatchEntriesSummary()
        {
            var count = BatchPrEntries.Count;
            BatchEntriesSummary = count == 1 ? "1 requisition ready to create" : $"{count} requisitions ready to create";

            // Safety net for any path that mutates BatchPrEntries without also managing selection
            // directly - falls back to the first row rather than leaving the detail pane pointed
            // at a row that's no longer in the list.
            if (count > 0 && (SelectedBatchEntry == null || !BatchPrEntries.Contains(SelectedBatchEntry)))
            {
                SelectedBatchEntry = BatchPrEntries[0];
            }

            for (int i = 0; i < BatchPrEntries.Count; i++)
            {
                BatchPrEntries[i].DisplayIndex = i + 1;
            }

            IsBulkMode = count > 1;
            if (IsBulkMode)
            {
                ModalTitle = "Add PRs";
                ModalSubtitle = "Add multiple requisitions at once with shared tags, requestor, and customizable line items.";
                SaveButtonText = $"Add {count} PRs";
                RequisitionsSectionLabel = "Requisitions";
            }
            else
            {
                ModalTitle = "Add PR";
                ModalSubtitle = "Fill in the requisition details below.";
                SaveButtonText = "Add PR";
                RequisitionsSectionLabel = "Requisition Details";
            }
        }

        [RelayCommand]
        public async Task SaveBatchPrsModalAsync()
        {
            if (BatchPrEntries.Count == 0) return;

            // Validate PR entries. Failures accumulate so one submit reports every bad row at once
            // instead of one round trip through a dialog per row.
            var validEntries = new List<PurchaseRequisition>();
            var errors = new List<string>();
            int index = 1;

            foreach (var entry in BatchPrEntries)
            {
                if (string.IsNullOrWhiteSpace(entry.PrNo))
                {
                    errors.Add($"Row {index}: PR Number is required.");
                    index++;
                    continue;
                }

                // Filter valid line items
                var validItems = entry.Items.Where(i => !string.IsNullOrWhiteSpace(i.ItemName)).ToList();

                // Description is optional. When it's blank but the row has line items, fill it from
                // them so the board card still has a readable title; a row with neither is allowed
                // (a placeholder PR held by its number).
                var desc = entry.Description?.Trim() ?? string.Empty;
                if (string.IsNullOrWhiteSpace(desc) && validItems.Count > 0)
                {
                    desc = string.Join(", ", validItems.Select(i => $"{i.ItemName} ({i.FormattedQuantity})"));
                }

                var prType = string.IsNullOrWhiteSpace(entry.PrType) ? BatchSharedPrType : entry.PrType;
                if (string.IsNullOrWhiteSpace(prType))
                {
                    errors.Add($"Row {index} ({entry.PrNo}): PR Type is required. Please select a PR Type.");
                    index++;
                    continue;
                }

                var pr = new PurchaseRequisition
                {
                    Id = entry.Id,
                    PrNo = entry.PrNo.Trim(),
                    Description = desc,
                    Requestor = string.IsNullOrWhiteSpace(entry.Requestor) ? (string.IsNullOrWhiteSpace(BatchSharedRequestor) ? "Unassigned" : BatchSharedRequestor.Trim()) : entry.Requestor.Trim(),
                    Plant = string.IsNullOrWhiteSpace(entry.Plant) ? (string.IsNullOrWhiteSpace(BatchSharedPlant) ? ProcurementPlant.RW01 : BatchSharedPlant) : entry.Plant,
                    PrType = prType,
                    Priority = string.IsNullOrWhiteSpace(entry.Priority) ? BatchSharedPriority : entry.Priority,
                    Status = string.IsNullOrWhiteSpace(entry.Status) ? ProcurementStatus.PrRaised : entry.Status,
                    Notes = entry.Notes?.Trim() ?? string.Empty,
                    CreatedAt = DateTime.Now,
                    UpdatedAt = DateTime.Now,
                    Items = new ObservableCollection<PrItem>(validItems),
                    CustomValues = new ObservableCollection<CustomFieldValue>(entry.CustomValues)
                };

                validEntries.Add(pr);
                index++;
            }

            if (errors.Count > 0)
            {
                    await _dialogs.DisplayAlertAsync("Validation", string.Join("\n", errors), "OK");
                return;
            }

            try
            {
                IsBusy = true;
                await _prRepo.SaveBatchPrsAsync(validEntries);

                // New rows change the match set, so reset the window (like a filter change): a plain
                // windowed re-read is bounded to the rows already shown and can miss the fresh row,
                // leaving "Showing N of N+1" with no new card until a manual refresh.
                IsBatchCreateModalVisible = false;
                BatchPrEntries.Clear();
                SelectedBatchEntry = null;
                ApplyFilters(resetToTop: true);
                DataChangeNotifier.Notify(ProcurementChange.Pr);

                ShowToast($"Created {validEntries.Count} purchase requisitions");
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
        public void CloseBatchCreateModal()
        {
            IsBatchCreateModalVisible = false;
            BatchPrEntries.Clear();
            SelectedBatchEntry = null;
        }

        [RelayCommand]
        public async Task PasteBatchPrRowsFromClipboardAsync()
        {
            try
            {
                if (!await _clipboard.HasTextAsync())
                {
                        await _dialogs.DisplayAlertAsync("Clipboard Empty", "No text found on clipboard. Please copy lines from Excel first.", "OK");
                    return;
                }

                var text = await _clipboard.GetTextAsync();
                if (string.IsNullOrWhiteSpace(text)) return;

                var parsedEntries = ClipboardItemParser.ParseBatchPrEntries(
                    text,
                    BatchSharedRequestor,
                    BatchSharedPriority,
                    BatchSharedNotes,
                    BatchSharedCustomValues,
                    defaultPlant: BatchSharedPlant,
                    defaultPrType: BatchSharedPrType);

                if (parsedEntries.Count == 0)
                {
                        await _dialogs.DisplayAlertAsync("No Requisitions Detected", "Could not detect valid requisitions from clipboard text.", "OK");
                    return;
                }

                // If current entries is only the initial blank rows with empty descriptions, clear them
                if (BatchPrEntries.Count <= 3 && BatchPrEntries.All(e => string.IsNullOrWhiteSpace(e.Description) && (e.Items.Count == 0 || string.IsNullOrWhiteSpace(e.Items[0].ItemName))))
                {
                    BatchPrEntries.Clear();
                }

                BatchPrEntry? firstAdded = null;
                foreach (var parsedEntry in parsedEntries)
                {
                    // Skip if a row with this PR number is already in BatchPrEntries
                    if (!string.IsNullOrWhiteSpace(parsedEntry.PrNo) &&
                        BatchPrEntries.Any(e => string.Equals(e.PrNo?.Trim(), parsedEntry.PrNo.Trim(), StringComparison.OrdinalIgnoreCase)))
                    {
                        continue;
                    }

                    BatchPrEntries.Add(parsedEntry);
                    firstAdded ??= parsedEntry;
                }

                // Land on the first row this paste actually added, so what just changed is what
                // you see - not whatever was selected before the paste.
                if (firstAdded != null) SelectedBatchEntry = firstAdded;

                UpdateBatchEntriesSummary();
            }
            catch (Exception ex)
            {
                _errorHandler.HandleError(ex);
            }
        }

        // Excel import for line items is a button now (Paste items from Excel), not a paste hook on
        // the name field - Ctrl+V in a field is a plain text paste. Parsed items are appended;
        // a single leftover blank row is dropped first.
        [RelayCommand]
        public async Task PasteBatchItemsFromClipboardAsync(BatchPrEntry? entry)
        {
            if (entry == null) return;
            try
            {
                if (!await _clipboard.HasTextAsync())
                {
                        await _dialogs.DisplayAlertAsync("Clipboard Empty", "No text found on clipboard. Copy the item rows from Excel first.", "OK");
                    return;
                }

                var text = await _clipboard.GetTextAsync();
                if (string.IsNullOrWhiteSpace(text)) return;

                var parsed = ClipboardItemParser.ParsePrItems(text, entry.Id, entry.Items.Count);
                if (parsed.Count == 0)
                {
                        await _dialogs.DisplayAlertAsync("No Items Detected", "Could not read any line items from the clipboard text.", "OK");
                    return;
                }

                if (entry.Items.Count == 1 && string.IsNullOrWhiteSpace(entry.Items[0].ItemName))
                    entry.Items.Clear();

                foreach (var item in parsed)
                {
                    item.PrId = entry.Id;
                    entry.Items.Add(item);
                }
                entry.NotifyItemsChanged();

                if (CurrentEditingPr != null && string.IsNullOrWhiteSpace(CurrentEditingPr.Description) && entry.Items.Count > 0)
                    CurrentEditingPr.Description = entry.Items[0].ItemName;
            }
            catch (Exception ex)
            {
                _errorHandler.HandleError(ex);
            }
        }
    }
}
