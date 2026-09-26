using System.ComponentModel;
using System.Diagnostics;
using HerdMe.Windows.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;

namespace HerdMe.Windows.Pages;

/// <summary>
/// Proxy sites: https://name.test/ forwarded to a development server that already listens
/// on a loopback port. Requests, WebSockets, and streamed responses go through the same
/// HTTPS listener as folder sites, so the browser never sees a port.
/// </summary>
public sealed partial class SitesPage
{
    private async void ProxySites_Click(object sender, RoutedEventArgs e)
    {
        if (XamlRoot is null) return;
        var tld = settingsStore.Load().Tld;
        string? editingName = null;
        var busy = false;

        var description = new TextBlock
        {
            Text = AppLocalization.Get("SitesProxyDialogDescription"),
            TextWrapping = TextWrapping.WrapWholeWords,
            Style = (Style)Application.Current.Resources["SettingsRowDescriptionStyle"]
        };
        var nameBox = new TextBox
        {
            Header = AppLocalization.Get("SitesProxyNameHeader"),
            PlaceholderText = "myapp",
            MinWidth = 180
        };
        var targetBox = new TextBox
        {
            Header = AppLocalization.Get("SitesProxyTargetHeader"),
            PlaceholderText = "localhost:3000",
            MinWidth = 180,
            FlowDirection = FlowDirection.LeftToRight
        };
        var saveButton = new Button
        {
            Content = AppLocalization.Get("SitesProxyAddButton"),
            VerticalAlignment = VerticalAlignment.Bottom,
            Style = (Style)Application.Current.Resources["AccentButtonStyle"]
        };
        var form = new Grid { ColumnSpacing = 8 };
        form.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        form.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        form.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        Grid.SetColumn(targetBox, 1);
        Grid.SetColumn(saveButton, 2);
        form.Children.Add(nameBox);
        form.Children.Add(targetBox);
        form.Children.Add(saveButton);
        var preview = new TextBlock
        {
            Style = (Style)Application.Current.Resources["CaptionTextStyle"],
            TextWrapping = TextWrapping.Wrap
        };
        var errorBar = new InfoBar { Severity = InfoBarSeverity.Error, IsOpen = false, IsClosable = true };
        var rows = new StackPanel { Spacing = 6 };
        var emptyText = new TextBlock
        {
            Text = AppLocalization.Get("SitesProxyEmpty"),
            Style = (Style)Application.Current.Resources["SettingsRowDescriptionStyle"]
        };
        var content = new StackPanel { Spacing = 12, MinWidth = 520 };
        content.Children.Add(description);
        content.Children.Add(form);
        content.Children.Add(preview);
        content.Children.Add(errorBar);
        content.Children.Add(emptyText);
        content.Children.Add(new ScrollViewer { Content = rows, MaxHeight = 300 });

        var dialog = new ContentDialog
        {
            FlowDirection = AppLocalization.LayoutDirection,
            XamlRoot = XamlRoot,
            Title = AppLocalization.Get("SitesProxyDialogTitle"),
            Content = content,
            CloseButtonText = AppLocalization.Get("SitesProxyCloseButton")
        };

        void UpdatePreview()
        {
            var name = ProxySiteStore.NormalizeName(nameBox.Text);
            preview.Text = name is not null && ProxySiteStore.TryParseTarget(targetBox.Text, out var port)
                ? AppLocalization.Format("SitesProxyPreview", $"https://{name}.{tld}/", port)
                : string.Empty;
        }

        void ResetForm()
        {
            editingName = null;
            nameBox.Text = string.Empty;
            targetBox.Text = string.Empty;
            saveButton.Content = AppLocalization.Get("SitesProxyAddButton");
            UpdatePreview();
        }

        void ShowError(string message)
        {
            errorBar.Message = message;
            errorBar.IsOpen = true;
        }

        void Rebuild()
        {
            rows.Children.Clear();
            var proxies = proxySites.Load();
            emptyText.Visibility = proxies.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
            foreach (var proxy in proxies)
            {
                rows.Children.Add(ProxyRow(proxy));
            }
        }

        async Task ApplyAsync(Action change)
        {
            if (busy) return;
            busy = true;
            saveButton.IsEnabled = false;
            errorBar.IsOpen = false;
            try
            {
                change();
                Rebuild();
                ResetForm();
                await environment.ReloadProxySitesAsync(tld);
                UpdateEnvironmentState();
            }
            catch (Exception error) when (error is not OperationCanceledException)
            {
                ShowError(UserErrorPresentation.Describe(error));
                Rebuild();
            }
            finally
            {
                busy = false;
                saveButton.IsEnabled = true;
            }
        }

        Grid ProxyRow(ProxySite proxy)
        {
            var url = $"https://{proxy.Domain(tld)}/";
            var row = new Grid { ColumnSpacing = 4 };
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            for (var column = 0; column < 4; column++)
            {
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            }
            var label = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
            label.Children.Add(new TextBlock { Text = url, IsTextSelectionEnabled = true });
            label.Children.Add(new TextBlock
            {
                Text = AppLocalization.Format("SitesProxyTargetLabel", proxy.Port),
                Style = (Style)Application.Current.Resources["CaptionTextStyle"]
            });
            row.Children.Add(label);
            AddProxyRowButton(row, 1, Symbol.Globe, "SitesProxyOpenButton", () =>
            {
                try
                {
                    Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
                }
                catch (Exception error) when (error is Win32Exception or InvalidOperationException)
                {
                    ShowError(error.Message);
                }
                return Task.CompletedTask;
            });
            AddProxyRowButton(row, 2, Symbol.Copy, "SitesProxyCopyButton", () =>
            {
                CopyText(url);
                return Task.CompletedTask;
            });
            AddProxyRowButton(row, 3, Symbol.Edit, "SitesProxyEditButton", () =>
            {
                editingName = proxy.Name;
                nameBox.Text = proxy.Name;
                targetBox.Text = $"localhost:{proxy.Port}";
                saveButton.Content = AppLocalization.Get("SitesProxySaveButton");
                UpdatePreview();
                return Task.CompletedTask;
            });
            AddProxyRowButton(row, 4, Symbol.Delete, "SitesProxyRemoveButton", () =>
                ApplyAsync(() => proxySites.Remove(proxy.Name)));
            return row;
        }

        nameBox.TextChanged += (_, _) => UpdatePreview();
        targetBox.TextChanged += (_, _) => UpdatePreview();
        saveButton.Click += async (_, _) =>
        {
            var name = ProxySiteStore.NormalizeName(nameBox.Text);
            if (name is null)
            {
                ShowError(AppLocalization.Get("SitesProxyInvalidName"));
                return;
            }
            if (!ProxySiteStore.TryParseTarget(targetBox.Text, out var port))
            {
                ShowError(AppLocalization.Get("SitesProxyInvalidTarget"));
                return;
            }
            var reserved = ReservedProxyNames(tld);
            if (reserved.Contains(name, StringComparer.OrdinalIgnoreCase))
            {
                ShowError(AppLocalization.Format("SitesProxyNameTaken", name));
                return;
            }
            var existing = proxySites.Load();
            var replacing = existing.Any(item => item.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
            if (!replacing && existing.Count >= ProxySiteStore.MaximumProxySites)
            {
                ShowError(AppLocalization.Format("SitesProxyLimit", ProxySiteStore.MaximumProxySites));
                return;
            }
            var previousName = editingName;
            await ApplyAsync(() =>
            {
                proxySites.Save(new ProxySite(name, port), reserved);
                if (previousName is not null
                    && !previousName.Equals(name, StringComparison.OrdinalIgnoreCase))
                {
                    proxySites.Remove(previousName);
                }
            });
        };

        Rebuild();
        ResetForm();
        await dialog.ShowAsync();
    }

    private void AddProxyRowButton(Grid row, int column, Symbol symbol, string labelKey, Func<Task> action)
    {
        var label = AppLocalization.Get(labelKey);
        var button = new Button
        {
            Content = new SymbolIcon(symbol),
            Style = (Style)Resources["Sites" + "IconButtonStyle"]
        };
        AutomationProperties.SetName(button, label);
        ToolTipService.SetToolTip(button, label);
        button.Click += async (_, _) => await action();
        Grid.SetColumn(button, column);
        row.Children.Add(button);
    }

    /// <summary>Names already used by scanned folder sites; those always win over a proxy.</summary>
    private string[] ReservedProxyNames(string tld)
    {
        var suffix = "." + tld;
        return Sites
            .Select(site => site.Domain.Trim().TrimEnd('.'))
            .Where(domain => domain.EndsWith(suffix, StringComparison.OrdinalIgnoreCase))
            .Select(domain => domain[..^suffix.Length])
            .ToArray();
    }
}
