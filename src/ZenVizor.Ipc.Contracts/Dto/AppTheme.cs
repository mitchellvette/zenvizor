namespace ZenVizor.Ipc.Contracts.Dto;

/// <summary>
/// UI theme preference. Persisted in the <c>appearance.theme</c> settings
/// row AND cached at <c>%LocalAppData%\ZenVizor\ui.theme</c> so App.OnStartup
/// can resolve the theme without blocking on the service pipe.
/// </summary>
public enum AppTheme
{
    /// <summary>Follow the OS theme via Wpf.Ui's <c>SystemThemeWatcher</c>. Default.</summary>
    System = 0,

    /// <summary>Explicit light theme; <c>SystemThemeWatcher</c> is unwired.</summary>
    Light = 1,

    /// <summary>Explicit dark theme; <c>SystemThemeWatcher</c> is unwired.</summary>
    Dark = 2,
}
