using System.Threading.Tasks;

namespace Procure.Abstractions;

/// <summary>
/// Modal dialogs, decoupled from the UI framework. Method names and signatures
/// mirror MAUI's <c>Page.DisplayAlertAsync</c> / <c>DisplayActionSheetAsync</c> so
/// the view-model call sites port with a near-mechanical find/replace.
/// The MAUI host implements this over the Shell; the WinUI host over ContentDialog.
/// </summary>
public interface IDialogService
{
    /// <summary>One-button message. Returns when dismissed.</summary>
    Task DisplayAlertAsync(string title, string message, string cancel);

    /// <summary>Two-button prompt. <c>true</c> if the accept button was chosen.</summary>
    Task<bool> DisplayAlertAsync(string title, string message, string accept, string cancel);

    /// <summary>
    /// A list of choices. Returns the chosen button text, or null if cancelled.
    /// <paramref name="destruction"/> may be null.
    /// </summary>
    Task<string?> DisplayActionSheetAsync(string title, string cancel, string? destruction, params string[] buttons);

    /// <summary>Single-line text prompt. Returns the entered text, or null if cancelled.</summary>
    Task<string?> DisplayPromptAsync(string title, string message, string accept, string cancel,
        string? placeholder = null, string initialValue = "");

    /// <summary>Native folder picker. Returns the chosen path, or null if cancelled.</summary>
    Task<string?> PickFolderAsync();
}
