using HerdMe.Windows.Models;
using HerdMe.Windows.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace HerdMe.Windows.Views;

public sealed partial class OnboardingView : UserControl
{
    private readonly InitialSetupManager setupManager = new();
    private bool isRunning;

    public OnboardingView()
    {
        InitializeComponent();
    }

    public event EventHandler? SetupCompleted;

    private async void Start_Click(object sender, RoutedEventArgs e)
    {
        if (isRunning) return;
        isRunning = true;
        ShowOnly(ProgressPanel);
        UpdateProgress(InitialSetupStage.LocalDomains);
        try
        {
            var progress = new Progress<InitialSetupStage>(UpdateProgress);
            await setupManager.RunAsync(progress);
            ShowOnly(CompletedPanel);
        }
        catch (Exception error)
        {
            FailureText.Text = error.Message;
            ShowOnly(FailurePanel);
        }
        finally
        {
            isRunning = false;
        }
    }

    private void Continue_Click(object sender, RoutedEventArgs e)
    {
        SetupCompleted?.Invoke(this, EventArgs.Empty);
    }

    private void UpdateProgress(InitialSetupStage stage)
    {
        if (stage == InitialSetupStage.Completed) return;
        StageTitleText.Text = InitialSetupStages.Title(stage);
        StageDetailText.Text = InitialSetupStages.Detail(stage);
        var index = InitialSetupStages.Installation.ToList().IndexOf(stage);
        if (index < 0)
        {
            SetupProgress.Value = 0;
            StepText.Text = "Preparing";
            return;
        }
        SetupProgress.Value = 100.0 * (index + 1) / InitialSetupStages.Installation.Count;
        StepText.Text = $"Step {index + 1} of {InitialSetupStages.Installation.Count}";
    }

    private void ShowOnly(FrameworkElement visible)
    {
        WelcomePanel.Visibility = Visibility.Collapsed;
        ProgressPanel.Visibility = Visibility.Collapsed;
        FailurePanel.Visibility = Visibility.Collapsed;
        CompletedPanel.Visibility = Visibility.Collapsed;
        visible.Visibility = Visibility.Visible;
    }
}
