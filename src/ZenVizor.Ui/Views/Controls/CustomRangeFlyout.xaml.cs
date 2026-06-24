// SPDX-License-Identifier: GPL-3.0-or-later

using System.ComponentModel;
using System.Globalization;
using System.Runtime.Versioning;
using System.Windows;
using System.Windows.Controls;
using Wpf.Ui.Controls;
using ZenVizor.Ipc.Contracts.Dto;

namespace ZenVizor.Ui.Views.Controls;

/// <summary>
/// Epic A (1.1.0) — flyout interior for the user-driven custom range
/// picker. Hosted inside a <see cref="System.Windows.Controls.Primitives.Popup"/>
/// on PerAppPage / AppDetailPage. Date pickers are the existing
/// <c>ui:CalendarDatePicker</c> chrome; time is hour (1-12) × minute
/// (00/15/30/45) × AM-PM dropdowns — locked to 15-min intervals (per
/// product call, keeps the window clean; sub-15-min precision is the
/// History popover's job).
///
/// Validation: From &lt; To and To &lt;= now. Server-side <c>ValidateWindow</c>
/// also enforces retention horizon; we surface that error if Apply hits
/// the server and bounces, but client-side we only block invalid order /
/// future windows.
/// </summary>
[SupportedOSPlatform("windows")]
public partial class CustomRangeFlyout : UserControl
{
    /// <summary>Fired when the user clicks Apply with a valid window.</summary>
    public event EventHandler<QueryWindow>? Applied;

    /// <summary>Fired when the user clicks Cancel.</summary>
    public event EventHandler? Cancelled;

    /// <summary>
    /// Set true while <see cref="Open"/> is repopulating the inputs, so
    /// the per-input change handlers don't re-render the span/validation
    /// line N times during initialization.
    /// </summary>
    private bool _initializing;

    public CustomRangeFlyout()
    {
        InitializeComponent();

        // Hours 1..12; minutes 00/15/30/45; AM/PM. Populated once here so
        // the items survive multiple Open() calls.
        for (var h = 1; h <= 12; h++)
        {
            FromHourCombo.Items.Add(h.ToString(CultureInfo.InvariantCulture));
            ToHourCombo.Items.Add(h.ToString(CultureInfo.InvariantCulture));
        }
        foreach (var m in new[] { "00", "15", "30", "45" })
        {
            FromMinuteCombo.Items.Add(m);
            ToMinuteCombo.Items.Add(m);
        }
        foreach (var ap in new[] { "AM", "PM" })
        {
            FromAmPmCombo.Items.Add(ap);
            ToAmPmCombo.Items.Add(ap);
        }

        // Wpf.Ui's CalendarDatePicker exposes Date as a DependencyProperty
        // but doesn't surface a public DateChanged event (same gotcha as
        // ReportsPage). Hook the DP descriptor to re-validate the span on
        // any date change.
        var dateDpd = DependencyPropertyDescriptor.FromProperty(
            CalendarDatePicker.DateProperty,
            typeof(CalendarDatePicker));
        dateDpd?.AddValueChanged(FromDatePicker, OnDatePickerChanged);
        dateDpd?.AddValueChanged(ToDatePicker,   OnDatePickerChanged);
    }

    private void OnDatePickerChanged(object? sender, EventArgs e)
    {
        if (_initializing) return;
        UpdateValidationAndSpan();
    }

    /// <summary>
    /// Populate the inputs from <paramref name="current"/> (the currently
    /// active fixed window, if any) or from defaults: To = now snapped
    /// down to the nearest 15 min, From = To - 1 h. Called by the host
    /// page immediately before opening the Popup.
    /// </summary>
    public void Open(QueryWindow? current)
    {
        _initializing = true;
        try
        {
            DateTime fromLocal, toLocal;
            if (current is not null)
            {
                fromLocal = DateTimeOffset.FromUnixTimeMilliseconds(current.FromUnixMs).LocalDateTime;
                toLocal   = DateTimeOffset.FromUnixTimeMilliseconds(current.ToUnixMs).LocalDateTime;
            }
            else
            {
                toLocal   = SnapDownTo15(DateTime.Now);
                fromLocal = toLocal.AddHours(-1);
            }
            PopulateRow(FromDatePicker, FromHourCombo, FromMinuteCombo, FromAmPmCombo, fromLocal);
            PopulateRow(ToDatePicker,   ToHourCombo,   ToMinuteCombo,   ToAmPmCombo,   toLocal);
        }
        finally
        {
            _initializing = false;
        }
        UpdateValidationAndSpan();
    }

    private void OnInputChanged(object sender, RoutedEventArgs e)
    {
        if (_initializing) return;
        UpdateValidationAndSpan();
    }

    private void OnApplyClick(object sender, RoutedEventArgs e)
    {
        if (!TryBuildWindow(out var window, out _)) return;
        Applied?.Invoke(this, window);
    }

    private void OnCancelClick(object sender, RoutedEventArgs e)
    {
        Cancelled?.Invoke(this, EventArgs.Empty);
    }

    private void UpdateValidationAndSpan()
    {
        if (!TryBuildWindow(out var window, out var error))
        {
            SpanLine.Visibility = Visibility.Collapsed;
            ErrorLine.Text = error;
            ErrorLine.Visibility = Visibility.Visible;
            ApplyButton.IsEnabled = false;
            return;
        }

        SpanLine.Text = "Span: " + FormatSpan(window.SpanMs);
        SpanLine.Visibility = Visibility.Visible;
        ErrorLine.Visibility = Visibility.Collapsed;
        ApplyButton.IsEnabled = true;
    }

    private bool TryBuildWindow(out QueryWindow window, out string error)
    {
        window = null!;
        error = string.Empty;

        if (!TryReadLocal(FromDatePicker, FromHourCombo, FromMinuteCombo, FromAmPmCombo, out var fromLocal) ||
            !TryReadLocal(ToDatePicker,   ToHourCombo,   ToMinuteCombo,   ToAmPmCombo,   out var toLocal))
        {
            error = "Pick a date and time for both From and To.";
            return false;
        }

        if (fromLocal >= toLocal)
        {
            error = "From must be earlier than To.";
            return false;
        }

        var nowLocal = DateTime.Now;
        if (toLocal > nowLocal)
        {
            error = "To can't be in the future.";
            return false;
        }

        // Convert the user's local input to a UTC unix-ms QueryWindow.
        // TimeZoneInfo.ConvertTimeToUtc mirrors LocalDayWindow in
        // AppDetailPage / DailyReportRepository so the produced window
        // reconciles with the existing rollups.
        var fromUtc = TimeZoneInfo.ConvertTimeToUtc(fromLocal, TimeZoneInfo.Local);
        var toUtc   = TimeZoneInfo.ConvertTimeToUtc(toLocal,   TimeZoneInfo.Local);
        window = new QueryWindow(
            FromUnixMs: new DateTimeOffset(fromUtc, TimeSpan.Zero).ToUnixTimeMilliseconds(),
            ToUnixMs:   new DateTimeOffset(toUtc,   TimeSpan.Zero).ToUnixTimeMilliseconds());
        return true;
    }

    private static bool TryReadLocal(
        Wpf.Ui.Controls.CalendarDatePicker datePicker,
        ComboBox hourCombo, ComboBox minuteCombo, ComboBox amPmCombo,
        out DateTime local)
    {
        local = default;
        if (datePicker.Date is not { } date) return false;
        if (hourCombo.SelectedItem is not string hourStr) return false;
        if (minuteCombo.SelectedItem is not string minuteStr) return false;
        if (amPmCombo.SelectedItem is not string amPm) return false;

        if (!int.TryParse(hourStr, NumberStyles.Integer, CultureInfo.InvariantCulture, out var hour12))
            return false;
        if (!int.TryParse(minuteStr, NumberStyles.Integer, CultureInfo.InvariantCulture, out var minute))
            return false;

        var isPm = string.Equals(amPm, "PM", StringComparison.Ordinal);
        var hour24 = isPm
            ? (hour12 == 12 ? 12 : hour12 + 12)
            : (hour12 == 12 ? 0  : hour12);

        local = new DateTime(date.Year, date.Month, date.Day, hour24, minute, 0, DateTimeKind.Local);
        return true;
    }

    private static void PopulateRow(
        Wpf.Ui.Controls.CalendarDatePicker datePicker,
        ComboBox hourCombo, ComboBox minuteCombo, ComboBox amPmCombo,
        DateTime local)
    {
        datePicker.Date = local.Date;

        var isPm = local.Hour >= 12;
        var hour12 = local.Hour % 12;
        if (hour12 == 0) hour12 = 12;
        var minuteSlot = (local.Minute / 15) * 15;

        hourCombo.SelectedItem   = hour12.ToString(CultureInfo.InvariantCulture);
        minuteCombo.SelectedItem = minuteSlot.ToString("D2", CultureInfo.InvariantCulture);
        amPmCombo.SelectedItem   = isPm ? "PM" : "AM";
    }

    private static DateTime SnapDownTo15(DateTime t) =>
        new(t.Year, t.Month, t.Day, t.Hour, (t.Minute / 15) * 15, 0, t.Kind);

    private static string FormatSpan(long ms)
    {
        if (ms < 60_000)     return $"{ms / 1000} s";
        if (ms < 3_600_000)  return $"{ms / 60_000} min";
        if (ms < 86_400_000) return FormatHoursMinutes(ms);
        return $"{ms / 86_400_000} d";
    }

    private static string FormatHoursMinutes(long ms)
    {
        var totalMin = ms / 60_000;
        var h = totalMin / 60;
        var m = totalMin % 60;
        return m == 0 ? $"{h} h" : $"{h} h {m} min";
    }
}
