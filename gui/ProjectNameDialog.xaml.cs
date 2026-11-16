using System.Windows;

namespace DiscordSnapshot.Gui;

public partial class ProjectNameDialog : Window
{
    internal ProjectNameDialog(string name, bool creating)
    {
        InitializeComponent();
        SetResourceReference(TitleProperty, creating ? "UiNewProjectName" : "UiRenameProject");
        NameInput.Text = name;
        Loaded += (_, _) =>
        {
            NameInput.Focus();
            NameInput.SelectAll();
        };
    }

    internal string ProjectName => NameInput.Text.Trim();

    private void SaveClick(object sender, RoutedEventArgs e)
    {
        if (ProjectName.Length == 0)
        {
            ErrorText.Text = Strings.Get("ProjectNameRequired");
            NameInput.Focus();
            return;
        }
        DialogResult = true;
    }
}
