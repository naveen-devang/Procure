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

            // Populate 3 initial PR rows
            var entries = new ObservableCollection<BatchPrEntry>();
            for (int i = 0; i < 3; i++)
            {
                entries.Add(CreateNewBatchPrEntry(BatchSharedRequestor, BatchSharedPriority, BatchSharedPlant, BatchSharedPrType));
            }

            BatchPrEntries = entries;
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
            UpdateBatchEntriesSummary();
        }

        [RelayCommand]
        public void RemoveBatchPrRow(BatchPrEntry row)
        {
            if (BatchPrEntries.Count <= 1) return;
            if (BatchPrEntries.Contains(row))
            {
                BatchPrEntries.Remove(row);
                UpdateBatchEntriesSummary();
            }
        }

        [RelayCommand]
        public void DuplicateBatchPrRow(BatchPrEntry source)
        {
            var newEntryId = Guid.NewGuid();

            var dupCustomVals = new ObservableCollection<CustomFieldValue>();
            foreach (var cv in source.CustomValues)
            {
                dupCustomVals.Add(new CustomFieldValue
                {
                    Id = Guid.NewGuid(),
                    PrId = newEntryId,
                    ColumnId = cv.ColumnId,
                    ColumnName = cv.ColumnName,
                    ColumnDataType = cv.ColumnDataType,
                    SelectOptions = cv.SelectOptions,
                    Value = cv.Value
                });
            }

            var dupItems = new ObservableCollection<PrItem>();
            int s = 0;
            foreach (var item in source.Items)
            {
                dupItems.Add(new PrItem
                {
                    Id = Guid.NewGuid(),
                    PrId = newEntryId,
                    ItemName = item.ItemName,
                    Quantity = item.Quantity,
                    Unit = item.Unit,
                    EstimatedUnitPrice = item.EstimatedUnitPrice,
                    Notes = item.Notes,
                    SortOrder = s++
                });
            }

            var duplicate = new BatchPrEntry
            {
                Id = newEntryId,
                // Blank, not the source's number: duplicating a row copies its content, and two PRs
                // must never share a number.
                PrNo = string.Empty,
                Description = source.Description,
                Requestor = source.Requestor,
                Plant = source.Plant,
                PrType = source.PrType,
                Priority = source.Priority,
                Status = source.Status,
                Notes = source.Notes,
                CustomValues = dupCustomVals,
                Items = dupItems
            };

            BatchPrEntries.Add(duplicate);
            UpdateBatchEntriesSummary();
        }

        [RelayCommand]
        public void ApplySharedFieldsToAll()
        {
            foreach (var entry in BatchPrEntries)
            {
                if (!string.IsNullOrWhiteSpace(BatchSharedRequestor))
                    entry.Requestor = BatchSharedRequestor.Trim();

                if (!string.IsNullOrWhiteSpace(BatchSharedPlant))
                    entry.Plant = BatchSharedPlant;

                if (!string.IsNullOrWhiteSpace(BatchSharedPrType))
                    entry.PrType = BatchSharedPrType;

                entry.Priority = BatchSharedPriority;

                if (!string.IsNullOrWhiteSpace(BatchSharedNotes))
                    entry.Notes = BatchSharedNotes.Trim();

                foreach (var sharedVal in BatchSharedCustomValues)
                {
                    if (!string.IsNullOrWhiteSpace(sharedVal.Value))
                    {
                        var target = entry.CustomValues.FirstOrDefault(v => v.ColumnId == sharedVal.ColumnId);
                        if (target != null)
                        {
                            target.Value = sharedVal.Value;
                        }
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

                var desc = entry.Description?.Trim() ?? string.Empty;
                if (string.IsNullOrWhiteSpace(desc))
                {
                    if (validItems.Count > 0)
                    {
                        desc = string.Join(", ", validItems.Select(i => $"{i.ItemName} ({i.FormattedQuantity})"));
                    }
                    else
                    {
                        errors.Add($"Row {index} ({entry.PrNo}): Description or at least one Line Item is required.");
                        index++;
                        continue;
                    }
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
                if (Shell.Current != null)
                    await Shell.Current.DisplayAlertAsync("Validation", string.Join("\n", errors), "OK");
                return;
            }

            try
            {
                IsBusy = true;
                await _prRepo.SaveBatchPrsAsync(validEntries);

                // Nothing to insert in memory: ApplyFilters re-reads the window from SQLite, where the
                // rows now are, and the merge hands the board the instances it is already bound to.
                IsBatchCreateModalVisible = false;
                BatchPrEntries.Clear();
                ApplyFilters();
                UpdateStatusBanner();

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
        }

        [RelayCommand]
        public async Task PasteBatchPrItemsFromClipboardAsync(BatchPrEntry? entry)
        {
            if (entry == null) return;
            try
            {
                if (!Clipboard.Default.HasText)
                {
                    if (Shell.Current != null)
                        await Shell.Current.DisplayAlertAsync("Clipboard Empty", "No text found on clipboard. Please copy items from Excel first.", "OK");
                    return;
                }

                var text = await Clipboard.Default.GetTextAsync();
                if (string.IsNullOrWhiteSpace(text)) return;

                var parsedItems = ClipboardItemParser.ParsePrItems(text, entry.Id, entry.Items.Count);
                if (parsedItems.Count == 0) return;

                // If only 1 placeholder blank row exists, replace it
                if (entry.Items.Count == 1 && string.IsNullOrWhiteSpace(entry.Items[0].ItemName))
                {
                    entry.Items.Clear();
                }

                foreach (var item in parsedItems)
                {
                    entry.Items.Add(item);
                }

                entry.NotifyItemsChanged();

                if (string.IsNullOrWhiteSpace(entry.Description) && entry.Items.Count > 0)
                {
                    entry.Description = entry.Items[0].ItemName;
                }
            }
            catch (Exception ex)
            {
                _errorHandler.HandleError(ex);
            }
        }

        [RelayCommand]
        public async Task PasteBatchPrRowsFromClipboardAsync()
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
                    if (Shell.Current != null)
                        await Shell.Current.DisplayAlertAsync("No Requisitions Detected", "Could not detect valid requisitions from clipboard text.", "OK");
                    return;
                }

                // If current entries is only the initial blank rows with empty descriptions, clear them
                if (BatchPrEntries.Count <= 3 && BatchPrEntries.All(e => string.IsNullOrWhiteSpace(e.Description) && (e.Items.Count == 0 || string.IsNullOrWhiteSpace(e.Items[0].ItemName))))
                {
                    BatchPrEntries.Clear();
                }

                foreach (var parsedEntry in parsedEntries)
                {
                    // Skip if a row with this PR number is already in BatchPrEntries
                    if (!string.IsNullOrWhiteSpace(parsedEntry.PrNo) &&
                        BatchPrEntries.Any(e => string.Equals(e.PrNo?.Trim(), parsedEntry.PrNo.Trim(), StringComparison.OrdinalIgnoreCase)))
                    {
                        continue;
                    }

                    BatchPrEntries.Add(parsedEntry);
                }

                UpdateBatchEntriesSummary();
            }
            catch (Exception ex)
            {
                _errorHandler.HandleError(ex);
            }
        }

        public void HandleInlineItemPaste(PrItem currentItem, string rawPastedText, ObservableCollection<PrItem> targetCollection)
        {
            if (string.IsNullOrWhiteSpace(rawPastedText))
                return;

            var parsed = ClipboardItemParser.ParsePrItems(rawPastedText, currentItem.PrId, currentItem.SortOrder);
            if (parsed.Count == 0) return;

            // Set current item to first parsed item's attributes
            var first = parsed[0];
            currentItem.ItemName = first.ItemName;
            currentItem.Quantity = first.Quantity;
            currentItem.Unit = first.Unit;
            currentItem.EstimatedUnitPrice = first.EstimatedUnitPrice;
            currentItem.Notes = first.Notes;

            // Insert subsequent parsed items right after current item
            int currentIndex = targetCollection.IndexOf(currentItem);
            if (currentIndex < 0) currentIndex = 0;

            for (int i = 1; i < parsed.Count; i++)
            {
                int targetIndex = currentIndex + i;
                if (targetIndex < targetCollection.Count && string.IsNullOrWhiteSpace(targetCollection[targetIndex].ItemName))
                {
                    targetCollection[targetIndex].ItemName = parsed[i].ItemName;
                    targetCollection[targetIndex].Quantity = parsed[i].Quantity;
                    targetCollection[targetIndex].Unit = parsed[i].Unit;
                    targetCollection[targetIndex].EstimatedUnitPrice = parsed[i].EstimatedUnitPrice;
                    targetCollection[targetIndex].Notes = parsed[i].Notes;
                }
                else if (targetIndex <= targetCollection.Count)
                {
                    targetCollection.Insert(targetIndex, parsed[i]);
                }
                else
                {
                    targetCollection.Add(parsed[i]);
                }
            }

            if (CurrentEditingPr != null && string.IsNullOrWhiteSpace(CurrentEditingPr.Description) && targetCollection.Count > 0)
            {
                CurrentEditingPr.Description = targetCollection[0].ItemName;
            }
        }

        public void HandleInlineQuantityPaste(PrItem currentItem, string rawPastedText, ObservableCollection<PrItem> targetCollection)
        {
            if (string.IsNullOrWhiteSpace(rawPastedText))
                return;

            var parsed = ClipboardItemParser.ParseQuantities(rawPastedText);
            if (parsed.Count == 0) return;

            // Set current item's quantity
            var first = parsed[0];
            currentItem.Quantity = first.Quantity;
            if (!string.IsNullOrWhiteSpace(first.Unit))
            {
                currentItem.Unit = first.Unit;
            }

            int currentIndex = targetCollection.IndexOf(currentItem);
            if (currentIndex < 0) currentIndex = 0;

            for (int i = 1; i < parsed.Count; i++)
            {
                int targetIndex = currentIndex + i;
                if (targetIndex < targetCollection.Count)
                {
                    targetCollection[targetIndex].Quantity = parsed[i].Quantity;
                    if (!string.IsNullOrWhiteSpace(parsed[i].Unit))
                    {
                        targetCollection[targetIndex].Unit = parsed[i].Unit!;
                    }
                }
                else
                {
                    targetCollection.Add(new PrItem
                    {
                        Id = Guid.NewGuid(),
                        PrId = currentItem.PrId,
                        ItemName = string.Empty,
                        Quantity = parsed[i].Quantity,
                        Unit = !string.IsNullOrWhiteSpace(parsed[i].Unit) ? parsed[i].Unit! : "pcs",
                        SortOrder = targetIndex
                    });
                }
            }
        }

        public void HandleInlineUnitPaste(PrItem currentItem, string rawPastedText, ObservableCollection<PrItem> targetCollection)
        {
            if (string.IsNullOrWhiteSpace(rawPastedText))
                return;

            var parsed = ClipboardItemParser.ParseUnits(rawPastedText);
            if (parsed.Count == 0) return;

            // Set current item's unit
            currentItem.Unit = parsed[0];

            int currentIndex = targetCollection.IndexOf(currentItem);
            if (currentIndex < 0) currentIndex = 0;

            for (int i = 1; i < parsed.Count; i++)
            {
                int targetIndex = currentIndex + i;
                if (targetIndex < targetCollection.Count)
                {
                    targetCollection[targetIndex].Unit = parsed[i];
                }
                else
                {
                    targetCollection.Add(new PrItem
                    {
                        Id = Guid.NewGuid(),
                        PrId = currentItem.PrId,
                        ItemName = string.Empty,
                        Quantity = 1,
                        Unit = parsed[i],
                        SortOrder = targetIndex
                    });
                }
            }
        }

        public void HandleBatchPrRowPaste(BatchPrEntry currentEntry, string rawPastedText, bool isPrNoColumn)
        {
            if (string.IsNullOrWhiteSpace(rawPastedText))
                return;

            var parsed = ClipboardItemParser.ParseBatchPrEntries(
                rawPastedText,
                BatchSharedRequestor,
                BatchSharedPriority,
                BatchSharedNotes,
                BatchSharedCustomValues,
                isPrNoFirst: isPrNoColumn,
                defaultPlant: BatchSharedPlant,
                defaultPrType: BatchSharedPrType);

            if (parsed.Count == 0) return;

            // Set current entry attributes to first parsed row
            var first = parsed[0];
            if (isPrNoColumn)
            {
                currentEntry.PrNo = first.PrNo;
                if (!string.IsNullOrWhiteSpace(first.Description))
                    currentEntry.Description = first.Description;
            }
            else
            {
                currentEntry.Description = first.Description;
                if (first.PrNo != null && (first.PrNo.StartsWith("PR-", StringComparison.OrdinalIgnoreCase) || first.PrNo.StartsWith("PR#", StringComparison.OrdinalIgnoreCase)))
                    currentEntry.PrNo = first.PrNo;
            }

            if (!string.IsNullOrWhiteSpace(first.Requestor))
                currentEntry.Requestor = first.Requestor;
            if (!string.IsNullOrWhiteSpace(first.Priority))
                currentEntry.Priority = first.Priority;
            if (!string.IsNullOrWhiteSpace(first.Notes))
                currentEntry.Notes = first.Notes;

            // If current entry items was empty or blank and first has items with names, copy items
            if (first.Items.Count > 0 && !string.IsNullOrWhiteSpace(first.Items[0].ItemName))
            {
                if (currentEntry.Items.Count <= 1 && (currentEntry.Items.Count == 0 || string.IsNullOrWhiteSpace(currentEntry.Items[0].ItemName)))
                {
                    currentEntry.Items.Clear();
                    foreach (var itm in first.Items)
                    {
                        itm.PrId = currentEntry.Id;
                        currentEntry.Items.Add(itm);
                    }
                    currentEntry.NotifyItemsChanged();
                }
            }

            // Insert subsequent parsed PR rows directly after currentEntry, skipping duplicates
            int currentIndex = BatchPrEntries.IndexOf(currentEntry);
            if (currentIndex < 0) currentIndex = 0;
            int insertOffset = 1;

            for (int i = 1; i < parsed.Count; i++)
            {
                var candidate = parsed[i];

                // Skip if another row in BatchPrEntries already has this PR number
                if (!string.IsNullOrWhiteSpace(candidate.PrNo) &&
                    BatchPrEntries.Any(e => e != currentEntry && string.Equals(e.PrNo?.Trim(), candidate.PrNo.Trim(), StringComparison.OrdinalIgnoreCase)))
                {
                    continue;
                }

                int targetIndex = currentIndex + insertOffset;
                if (targetIndex < BatchPrEntries.Count && string.IsNullOrWhiteSpace(BatchPrEntries[targetIndex].Description) && string.IsNullOrWhiteSpace(BatchPrEntries[targetIndex].PrNo))
                {
                    BatchPrEntries[targetIndex] = candidate;
                }
                else if (targetIndex <= BatchPrEntries.Count)
                {
                    BatchPrEntries.Insert(targetIndex, candidate);
                }
                else
                {
                    BatchPrEntries.Add(candidate);
                }
                insertOffset++;
            }

            UpdateBatchEntriesSummary();
        }
    }
}
