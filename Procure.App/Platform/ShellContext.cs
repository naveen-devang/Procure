using System;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace Procure.App.Platform;

/// <summary>
/// The live window/frame handles the platform services need. MainWindow fills this
/// in once it is up; the services read it lazily.
/// </summary>
public sealed class ShellContext
{
    public Window? Window { get; set; }
    public Frame? ContentFrame { get; set; }

    public XamlRoot? XamlRoot => Window?.Content?.XamlRoot;

    /// <summary>Raised after ApplyTheme so pages/HUD can react. </summary>
    public event EventHandler? ThemeChanged;

    public void RaiseThemeChanged() => ThemeChanged?.Invoke(this, EventArgs.Empty);
}
