using System.Windows;

namespace DiscordSnapshot.Gui;

public partial class TokenHelp : Window
{
    public TokenHelp() => InitializeComponent();

    private void OpenPortalClick(object sender, RoutedEventArgs e) =>
        App.OpenInShell(this, "https://discord.com/developers/applications");
}
