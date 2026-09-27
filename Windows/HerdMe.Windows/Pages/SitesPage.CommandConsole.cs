using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using HerdMe.Windows.Services;
using HerdMe.Windows.Views;

namespace HerdMe.Windows.Pages;

/// <summary>
/// Shared run/cancel/status/output surface for the Artisan and npm command dialogs. Each dialog
/// keeps its own inputs; this owns the controls and the running state they have in common.
/// </summary>
public sealed partial class SitesPage
{
    private CommandConsole? activeCommandConsole;

    private static StackPanel CommandButtonContent(Symbol symbol, string label)
    {
        var panel = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        panel.Children.Add(new SymbolIcon(symbol));
        panel.Children.Add(new TextBlock { Text = label });
        return panel;
    }

    private sealed class CommandConsole
    {
        private readonly string cancellingKey;
        private readonly ProgressRing progressRing = new() { Width = 18, Height = 18 };
        private CancellationTokenSource? cancellation;

        internal CommandConsole(string readyKey, string placeholderKey, string cancellingKey)
        {
            this.cancellingKey = cancellingKey;
            StatusText = new TextBlock
            {
                Text = AppLocalization.Get(readyKey),
                FontWeight = Microsoft.UI.Text.FontWeights.SemiBold
            };
            // Output keeps ANSI colours (Artisan runs with --ansi); see ConsoleOutputView.
            OutputBox = new ConsoleOutputView(240) { Text = AppLocalization.Get(placeholderKey) };
            RunButton = new Button
            {
                Content = AppLocalization.Get("SitesRun"),
                Style = (Style)Application.Current.Resources["AccentButtonStyle"]
            };
            // Stop is destructive for the running command only; it is red while it can act.
            CancelButton = new Button
            {
                Content = CommandButtonContent(Symbol.Stop, AppLocalization.Get("SitesConsoleStop")),
                IsEnabled = false
            };
            Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(CancelButton, AppLocalization.Get("SitesConsoleStop"));
            CancelButton.Click += (_, _) => Cancel();
            CopyButton = new Button
            {
                Content = new SymbolIcon(Symbol.Copy),
                Style = (Style)Application.Current.Resources["ToolbarIconButtonStyle"]
            };
            Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(CopyButton, AppLocalization.Get("SitesConsoleCopy"));
            ToolTipService.SetToolTip(CopyButton, AppLocalization.Get("SitesConsoleCopy"));
            CopyButton.Click += (_, _) =>
            {
                var package = new global::Windows.ApplicationModel.DataTransfer.DataPackage();
                package.SetText(OutputBox.Text);
                global::Windows.ApplicationModel.DataTransfer.Clipboard.SetContent(package);
                App.MainWindow.ShowToast(AppLocalization.Get("CommonCopiedToast"));
            };
        }

        internal TextBlock StatusText { get; }

        internal ConsoleOutputView OutputBox { get; }

        internal Button CopyButton { get; }

        internal bool HasRun { get; private set; }

        internal Button RunButton { get; }

        internal Button CancelButton { get; }

        internal bool IsRunning { get; private set; }

        /// <summary>Shows the progress ring for work that is not a command run (e.g. reloading).</summary>
        internal void SetBusy(bool busy) => progressRing.IsActive = busy;

        /// <summary>Adds the buttons, status row, and output box below the dialog's own inputs.</summary>
        internal void AddTo(StackPanel content)
        {
            var buttons = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Spacing = 8,
                HorizontalAlignment = HorizontalAlignment.Right
            };
            buttons.Children.Add(CopyButton);
            buttons.Children.Add(CancelButton);
            buttons.Children.Add(RunButton);
            var statusRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
            statusRow.Children.Add(progressRing);
            statusRow.Children.Add(StatusText);
            content.Children.Add(buttons);
            content.Children.Add(statusRow);
            content.Children.Add(OutputBox.Scroll);
        }

        /// <summary>Closing the dialog while a command runs cancels it instead of hiding it.</summary>
        internal void AttachTo(ContentDialog dialog)
        {
            dialog.Closing += (_, args) =>
            {
                if (!IsRunning) return;
                args.Cancel = true;
                Cancel();
            };
        }

        internal void Cancel()
        {
            if (!IsRunning || cancellation is null) return;
            StatusText.Text = AppLocalization.Get(cancellingKey);
            cancellation.Cancel();
        }

        internal void Append(string value) => OutputBox.Append(value);

        internal IProgress<string> OutputProgress() => new Progress<string>(Append);

        /// <summary>
        /// Runs one command with the shared busy state. <paramref name="setInputsEnabled"/> locks
        /// the dialog's own inputs while it runs; <paramref name="run"/> reports status and output.
        /// A cancellation the user asked for ends with <paramref name="cancelledKey"/>.
        /// </summary>
        internal async Task RunAsync(
            string startingStatus,
            string cancelledKey,
            Action<bool> setInputsEnabled,
            Func<CancellationToken, Task> run
        )
        {
            if (IsRunning) return;
            using var source = new CancellationTokenSource();
            cancellation = source;
            IsRunning = true;
            RunButton.IsEnabled = false;
            CancelButton.IsEnabled = true;
            CancelButton.Style = DangerStyles.Button;
            setInputsEnabled(false);
            progressRing.IsActive = true;
            StatusText.Text = startingStatus;
            OutputBox.Text = string.Empty;
            try
            {
                await run(source.Token);
            }
            catch (OperationCanceledException) when (source.IsCancellationRequested)
            {
                StatusText.Text = AppLocalization.Get(cancelledKey);
            }
            finally
            {
                IsRunning = false;
                progressRing.IsActive = false;
                RunButton.IsEnabled = true;
                CancelButton.IsEnabled = false;
                CancelButton.ClearValue(FrameworkElement.StyleProperty);
                // After the first run the same button re-runs the command with the same inputs.
                HasRun = true;
                RunButton.Content = CommandButtonContent(Symbol.Refresh, AppLocalization.Get("SitesConsoleRunAgain"));
                Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(RunButton, AppLocalization.Get("SitesConsoleRunAgain"));
                setInputsEnabled(true);
                if (ReferenceEquals(cancellation, source)) cancellation = null;
            }
        }
    }
}
