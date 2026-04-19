using System;
using System.Windows;
using System.Windows.Controls;
using BetterES.ViewModel;
using Wpf.Ui;
using Wpf.Ui.Abstractions;
using Wpf.Ui.Controls;

namespace BetterES.View;

public partial class BetterESWindow : FluentWindow, INavigationWindow
{
    public MainWindowViewModel ViewModel { get; }

    public BetterESWindow(MainWindowViewModel viewModel, INavigationService navigationService, ISnackbarService snackbarService, IContentDialogService dialogService)
    {
        ViewModel = viewModel;
        DataContext = ViewModel;

        InitializeComponent();

        navigationService.SetNavigationControl(RootNavigation);
        snackbarService.SetSnackbarPresenter(AppSnackbar);
        dialogService.SetDialogHost(RootDialog);

        Application.Current.MainWindow = this;
        Loaded += (s, e) => WireTuningNavOpenPaneOnClick();
    }

    /// <summary>
    /// When the pane is collapsed and the Tuning parent is clicked,
    /// open the pane and expand its submenu — same pattern as EngineSimAutoRecorder.
    /// </summary>
    private void WireTuningNavOpenPaneOnClick()
    {
        foreach (object item in ViewModel.MenuItems)
        {
            if (item is not NavigationViewItem nvi) continue;

            string? tag = nvi.Tag as string;
            if (MainWindowViewModel.TuningNavigationRootTag.Equals(tag)
                || MainWindowViewModel.ConnectionNavigationRootTag.Equals(tag))
            {
                nvi.Click += (_, _) =>
                {
                    if (!RootNavigation.IsPaneOpen)
                    {
                        RootNavigation.IsPaneOpen = true;
                        nvi.IsExpanded = true;
                    }
                };
            }
        }
    }

    public INavigationView GetNavigation() => RootNavigation;
    public bool Navigate(Type pageType) => RootNavigation.Navigate(pageType);
    public void SetServiceProvider(IServiceProvider serviceProvider) { }
    public void SetPageService(INavigationViewPageProvider navigationViewPageProvider) =>
        RootNavigation.SetPageProviderService(navigationViewPageProvider);
    public void ShowWindow() => Show();
    public void CloseWindow() => Close();

    private void OnNavigationSelectionChanged(object sender, RoutedEventArgs e)
    {
        ViewModel.OnNavigationSelectionChanged(sender, e);
    }
}
