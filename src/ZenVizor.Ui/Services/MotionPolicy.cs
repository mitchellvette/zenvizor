using System.Runtime.Versioning;
using System.Windows;

namespace ZenVizor.Ui.Services;

/// <summary>
/// Centralized gate for whether the app should run non-essential motion.
/// Animations skip when the user has disabled client-area animation in
/// Windows (the canonical reduced-motion signal) OR when High Contrast is
/// active — HC users expect flat surfaces and stable foregrounds, so an
/// opacity wink / pulse / fade-in is the wrong cue regardless of the
/// ClientAreaAnimation flag.
/// </summary>
/// <remarks>
/// Call sites: <c>MainWindow.PulseAlertsBadge</c> (nav-rail pulse ring) and
/// <c>AlertsPage.PulseDeepLinkTarget</c> (Reports->Alerts deep-link wink).
/// XAML-trigger animations (e.g. the expand chevron on alert cards) are
/// short property-changes and stay always-on — gating them would require
/// lifting the triggers into code-behind with no real accessibility win.
/// </remarks>
[SupportedOSPlatform("windows")]
internal static class MotionPolicy
{
    public static bool AnimationsEnabled =>
        SystemParameters.ClientAreaAnimation && !SystemParameters.HighContrast;
}
