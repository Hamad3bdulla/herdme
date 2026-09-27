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
    private InitialSetupStage currentStage = InitialSetupStage.Welcome;

    public OnboardingView()
    {
        InitializeComponent();
        SetupSummaryText.Text = AppLocalization.Format(
            "OnboardingSetupSummaryText",
            RuntimeCatalog.DefaultPhpCycle,
            RuntimeCatalog.DefaultNodeMajor
        );
        ExplainRuntimesText.Text = AppLocalization.Format(
            "OnboardingExplainRuntimesText",
            RuntimeCatalog.DefaultPhpCycle,
            RuntimeCatalog.DefaultNodeMajor
        );
    }

    public event EventHandler? SetupCompleted;

    // Read by the main window when SetupCompleted is raised.
    public OnboardingNextStep RequestedNextStep { get; private set; }

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
            FailureText.Text = AppLocalization.Format(
                "OnboardingFailureMessage",
                LocalizedStageTitle(currentStage)
            );
            FailureDetailsText.Text = error.ToString();
            FailureDetailsExpander.IsExpanded = false;
            ShowOnly(FailurePanel);
        }
        finally
        {
            isRunning = false;
        }
    }

    private void LearnMore_Click(object sender, RoutedEventArgs e)
    {
        if (!isRunning) ShowOnly(ExplainPanel);
    }

    private void Back_Click(object sender, RoutedEventArgs e)
    {
        if (!isRunning) ShowOnly(WelcomePanel);
    }

    private void Continue_Click(object sender, RoutedEventArgs e) => Finish(OnboardingNextStep.None);

    private void CreateLaravel_Click(object sender, RoutedEventArgs e) => Finish(OnboardingNextStep.CreateLaravel);

    private void ParkFolder_Click(object sender, RoutedEventArgs e) => Finish(OnboardingNextStep.ParkFolder);

    private void Finish(OnboardingNextStep nextStep)
    {
        RequestedNextStep = nextStep;
        SetupCompleted?.Invoke(this, EventArgs.Empty);
    }

    private void CopyFailureDetails_Click(object sender, RoutedEventArgs e)
    {
        var package = new DataPackage();
        package.SetText(FailureDetailsText.Text);
        Clipboard.SetContent(package);
        Clipboard.Flush();
        App.MainWindow.ShowToast(AppLocalization.Get("CommonCopiedToast"));
    }

    private void UpdateProgress(InitialSetupStage stage)
    {
        if (stage == InitialSetupStage.Completed) return;
        currentStage = stage;
        StageTitleText.Text = LocalizedStageTitle(stage);
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

    private static string LocalizedStageTitle(InitialSetupStage stage)
    {
        return stage switch
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
    }

    private void ShowOnly(FrameworkElement visible)
    {
        WelcomePanel.Visibility = Visibility.Collapsed;
        ExplainPanel.Visibility = Visibility.Collapsed;
        ProgressPanel.Visibility = Visibility.Collapsed;
        FailurePanel.Visibility = Visibility.Collapsed;
        CompletedPanel.Visibility = Visibility.Collapsed;
        visible.Visibility = Visibility.Visible;
    }
}
