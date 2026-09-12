using System;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Procure.Abstractions;
using Procure.App.Platform;
using Procure.App.Views;
using Procure.Data;
using Procure.Data.Repositories;
using Procure.PageModels;
using Procure.Services;
using Procure.Services.Export;

namespace Procure.App;

public partial class App : Application
{
    public static IServiceProvider Services { get; private set; } = null!;
    public static DispatcherQueue UiQueue { get; private set; } = null!;

    public App()
    {
        // Must run before anything else - lets Velopack intercept install/update/uninstall
        // command-line invocations on launch (e.g. the relaunch hop after ApplyUpdatesAndRestart),
        // and sets VelopackLocator.Current, which UpdateService's UpdateManager needs at construction.
        Velopack.VelopackApp.Build().Run();

        InitializeComponent();
        UiQueue = DispatcherQueue.GetForCurrentThread();

        UnhandledException += (_, e) =>
            Procure.Utilities.CrashLog.Write("WinUI UnhandledException", e.Exception);
        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
            Procure.Utilities.CrashLog.Write("AppDomain.UnhandledException", e.ExceptionObject as Exception);
        System.Threading.Tasks.TaskScheduler.UnobservedTaskException += (_, e) =>
        {
            Procure.Utilities.CrashLog.Write("TaskScheduler.UnobservedTaskException", e.Exception);
            e.SetObserved();
        };

        // No default database path here. This used to point at the 20,000-PR test folder unless
        // PROCURE_DB_DIR said otherwise, which on any machine but this one is a path that does not
        // exist - so the app would create a new empty database somewhere the user would never look,
        // and every requisition they had ever entered would appear to be gone. The saved location,
        // or AppPaths' default, is resolved by DatabaseConstants; PROCURE_DB_DIR still overrides it
        // for a test run (see Tools/generate-test-db.py).

        // WinUI x:Bind invokes PropertyChanged on the raising thread and throws cross-thread;
        // several repo writes mutate models from Task.Run. Marshal those notifications.
        Procure.Models.ObservableModel.OnUiThread = () => UiQueue.HasThreadAccess;
        Procure.Models.ObservableModel.UiPost = a => UiQueue.TryEnqueue(() => a());

        SQLitePCL.Batteries_V2.Init();
        Services = BuildServices();

        // Force the settings service up now: its ctor migrates MAUI preferences and wires
        // DatabaseConstants' saved-directory hooks, both of which must be in place before the
        // first DB access (PrBoardPage load).
        var settings = Services.GetRequiredService<ISettingsService>();

        // App-level theme is the only one that reaches the NavigationView pane and popups;
        // element-level RequestedTheme (WinUiAppHost) does not, and left the light-mode tab
        // bar dark. Set it here, before any window - "System" just follows the OS.
        RequestedTheme = settings.AppTheme switch
        {
            "Light" => ApplicationTheme.Light,
            "Dark" => ApplicationTheme.Dark,
            _ => RequestedTheme,
        };
    }

    private Window? _window;

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        // Before any window exists: the first ApplyAccentColor is the only one that may INSERT
        // a brush (later ones recolour it in place). Anything that resolved an accent key before
        // that insert would be holding the framework's brush and would never repaint.
        (Services.GetRequiredService<IAppHost>() as WinUiAppHost)?
            .ApplyAccentColor(Services.GetRequiredService<ISettingsService>().AccentTheme);

        _window = Services.GetRequiredService<MainWindow>();
        _window.Activate();

#if DEBUG
        // The same suite the MAUI head runs, from Procure.Core - opt-in per environment variable
        // (PROCURE_SELFCHECK, PROCURE_FLOW_SELFCHECK, ...). After Activate so a check that takes
        // half a minute is not sitting between launch and the first window.
        _ = Procure.Data.SelfCheckSuite.RunAsync(Services);
#endif
    }

    private static IServiceProvider BuildServices()
    {
        var s = new ServiceCollection();

        s.AddSingleton<ShellContext>();

        // Platform abstractions (WinUI implementations of the Procure.Core interfaces).
        s.AddSingleton<IUiDispatcher, WinUiDispatcher>();
        s.AddSingleton<INavigationService, WinUiNavigationService>();
        s.AddSingleton<IDialogService, WinUiDialogService>();
        s.AddSingleton<IClipboardService, WinUiClipboardService>();
        s.AddSingleton<IAppHost, WinUiAppHost>();

        // Data + repositories
        s.AddSingleton<SqliteDatabase>();
        s.AddSingleton<ICustomColumnRepository, CustomColumnRepository>();
        s.AddSingleton<ICallOffRepository, CallOffRepository>();
        s.AddSingleton<ITodoRepository, TodoRepository>();
        s.AddSingleton<INoteRepository, NoteRepository>();
        s.AddSingleton<ILinkTargetService, LinkTargetService>();
        s.AddSingleton<IPurchaseRequisitionRepository, PurchaseRequisitionRepository>();

        // Services
        s.AddSingleton<ISettingsService, JsonSettingsService>();
        s.AddSingleton<IErrorHandler, WinUiErrorHandler>();
        s.AddSingleton<IKeyboardShortcutService, WinUiKeyboardShortcutService>();
        s.AddSingleton<IDashboardMetricsService, DashboardMetricsService>();
        s.AddSingleton<IUpdateService, Procure.App.Platform.UpdateService>();
        s.AddSingleton<ICsvExportService, Procure.Services.CsvExportService>();
        s.AddSingleton<IPcrExportService, Procure.App.Platform.PcrExportService>();

        // View models
        s.AddSingleton<DashboardPageModel>();
        s.AddSingleton<PrListPageModel>();
        s.AddSingleton<CallOffPageModel>();
        s.AddSingleton<TodoPageModel>();
        s.AddSingleton<NotePageModel>();
        s.AddSingleton<ManageColumnsPageModel>();
        s.AddSingleton<SettingsPageModel>();

        // Windows / pages. Singletons: MainWindow.NavigateTo caches by route, but it was
        // resolving a NEW page instance on every revisit - each new PrBoardPage re-ran the
        // full 20k board load and added another never-removed FilteredPrs.CollectionChanged
        // handler bound to the now-dead page, so the board got jankier and RAM climbed with
        // every visit. The page models are already singletons; the pages should be too.
        s.AddSingleton<MainWindow>();
        s.AddSingleton<PrBoardPage>();
        s.AddSingleton<DashboardPage>();
        s.AddSingleton<TasksPage>();
        s.AddSingleton<NotesPage>();
        s.AddSingleton<CallOffPage>();
        s.AddSingleton<SettingsPage>();

        return s.BuildServiceProvider(new ServiceProviderOptions { ValidateOnBuild = false });
    }
}
