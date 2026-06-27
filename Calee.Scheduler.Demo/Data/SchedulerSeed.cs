#nullable enable
using Calee.Scheduler.Contracts;

namespace Calee.Scheduler.Demo.Data;

/// <summary>
/// Centralized fixture data for the demo. Each method returns events anchored
/// relative to a supplied "today" so the demo always shows live-feeling content
/// — the current-time indicator lands inside a populated day and the "today"
/// highlight is visible on every view.
/// </summary>
/// <remarks>
/// <para>
/// Every method takes <see cref="TimeZoneInfo"/> explicitly because the library
/// never converts time zones (ADR-0001) — the demo normalizes upstream so all
/// emitted events carry the offset of the display zone for the requested day.
/// </para>
/// <para>
/// The palette here is editorial-warm: a small set of muted hues (terracotta,
/// olive, slate-blue, ochre, plum) that read against the demo's #fbfaf7 surface.
/// Avoid bright blues and saturated greens — they fight the page chrome.
/// </para>
/// </remarks>
internal static class SchedulerSeed
{
    /// <summary>Default demo timezone — anchored to a US-eastern interpretation of "today".</summary>
    public static TimeZoneInfo DefaultTimeZone { get; } =
        TimeZoneInfo.FindSystemTimeZoneById("America/New_York");

    // ── Palette ──────────────────────────────────────────────────────────────
    // Used inline by the seed events so each scenario reads visually as a story:
    // similar-purpose events share a hue across views.
    private const string ColorTerracotta = "#b1462d";
    private const string ColorOlive = "#7a7038";
    private const string ColorSlate = "#4a6079";
    private const string ColorOchre = "#c08832";
    private const string ColorPlum = "#7a3b54";
    private const string ColorSage = "#6b7a5a";
    private const string ColorClay = "#9a5f3d";

    // ── Drivers (Timeline view) ──────────────────────────────────────────────

    private static readonly IReadOnlyList<ILane> _drivers = new ILane[]
    {
        new Lane("alex",   "Alex Chen",     ColorSlate),
        new Lane("maria",  "Maria Garcia",  ColorTerracotta),
        new Lane("jordan", "Jordan Park",   ColorSage),
    };

    /// <summary>Returns the demo's three drivers in display order. Cached.</summary>
    public static IReadOnlyList<ILane> GetDrivers() => _drivers;

    // ── Helpers ──────────────────────────────────────────────────────────────

    /// <summary>
    /// Build a <see cref="DateTimeOffset"/> for the supplied date + time-of-day in
    /// the requested time zone, carrying that zone's UTC offset for the date.
    /// Centralized so individual seed events don't have to fight DST math.
    /// </summary>
    private static DateTimeOffset At(DateTime date, int hour, int minute, TimeZoneInfo tz)
    {
        var dateOnly = date.Date.AddHours(hour).AddMinutes(minute);
        var offset = tz.GetUtcOffset(dateOnly);
        return new DateTimeOffset(dateOnly, offset);
    }

    /// <summary>The midnight start of the supplied date, in the supplied time zone.</summary>
    private static DateTimeOffset Midnight(DateTime date, TimeZoneInfo tz) => At(date, 0, 0, tz);

    /// <summary>
    /// "Today" in the supplied time zone, derived from the consumer-supplied anchor.
    /// Use this instead of <c>anchor.LocalDateTime.Date</c> — the latter reads the
    /// anchor's own offset (typically UTC from <see cref="DateTimeOffset.UtcNow"/>),
    /// which can be off-by-one from the actual zone wall-clock near midnight.
    /// </summary>
    private static DateTime TodayInZone(DateTimeOffset anchor, TimeZoneInfo tz)
        => TimeZoneInfo.ConvertTime(anchor, tz).Date;

    /// <summary>The first day of the week containing <paramref name="anchor"/>, anchored at midnight.</summary>
    private static DateTime StartOfWeek(DateTime anchor, DayOfWeek firstDayOfWeek)
    {
        var date = anchor.Date;
        var offset = ((int)date.DayOfWeek - (int)firstDayOfWeek + 7) % 7;
        return date.AddDays(-offset);
    }

    // ── Week-view seed ───────────────────────────────────────────────────────

    /// <summary>
    /// Week-view fixtures. Surfaces, in this order: a Mon–Wed multi-day all-day
    /// "Vacation" block; a Mon-11pm → Tue-2am timed event that crosses midnight;
    /// a Thursday three-way overlap (A: 9–10, B: 9:30–10:30, C: 10–11); plus
    /// supporting events on the remaining weekdays so the grid isn't bare.
    /// </summary>
    public static IReadOnlyList<CalendarEvent> GetWeekSeed(DateTimeOffset anchor, TimeZoneInfo tz)
    {
        var weekStart = StartOfWeek(TodayInZone(anchor, tz), DayOfWeek.Sunday);
        var mon = weekStart.AddDays(1);
        var tue = weekStart.AddDays(2);
        var wed = weekStart.AddDays(3);
        var thu = weekStart.AddDays(4);
        var fri = weekStart.AddDays(5);

        return new List<CalendarEvent>
        {
            // Multi-day ALL-DAY block: Vacation Mon–Wed. End is exclusive (Thu 00:00).
            new(
                Id: "vacation",
                Title: "Vacation",
                Start: Midnight(mon, tz),
                End: Midnight(thu, tz),
                IsAllDay: true,
                Color: ColorTerracotta),

            // Timed event crossing midnight — Mon 11 PM → Tue 2 AM.
            new(
                Id: "route-overnight",
                Title: "Route 6 → Portland",
                Start: At(mon, 23, 0, tz),
                End: At(tue, 2, 0, tz),
                Color: ColorClay),

            // Tuesday: a moderate block.
            new(
                Id: "tue-warehouse",
                Title: "Warehouse audit",
                Start: At(tue, 14, 0, tz),
                End: At(tue, 16, 30, tz),
                Color: ColorSlate),

            // Wednesday: a quick standup overlapping a longer meeting.
            new(
                Id: "wed-standup",
                Title: "Standup",
                Start: At(wed, 9, 15, tz),
                End: At(wed, 9, 30, tz),
                Color: ColorOlive),
            new(
                Id: "wed-design",
                Title: "Design review",
                Start: At(wed, 9, 0, tz),
                End: At(wed, 10, 30, tz),
                Color: ColorSage),

            // Thursday three-way A/B/C overlap (ADR-0003 case study).
            new(
                Id: "thu-A",
                Title: "Customer sync",
                Start: At(thu, 9, 0, tz),
                End: At(thu, 10, 0, tz),
                Color: ColorPlum),
            new(
                Id: "thu-B",
                Title: "Hiring loop",
                Start: At(thu, 9, 30, tz),
                End: At(thu, 10, 30, tz),
                Color: ColorOchre),
            new(
                Id: "thu-C",
                Title: "Vendor call",
                Start: At(thu, 10, 0, tz),
                End: At(thu, 11, 0, tz),
                Color: ColorTerracotta),

            // Thursday afternoon — a longer focus block.
            new(
                Id: "thu-focus",
                Title: "Quarterly planning",
                Start: At(thu, 13, 0, tz),
                End: At(thu, 17, 0, tz),
                Color: ColorSlate),

            // Friday: a single chip + an early-morning event that lands inside the band.
            new(
                Id: "fri-1on1",
                Title: "1:1 with Maria",
                Start: At(fri, 11, 0, tz),
                End: At(fri, 11, 30, tz),
                Color: ColorOlive),
            new(
                Id: "fri-followup",
                Title: "Customer follow-ups",
                Start: At(fri, 15, 0, tz),
                End: At(fri, 16, 0, tz),
                Color: ColorSlate),
        };
    }

    // ── Day-view seed ────────────────────────────────────────────────────────

    /// <summary>
    /// Day-view fixtures anchored on the supplied date. Surfaces: a 6:30 AM event
    /// outside the band (StartHour=8) so the "+N earlier" chip appears; an end-of-
    /// day event that the band crops; the A/B/C three-way overlap cluster mid-morning.
    /// </summary>
    public static IReadOnlyList<CalendarEvent> GetDaySeed(DateTimeOffset anchor, TimeZoneInfo tz)
    {
        var d = TodayInZone(anchor, tz);
        return new List<CalendarEvent>
        {
            // Pre-band: 6:30 AM → 7:30 AM. Outside the demo StartHour=8 → surfaces "+1 earlier".
            new(
                Id: "day-early",
                Title: "Airport pickup",
                Start: At(d, 6, 30, tz),
                End: At(d, 7, 30, tz),
                Color: ColorClay),

            // A/B/C three-way overlap mid-morning (the canonical ADR-0003 case).
            new(
                Id: "day-A",
                Title: "Customer sync",
                Start: At(d, 9, 0, tz),
                End: At(d, 10, 0, tz),
                Color: ColorPlum),
            new(
                Id: "day-B",
                Title: "Hiring loop",
                Start: At(d, 9, 30, tz),
                End: At(d, 10, 30, tz),
                Color: ColorOchre),
            new(
                Id: "day-C",
                Title: "Vendor call",
                Start: At(d, 10, 0, tz),
                End: At(d, 11, 0, tz),
                Color: ColorTerracotta),

            // Midday: focus block.
            new(
                Id: "day-focus",
                Title: "Deep work — Q3 plan",
                Start: At(d, 12, 30, tz),
                End: At(d, 14, 30, tz),
                Color: ColorSlate),

            // Afternoon: a couple of standalone meetings.
            new(
                Id: "day-1on1",
                Title: "1:1 with Jordan",
                Start: At(d, 15, 0, tz),
                End: At(d, 15, 30, tz),
                Color: ColorOlive),
            new(
                Id: "day-design",
                Title: "Design review",
                Start: At(d, 16, 0, tz),
                End: At(d, 17, 30, tz),
                Color: ColorSage),

            // Late event that ends past EndHour=20 — surfaces clip indicator at bottom.
            new(
                Id: "day-evening",
                Title: "Customer dinner",
                Start: At(d, 19, 30, tz),
                End: At(d, 21, 0, tz),
                Color: ColorTerracotta),

            // All-day banner.
            new(
                Id: "day-allday",
                Title: "Inventory week",
                Start: Midnight(d, tz),
                End: Midnight(d.AddDays(1), tz),
                IsAllDay: true,
                Color: ColorOchre),
        };
    }

    // ── Month-view seed ──────────────────────────────────────────────────────

    /// <summary>
    /// Month-view fixtures. Surfaces: five events on the 15th (cap=3 → "+2 more");
    /// a 4-day multi-day bar inside one week; a multi-day bar that crosses a week
    /// boundary; plus a smattering of single-day chips on other dates so the grid
    /// reads like a real month.
    /// </summary>
    public static IReadOnlyList<CalendarEvent> GetMonthSeed(DateTimeOffset anchor, TimeZoneInfo tz)
    {
        var firstOfMonth = new DateTime(anchor.Year, anchor.Month, 1);
        var fifteenth = firstOfMonth.AddDays(14);

        // Find a Monday that's at least a week into the month for the in-week bar.
        var inWeekBarStart = firstOfMonth.AddDays(7);
        while (inWeekBarStart.DayOfWeek != DayOfWeek.Monday)
        {
            inWeekBarStart = inWeekBarStart.AddDays(1);
        }

        // A second multi-day bar that crosses a week boundary — start on a Thursday
        // and end the following Tuesday (6 days).
        var crossWeekStart = inWeekBarStart.AddDays(10);
        while (crossWeekStart.DayOfWeek != DayOfWeek.Thursday)
        {
            crossWeekStart = crossWeekStart.AddDays(1);
        }

        var seed = new List<CalendarEvent>();

        // ── Five events on the 15th — chip cap = 3 should fire "+2 more" ──
        seed.Add(new CalendarEvent(
            Id: "m15-1",
            Title: "Board meeting",
            Start: At(fifteenth, 9, 0, tz),
            End: At(fifteenth, 10, 30, tz),
            Color: ColorSlate));
        seed.Add(new CalendarEvent(
            Id: "m15-2",
            Title: "Vendor review",
            Start: At(fifteenth, 11, 0, tz),
            End: At(fifteenth, 11, 45, tz),
            Color: ColorOlive));
        seed.Add(new CalendarEvent(
            Id: "m15-3",
            Title: "Onboarding lunch",
            Start: At(fifteenth, 12, 30, tz),
            End: At(fifteenth, 13, 30, tz),
            Color: ColorOchre));
        seed.Add(new CalendarEvent(
            Id: "m15-4",
            Title: "Design review",
            Start: At(fifteenth, 14, 0, tz),
            End: At(fifteenth, 15, 30, tz),
            Color: ColorSage));
        seed.Add(new CalendarEvent(
            Id: "m15-5",
            Title: "Customer dinner",
            Start: At(fifteenth, 18, 30, tz),
            End: At(fifteenth, 21, 0, tz),
            Color: ColorTerracotta));

        // ── 4-day multi-day bar inside a single week (Mon → Thu). All-day. ──
        seed.Add(new CalendarEvent(
            Id: "m-conf",
            Title: "Industry conference — Chicago",
            Start: Midnight(inWeekBarStart, tz),
            End: Midnight(inWeekBarStart.AddDays(4), tz),
            IsAllDay: true,
            Color: ColorPlum));

        // ── 6-day multi-day bar crossing a week boundary (Thu → Tue). All-day. ──
        seed.Add(new CalendarEvent(
            Id: "m-launch",
            Title: "Launch week",
            Start: Midnight(crossWeekStart, tz),
            End: Midnight(crossWeekStart.AddDays(6), tz),
            IsAllDay: true,
            Color: ColorTerracotta));

        // ── Scatter a few more single-day chips for visual texture ──
        var today = TodayInZone(anchor, tz);
        seed.Add(new CalendarEvent(
            Id: "m-today",
            Title: "Coffee with Maria",
            Start: At(today, 10, 0, tz),
            End: At(today, 10, 30, tz),
            Color: ColorOlive));
        seed.Add(new CalendarEvent(
            Id: "m-today-pm",
            Title: "Site walk",
            Start: At(today, 14, 0, tz),
            End: At(today, 15, 30, tz),
            Color: ColorClay));

        var second = firstOfMonth.AddDays(1);
        seed.Add(new CalendarEvent(
            Id: "m-second",
            Title: "All-hands",
            Start: At(second, 10, 0, tz),
            End: At(second, 11, 0, tz),
            Color: ColorSlate));

        var thirdLast = new DateTime(anchor.Year, anchor.Month,
            DateTime.DaysInMonth(anchor.Year, anchor.Month) - 2);
        seed.Add(new CalendarEvent(
            Id: "m-thirdlast",
            Title: "Quarterly report due",
            Start: At(thirdLast, 17, 0, tz),
            End: At(thirdLast, 17, 30, tz),
            Color: ColorOchre));

        return seed;
    }

    // ── Year-view seed ───────────────────────────────────────────────────────

    /// <summary>
    /// Year-view fixtures spread across the displayed year. Surfaces: a quiet first
    /// quarter, a busy mid-year stretch with a few high-density days (bucket 3 — 5+
    /// events on the 15th of the anchor month), several multi-day all-day spans that
    /// stretch density across consecutive cells, a scatter of single-day chips, and
    /// a deliberately empty late-year stretch so the bucket-0 ("no indicator") state
    /// reads on the page.
    /// </summary>
    public static IReadOnlyList<CalendarEvent> GetYearSeed(DateTimeOffset anchor, TimeZoneInfo tz)
    {
        var year = anchor.Year;
        var seed = new List<CalendarEvent>();

        // ── Q1 (quiet): one quarterly review per month ──
        for (var m = 1; m <= 3; m++)
        {
            var d = new DateTime(year, m, 15);
            seed.Add(new CalendarEvent(
                Id: $"y-q1-{m}",
                Title: "Quarterly review",
                Start: At(d, 9, 0, tz),
                End: At(d, 10, 30, tz),
                Color: ColorSlate));
        }

        // ── Q2: a busier stretch around the anchor month ──
        var anchorMonth = anchor.Month;
        var anchorMidMonth = new DateTime(year, anchorMonth, 15);

        // Five events on the 15th — yields density bucket 3 (5+).
        seed.Add(new CalendarEvent($"y-busy-1", "Board meeting",
            At(anchorMidMonth, 9, 0, tz), At(anchorMidMonth, 10, 30, tz), Color: ColorSlate));
        seed.Add(new CalendarEvent($"y-busy-2", "Vendor review",
            At(anchorMidMonth, 11, 0, tz), At(anchorMidMonth, 11, 45, tz), Color: ColorOlive));
        seed.Add(new CalendarEvent($"y-busy-3", "Onboarding lunch",
            At(anchorMidMonth, 12, 30, tz), At(anchorMidMonth, 13, 30, tz), Color: ColorOchre));
        seed.Add(new CalendarEvent($"y-busy-4", "Design review",
            At(anchorMidMonth, 14, 0, tz), At(anchorMidMonth, 15, 30, tz), Color: ColorSage));
        seed.Add(new CalendarEvent($"y-busy-5", "Customer dinner",
            At(anchorMidMonth, 18, 30, tz), At(anchorMidMonth, 21, 0, tz), Color: ColorTerracotta));

        // Two events on the 16th — bucket 2.
        var anchorMidPlus1 = anchorMidMonth.AddDays(1);
        seed.Add(new CalendarEvent($"y-busy-6", "Follow-up",
            At(anchorMidPlus1, 10, 0, tz), At(anchorMidPlus1, 10, 30, tz), Color: ColorClay));
        seed.Add(new CalendarEvent($"y-busy-7", "Site walk",
            At(anchorMidPlus1, 14, 0, tz), At(anchorMidPlus1, 15, 30, tz), Color: ColorPlum));

        // Multi-day all-day spans — each contributes 1/day per touched date.
        var confStart = new DateTime(year, 6, 3);
        seed.Add(new CalendarEvent(
            Id: "y-conf",
            Title: "Industry conference",
            Start: Midnight(confStart, tz),
            End: Midnight(confStart.AddDays(4), tz),
            IsAllDay: true,
            Color: ColorPlum));

        var trainingStart = new DateTime(year, 9, 22);
        seed.Add(new CalendarEvent(
            Id: "y-training",
            Title: "Engineering offsite",
            Start: Midnight(trainingStart, tz),
            End: Midnight(trainingStart.AddDays(3), tz),
            IsAllDay: true,
            Color: ColorTerracotta));

        // ── Scattered single-day chips through the year ──
        var single = new[]
        {
            (new DateTime(year, 1, 5), "Kickoff", ColorTerracotta),
            (new DateTime(year, 2, 20), "Hire round", ColorOlive),
            (new DateTime(year, 4, 14), "All-hands", ColorSlate),
            (new DateTime(year, 5, 1), "Customer demo", ColorOchre),
            (new DateTime(year, 7, 4), "Holiday party", ColorTerracotta),
            (new DateTime(year, 8, 9), "Strategy retreat", ColorSage),
            (new DateTime(year, 10, 28), "Quarterly close", ColorPlum),
        };
        var idx = 0;
        foreach (var (d, title, color) in single)
        {
            seed.Add(new CalendarEvent(
                Id: $"y-sing-{idx++}",
                Title: title,
                Start: At(d, 9, 0, tz),
                End: At(d, 10, 0, tz),
                Color: color));
        }

        // ── Today gets one event so the current-date highlight has signal next to it ──
        var today = TodayInZone(anchor, tz);
        if (today.Year == year)
        {
            seed.Add(new CalendarEvent(
                Id: "y-today",
                Title: "Stand-up",
                Start: At(today, 9, 0, tz),
                End: At(today, 9, 30, tz),
                Color: ColorOlive));
        }

        // Late November + December are left deliberately sparse so the bucket-0 cells
        // read on the page.

        return seed;
    }

    // ── Agenda-view seed ─────────────────────────────────────────────────────

    /// <summary>
    /// Agenda-view fixtures anchored on the supplied date. Exercises the locked
    /// design decisions from phase-2-plan §5.3 Q16:
    /// <list type="bullet">
    ///   <item>One event on "today" — visible immediately on first render.</item>
    ///   <item>Two events scattered across the default 7-day window (one timed, one
    ///     all-day mid-window) — verifies date grouping.</item>
    ///   <item>One empty day in the middle of the window — verifies the hidden-
    ///     empty-days rule (the day's date should NOT appear as a header).</item>
    ///   <item>A 4-day multi-day all-day event spanning a window boundary —
    ///     verifies the once-with-range-label rule AND the boundary handling.</item>
    ///   <item>One event whose start is before the window but extends into it —
    ///     verifies the leading-edge pin behavior (row pins to the window's
    ///     first day with the original range label).</item>
    /// </list>
    /// </summary>
    /// <param name="anchor">The window's start anchor — typically the bindable Date.</param>
    /// <param name="tz">The configured time zone for date math.</param>
    public static IReadOnlyList<CalendarEvent> GetAgendaSeed(DateTimeOffset anchor, TimeZoneInfo tz)
    {
        var d0 = TodayInZone(anchor, tz); // The anchor's date in the zone.
        var seed = new List<CalendarEvent>();

        // ── Today: a couple events so the today highlight has signal ──
        seed.Add(new CalendarEvent(
            Id: "ag-today-1",
            Title: "Standup",
            Start: At(d0, 9, 0, tz),
            End: At(d0, 9, 30, tz),
            Color: ColorOlive));
        seed.Add(new CalendarEvent(
            Id: "ag-today-2",
            Title: "Customer call",
            Start: At(d0, 11, 0, tz),
            End: At(d0, 12, 0, tz),
            Color: ColorSlate));

        // ── Day +1: nothing (this day stays out of the rendered list — verifies
        // the hidden-empty-days rule). ──

        // ── Day +2: an all-day event (banner-style row, "All day" label) ──
        var d2 = d0.AddDays(2);
        seed.Add(new CalendarEvent(
            Id: "ag-day2-allday",
            Title: "Customer demo",
            Start: Midnight(d2, tz),
            End: Midnight(d2.AddDays(1), tz),
            IsAllDay: true,
            Color: ColorTerracotta));

        // ── Day +3: one timed event ──
        var d3 = d0.AddDays(3);
        seed.Add(new CalendarEvent(
            Id: "ag-day3",
            Title: "Design review",
            Start: At(d3, 14, 0, tz),
            End: At(d3, 15, 30, tz),
            Color: ColorSage));

        // ── 4-day multi-day all-day spanning the window's trailing edge ──
        // Starts on day +5 (inside the default 7-day window) and runs through
        // day +8 (outside the default window). Verifies that a multi-day event
        // anchored INSIDE the window renders once at its start.
        var d5 = d0.AddDays(5);
        seed.Add(new CalendarEvent(
            Id: "ag-conf",
            Title: "Industry conference",
            Start: Midnight(d5, tz),
            End: Midnight(d5.AddDays(4), tz),
            IsAllDay: true,
            Color: ColorPlum));

        // ── Leading-edge pin case: start BEFORE the window, end INSIDE it ──
        // Started day -3 → ends day +1 (inside the 7-day window). The agenda
        // should pin this row to the window's first day (= today) with the
        // ORIGINAL range label still showing.
        var dMinus3 = d0.AddDays(-3);
        seed.Add(new CalendarEvent(
            Id: "ag-rollover",
            Title: "Rolling release window",
            Start: At(dMinus3, 9, 0, tz),
            End: At(d0.AddDays(1), 17, 0, tz),
            Color: ColorOchre));

        // ── Day +6: late-window single-day timed event ──
        var d6 = d0.AddDays(6);
        seed.Add(new CalendarEvent(
            Id: "ag-day6",
            Title: "Quarterly close prep",
            Start: At(d6, 15, 0, tz),
            End: At(d6, 16, 0, tz),
            Color: ColorClay));

        return seed;
    }

    // ── Fleet (Timeline view) seed ───────────────────────────────────────────

    /// <summary>
    /// Fleet-view fixtures anchored on the supplied date. Surfaces, in this order:
    /// per-driver overlapping routes for Day mode; at least one all-day event for
    /// a driver (the banner strip case); one event whose LaneKey returns an
    /// id NOT in the drivers list (lands in the trailing unassigned row).
    /// </summary>
    public static IReadOnlyList<FleetEvent> GetFleetEvents(DateTimeOffset anchor, TimeZoneInfo tz)
    {
        var d = TodayInZone(anchor, tz);

        return new List<FleetEvent>
        {
            // ── Alex — three runs, two of which overlap so the engine has to lane them ──
            new(
                Id: "alex-1",
                Title: "Route 12 — Bay Area",
                Start: At(d, 7, 30, tz),
                End: At(d, 10, 0, tz),
                DriverId: "alex",
                Color: ColorSlate),
            new(
                Id: "alex-2",
                Title: "Refuel + inspection",
                Start: At(d, 9, 30, tz),
                End: At(d, 10, 30, tz),
                DriverId: "alex",
                Color: ColorOlive),
            new(
                Id: "alex-3",
                Title: "Route 4 — Oakland",
                Start: At(d, 13, 0, tz),
                End: At(d, 16, 0, tz),
                DriverId: "alex",
                Color: ColorSlate),

            // ── Maria — overlapping morning + afternoon, plus an all-day banner ──
            new(
                Id: "maria-1",
                Title: "Long-haul Route 22",
                Start: At(d, 8, 0, tz),
                End: At(d, 12, 30, tz),
                DriverId: "maria",
                Color: ColorTerracotta),
            new(
                Id: "maria-2",
                Title: "Customer drop — Salinas",
                Start: At(d, 11, 30, tz),
                End: At(d, 13, 0, tz),
                DriverId: "maria",
                Color: ColorClay),
            new(
                Id: "maria-3",
                Title: "Return run",
                Start: At(d, 14, 30, tz),
                End: At(d, 17, 0, tz),
                DriverId: "maria",
                Color: ColorTerracotta),

            // All-day banner — surfaces in the resource label area (FR-09e).
            new(
                Id: "maria-allday",
                Title: "On-call shift",
                Start: Midnight(d, tz),
                End: Midnight(d.AddDays(1), tz),
                DriverId: "maria",
                IsAllDay: true,
                Color: ColorOchre),

            // ── Jordan — a single morning route, then a long late-shift block ──
            new(
                Id: "jordan-1",
                Title: "Route 7 — Sausalito",
                Start: At(d, 9, 0, tz),
                End: At(d, 11, 30, tz),
                DriverId: "jordan",
                Color: ColorSage),
            new(
                Id: "jordan-2",
                Title: "Evening run — SFO",
                Start: At(d, 15, 0, tz),
                End: At(d, 18, 0, tz),
                DriverId: "jordan",
                Color: ColorSage),

            // ── Unassigned: LaneKey returns "casey" which is NOT in GetDrivers().
            // Lands in the trailing "Unassigned" row per FR-09d. ──
            new(
                Id: "unassigned-1",
                Title: "Emergency call — needs assignment",
                Start: At(d, 10, 30, tz),
                End: At(d, 12, 0, tz),
                DriverId: "casey",
                Color: ColorPlum),
        };
    }

    /// <summary>
    /// Demo event type carrying a driver id. The Timeline view reads <see cref="DriverId"/>
    /// via a <c>LaneKey</c> projection — the library never reaches into a "DriverId"
    /// field directly (ADR-0011: events know nothing about lanes; consumers map them in).
    /// </summary>
    /// <param name="Id">Stable identifier.</param>
    /// <param name="Title">Display title.</param>
    /// <param name="Start">Start instant.</param>
    /// <param name="End">End instant.</param>
    /// <param name="DriverId">The intended driver id; <see langword="null"/> would also route to unassigned.</param>
    /// <param name="IsAllDay">True for the on-call banner case.</param>
    /// <param name="Color">Per-event hue.</param>
    public sealed record FleetEvent(
        string Id,
        string Title,
        DateTimeOffset Start,
        DateTimeOffset End,
        string? DriverId,
        bool IsAllDay = false,
        string? Color = null
    ) : ICalendarEvent;
}
