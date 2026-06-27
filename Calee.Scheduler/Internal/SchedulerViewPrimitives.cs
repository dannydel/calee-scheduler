#nullable enable
using System.Globalization;
using Calee.Scheduler.Contracts;
using Microsoft.JSInterop;

namespace Calee.Scheduler.Internal;

/// <summary>
/// Internal, pure helpers shared between time-grid views (Day, Week, and — in Task 9 —
/// TimelineView TimeScale=Day). These are extracted so adding a second consumer of a
/// piece of math does not mean copying it.
/// </summary>
/// <remarks>
/// <para>
/// Anything in here must be:
/// <list type="bullet">
///   <item><description>Pure (no Blazor state, no instance fields).</description></item>
///   <item><description>Stable across renders (so it can be called from any render-phase hook).</description></item>
///   <item><description>Independent of the calling view's type parameters when reasonable
///     (helpers that need <c>TEvent</c> stay as instance methods on the view).</description></item>
/// </list>
/// </para>
/// </remarks>
internal static class SchedulerViewPrimitives
{
    /// <summary>Format an hour-of-day for the time gutter (e.g., "8 AM", "12 PM").</summary>
    internal static string FormatHour(int hour)
    {
        if (hour == 0) return "12 AM";
        if (hour == 12) return "12 PM";
        if (hour == 24) return "12 AM";
        return hour < 12 ? $"{hour} AM" : $"{hour - 12} PM";
    }

    /// <summary>Format an event's start/end as an accessible time range, converted into the supplied zone.</summary>
    internal static string FormatEventTimeRange(ICalendarEvent ev, TimeZoneInfo tz)
    {
        var startLocal = TimeZoneInfo.ConvertTime(ev.Start, tz);
        var endLocal = TimeZoneInfo.ConvertTime(ev.End, tz);
        return $"{startLocal:h:mm tt} to {endLocal:h:mm tt}";
    }

    /// <summary>
    /// Validate the visible-hour parameters and slot duration per PRD §4.6. Throws
    /// <see cref="ArgumentException"/> on violation.
    /// </summary>
    internal static void ValidateHourParameters(int startHour, int endHour, int slotMinutes)
    {
        if (startHour < 0)
        {
            throw new ArgumentException(
                $"StartHour must be >= 0; got {startHour}.", nameof(startHour));
        }
        if (endHour > 24)
        {
            throw new ArgumentException(
                $"EndHour must be <= 24; got {endHour}.", nameof(endHour));
        }
        if (startHour > endHour)
        {
            throw new ArgumentException(
                $"StartHour ({startHour}) must be <= EndHour ({endHour}).", nameof(startHour));
        }
        if (slotMinutes is not (15 or 30 or 60))
        {
            throw new ArgumentException(
                $"SlotDurationMinutes must be one of {{15, 30, 60}}; got {slotMinutes}.",
                nameof(slotMinutes));
        }
    }

    /// <summary>
    /// Compute the visible week's day boundaries given an anchor and a first-day-of-week.
    /// Returns 7 ordered (start, end) pairs in the supplied <paramref name="tz"/>.
    /// </summary>
    /// <param name="anchor">The anchor date (date portion is used; offset is irrelevant per FR-09a).</param>
    /// <param name="firstDayOfWeek">The configured first day of the week (FR-04).</param>
    /// <param name="tz">The view's time zone — used to derive the midnight offset for each day.</param>
    internal static IReadOnlyList<(DateTimeOffset Start, DateTimeOffset End)> ComputeWeekDays(
        DateTimeOffset anchor, DayOfWeek firstDayOfWeek, TimeZoneInfo tz)
    {
        var anchorDate = anchor.Date;
        var delta = (7 + (int)anchorDate.DayOfWeek - (int)firstDayOfWeek) % 7;
        var firstDay = anchorDate.AddDays(-delta);

        var result = new (DateTimeOffset, DateTimeOffset)[7];
        for (var i = 0; i < 7; i++)
        {
            var d = firstDay.AddDays(i);
            var offset = tz.GetUtcOffset(d);
            var start = new DateTimeOffset(d, offset);
            var end = start.AddDays(1);
            result[i] = (start, end);
        }
        return result;
    }

    /// <summary>
    /// Attempt to import the library's JS helper module. Returns <see langword="null"/>
    /// when the JS runtime is unavailable (typical in test environments).
    /// </summary>
    internal static async Task<IJSObjectReference?> TryLoadJsModuleAsync(IJSRuntime js)
    {
        try
        {
            return await js.InvokeAsync<IJSObjectReference>(
                "import", "./_content/Calee.Scheduler/calee-scheduler.js");
        }
        catch (JSException)
        {
            return null;
        }
        catch (InvalidOperationException)
        {
            return null;
        }
    }

    /// <summary>
    /// Compute the hour-offset from the top of the visible grid for the initial scroll
    /// position (FR-09b). Pixels-per-hour is read at runtime by the JS helper from
    /// <c>--calee-scheduler-pixels-per-hour</c>.
    /// </summary>
    /// <param name="todayInZone">"Today" in the view's TimeZone.</param>
    /// <param name="rangeStart">Inclusive start of the visible date range (date portion only used).</param>
    /// <param name="rangeEndExclusive">Exclusive end of the visible date range.</param>
    /// <param name="startHour">Configured StartHour.</param>
    /// <param name="endHour">Configured EndHour.</param>
    internal static double ComputeInitialScrollHourOffset(
        DateTimeOffset todayInZone,
        DateTimeOffset rangeStart,
        DateTimeOffset rangeEndExclusive,
        int startHour,
        int endHour)
    {
        double targetHour;
        var todayInRange = todayInZone.Date >= rangeStart.Date && todayInZone.Date < rangeEndExclusive.Date;
        if (todayInRange)
        {
            targetHour = todayInZone.Hour + todayInZone.Minute / 60.0;
        }
        else
        {
            targetHour = 8;
        }

        if (targetHour < startHour) targetHour = startHour;
        if (targetHour > endHour) targetHour = endHour;
        return targetHour - startHour;
    }

    /// <summary>
    /// Compute the natural-month boundary containing the supplied anchor, in the supplied
    /// time zone. Returns (firstDayOfMonth at midnight, firstDayOfNextMonth at midnight) —
    /// used by TimelineView's <c>TimeScale.Month</c> mode, which spans only the calendar
    /// month rather than the 6-week grid used by <c>CaleeSchedulerMonthView</c>.
    /// </summary>
    internal static (DateTimeOffset Start, DateTimeOffset EndExclusive) ComputeMonthRange(
        DateTimeOffset anchor, TimeZoneInfo tz)
    {
        var d = anchor.Date;
        var firstOfMonth = new DateTime(d.Year, d.Month, 1);
        var firstOfNext = firstOfMonth.AddMonths(1);
        var startOffset = tz.GetUtcOffset(firstOfMonth);
        var endOffset = tz.GetUtcOffset(firstOfNext);
        return (
            new DateTimeOffset(firstOfMonth, startOffset),
            new DateTimeOffset(firstOfNext, endOffset));
    }

    /// <summary>
    /// Format the toolbar's range label per FR-41. English-only in Phase 1 (PRD §8 /
    /// open question resolution).
    /// </summary>
    /// <remarks>
    /// <para>
    /// The culture is pinned to <c>en-US</c> explicitly so consumer-app cultures cannot
    /// drift the format. Localization is a Phase 3 concern (FR-41).
    /// </para>
    /// <para>The en-dash character (<c>–</c>, U+2013) is used for ranges — not a hyphen-minus.</para>
    /// <list type="bullet">
    ///   <item><description>Day → <c>"Tue, Mar 18, 2026"</c> (<c>ddd, MMM d, yyyy</c>).</description></item>
    ///   <item><description>Week same-month → <c>"Mar 18 – 24, 2026"</c>.</description></item>
    ///   <item><description>Week cross-month → <c>"Mar 29 – Apr 4, 2026"</c>.</description></item>
    ///   <item><description>Week cross-year → <c>"Dec 29, 2025 – Jan 4, 2026"</c>.</description></item>
    ///   <item><description>Month → <c>"March 2026"</c> (<c>MMMM yyyy</c>).</description></item>
    ///   <item><description>Year → <c>"2026"</c> (<c>yyyy</c>).</description></item>
    ///   <item><description>Agenda → window spanning <paramref name="agendaDays"/> days
    ///     starting at <paramref name="date"/>. For <c>AgendaDays=1</c> the label
    ///     degenerates to the Day shape (<c>"Tue, Mar 17, 2026"</c>); otherwise the
    ///     label follows Week's same-month / cross-month / cross-year branches —
    ///     <c>"Mar 18 – 24, 2026"</c>, <c>"Mar 29 – Apr 4, 2026"</c>, or
    ///     <c>"Dec 29, 2025 – Jan 4, 2026"</c>.</description></item>
    ///   <item><description>Timeline → delegates to the matching scale (Day/Week/Month).</description></item>
    /// </list>
    /// </remarks>
    /// <param name="view">The active view.</param>
    /// <param name="date">The anchor date — typically the bindable <c>Date</c> of the active view.</param>
    /// <param name="tz">The grid time zone the view uses for date math.</param>
    /// <param name="firstDayOfWeek">The configured first day of the week (FR-04); drives the Week branch.</param>
    /// <param name="timelineScale">The current TimelineScale when <paramref name="view"/> is <see cref="SchedulerView.Timeline"/>; ignored otherwise.</param>
    /// <param name="agendaDays">
    /// Length of the Agenda window in days; ignored for non-Agenda views. Must be
    /// <c>&gt;= 1</c> when supplied; defaults to <c>7</c> to match
    /// <c>CaleeSchedulerAgendaView.AgendaDays</c>'s default.
    /// </param>
    internal static string FormatRangeLabel(
        SchedulerView view,
        DateTimeOffset date,
        TimeZoneInfo tz,
        DayOfWeek firstDayOfWeek,
        TimelineScale? timelineScale = null,
        int agendaDays = 7)
    {
        var culture = CultureInfo.GetCultureInfo("en-US");

        switch (view)
        {
            case SchedulerView.Day:
                return date.ToString("ddd, MMM d, yyyy", culture);

            case SchedulerView.Week:
            {
                var days = ComputeWeekDays(date, firstDayOfWeek, tz);
                var weekStart = days[0].Start;
                // The week's inclusive end day is six days after the start (days[6] is the
                // last (start, end) entry — its Start is the inclusive end day).
                var weekEndInclusive = days[6].Start;
                return FormatWeekRange(weekStart, weekEndInclusive, culture);
            }

            case SchedulerView.Month:
                return date.ToString("MMMM yyyy", culture);

            case SchedulerView.Year:
                return date.ToString("yyyy", culture);

            case SchedulerView.Agenda:
            {
                // Defensive clamp on agendaDays so a degenerate caller can't produce a
                // negative-length window. The view-side parameter is already clamped on
                // set (per CaleeSchedulerAgendaView.AgendaDays), but FormatRangeLabel is
                // an internal pure helper that callers can drive directly in tests.
                var days = agendaDays < 1 ? 1 : agendaDays;
                if (days == 1)
                {
                    // Single-day window — fall back to the Day shape so the label reads
                    // identically to a Day-view header for that date.
                    return date.ToString("ddd, MMM d, yyyy", culture);
                }
                // Multi-day window — reuse Week's same-month / cross-month / cross-year
                // branches. Compute the inclusive end day as the anchor + (days - 1).
                var windowStartDate = date.Date;
                var startOffset = tz.GetUtcOffset(windowStartDate);
                var windowStart = new DateTimeOffset(windowStartDate, startOffset);
                var endInclusiveDate = windowStartDate.AddDays(days - 1);
                var endOffset = tz.GetUtcOffset(endInclusiveDate);
                var windowEndInclusive = new DateTimeOffset(endInclusiveDate, endOffset);
                return FormatWeekRange(windowStart, windowEndInclusive, culture);
            }

            case SchedulerView.Timeline:
            {
                var scale = timelineScale ?? TimelineScale.Day;
                return scale switch
                {
                    TimelineScale.Day => FormatRangeLabel(SchedulerView.Day, date, tz, firstDayOfWeek),
                    TimelineScale.Week => FormatRangeLabel(SchedulerView.Week, date, tz, firstDayOfWeek),
                    TimelineScale.Month => FormatRangeLabel(SchedulerView.Month, date, tz, firstDayOfWeek),
                    _ => FormatRangeLabel(SchedulerView.Day, date, tz, firstDayOfWeek),
                };
            }

            default:
                return date.ToString("ddd, MMM d, yyyy", culture);
        }
    }

    /// <summary>
    /// Format the Week range label per FR-41. The three branches are: same-month,
    /// cross-month/same-year, and cross-year.
    /// </summary>
    private static string FormatWeekRange(
        DateTimeOffset weekStart, DateTimeOffset weekEndInclusive, CultureInfo culture)
    {
        const string EnDash = "–";

        if (weekStart.Year != weekEndInclusive.Year)
        {
            // Cross-year: "Dec 29, 2025 – Jan 4, 2026"
            var start = weekStart.ToString("MMM d, yyyy", culture);
            var end = weekEndInclusive.ToString("MMM d, yyyy", culture);
            return $"{start} {EnDash} {end}";
        }

        if (weekStart.Month != weekEndInclusive.Month)
        {
            // Cross-month, same year: "Mar 29 – Apr 4, 2026"
            var start = weekStart.ToString("MMM d", culture);
            var end = weekEndInclusive.ToString("MMM d, yyyy", culture);
            return $"{start} {EnDash} {end}";
        }

        // Same month: "Mar 18 – 24, 2026"
        var startSame = weekStart.ToString("MMM d", culture);
        var endSame = weekEndInclusive.ToString("d, yyyy", culture);
        return $"{startSame} {EnDash} {endSame}";
    }

    /// <summary>
    /// Compute the new anchor date for a Prev/Next click on the toolbar (FR-40). The
    /// <paramref name="direction"/> is <c>-1</c> for Previous and <c>+1</c> for Next.
    /// </summary>
    /// <remarks>
    /// <para>
    /// For Month the new offset is taken from <paramref name="tz"/> at the new date so
    /// DST and zone transitions are honored (avoids carrying the old offset into a date
    /// that lives in the other side of a transition).
    /// </para>
    /// <para>
    /// Agenda steps by <paramref name="agendaDays"/> days (the locked decision from
    /// phase-2-plan §5.3 Q16 — prev/next moves by the window length, not by one day or
    /// a fixed week). The offset on the returned anchor is recomputed at the new date so
    /// DST transitions are honored on long windows that straddle a DST boundary.
    /// </para>
    /// </remarks>
    /// <param name="view">The active view.</param>
    /// <param name="date">The current anchor date.</param>
    /// <param name="direction"><c>-1</c> for Previous, <c>+1</c> for Next.</param>
    /// <param name="tz">The grid time zone used to resolve the new anchor's offset.</param>
    /// <param name="timelineScale">The current TimelineScale when <paramref name="view"/> is <see cref="SchedulerView.Timeline"/>; ignored otherwise.</param>
    /// <param name="agendaDays">
    /// Window length used by the Agenda step; ignored for non-Agenda views. Must be
    /// <c>&gt;= 1</c>; defaults to <c>7</c>.
    /// </param>
    internal static DateTimeOffset AdvanceAnchor(
        SchedulerView view,
        DateTimeOffset date,
        int direction,
        TimeZoneInfo tz,
        TimelineScale? timelineScale = null,
        int agendaDays = 7)
    {
        switch (view)
        {
            case SchedulerView.Day:
                return date.AddDays(direction);

            case SchedulerView.Week:
                return date.AddDays(direction * 7);

            case SchedulerView.Month:
            {
                var newDate = date.Date.AddMonths(direction);
                var offset = tz.GetUtcOffset(newDate);
                return new DateTimeOffset(newDate, offset);
            }

            case SchedulerView.Year:
            {
                var newDate = date.Date.AddYears(direction);
                var offset = tz.GetUtcOffset(newDate);
                return new DateTimeOffset(newDate, offset);
            }

            case SchedulerView.Agenda:
            {
                var days = agendaDays < 1 ? 1 : agendaDays;
                var newDate = date.Date.AddDays(direction * days);
                var offset = tz.GetUtcOffset(newDate);
                return new DateTimeOffset(newDate, offset);
            }

            case SchedulerView.Timeline:
            {
                var scale = timelineScale ?? TimelineScale.Day;
                return scale switch
                {
                    TimelineScale.Day => AdvanceAnchor(SchedulerView.Day, date, direction, tz),
                    TimelineScale.Week => AdvanceAnchor(SchedulerView.Week, date, direction, tz),
                    TimelineScale.Month => AdvanceAnchor(SchedulerView.Month, date, direction, tz),
                    _ => AdvanceAnchor(SchedulerView.Day, date, direction, tz),
                };
            }

            default:
                return date.AddDays(direction);
        }
    }

    /// <summary>
    /// Compute one (start, end) midnight-midnight bound per day in the supplied half-open
    /// range, in the supplied time zone. Used by TimelineView's <c>TimeScale.Month</c>
    /// (one entry per day in the month) and <c>TimeScale.Week</c> (delegates to
    /// <see cref="ComputeWeekDays"/>).
    /// </summary>
    internal static IReadOnlyList<(DateTimeOffset Start, DateTimeOffset End)> ComputeDayBounds(
        DateTimeOffset rangeStart, DateTimeOffset rangeEndExclusive, TimeZoneInfo tz)
    {
        var startDate = rangeStart.Date;
        var endDate = rangeEndExclusive.Date;
        // If the end isn't exactly on midnight, round up to include the partial last day.
        if (rangeEndExclusive > new DateTimeOffset(endDate, tz.GetUtcOffset(endDate)))
        {
            endDate = endDate.AddDays(1);
        }
        var dayCount = (int)(endDate - startDate).TotalDays;
        if (dayCount < 1) dayCount = 1;
        var result = new (DateTimeOffset, DateTimeOffset)[dayCount];
        for (var i = 0; i < dayCount; i++)
        {
            var d = startDate.AddDays(i);
            var off = tz.GetUtcOffset(d);
            var s = new DateTimeOffset(d, off);
            var e = s.AddDays(1);
            result[i] = (s, e);
        }
        return result;
    }
}
