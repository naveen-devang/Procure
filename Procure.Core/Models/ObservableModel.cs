using System;
using System.ComponentModel;
using CommunityToolkit.Mvvm.ComponentModel;

namespace Procure.Models
{
    /// <summary>
    /// Base for every bound model. WinUI's x:Bind invokes its PropertyChanged handler on
    /// whatever thread raised the event and throws RPC_E_WRONG_THREAD (0x8001010E) if that
    /// isn't the UI thread - and several repository writes mutate model fields (UpdatedAt,
    /// aggregates) from the thread pool via Task.Run. MAUI auto-marshalled; WinUI does not.
    ///
    /// So: when a UI host has registered <see cref="UiPost"/> and we're off the UI thread,
    /// the change notification is posted to the UI thread. Headless/self-check runs leave the
    /// hooks null and notifications stay synchronous.
    /// </summary>
    public abstract class ObservableModel : ObservableObject
    {
        /// <summary>UI host sets this (WinUI: App.UiQueue.TryEnqueue).</summary>
        public static Action<Action>? UiPost { get; set; }

        /// <summary>UI host sets this (WinUI: App.UiQueue.HasThreadAccess).</summary>
        public static Func<bool>? OnUiThread { get; set; }

        protected override void OnPropertyChanged(PropertyChangedEventArgs e)
        {
            if (UiPost is { } post && OnUiThread is { } onUi && !onUi())
                post(() => base.OnPropertyChanged(e));
            else
                base.OnPropertyChanged(e);
        }
    }
}
