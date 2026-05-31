using System.Windows.Controls;

namespace TitaniRun.Ui.Views;

public partial class PlaceholderPage : Page
{
    protected PlaceholderPage(string title, string subtitle)
    {
        InitializeComponent();
        TitleText.Text = title;
        SubtitleText.Text = subtitle;
    }

    // Parameterless ctor required by NavigationView.Navigate(Type).
    public PlaceholderPage() : this("Placeholder", "Wired up in a later phase.")
    {
    }
}
