using System.Collections.ObjectModel;
using HerdMe.Windows.Models;
using HerdMe.Windows.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace HerdMe.Windows.Pages;

public sealed partial class DumpsPage : Page
{
    private readonly DumpCaptureService capture;

    public ObservableCollection<CapturedDump> Dumps { get; } = [];

    public DumpsPage(DumpCaptureService capture)
    {
        this.capture = capture;
        InitializeComponent();
    }

    private void Page_Loaded(object sender, RoutedEventArgs e)
    {
        capture.DumpCaptured += Capture_DumpCaptured;
        Reload();
        UpdateServerState();
    }

    private void Page_Unloaded(object sender, RoutedEventArgs e)
    {
        capture.DumpCaptured -= Capture_DumpCaptured;
    }

    private void Capture_DumpCaptured(object? sender, CapturedDump dump)
    {
        DispatcherQueue.TryEnqueue(() =>
        {
            Dumps.Insert(0, dump);
            DumpList.SelectedItem = dump;
        });
    }

    private async void Server_Click(object sender, RoutedEventArgs e)
    {
        ServerButton.IsEnabled = false;
        try
        {
            if (capture.IsRunning) await capture.StopAsync();
            else await capture.StartAsync();
        }
        catch (Exception error)
        {
            var dialog = new ContentDialog
            {
                XamlRoot = XamlRoot,
                Title = "HerdMe",
                Content = error.Message,
                CloseButtonText = AppLocalization.Get("CommonOk")
            };
            await dialog.ShowAsync();
        }
        finally
        {
            ServerButton.IsEnabled = true;
            UpdateServerState();
        }
    }

    private void DumpList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        ShowDump(DumpList.SelectedItem as CapturedDump);
    }

    private void Clear_Click(object sender, RoutedEventArgs e)
    {
        capture.Clear();
        Dumps.Clear();
        ShowDump(null);
    }

    private void Reload()
    {
        Dumps.Clear();
        foreach (var dump in capture.Load()) Dumps.Add(dump);
        DumpList.SelectedItem = Dumps.FirstOrDefault();
    }

    private void ShowDump(CapturedDump? dump)
    {
        SourceText.Text = dump?.Source ?? AppLocalization.Get("DumpsSelectDump");
        SummaryText.Text = dump?.Summary ?? string.Empty;
    }

    private void UpdateServerState()
    {
        ServerStatusText.Text = capture.IsRunning
            ? AppLocalization.Format("DumpsRunningOn", capture.Port)
            : AppLocalization.Get("DumpsStopped");
        ServerButtonIcon.Symbol = capture.IsRunning ? Symbol.Stop : Symbol.Play;
    }
}
