using AngleSharp.Dom;
using System.Linq;
using Bunit;
using Calee.Scheduler.Components;
using Calee.Scheduler.Contracts;
using Calee.Scheduler.Extensions;
using Calee.Scheduler.Internal;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.Extensions.DependencyInjection;

namespace Calee.Scheduler.Tests;

/// <summary>
/// Tests for <see cref="CaleeSchedulerAgendaView{TEvent}"/> covering FR-39, FR-23, FR-30
/// (Agenda portion), NFR-06 (Agenda portion), and the locked design decisions in
/// phase-2-plan §5.3 Q16 (rolling N-day window, empty days hidden, multi-day-once-with-
/// range-label, AgendaDays clamp at [1, 90], default 7, toolbar step = AgendaDays).
/// </summary>
public class CaleeSchedulerAgendaViewTests
{
    private static readonly TimeZoneInfo TZ = TimeZoneInfo.FindSystemTimeZoneById("America/New_York");
    // Anchor on Monday May 18, 2026 — sits in EDT (offset -04:00) so the literal offset
    // checks below are stable across runs.
    private static readonly DateTimeOffset Anchor = new(2026, 5, 18, 0, 0, 0, TimeSpan.FromHours(-4));

    private static BunitContext NewContext()
    {
        var ctx = new BunitContext();
        ctx.Services.AddCaleeScheduler();
        ctx.JSInterop.Mode = JSRuntimeMode.Loose;
        return ctx;
    }

    /// <summary>Build a midnight DateTimeOffset on the supplied date in -04:00 (EDT) offset.</summary>
    private static DateTimeOffset Edt(int year, int month, int day, int hour = 0, int minute = 0) =>
        new(year, month, day, hour, minute, 0, TimeSpan.FromHours(-4));

    private static CalendarEvent Timed(
        string id, DateTimeOffset start, DateTimeOffset end, string? color = null) =>
        new(id, id, start, end, IsAllDay: false, Color: color);

    private static CalendarEvent AllDay(string id, DateTimeOffset start, DateTimeOffset end) =>
        new(id, id, start, end, IsAllDay: true);

    // ────────────────────────────────────────────────────────────────────────
    // SchedulerView.Agenda enum + ordering
    // ────────────────────────────────────────────────────────────────────────

    [Fact]
    public void SchedulerView_Agenda_Enum_Value_Exists()
    {
        var values = Enum.GetNames(typeof(SchedulerView));
        Assert.Contains(nameof(SchedulerView.Agenda), values);
    }

    [Fact]
    public void SchedulerView_Agenda_Ordered_Between_Year_And_Timeline()
    {
        // Phase 2 Task 17 inserted Agenda between Year and Timeline so the enum's
        // declaration order matches the toolbar's visual ordering — Day, Week, Month,
        // Year, Agenda, Timeline.
        Assert.Equal((int)SchedulerView.Year + 1, (int)SchedulerView.Agenda);
        Assert.Equal((int)SchedulerView.Agenda + 1, (int)SchedulerView.Timeline);
    }

    // ────────────────────────────────────────────────────────────────────────
    // AgendaDays parameter — default + clamp
    // ────────────────────────────────────────────────────────────────────────

    [Fact]
    public void AgendaDays_Defaults_To_7()
    {
        using var ctx = NewContext();
        var cut = ctx.Render<CaleeSchedulerAgendaView<CalendarEvent>>(p => p
            .Add(c => c.TimeZone, TZ)
            .Add(c => c.Date, Anchor));

        Assert.Equal(7, cut.Instance.ResolvedAgendaDays);
    }

    [Fact]
    public void AgendaDays_Clamps_Below_1()
    {
        using var ctx = NewContext();
        var cut = ctx.Render<CaleeSchedulerAgendaView<CalendarEvent>>(p => p
            .Add(c => c.TimeZone, TZ)
            .Add(c => c.Date, Anchor)
            .Add(c => c.AgendaDays, 0));

        Assert.Equal(1, cut.Instance.ResolvedAgendaDays);
    }

    [Fact]
    public void AgendaDays_Clamps_Negative_To_1()
    {
        using var ctx = NewContext();
        var cut = ctx.Render<CaleeSchedulerAgendaView<CalendarEvent>>(p => p
            .Add(c => c.TimeZone, TZ)
            .Add(c => c.Date, Anchor)
            .Add(c => c.AgendaDays, -10));

        Assert.Equal(1, cut.Instance.ResolvedAgendaDays);
    }

    [Fact]
    public void AgendaDays_Clamps_Above_90()
    {
        using var ctx = NewContext();
        var cut = ctx.Render<CaleeSchedulerAgendaView<CalendarEvent>>(p => p
            .Add(c => c.TimeZone, TZ)
            .Add(c => c.Date, Anchor)
            .Add(c => c.AgendaDays, 365));

        Assert.Equal(CaleeSchedulerAgendaView<CalendarEvent>.MaxAgendaDays, cut.Instance.ResolvedAgendaDays);
        Assert.Equal(90, cut.Instance.ResolvedAgendaDays);
    }

    [Fact]
    public void AgendaDays_In_Range_Pass_Through()
    {
        using var ctx = NewContext();
        var cut = ctx.Render<CaleeSchedulerAgendaView<CalendarEvent>>(p => p
            .Add(c => c.TimeZone, TZ)
            .Add(c => c.Date, Anchor)
            .Add(c => c.AgendaDays, 14));

        Assert.Equal(14, cut.Instance.ResolvedAgendaDays);
    }

    // ────────────────────────────────────────────────────────────────────────
    // Window bounds
    // ────────────────────────────────────────────────────────────────────────

    [Fact]
    public void Window_Defaults_To_7_Days_From_Anchor()
    {
        using var ctx = NewContext();
        var cut = ctx.Render<CaleeSchedulerAgendaView<CalendarEvent>>(p => p
            .Add(c => c.TimeZone, TZ)
            .Add(c => c.Date, Anchor));

        Assert.Equal(Edt(2026, 5, 18), cut.Instance.WindowStart);
        Assert.Equal(Edt(2026, 5, 25), cut.Instance.WindowEndExclusive);
    }

    [Fact]
    public void Window_AgendaDays_3_Spans_3_Days()
    {
        using var ctx = NewContext();
        var cut = ctx.Render<CaleeSchedulerAgendaView<CalendarEvent>>(p => p
            .Add(c => c.TimeZone, TZ)
            .Add(c => c.Date, Anchor)
            .Add(c => c.AgendaDays, 3));

        Assert.Equal(Edt(2026, 5, 18), cut.Instance.WindowStart);
        Assert.Equal(Edt(2026, 5, 21), cut.Instance.WindowEndExclusive);
    }

    // ────────────────────────────────────────────────────────────────────────
    // Render — single-day events
    // ────────────────────────────────────────────────────────────────────────

    [Fact]
    public void Timed_Single_Day_Event_Renders_Once_In_Its_Group()
    {
        using var ctx = NewContext();
        var ev = Timed("e1", Edt(2026, 5, 20, 9), Edt(2026, 5, 20, 10));

        var cut = ctx.Render<CaleeSchedulerAgendaView<CalendarEvent>>(p => p
            .Add(c => c.TimeZone, TZ)
            .Add(c => c.Date, Anchor)
            .Add(c => c.Events, new[] { ev }));

        var rows = cut.FindAll("[data-calee-region='agenda-row']");
        Assert.Single(rows);
        Assert.Equal("e1", rows[0].GetAttribute("data-calee-event-id"));
    }

    [Fact]
    public void AllDay_Single_Day_Event_Renders_Once_With_All_Day_Class()
    {
        using var ctx = NewContext();
        // All-day Wed May 20 → end of Wed (exclusive Thu May 21).
        var ev = AllDay("ad1", Edt(2026, 5, 20), Edt(2026, 5, 21));

        var cut = ctx.Render<CaleeSchedulerAgendaView<CalendarEvent>>(p => p
            .Add(c => c.TimeZone, TZ)
            .Add(c => c.Date, Anchor)
            .Add(c => c.Events, new[] { ev }));

        var rows = cut.FindAll("[data-calee-region='agenda-row']");
        Assert.Single(rows);
        Assert.Contains("calee-scheduler-agenda-row--all-day", rows[0].GetAttribute("class") ?? string.Empty);
        // Time label reads "All day" for a single-day all-day event.
        var time = rows[0].QuerySelector(".calee-scheduler-agenda-row-time");
        Assert.NotNull(time);
        Assert.Equal("All day", time!.TextContent.Trim());
    }

    // ────────────────────────────────────────────────────────────────────────
    // Render — multi-day events
    // ────────────────────────────────────────────────────────────────────────

    [Fact]
    public void Timed_MultiDay_Event_Renders_Once_At_Start_Date_With_Range_Label()
    {
        using var ctx = NewContext();
        // Mon May 18 9 AM → Wed May 20 5 PM. The event spans three calendar dates.
        var ev = Timed("multi", Edt(2026, 5, 18, 9), Edt(2026, 5, 20, 17));

        var cut = ctx.Render<CaleeSchedulerAgendaView<CalendarEvent>>(p => p
            .Add(c => c.TimeZone, TZ)
            .Add(c => c.Date, Anchor)
            .Add(c => c.Events, new[] { ev }));

        // One row, not three.
        var rows = cut.FindAll("[data-calee-region='agenda-row']");
        Assert.Single(rows);

        // Row sits under the May 18 group.
        var groups = cut.FindAll("[data-calee-region='agenda-group']");
        Assert.Equal("2026-05-18", groups[0].GetAttribute("data-calee-date"));

        // The row's time label includes a range arrow.
        var time = rows[0].QuerySelector(".calee-scheduler-agenda-row-time");
        Assert.NotNull(time);
        Assert.Contains("→", time!.TextContent);
    }

    [Fact]
    public void AllDay_MultiDay_Event_Renders_Once_With_Date_Range_Label()
    {
        using var ctx = NewContext();
        // All-day Mon May 18 → end of Wed May 20 (exclusive Thu May 21). Spans 3 dates.
        var ev = AllDay("conf", Edt(2026, 5, 18), Edt(2026, 5, 21));

        var cut = ctx.Render<CaleeSchedulerAgendaView<CalendarEvent>>(p => p
            .Add(c => c.TimeZone, TZ)
            .Add(c => c.Date, Anchor)
            .Add(c => c.Events, new[] { ev }));

        var rows = cut.FindAll("[data-calee-region='agenda-row']");
        Assert.Single(rows);

        var time = rows[0].QuerySelector(".calee-scheduler-agenda-row-time");
        Assert.NotNull(time);
        // The range label should mention both end dates ("May 18 → May 20" or similar)
        // and NOT read as "All day" — the multi-day branch overrides.
        Assert.Contains("→", time!.TextContent);
        Assert.DoesNotContain("All day", time.TextContent);
    }

    // ────────────────────────────────────────────────────────────────────────
    // Empty days hidden
    // ────────────────────────────────────────────────────────────────────────

    [Fact]
    public void Empty_Days_Are_Not_Rendered()
    {
        using var ctx = NewContext();
        // Events only on May 18 and May 22. The four intervening days (May 19/20/21)
        // have no events and must NOT produce date groups.
        var events = new[]
        {
            Timed("e1", Edt(2026, 5, 18, 9), Edt(2026, 5, 18, 10)),
            Timed("e2", Edt(2026, 5, 22, 14), Edt(2026, 5, 22, 15)),
        };

        var cut = ctx.Render<CaleeSchedulerAgendaView<CalendarEvent>>(p => p
            .Add(c => c.TimeZone, TZ)
            .Add(c => c.Date, Anchor)
            .Add(c => c.Events, events));

        var groups = cut.FindAll("[data-calee-region='agenda-group']");
        Assert.Equal(2, groups.Count);
        Assert.Equal("2026-05-18", groups[0].GetAttribute("data-calee-date"));
        Assert.Equal("2026-05-22", groups[1].GetAttribute("data-calee-date"));
    }

    [Fact]
    public void Default_Renders_Empty_State_When_No_Events()
    {
        using var ctx = NewContext();
        var cut = ctx.Render<CaleeSchedulerAgendaView<CalendarEvent>>(p => p
            .Add(c => c.TimeZone, TZ)
            .Add(c => c.Date, Anchor)
            .Add(c => c.Events, Array.Empty<CalendarEvent>()));

        // No date groups; the whole-view empty-state placeholder appears.
        Assert.Empty(cut.FindAll("[data-calee-region='agenda-group']"));
        Assert.NotNull(cut.Find("[data-calee-region='agenda-empty']"));
    }

    // ────────────────────────────────────────────────────────────────────────
    // EventFilter
    // ────────────────────────────────────────────────────────────────────────

    [Fact]
    public void EventFilter_Drops_Events_Before_Grouping()
    {
        using var ctx = NewContext();
        var keep = Timed("keep", Edt(2026, 5, 20, 9), Edt(2026, 5, 20, 10));
        var hide = Timed("hide", Edt(2026, 5, 20, 11), Edt(2026, 5, 20, 12));

        var cut = ctx.Render<CaleeSchedulerAgendaView<CalendarEvent>>(p => p
            .Add(c => c.TimeZone, TZ)
            .Add(c => c.Date, Anchor)
            .Add(c => c.Events, new[] { keep, hide })
            .Add(c => c.EventFilter, e => e.Id != "hide"));

        var rows = cut.FindAll("[data-calee-region='agenda-row']");
        Assert.Single(rows);
        Assert.Equal("keep", rows[0].GetAttribute("data-calee-event-id"));
    }

    // ────────────────────────────────────────────────────────────────────────
    // Out-of-window + leading-edge pin
    // ────────────────────────────────────────────────────────────────────────

    [Fact]
    public void Events_Entirely_Before_Window_Are_Not_Rendered()
    {
        using var ctx = NewContext();
        // Sat May 16 + Sun May 17 — both before the May 18 anchor.
        var ev = Timed("before", Edt(2026, 5, 16, 9), Edt(2026, 5, 17, 10));

        var cut = ctx.Render<CaleeSchedulerAgendaView<CalendarEvent>>(p => p
            .Add(c => c.TimeZone, TZ)
            .Add(c => c.Date, Anchor)
            .Add(c => c.Events, new[] { ev }));

        Assert.Empty(cut.FindAll("[data-calee-region='agenda-row']"));
    }

    [Fact]
    public void Events_Entirely_After_Window_Are_Not_Rendered()
    {
        using var ctx = NewContext();
        // May 26 is just past the May 18 + 7 day window (which ends May 25 exclusive).
        var ev = Timed("after", Edt(2026, 5, 26, 9), Edt(2026, 5, 26, 10));

        var cut = ctx.Render<CaleeSchedulerAgendaView<CalendarEvent>>(p => p
            .Add(c => c.TimeZone, TZ)
            .Add(c => c.Date, Anchor)
            .Add(c => c.Events, new[] { ev }));

        Assert.Empty(cut.FindAll("[data-calee-region='agenda-row']"));
    }

    [Fact]
    public void Event_Starting_Before_Window_But_Extending_In_Pins_To_First_Day()
    {
        using var ctx = NewContext();
        // Started Sat May 16 (before window) → ends Wed May 20 (inside window). The
        // event's anchor row pins to the window's first day (May 18) so the user has a
        // visible row for it; without the pin a multi-day event crossing the leading
        // edge would be invisible (Agenda doesn't replicate events across days).
        var ev = AllDay("rollover", Edt(2026, 5, 16), Edt(2026, 5, 21));

        var cut = ctx.Render<CaleeSchedulerAgendaView<CalendarEvent>>(p => p
            .Add(c => c.TimeZone, TZ)
            .Add(c => c.Date, Anchor)
            .Add(c => c.Events, new[] { ev }));

        var rows = cut.FindAll("[data-calee-region='agenda-row']");
        Assert.Single(rows);

        // Row sits under the May 18 group (the window's first day), not under May 16.
        var groups = cut.FindAll("[data-calee-region='agenda-group']");
        Assert.Equal("2026-05-18", groups[0].GetAttribute("data-calee-date"));

        // The row carries the "pinned-earlier" class so the markup can surface a cue.
        Assert.Contains("calee-scheduler-agenda-row--pinned-earlier", rows[0].GetAttribute("class") ?? string.Empty);

        // The time label still shows the ORIGINAL range (May 16 → May 20), not a
        // clamped one.
        var time = rows[0].QuerySelector(".calee-scheduler-agenda-row-time")!;
        Assert.Contains("May 16", time.TextContent);
        Assert.Contains("May 20", time.TextContent);
    }

    // ────────────────────────────────────────────────────────────────────────
    // OnEventClicked — mouse + Enter + Space
    // ────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Row_Click_Fires_OnEventClicked_With_TEvent()
    {
        using var ctx = NewContext();
        CalendarEvent? captured = null;
        var ev = Timed("e1", Edt(2026, 5, 20, 9), Edt(2026, 5, 20, 10));

        var cut = ctx.Render<CaleeSchedulerAgendaView<CalendarEvent>>(p => p
            .Add(c => c.TimeZone, TZ)
            .Add(c => c.Date, Anchor)
            .Add(c => c.Events, new[] { ev })
            .Add(c => c.OnEventClicked,
                EventCallback.Factory.Create<CalendarEvent>(this, e => captured = e)));

        var row = cut.Find("[data-calee-region='agenda-row']");
        await cut.InvokeAsync(() => row.Click());

        Assert.NotNull(captured);
        Assert.Equal("e1", captured!.Id);
    }

    [Fact]
    public async Task Enter_On_Focused_Row_Fires_OnEventClicked()
    {
        using var ctx = NewContext();
        CalendarEvent? captured = null;
        var ev = Timed("e1", Edt(2026, 5, 20, 9), Edt(2026, 5, 20, 10));

        var cut = ctx.Render<CaleeSchedulerAgendaView<CalendarEvent>>(p => p
            .Add(c => c.TimeZone, TZ)
            .Add(c => c.Date, Anchor)
            .Add(c => c.Events, new[] { ev })
            .Add(c => c.OnEventClicked,
                EventCallback.Factory.Create<CalendarEvent>(this, e => captured = e)));

        var rows = cut.Instance.Groups[0].Rows;
        await cut.InvokeAsync(() =>
            cut.Instance.InvokeRowKeyDownForTestAsync(
                new KeyboardEventArgs { Key = "Enter" }, rows[0], 0));

        Assert.NotNull(captured);
        Assert.Equal("e1", captured!.Id);
    }

    [Fact]
    public async Task Space_On_Focused_Row_Fires_OnEventClicked_When_MultiSelect_Off()
    {
        // FR-29 fail-closed: with AllowMultiSelect=false, Space acts as a row activator
        // (matching the browser's default button activation) — single-id selection +
        // OnEventClicked fire.
        using var ctx = NewContext();
        CalendarEvent? captured = null;
        var ev = Timed("e1", Edt(2026, 5, 20, 9), Edt(2026, 5, 20, 10));

        var cut = ctx.Render<CaleeSchedulerAgendaView<CalendarEvent>>(p => p
            .Add(c => c.TimeZone, TZ)
            .Add(c => c.Date, Anchor)
            .Add(c => c.Events, new[] { ev })
            .Add(c => c.OnEventClicked,
                EventCallback.Factory.Create<CalendarEvent>(this, e => captured = e)));

        var rows = cut.Instance.Groups[0].Rows;
        await cut.InvokeAsync(() =>
            cut.Instance.InvokeRowKeyDownForTestAsync(
                new KeyboardEventArgs { Key = " " }, rows[0], 0));

        Assert.NotNull(captured);
        Assert.Equal("e1", captured!.Id);
    }

    // ────────────────────────────────────────────────────────────────────────
    // Date-header click drill-down
    // ────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Date_Header_Click_Fires_OnDateClicked_With_First_Of_Group()
    {
        using var ctx = NewContext();
        DateOnly? captured = null;
        var ev = Timed("e1", Edt(2026, 5, 20, 9), Edt(2026, 5, 20, 10));

        var cut = ctx.Render<CaleeSchedulerAgendaView<CalendarEvent>>(p => p
            .Add(c => c.TimeZone, TZ)
            .Add(c => c.Date, Anchor)
            .Add(c => c.Events, new[] { ev })
            .Add(c => c.OnDateClicked,
                EventCallback.Factory.Create<DateOnly>(this, d => captured = d)));

        var header = cut.Find("[data-calee-region='agenda-header']");
        await cut.InvokeAsync(() => header.Click());

        Assert.NotNull(captured);
        Assert.Equal(new DateOnly(2026, 5, 20), captured!.Value);
    }

    // ────────────────────────────────────────────────────────────────────────
    // ARIA shape (ADR-0009 / NFR-06)
    // ────────────────────────────────────────────────────────────────────────

    [Fact]
    public void Container_Is_Role_List_Not_Role_Grid()
    {
        // Agenda is the ARIA list pattern, NOT the grid pattern used by the other
        // views (Day/Week/Month/Year all carry role="grid"). The container's role
        // surfaces this.
        using var ctx = NewContext();
        var ev = Timed("e1", Edt(2026, 5, 20, 9), Edt(2026, 5, 20, 10));

        var cut = ctx.Render<CaleeSchedulerAgendaView<CalendarEvent>>(p => p
            .Add(c => c.TimeZone, TZ)
            .Add(c => c.Date, Anchor)
            .Add(c => c.Events, new[] { ev }));

        Assert.NotNull(cut.Find("[role='list']"));
        Assert.Empty(cut.FindAll("[role='grid']"));
    }

    [Fact]
    public void Each_Row_Is_Role_ListItem()
    {
        using var ctx = NewContext();
        var events = new[]
        {
            Timed("e1", Edt(2026, 5, 18, 9), Edt(2026, 5, 18, 10)),
            Timed("e2", Edt(2026, 5, 19, 9), Edt(2026, 5, 19, 10)),
            Timed("e3", Edt(2026, 5, 20, 9), Edt(2026, 5, 20, 10)),
        };

        var cut = ctx.Render<CaleeSchedulerAgendaView<CalendarEvent>>(p => p
            .Add(c => c.TimeZone, TZ)
            .Add(c => c.Date, Anchor)
            .Add(c => c.Events, events));

        Assert.Equal(3, cut.FindAll("[role='listitem']").Count);
    }

    [Fact]
    public void Each_Date_Header_Is_Role_Heading_With_Level_2()
    {
        using var ctx = NewContext();
        var events = new[]
        {
            Timed("e1", Edt(2026, 5, 18, 9), Edt(2026, 5, 18, 10)),
            Timed("e2", Edt(2026, 5, 19, 9), Edt(2026, 5, 19, 10)),
        };

        var cut = ctx.Render<CaleeSchedulerAgendaView<CalendarEvent>>(p => p
            .Add(c => c.TimeZone, TZ)
            .Add(c => c.Date, Anchor)
            .Add(c => c.Events, events));

        var headers = cut.FindAll("[role='heading']");
        Assert.Equal(2, headers.Count);
        foreach (var h in headers)
        {
            Assert.Equal("2", h.GetAttribute("aria-level"));
        }
    }

    [Fact]
    public void Roving_Tabindex_Exactly_One_Row_Tabbable()
    {
        using var ctx = NewContext();
        var events = new[]
        {
            Timed("e1", Edt(2026, 5, 18, 9), Edt(2026, 5, 18, 10)),
            Timed("e2", Edt(2026, 5, 19, 9), Edt(2026, 5, 19, 10)),
            Timed("e3", Edt(2026, 5, 20, 9), Edt(2026, 5, 20, 10)),
        };

        var cut = ctx.Render<CaleeSchedulerAgendaView<CalendarEvent>>(p => p
            .Add(c => c.TimeZone, TZ)
            .Add(c => c.Date, Anchor)
            .Add(c => c.Events, events));

        var rows = cut.FindAll("[data-calee-region='agenda-row']");
        var tabbable = rows.Count(r => r.GetAttribute("tabindex") == "0");
        Assert.Equal(1, tabbable);
    }

    [Fact]
    public async Task ArrowDown_Moves_Focus_To_Next_Row_Skipping_Headers()
    {
        using var ctx = NewContext();
        // Two events on different days — Arrow Down from row 0 should land on row 1
        // (the row in the next group), skipping the intervening date header.
        var events = new[]
        {
            Timed("e1", Edt(2026, 5, 18, 9), Edt(2026, 5, 18, 10)),
            Timed("e2", Edt(2026, 5, 19, 9), Edt(2026, 5, 19, 10)),
        };

        var cut = ctx.Render<CaleeSchedulerAgendaView<CalendarEvent>>(p => p
            .Add(c => c.TimeZone, TZ)
            .Add(c => c.Date, Anchor)
            .Add(c => c.Events, events));

        var rows = cut.Instance.Groups[0].Rows;
        Assert.Equal(0, cut.Instance.FocusedRowIndexForTest);

        await cut.InvokeAsync(() =>
            cut.Instance.InvokeRowKeyDownForTestAsync(
                new KeyboardEventArgs { Key = "ArrowDown" }, rows[0], 0));

        Assert.Equal(1, cut.Instance.FocusedRowIndexForTest);
    }

    [Fact]
    public async Task ArrowUp_Moves_Focus_To_Previous_Row()
    {
        using var ctx = NewContext();
        var events = new[]
        {
            Timed("e1", Edt(2026, 5, 18, 9), Edt(2026, 5, 18, 10)),
            Timed("e2", Edt(2026, 5, 19, 9), Edt(2026, 5, 19, 10)),
        };

        var cut = ctx.Render<CaleeSchedulerAgendaView<CalendarEvent>>(p => p
            .Add(c => c.TimeZone, TZ)
            .Add(c => c.Date, Anchor)
            .Add(c => c.Events, events));

        // Drive focus to index 1 first, then ArrowUp.
        var allRows = new List<CaleeSchedulerAgendaView<CalendarEvent>.EventRow>();
        for (var g = 0; g < cut.Instance.Groups.Count; g++)
        {
            allRows.AddRange(cut.Instance.Groups[g].Rows);
        }

        await cut.InvokeAsync(() =>
            cut.Instance.InvokeRowKeyDownForTestAsync(
                new KeyboardEventArgs { Key = "ArrowDown" }, allRows[0], 0));
        Assert.Equal(1, cut.Instance.FocusedRowIndexForTest);

        await cut.InvokeAsync(() =>
            cut.Instance.InvokeRowKeyDownForTestAsync(
                new KeyboardEventArgs { Key = "ArrowUp" }, allRows[1], 1));

        Assert.Equal(0, cut.Instance.FocusedRowIndexForTest);
    }

    [Fact]
    public async Task Home_Jumps_To_First_Row_End_Jumps_To_Last()
    {
        using var ctx = NewContext();
        var events = new[]
        {
            Timed("e1", Edt(2026, 5, 18, 9), Edt(2026, 5, 18, 10)),
            Timed("e2", Edt(2026, 5, 19, 9), Edt(2026, 5, 19, 10)),
            Timed("e3", Edt(2026, 5, 20, 9), Edt(2026, 5, 20, 10)),
        };

        var cut = ctx.Render<CaleeSchedulerAgendaView<CalendarEvent>>(p => p
            .Add(c => c.TimeZone, TZ)
            .Add(c => c.Date, Anchor)
            .Add(c => c.Events, events));

        var firstRow = cut.Instance.Groups[0].Rows[0];

        await cut.InvokeAsync(() =>
            cut.Instance.InvokeRowKeyDownForTestAsync(
                new KeyboardEventArgs { Key = "End" }, firstRow, 0));
        Assert.Equal(2, cut.Instance.FocusedRowIndexForTest);

        await cut.InvokeAsync(() =>
            cut.Instance.InvokeRowKeyDownForTestAsync(
                new KeyboardEventArgs { Key = "Home" }, firstRow, 2));
        Assert.Equal(0, cut.Instance.FocusedRowIndexForTest);
    }

    [Fact]
    public async Task PageDown_Jumps_To_Next_Date_Group()
    {
        using var ctx = NewContext();
        // Two events on May 18 + one on May 19. PageDown from row 0 (under May 18)
        // should land on row 2 (the first row of the May 19 group).
        var events = new[]
        {
            Timed("e1", Edt(2026, 5, 18, 9), Edt(2026, 5, 18, 10)),
            Timed("e2", Edt(2026, 5, 18, 14), Edt(2026, 5, 18, 15)),
            Timed("e3", Edt(2026, 5, 19, 9), Edt(2026, 5, 19, 10)),
        };

        var cut = ctx.Render<CaleeSchedulerAgendaView<CalendarEvent>>(p => p
            .Add(c => c.TimeZone, TZ)
            .Add(c => c.Date, Anchor)
            .Add(c => c.Events, events));

        var firstRow = cut.Instance.Groups[0].Rows[0];
        await cut.InvokeAsync(() =>
            cut.Instance.InvokeRowKeyDownForTestAsync(
                new KeyboardEventArgs { Key = "PageDown" }, firstRow, 0));

        // Group 1's first row is flat index 2 (two rows in group 0 came first).
        Assert.Equal(2, cut.Instance.FocusedRowIndexForTest);
    }

    [Fact]
    public void Today_Group_Header_Has_Aria_Current_Date()
    {
        using var ctx = NewContext();
        var todayInTz = TimeZoneInfo.ConvertTime(DateTimeOffset.UtcNow, TZ);
        var ev = Timed("e1",
            new DateTimeOffset(todayInTz.Date, TZ.GetUtcOffset(todayInTz.Date)).AddHours(9),
            new DateTimeOffset(todayInTz.Date, TZ.GetUtcOffset(todayInTz.Date)).AddHours(10));

        var cut = ctx.Render<CaleeSchedulerAgendaView<CalendarEvent>>(p => p
            .Add(c => c.TimeZone, TZ)
            .Add(c => c.Date, todayInTz)
            .Add(c => c.Events, new[] { ev }));

        var todayHeader = cut.FindAll("[data-calee-region='agenda-header'][aria-current='date']");
        Assert.Single(todayHeader);
    }

    // ────────────────────────────────────────────────────────────────────────
    // Toolbar prev/next stepping (AdvanceAnchor Agenda case)
    // ────────────────────────────────────────────────────────────────────────

    [Fact]
    public void AdvanceAnchor_Agenda_Steps_By_AgendaDays()
    {
        var anchor = Edt(2026, 5, 18);

        var next = SchedulerViewPrimitives.AdvanceAnchor(
            SchedulerView.Agenda, anchor, +1, TZ, agendaDays: 7);
        Assert.Equal(Edt(2026, 5, 25), next);

        var prev = SchedulerViewPrimitives.AdvanceAnchor(
            SchedulerView.Agenda, anchor, -1, TZ, agendaDays: 7);
        Assert.Equal(Edt(2026, 5, 11), prev);
    }

    [Fact]
    public void AdvanceAnchor_Agenda_Steps_Match_AgendaDays_Value()
    {
        // Verify the step explicitly tracks the agendaDays argument: AgendaDays=14
        // moves by 14 days, AgendaDays=3 moves by 3 days.
        var anchor = Edt(2026, 5, 18);

        var next14 = SchedulerViewPrimitives.AdvanceAnchor(
            SchedulerView.Agenda, anchor, +1, TZ, agendaDays: 14);
        Assert.Equal(Edt(2026, 6, 1), next14);

        var next3 = SchedulerViewPrimitives.AdvanceAnchor(
            SchedulerView.Agenda, anchor, +1, TZ, agendaDays: 3);
        Assert.Equal(Edt(2026, 5, 21), next3);
    }

    [Fact]
    public void FormatRangeLabel_Agenda_Default_7_Days_Formats_As_Same_Month_Range()
    {
        var anchor = Edt(2026, 5, 18);
        var label = SchedulerViewPrimitives.FormatRangeLabel(
            SchedulerView.Agenda, anchor, TZ, DayOfWeek.Sunday, agendaDays: 7);

        // 7 days from May 18 = May 18..24 inclusive.
        Assert.Equal("May 18 – 24, 2026", label);
    }

    [Fact]
    public void FormatRangeLabel_Agenda_1_Day_Formats_As_Day_Header()
    {
        // AgendaDays=1 degenerates to the single-day case — same shape as Day view.
        var anchor = Edt(2026, 5, 18);
        var label = SchedulerViewPrimitives.FormatRangeLabel(
            SchedulerView.Agenda, anchor, TZ, DayOfWeek.Sunday, agendaDays: 1);

        Assert.Equal("Mon, May 18, 2026", label);
    }

    [Fact]
    public void FormatRangeLabel_Agenda_Cross_Month()
    {
        // 7 days from Mar 29 = Mar 29..Apr 4.
        var anchor = Edt(2026, 3, 29);
        var label = SchedulerViewPrimitives.FormatRangeLabel(
            SchedulerView.Agenda, anchor, TZ, DayOfWeek.Sunday, agendaDays: 7);

        Assert.Equal("Mar 29 – Apr 4, 2026", label);
    }

    // ────────────────────────────────────────────────────────────────────────
    // Palette + keystroke binding (Tasks 14 / 15 integration)
    // ────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Keystroke_5_Fires_View_Switch_To_Agenda()
    {
        // ADR-0013 §5.2 Q10: "5" switches to Agenda. After Task 17 the binding flips
        // from matched-but-no-op to a live view-switch.
        using var ctx = NewContext();
        SchedulerView? captured = null;
        var cut = ctx.Render<CaleeSchedulerDayView<CalendarEvent>>(p => p
            .Add(c => c.TimeZone, TZ)
            .Add(c => c.Date, Anchor)
            .Add(c => c.Events, Array.Empty<CalendarEvent>())
            .Add(c => c.OnViewSwitchRequested,
                EventCallback.Factory.Create<SchedulerView>(this, v => captured = v)));

        await cut.InvokeAsync(() =>
            cut.Find("[role='grid']").KeyDown(new KeyboardEventArgs { Key = "5" }));

        Assert.Equal(SchedulerView.Agenda, captured);
    }

    // ────────────────────────────────────────────────────────────────────────
    // Sort order — all-day first, then timed by start
    // ────────────────────────────────────────────────────────────────────────

    [Fact]
    public void Rows_Sort_AllDay_First_Then_Timed_By_Start()
    {
        using var ctx = NewContext();
        var timed10 = Timed("t10", Edt(2026, 5, 18, 10), Edt(2026, 5, 18, 11));
        var timed9 = Timed("t9", Edt(2026, 5, 18, 9), Edt(2026, 5, 18, 10));
        var allDay = AllDay("ad", Edt(2026, 5, 18), Edt(2026, 5, 19));

        var cut = ctx.Render<CaleeSchedulerAgendaView<CalendarEvent>>(p => p
            .Add(c => c.TimeZone, TZ)
            .Add(c => c.Date, Anchor)
            // Supplied in shuffled order — sort should still put all-day first, then
            // 9 AM, then 10 AM.
            .Add(c => c.Events, new[] { timed10, allDay, timed9 }));

        var rows = cut.FindAll("[data-calee-region='agenda-row']");
        Assert.Equal(3, rows.Count);
        Assert.Equal("ad", rows[0].GetAttribute("data-calee-event-id"));
        Assert.Equal("t9", rows[1].GetAttribute("data-calee-event-id"));
        Assert.Equal("t10", rows[2].GetAttribute("data-calee-event-id"));
    }

    // ────────────────────────────────────────────────────────────────────────
    // OnRangeChanged firing (FR-23)
    // ────────────────────────────────────────────────────────────────────────

    [Fact]
    public void OnRangeChanged_Fires_On_First_Render()
    {
        using var ctx = NewContext();
        SchedulerRange? captured = null;
        var cut = ctx.Render<CaleeSchedulerAgendaView<CalendarEvent>>(p => p
            .Add(c => c.TimeZone, TZ)
            .Add(c => c.Date, Anchor)
            .Add(c => c.OnRangeChanged,
                EventCallback.Factory.Create<SchedulerRange>(this, r => captured = r)));

        Assert.NotNull(captured);
        Assert.Equal(Edt(2026, 5, 18), captured!.Start);
        Assert.Equal(Edt(2026, 5, 25), captured.End);
    }

    // ────────────────────────────────────────────────────────────────────────
    // Issue #30 — drag-to-move: Task 1 root forwarding + markup gating
    // ────────────────────────────────────────────────────────────────────────

    [Fact]
    public void AllowDragToMove_False_RendersByteIdenticalMarkup()
    {
        // AllowDragToMove=false (whether omitted or set explicitly) must render
        // byte-identical markup to the pre-issue-30 shape — no data-calee-drag-handle,
        // no @onpointerdown, no phantom class, aria-roledescription untouched.
        using var ctxA = NewContext();
        using var ctxB = NewContext();
        var events = new[]
        {
            Timed("e1", Edt(2026, 5, 18, 9), Edt(2026, 5, 18, 10)),
            Timed("e2", Edt(2026, 5, 19, 9), Edt(2026, 5, 19, 10)),
        };

        var cutA = ctxA.Render<CaleeSchedulerAgendaView<CalendarEvent>>(p => p
            .Add(c => c.TimeZone, TZ)
            .Add(c => c.Date, Anchor)
            .Add(c => c.Events, events));

        var cutB = ctxB.Render<CaleeSchedulerAgendaView<CalendarEvent>>(p => p
            .Add(c => c.TimeZone, TZ)
            .Add(c => c.Date, Anchor)
            .Add(c => c.Events, events)
            .Add(c => c.AllowDragToMove, false));

        // bUnit's static renderer serializes each @ref capture as a
        // `blazor:elementReference="<random guid>"` marker in its Markup snapshot —
        // an artifact of bUnit's test-rendering pipeline, not something real Blazor
        // ever puts in the browser DOM (@ref is pure renderer bookkeeping there).
        // Normalize those two per-instance-random GUIDs out before the string
        // equality check so the assertion targets the drag-related markup drift it's
        // meant to catch, not incidental ref-capture-id randomness.
        var normalizedA = System.Text.RegularExpressions.Regex.Replace(
            cutA.Markup, "blazor:elementReference=\"[^\"]*\"", "blazor:elementReference=\"X\"");
        var normalizedB = System.Text.RegularExpressions.Regex.Replace(
            cutB.Markup, "blazor:elementReference=\"[^\"]*\"", "blazor:elementReference=\"X\"");
        Assert.Equal(normalizedA, normalizedB);

        var rows = cutA.FindAll("[data-calee-region='agenda-row']");
        Assert.NotEmpty(rows);
        foreach (var row in rows)
        {
            Assert.Null(row.GetAttribute("data-calee-drag-handle"));
            Assert.Null(row.GetAttribute("aria-roledescription"));
        }
    }

    [Fact]
    public void AllowDragToMove_True_AddsDragHandleAndRoledescription()
    {
        using var ctx = NewContext();
        var events = new[]
        {
            Timed("e1", Edt(2026, 5, 18, 9), Edt(2026, 5, 18, 10)),
            Timed("e2", Edt(2026, 5, 19, 9), Edt(2026, 5, 19, 10)),
        };

        var cut = ctx.Render<CaleeSchedulerAgendaView<CalendarEvent>>(p => p
            .Add(c => c.TimeZone, TZ)
            .Add(c => c.Date, Anchor)
            .Add(c => c.Events, events)
            .Add(c => c.AllowDragToMove, true));

        var rows = cut.FindAll("[data-calee-region='agenda-row']");
        Assert.NotEmpty(rows);
        foreach (var row in rows)
        {
            Assert.Equal("move", row.GetAttribute("data-calee-drag-handle"));
            Assert.Equal("draggable event", row.GetAttribute("aria-roledescription"));
        }
    }

    [Fact]
    public void Root_ForwardsAllowDragToMoveAndOnEventMoved_ToAgendaArm()
    {
        using var ctx = NewContext();
        var ev = Timed("e1", Edt(2026, 5, 18, 9), Edt(2026, 5, 18, 10));

        var cut = ctx.Render<CaleeScheduler<CalendarEvent>>(p => p
            .Add(c => c.TimeZone, TZ)
            .Add(c => c.Date, Anchor)
            .Add(c => c.View, SchedulerView.Agenda)
            .Add(c => c.Events, new[] { ev })
            .Add(c => c.AllowDragToMove, true));

        var rows = cut.FindAll("[data-calee-region='agenda-row']");
        Assert.NotEmpty(rows);
        Assert.All(rows, r => Assert.Equal("move", r.GetAttribute("data-calee-drag-handle")));
    }
}
