using HerdMe.Windows.Pages;
using Microsoft.UI;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Windows.Graphics;
using WinRT.Interop;

namespace HerdMe.Windows;

public sealed partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();
        ResizeWindow();
        Navigation.SelectedItem = Navigation.MenuItems[0];
    }

    private void ResizeWindow()
    {
        var windowHandle = WindowNative.GetWindowHandle(this);
        var windowId = Win32Interop.GetWindowIdFromWindow(windowHandle);
        AppWindow.GetFromWindowId(windowId).Resize(new SizeInt32(1_080, 720));
    }

    private void Navigation_SelectionChanged(
        NavigationView sender,
        NavigationViewSelectionChangedEventArgs args
    )
    {
        if (args.SelectedItemContainer?.Tag is not string tag)
        {
            return;
        }

        switch (tag)
        {
            case "general":
                ContentFrame.Navigate(typeof(GeneralPage));
                break;
            case "sites":
                ContentFrame.Navigate(typeof(SitesPage));
                break;
            case "php":
                ContentFrame.Navigate(typeof(PhpPage));
                break;
            case "node":
                ContentFrame.Navigate(typeof(NodePage));
                break;
            case "services":
                ContentFrame.Navigate(typeof(ServicesPage));
                break;
            case "mail":
                ContentFrame.Navigate(typeof(MailPage));
                break;
            case "dumps":
                ContentFrame.Navigate(typeof(DumpsPage));
                break;
            case "logs":
                ContentFrame.Navigate(typeof(LogsPage));
                break;
            case "debugger":
                ContentFrame.Navigate(typeof(DebuggerPage));
                break;
            case "about":
                ContentFrame.Navigate(typeof(AboutPage));
                break;
            default:
                ContentFrame.Navigate(typeof(GeneralPage));
                break;
        }
    }
}
