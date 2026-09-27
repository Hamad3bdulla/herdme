using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using HerdMe.Windows.Models;
using HerdMe.Windows.Services;
using HerdMe.Windows.Views;
using HerdMe.Windows.ViewModels;
using Microsoft.Web.WebView2.Core;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Windows.ApplicationModel.DataTransfer;
using Windows.Storage.Pickers;
using WinRT.Interop;

namespace HerdMe.Windows.Pages;

public sealed partial class SitesPage
{
    private async void CreateLaravel_Click(object sender, RoutedEventArgs e)
    {
        var parent = RootList.SelectedItem as string ?? Roots.FirstOrDefault();
        if (parent is null)
        {
            await ShowErrorAsync(AppLocalization.Get("SitesAddFolderBeforeCreate"));
            return;
        }
        var nameBox = new TextBox
        {
            Header = AppLocalization.Get("SitesProjectNameField"),
            PlaceholderText = "my-app"
        };
        var starterBox = CreateTemplatePicker();
        var customStarterBox = new TextBox
        {
            Header = AppLocalization.Get("SitesCustomComposerPackageField"),
            PlaceholderText = "vendor/package",
            Visibility = Visibility.Collapsed
        };
        var templateLink = new HyperlinkButton
        {
            Content = AppLocalization.Get("SitesTemplateLearnMore"),
            Padding = new Thickness(0)
        };
        void UpdateTemplateDetails()
        {
            var template = SelectedTemplate(starterBox);
            customStarterBox.Visibility = template.Group == ProjectTemplateGroup.Custom
                ? Visibility.Visible
                : Visibility.Collapsed;
            templateLink.NavigateUri = new Uri(template.DocumentationUrl);
            Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(
                templateLink,
                AppLocalization.Format("SitesTemplateLearnMoreAbout", AppLocalization.Get(template.NameKey))
            );
        }
        starterBox.SelectionChanged += (_, _) => UpdateTemplateDetails();
        UpdateTemplateDetails();
        var testingOptions = new[]
        {
            new DisplayOption("Pest", "Pest"),
            new DisplayOption("PHPUnit", "PHPUnit")
        };
        var testingBox = new ComboBox
        {
            Header = AppLocalization.Get("SitesTestingFrameworkField"),
            ItemsSource = testingOptions,
            SelectedIndex = 0,
            HorizontalAlignment = HorizontalAlignment.Stretch
        };
        var boostToggle = new ToggleSwitch
        {
            Header = AppLocalization.Get("SitesInstallBoostField"),
            IsOn = true
        };
        var gitToggle = new ToggleSwitch
        {
            Header = AppLocalization.Get("SitesInitializeGitField")
        };
        var content = new StackPanel { Spacing = 12, MinWidth = 380, MaxWidth = 480 };
        content.Children.Add(new TextBlock
        {
            Text = AppLocalization.Format("SitesProjectLocation", parent),
            TextWrapping = TextWrapping.Wrap,
            Style = StatusStyles.Text(StatusTone.Neutral)
        });
        content.Children.Add(nameBox);
        content.Children.Add(starterBox);
        content.Children.Add(templateLink);
        content.Children.Add(customStarterBox);
        content.Children.Add(testingBox);
        content.Children.Add(boostToggle);
        content.Children.Add(gitToggle);
        var dialog = new ContentDialog
        {
            FlowDirection = AppLocalization.LayoutDirection,
            XamlRoot = XamlRoot,
            Title = AppLocalization.Get("SitesCreateLaravelDialogTitle"),
            Content = new ScrollViewer
            {
                Content = content,
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                VerticalScrollMode = ScrollMode.Auto
            },
            PrimaryButtonText = AppLocalization.Get("SitesCreate"),
            CloseButtonText = AppLocalization.Get("SitesCancel"),
            DefaultButton = ContentDialogButton.Primary
        };
        if (await dialog.ShowAsync() != ContentDialogResult.Primary) return;

        var selectedTemplate = SelectedTemplate(starterBox);
        var request = new LaravelProjectRequest(
            nameBox.Text,
            parent,
            selectedTemplate.Id,
            (testingBox.SelectedItem as DisplayOption)?.Value ?? "Pest",
            boostToggle.IsOn,
            gitToggle.IsOn,
            selectedTemplate.Group == ProjectTemplateGroup.Custom ? customStarterBox.Text : null
        );
        var stages = LaravelProjectCreationStages.For(request);
        var statusText = new TextBlock
        {
            TextWrapping = TextWrapping.Wrap,
            Text = LocalizedStageDetail(stages[0])
        };
        var stageRows = new Dictionary<LaravelProjectCreationStage, TextBlock>();
        var progressContent = new StackPanel { Spacing = 10, MinWidth = 420 };
        var progressRing = new ProgressRing { IsActive = true, Width = 20, Height = 20 };
        var progressHeader = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 10 };
        progressHeader.Children.Add(progressRing);
        progressHeader.Children.Add(statusText);
        progressContent.Children.Add(progressHeader);
        foreach (var stage in stages)
        {
            var row = new TextBlock { TextWrapping = TextWrapping.Wrap };
            stageRows.Add(stage, row);
            progressContent.Children.Add(row);
        }
        var progressDialog = new ContentDialog
        {
            FlowDirection = AppLocalization.LayoutDirection,
            XamlRoot = XamlRoot,
            Title = AppLocalization.Get("SitesCreatingLaravelDialogTitle"),
            CloseButtonText = AppLocalization.Get("SitesCancel"),
            Content = new ScrollViewer
            {
                Content = progressContent,
                MaxHeight = 520,
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                VerticalScrollMode = ScrollMode.Auto
            }
        };
        var currentStage = stages[0];
        UpdateProjectCreationProgress(stages, stageRows, statusText, currentStage);
        var creationProgress = new Progress<LaravelProjectCreationStage>(stage =>
        {
            currentStage = stage;
            UpdateProjectCreationProgress(stages, stageRows, statusText, stage);
        });
        using var cancellation = new CancellationTokenSource();
        projectCreationCancellation = cancellation;
        var creationFinished = false;
        progressDialog.Closing += (_, args) =>
        {
            if (creationFinished) return;
            args.Cancel = true;
            if (cancellation.IsCancellationRequested) return;
            cancellation.Cancel();
            statusText.Text = AppLocalization.Get("SitesCancellingProjectCreation");
            progressDialog.CloseButtonText = string.Empty;
        };

        CreateLaravelButton.IsEnabled = false;
        ScanProgress.IsActive = true;
        var progressDialogOperation = progressDialog.ShowAsync();
        await Task.Yield();
        var elapsed = Stopwatch.StartNew();
        var operationName = AppLocalization.Format("SitesProjectCreationOperationName", request.Name.Trim());
        void ReportFinished(OperationOutcome outcome, string? error, NotificationAction? primary) =>
            App.MainWindow.ReportLongOperationFinished(
                new FinishedOperation("create-project:" + request.Name.Trim(), operationName, outcome, elapsed.Elapsed, error),
                primary
            );
        try
        {
            var createdPath = await projectCreator.CreateAsync(request, creationProgress, cancellation.Token);
            currentStage = LaravelProjectCreationStage.RegisteringSite;
            UpdateProjectCreationProgress(stages, stageRows, statusText, currentStage);
            await ScanAsync(throwOnError: true);
            currentStage = LaravelProjectCreationStage.Completed;
            UpdateProjectCreationProgress(stages, stageRows, statusText, currentStage);
            progressRing.IsActive = false;
            progressDialog.Title = AppLocalization.Get("SitesLaravelCreatedTitle");
            progressDialog.CloseButtonText = AppLocalization.Get("SitesDone");
            ReportFinished(OperationOutcome.Succeeded, null, NotificationActions.OpenSite(createdPath));
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        {
            UpdateProjectCreationFailure(stageRows, currentStage);
            progressRing.IsActive = false;
            progressDialog.Title = AppLocalization.Get("SitesLaravelCreationCancelledTitle");
            statusText.Text = AppLocalization.Get("SitesLaravelCreationCancelledMessage");
            progressDialog.CloseButtonText = AppLocalization.Get("SitesClose");
        }
        catch (Exception error)
        {
            UpdateProjectCreationFailure(stageRows, currentStage);
            progressRing.IsActive = false;
            progressDialog.Title = AppLocalization.Get("SitesLaravelCreationFailedTitle");
            var failure = CommandErrorPresenter.Present(
                error.Message,
                AppLocalization.Get("SitesLaravelCreationFallback")
            );
            statusText.Text = failure.Message;
            ReportFinished(OperationOutcome.Failed, failure.Message, null);
            if (failure.TechnicalDetails is not null)
            {
                progressContent.Children.Add(new Expander
                {
                    Header = AppLocalization.Get("SitesTechnicalDetails"),
                    IsExpanded = false,
                    Content = new ScrollViewer
                    {
                        MaxHeight = 160,
                        Content = new TextBlock
                        {
                            Text = failure.TechnicalDetails,
                            TextWrapping = TextWrapping.Wrap,
                            IsTextSelectionEnabled = true
                        }
                    }
                });
            }
            progressDialog.CloseButtonText = AppLocalization.Get("SitesClose");
        }
        finally
        {
            creationFinished = true;
            if (ReferenceEquals(projectCreationCancellation, cancellation))
            {
                projectCreationCancellation = null;
            }
            ScanProgress.IsActive = false;
            CreateLaravelButton.IsEnabled = true;
        }
        await progressDialogOperation;
    }

    // Every template is listed with a one-line description so the choice is made before creating.
    private static ListView CreateTemplatePicker()
    {
        var list = new ListView
        {
            Header = AppLocalization.Get("SitesStarterKitField"),
            SelectionMode = ListViewSelectionMode.Single,
            MaxHeight = 300,
            HorizontalAlignment = HorizontalAlignment.Stretch
        };
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(list, AppLocalization.Get("SitesStarterKitField"));
        foreach (var template in ProjectTemplateCatalog.All)
        {
            var name = AppLocalization.Get(template.NameKey);
            var description = AppLocalization.Get(template.DescriptionKey);
            var title = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
            title.Children.Add(new TextBlock
            {
                Text = name,
                FontWeight = Microsoft.UI.Text.FontWeights.SemiBold
            });
            title.Children.Add(new TextBlock
            {
                Text = AppLocalization.Get($"SitesTemplateGroup{template.Group}"),
                Style = StatusStyles.Text(StatusTone.Neutral),
                VerticalAlignment = VerticalAlignment.Center
            });
            var body = new StackPanel { Spacing = 2, Padding = new Thickness(0, 6, 0, 6) };
            body.Children.Add(title);
            body.Children.Add(new TextBlock
            {
                Text = description,
                TextWrapping = TextWrapping.Wrap,
                Style = StatusStyles.Text(StatusTone.Neutral)
            });
            var item = new ListViewItem { Content = body, Tag = template.Id };
            Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(item, name);
            Microsoft.UI.Xaml.Automation.AutomationProperties.SetHelpText(item, description);
            list.Items.Add(item);
        }
        list.SelectedIndex = 0;
        return list;
    }

    private static ProjectTemplate SelectedTemplate(ListView list) =>
        ProjectTemplateCatalog.Find((list.SelectedItem as ListViewItem)?.Tag as string)
            ?? ProjectTemplateCatalog.All[0];

    private static void UpdateProjectCreationProgress(
        IReadOnlyList<LaravelProjectCreationStage> stages,
        IReadOnlyDictionary<LaravelProjectCreationStage, TextBlock> rows,
        TextBlock statusText,
        LaravelProjectCreationStage current
    )
    {
        var currentIndex = 0;
        while (currentIndex < stages.Count && stages[currentIndex] != current) currentIndex++;
        for (var index = 0; index < stages.Count; index++)
        {
            var stage = stages[index];
            var prefix = AppLocalization.Get(
                index < currentIndex
                    ? "SitesProgressCompleted"
                    : index == currentIndex ? "SitesProgressInProgress" : "SitesProgressPending"
            );
            rows[stage].Text = AppLocalization.Format(
                "SitesProgressRow",
                prefix,
                LocalizedStageTitle(stage)
            );
            rows[stage].FontWeight = index == currentIndex
                ? Microsoft.UI.Text.FontWeights.SemiBold
                : Microsoft.UI.Text.FontWeights.Normal;
        }
        statusText.Text = LocalizedStageDetail(current);
    }

    private static void UpdateProjectCreationFailure(
        IReadOnlyDictionary<LaravelProjectCreationStage, TextBlock> rows,
        LaravelProjectCreationStage current
    )
    {
        rows[current].Text = AppLocalization.Format(
            "SitesProgressRow",
            AppLocalization.Get("SitesProgressFailed"),
            LocalizedStageTitle(current)
        );
        rows[current].FontWeight = Microsoft.UI.Text.FontWeights.SemiBold;
    }

    private static string LocalizedStageTitle(LaravelProjectCreationStage stage)
    {
        return AppLocalization.Get(LaravelProjectCreationStages.TitleKey(stage));
    }

    private static string LocalizedStageDetail(LaravelProjectCreationStage stage)
    {
        return AppLocalization.Get(LaravelProjectCreationStages.DetailKey(stage));
    }
}
