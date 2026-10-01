using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.Linq;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Procure.Abstractions;
using Procure.Data.Repositories;
using Procure.Models;
using Procure.Services;
using Procure.Services.Export;

namespace Procure.PageModels
{
    /// <summary>One step of the handover trail in the side panel.</summary>
    public sealed record ServiceEntryStep(string Title, string Detail, bool Done, bool IsLast);

    /// <summary>The Service Entry register: a hidden-by-default tab (Settings) that tracks service
    /// invoices from receipt to accounts. Its own table; nothing here reads or writes PRs or POs.</summary>
    public partial class ServiceEntryPageModel : ObservableObject
    {
        private const int PageSize = 60;
        public static readonly string[] StepTitles = { "Invoice received", "Handed to technical", "Returned with SAP SE", "Handed to accounts" };

        private readonly ServiceEntryRepository _repo;
        private readonly ISettingsService _settings;
        private readonly IErrorHandler _errors;
        private readonly IDialogService _dialogs;
        private readonly IUiDispatcher _dispatcher;
        private readonly IPcrExportService _files;

        private int _generation;
        private bool _allLoaded, _loadingMore, _loaded;

        public bool IsVisible { get; set; }

        public ServiceEntryPageModel(ServiceEntryRepository repo, ISettingsService settings, IErrorHandler errors,
            IDialogService dialogs, IUiDispatcher dispatcher, IPcrExportService files)
        {
            _repo = repo;
            _settings = settings;
            _errors = errors;
            _dialogs = dialogs;
            _dispatcher = dispatcher;
            _files = files;


            // Singleton, like the settings service: never unsubscribed.
            _settings.SettingsChanged += (_, e) =>
            {
                if (e.Key != nameof(ISettingsService.ServiceEntryFlagDays)) return;
                _loaded = false;
                if (IsVisible) _dispatcher.Post(() => _ = LoadAsync());
            };
        }

        // ---------------- list ----------------

        [ObservableProperty]
        public partial ObservableCollection<ServiceEntry> Entries { get; set; } = new();

        [ObservableProperty]
        public partial string SearchText { get; set; } = string.Empty;

        /// <summary>"open", "all", or "0".."3".</summary>
        [ObservableProperty]
        public partial string StageFilter { get; set; } = "open";


        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(IsEmpty))]
        public partial bool IsBusy { get; set; }

        public bool IsEmpty => !IsBusy && Entries.Count == 0;

        partial void OnSearchTextChanged(string value)
        {
            var g = ++_generation;
            _dispatcher.PostDelayed(TimeSpan.FromMilliseconds(300), () => { if (g == _generation) _ = LoadAsync(); });
        }

        partial void OnStageFilterChanged(string value) => _ = LoadAsync();

        public async Task EnsureLoadedAsync()
        {
            if (!_loaded) await LoadAsync();
        }

        [RelayCommand]
        public async Task LoadAsync()
        {
            var g = ++_generation;
            var flag = _settings.ServiceEntryFlagDays;
            try
            {
                IsBusy = true;
                // Off the UI thread: Microsoft.Data.Sqlite's async calls run synchronously (see CallOffPageModel).
                var (page, summary) = await Task.Run(async () =>
                    (await _repo.GetPageAsync(StageFilter, SearchText, 0, PageSize),
                     await _repo.GetSummaryAsync(flag))).ConfigureAwait(true);
                if (g != _generation) return;

                foreach (var e in page) e.FlagDays = flag;
                Entries = new ObservableCollection<ServiceEntry>(page);
                _allLoaded = page.Count < PageSize;
                _loaded = true;
                ApplySummary(summary, flag);
                ReselectAfterLoad();
            }
            catch (Exception ex) { _errors.HandleError(ex); }
            finally
            {
                if (g == _generation) IsBusy = false;
                OnPropertyChanged(nameof(IsEmpty));
            }
        }

        [RelayCommand]
        public async Task LoadMoreAsync()
        {
            if (_allLoaded || _loadingMore || IsBusy) return;
            var g = _generation;
            var flag = _settings.ServiceEntryFlagDays;
            try
            {
                _loadingMore = true;
                var skip = Entries.Count;
                var page = await Task.Run(() => _repo.GetPageAsync(StageFilter, SearchText, skip, PageSize)).ConfigureAwait(true);
                if (g != _generation) return;
                foreach (var e in page) { e.FlagDays = flag; Entries.Add(e); }
                _allLoaded = page.Count < PageSize;
            }
            catch (Exception ex) { _errors.HandleError(ex); }
            finally { _loadingMore = false; }
        }

        // ---------------- summary ----------------

        [ObservableProperty] public partial string OpenTab { get; set; } = "Open";
        [ObservableProperty] public partial string InvoiceInTab { get; set; } = "Invoice in";
        [ObservableProperty] public partial string WithTechnicalTab { get; set; } = "With technical";
        [ObservableProperty] public partial string SeDoneTab { get; set; } = "SE done";
        [ObservableProperty] public partial string WithAccountsTab { get; set; } = "With accounts";
        [ObservableProperty] public partial string AllTab { get; set; } = "All";

        [ObservableProperty] public partial string OpenCount { get; set; } = "0";
        [ObservableProperty] public partial string OpenAmount { get; set; } = string.Empty;
        [ObservableProperty] public partial string TechCount { get; set; } = "0";
        [ObservableProperty] public partial string LateCaption { get; set; } = "OVER 10 DAYS IN ONE STEP";
        [ObservableProperty] public partial string LateCount { get; set; } = "0";
        [ObservableProperty] public partial bool HasLate { get; set; }
        [ObservableProperty] public partial string SentCaption { get; set; } = "SENT TO ACCOUNTS";
        [ObservableProperty] public partial string SentCount { get; set; } = "0";
        [ObservableProperty] public partial string SentAmount { get; set; } = string.Empty;

        private string Currency => string.IsNullOrWhiteSpace(_settings.DefaultCurrency) ? "AED" : _settings.DefaultCurrency;

        private void ApplySummary(ServiceEntrySummary s, int flag)
        {
            OpenTab = $"Open  {s.OpenCount}";
            InvoiceInTab = $"Invoice in  {s.StageCounts[0]}";
            WithTechnicalTab = $"With technical  {s.StageCounts[1]}";
            SeDoneTab = $"SE done  {s.StageCounts[2]}";
            WithAccountsTab = $"With accounts  {s.StageCounts[3]}";
            AllTab = $"All  {s.AllCount}";

            OpenCount = s.OpenCount.ToString(CultureInfo.InvariantCulture);
            OpenAmount = $"{Currency} {ServiceEntry.Money(s.OpenAmount)}";
            TechCount = s.StageCounts[1].ToString(CultureInfo.InvariantCulture);
            LateCaption = $"OVER {flag} DAYS IN ONE STEP";
            LateCount = s.LateCount.ToString(CultureInfo.InvariantCulture);
            HasLate = s.LateCount > 0;
            SentCaption = $"SENT TO ACCOUNTS, {DateTime.Today.ToString("MMM", CultureInfo.InvariantCulture).ToUpperInvariant()}";
            SentCount = s.SentThisMonth.ToString(CultureInfo.InvariantCulture);
            SentAmount = $"{Currency} {ServiceEntry.Money(s.SentThisMonthAmount)}";
        }

        // ---------------- side panel ----------------

        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(HasSelection))]
        [NotifyPropertyChangedFor(nameof(NoSelection))]
        public partial ServiceEntry? Selected { get; set; }

        public bool HasSelection => Selected is not null;
        public bool NoSelection => Selected is null;

        [ObservableProperty] public partial IReadOnlyList<ServiceEntryStep> Steps { get; set; } = Array.Empty<ServiceEntryStep>();
        [ObservableProperty] public partial string PoBilledLabel { get; set; } = string.Empty;
        [ObservableProperty] public partial string PoBilledText { get; set; } = string.Empty;
        [ObservableProperty] public partial double PoBilledPercent { get; set; }
        [ObservableProperty] public partial bool PoOver { get; set; }
        [ObservableProperty] public partial bool HasPoAmount { get; set; }
        [ObservableProperty] public partial string PoOverText { get; set; } = string.Empty;

        // The one step that can be done next.
        [ObservableProperty] public partial bool HasNextStep { get; set; }
        [ObservableProperty] public partial bool CanUndoStep { get; set; }
        [ObservableProperty] public partial string NextStepTitle { get; set; } = string.Empty;
        [ObservableProperty] public partial bool NextStepNeedsSeNo { get; set; }
        [ObservableProperty] public partial DateTime NextStepDate { get; set; } = DateTime.Today;
        [ObservableProperty] public partial string NextSeNo { get; set; } = string.Empty;

        [RelayCommand]
        public async Task SelectAsync(ServiceEntry? entry)
        {
            Selected = entry;
            if (entry is null) return;
            BuildSteps(entry);
            await RefreshPoBilledAsync(entry);
        }

        private void ReselectAfterLoad()
        {
            if (Selected is null) return;
            var again = Entries.FirstOrDefault(e => e.Id == Selected.Id);
            if (again is null) return;   // filtered out: the panel keeps the old copy
            Selected = again;
            BuildSteps(again);
        }

        private void BuildSteps(ServiceEntry e)
        {
            string Days(DateTime? d) => ServiceEntry.Short(d);
            Steps = new[]
            {
                new ServiceEntryStep(StepTitles[0], $"{Days(e.InvoiceDate)} · {e.InvoiceNo}", true, false),
                new ServiceEntryStep(StepTitles[1], e.TechHandoverDate.HasValue ? Days(e.TechHandoverDate) : "Not yet", e.TechHandoverDate.HasValue, false),
                new ServiceEntryStep(StepTitles[2], e.SapSeDate.HasValue ? $"{Days(e.SapSeDate)} · SE {e.SeNoText}" : "Not yet", e.SapSeDate.HasValue, false),
                new ServiceEntryStep(StepTitles[3], e.AccountHandoverDate.HasValue ? Days(e.AccountHandoverDate) : "Not yet", e.AccountHandoverDate.HasValue, true),
            };
            HasNextStep = !e.IsDone;
            CanUndoStep = e.Stage > 0;
            NextStepTitle = e.IsDone ? string.Empty : "Next: " + StepTitles[e.Stage + 1];
            NextStepNeedsSeNo = e.Stage == ServiceEntry.WithTechnical;
            NextStepDate = DateTime.Today;
            NextSeNo = e.ServiceEntryNo ?? string.Empty;
        }

        private async Task RefreshPoBilledAsync(ServiceEntry e)
        {
            HasPoAmount = e.PoAmount is > 0;
            PoOver = false;
            if (string.IsNullOrWhiteSpace(e.PoNo)) { PoBilledLabel = PoBilledText = string.Empty; return; }
            try
            {
                var (count, total) = await Task.Run(() => _repo.GetPoBilledAsync(e.PoNo, Guid.Empty)).ConfigureAwait(true);
                if (Selected?.Id != e.Id) return;
                PoBilledLabel = count == 1 ? "Billed on this PO, 1 invoice" : $"Billed on this PO, {count} invoices";
                if (e.PoAmount is { } po && po > 0)
                {
                    PoBilledText = $"{ServiceEntry.Money(total)} / {ServiceEntry.Money(po)}";
                    PoBilledPercent = (double)Math.Min(100m, total / po * 100m);
                    PoOver = total - po > 0.005m;
                    PoOverText = PoOver ? $"Invoices are {ServiceEntry.Money(total - po)} over the PO amount." : string.Empty;
                }
                else PoBilledText = ServiceEntry.Money(total);
            }
            catch (Exception ex) { _errors.HandleError(ex); }
        }

        [RelayCommand]
        public async Task MarkDoneAsync()
        {
            if (Selected is not { IsDone: false } current) return;
            var e = current.Clone();
            var date = NextStepDate == default ? DateTime.Today : NextStepDate.Date;
            switch (e.Stage)
            {
                case ServiceEntry.InvoiceIn: e.TechHandoverDate = date; break;
                case ServiceEntry.WithTechnical:
                    if (string.IsNullOrWhiteSpace(NextSeNo))
                    {
                        await _dialogs.DisplayAlertAsync("Service Entry No needed", "Type the SAP service entry number, then click Mark Done.", "OK");
                        return;
                    }
                    e.SapSeDate = date;
                    e.ServiceEntryNo = NextSeNo.Trim();
                    break;
                case ServiceEntry.SeDone: e.AccountHandoverDate = date; break;
            }
            await SaveAndReloadAsync(e);
        }

        /// <summary>Takes back the most recent step - for a Mark Done clicked on the wrong entry.</summary>
        [RelayCommand]
        public async Task UndoStepAsync()
        {
            if (Selected is not { Stage: > 0 } current) return;
            var e = current.Clone();
            if (e.AccountHandoverDate.HasValue) e.AccountHandoverDate = null;
            else if (e.SapSeDate.HasValue) e.SapSeDate = null;   // the SE No stays, ready for the next Mark Done
            else e.TechHandoverDate = null;
            await SaveAndReloadAsync(e);
        }

        // ---------------- editor ----------------

        [ObservableProperty] public partial bool IsEditorOpen { get; set; }
        [ObservableProperty] public partial string EditorTitle { get; set; } = "New Service Entry";
        [ObservableProperty] public partial string EditorSaveText { get; set; } = "Add Entry";
        [ObservableProperty] public partial string EditSrNo { get; set; } = string.Empty;
        [ObservableProperty] public partial string EditPoNo { get; set; } = string.Empty;
        [ObservableProperty] public partial string EditPoAmount { get; set; } = string.Empty;
        [ObservableProperty] public partial string EditVendor { get; set; } = string.Empty;
        [ObservableProperty] public partial string EditDescription { get; set; } = string.Empty;
        [ObservableProperty] public partial DateTime EditInvoiceDate { get; set; } = DateTime.Today;
        [ObservableProperty] public partial string EditInvoiceNo { get; set; } = string.Empty;
        [ObservableProperty] public partial string EditInvoiceAmount { get; set; } = string.Empty;
        [ObservableProperty] public partial DateTime EditTech { get; set; }
        [ObservableProperty] public partial DateTime EditSapSe { get; set; }
        [ObservableProperty] public partial string EditSeNo { get; set; } = string.Empty;
        [ObservableProperty] public partial DateTime EditAccount { get; set; }
        [ObservableProperty] public partial string EditPoHint { get; set; } = string.Empty;
        [ObservableProperty] public partial bool EditPoHintWarn { get; set; }

        private Guid _editId;

        [RelayCommand]
        public async Task NewEntryAsync()
        {
            _editId = Guid.NewGuid();
            EditorTitle = "New Service Entry";
            EditorSaveText = "Add Entry";
            Fill(new ServiceEntry());
            try { EditSrNo = (await Task.Run(_repo.NextSrNoAsync).ConfigureAwait(true)).ToString(CultureInfo.InvariantCulture); }
            catch (Exception ex) { _errors.HandleError(ex); }
            IsEditorOpen = true;
        }

        [RelayCommand]
        public void EditEntry()
        {
            if (Selected is null) return;
            _editId = Selected.Id;
            EditorTitle = $"Edit Service Entry {Selected.SrNo}";
            EditorSaveText = "Save";
            Fill(Selected);
            EditSrNo = Selected.SrNo.ToString(CultureInfo.InvariantCulture);
            IsEditorOpen = true;
        }

        private void Fill(ServiceEntry e)
        {
            EditPoNo = e.PoNo;
            EditPoAmount = e.PoAmount is { } p ? p.ToString("0.00", CultureInfo.InvariantCulture) : string.Empty;
            EditVendor = e.Vendor;
            EditDescription = e.Description;
            EditInvoiceDate = e.InvoiceDate;
            EditInvoiceNo = e.InvoiceNo;
            EditInvoiceAmount = e.InvoiceAmount == 0 ? string.Empty : e.InvoiceAmount.ToString("0.00", CultureInfo.InvariantCulture);
            EditTech = e.TechHandoverDate ?? default;
            EditSapSe = e.SapSeDate ?? default;
            EditSeNo = e.ServiceEntryNo ?? string.Empty;
            EditAccount = e.AccountHandoverDate ?? default;
            EditPoHint = string.Empty;
            EditPoHintWarn = false;
        }

        [RelayCommand]
        public void CancelEdit() => IsEditorOpen = false;

        private int _poHintGeneration;

        partial void OnEditPoNoChanged(string value) => QueuePoHint();
        partial void OnEditPoAmountChanged(string value) => QueuePoHint();
        partial void OnEditInvoiceAmountChanged(string value) => QueuePoHint();

        private void QueuePoHint()
        {
            if (!IsEditorOpen) return;
            var g = ++_poHintGeneration;
            _dispatcher.PostDelayed(TimeSpan.FromMilliseconds(350), async () =>
            {
                if (g != _poHintGeneration || string.IsNullOrWhiteSpace(EditPoNo)) { if (g == _poHintGeneration) EditPoHint = string.Empty; return; }
                try
                {
                    var po = EditPoNo;
                    var (count, total) = await Task.Run(() => _repo.GetPoBilledAsync(po, _editId)).ConfigureAwait(true);
                    if (g != _poHintGeneration) return;
                    if (count == 0) { EditPoHint = string.Empty; return; }
                    var billed = total + (ParseMoney(EditInvoiceAmount) ?? 0);
                    var poAmt = ParseMoney(EditPoAmount);
                    var invoices = count == 1 ? "1 invoice" : $"{count} invoices";
                    EditPoHint = poAmt is > 0
                        ? $"{invoices} already on this PO. With this one: {ServiceEntry.Money(billed)} billed of {ServiceEntry.Money(poAmt.Value)}."
                        : $"{invoices} already on this PO, {ServiceEntry.Money(total)} billed.";
                    EditPoHintWarn = poAmt is > 0 && billed - poAmt.Value > 0.005m;
                }
                catch (Exception ex) { _errors.HandleError(ex); }
            });
        }

        [RelayCommand]
        public async Task SaveEntryAsync()
        {
            string? problem = null;
            if (!int.TryParse(EditSrNo?.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out var sr) || sr < 1) problem = "Sr No must be a whole number, 1 or more.";
            else if (string.IsNullOrWhiteSpace(EditPoNo)) problem = "Enter the PO number.";
            else if (string.IsNullOrWhiteSpace(EditVendor)) problem = "Enter the vendor.";
            else if (string.IsNullOrWhiteSpace(EditInvoiceNo)) problem = "Enter the invoice number.";
            else if (ParseMoney(EditInvoiceAmount) is null) problem = "Enter the invoice amount as a number, like 3281.28.";
            else if (!string.IsNullOrWhiteSpace(EditPoAmount) && ParseMoney(EditPoAmount) is null) problem = "PO amount must be a number, or left empty.";
            else if (EditSapSe != default && EditTech == default) problem = "Fill Tech Handover before the SAP SE date.";
            else if (EditAccount != default && EditSapSe == default) problem = "Fill the SAP SE date before Account Handover.";
            else if (EditSapSe != default && string.IsNullOrWhiteSpace(EditSeNo)) problem = "Enter the Service Entry No for the SAP SE date.";

            if (problem is null)
            {
                try
                {
                    if (await Task.Run(() => _repo.SrNoTakenAsync(sr, _editId)).ConfigureAwait(true))
                        problem = $"Sr No {sr} is already used by another entry.";
                }
                catch (Exception ex) { _errors.HandleError(ex); return; }
            }
            if (problem is not null)
            {
                await _dialogs.DisplayAlertAsync("Check the entry", problem, "OK");
                return;
            }

            var e = new ServiceEntry
            {
                Id = _editId,
                SrNo = sr,
                PoNo = EditPoNo.Trim(),
                PoAmount = ParseMoney(EditPoAmount),
                Vendor = EditVendor.Trim(),
                Description = EditDescription?.Trim() ?? string.Empty,
                InvoiceDate = EditInvoiceDate == default ? DateTime.Today : EditInvoiceDate.Date,
                InvoiceNo = EditInvoiceNo.Trim(),
                InvoiceAmount = ParseMoney(EditInvoiceAmount)!.Value,
                TechHandoverDate = EditTech == default ? null : EditTech.Date,
                SapSeDate = EditSapSe == default ? null : EditSapSe.Date,
                ServiceEntryNo = string.IsNullOrWhiteSpace(EditSeNo) ? null : EditSeNo.Trim(),
                AccountHandoverDate = EditAccount == default ? null : EditAccount.Date,
            };
            if (await SaveAndReloadAsync(e)) IsEditorOpen = false;
        }

        private async Task<bool> SaveAndReloadAsync(ServiceEntry e)
        {
            try
            {
                await Task.Run(() => _repo.SaveAsync(e)).ConfigureAwait(true);
            }
            catch (Exception ex) { _errors.HandleError(ex); return false; }

            e.FlagDays = _settings.ServiceEntryFlagDays;
            Selected = e;   // the panel follows the saved entry even if the filter now hides it
            await LoadAsync();
            if (Selected?.Id == e.Id) { BuildSteps(Selected); await RefreshPoBilledAsync(Selected); }
            return true;
        }

        [RelayCommand]
        public async Task DeleteEntryAsync()
        {
            if (Selected is not { } e) return;
            var ok = await _dialogs.DisplayAlertAsync("Delete entry",
                $"Delete Sr {e.SrNo}, {e.Vendor} invoice {e.InvoiceNo}? This cannot be undone.", "Delete", "Cancel");
            if (!ok) return;
            try
            {
                await Task.Run(() => _repo.DeleteAsync(e.Id)).ConfigureAwait(true);
                Selected = null;
                await LoadAsync();
            }
            catch (Exception ex) { _errors.HandleError(ex); }
        }

        // ---------------- export ----------------

        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(CanExport))]
        public partial bool IsExporting { get; set; }

        public bool CanExport => !IsExporting;

        /// <summary>Everything the current tab and search show - not just the loaded page.</summary>
        [RelayCommand]
        public async Task ExportAsync()
        {
            if (IsExporting) return;
            try
            {
                IsExporting = true;
                var stage = StageFilter; var search = SearchText;
                var bytes = await Task.Run(async () =>
                {
                    var rows = await _repo.GetAllAsync(stage, search);
                    return rows.Count == 0 ? null : ServiceEntryExcelExporter.Generate(rows);
                }).ConfigureAwait(true);
                if (bytes is null)
                {
                    await _dialogs.DisplayAlertAsync("Nothing to export", "No entries match the current tab and search.", "OK");
                    return;
                }
                await _files.SaveAndOpenFileAsync(bytes, $"ServiceEntries_{DateTime.Now:yyyyMMdd_HHmmss}.xlsx");
            }
            catch (Exception ex) { _errors.HandleError(ex); }
            finally { IsExporting = false; }
        }

        public Task<List<string>> FindVendorsAsync(string text) => Task.Run(() => _repo.FindVendorsAsync(text));

        /// <summary>"3,281.28", "3281.28" or "AED 3281.28".</summary>
        public static decimal? ParseMoney(string? s)
        {
            if (string.IsNullOrWhiteSpace(s)) return null;
            var t = new string(s.Where(c => char.IsDigit(c) || c is '.' or '-').ToArray());
            return decimal.TryParse(t, NumberStyles.Number, CultureInfo.InvariantCulture, out var v) && v >= 0 ? Math.Round(v, 2) : null;
        }
    }
}
