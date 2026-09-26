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

        // Typed header fields are edited through these buffers, not the live board PR: TwoWay
        // bindings to the live object re-rendered the board card behind the modal on every
        // keystroke. Committed to the PR in SavePrModalAsync; dropdown fields (Plant, PR Type,
        // Priority, Status) still bind live — a Picker fires once per selection, not per key.
        [ObservableProperty]
        public partial string EditingPrNo { get; set; } = string.Empty;

        [ObservableProperty]
        public partial string EditingPrRequestor { get; set; } = string.Empty;

        [ObservableProperty]
        public partial string EditingPrRequestedFor { get; set; } = string.Empty;

        [ObservableProperty]
        public partial string EditingPrDescription { get; set; } = string.Empty;

        [ObservableProperty]
        public partial string EditingPrNotes { get; set; } = string.Empty;

        // The edit modal binds TwoWay to the live PR the card shows, so cancelling must put back
        // what was there on open or abandoned edits stay on the card and ride along with the next
        // unrelated save. Items/CustomValues references included: a failed validation replaces them
        // before the user can still cancel.
        private sealed record PrEditSnapshot(string PrNo, string Requestor, string RequestedFor, string Plant, string PrType,
            string Description, string Priority, string Status, string Notes,
            ObservableCollection<PrItem> Items, ObservableCollection<CustomFieldValue> CustomValues);
        private PrEditSnapshot? _editSnapshot;


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
        public async Task RemoveEditingPrItemAsync(PrItem item)
        {
            if (!EditingPrItems.Contains(item)) return;

            // Refuses if the line is already on a PO, and offers to take the quote lines with it if
            // vendors have priced it. See PrListPageModel.PrSync.
            if (!await ConfirmRemovePrItemAsync(item)) return;

            EditingPrItems.Remove(item);
        }

        [RelayCommand]
        public async Task PasteEditingPrItemsFromClipboardAsync()
        {
            try
            {
                if (!await _clipboard.HasTextAsync())
                {
                        await _dialogs.DisplayAlertAsync("Clipboard Empty", "No text found on clipboard. Please copy lines from Excel first.", "OK");
                    return;
                }

                var text = await _clipboard.GetTextAsync();
                if (string.IsNullOrWhiteSpace(text))
                {
                        await _dialogs.DisplayAlertAsync("Clipboard Empty", "Clipboard text is empty.", "OK");
                    return;
                }

                var prId = CurrentEditingPr?.Id ?? Guid.NewGuid();
                var parsedItems = ClipboardItemParser.ParsePrItems(text, prId, EditingPrItems.Count);

                if (parsedItems.Count == 0)
                {
                        await _dialogs.DisplayAlertAsync("No Items Detected", "Could not detect valid items from clipboard text.", "OK");
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
                _ = FillPrItemPricesAsync(parsedItems, CurrentEditingPr?.Id);

                if (CurrentEditingPr != null && string.IsNullOrWhiteSpace(EditingPrDescription) && EditingPrItems.Count > 0)
                {
                    EditingPrDescription = EditingPrItems[0].ItemName;
                }
            }
            catch (Exception ex)
            {
                _errorHandler.HandleError(ex);
            }
        }

        private bool _isOpeningEditPrModal;

        [RelayCommand]
        public async Task OpenEditPrModalAsync(PurchaseRequisition pr)
        {
            pr = await EnsureHydratedAsync(pr);   // custom field values are left out of the board read

            // A second click before this reaches IsEditModalVisible = true (there's an await right
            // below, so that window is real - IsEditModalVisible itself isn't set yet during it, so
            // it can't be the guard) let two calls both populate EditingPrItems/EditingCustomValues
            // around the same time. The modal's BindableLayout rebuilding its children while a
            // previous rebuild was still enumerating them threw "Collection was modified" and took
            // the whole process down with it - a plain managed exception, but one that crossed a
            // WinRT dispatch boundary and surfaced as an unrecoverable native fault instead.
            if (_isOpeningEditPrModal) return;
            _isOpeningEditPrModal = true;

            try
            {
                await OpenEditPrModalCoreAsync(pr);
            }
            finally
            {
                _isOpeningEditPrModal = false;
            }
        }

        private async Task OpenEditPrModalCoreAsync(PurchaseRequisition pr)
        {
            var defs = await _customColumnRepo.GetAllDefinitionsAsync();
            CustomColumnDefinitions = new ObservableCollection<CustomColumnDefinition>(defs);

            EditModalTitle = $"Edit Requisition {pr.PrNo}";
            CurrentEditingPr = pr;
            EditingPrNo = pr.PrNo;
            EditingPrRequestor = pr.Requestor;
            EditingPrRequestedFor = pr.RequestedFor;
            EditingPrDescription = pr.Description;
            EditingPrNotes = pr.Notes;
            _editSnapshot = new PrEditSnapshot(pr.PrNo, pr.Requestor, pr.RequestedFor, pr.Plant, pr.PrType,
                pr.Description, pr.Priority, pr.Status, pr.Notes, pr.Items, pr.CustomValues);

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
                    // A line break in an item name is legitimate now - a spec pasted from one Excel
                    // cell. Keep it (just normalise CRLF, drop tabs which would confuse the paste
                    // parser). The unit is always a short token, so that stays fully stripped.
                    ItemName = NormalizeMultilineName(i.ItemName),
                    Quantity = i.Quantity,
                    Unit = string.IsNullOrWhiteSpace(i.Unit) ? "pcs" : StripControlChars(i.Unit),
                    EstimatedUnitPrice = i.EstimatedUnitPrice,
                    Notes = i.Notes,
                    SortOrder = i.SortOrder
                }).ToList();
                EditingPrItems = new ObservableCollection<PrItem>(copied);
                CapturePrItemsSnapshot(pr);
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
            _ = FillPrItemPricesAsync(EditingPrItems.ToList(), pr.Id);   // an older PR's empty prices
        }

        private static string StripControlChars(string? value) =>
            string.IsNullOrEmpty(value) ? string.Empty : value.Replace("\n", " ").Replace("\r", " ").Replace("\t", " ").Trim();

        private static string NormalizeMultilineName(string? value) =>
            string.IsNullOrEmpty(value) ? string.Empty
                : value.Replace("\r\n", "\n").Replace("\r", "\n").Replace("\t", " ").Trim();

        [RelayCommand]
        public async Task SavePrModalAsync()
        {
            if (CurrentEditingPr == null) return;

            // Commit the buffered header fields onto the live PR now — the modal edits buffers so
            // typing does not repaint the board card behind it.
            CurrentEditingPr.PrNo = EditingPrNo;
            CurrentEditingPr.Requestor = EditingPrRequestor;
            CurrentEditingPr.RequestedFor = EditingPrRequestedFor?.Trim() ?? string.Empty;
            CurrentEditingPr.Description = EditingPrDescription;
            CurrentEditingPr.Notes = EditingPrNotes;

            if (string.IsNullOrWhiteSpace(CurrentEditingPr.PrNo))
            {
                    await _dialogs.DisplayAlertAsync("Validation", "PR Number is required.", "OK");
                return;
            }

            if (string.IsNullOrWhiteSpace(CurrentEditingPr.PrType))
            {
                    await _dialogs.DisplayAlertAsync("Validation", "PR Type is required. Please select a PR Type.", "OK");
                return;
            }

            var validItems = EditingPrItems.Where(i => !string.IsNullOrWhiteSpace(i.ItemName)).ToList();

            // Cutting a line below what is already ordered is allowed, but not silently: the PR then
            // carries a visible Over-ordered state instead of the old "Complete".
            if (!await ConfirmQuantityCutsAsync(CurrentEditingPr, validItems)) return;

            // The pre-edit lines, kept back before the new list replaces them: the quote sync below
            // resolves against these, which is the only way it can tell a rename from a deletion
            // plus an unrelated addition. The modal edits copies, so these still hold the old values.
            var preEditItems = CurrentEditingPr.Items?.ToList() ?? new List<PrItem>();

            CurrentEditingPr.Items = new ObservableCollection<PrItem>(validItems);

            // Description is optional - filled from the line items when blank so the board card
            // keeps a readable title, but never required.
            if (string.IsNullOrWhiteSpace(CurrentEditingPr.Description) && validItems.Count > 0)
            {
                CurrentEditingPr.Description = CurrentEditingPr.ItemsSummary;
            }

            try
            {
                CurrentEditingPr.CustomValues = new ObservableCollection<CustomFieldValue>(EditingCustomValues);
                await _prRepo.SaveAsync(CurrentEditingPr);

                // Only now: a quote line references a PR line by foreign key, so a line added to the
                // requisition has to be in the database before a quote can point at it.
                var syncSummary = await SyncQuotesToPrAsync(CurrentEditingPr, preEditItems, validItems);

                CurrentEditingPr.NotifyHierarchyChanged();
                ApplyFilters();
                DataChangeNotifier.Notify(ProcurementChange.Pr | ProcurementChange.Rfq);

                _editSnapshot = null;
                _prItemsToPurgeFromQuotes.Clear();
                IsEditModalVisible = false;

                if (!string.IsNullOrEmpty(syncSummary)) ShowToast(syncSummary);
            }
            catch (Exception ex)
            {
                _errorHandler.HandleError(ex);
            }
        }

        [RelayCommand]
        public void CloseEditModal()
        {
            if (CurrentEditingPr is { } pr && _editSnapshot is { } s)
            {
                pr.PrNo = s.PrNo;
                pr.Requestor = s.Requestor;
                pr.RequestedFor = s.RequestedFor;
                pr.Plant = s.Plant;
                pr.PrType = s.PrType;
                pr.Description = s.Description;
                pr.Priority = s.Priority;
                pr.Status = s.Status;
                pr.Notes = s.Notes;
                pr.Items = s.Items;
                pr.CustomValues = s.CustomValues;
            }
            _editSnapshot = null;
            IsEditModalVisible = false;
        }

        [RelayCommand]
        public async Task DeletePrAsync(PurchaseRequisition pr)
        {

            // Naming the orders is the whole point of the warning - "and all associated POs" gave no
            // clue that a raised order was about to go with it. Deleting is still allowed: a
            // requisition raised by mistake has to be removable, orders and all.
            var poCount = pr.Pos?.Count ?? 0;
            var poNos = poCount == 0
                ? string.Empty
                : string.Join(", ", pr.Pos!.Select(p => p.PoNo).Where(n => !string.IsNullOrWhiteSpace(n)).Distinct());

            var message = string.IsNullOrEmpty(poNos)
                ? $"Are you sure you want to delete {pr.PrNo} ({pr.Description}) and all associated RFQs, PCRs, and POs?\n\nYou can undo this for 10 seconds."
                : $"{pr.PrNo} has {poCount} purchase order(s) raised against it: {poNos}.\n\nDeleting the requisition deletes those orders and their call-off history too. You can undo this for 10 seconds.";

            var confirm = await _dialogs.DisplayAlertAsync(
                string.IsNullOrEmpty(poNos) ? "Delete PR" : "Delete PR With Orders",
                message,
                "Delete",
                "Cancel");

            if (!confirm) return;

            await DeletePrsWithUndoAsync(new[] { pr },
                string.IsNullOrWhiteSpace(pr.PrNo) ? "Requisition deleted" : $"{pr.PrNo} deleted");
        }

        /// <summary>Deletes one RFQ or PO with Undo: off its PR's card now, from the database when Undo
        /// runs out. Undo puts the same object back where it was, on whichever copy of the PR the board
        /// holds by then (a reload in between builds new PR objects).</summary>
        internal async Task DeleteChildWithUndoAsync<T>(T child, Func<T, Guid> idOf, Guid prId, DeleteKind kind,
            string message, Func<PurchaseRequisition, ObservableCollection<T>> collectionOf, Action notify)
        {
            var id = idOf(child);
            var parent = _loadedPrs.FirstOrDefault(p => p.Id == prId);
            var index = parent is null ? 0 : Math.Max(0, collectionOf(parent).IndexOf(child));

            if (_undo is null)
            {
                await (kind == DeleteKind.Rfq ? _prRepo.DeleteRfqAsync(id) : _prRepo.DeletePoAsync(id));
            }
            else
            {
                await _undo.StartAsync(message, new[] { new PendingDeleteItem(kind, id) },
                    onUndo: () =>
                    {
                        var current = _loadedPrs.FirstOrDefault(p => p.Id == prId);
                        if (current is null) return;   // scrolled out of the window: the next load reads it back
                        var list = collectionOf(current);
                        if (list.Any(c => idOf(c) == id)) return;
                        list.Insert(Math.Min(index, list.Count), child);
                        current.NotifyHierarchyChanged();
                    },
                    onCommitted: notify);
            }

            if (parent is not null)
            {
                collectionOf(parent).Remove(child);
                parent.NotifyHierarchyChanged();
            }
            if (_undo is null) notify();
        }

        /// <summary>The one PR delete path, for a single PR and for a selection: hides them now, and
        /// UndoDeleteService deletes them - RFQs, PCRs and POs with them - once Undo has run out.</summary>
        internal async Task DeletePrsWithUndoAsync(IReadOnlyList<PurchaseRequisition> prs, string message)
        {
            try
            {
                if (_undo is null)
                {
                    foreach (var pr in prs) await _prRepo.DeleteAsync(pr.Id);
                }
                else
                {
                    await _undo.StartAsync(message,
                        prs.Select(p => new PendingDeleteItem(DeleteKind.Pr, p.Id)).ToList(),
                        onUndo: () => ApplyFilters(resetToTop: true),
                        onCommitted: () => DataChangeNotifier.Notify(ProcurementChange.All));
                }

                foreach (var pr in prs)
                {
                    pr.PropertyChanged -= OnPrItemPropertyChanged;
                    _selectedIds.Remove(pr.Id);
                }
                ApplyFilters(resetToTop: true);
                UpdateSelectionState();
                if (_undo is null) DataChangeNotifier.Notify(ProcurementChange.All);
            }
            catch (Exception ex)
            {
                _errorHandler.HandleError(ex);
            }
        }

    }
}
