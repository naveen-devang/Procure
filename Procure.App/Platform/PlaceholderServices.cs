using System;
using Microsoft.UI.Xaml.Controls;
using Procure.Services;
using Procure.Utilities;

namespace Procure.App.Platform;

// The WinUI error dialog. The other former placeholders (updates / CSV / PCR export /
// keyboard shortcuts) are now real ports - see UpdateService, PcrExportService,
// WinUiKeyboardShortcutService, and Procure.Core/Services/CsvExportService.

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
                Content = UserFacingError.Describe(ex),
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
