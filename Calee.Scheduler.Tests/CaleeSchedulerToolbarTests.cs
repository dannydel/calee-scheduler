using AngleSharp.Dom;
using Bunit;
using Calee.Scheduler.Components;
using Calee.Scheduler.Contracts;
using Calee.Scheduler.Extensions;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;

namespace Calee.Scheduler.Tests;

/// <summary>
/// Tests for <see cref="CaleeSchedulerToolbar"/> in standalone mode. The cascaded
/// path (via <c>SchedulerStateContainer</c>) is exercised end-to-end in Task 11's
/// root-component tests.
/// </summary>
public class CaleeSchedulerToolbarTests
{
    private static readonly TimeZoneInfo TZ = TimeZoneInfo.FindSystemTimeZoneById("America/New_York");

    // Tue 2026-03-17 in America/New_York (EDT: -04:00 begins 2026-03-08).
    private static readonly DateTimeOffset Anchor = new(2026, 3, 17, 0, 0, 0, TimeSpan.FromHours(-4));

    private static BunitContext NewContext(TimeZoneInfo? defaultTimeZone = null)
    {
        var ctx = new BunitContext();
        ctx.Services.AddCaleeScheduler(o => o.DefaultTimeZone = defaultTimeZone);
        ctx.JSInterop.Mode = JSRuntimeMode.Loose;
        return ctx;
    }

    [Fact]
    public void Renders_Default_View_Buttons()
    {
        using var ctx = NewContext();
        var cut = ctx.Render<CaleeSchedulerToolbar>(p => p
            .Add(c => c.TimeZone, TZ)
            .Add(c => c.Date, Anchor)
            .Add(c => c.View, SchedulerView.Week));

        var buttons = cut.FindAll("[data-calee-region='toolbar-view-button']");
        Assert.Equal(5, buttons.Count);
        var labels = buttons.Select(b => b.TextContent.Trim()).ToList();
        // Phase 2 Task 18 reviewer follow-up — the bare-toolbar default surfaces the five
        // unconditionally-renderable views. Timeline is intentionally omitted because it
        // requires Lanes + LaneKey wiring that a bare toolbar can't reach; the composed
        // root re-gates Timeline against TimelineViewAvailable for the cascaded path.
        Assert.Equal(new[] { "Day", "Week", "Month", "Year", "Agenda" }, labels);
        Assert.DoesNotContain("Timeline", labels);
    }

    [Fact]
    public void Renders_Timeline_Button_When_AvailableViews_Includes_It()
    {
        using var ctx = NewContext();
        var cut = ctx.Render<CaleeSchedulerToolbar>(p => p
            .Add(c => c.TimeZone, TZ)
            .Add(c => c.Date, Anchor)
            .Add(c => c.View, SchedulerView.Week)
            .Add(c => c.AvailableViews, new[]
            {
                SchedulerView.Day, SchedulerView.Week, SchedulerView.Month, SchedulerView.Timeline,
            }));

        var buttons = cut.FindAll("[data-calee-region='toolbar-view-button']");
        Assert.Equal(4, buttons.Count);
        Assert.Contains(buttons, b => b.TextContent.Trim() == "Timeline");
    }

    [Fact]
    public void View_Switcher_Click_Fires_ViewChanged()
    {
        using var ctx = NewContext();
        SchedulerView? captured = null;
        var cut = ctx.Render<CaleeSchedulerToolbar>(p => p
            .Add(c => c.TimeZone, TZ)
            .Add(c => c.Date, Anchor)
            .Add(c => c.View, SchedulerView.Week)
            .Add(c => c.ViewChanged, v => captured = v));

        var monthBtn = cut.FindAll("[data-calee-region='toolbar-view-button']")
            .First(b => b.TextContent.Trim() == "Month");
        monthBtn.Click();

        Assert.Equal(SchedulerView.Month, captured);
    }

    [Fact]
    public void Today_Button_Sets_Date_To_Today_In_TimeZone()
    {
        using var ctx = NewContext();
        DateTimeOffset? captured = null;
        var cut = ctx.Render<CaleeSchedulerToolbar>(p => p
            .Add(c => c.TimeZone, TZ)
            .Add(c => c.Date, Anchor)
            .Add(c => c.View, SchedulerView.Day)
            .Add(c => c.DateChanged, d => captured = d));

        var today = cut.Find("[data-calee-region='toolbar-today']");
        today.Click();

        Assert.NotNull(captured);
        var nowInZone = TimeZoneInfo.ConvertTime(DateTimeOffset.UtcNow, TZ);
        Assert.Equal(nowInZone.Date, captured!.Value.Date);
        // The emitted offset must equal the zone's offset at that date.
        Assert.Equal(TZ.GetUtcOffset(captured.Value.Date), captured.Value.Offset);
    }

    [Fact]
    public void Prev_Button_Subtracts_One_Day_When_View_Day()
    {
        using var ctx = NewContext();
        DateTimeOffset? captured = null;
        var cut = ctx.Render<CaleeSchedulerToolbar>(p => p
            .Add(c => c.TimeZone, TZ)
            .Add(c => c.Date, Anchor)
            .Add(c => c.View, SchedulerView.Day)
            .Add(c => c.DateChanged, d => captured = d));

        cut.Find("[data-calee-region='toolbar-prev']").Click();
        Assert.Equal(Anchor.AddDays(-1), captured);
    }

    [Fact]
    public void Prev_Button_Subtracts_Seven_Days_When_View_Week()
    {
        using var ctx = NewContext();
        DateTimeOffset? captured = null;
        var cut = ctx.Render<CaleeSchedulerToolbar>(p => p
            .Add(c => c.TimeZone, TZ)
            .Add(c => c.Date, Anchor)
            .Add(c => c.View, SchedulerView.Week)
            .Add(c => c.DateChanged, d => captured = d));

        cut.Find("[data-calee-region='toolbar-prev']").Click();
        Assert.Equal(Anchor.AddDays(-7), captured);
    }

    [Fact]
    public void Prev_Button_Subtracts_One_Month_When_View_Month()
    {
        using var ctx = NewContext();
        DateTimeOffset? captured = null;
        var cut = ctx.Render<CaleeSchedulerToolbar>(p => p
            .Add(c => c.TimeZone, TZ)
            .Add(c => c.Date, Anchor)
            .Add(c => c.View, SchedulerView.Month)
            .Add(c => c.DateChanged, d => captured = d));

        cut.Find("[data-calee-region='toolbar-prev']").Click();
        Assert.NotNull(captured);
        Assert.Equal(new DateTime(2026, 2, 17), captured!.Value.Date);
        Assert.Equal(TZ.GetUtcOffset(captured.Value.Date), captured.Value.Offset);
    }

    [Fact]
    public void Prev_Button_Uses_TimelineScale_When_View_Timeline()
    {
        using var ctx = NewContext();
        DateTimeOffset? captured = null;
        var cut = ctx.Render<CaleeSchedulerToolbar>(p => p
            .Add(c => c.TimeZone, TZ)
            .Add(c => c.Date, Anchor)
            .Add(c => c.View, SchedulerView.Timeline)
            .Add(c => c.TimelineScale, TimelineScale.Week)
            .Add(c => c.AvailableViews, new[]
            {
                SchedulerView.Day, SchedulerView.Week, SchedulerView.Month, SchedulerView.Timeline,
            })
            .Add(c => c.DateChanged, d => captured = d));

        cut.Find("[data-calee-region='toolbar-prev']").Click();
        Assert.Equal(Anchor.AddDays(-7), captured);
    }

    [Fact]
    public void Next_Button_Mirrors_Prev()
    {
        using var ctx = NewContext();
        DateTimeOffset? captured = null;
        var cut = ctx.Render<CaleeSchedulerToolbar>(p => p
            .Add(c => c.TimeZone, TZ)
            .Add(c => c.Date, Anchor)
            .Add(c => c.View, SchedulerView.Week)
            .Add(c => c.DateChanged, d => captured = d));

        cut.Find("[data-calee-region='toolbar-next']").Click();
        Assert.Equal(Anchor.AddDays(7), captured);
    }

    [Fact]
    public void Range_Label_Day_Format()
    {
        using var ctx = NewContext();
        var cut = ctx.Render<CaleeSchedulerToolbar>(p => p
            .Add(c => c.TimeZone, TZ)
            .Add(c => c.Date, Anchor)
            .Add(c => c.View, SchedulerView.Day));

        var label = cut.Find("[data-calee-region='range-label']");
        Assert.Equal("Tue, Mar 17, 2026", label.TextContent.Trim());
    }

    [Fact]
    public void Range_Label_Week_Same_Month_Format()
    {
        using var ctx = NewContext();
        var cut = ctx.Render<CaleeSchedulerToolbar>(p => p
            .Add(c => c.TimeZone, TZ)
            .Add(c => c.Date, new DateTimeOffset(2026, 3, 18, 0, 0, 0, TimeSpan.FromHours(-4)))
            .Add(c => c.FirstDayOfWeek, DayOfWeek.Wednesday)
            .Add(c => c.View, SchedulerView.Week));

        var label = cut.Find("[data-calee-region='range-label']");
        Assert.Equal("Mar 18 – 24, 2026", label.TextContent.Trim());
    }

    [Fact]
    public void Range_Label_Week_Cross_Month_Format()
    {
        using var ctx = NewContext();
        var cut = ctx.Render<CaleeSchedulerToolbar>(p => p
            .Add(c => c.TimeZone, TZ)
            .Add(c => c.Date, new DateTimeOffset(2026, 3, 31, 0, 0, 0, TimeSpan.FromHours(-4)))
            .Add(c => c.FirstDayOfWeek, DayOfWeek.Sunday)
            .Add(c => c.View, SchedulerView.Week));

        var label = cut.Find("[data-calee-region='range-label']");
        Assert.Equal("Mar 29 – Apr 4, 2026", label.TextContent.Trim());
    }

    [Fact]
    public void Range_Label_Week_Cross_Year_Format()
    {
        using var ctx = NewContext();
        var cut = ctx.Render<CaleeSchedulerToolbar>(p => p
            .Add(c => c.TimeZone, TZ)
            .Add(c => c.Date, new DateTimeOffset(2025, 12, 31, 0, 0, 0, TimeSpan.FromHours(-5)))
            .Add(c => c.FirstDayOfWeek, DayOfWeek.Monday)
            .Add(c => c.View, SchedulerView.Week));

        var label = cut.Find("[data-calee-region='range-label']");
        Assert.Equal("Dec 29, 2025 – Jan 4, 2026", label.TextContent.Trim());
    }

    [Fact]
    public void Range_Label_Month_Format()
    {
        using var ctx = NewContext();
        var cut = ctx.Render<CaleeSchedulerToolbar>(p => p
            .Add(c => c.TimeZone, TZ)
            .Add(c => c.Date, Anchor)
            .Add(c => c.View, SchedulerView.Month));

        var label = cut.Find("[data-calee-region='range-label']");
        Assert.Equal("March 2026", label.TextContent.Trim());
    }

    [Fact]
    public void Range_Label_Timeline_Mode_Delegates_To_TimeScale()
    {
        using var ctx = NewContext();
        var cut = ctx.Render<CaleeSchedulerToolbar>(p => p
            .Add(c => c.TimeZone, TZ)
            .Add(c => c.Date, Anchor)
            .Add(c => c.View, SchedulerView.Timeline)
            .Add(c => c.TimelineScale, TimelineScale.Month)
            .Add(c => c.AvailableViews, new[]
            {
                SchedulerView.Day, SchedulerView.Week, SchedulerView.Month, SchedulerView.Timeline,
            }));

        var label = cut.Find("[data-calee-region='range-label']");
        Assert.Equal("March 2026", label.TextContent.Trim());
    }

    [Fact]
    public void View_Switcher_Active_Button_Has_AriaChecked_True_Exactly_Once()
    {
        using var ctx = NewContext();
        var cut = ctx.Render<CaleeSchedulerToolbar>(p => p
            .Add(c => c.TimeZone, TZ)
            .Add(c => c.Date, Anchor)
            .Add(c => c.View, SchedulerView.Week)
            .Add(c => c.AvailableViews, new[]
            {
                SchedulerView.Day, SchedulerView.Week, SchedulerView.Month, SchedulerView.Timeline,
            }));

        var buttons = cut.FindAll("[data-calee-region='toolbar-view-button']");
        var checkedButtons = buttons.Where(b => b.GetAttribute("aria-checked") == "true").ToList();
        Assert.Single(checkedButtons);
        Assert.Equal("Week", checkedButtons[0].TextContent.Trim());

        // All others must explicitly say aria-checked="false".
        foreach (var b in buttons.Where(b => b != checkedButtons[0]))
        {
            Assert.Equal("false", b.GetAttribute("aria-checked"));
        }
    }

    [Fact]
    public void Range_Label_Has_AriaLive_Polite()
    {
        using var ctx = NewContext();
        var cut = ctx.Render<CaleeSchedulerToolbar>(p => p
            .Add(c => c.TimeZone, TZ)
            .Add(c => c.Date, Anchor)
            .Add(c => c.View, SchedulerView.Day));

        var label = cut.Find("[data-calee-region='range-label']");
        Assert.Equal("polite", label.GetAttribute("aria-live"));
    }

    [Fact]
    public void ToolbarClass_Applied_To_Root()
    {
        using var ctx = NewContext();
        var cut = ctx.Render<CaleeSchedulerToolbar>(p => p
            .Add(c => c.TimeZone, TZ)
            .Add(c => c.Date, Anchor)
            .Add(c => c.View, SchedulerView.Week)
            .Add(c => c.ToolbarClass, "my-fancy-toolbar"));

        var root = cut.Find("[data-calee-region='toolbar']");
        var classes = root.GetAttribute("class") ?? string.Empty;
        Assert.Contains("calee-scheduler-toolbar", classes);
        Assert.Contains("my-fancy-toolbar", classes);
    }

    [Fact]
    public void AdditionalAttributes_Splatted_Onto_Root()
    {
        using var ctx = NewContext();
        var extra = new Dictionary<string, object>
        {
            ["data-test-id"] = "main-toolbar",
            ["title"] = "Scheduler controls",
        };
        var cut = ctx.Render<CaleeSchedulerToolbar>(p => p
            .Add(c => c.TimeZone, TZ)
            .Add(c => c.Date, Anchor)
            .Add(c => c.View, SchedulerView.Week)
            .Add(c => c.AdditionalAttributes, extra));

        var root = cut.Find("[data-calee-region='toolbar']");
        Assert.Equal("main-toolbar", root.GetAttribute("data-test-id"));
        Assert.Equal("Scheduler controls", root.GetAttribute("title"));
    }

    // ───────────────────────────────────────────────────────────────────────────
    // Phase 2 Task 18 — Year + Agenda surfacing in the standalone toolbar
    // ───────────────────────────────────────────────────────────────────────────

    [Fact]
    public void Renders_Year_Button_When_AvailableViews_Includes_It()
    {
        using var ctx = NewContext();
        var cut = ctx.Render<CaleeSchedulerToolbar>(p => p
            .Add(c => c.TimeZone, TZ)
            .Add(c => c.Date, Anchor)
            .Add(c => c.View, SchedulerView.Year)
            .Add(c => c.AvailableViews, new[]
            {
                SchedulerView.Day, SchedulerView.Week, SchedulerView.Month, SchedulerView.Year,
            }));

        var labels = cut.FindAll("[data-calee-region='toolbar-view-button']")
            .Select(b => b.TextContent.Trim())
            .ToList();
        Assert.Contains("Year", labels);
    }

    [Fact]
    public void Renders_Agenda_Button_When_AvailableViews_Includes_It()
    {
        using var ctx = NewContext();
        var cut = ctx.Render<CaleeSchedulerToolbar>(p => p
            .Add(c => c.TimeZone, TZ)
            .Add(c => c.Date, Anchor)
            .Add(c => c.View, SchedulerView.Agenda)
            .Add(c => c.AvailableViews, new[]
            {
                SchedulerView.Day, SchedulerView.Week, SchedulerView.Month,
                SchedulerView.Year, SchedulerView.Agenda,
            }));

        var labels = cut.FindAll("[data-calee-region='toolbar-view-button']")
            .Select(b => b.TextContent.Trim())
            .ToList();
        Assert.Contains("Agenda", labels);
    }

    [Fact]
    public void Year_Button_Click_Fires_ViewChanged_With_Year()
    {
        using var ctx = NewContext();
        SchedulerView? captured = null;
        var cut = ctx.Render<CaleeSchedulerToolbar>(p => p
            .Add(c => c.TimeZone, TZ)
            .Add(c => c.Date, Anchor)
            .Add(c => c.View, SchedulerView.Day)
            .Add(c => c.AvailableViews, new[]
            {
                SchedulerView.Day, SchedulerView.Week, SchedulerView.Month, SchedulerView.Year,
            })
            .Add(c => c.ViewChanged, v => captured = v));

        var yearBtn = cut.FindAll("[data-calee-region='toolbar-view-button']")
            .First(b => b.TextContent.Trim() == "Year");
        yearBtn.Click();

        Assert.Equal(SchedulerView.Year, captured);
    }

    [Fact]
    public void Agenda_Button_Click_Fires_ViewChanged_With_Agenda()
    {
        using var ctx = NewContext();
        SchedulerView? captured = null;
        var cut = ctx.Render<CaleeSchedulerToolbar>(p => p
            .Add(c => c.TimeZone, TZ)
            .Add(c => c.Date, Anchor)
            .Add(c => c.View, SchedulerView.Day)
            .Add(c => c.AvailableViews, new[]
            {
                SchedulerView.Day, SchedulerView.Week, SchedulerView.Month,
                SchedulerView.Year, SchedulerView.Agenda,
            })
            .Add(c => c.ViewChanged, v => captured = v));

        var agendaBtn = cut.FindAll("[data-calee-region='toolbar-view-button']")
            .First(b => b.TextContent.Trim() == "Agenda");
        agendaBtn.Click();

        Assert.Equal(SchedulerView.Agenda, captured);
    }

    [Fact]
    public void Range_Label_Year_Format()
    {
        using var ctx = NewContext();
        var cut = ctx.Render<CaleeSchedulerToolbar>(p => p
            .Add(c => c.TimeZone, TZ)
            .Add(c => c.Date, Anchor)
            .Add(c => c.View, SchedulerView.Year));

        var label = cut.Find("[data-calee-region='range-label']");
        Assert.Equal("2026", label.TextContent.Trim());
    }

    [Fact]
    public void Range_Label_Agenda_Format_Multi_Day_Window()
    {
        using var ctx = NewContext();
        var cut = ctx.Render<CaleeSchedulerToolbar>(p => p
            .Add(c => c.TimeZone, TZ)
            .Add(c => c.Date, Anchor)
            .Add(c => c.View, SchedulerView.Agenda)
            .Add(c => c.AgendaDays, 7));

        var label = cut.Find("[data-calee-region='range-label']");
        // Anchor is Tue 2026-03-17 → "Mar 17 – 23, 2026" (same month).
        Assert.Equal("Mar 17 – 23, 2026", label.TextContent.Trim());
    }

    [Fact]
    public void Range_Label_Agenda_Format_Single_Day_Window()
    {
        using var ctx = NewContext();
        var cut = ctx.Render<CaleeSchedulerToolbar>(p => p
            .Add(c => c.TimeZone, TZ)
            .Add(c => c.Date, Anchor)
            .Add(c => c.View, SchedulerView.Agenda)
            .Add(c => c.AgendaDays, 1));

        var label = cut.Find("[data-calee-region='range-label']");
        // AgendaDays=1 → degenerates to the Day shape.
        Assert.Equal("Tue, Mar 17, 2026", label.TextContent.Trim());
    }

    [Fact]
    public void Prev_Button_Steps_By_One_Year_When_View_Year()
    {
        using var ctx = NewContext();
        DateTimeOffset? captured = null;
        var cut = ctx.Render<CaleeSchedulerToolbar>(p => p
            .Add(c => c.TimeZone, TZ)
            .Add(c => c.Date, Anchor)
            .Add(c => c.View, SchedulerView.Year)
            .Add(c => c.DateChanged, d => captured = d));

        cut.Find("[data-calee-region='toolbar-prev']").Click();
        Assert.NotNull(captured);
        Assert.Equal(2025, captured!.Value.Year);
    }

    [Fact]
    public void Prev_Button_Steps_By_AgendaDays_When_View_Agenda()
    {
        using var ctx = NewContext();
        DateTimeOffset? captured = null;
        var cut = ctx.Render<CaleeSchedulerToolbar>(p => p
            .Add(c => c.TimeZone, TZ)
            .Add(c => c.Date, Anchor)
            .Add(c => c.View, SchedulerView.Agenda)
            .Add(c => c.AgendaDays, 14)
            .Add(c => c.DateChanged, d => captured = d));

        cut.Find("[data-calee-region='toolbar-prev']").Click();
        Assert.NotNull(captured);
        // Anchor is Tue 2026-03-17 (EDT). Step back 14 days → Tue 2026-03-03 (EST,
        // -05:00) because the toolbar's AdvanceAnchor recomputes the offset at the
        // new date so DST transitions are honored.
        Assert.Equal(new DateTime(2026, 3, 3), captured!.Value.Date);
        Assert.Equal(TZ.GetUtcOffset(captured.Value.Date), captured.Value.Offset);
    }

    // ───────────────────────────────────────────────────────────────────────────
    // Issue #7 — SchedulerView.WorkWeek surfacing in the standalone toolbar.
    // ───────────────────────────────────────────────────────────────────────────

    [Fact]
    public void Renders_WorkWeek_Button_When_AvailableViews_Includes_It()
    {
        using var ctx = NewContext();
        var cut = ctx.Render<CaleeSchedulerToolbar>(p => p
            .Add(c => c.TimeZone, TZ)
            .Add(c => c.Date, Anchor)
            .Add(c => c.View, SchedulerView.WorkWeek)
            .Add(c => c.AvailableViews, new[]
            {
                SchedulerView.Day, SchedulerView.WorkWeek, SchedulerView.Week, SchedulerView.Month,
            }));

        var labels = cut.FindAll("[data-calee-region='toolbar-view-button']")
            .Select(b => b.TextContent.Trim())
            .ToList();
        Assert.Contains("Work Week", labels);
    }

    [Fact]
    public void WorkWeek_Button_Click_Fires_ViewChanged_With_WorkWeek()
    {
        using var ctx = NewContext();
        SchedulerView? captured = null;
        var cut = ctx.Render<CaleeSchedulerToolbar>(p => p
            .Add(c => c.TimeZone, TZ)
            .Add(c => c.Date, Anchor)
            .Add(c => c.View, SchedulerView.Day)
            .Add(c => c.AvailableViews, new[]
            {
                SchedulerView.Day, SchedulerView.WorkWeek, SchedulerView.Week, SchedulerView.Month,
            })
            .Add(c => c.ViewChanged, v => captured = v));

        var workWeekBtn = cut.FindAll("[data-calee-region='toolbar-view-button']")
            .First(b => b.TextContent.Trim() == "Work Week");
        workWeekBtn.Click();

        Assert.Equal(SchedulerView.WorkWeek, captured);
    }

    [Fact]
    public void Prev_And_Next_Button_Step_Seven_Days_When_View_WorkWeek()
    {
        using var ctx = NewContext();
        DateTimeOffset? captured = null;
        var cut = ctx.Render<CaleeSchedulerToolbar>(p => p
            .Add(c => c.TimeZone, TZ)
            .Add(c => c.Date, Anchor)
            .Add(c => c.View, SchedulerView.WorkWeek)
            .Add(c => c.DateChanged, d => captured = d));

        cut.Find("[data-calee-region='toolbar-prev']").Click();
        Assert.Equal(Anchor.AddDays(-7), captured);

        cut.Find("[data-calee-region='toolbar-next']").Click();
        Assert.Equal(Anchor.AddDays(7), captured);
    }

    [Fact]
    public void Range_Label_WorkWeek_Default_MonToFri_Format()
    {
        using var ctx = NewContext();
        // Anchor Tue 2026-03-17, FirstDayOfWeek=Sunday → week is Sun 3/15..Sat 3/21.
        // WorkWeekDays defaults to Mon-Fri → 3/16..3/20 → "Mar 16 – 20, 2026".
        var cut = ctx.Render<CaleeSchedulerToolbar>(p => p
            .Add(c => c.TimeZone, TZ)
            .Add(c => c.Date, Anchor)
            .Add(c => c.View, SchedulerView.WorkWeek));

        var label = cut.Find("[data-calee-region='range-label']");
        Assert.Equal("Mar 16 – 20, 2026", label.TextContent.Trim());
    }

    [Fact]
    public void Range_Label_WorkWeek_Honors_WorkWeekDays_Override()
    {
        using var ctx = NewContext();
        var cut = ctx.Render<CaleeSchedulerToolbar>(p => p
            .Add(c => c.TimeZone, TZ)
            .Add(c => c.Date, Anchor)
            .Add(c => c.View, SchedulerView.WorkWeek)
            .Add(c => c.WorkWeekDays, new[] { DayOfWeek.Tuesday, DayOfWeek.Wednesday, DayOfWeek.Thursday }));

        var label = cut.Find("[data-calee-region='range-label']");
        Assert.Equal("Mar 17 – 19, 2026", label.TextContent.Trim());
    }

    [Fact]
    public void View_Switcher_Active_Button_Highlights_WorkWeek_Exactly_Once()
    {
        using var ctx = NewContext();
        var cut = ctx.Render<CaleeSchedulerToolbar>(p => p
            .Add(c => c.TimeZone, TZ)
            .Add(c => c.Date, Anchor)
            .Add(c => c.View, SchedulerView.WorkWeek)
            .Add(c => c.AvailableViews, new[]
            {
                SchedulerView.Day, SchedulerView.WorkWeek, SchedulerView.Week, SchedulerView.Month,
            }));

        var buttons = cut.FindAll("[data-calee-region='toolbar-view-button']");
        var checkedButtons = buttons.Where(b => b.GetAttribute("aria-checked") == "true").ToList();
        Assert.Single(checkedButtons);
        Assert.Equal("Work Week", checkedButtons[0].TextContent.Trim());
    }

    [Fact]
    public void Missing_TimeZone_In_Standalone_Mode_Throws()
    {
        using var ctx = NewContext();

        // No TimeZone, no cascade, and NewContext's AddCaleeScheduler() leaves
        // DefaultTimeZone at its null default — the layered resolution's terminal
        // throw fires (issue #34).
        var ex = Assert.Throws<InvalidOperationException>(() =>
            ctx.Render<CaleeSchedulerToolbar>(p => p
                .Add(c => c.TimeZone, (TimeZoneInfo)null!)
                .Add(c => c.Date, Anchor)
                .Add(c => c.View, SchedulerView.Week)));
        Assert.Contains("TimeZone", ex.Message, StringComparison.Ordinal);
        Assert.Contains("CascadingValue", ex.Message, StringComparison.Ordinal);
        Assert.Contains("DefaultTimeZone", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Standalone_Mode_Resolves_TimeZone_From_Ancestor_Cascade()
    {
        // No TimeZone parameter, no DefaultTimeZone option — only an ancestor
        // CascadingValue<TimeZoneInfo> (issue #34, rung 2).
        using var ctx = NewContext();
        DateTimeOffset? captured = null;
        var cut = ctx.Render<CaleeSchedulerToolbar>(p => p
            .AddCascadingValue(TZ)
            .Add(c => c.Date, Anchor)
            .Add(c => c.View, SchedulerView.Day)
            .Add(c => c.DateChanged, d => captured = d));

        var today = cut.Find("[data-calee-region='toolbar-today']");
        today.Click();

        Assert.NotNull(captured);
        var nowInZone = TimeZoneInfo.ConvertTime(DateTimeOffset.UtcNow, TZ);
        Assert.Equal(nowInZone.Date, captured!.Value.Date);
        Assert.Equal(TZ.GetUtcOffset(captured.Value.Date), captured.Value.Offset);
    }

    [Fact]
    public void Standalone_Mode_Resolves_TimeZone_From_DefaultTimeZone_Option()
    {
        // No TimeZone parameter, no cascade — only CaleeSchedulerOptions.DefaultTimeZone
        // (issue #34, rung 3).
        using var ctx = NewContext(defaultTimeZone: TZ);
        DateTimeOffset? captured = null;
        var cut = ctx.Render<CaleeSchedulerToolbar>(p => p
            .Add(c => c.Date, Anchor)
            .Add(c => c.View, SchedulerView.Day)
            .Add(c => c.DateChanged, d => captured = d));

        var today = cut.Find("[data-calee-region='toolbar-today']");
        today.Click();

        Assert.NotNull(captured);
        var nowInZone = TimeZoneInfo.ConvertTime(DateTimeOffset.UtcNow, TZ);
        Assert.Equal(nowInZone.Date, captured!.Value.Date);
        Assert.Equal(TZ.GetUtcOffset(captured.Value.Date), captured.Value.Offset);
    }

    // ───────────────────────────────────────────────────────────────────────────
    // Issue #31 — toolbar content slots (ToolbarStart / ToolbarEnd).
    // ───────────────────────────────────────────────────────────────────────────

    [Fact]
    public void ToolbarStart_Renders_Before_Nav_Group()
    {
        using var ctx = NewContext();
        var cut = ctx.Render<CaleeSchedulerToolbar>(p => p
            .Add(c => c.TimeZone, TZ)
            .Add(c => c.Date, Anchor)
            .Add(c => c.View, SchedulerView.Week)
            .Add(c => c.ToolbarStart, "<button type=\"button\" data-testid=\"start-btn\">Start</button>"));

        var start = cut.Find("[data-calee-region='toolbar-start']");
        Assert.Contains("start-btn", start.InnerHtml);

        var markup = cut.Markup;
        var startIndex = markup.IndexOf("data-calee-region=\"toolbar-start\"", StringComparison.Ordinal);
        var todayIndex = markup.IndexOf("data-calee-region=\"toolbar-today\"", StringComparison.Ordinal);
        Assert.True(startIndex >= 0 && todayIndex >= 0 && startIndex < todayIndex,
            "toolbar-start must precede toolbar-today in document order.");
    }

    [Fact]
    public void ToolbarEnd_Renders_After_View_Switcher()
    {
        using var ctx = NewContext();
        var cut = ctx.Render<CaleeSchedulerToolbar>(p => p
            .Add(c => c.TimeZone, TZ)
            .Add(c => c.Date, Anchor)
            .Add(c => c.View, SchedulerView.Week)
            .Add(c => c.ToolbarEnd, "<button type=\"button\" data-testid=\"end-btn\">End</button>"));

        var end = cut.Find("[data-calee-region='toolbar-end']");
        Assert.Contains("end-btn", end.InnerHtml);

        var markup = cut.Markup;
        var switcherIndex = markup.IndexOf("data-calee-region=\"view-switcher\"", StringComparison.Ordinal);
        var endIndex = markup.IndexOf("data-calee-region=\"toolbar-end\"", StringComparison.Ordinal);
        Assert.True(switcherIndex >= 0 && endIndex >= 0 && switcherIndex < endIndex,
            "toolbar-end must follow view-switcher in document order.");
    }

    [Fact]
    public void Null_Slots_Emit_No_Wrapper()
    {
        using var ctx = NewContext();
        var cut = ctx.Render<CaleeSchedulerToolbar>(p => p
            .Add(c => c.TimeZone, TZ)
            .Add(c => c.Date, Anchor)
            .Add(c => c.View, SchedulerView.Week));

        Assert.Empty(cut.FindAll("[data-calee-region='toolbar-start']"));
        Assert.Empty(cut.FindAll("[data-calee-region='toolbar-end']"));
    }

    [Fact]
    public void Explicit_Null_Slots_Markup_Matches_Default()
    {
        using var ctxDefault = NewContext();
        var defaultCut = ctxDefault.Render<CaleeSchedulerToolbar>(p => p
            .Add(c => c.TimeZone, TZ)
            .Add(c => c.Date, Anchor)
            .Add(c => c.View, SchedulerView.Week));

        using var ctxExplicit = NewContext();
        var explicitCut = ctxExplicit.Render<CaleeSchedulerToolbar>(p => p
            .Add(c => c.TimeZone, TZ)
            .Add(c => c.Date, Anchor)
            .Add(c => c.View, SchedulerView.Week)
            .Add(c => c.ToolbarStart, (RenderFragment?)null)
            .Add(c => c.ToolbarEnd, (RenderFragment?)null));

        explicitCut.MarkupMatches(defaultCut.Markup);
    }

    [Fact]
    public void Populated_Slots_Expose_Both_Region_Attributes()
    {
        using var ctx = NewContext();
        var cut = ctx.Render<CaleeSchedulerToolbar>(p => p
            .Add(c => c.TimeZone, TZ)
            .Add(c => c.Date, Anchor)
            .Add(c => c.View, SchedulerView.Week)
            .Add(c => c.ToolbarStart, "<span>Start content</span>")
            .Add(c => c.ToolbarEnd, "<span>End content</span>"));

        var start = cut.Find("[data-calee-region='toolbar-start']");
        var end = cut.Find("[data-calee-region='toolbar-end']");
        Assert.Contains("Start content", start.InnerHtml);
        Assert.Contains("End content", end.InnerHtml);
    }

    [Fact]
    public void Slots_Do_Not_Disturb_Shell()
    {
        using var ctx = NewContext();
        var cut = ctx.Render<CaleeSchedulerToolbar>(p => p
            .Add(c => c.TimeZone, TZ)
            .Add(c => c.Date, Anchor)
            .Add(c => c.View, SchedulerView.Week)
            .Add(c => c.ToolbarStart, "<span>Start content</span>")
            .Add(c => c.ToolbarEnd, "<span>End content</span>"));

        Assert.Single(cut.FindAll("[data-calee-region='range-label'][aria-live='polite']"));
        Assert.Single(cut.FindAll("[data-calee-region='view-switcher'][role='radiogroup']"));
        Assert.Equal(5, cut.FindAll("[data-calee-region='toolbar-view-button']").Count);
    }
}
