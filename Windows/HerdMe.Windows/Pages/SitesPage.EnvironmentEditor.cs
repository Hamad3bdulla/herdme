using HerdMe.Windows.Models;
using HerdMe.Windows.Services;
using HerdMe.Windows.Views;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace HerdMe.Windows.Pages;

// Per-site .env editor. The Variables view lists one row per key with secret values masked
// (PasswordBox) and adds keys through autocomplete (.env.example first, then common Laravel
// keys). The Text view edits the raw file. Saving always goes through a review of what
// changes, key by key, with secrets still masked.
public sealed partial class SitesPage
{
    private async void EditEnvironment_Click(object sender, RoutedEventArgs e)
    {
        var site = selectedSite;
        if (site is null) return;

        ProjectEnvironmentDocument document;
        string? example;
        try
        {
            (document, example) = await Task.Run(() => (
                ProjectEnvironmentFile.Load(site.Path),
                ProjectEnvironmentFile.LoadExample(site.Path)
            ));
        }
        catch (Exception error) when (error is IOException
            or UnauthorizedAccessException
            or InvalidDataException
            or InvalidOperationException)
        {
            await ShowErrorAsync(error.Message);
            return;
        }
        if (!IsSelected(site)) return;

        await new EnvironmentEditorSession(this, site, document, example).ShowAsync();
    }

    private sealed class EnvironmentEditorSession
    {
        private static readonly FontFamily Monospace = new("Consolas");

        private readonly SitesPage page;
        private readonly SiteRecord site;
        private readonly string? example;
        private ProjectEnvironmentDocument document;
        private string current;
        private bool reviewing;
        private bool discardArmed;
        private bool syncingEditor;

        private readonly ContentDialog dialog;
        private readonly TextBlock statusText;
        private readonly InfoBar problemsBar;
        private readonly InfoBar missingBar;
        private readonly SelectorBar views;
        private readonly SelectorBarItem variablesItem;
        private readonly Grid variablesPanel;
        private readonly StackPanel rowsPanel;
        private readonly AutoSuggestBox addBox;
        private readonly CheckBox revealBox;
        private readonly TextBox editor;
        private readonly StackPanel editPanel;
        private readonly StackPanel reviewPanel;
        private readonly StackPanel reviewLines;
        private readonly TextBlock reviewNote;

        public EnvironmentEditorSession(SitesPage page, SiteRecord site, ProjectEnvironmentDocument document, string? example)
        {
            this.page = page;
            this.site = site;
            this.document = document;
            this.example = example;
            current = document.Contents;

            var pathText = new TextBlock
            {
                Text = Path.Combine(site.Path, ".env"),
                FontFamily = Monospace,
                FontSize = 12,
                Style = StatusStyles.Text(StatusTone.Neutral),
                TextTrimming = TextTrimming.CharacterEllipsis,
                IsTextSelectionEnabled = true,
                FlowDirection = FlowDirection.LeftToRight
            };
            statusText = new TextBlock
            {
                Text = EnvironmentDocumentStatus(document),
                TextWrapping = TextWrapping.Wrap,
                Style = StatusStyles.Text(StatusTone.Neutral)
            };
            problemsBar = new InfoBar
            {
                Severity = InfoBarSeverity.Warning,
                IsClosable = false,
                IsOpen = false,
                Title = AppLocalization.Get("SitesEnvironmentProblemsTitle")
            };
            var addMissingButton = new Button { Content = AppLocalization.Get("SitesEnvironmentAddMissing") };
            addMissingButton.Click += (_, _) =>
            {
                if (example is null) return;
                Apply(EnvironmentEditorModel.AddMissingFromExample(current, example), rebuildRows: true);
            };
            missingBar = new InfoBar
            {
                Severity = InfoBarSeverity.Informational,
                IsClosable = false,
                IsOpen = false,
                ActionButton = addMissingButton
            };

            variablesItem = new SelectorBarItem
            {
                Text = AppLocalization.Get("SitesEnvironmentVariablesView"),
                IsSelected = true
            };
            var textItem = new SelectorBarItem { Text = AppLocalization.Get("SitesEnvironmentTextView") };
            views = new SelectorBar();
            views.Items.Add(variablesItem);
            views.Items.Add(textItem);
            views.SelectionChanged += (_, _) => ShowSelectedView();

            addBox = new AutoSuggestBox
            {
                PlaceholderText = AppLocalization.Get("SitesEnvironmentAddPlaceholder"),
                QueryIcon = new SymbolIcon(Symbol.Add),
                FontFamily = Monospace,
                FlowDirection = FlowDirection.LeftToRight
            };
            ToolTipService.SetToolTip(addBox, AppLocalization.Get("SitesEnvironmentAddTooltip"));
            Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(addBox, AppLocalization.Get("SitesEnvironmentAddPlaceholder"));
            addBox.GotFocus += (_, _) => RefreshSuggestions();
            addBox.TextChanged += (sender, args) =>
            {
                if (args.Reason == AutoSuggestionBoxTextChangeReason.UserInput) RefreshSuggestions();
            };
            addBox.QuerySubmitted += (_, args) => AddVariable(args.ChosenSuggestion as string ?? addBox.Text);
            revealBox = new CheckBox
            {
                Content = AppLocalization.Get("SitesEnvironmentRevealSecrets"),
                MinWidth = 0,
                VerticalAlignment = VerticalAlignment.Center
            };
            revealBox.Checked += (_, _) => RebuildRows();
            revealBox.Unchecked += (_, _) => RebuildRows();
            var addRow = new Grid { ColumnSpacing = 12 };
            addRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            addRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            addRow.Children.Add(addBox);
            Grid.SetColumn(revealBox, 1);
            addRow.Children.Add(revealBox);

            rowsPanel = new StackPanel { Spacing = 6, Padding = new Thickness(0, 0, 12, 0) };
            var rowsScroller = new ScrollViewer
            {
                Content = rowsPanel,
                Height = 340,
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto
            };
            variablesPanel = new Grid { RowSpacing = 10 };
            variablesPanel.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            variablesPanel.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            variablesPanel.Children.Add(addRow);
            Grid.SetRow(rowsScroller, 1);
            variablesPanel.Children.Add(rowsScroller);

            editor = new TextBox
            {
                Header = AppLocalization.Get("SitesEnvironmentContentsField"),
                Text = current,
                AcceptsReturn = true,
                TextWrapping = TextWrapping.NoWrap,
                IsSpellCheckEnabled = false,
                FontFamily = Monospace,
                FlowDirection = FlowDirection.LeftToRight,
                HorizontalAlignment = HorizontalAlignment.Stretch,
                Height = 380,
                Visibility = Visibility.Collapsed
            };
            ScrollViewer.SetHorizontalScrollBarVisibility(editor, ScrollBarVisibility.Auto);
            ScrollViewer.SetVerticalScrollBarVisibility(editor, ScrollBarVisibility.Auto);
            editor.TextChanged += (_, _) =>
            {
                if (syncingEditor) return;
                current = editor.Text;
                UpdateState();
            };

            editPanel = new StackPanel { Spacing = 10 };
            editPanel.Children.Add(problemsBar);
            editPanel.Children.Add(missingBar);
            editPanel.Children.Add(views);
            editPanel.Children.Add(variablesPanel);
            editPanel.Children.Add(editor);

            reviewLines = new StackPanel { Spacing = 4 };
            reviewNote = new TextBlock { TextWrapping = TextWrapping.Wrap, Style = StatusStyles.Text(StatusTone.Neutral) };
            reviewPanel = new StackPanel { Spacing = 10, Visibility = Visibility.Collapsed };
            reviewPanel.Children.Add(new TextBlock
            {
                Text = AppLocalization.Get("SitesEnvironmentReviewHeading"),
                Style = (Style)Application.Current.Resources["BodyStrongTextBlockStyle"]
            });
            reviewPanel.Children.Add(new ScrollViewer
            {
                Content = reviewLines,
                MaxHeight = 360,
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto
            });
            reviewPanel.Children.Add(reviewNote);

            var content = new StackPanel { Width = 620, Spacing = 10 };
            content.Children.Add(pathText);
            content.Children.Add(statusText);
            content.Children.Add(editPanel);
            content.Children.Add(reviewPanel);

            dialog = new ContentDialog
            {
                FlowDirection = AppLocalization.LayoutDirection,
                XamlRoot = page.XamlRoot,
                Title = AppLocalization.Format("SitesEnvironmentDialogTitle", site.Name),
                Content = content,
                PrimaryButtonText = AppLocalization.Get("SitesEnvironmentReviewButton"),
                CloseButtonText = AppLocalization.Get("SitesClose"),
                DefaultButton = ContentDialogButton.None,
                IsPrimaryButtonEnabled = false
            };
            dialog.Resources["ContentDialogMaxWidth"] = 720d;
            dialog.PrimaryButtonClick += Dialog_PrimaryButtonClick;
            dialog.SecondaryButtonClick += (_, args) =>
            {
                // Secondary is "Back" while reviewing.
                args.Cancel = true;
                ShowReview(false);
            };
            dialog.Closing += (_, args) =>
            {
                if (args.Result != ContentDialogResult.None || !IsDirty || discardArmed) return;
                args.Cancel = true;
                discardArmed = true;
                SetStatus(AppLocalization.Get("SitesEnvironmentConfirmDiscard"), StatusTone.Caution);
            };

            RebuildRows();
            UpdateState();
        }

        private bool IsDirty => !string.Equals(current, document.Contents, StringComparison.Ordinal);

        public async Task ShowAsync() => await dialog.ShowAsync();

        private void ShowSelectedView()
        {
            var showVariables = views.SelectedItem == variablesItem;
            if (showVariables) RebuildRows();
            variablesPanel.Visibility = showVariables ? Visibility.Visible : Visibility.Collapsed;
            editor.Visibility = showVariables ? Visibility.Collapsed : Visibility.Visible;
        }

        private void Apply(string contents, bool rebuildRows)
        {
            current = contents;
            syncingEditor = true;
            try
            {
                editor.Text = contents;
            }
            finally
            {
                syncingEditor = false;
            }
            if (rebuildRows) RebuildRows();
            UpdateState();
        }

        private void UpdateState()
        {
            discardArmed = false;
            dialog.IsPrimaryButtonEnabled = IsDirty;
            if (IsDirty) SetStatus(AppLocalization.Get("SitesEnvironmentStatusUnsaved"), StatusTone.Neutral);
            else if (statusText.Style != StatusStyles.Text(StatusTone.Success)) SetStatus(EnvironmentDocumentStatus(document), StatusTone.Neutral);

            var problems = EnvironmentEditorModel.Problems(current);
            problemsBar.IsOpen = problems.Count > 0;
            if (problems.Count > 0)
            {
                problemsBar.Message = string.Join(
                    Environment.NewLine,
                    problems.Take(4).Select(ProblemText)
                ) + (problems.Count > 4
                    ? Environment.NewLine + AppLocalization.Format("SitesEnvironmentMoreProblems", problems.Count - 4)
                    : string.Empty);
            }

            var missing = EnvironmentEditorModel.MissingFromExample(current, example);
            missingBar.IsOpen = missing.Count > 0;
            if (missing.Count > 0)
            {
                missingBar.Title = AppLocalization.Format("SitesEnvironmentMissingTitle", missing.Count);
                missingBar.Message = string.Join(", ", missing.Take(6)) + (missing.Count > 6 ? ", ..." : string.Empty);
            }
        }

        private static string ProblemText(EnvironmentProblem problem) => problem.Kind switch
        {
            EnvironmentProblemKind.DuplicateKey => AppLocalization.Format("SitesEnvironmentProblemDuplicate", problem.LineNumber, problem.Key),
            EnvironmentProblemKind.UnquotedWhitespace => AppLocalization.Format("SitesEnvironmentProblemWhitespace", problem.LineNumber, problem.Key),
            EnvironmentProblemKind.UnclosedQuote => AppLocalization.Format("SitesEnvironmentProblemQuote", problem.LineNumber, problem.Key),
            _ => AppLocalization.Format("SitesEnvironmentProblemInvalid", problem.LineNumber)
        };

        private void SetStatus(string text, StatusTone tone)
        {
            statusText.Text = text;
            statusText.Style = StatusStyles.Text(tone);
        }

        private void RefreshSuggestions()
        {
            addBox.ItemsSource = EnvironmentEditorModel.Suggestions(addBox.Text, current, example);
        }

        private void AddVariable(string? text)
        {
            var key = (text ?? string.Empty).Trim();
            if (key.Length == 0) return;
            if (!EnvironmentEditorModel.IsValidKey(key))
            {
                SetStatus(AppLocalization.Get("SitesEnvironmentInvalidKey"), StatusTone.Critical);
                return;
            }
            Apply(EnvironmentEditorModel.Add(current, key, string.Empty), rebuildRows: true);
            addBox.Text = string.Empty;
            addBox.ItemsSource = null;
            FocusValue(key);
        }

        private void FocusValue(string key)
        {
            foreach (var child in rowsPanel.Children)
            {
                if (child is Grid { Tag: string rowKey } row && rowKey == key)
                {
                    var value = row.Children.OfType<Control>().FirstOrDefault(control => control is TextBox or PasswordBox);
                    value?.StartBringIntoView();
                    value?.Focus(FocusState.Programmatic);
                    return;
                }
            }
        }

        private void RebuildRows()
        {
            rowsPanel.Children.Clear();
            var entries = EnvironmentEditorModel.Entries(current);
            if (entries.Count == 0)
            {
                rowsPanel.Children.Add(new TextBlock
                {
                    Text = AppLocalization.Get("SitesEnvironmentNoVariables"),
                    TextWrapping = TextWrapping.Wrap,
                    Style = StatusStyles.Text(StatusTone.Neutral)
                });
                return;
            }
            foreach (var entry in entries) rowsPanel.Children.Add(BuildRow(entry));
        }

        private Grid BuildRow(EnvironmentEntry entry)
        {
            var row = new Grid { ColumnSpacing = 8, Tag = entry.Key, FlowDirection = FlowDirection.LeftToRight };
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(210) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            var keyText = new TextBlock
            {
                Text = entry.Key,
                FontFamily = Monospace,
                VerticalAlignment = VerticalAlignment.Center,
                TextTrimming = TextTrimming.CharacterEllipsis,
                IsTextSelectionEnabled = true
            };
            ToolTipService.SetToolTip(keyText, entry.Key);
            row.Children.Add(keyText);

            FrameworkElement value;
            var lineIndex = entry.LineIndex;
            if (entry.IsMultiline)
            {
                value = new TextBlock
                {
                    Text = AppLocalization.Get("SitesEnvironmentMultiline"),
                    FontStyle = global::Windows.UI.Text.FontStyle.Italic,
                    VerticalAlignment = VerticalAlignment.Center,
                    TextWrapping = TextWrapping.Wrap,
                    Style = StatusStyles.Text(StatusTone.Neutral)
                };
            }
            else if (entry.IsSecret)
            {
                var secret = new PasswordBox
                {
                    Password = entry.Value,
                    PasswordRevealMode = revealBox.IsChecked == true ? PasswordRevealMode.Visible : PasswordRevealMode.Peek,
                    FontFamily = Monospace
                };
                secret.PasswordChanged += (_, _) =>
                    Apply(EnvironmentEditorModel.SetValue(current, lineIndex, secret.Password), rebuildRows: false);
                value = secret;
            }
            else
            {
                var text = new TextBox
                {
                    Text = entry.Value,
                    FontFamily = Monospace,
                    IsSpellCheckEnabled = false
                };
                text.TextChanged += (_, _) =>
                    Apply(EnvironmentEditorModel.SetValue(current, lineIndex, text.Text), rebuildRows: false);
                value = text;
            }
            Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(value, entry.Key);
            Grid.SetColumn(value, 1);
            row.Children.Add(value);

            var remove = new Button
            {
                Content = new FontIcon { Glyph = "\uE74D", FontSize = 14 },
                Style = (Style)Application.Current.Resources["ToolbarIconButtonStyle"]
            };
            var removeLabel = AppLocalization.Format("SitesEnvironmentRemoveVariable", entry.Key);
            ToolTipService.SetToolTip(remove, removeLabel);
            Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(remove, removeLabel);
            remove.Click += (_, _) => Apply(EnvironmentEditorModel.Remove(current, lineIndex), rebuildRows: true);
            Grid.SetColumn(remove, 2);
            row.Children.Add(remove);
            return row;
        }

        private void ShowReview(bool show)
        {
            reviewing = show;
            editPanel.Visibility = show ? Visibility.Collapsed : Visibility.Visible;
            reviewPanel.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
            dialog.PrimaryButtonText = AppLocalization.Get(show ? "SitesSave" : "SitesEnvironmentReviewButton");
            dialog.SecondaryButtonText = show ? AppLocalization.Get("SitesEnvironmentBack") : string.Empty;
            if (!show) ShowSelectedView();
        }

        private void BuildReview()
        {
            reviewLines.Children.Clear();
            var changes = EnvironmentEditorModel.Diff(document.Contents, current);
            foreach (var change in changes)
            {
                var (text, tone) = change.Kind switch
                {
                    EnvironmentDiffKind.Added => (AppLocalization.Format("SitesEnvironmentDiffAdded", change.Key, change.After), StatusTone.Success),
                    EnvironmentDiffKind.Removed => (AppLocalization.Format("SitesEnvironmentDiffRemoved", change.Key), StatusTone.Critical),
                    _ => (AppLocalization.Format("SitesEnvironmentDiffChanged", change.Key, change.Before, change.After), StatusTone.Caution)
                };
                reviewLines.Children.Add(new TextBlock
                {
                    Text = text,
                    FontFamily = Monospace,
                    TextWrapping = TextWrapping.Wrap,
                    IsTextSelectionEnabled = true,
                    Style = StatusStyles.Text(tone)
                });
            }
            if (changes.Count == 0)
            {
                reviewLines.Children.Add(new TextBlock
                {
                    Text = AppLocalization.Get("SitesEnvironmentDiffNoVariables"),
                    Style = StatusStyles.Text(StatusTone.Neutral)
                });
            }

            var notes = new List<string>();
            if (EnvironmentEditorModel.OtherLinesChanged(document.Contents, current))
            {
                notes.Add(AppLocalization.Get("SitesEnvironmentDiffOtherLines"));
            }
            if (!document.Exists) notes.Add(AppLocalization.Get("SitesEnvironmentDiffCreates"));
            var problems = EnvironmentEditorModel.Problems(current).Count;
            if (problems > 0) notes.Add(AppLocalization.Format("SitesEnvironmentDiffProblems", problems));
            reviewNote.Text = string.Join(Environment.NewLine, notes);
            reviewNote.Style = StatusStyles.Text(problems > 0 ? StatusTone.Caution : StatusTone.Neutral);
            reviewNote.Visibility = notes.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        }

        private async void Dialog_PrimaryButtonClick(ContentDialog sender, ContentDialogButtonClickEventArgs args)
        {
            args.Cancel = true;
            if (!IsDirty) return;
            if (!reviewing)
            {
                BuildReview();
                ShowReview(true);
                return;
            }

            var deferral = args.GetDeferral();
            dialog.IsPrimaryButtonEnabled = false;
            dialog.IsSecondaryButtonEnabled = false;
            try
            {
                var editedContents = current;
                var expectedRevision = document.Revision;
                document = await Task.Run(() => ProjectEnvironmentFile.Save(
                    site.Path,
                    editedContents,
                    expectedRevision
                ));
                ShowReview(false);
                SetStatus(AppLocalization.Get("SitesEnvironmentStatusSaved"), StatusTone.Success);
                UpdateState();
                App.MainWindow.ShowToast(AppLocalization.Format("SitesEnvironmentSavedToast", site.Name));
            }
            catch (ProjectEnvironmentChangedException)
            {
                ShowReview(false);
                SetStatus(AppLocalization.Get("SitesEnvironmentExternalChange"), StatusTone.Critical);
            }
            catch (Exception error) when (error is IOException
                or UnauthorizedAccessException
                or InvalidDataException
                or InvalidOperationException)
            {
                ShowReview(false);
                SetStatus(error.Message, StatusTone.Critical);
            }
            finally
            {
                dialog.IsPrimaryButtonEnabled = IsDirty;
                dialog.IsSecondaryButtonEnabled = true;
                deferral.Complete();
            }
        }
    }
}
