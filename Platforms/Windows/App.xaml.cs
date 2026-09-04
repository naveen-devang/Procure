using System;
using System.Runtime.InteropServices;
using System.Threading;
using Microsoft.UI.Xaml;

// To learn more about WinUI, the WinUI project structure,
// and more about our project templates, see: http://aka.ms/winui-project-info.

namespace Procure.WinUI
{
    /// <summary>
    /// Provides application-specific behavior to supplement the default Application class.
    /// </summary>
    public partial class App : MauiWinUIApplication
    {
        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        private static extern int MessageBox(IntPtr hWnd, string text, string caption, uint type);

        // Held for the process lifetime - a Mutex's ownership (and so the "is another instance
        // alive" signal to the next launch) ends the moment this is collected or the process exits.
        private static Mutex? _singleInstanceMutex;

        /// <summary>
        /// Initializes the singleton application object.  This is the first line of authored code
        /// executed, and as such is the logical equivalent of main() or WinMain().
        /// </summary>
        public App()
        {
            // Two processes can end up pointed at the same procure_tracker.db3 - most commonly when
            // the previous window hung (not crashed) and someone opens Procure again from the icon
            // instead of waiting. SQLite has no PRAGMA busy_timeout set for a plain read, so the
            // second process's very first query can throw "database is locked", which the caller
            // then swallows and just leaves the screen at its empty/zero defaults - which is what
            // "shows default data until I hit refresh" actually was. Refusing to start a second
            // process removes the race outright instead of just handling it better.
            _singleInstanceMutex = new Mutex(true, "Procure-SingleInstance-3F2A9C7E-B4A1-4E2D-9C3A", out var createdNew);
            if (!createdNew)
            {
                MessageBox(IntPtr.Zero,
                    "Procure is already running. Check your taskbar or Task Manager.",
                    "Procure", 0x40 /* MB_ICONINFORMATION */);
                Environment.Exit(0);
            }

            // Must run before anything else - this is what lets Velopack intercept
            // install/update/uninstall command-line invocations on launch (e.g. the
            // "apply this pending update and relaunch" hop after ApplyUpdatesAndRestart).
            Velopack.VelopackApp.Build().Run();

            this.InitializeComponent();

            // Belt-and-suspenders diagnostic: logs and lets the exception continue exactly as
            // before (no e.Handled = true) - this only makes a crash leave a record, it does not
            // change whether one happens.
            this.UnhandledException += (_, e) =>
                Procure.Utilities.CrashLog.Write("WinUI UnhandledException", e.Exception);
        }

        protected override MauiApp CreateMauiApp() => MauiProgram.CreateMauiApp();
    }

}
