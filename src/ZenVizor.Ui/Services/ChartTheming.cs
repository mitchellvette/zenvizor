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
/// chart text/grid colors must be computed in code from the current resource
/// dictionary and re-applied on every event that changes which dictionary
/// owns each token.
///
/// Pages that own charts call <see cref="Apply"/> once at construction with
/// the chart, then subscribe <see cref="Changed"/> and call Apply(chart)
/// again on every flip.
/// </summary>
/// <remarks>
/// Three event sources fire <see cref="Changed"/>:
/// <list type="bullet">
///   <item><see cref="ApplicationThemeManager.Changed"/> — OS Light↔Dark flip
///   (swaps BrandAccent.Light.xaml ↔ BrandAccent.Dark.xaml in App.xaml.cs).</item>
///   <item><see cref="SystemParameters.StaticPropertyChanged"/> — Windows
///   High Contrast toggle (merges/unmerges HighContrast.xaml in
///   App.xaml.cs Phase 6.5 wiring).</item>
/// </list>
/// Colors are read from the existing chart-chrome tokens
/// (chart.axis.label / chart.gridline / chart.legend.text) so HC overrides
/// flow through automatically — no hardcoded SKColors per theme.
/// </remarks>
[SupportedOSPlatform("windows")]
internal static class ChartTheming
{
    /// <summary>Fires when chart paints need to be re-applied (OS theme flip
    /// OR Windows High Contrast toggle).</summary>
    public static event Action? Changed;

    static ChartTheming()
    {
        ApplicationThemeManager.Changed += (_, _) => RaiseOnDispatcher();
        // Phase 6.5 — HC flips re-paint chart chrome. The SystemParameters
        // event fires on a worker thread, so we marshal the broadcast to
        // the UI dispatcher centrally rather than asking every subscriber
        // to remember to Dispatcher.Invoke. Pre-6.5 the event only fired
        // from ApplicationThemeManager.Changed which is already UI-thread,
        // so existing subscribers vary in whether they marshal — central
        // marshaling here keeps both shapes safe.
        SystemParameters.StaticPropertyChanged += (_, _) => RaiseOnDispatcher();
    }

    private static void RaiseOnDispatcher()
    {
        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher is null || dispatcher.CheckAccess())
        {
            Changed?.Invoke();
            return;
        }
        _ = dispatcher.BeginInvoke(new Action(() => Changed?.Invoke()));
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
    /// chart.downSeries). Public so pages can call it directly after assigning
    /// fresh series to a chart — <see cref="Apply"/> in a page ctor runs BEFORE
    /// <c>CartesianChart.Series</c> is set, so the initial pass through here
    /// no-ops on the still-null Series array; without an explicit re-call after
    /// every Series assignment, new line/bar series render in LC2's default
    /// palette. Also fires on OS theme flip via <see cref="Apply"/> once Series
    /// is populated, swapping to the dark / light stops in
    /// BrandAccent.{Light,Dark}.xaml. Series are matched by Name ("Up"/"Down");
    /// anything else is skipped, so chart-state placeholders or future series
    /// stay unaffected.
    /// <list type="bullet">
    ///   <item><see cref="LineSeries{T}"/>: stroke thickness 2; fill at
    ///   alpha=60 (~24%) of the same hue for the area-under-line.</item>
    ///   <item><see cref="StackedColumnSeries{T}"/>: bar fill at full alpha;
    ///   no stroke (clean rectangles, the stacked Up/Down split carries
    ///   the visual separation).</item>
    /// </list>
    /// </summary>
    public static void ApplyToSeries(IEnumerable<ISeries>? series)
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

    /// <summary>
    /// Axis + legend label color. Reads <c>chart.axis.label</c> from the
    /// current resource dictionary so theme flips AND HC overrides flow
    /// through. Falls back to a Wpf.Ui-matched per-theme constant if the
    /// resource is missing (defensive; should never fire because
    /// DesignTokens.xaml always carries the key).
    /// </summary>
    private static SKColor LabelColor()
    {
        if (TryGetBrandColor("chart.axis.label", out var c))
        {
            return new SKColor(c.R, c.G, c.B, c.A);
        }
        return ApplicationThemeManager.GetAppTheme() == ApplicationTheme.Dark
            ? new SKColor(0xFF, 0xFF, 0xFF, 0xFF)
            : new SKColor(0x00, 0x00, 0x00, 0xE4);
    }

    /// <summary>
    /// Grid separator color. Reads <c>chart.gridline</c> from the current
    /// resource dictionary. Same flow-through rationale as
    /// <see cref="LabelColor"/>; same defensive per-theme fallback.
    /// </summary>
    private static SKColor SeparatorColor()
    {
        if (TryGetBrandColor("chart.gridline", out var c))
        {
            return new SKColor(c.R, c.G, c.B, c.A);
        }
        return ApplicationThemeManager.GetAppTheme() == ApplicationTheme.Dark
            ? new SKColor(0xFF, 0xFF, 0xFF, 0x14)
            : new SKColor(0x00, 0x00, 0x00, 0x14);
    }
}
