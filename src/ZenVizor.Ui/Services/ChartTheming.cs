using System.Runtime.Versioning;
using LiveChartsCore.Kernel.Sketches;
using LiveChartsCore.SkiaSharpView.Painting;
using LiveChartsCore.SkiaSharpView.WPF;
using SkiaSharp;
using Wpf.Ui.Appearance;

namespace ZenVizor.Ui.Services;

/// <summary>
/// Themed SkiaSharp paints for LiveCharts2 axis labels, grid separators, and
/// legend text. SkiaSharp paints do NOT inherit Wpf.Ui DynamicResource, so
/// chart text/grid colors must be computed in code from the current
/// ApplicationTheme and re-applied on OS theme flip.
///
/// Pages that own charts call <see cref="Apply"/> once at construction with
/// the chart, then subscribe <see cref="Changed"/> and call Apply(chart)
/// again on every flip.
/// </summary>
/// <remarks>
/// The Wpf.Ui Dark.xaml + Light.xaml exact values are mirrored here so chart
/// text matches body text in either theme:
///   Dark:  TextFillColorPrimary = #FFFFFFFF   |  separators ~ 8% white
///   Light: TextFillColorPrimary = #E4000000   |  separators ~ 8% black
/// </remarks>
[SupportedOSPlatform("windows")]
internal static class ChartTheming
{
    /// <summary>Fires when the OS theme flips (forwarded from
    /// <see cref="ApplicationThemeManager.Changed"/>).</summary>
    public static event Action? Changed;

    static ChartTheming()
    {
        ApplicationThemeManager.Changed += (_, _) => Changed?.Invoke();
    }

    /// <summary>
    /// Apply themed paints to every axis + the legend on the given chart.
    /// Idempotent — safe to call repeatedly (each call assigns fresh paint
    /// instances).
    /// </summary>
    public static void Apply(CartesianChart chart)
    {
        ApplyToAxes(chart.XAxes);
        ApplyToAxes(chart.YAxes);
        chart.LegendTextPaint = AxisLabelsPaint();
    }

    private static void ApplyToAxes(IEnumerable<ICartesianAxis>? axes)
    {
        if (axes is null) return;
        foreach (var axis in axes)
        {
            axis.LabelsPaint = AxisLabelsPaint();
            axis.SeparatorsPaint = SeparatorsPaint();
        }
    }

    private static SolidColorPaint AxisLabelsPaint() =>
        new(LabelColor());

    private static SolidColorPaint SeparatorsPaint() =>
        new(SeparatorColor()) { StrokeThickness = 0.5f };

    private static SKColor LabelColor() =>
        ApplicationThemeManager.GetAppTheme() == ApplicationTheme.Dark
            ? new SKColor(0xFF, 0xFF, 0xFF, 0xFF)
            : new SKColor(0x00, 0x00, 0x00, 0xE4);

    private static SKColor SeparatorColor() =>
        ApplicationThemeManager.GetAppTheme() == ApplicationTheme.Dark
            ? new SKColor(0xFF, 0xFF, 0xFF, 0x14)
            : new SKColor(0x00, 0x00, 0x00, 0x14);
}
