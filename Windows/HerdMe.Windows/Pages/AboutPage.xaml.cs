using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace HerdMe.Windows.Pages;

public sealed partial class AboutPage : Page
{
    public AboutPage()
    {
        InitializeComponent();
        var version = typeof(AboutPage).Assembly.GetName().Version ?? new Version(0, 0, 0, 0);
        VersionText.Text = $"Version {version.Major}.{version.Minor}.{Math.Max(version.Build, 0)} (Build {Math.Max(version.Revision, 0)})";
    }

    private async void License_Click(object sender, Microsoft.UI.Xaml.RoutedEventArgs e)
    {
        await ShowDocumentAsync("MIT License", "LICENSE");
    }

    private async void Acknowledgements_Click(object sender, Microsoft.UI.Xaml.RoutedEventArgs e)
    {
        await ShowDocumentAsync("Acknowledgements", "THIRD_PARTY.md");
    }

    private async Task ShowDocumentAsync(string title, string fileName)
    {
        var path = Path.Combine(AppContext.BaseDirectory, fileName);
        var contents = File.Exists(path)
            ? await File.ReadAllTextAsync(path)
            : "The bundled document could not be loaded.";
        var text = new TextBlock
        {
            Text = contents,
            TextWrapping = Microsoft.UI.Xaml.TextWrapping.Wrap,
            IsTextSelectionEnabled = true,
            FontFamily = new FontFamily("Consolas")
        };
        var dialog = new ContentDialog
        {
            XamlRoot = XamlRoot,
            Title = title,
            Content = new ScrollViewer
            {
                Content = text,
                MaxHeight = 520,
                MaxWidth = 760
            },
            CloseButtonText = "Done"
        };
        await dialog.ShowAsync();
    }
}
