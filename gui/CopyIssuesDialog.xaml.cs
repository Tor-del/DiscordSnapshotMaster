using System.Windows;

namespace DiscordSnapshot.Gui;

internal partial class CopyIssuesDialog : Window
{
    private sealed class IssueRow(CopyIssue issue)
    {
        public CopyIssue Issue { get; } = issue;
        public string Title { get; } = Strings.Get(issue.IsFile ? "CopyIssueFile" : "CopyIssueChannel", issue.Title);
        public string Detail { get; } = issue.Context is null ? issue.Error : $"{issue.Context}\n{issue.Error}";
        public bool Selected { get; set; } = true;
    }

    private readonly IssueRow[] rows;
    public bool Retry { get; private set; }
    public CopyIssue[] SelectedIssues => rows.Where(row => row.Selected).Select(row => row.Issue).ToArray();

    public CopyIssuesDialog(CopyIssue[] issues, string? error, bool canRetry)
    {
        rows = issues.Select(issue => new IssueRow(issue)).ToArray();
        InitializeComponent();
        var instructions = rows.Length == 0 ? null : Strings.Get("CopyIssuesSummary");
        Summary.Text = string.Join('\n', new[] { error, instructions }.OfType<string>());
        IssueList.ItemsSource = rows;
        IssueList.Visibility = SkipButton.Visibility = rows.Length == 0 ? Visibility.Collapsed : Visibility.Visible;
        RetryButton.IsEnabled = canRetry;
    }

    private void SelectionChanged(object sender, RoutedEventArgs e) => SkipButton.IsEnabled = rows.Any(row => row.Selected);
    private void SkipClick(object sender, RoutedEventArgs e) => DialogResult = true;

    private void RetryClick(object sender, RoutedEventArgs e)
    {
        Retry = true;
        DialogResult = true;
    }
}
