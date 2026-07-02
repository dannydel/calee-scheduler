using Bunit;
using Calee.Scheduler.Components;
using Calee.Scheduler.Contracts;
using Calee.Scheduler.Extensions;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.Extensions.DependencyInjection;

namespace Calee.Scheduler.Tests;

/// <summary>
/// Tests for <c>DayHeaderTemplate</c> + <c>OnDayHeaderClicked</c> (issue #9) across Day,
/// Week (including a <c>VisibleDays</c>/WorkWeek subset), and the root scheduler's
/// forwarding. Covers: template context (the day's midnight <see cref="DateTimeOffset"/>
/// in the grid time zone — the same shape <c>DayModifier</c> receives), the null-template
/// default, click + Enter/Space activation, the fail-closed "no delegate → inert header"
/// contract, drag-active suppression, composition with blocked days (issue #8), and a
/// DST-week date-context check.
/// </summary>
public class DayHeaderTests
{
    // Fixed time zone and anchor for determinism. Anchor: Tuesday, 2026-05-19 EDT.
    private static readonly TimeZoneInfo TZ = TimeZoneInfo.FindSystemTimeZoneById("America/New_York");
    private static readonly DateTimeOffset Anchor = new(2026, 5, 19, 0, 0, 0, TimeSpan.FromHours(-4));

    private static BunitContext NewContext()
    {
        var ctx = new BunitContext();
        ctx.Services.AddCaleeScheduler();
        ctx.JSInterop.Mode = JSRuntimeMode.Loose;
        return ctx;
    }

    // A DayHeaderTemplate that stamps the received DateTimeOffset's round-trip ("O")
    // string into a marker span so tests can assert the exact context value without
    // relying on any particular display formatting.
    private static readonly RenderFragment<DateTimeOffset> MarkerTemplate = day => builder =>
    {
        builder.OpenElement(0, "span");
        builder.AddAttribute(1, "class", "test-day-header-marker");
        builder.AddContent(2, day.ToString("O"));
        builder.CloseElement();
    };

    private static CalendarEvent Timed(string id, DateTimeOffset day, int startHour, int endHour)
    {
        var s = new DateTimeOffset(day.Year, day.Month, day.Day, startHour, 0, 0, day.Offset);
        var e = new DateTimeOffset(day.Year, day.Month, day.Day, endHour, 0, 0, day.Offset);
        return new CalendarEvent(id, id, s, e, IsAllDay: false);
    }

    // ════════════════════════════════════════════════════════════════════════════
    // Day view — template context + null default
    // ════════════════════════════════════════════════════════════════════════════

    [Fact]
    public void Day_DayHeaderTemplate_Renders_WithMidnightInGridTimeZone_AfterDefaultLabel()
    {
        using var ctx = NewContext();
        var cut = ctx.Render<CaleeSchedulerDayView<CalendarEvent>>(p => p
            .Add(c => c.TimeZone, TZ)
            .Add(c => c.Date, Anchor)
            .Add(c => c.DayHeaderTemplate, MarkerTemplate));

        var header = cut.Find("[data-calee-region='day-header']");
        var expected = new DateTimeOffset(Anchor.Date, TZ.GetUtcOffset(Anchor.Date));

        // Default label still present, and the marker comes after it (ADR-0002 spirit —
        // the template does not replace the library-owned label).
        var weekday = header.QuerySelector(".calee-scheduler-day-header-weekday");
        var marker = header.QuerySelector(".test-day-header-marker");
        Assert.NotNull(weekday);
        Assert.NotNull(marker);
        Assert.Equal(expected.ToString("O"), marker!.TextContent);

        // The label div(s) precede the marker in document order.
        var children = header.Children;
        Assert.True(Array.IndexOf(children.ToArray(), marker) > Array.IndexOf(children.ToArray(), weekday));
    }

    [Fact]
    public void Day_NullDayHeaderTemplate_RendersUnchangedMarkup()
    {
        using var ctx = NewContext();
        var cut = ctx.Render<CaleeSchedulerDayView<CalendarEvent>>(p => p
            .Add(c => c.TimeZone, TZ)
            .Add(c => c.Date, Anchor));

        var header = cut.Find("[data-calee-region='day-header']");
        Assert.Null(header.QuerySelector(".test-day-header-marker"));
        // Exactly the two default label children — no extra nodes injected.
        Assert.Equal(2, header.Children.Length);
        Assert.False(header.HasAttribute("tabindex"));
        Assert.False(header.HasAttribute("role"));
    }

    // ════════════════════════════════════════════════════════════════════════════
    // Day view — click / keyboard activation + fail-closed default
    // ════════════════════════════════════════════════════════════════════════════

    [Fact]
    public async Task Day_OnDayHeaderClicked_FiresWithMidnightInGridTimeZone_OnClick()
    {
        using var ctx = NewContext();
        DateTimeOffset? fired = null;
        var cut = ctx.Render<CaleeSchedulerDayView<CalendarEvent>>(p => p
            .Add(c => c.TimeZone, TZ)
            .Add(c => c.Date, Anchor)
            .Add(c => c.OnDayHeaderClicked,
                EventCallback.Factory.Create<DateTimeOffset>(this, d => fired = d)));

        var header = cut.Find("[data-calee-region='day-header']");
        await cut.InvokeAsync(() => header.Click());

        var expected = new DateTimeOffset(Anchor.Date, TZ.GetUtcOffset(Anchor.Date));
        Assert.Equal(expected, fired);
    }

    [Theory]
    [InlineData("Enter")]
    [InlineData(" ")]
    public async Task Day_OnDayHeaderClicked_FiresOnEnterAndSpace_WhenFocused(string key)
    {
        using var ctx = NewContext();
        DateTimeOffset? fired = null;
        var cut = ctx.Render<CaleeSchedulerDayView<CalendarEvent>>(p => p
            .Add(c => c.TimeZone, TZ)
            .Add(c => c.Date, Anchor)
            .Add(c => c.OnDayHeaderClicked,
                EventCallback.Factory.Create<DateTimeOffset>(this, d => fired = d)));

        var header = cut.Find("[data-calee-region='day-header']");
        await cut.InvokeAsync(() => header.KeyDown(key));

        var expected = new DateTimeOffset(Anchor.Date, TZ.GetUtcOffset(Anchor.Date));
        Assert.Equal(expected, fired);
    }

    [Fact]
    public async Task Day_OnDayHeaderClicked_OtherKeys_DoNotFire()
    {
        using var ctx = NewContext();
        var fireCount = 0;
        var cut = ctx.Render<CaleeSchedulerDayView<CalendarEvent>>(p => p
            .Add(c => c.TimeZone, TZ)
            .Add(c => c.Date, Anchor)
            .Add(c => c.OnDayHeaderClicked,
                EventCallback.Factory.Create<DateTimeOffset>(this, _ => fireCount++)));

        var header = cut.Find("[data-calee-region='day-header']");
        await cut.InvokeAsync(() => header.KeyDown("Tab"));
        await cut.InvokeAsync(() => header.KeyDown("ArrowDown"));

        Assert.Equal(0, fireCount);
    }

    [Fact]
    public void Day_Interactive_When_OnDayHeaderClicked_Wired()
    {
        using var ctx = NewContext();
        var cut = ctx.Render<CaleeSchedulerDayView<CalendarEvent>>(p => p
            .Add(c => c.TimeZone, TZ)
            .Add(c => c.Date, Anchor)
            .Add(c => c.OnDayHeaderClicked,
                EventCallback.Factory.Create<DateTimeOffset>(this, _ => { })));

        var header = cut.Find("[data-calee-region='day-header']");
        Assert.Equal("0", header.GetAttribute("tabindex"));
        Assert.Equal("button", header.GetAttribute("role"));
        Assert.Contains("calee-scheduler-day-header--interactive", header.GetAttribute("class") ?? string.Empty);
        var expectedName = new DateTimeOffset(Anchor.Date, TZ.GetUtcOffset(Anchor.Date))
            .ToString("dddd, MMMM d, yyyy", System.Globalization.CultureInfo.GetCultureInfo("en-US"));
        Assert.Equal(expectedName, header.GetAttribute("aria-label"));
    }

    [Fact]
    public async Task Day_NotInteractive_When_OnDayHeaderClicked_Unwired()
    {
        // Fail-closed default (issue #9): no delegate → no tabindex, no role, no
        // pointer cursor class, no aria-label, and no listener at all — a DOM click
        // throws bUnit's strict "no handler bound" exception, mirroring the existing
        // Day_DoubleClickToCreate_NoOp_OnBlockedDay precedent for a gated-off handler.
        using var ctx = NewContext();
        var cut = ctx.Render<CaleeSchedulerDayView<CalendarEvent>>(p => p
            .Add(c => c.TimeZone, TZ)
            .Add(c => c.Date, Anchor));

        var header = cut.Find("[data-calee-region='day-header']");
        Assert.False(header.HasAttribute("tabindex"));
        Assert.False(header.HasAttribute("role"));
        Assert.DoesNotContain("calee-scheduler-day-header--interactive", header.GetAttribute("class") ?? string.Empty);
        Assert.Null(header.GetAttribute("aria-label"));

        await Assert.ThrowsAsync<Bunit.MissingEventHandlerException>(
            () => cut.InvokeAsync(() => header.Click()));
    }

    [Fact]
    public async Task Day_OnDayHeaderClicked_Suppressed_WhileDragActive()
    {
        // Real drag-active precedence check (ADR-0006), mirroring the technique
        // SchedulerStatefulComponentBaseTests uses to exercise BeginDragOnPointerAsync's
        // mouse-pointer path: mock the JS module's startDrag so a real drag starts,
        // then confirm the header's click handler short-circuits while IsDragActive.
        const string modulePath = "./_content/Calee.Scheduler/calee-scheduler.js";
        using var ctx = NewContext();
        var module = ctx.JSInterop.SetupModule(modulePath);
        module.Setup<string>("startDrag", _ => true).SetResult("handle-1");

        DateTimeOffset? fired = null;
        var ev = Timed("a", Anchor, 9, 10);
        var cut = ctx.Render<CaleeSchedulerDayView<CalendarEvent>>(p => p
            .Add(c => c.TimeZone, TZ)
            .Add(c => c.Date, Anchor)
            .Add(c => c.StartHour, 8)
            .Add(c => c.EndHour, 18)
            .Add(c => c.AllowDragToMove, true)
            .Add(c => c.Events, new[] { ev })
            .Add(c => c.OnDayHeaderClicked,
                EventCallback.Factory.Create<DateTimeOffset>(this, d => fired = d)));

        // Start a real drag on the event chip (mouse pointer starts immediately).
        await cut.InvokeAsync(() => cut.Instance.OnEventPointerDownAsync(
            new PointerEventArgs { Button = 0, PointerType = "mouse", PointerId = 1 }, ev));
        Assert.True(cut.Instance.IsDragActiveForTest);

        var header = cut.Find("[data-calee-region='day-header']");
        await cut.InvokeAsync(() => header.Click());
        await cut.InvokeAsync(() => header.KeyDown("Enter"));

        Assert.Null(fired);
    }

    // ════════════════════════════════════════════════════════════════════════════
    // Blocked-day composition (issue #8)
    // ════════════════════════════════════════════════════════════════════════════

    [Fact]
    public async Task Day_BlockedDay_StillRendersTemplate_AndStillFiresClick()
    {
        using var ctx = NewContext();
        DateTimeOffset? fired = null;
        var cut = ctx.Render<CaleeSchedulerDayView<CalendarEvent>>(p => p
            .Add(c => c.TimeZone, TZ)
            .Add(c => c.Date, Anchor)
            .Add(c => c.DayHeaderTemplate, MarkerTemplate)
            .Add(c => c.DayModifier,
                (Func<DateTimeOffset, SchedulerDayState?>)(day => DateOnly.FromDateTime(day.Date) == DateOnly.FromDateTime(Anchor.Date)
                    ? new SchedulerDayState(IsBlocked: true, Label: "Blocked — holiday")
                    : null))
            .Add(c => c.OnDayHeaderClicked,
                EventCallback.Factory.Create<DateTimeOffset>(this, d => fired = d)));

        var header = cut.Find("[data-calee-region='day-header']");
        // Blocked class/label win, but the template still renders and the click still fires.
        Assert.Contains("calee-scheduler-day-blocked", header.GetAttribute("class") ?? string.Empty);
        Assert.Contains("Blocked — holiday", header.GetAttribute("aria-label") ?? string.Empty);
        Assert.NotNull(header.QuerySelector(".test-day-header-marker"));

        await cut.InvokeAsync(() => header.Click());
        var expected = new DateTimeOffset(Anchor.Date, TZ.GetUtcOffset(Anchor.Date));
        Assert.Equal(expected, fired);
    }

    // ════════════════════════════════════════════════════════════════════════════
    // Week view — template context across all 7 columns + VisibleDays subset
    // ════════════════════════════════════════════════════════════════════════════

    [Fact]
    public void Week_DayHeaderTemplate_RendersPerColumn_WithEachColumnsMidnight()
    {
        using var ctx = NewContext();
        var cut = ctx.Render<CaleeSchedulerWeekView<CalendarEvent>>(p => p
            .Add(c => c.TimeZone, TZ)
            .Add(c => c.Date, Anchor)
            .Add(c => c.DayHeaderTemplate, MarkerTemplate));

        var headers = cut.FindAll("[data-calee-region='day-header']");
        Assert.Equal(7, headers.Count);

        // Anchor is Tue 2026-05-19; default FirstDayOfWeek=Sunday → week is Sun 5/17..Sat 5/23.
        var expectedFirstDay = new DateTimeOffset(2026, 5, 17, 0, 0, 0, TimeSpan.FromHours(-4));
        for (var i = 0; i < headers.Count; i++)
        {
            var marker = headers[i].QuerySelector(".test-day-header-marker");
            Assert.NotNull(marker);
            var expected = expectedFirstDay.AddDays(i);
            Assert.Equal(expected.ToString("O"), marker!.TextContent);
        }
    }

    [Fact]
    public void Week_VisibleDays_Subset_OnlyRendersTemplate_ForVisibleColumns()
    {
        using var ctx = NewContext();
        var cut = ctx.Render<CaleeSchedulerWeekView<CalendarEvent>>(p => p
            .Add(c => c.TimeZone, TZ)
            .Add(c => c.Date, Anchor)
            .Add(c => c.VisibleDays, new[] { DayOfWeek.Monday, DayOfWeek.Tuesday, DayOfWeek.Wednesday, DayOfWeek.Thursday, DayOfWeek.Friday })
            .Add(c => c.DayHeaderTemplate, MarkerTemplate));

        var headers = cut.FindAll("[data-calee-region='day-header']");
        Assert.Equal(5, headers.Count);
        foreach (var header in headers)
        {
            Assert.NotNull(header.QuerySelector(".test-day-header-marker"));
        }
    }

    [Fact]
    public async Task Week_OnDayHeaderClicked_FiresWithCorrectColumnsMidnight()
    {
        using var ctx = NewContext();
        DateTimeOffset? fired = null;
        var cut = ctx.Render<CaleeSchedulerWeekView<CalendarEvent>>(p => p
            .Add(c => c.TimeZone, TZ)
            .Add(c => c.Date, Anchor)
            .Add(c => c.OnDayHeaderClicked,
                EventCallback.Factory.Create<DateTimeOffset>(this, d => fired = d)));

        // Click the 3rd column (index 2 → Tue 5/19, the anchor itself).
        var headers = cut.FindAll("[data-calee-region='day-header']");
        await cut.InvokeAsync(() => headers[2].Click());

        var expected = new DateTimeOffset(Anchor.Date, TZ.GetUtcOffset(Anchor.Date));
        Assert.Equal(expected, fired);
    }

    [Theory]
    [InlineData("Enter")]
    [InlineData(" ")]
    public async Task Week_OnDayHeaderClicked_FiresOnEnterAndSpace(string key)
    {
        using var ctx = NewContext();
        DateTimeOffset? fired = null;
        var cut = ctx.Render<CaleeSchedulerWeekView<CalendarEvent>>(p => p
            .Add(c => c.TimeZone, TZ)
            .Add(c => c.Date, Anchor)
            .Add(c => c.OnDayHeaderClicked,
                EventCallback.Factory.Create<DateTimeOffset>(this, d => fired = d)));

        var headers = cut.FindAll("[data-calee-region='day-header']");
        await cut.InvokeAsync(() => headers[0].KeyDown(key));

        var expectedFirstDay = new DateTimeOffset(2026, 5, 17, 0, 0, 0, TimeSpan.FromHours(-4));
        Assert.Equal(expectedFirstDay, fired);
    }

    [Fact]
    public void Week_NotInteractive_When_OnDayHeaderClicked_Unwired()
    {
        using var ctx = NewContext();
        var cut = ctx.Render<CaleeSchedulerWeekView<CalendarEvent>>(p => p
            .Add(c => c.TimeZone, TZ)
            .Add(c => c.Date, Anchor));

        foreach (var header in cut.FindAll("[data-calee-region='day-header']"))
        {
            Assert.False(header.HasAttribute("tabindex"));
            Assert.False(header.HasAttribute("role"));
            Assert.DoesNotContain("calee-scheduler-day-header--interactive", header.GetAttribute("class") ?? string.Empty);
        }
    }

    [Fact]
    public void Week_NullDayHeaderTemplate_RendersUnchangedMarkup()
    {
        using var ctx = NewContext();
        var cut = ctx.Render<CaleeSchedulerWeekView<CalendarEvent>>(p => p
            .Add(c => c.TimeZone, TZ)
            .Add(c => c.Date, Anchor));

        foreach (var header in cut.FindAll("[data-calee-region='day-header']"))
        {
            Assert.Null(header.QuerySelector(".test-day-header-marker"));
            Assert.Equal(2, header.Children.Length);
        }
    }

    [Fact]
    public async Task Week_BlockedColumn_StillRendersTemplate_AndStillFiresClick()
    {
        using var ctx = NewContext();
        DateTimeOffset? fired = null;
        var blockedDate = DateOnly.FromDateTime(Anchor.Date); // Tue 5/19, column index 2.
        var cut = ctx.Render<CaleeSchedulerWeekView<CalendarEvent>>(p => p
            .Add(c => c.TimeZone, TZ)
            .Add(c => c.Date, Anchor)
            .Add(c => c.DayHeaderTemplate, MarkerTemplate)
            .Add(c => c.DayModifier,
                (Func<DateTimeOffset, SchedulerDayState?>)(day => DateOnly.FromDateTime(day.Date) == blockedDate
                    ? new SchedulerDayState(IsBlocked: true, Label: "Blocked — no route")
                    : null))
            .Add(c => c.OnDayHeaderClicked,
                EventCallback.Factory.Create<DateTimeOffset>(this, d => fired = d)));

        var headers = cut.FindAll("[data-calee-region='day-header']");
        var blockedHeader = headers[2];
        Assert.Contains("calee-scheduler-day-blocked", blockedHeader.GetAttribute("class") ?? string.Empty);
        Assert.Contains("Blocked — no route", blockedHeader.GetAttribute("aria-label") ?? string.Empty);
        Assert.NotNull(blockedHeader.QuerySelector(".test-day-header-marker"));

        await cut.InvokeAsync(() => blockedHeader.Click());
        var expected = new DateTimeOffset(Anchor.Date, TZ.GetUtcOffset(Anchor.Date));
        Assert.Equal(expected, fired);
    }

    // ════════════════════════════════════════════════════════════════════════════
    // Root scheduler forwarding — Day, WorkWeek, Week arms
    // ════════════════════════════════════════════════════════════════════════════

    [Fact]
    public void Root_Forwards_DayHeaderTemplate_And_OnDayHeaderClicked_ToDayArm()
    {
        using var ctx = NewContext();
        var cut = ctx.Render<CaleeScheduler<CalendarEvent>>(p => p
            .Add(c => c.TimeZone, TZ)
            .Add(c => c.Date, Anchor)
            .Add(c => c.View, SchedulerView.Day)
            .Add(c => c.OnDayHeaderClicked,
                EventCallback.Factory.Create<DateTimeOffset>(this, _ => { }))
            .Add(c => c.DayHeaderTemplate, MarkerTemplate));

        var header = cut.Find("[data-calee-region='day-header']");
        Assert.NotNull(header.QuerySelector(".test-day-header-marker"));
        Assert.Equal("button", header.GetAttribute("role"));
    }

    [Fact]
    public void Root_Forwards_DayHeaderTemplate_And_OnDayHeaderClicked_ToWeekArm()
    {
        using var ctx = NewContext();
        var cut = ctx.Render<CaleeScheduler<CalendarEvent>>(p => p
            .Add(c => c.TimeZone, TZ)
            .Add(c => c.Date, Anchor)
            .Add(c => c.View, SchedulerView.Week)
            .Add(c => c.OnDayHeaderClicked,
                EventCallback.Factory.Create<DateTimeOffset>(this, _ => { }))
            .Add(c => c.DayHeaderTemplate, MarkerTemplate));

        var headers = cut.FindAll("[data-calee-region='day-header']");
        Assert.Equal(7, headers.Count);
        foreach (var header in headers)
        {
            Assert.NotNull(header.QuerySelector(".test-day-header-marker"));
            Assert.Equal("button", header.GetAttribute("role"));
        }
    }

    [Fact]
    public async Task Root_Forwards_DayHeaderTemplate_And_OnDayHeaderClicked_ToWorkWeekArm()
    {
        using var ctx = NewContext();
        DateTimeOffset? fired = null;
        var cut = ctx.Render<CaleeScheduler<CalendarEvent>>(p => p
            .Add(c => c.TimeZone, TZ)
            .Add(c => c.Date, Anchor)
            .Add(c => c.View, SchedulerView.WorkWeek)
            .Add(c => c.OnDayHeaderClicked,
                EventCallback.Factory.Create<DateTimeOffset>(this, d => fired = d))
            .Add(c => c.DayHeaderTemplate, MarkerTemplate));

        // WorkWeekDays defaults to Mon-Fri → 5 columns, Mon 5/18..Fri 5/22.
        var headers = cut.FindAll(".calee-scheduler-week [data-calee-region='day-header']");
        Assert.Equal(5, headers.Count);
        foreach (var header in headers)
        {
            Assert.NotNull(header.QuerySelector(".test-day-header-marker"));
        }

        await cut.InvokeAsync(() => headers[0].Click());
        var expectedMonday = new DateTimeOffset(2026, 5, 18, 0, 0, 0, TimeSpan.FromHours(-4));
        Assert.Equal(expectedMonday, fired);
    }

    [Fact]
    public void Root_Month_Ignores_DayHeaderTemplate_OutOfScope()
    {
        // Month is explicitly out of scope for issue #9 (its headers are weekday
        // names, not dates) — the root does not forward DayHeaderTemplate to it,
        // even though the parameter is inherited on the generic surface.
        using var ctx = NewContext();
        var cut = ctx.Render<CaleeScheduler<CalendarEvent>>(p => p
            .Add(c => c.TimeZone, TZ)
            .Add(c => c.Date, Anchor)
            .Add(c => c.View, SchedulerView.Month)
            .Add(c => c.DayHeaderTemplate, MarkerTemplate));

        Assert.Empty(cut.FindAll(".test-day-header-marker"));
    }

    // ════════════════════════════════════════════════════════════════════════════
    // DST-week date context
    // ════════════════════════════════════════════════════════════════════════════

    [Fact]
    public void Week_DayHeaderTemplate_Context_CorrectAcrossDstSpringForwardWeek()
    {
        // America/New_York springs forward at 2:00 AM on Sunday 2026-03-08. With the
        // default FirstDayOfWeek=Sunday, the week containing Wed 2026-03-11 is
        // Sun 3/8 (-05:00, pre-transition midnight) .. Sat 3/14 (-04:00). Each column's
        // template context must carry the correct per-day offset, not a blanket one.
        using var ctx = NewContext();
        var midWeek = new DateTimeOffset(2026, 3, 11, 0, 0, 0, TimeSpan.FromHours(-4));
        var cut = ctx.Render<CaleeSchedulerWeekView<CalendarEvent>>(p => p
            .Add(c => c.TimeZone, TZ)
            .Add(c => c.Date, midWeek)
            .Add(c => c.DayHeaderTemplate, MarkerTemplate));

        var headers = cut.FindAll("[data-calee-region='day-header']");
        Assert.Equal(7, headers.Count);

        var expectedOffsets = new[]
        {
            TimeSpan.FromHours(-5), // Sun 3/8 — midnight is still EST (DST starts 2 AM that day).
            TimeSpan.FromHours(-4), // Mon 3/9 onward — EDT.
            TimeSpan.FromHours(-4),
            TimeSpan.FromHours(-4),
            TimeSpan.FromHours(-4),
            TimeSpan.FromHours(-4),
            TimeSpan.FromHours(-4),
        };
        var expectedFirstDay = new DateTimeOffset(2026, 3, 8, 0, 0, 0, expectedOffsets[0]);

        for (var i = 0; i < headers.Count; i++)
        {
            var marker = headers[i].QuerySelector(".test-day-header-marker");
            Assert.NotNull(marker);
            var expectedDay = new DateTimeOffset(
                expectedFirstDay.Date.AddDays(i), expectedOffsets[i]);
            Assert.Equal(expectedDay.ToString("O"), marker!.TextContent);
        }
    }
}
