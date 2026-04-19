using System.Windows;
using System.Windows.Controls;
using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using BetterES.View.Pages;
using Wpf.Ui.Controls;

namespace BetterES.ViewModel;

public partial class MainWindowViewModel : ObservableObject
{
    [ObservableProperty]
    private string _applicationTitle = "BetterES";

    [ObservableProperty]
    private bool _isNavigationPaneOpen = false;

    internal const string TuningNavigationRootTag = "TuningNavRoot";
    internal const string ConnectionNavigationRootTag = "ConnectionNavRoot";

    private static ObservableCollection<object> BuildMenuItems()
    {
        var tuning = new NavigationViewItem("Tuning", SymbolRegular.Wrench24, typeof(TuningPage));
        tuning.Tag = TuningNavigationRootTag;
        tuning.MenuItems.Add(new NavigationViewItem("Timing", SymbolRegular.ClockAlarm24, typeof(TimingPage)));
        tuning.MenuItems.Add(new NavigationViewItem("Turbo", SymbolRegular.Flash24, typeof(TurboPage)));
        tuning.MenuItems.Add(new NavigationViewItem("Extra", SymbolRegular.Box24, typeof(ExtraPage)));

        var connection = new NavigationViewItem("Connection", SymbolRegular.PlugConnected24, typeof(ConnectionPage));
        connection.Tag = ConnectionNavigationRootTag;
        connection.MenuItems.Add(new NavigationViewItem("Mode", SymbolRegular.Settings24, typeof(ModePage)));

        return new ObservableCollection<object>
        {
            new NavigationViewItem("Home", SymbolRegular.Home24, typeof(HomePage)),
            connection,
            tuning,
            new NavigationViewItem("Dyno", SymbolRegular.DataTrending24, typeof(DynoPage)),
            new NavigationViewItem("Drag", SymbolRegular.Gauge24, typeof(DragPage)),
            new NavigationViewItem("Generator", SymbolRegular.Globe24, typeof(EngineGeneratorPage)),
        };
    }

    [ObservableProperty]
    private ObservableCollection<object> _menuItems = BuildMenuItems();

    [ObservableProperty]
    private ObservableCollection<object> _footerMenuItems = new()
    {
        new NavigationViewItem("Log", SymbolRegular.ClipboardCode24, typeof(LogPage)),
        new NavigationViewItem("Settings", SymbolRegular.Settings24, typeof(SettingsPage)),
    };

    public void OnNavigationSelectionChanged(object sender, RoutedEventArgs e)
    {
    }
}
