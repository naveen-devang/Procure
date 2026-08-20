using CommunityToolkit.Maui;
using Microsoft.Extensions.Logging;
using Microsoft.Maui.LifecycleEvents;
using Procure.Data;
using Procure.Data.Repositories;
using Procure.PageModels;
using Procure.Pages;
using Procure.Services;
using Procure.Services.Export;

namespace Procure
{
    public static class MauiProgram
    {
        public static MauiApp CreateMauiApp()
        {
            var builder = MauiApp.CreateBuilder();
            builder
                .UseMauiApp<App>()
                .UseMauiCommunityToolkit()
                .ConfigureLifecycleEvents(events =>
                {
#if WINDOWS
                    events.AddWindows(windows =>
                    {
                        windows.OnWindowCreated(window =>
                        {
                            window.SystemBackdrop = new Microsoft.UI.Xaml.Media.MicaBackdrop
                            {
                                Kind = Microsoft.UI.Composition.SystemBackdrops.MicaKind.Base
                            };

                            try
                            {
                                var handle = WinRT.Interop.WindowNative.GetWindowHandle(window);
                                var windowId = Microsoft.UI.Win32Interop.GetWindowIdFromWindow(handle);
                                var appWindow = Microsoft.UI.Windowing.AppWindow.GetFromWindowId(windowId);
                                if (appWindow?.Presenter is Microsoft.UI.Windowing.OverlappedPresenter presenter)
                                {
                                    presenter.Maximize();
                                }
                            }
                            catch
                            {
                                // Fallback to standard window sizing
                            }

                            window.VisibilityChanged += (s, e) =>
                            {
                                if (!e.Visible)
                                {
                                    var memoryOptimizer = Application.Current?.Handler?.MauiContext?.Services?.GetService<IMemoryOptimizerService>();
                                    memoryOptimizer?.TrimMemory();
                                }
                            };

                            window.Activated += (s, e) =>
                            {
                                if (e.WindowActivationState == Microsoft.UI.Xaml.WindowActivationState.Deactivated)
                                {
                                    var memoryOptimizer = Application.Current?.Handler?.MauiContext?.Services?.GetService<IMemoryOptimizerService>();
                                    memoryOptimizer?.TrimMemory();
                                }
                            };
                        });
                    });
#endif
                })
                .ConfigureMauiHandlers(handlers =>
                {
#if WINDOWS
                    Microsoft.Maui.Controls.Handlers.Items.CollectionViewHandler.Mapper.AppendToMapping("KeyboardAccessibleCollectionView", (handler, view) =>
                    {
                        handler.PlatformView.SingleSelectionFollowsFocus = false;
                    });

                    Microsoft.Maui.Handlers.DatePickerHandler.Mapper.AppendToMapping("FullyRoundedDatePicker", (handler, view) =>
                    {
                        handler.PlatformView.CornerRadius = new Microsoft.UI.Xaml.CornerRadius(4);
                    });

                    Microsoft.Maui.Handlers.CheckBoxHandler.Mapper.AppendToMapping("TightWinUICheckBox", (handler, view) =>
                    {
                        handler.PlatformView.MinWidth = 0;
                        handler.PlatformView.MinHeight = 0;
                        handler.PlatformView.Padding = new Microsoft.UI.Xaml.Thickness(0);
                        handler.PlatformView.Margin = new Microsoft.UI.Xaml.Thickness(0);
                    });
#endif
                })
                .ConfigureFonts(fonts =>
                {
                    fonts.AddFont("OpenSans-Regular.ttf", "OpenSansRegular");
                    fonts.AddFont("OpenSans-Semibold.ttf", "OpenSansSemibold");
                    fonts.AddFont("SegoeUI-Semibold.ttf", "SegoeSemibold");
                    fonts.AddFont("FluentSystemIcons-Regular.ttf", Fonts.FluentUI.FontFamily);
                });

#if DEBUG
            builder.Logging.AddDebug();
            builder.Services.AddLogging(configure => configure.AddDebug());
#endif

            // Database & Repositories
            builder.Services.AddSingleton<SqliteDatabase>();
            builder.Services.AddSingleton<ICustomColumnRepository, CustomColumnRepository>();
            builder.Services.AddSingleton<IPurchaseRequisitionRepository, PurchaseRequisitionRepository>();

            // Services
            builder.Services.AddSingleton<ISettingsService, SettingsService>();
            builder.Services.AddSingleton<IDashboardMetricsService, DashboardMetricsService>();
            builder.Services.AddSingleton<ICsvExportService, CsvExportService>();
            builder.Services.AddSingleton<IPcrExportService, PcrExportService>();
            builder.Services.AddSingleton<IUpdateService, UpdateService>();
            builder.Services.AddSingleton<IMemoryOptimizerService, MemoryOptimizerService>();
            builder.Services.AddSingleton<SeedDataService>();
            builder.Services.AddSingleton<IErrorHandler, ModalErrorHandler>();

            // ViewModels & Pages
            // All pages are singletons: MAUI desktop page inflation is expensive (especially
            // PrListPage with its 76KB XAML). Transient would cause a full re-inflate + full
            // DB reload on every tab switch, which is the exact freeze we are trying to fix.
            builder.Services.AddSingleton<DashboardPageModel>();
            builder.Services.AddSingleton<DashboardPage>();

            builder.Services.AddSingleton<PrListPageModel>();
            builder.Services.AddSingleton<PrListPage>();

            builder.Services.AddSingleton<ManageColumnsPageModel>();
            builder.Services.AddSingleton<ManageColumnsPage>();

            builder.Services.AddSingleton<SettingsPageModel>();
            builder.Services.AddSingleton<SettingsPage>();

            builder.Services.AddSingleton<AppShell>();

            return builder.Build();
        }
    }
}
