using System.Runtime.Versioning;
using System.Windows;
using System.Windows.Media;
using LiveChartsCore;
using LiveChartsCore.Defaults;
using LiveChartsCore.Kernel.Sketches;
using LiveChartsCore.Measure;
using LiveChartsCore.SkiaSharpView;
using LiveChartsCore.SkiaSharpView.Painting;
using LiveChartsCore.SkiaSharpView.Painting.ImageFilters;
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
        ApplyToSeries(chart.Series);

        // Tooltip chrome — opaque brand background (never translucent over
        // Mica so text contrast isn't wallpaper-dependent) and brand text.
        // FindingStrategy = X-snap: hovering anywhere in the plot area
        // highlights the nearest X position and shows BOTH Up and Down
        // series simultaneously, rather than requiring the cursor to land
        // exactly on one of the lines.
        chart.TooltipBackgroundPaint = TooltipBackgroundPaint();
        chart.TooltipTextPaint = TooltipTextPaint();
        chart.FindingStrategy = FindingStrategy.CompareOnlyXTakeClosest;
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

    /// <summary>
    /// Repaint Up/Down series with their brand token colors (chart.upSeries /
    /// chart.downSeries) so a theme flip rebuilds the strokes and fills off
    /// the violet/teal stops in BrandAccent.{Light,Dark}.xaml. Series are
    /// matched by Name ("Up"/"Down"); anything else is skipped, so
    /// chart-state placeholders or future series stay unaffected.
    /// <list type="bullet">
    ///   <item><see cref="LineSeries{T}"/>: stroke thickness 2; fill at
    ///   alpha=60 (~24%) of the same hue for the area-under-line.</item>
    ///   <item><see cref="StackedColumnSeries{T}"/>: bar fill at full alpha;
    ///   no stroke (clean rectangles, the stacked Up/Down split carries
    ///   the visual separation).</item>
    /// </list>
    /// </summary>
    private static void ApplyToSeries(IEnumerable<ISeries>? series)
    {
        if (series is null) return;
        foreach (var s in series)
        {
            var name = s switch
            {
                LineSeries<DateTimePoint> ls => ls.Name,
                StackedColumnSeries<DateTimePoint> sc => sc.Name,
                _ => null,
            };
            var key = name switch
            {
                "Up" => "chart.upSeries",
                "Down" => "chart.downSeries",
                _ => null,
            };
            if (key is null) continue;
            if (!TryGetBrandColor(key, out var c)) continue;

            switch (s)
            {
                case LineSeries<DateTimePoint> line:
                    line.Stroke = new SolidColorPaint(new SKColor(c.R, c.G, c.B, c.A)) { StrokeThickness = 2 };
                    line.Fill   = new SolidColorPaint(new SKColor(c.R, c.G, c.B, 60));
                    break;
                case StackedColumnSeries<DateTimePoint> col:
                    col.Fill   = new SolidColorPaint(new SKColor(c.R, c.G, c.B, c.A));
                    col.Stroke = null;
                    break;
            }
        }
    }

    private static bool TryGetBrandColor(string key, out Color color)
    {
        if (Application.Current?.Resources[key] is SolidColorBrush brush)
        {
            color = brush.Color;
            return true;
        }
        color = default;
        return false;
    }

    private static SolidColorPaint? TooltipBackgroundPaint()
    {
        if (!TryGetBrandColor("chart.tooltip.bg", out var c)) return null;
        // Drop shadow on the tooltip background paint: at low alpha black,
        // offset 4px down with 8px sigma blur — enough to lift the tooltip
        // visually off similar-toned card / Mica backdrops without making
        // it feel heavy. Applied via SKImageFilter on the paint so the
        // shadow renders inside SkiaSharp without any WPF layering.
        return new SolidColorPaint(new SKColor(c.R, c.G, c.B, c.A))
        {
            ImageFilter = new DropShadow(0, 4, 8, 8, new SKColor(0, 0, 0, 96)),
        };
    }

    private static SolidColorPaint? TooltipTextPaint()
    {
        if (!TryGetBrandColor("chart.tooltip.text", out var c)) return null;
        return new SolidColorPaint(new SKColor(c.R, c.G, c.B, c.A));
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
