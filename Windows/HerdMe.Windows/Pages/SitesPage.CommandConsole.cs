using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using HerdMe.Windows.Services;

namespace HerdMe.Windows.Pages;

/// <summary>
/// Shared run/cancel/status/output surface for the Artisan and npm command dialogs. Each dialog
/// keeps its own inputs; this owns the controls and the running state they have in common.
/// </summary>
public sealed partial class SitesPage
{
    private CommandConsole? activeCommandConsole;

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
            OutputBox = new TextBox
            {
                Text = AppLocalization.Get(placeholderKey),
                IsReadOnly = true,
                AcceptsReturn = true,
                TextWrapping = TextWrapping.NoWrap,
                Height = 240,
                VerticalAlignment = VerticalAlignment.Top,
                FontFamily = new Microsoft.UI.Xaml.Media.FontFamily("Consolas")
            };
            ScrollViewer.SetVerticalScrollBarVisibility(OutputBox, ScrollBarVisibility.Auto);
            ScrollViewer.SetHorizontalScrollBarVisibility(OutputBox, ScrollBarVisibility.Auto);
            RunButton = new Button
            {
                Content = AppLocalization.Get("SitesRun"),
                Style = (Style)Application.Current.Resources["AccentButtonStyle"]
            };
            CancelButton = new Button
            {
                Content = AppLocalization.Get("SitesCancel"),
                IsEnabled = false
            };
            CancelButton.Click += (_, _) => Cancel();
        }

        internal TextBlock StatusText { get; }

        internal TextBox OutputBox { get; }

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
            buttons.Children.Add(CancelButton);
            buttons.Children.Add(RunButton);
            var statusRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
            statusRow.Children.Add(progressRing);
            statusRow.Children.Add(StatusText);
            content.Children.Add(buttons);
            content.Children.Add(statusRow);
            content.Children.Add(OutputBox);
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

        internal void Append(string value) => AppendCommandOutput(OutputBox, value);

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
                setInputsEnabled(true);
                if (ReferenceEquals(cancellation, source)) cancellation = null;
            }
        }
    }
}
