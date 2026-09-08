using System;
using System.Threading.Tasks;

namespace Procure.Abstractions;

/// <summary>
/// The bits of the application shell a view model needs: the OS theme-changed
/// signal, quitting, and opening a file with its default handler.
/// Replaces scattered <c>Application.Current.*</c> and <c>Launcher.Default</c> use.
/// </summary>
public interface IAppHost
{
    /// <summary>Raised when the OS (or the app's own theme setting) flips light/dark.</summary>
    event EventHandler? ThemeChanged;

    void Quit();

    /// <summary>Open <paramref name="path"/> with the OS default application.</summary>
    Task OpenFileAsync(string path);
}
