using System.Windows;
using System.Windows.Media;
using System.Windows.Threading;
using Wpf.Ui.Appearance;

namespace ZenVizor.Ui;

public partial class App : Application
{
    private static readonly Uri BrandAccentLightUri =
        new("pack://application:,,,/Resources/BrandAccent.Light.xaml", UriKind.Absolute);
    private static readonly Uri BrandAccentDarkUri =
        new("pack://application:,,,/Resources/BrandAccent.Dark.xaml", UriKind.Absolute);

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
        //
        // updateAccent: FALSE — Wpf.Ui's ApplySystemAccent overwrites the
        // SystemAccentColor* resources with the OS accent (Windows blue),
        // defeating the BrandAccent.{Light,Dark}.xaml brand-violet overrides.
        // The brand dict supplies every SystemAccentColor* AND every
        // AccentFillColor* key Wpf.Ui controls need (those AccentFill keys
        // aren't in Light.baml/Dark.baml either — they're normally
        // populated by ApplySystemAccent), so disabling that path is both
        // safe and necessary.
        ApplicationThemeManager.ApplySystemTheme(updateAccent: false);

        // Brand-accent dict has the same lifecycle as Wpf.Ui's ThemesDictionary:
        // initial Source URI swapped to the OS theme before the first frame is
        // built, then re-swapped on every runtime Light↔Dark flip. The dict
        // overrides Wpf.Ui's SystemAccentColor* and SystemFillColor* with brand
        // values, so NavigationView selection, focus rings, status banners,
        // and chart series all pick up brand violet without per-control work.
        SwapBrandAccentDictionary();

        // Direct-level overrides for the keys Wpf.Ui writes into
        // Application.Current.Resources at the direct level (which shadows
        // MergedDictionaries entries in WPF's lookup precedence). Queued at
        // ApplicationIdle so it runs AFTER MainWindow.ctor's
        // SystemThemeWatcher.Watch() — which is the call that does the
        // shadowing. See ApplyDirectLevelOverrides for the key list.
        _ = Dispatcher.BeginInvoke(new Action(ApplyDirectLevelOverrides), DispatcherPriority.ApplicationIdle);

        ApplicationThemeManager.Changed += (_, _) =>
            Dispatcher.Invoke(() =>
            {
                SwapBrandAccentDictionary();
                // Re-apply on every theme change. Queued at ApplicationIdle
                // again so Wpf.Ui's own Changed handlers (which may rewrite
                // SystemAccentColor* etc.) run first.
                _ = Dispatcher.BeginInvoke(new Action(ApplyDirectLevelOverrides), DispatcherPriority.ApplicationIdle);
            });

        base.OnStartup(e);
    }

    /// <summary>
    /// Stomps brand values into <c>Application.Current.Resources</c> at the
    /// direct level for keys Wpf.Ui sets there itself (which shadows
    /// MergedDictionaries lookups). Diagnostic from the previous startup
    /// flagged <c>SystemAccentColorPrimary</c>, <c>AccentFillColorDefaultBrush</c>,
    /// and <c>NavigationViewItemForegroundLeftFluent</c> as direct-level
    /// shadowed — even with <c>updateAccent: false</c>. Setting them HERE
    /// after Wpf.Ui's own writes guarantees brand wins.
    /// </summary>
    private void ApplyDirectLevelOverrides()
    {
        try
        {
            var isDark = ApplicationThemeManager.GetAppTheme() == ApplicationTheme.Dark;

            // Per-theme accent stops (light: violet-600/700/800; dark:
            // violet-500/400/600 lighter ramp).
            var accentDefault = isDark ? Color.FromRgb(0x82, 0x54, 0xE6) : Color.FromRgb(0x6D, 0x3F, 0xD1);
            var accentSecondary = isDark ? Color.FromRgb(0x9A, 0x72, 0xF0) : Color.FromRgb(0x56, 0x1F, 0xB0);
            var accentTertiary = isDark ? Color.FromRgb(0x6D, 0x3F, 0xD1) : Color.FromRgb(0x46, 0x1D, 0x7C);

            // SystemAccentColor* — Color resources, Wpf.Ui sets these at
            // direct level despite updateAccent: false.
            Resources["SystemAccentColorPrimary"] = accentDefault;
            Resources["SystemAccentColorSecondary"] = accentSecondary;
            Resources["SystemAccentColorTertiary"] = accentTertiary;

            // AccentFillColor* — Wpf.Ui's button accent path. Colors and
            // brushes both set at direct level.
            Resources["AccentFillColorDefault"] = accentDefault;
            Resources["AccentFillColorDefaultBrush"] = new SolidColorBrush(accentDefault);
            Resources["AccentFillColorSecondary"] = accentSecondary;
            Resources["AccentFillColorSecondaryBrush"] = new SolidColorBrush(accentSecondary);
            Resources["AccentFillColorTertiary"] = accentTertiary;
            Resources["AccentFillColorTertiaryBrush"] = new SolidColorBrush(accentTertiary);

            // NavigationView selection — every brush the visible selected
            // state binds to: indicator pill, item foreground (the icon
            // color in left-fluent layout), and selected background tint.
            // Icon Foreground uses accent.default (NOT accent.text) so it
            // matches the pill colour.
            Resources["NavigationViewSelectionIndicatorForeground"] = new SolidColorBrush(accentDefault);
            Resources["NavigationViewItemForegroundLeftFluent"] = new SolidColorBrush(accentDefault);
            Resources["NavigationViewItemForegroundPointerOverLeftFluent"] = new SolidColorBrush(accentDefault);
            // Selected row background — vertical violet fade-out gradient
            // per the mockup CSS: linear-gradient(180deg,
            // color-mix(accent.default 18%, transparent),
            // color-mix(accent.default 7%, transparent)). Same hue at
            // both stops (only alpha varies) so the ramp is a clean
            // single-color fade — 18% (0x2E) top, 7% (0x12) bottom.
            var gradientTop = Color.FromArgb(0x2E, accentDefault.R, accentDefault.G, accentDefault.B);
            var gradientBottom = Color.FromArgb(0x12, accentDefault.R, accentDefault.G, accentDefault.B);
            var selectedRowGradient = new LinearGradientBrush(gradientTop, gradientBottom, 90.0);
            Resources["NavigationViewItemBackgroundSelected"] = selectedRowGradient;
            Resources["NavigationViewItemBackgroundSelectedLeftFluent"] = selectedRowGradient;

            // Mica showthrough on the page area. Every Page sets
            // Background="{DynamicResource ApplicationBackgroundBrush}", so
            // Transparent here makes Pages fully see-through over the
            // MainWindow outer Grid. The brand tint is painted ONCE on the
            // outer Grid (via Background="{DynamicResource surface.background}"
            // in MainWindow.xaml) so the tint reads uniform across the page
            // area AND the surrounding chrome (nav pane, title bar,
            // bottom-bar) — no double-paint, no seam at Grid.Column 0/1 of
            // NavigationView. Cards stay opaque (surface.card) for text
            // legibility. Wpf.Ui shadows ApplicationBackgroundBrush at
            // direct level via SystemThemeWatcher, so the override has to
            // land here.
            Resources["ApplicationBackgroundColor"] = Colors.Transparent;
            Resources["ApplicationBackgroundBrush"] = new SolidColorBrush(Colors.Transparent);

            // Wpf.Ui's LeftNavigationViewTemplate wraps the page-side
            // content area in a <Border Background="{DynamicResource
            // NavigationViewContentBackground}" BorderBrush="{DynamicResource
            // NavigationViewContentGridBorderBrush}" ... />. The Wpf.Ui
            // defaults are #4C3A3A3A (~30% gray) and #19000000 (~10% black)
            // in dark — they sit ABOVE the Mica backdrop and BELOW the
            // hosted Page, so even with ApplicationBackgroundBrush set
            // transparent, this Border occludes Mica from the page area
            // (only the page area — the pane and chrome are outside this
            // Border). Override to Transparent so Mica finally shows
            // through the dashboard backdrop.
            Resources["NavigationViewContentBackground"] = new SolidColorBrush(Colors.Transparent);
            Resources["NavigationViewContentGridBorderBrush"] = new SolidColorBrush(Colors.Transparent);

            // SolidBackgroundFillColorBase propagation. Wpf.Ui's own
            // controls (Buttons, ComboBox dropdowns, popups, ContextMenu)
            // read this Color directly rather than via the surface.card
            // token. Without this override they'd render at Wpf.Ui's
            // default neutral gray, visually mismatched against our
            // brand-aligned cards. surface.card is opaque, so this is
            // opaque too.
            var cardColor = isDark
                ? Color.FromRgb(0x23, 0x27, 0x35)
                : Color.FromRgb(0xFF, 0xFF, 0xFF);
            Resources["SolidBackgroundFillColorBase"] = cardColor;
            Resources["SolidBackgroundFillColorBaseBrush"] = new SolidColorBrush(cardColor);
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[direct-overrides] failed: {ex.Message}");
        }
    }

    private void SwapBrandAccentDictionary()
    {
        var theme = ApplicationThemeManager.GetAppTheme();
        var targetUri = theme == ApplicationTheme.Dark ? BrandAccentDarkUri : BrandAccentLightUri;

        // Find the brand-accent dict by Source URI substring match. Matches
        // both BrandAccent.Light.xaml and BrandAccent.Dark.xaml regardless
        // of which one is currently merged.
        foreach (var dict in Resources.MergedDictionaries)
        {
            if (dict.Source?.OriginalString.Contains("BrandAccent.", StringComparison.OrdinalIgnoreCase) == true)
            {
                if (dict.Source == targetUri) return; // already correct
                dict.Source = targetUri;
                return;
            }
        }
        // First-call fallback (shouldn't fire — App.xaml declares the dict): add it.
        Resources.MergedDictionaries.Add(new ResourceDictionary { Source = targetUri });
    }
}
