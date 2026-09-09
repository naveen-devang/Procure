using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.UI.Xaml.Controls;
using Procure.Models;
using Procure.Services;
using Procure.Services.Export;
using Procure.Utilities;

namespace Procure.App.Platform;

// Phase 2 placeholders for the app-side services not yet ported. The board + nav +
// theme work without them; export / printing / updates / shortcut editing land in
// later Phase 2/4 commits. MIGRATION-PLAN.md.

public sealed class WinUiErrorHandler : IErrorHandler
{
    private readonly ShellContext _shell;
    public WinUiErrorHandler(ShellContext shell) => _shell = shell;

    private bool _dialogOpen;

    public void HandleError(Exception ex)
    {
        CrashLog.Write("WinUiErrorHandler.HandleError", ex);
        if (_shell.XamlRoot is null) return;

        void Show()
        {
            if (_dialogOpen) return;             // WinUI allows only one ContentDialog at a time
            _dialogOpen = true;
            var dialog = new ContentDialog
            {
                Title = "Something went wrong",
                Content = ex.Message,
                CloseButtonText = "OK",
                XamlRoot = _shell.XamlRoot,
            };
            dialog.Closed += (_, _) => _dialogOpen = false;
            _ = dialog.ShowAsync();
        }

        var q = App.UiQueue;
        if (q.HasThreadAccess) Show(); else q.TryEnqueue(Show);
    }
}

public sealed class RegistryOnlyKeyboardShortcutService : IKeyboardShortcutService
{
    public string GetCombo(string actionId) =>
        System.Linq.Enumerable.FirstOrDefault(KeyboardShortcutRegistry.All, d => d.Id == actionId)?.DefaultCombo ?? "";
    public bool IsCustomized(string actionId) => false;
    public void SetCombo(string actionId, string combo) { }
    public void ResetToDefault(string actionId) { }
    public void ResetAllToDefaults() { }
    public string? RecordingActionId { get; set; }
    public string? FindConflict(string combo, string excludingActionId) => null;
    public event EventHandler? ShortcutsChanged;
    public event EventHandler? RecordingActionChanged;
}

public sealed class UnavailableUpdateService : IUpdateService
{
    public string CurrentVersionString => CurrentVersion.ToString();
    public Version CurrentVersion => System.Reflection.Assembly.GetEntryAssembly()?.GetName().Version ?? new Version(0, 0);
    public Task<UpdateInfo> CheckForUpdatesAsync(string repoOwnerAndName) => Task.FromResult(new UpdateInfo());
    public Task<string> DownloadUpdateAsync(UpdateInfo update, IProgress<double>? progress = null, CancellationToken ct = default) => Task.FromResult("");
    public bool LaunchInstaller(string installerPath) => false;
    public Task OpenReleaseInBrowserAsync(string releaseUrl) => Task.CompletedTask;
    public UpdateDownloadStatus DownloadStatus => UpdateDownloadStatus.Idle;
    public double DownloadProgress => 0;
    public string? PendingUpdateTag => null;
    public UpdateInfo? LastKnownUpdate => null;
    public bool IsUpdateBusy => false;
    public event EventHandler? UpdateStateChanged;
    public Task<string?> GetReleaseNotesForVersionAsync(string repoOwnerAndName, string version) => Task.FromResult<string?>(null);
}

public sealed class UnavailableCsvExportService : ICsvExportService
{
    public Task<string> ExportPrsToCsvAsync(IEnumerable<PurchaseRequisition> prs, IEnumerable<CustomColumnDefinition> customColumns)
        => throw new NotSupportedException("CSV export not wired up yet (Phase 2).");
    public Task<string> SaveExportToFileAsync(string csvContent, string? filename = null)
        => throw new NotSupportedException("CSV export not wired up yet (Phase 2).");
}

public sealed class UnavailablePcrExportService : IPcrExportService
{
    public Task<string> ExportPcrToExcelAsync(PurchaseRequisition pr, PriceComparisonRequest pcr, IReadOnlyList<RequestForQuotation> selectedRfqs, string remarks)
        => throw new NotSupportedException("PCR export not wired up yet (Phase 2).");
    public byte[] GeneratePcrPdfBytes(PurchaseRequisition pr, PriceComparisonRequest pcr, IReadOnlyList<RequestForQuotation> selectedRfqs, string remarks, PcrPdfOptions options)
        => throw new NotSupportedException("PCR export not wired up yet (Phase 2).");
    public Task<string?> SavePcrPdfAsync(byte[] pdfBytes, string suggestedFileName) => Task.FromResult<string?>(null);
    public IReadOnlyList<string> GetAvailablePrinters() => Array.Empty<string>();
    public string GetDefaultPrinterName() => "";
    public Task<bool> PrintPcrPdfAsync(byte[] pdfBytes, string printerName, string jobTitle, bool doubleSided, IReadOnlyList<int>? pageIndices, int copies = 1) => Task.FromResult(false);
    public bool IsFileWriterPrinter(string printerName) => false;
}
