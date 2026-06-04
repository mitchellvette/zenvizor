using System.Windows;
using Wpf.Ui.Appearance;

namespace ZenVizor.Ui;

public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        // Apply the OS theme before MainWindow is constructed. base.OnStartup
        // triggers StartupUri (MainWindow.xaml) which runs InitializeComponent
        // -- that builds the chrome visual tree, resolves DynamicResource
        // references, and applies Wpf.Ui Styles. Some Wpf.Ui Styles (notably
        // ui:TextBlock + FontTypography) capture the themed Foreground at
        // apply-time and don't re-resolve cleanly through a later runtime
        // dict swap, so the page header rendered dark-on-dark in dark mode
        // until the user manually flipped Light->Dark->Light.
        //
        // ApplySystemTheme here mutates the placeholder ThemesDictionary in
        // App.xaml (Source URI replaced with Dark.xaml or Light.xaml) before
        // any element is built, so the very first frame is correctly themed.
        // SystemThemeWatcher (wired in MainWindow.ctor) continues to handle
        // runtime OS theme flips.
        ApplicationThemeManager.ApplySystemTheme(updateAccent: true);
        base.OnStartup(e);
    }
}
