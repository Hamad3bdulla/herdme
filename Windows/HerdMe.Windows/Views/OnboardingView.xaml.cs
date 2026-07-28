using HerdMe.Windows.Models;
using HerdMe.Windows.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Windows.ApplicationModel.DataTransfer;

namespace HerdMe.Windows.Views;

public sealed partial class OnboardingView : UserControl
{
    private InitialSetupManager? setupManager;
    private bool isRunning;

    public OnboardingView()
    {
        InitializeComponent();
        SetupSummaryText.Text = AppLocalization.Format(
            "OnboardingSetupSummary.Text",
            RuntimeCatalog.DefaultPhpCycle,
            RuntimeCatalog.DefaultNodeMajor
        );
    }

    public event EventHandler? SetupCompleted;

    public void Configure(InitialSetupManager setupManager)
    {
        this.setupManager = setupManager;
    }

    private async void Start_Click(object sender, RoutedEventArgs e)
    {
        if (isRunning || setupManager is null) return;
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
            FailureDetailsText.Text = error.ToString();
            FailureDetailsExpander.IsExpanded = false;
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

    private void CopyFailureDetails_Click(object sender, RoutedEventArgs e)
    {
        var package = new DataPackage();
        package.SetText(FailureDetailsText.Text);
        Clipboard.SetContent(package);
        Clipboard.Flush();
    }

    private void UpdateProgress(InitialSetupStage stage)
    {
        if (stage == InitialSetupStage.Completed) return;
        StageTitleText.Text = stage switch
        {
            InitialSetupStage.Php => AppLocalization.Format(
                "OnboardingStagePhpTitle",
                RuntimeCatalog.DefaultPhpCycle
            ),
            InitialSetupStage.Node => AppLocalization.Format(
                "OnboardingStageNodeTitle",
                RuntimeCatalog.DefaultNodeMajor
            ),
            _ => AppLocalization.Get($"OnboardingStage{stage}Title")
        };
        StageDetailText.Text = AppLocalization.Get($"OnboardingStage{stage}Detail");
        var index = InitialSetupStages.Installation.ToList().IndexOf(stage);
        if (index < 0)
        {
            SetupProgress.Value = 0;
            StepText.Text = AppLocalization.Get("OnboardingPreparing");
            return;
        }
        SetupProgress.Value = 100.0 * (index + 1) / InitialSetupStages.Installation.Count;
        StepText.Text = AppLocalization.Format(
            "OnboardingStepProgress",
            index + 1,
            InitialSetupStages.Installation.Count
        );
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
