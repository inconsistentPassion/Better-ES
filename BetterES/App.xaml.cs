using System.IO;
using System.Runtime.Versioning;
using System.Windows;
using BetterES.Services;
using BetterES.View;
using BetterES.View.Pages;
using BetterES.ViewModel;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Wpf.Ui;
using Wpf.Ui.Appearance;
using Wpf.Ui.DependencyInjection;

namespace BetterES;

[SupportedOSPlatform("windows7.0")]
public partial class App : Application
{
    private static readonly IHost _host = Host.CreateDefaultBuilder()
        .ConfigureServices((context, services) =>
        {
            // WPF-UI services
            services.AddNavigationViewPageProvider();
            services.AddSingleton<INavigationService, NavigationService>();
            services.AddSingleton<ISnackbarService, SnackbarService>();
            services.AddSingleton<IContentDialogService, ContentDialogService>();

            // Main window
            services.AddSingleton<BetterESWindow>();
            services.AddSingleton<MainWindowViewModel>();

            // Services
            services.AddSingleton<ConnectionService>();
            services.AddSingleton<SettingsService>();
            services.AddSingleton<LogService>(sp => new LogService(Application.Current.Dispatcher));
            services.AddSingleton<TurboService>();

            // Pages
            services.AddSingleton<HomePage>();
            services.AddSingleton<ConnectionPage>();
            services.AddSingleton<LogPage>();
            services.AddSingleton<DynoPage>();
            services.AddSingleton<SettingsPage>();
            services.AddSingleton<DragPage>();
            services.AddSingleton<TurboPage>();
            services.AddSingleton<TimingPage>();
            services.AddSingleton<ExtraPage>();
            services.AddSingleton<TuningPage>();
            services.AddSingleton<EngineGeneratorPage>();
            services.AddSingleton<ModePage>();
        })
        .Build();

    public static IServiceProvider Services => _host.Services;

    public static T GetService<T>() where T : class => _host.Services.GetRequiredService<T>();

    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        await _host.StartAsync();

        // Apply saved theme
        var settings = Services.GetRequiredService<SettingsService>();
        var appTheme = settings.Theme == Theme.Light ? ApplicationTheme.Light : ApplicationTheme.Dark;
        ApplicationThemeManager.Apply(appTheme);

        var mainWindow = Services.GetRequiredService<BetterESWindow>();

        // Apply saved window settings
        mainWindow.Topmost = settings.StayOnTop;
        mainWindow.Show();

        // Initial navigation
        mainWindow.Navigate(typeof(HomePage));
    }

    protected override async void OnExit(ExitEventArgs e)
    {
        // Disconnect and clean up all connections before host disposal
        try
        {
            var conn = Services.GetService<ConnectionService>();
            if (conn != null)
            {
                await conn.DisconnectBridgeAsync();
            }
        }
        catch { }

        await _host.StopAsync();
        _host.Dispose();
        base.OnExit(e);
    }
}