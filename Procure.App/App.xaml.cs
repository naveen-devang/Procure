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

        // Point at the real 20k test DB unless PROCURE_DB_DIR is already set. A real install
        // would fall through to DatabaseConstants' AppPaths default.
        var dbDir = Environment.GetEnvironmentVariable("PROCURE_DB_DIR")
                    ?? @"E:\Procure\Procure\TestData\procure-20k";
        Environment.SetEnvironmentVariable("PROCURE_DB_DIR", dbDir);

        // WinUI x:Bind invokes PropertyChanged on the raising thread and throws cross-thread;
        // several repo writes mutate models from Task.Run. Marshal those notifications.
        Procure.Models.ObservableModel.OnUiThread = () => UiQueue.HasThreadAccess;
        Procure.Models.ObservableModel.UiPost = a => UiQueue.TryEnqueue(() => a());

        SQLitePCL.Batteries_V2.Init();
        Services = BuildServices();
    }

    private Window? _window;

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        _window = Services.GetRequiredService<MainWindow>();
        _window.Activate();
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
