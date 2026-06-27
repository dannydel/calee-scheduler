using AngleSharp.Dom;
using Bunit;
using Calee.Scheduler.Components;
using Calee.Scheduler.Contracts;
using Calee.Scheduler.Extensions;
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

    private static BunitContext NewContext()
    {
        var ctx = new BunitContext();
        ctx.Services.AddCaleeScheduler();
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

    [Fact]
    public void Missing_TimeZone_In_Standalone_Mode_Throws()
    {
        using var ctx = NewContext();
        var ex = Assert.Throws<ArgumentNullException>(() =>
            ctx.Render<CaleeSchedulerToolbar>(p => p
                .Add(c => c.TimeZone, (TimeZoneInfo)null!)
                .Add(c => c.Date, Anchor)
                .Add(c => c.View, SchedulerView.Week)));
        Assert.Equal("TimeZone", ex.ParamName);
    }
}
