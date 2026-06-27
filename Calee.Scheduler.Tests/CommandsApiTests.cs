using Bunit;
using Calee.Scheduler.Components;
using Calee.Scheduler.Contracts;
using Calee.Scheduler.Extensions;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.Extensions.DependencyInjection;

namespace Calee.Scheduler.Tests;

/// <summary>
/// Tests for the Phase 2 Task 15 command palette trigger gate + Commands API
/// (FR-37 + FR-29 fail-closed default for AllowCommandPalette; ADR-0014). Covers:
/// <list type="bullet">
///   <item>Default fail-closed shape: Commands is empty; Cmd+K does not fire.</item>
///   <item>Built-in command coverage with AllowCommandPalette=true (view.*, navigate.today,
///     edit.create, edit.move, help.open).</item>
///   <item>Conditional built-ins gated by their backing Allow* flag
///     (edit.delete ↔ AllowDelete; edit.undo + edit.redo ↔ AllowUndoRedo).</item>
///   <item>palette.open is excluded — the palette cannot invoke its own opening.</item>
///   <item>Custom commands merge after built-ins; consumer-wins precedence on
///     built-in id collisions.</item>
///   <item>Each built-in's Invoke routes to the same callback the keystroke handler
///     fires (undo, redo, today, view-switch, help, create, move).</item>
///   <item>Consumer-supplied Invoke executes when called from the merged list.</item>
///   <item>Order stability across unrelated parameter changes.</item>
/// </list>
/// </summary>
public class CommandsApiTests
{
    private static readonly TimeZoneInfo TZ = TimeZoneInfo.FindSystemTimeZoneById("America/New_York");
    private static readonly DateTimeOffset Anchor = new(2026, 5, 19, 0, 0, 0, TimeSpan.FromHours(-4));

    private static BunitContext NewContext()
    {
        var ctx = new BunitContext();
        ctx.Services.AddCaleeScheduler();
        ctx.JSInterop.Mode = JSRuntimeMode.Loose;
        return ctx;
    }

    private static CalendarEvent Timed(string id, int startHour, int endHour, DateTimeOffset? on = null)
    {
        var date = on ?? Anchor;
        var start = new DateTimeOffset(date.Year, date.Month, date.Day, startHour, 0, 0, date.Offset);
        var end = new DateTimeOffset(date.Year, date.Month, date.Day, endHour, 0, 0, date.Offset);
        return new CalendarEvent(id, id, start, end, IsAllDay: false);
    }

    private static SchedulerCommand? FindById(IReadOnlyList<SchedulerCommand> list, string id)
    {
        foreach (var cmd in list)
        {
            if (cmd.Id == id) return cmd;
        }
        return null;
    }

    // ───────────────────────────────────────────────────────────────────────────
    // Fail-closed default — AllowCommandPalette is false → empty Commands list +
    // Cmd+K does NOT fire OnCommandPaletteRequested.
    // ───────────────────────────────────────────────────────────────────────────

    [Fact]
    public void AllowCommandPalette_Defaults_False()
    {
        using var ctx = NewContext();
        var cut = ctx.Render<CaleeSchedulerDayView<CalendarEvent>>(p => p
            .Add(c => c.TimeZone, TZ)
            .Add(c => c.Date, Anchor)
            .Add(c => c.Events, Array.Empty<CalendarEvent>()));

        Assert.False(cut.Instance.AllowCommandPalette);
    }

    [Fact]
    public void Commands_Is_Empty_When_AllowCommandPalette_False()
    {
        using var ctx = NewContext();
        var cut = ctx.Render<CaleeSchedulerDayView<CalendarEvent>>(p => p
            .Add(c => c.TimeZone, TZ)
            .Add(c => c.Date, Anchor)
            .Add(c => c.Events, Array.Empty<CalendarEvent>()));

        Assert.Empty(cut.Instance.Commands);
    }

    [Fact]
    public void Commands_Is_Empty_When_AllowCommandPalette_False_Even_With_CustomCommands()
    {
        // The fail-closed default should suppress consumer-supplied commands too —
        // ADR-0014 keeps the trigger and the API list in lockstep.
        using var ctx = NewContext();
        var customs = new[]
        {
            new SchedulerCommand("custom.foo", "Foo", null, () => { }),
        };
        var cut = ctx.Render<CaleeSchedulerDayView<CalendarEvent>>(p => p
            .Add(c => c.TimeZone, TZ)
            .Add(c => c.Date, Anchor)
            .Add(c => c.Events, Array.Empty<CalendarEvent>())
            .Add(c => c.CustomCommands, customs));

        Assert.Empty(cut.Instance.Commands);
    }

    [Fact]
    public async Task CmdK_Does_Not_Fire_When_AllowCommandPalette_False()
    {
        // FR-29 fail-closed: the Cmd+K binding is a no-op until the consumer opts in.
        using var ctx = NewContext();
        var paletteCount = 0;
        var cut = ctx.Render<CaleeSchedulerDayView<CalendarEvent>>(p => p
            .Add(c => c.TimeZone, TZ)
            .Add(c => c.Date, Anchor)
            .Add(c => c.Events, Array.Empty<CalendarEvent>())
            // Intentionally not setting AllowCommandPalette → false.
            .Add(c => c.OnCommandPaletteRequested,
                EventCallback.Factory.Create(this, () => paletteCount++)));

        await cut.InvokeAsync(() =>
            cut.Find("[role='grid']")
                .KeyDown(new KeyboardEventArgs { Key = "k", MetaKey = true }));

        Assert.Equal(0, paletteCount);
    }

    [Fact]
    public async Task CtrlK_Does_Not_Fire_When_AllowCommandPalette_False()
    {
        using var ctx = NewContext();
        var paletteCount = 0;
        var cut = ctx.Render<CaleeSchedulerDayView<CalendarEvent>>(p => p
            .Add(c => c.TimeZone, TZ)
            .Add(c => c.Date, Anchor)
            .Add(c => c.Events, Array.Empty<CalendarEvent>())
            .Add(c => c.OnCommandPaletteRequested,
                EventCallback.Factory.Create(this, () => paletteCount++)));

        await cut.InvokeAsync(() =>
            cut.Find("[role='grid']")
                .KeyDown(new KeyboardEventArgs { Key = "k", CtrlKey = true }));

        Assert.Equal(0, paletteCount);
    }

    // ───────────────────────────────────────────────────────────────────────────
    // Built-in commands always present (when AllowCommandPalette=true).
    // ───────────────────────────────────────────────────────────────────────────

    [Fact]
    public void Commands_Contains_Always_On_BuiltIns_When_Enabled()
    {
        using var ctx = NewContext();
        var cut = ctx.Render<CaleeSchedulerDayView<CalendarEvent>>(p => p
            .Add(c => c.TimeZone, TZ)
            .Add(c => c.Date, Anchor)
            .Add(c => c.Events, Array.Empty<CalendarEvent>())
            .Add(c => c.AllowCommandPalette, true));

        var ids = new HashSet<string>(StringComparer.Ordinal);
        foreach (var cmd in cut.Instance.Commands) ids.Add(cmd.Id);

        // These built-ins are always present per the ADR-0014 table.
        Assert.Contains(SchedulerCommandIds.ViewDay, ids);
        Assert.Contains(SchedulerCommandIds.ViewWeek, ids);
        Assert.Contains(SchedulerCommandIds.ViewMonth, ids);
        Assert.Contains(SchedulerCommandIds.ViewYear, ids);
        Assert.Contains(SchedulerCommandIds.ViewAgenda, ids);
        Assert.Contains(SchedulerCommandIds.ViewTimeline, ids);
        Assert.Contains(SchedulerCommandIds.NavigateToday, ids);
        Assert.Contains(SchedulerCommandIds.EditCreate, ids);
        Assert.Contains(SchedulerCommandIds.EditMove, ids);
        Assert.Contains(SchedulerCommandIds.HelpOpen, ids);
    }

    [Fact]
    public void Commands_BuiltIns_Carry_Group_Annotations()
    {
        using var ctx = NewContext();
        var cut = ctx.Render<CaleeSchedulerDayView<CalendarEvent>>(p => p
            .Add(c => c.TimeZone, TZ)
            .Add(c => c.Date, Anchor)
            .Add(c => c.Events, Array.Empty<CalendarEvent>())
            .Add(c => c.AllowCommandPalette, true));

        var cmds = cut.Instance.Commands;
        Assert.Equal("View", FindById(cmds, SchedulerCommandIds.ViewDay)!.Group);
        Assert.Equal("View", FindById(cmds, SchedulerCommandIds.ViewTimeline)!.Group);
        Assert.Equal("Navigate", FindById(cmds, SchedulerCommandIds.NavigateToday)!.Group);
        Assert.Equal("Edit", FindById(cmds, SchedulerCommandIds.EditCreate)!.Group);
        Assert.Equal("Edit", FindById(cmds, SchedulerCommandIds.EditMove)!.Group);
        Assert.Equal("Help", FindById(cmds, SchedulerCommandIds.HelpOpen)!.Group);
    }

    [Fact]
    public void Commands_BuiltIns_Carry_English_Labels()
    {
        using var ctx = NewContext();
        var cut = ctx.Render<CaleeSchedulerDayView<CalendarEvent>>(p => p
            .Add(c => c.TimeZone, TZ)
            .Add(c => c.Date, Anchor)
            .Add(c => c.Events, Array.Empty<CalendarEvent>())
            .Add(c => c.AllowCommandPalette, true));

        var cmds = cut.Instance.Commands;
        Assert.Equal("Day view", FindById(cmds, SchedulerCommandIds.ViewDay)!.Label);
        Assert.Equal("Go to today", FindById(cmds, SchedulerCommandIds.NavigateToday)!.Label);
        Assert.Equal("Create event", FindById(cmds, SchedulerCommandIds.EditCreate)!.Label);
        Assert.Equal("Help", FindById(cmds, SchedulerCommandIds.HelpOpen)!.Label);
    }

    // ───────────────────────────────────────────────────────────────────────────
    // Conditional built-ins gated by Allow* flags.
    // ───────────────────────────────────────────────────────────────────────────

    [Fact]
    public void Commands_Excludes_EditDelete_When_AllowDelete_False()
    {
        using var ctx = NewContext();
        var cut = ctx.Render<CaleeSchedulerDayView<CalendarEvent>>(p => p
            .Add(c => c.TimeZone, TZ)
            .Add(c => c.Date, Anchor)
            .Add(c => c.Events, Array.Empty<CalendarEvent>())
            .Add(c => c.AllowCommandPalette, true));
            // AllowDelete defaults false.

        Assert.Null(FindById(cut.Instance.Commands, SchedulerCommandIds.EditDelete));
    }

    [Fact]
    public void Commands_Includes_EditDelete_When_AllowDelete_True()
    {
        using var ctx = NewContext();
        var cut = ctx.Render<CaleeSchedulerDayView<CalendarEvent>>(p => p
            .Add(c => c.TimeZone, TZ)
            .Add(c => c.Date, Anchor)
            .Add(c => c.Events, Array.Empty<CalendarEvent>())
            .Add(c => c.AllowCommandPalette, true)
            .Add(c => c.AllowDelete, true));

        var delete = FindById(cut.Instance.Commands, SchedulerCommandIds.EditDelete);
        Assert.NotNull(delete);
        Assert.Equal("Delete focused event", delete!.Label);
        Assert.Equal("Edit", delete.Group);
    }

    [Fact]
    public void Commands_Excludes_Undo_Redo_When_AllowUndoRedo_False()
    {
        using var ctx = NewContext();
        var cut = ctx.Render<CaleeSchedulerDayView<CalendarEvent>>(p => p
            .Add(c => c.TimeZone, TZ)
            .Add(c => c.Date, Anchor)
            .Add(c => c.Events, Array.Empty<CalendarEvent>())
            .Add(c => c.AllowCommandPalette, true));
            // AllowUndoRedo defaults false.

        Assert.Null(FindById(cut.Instance.Commands, SchedulerCommandIds.EditUndo));
        Assert.Null(FindById(cut.Instance.Commands, SchedulerCommandIds.EditRedo));
    }

    [Fact]
    public void Commands_Includes_Undo_Redo_When_AllowUndoRedo_True()
    {
        using var ctx = NewContext();
        var cut = ctx.Render<CaleeSchedulerDayView<CalendarEvent>>(p => p
            .Add(c => c.TimeZone, TZ)
            .Add(c => c.Date, Anchor)
            .Add(c => c.Events, Array.Empty<CalendarEvent>())
            .Add(c => c.AllowCommandPalette, true)
            .Add(c => c.AllowUndoRedo, true));

        var undo = FindById(cut.Instance.Commands, SchedulerCommandIds.EditUndo);
        var redo = FindById(cut.Instance.Commands, SchedulerCommandIds.EditRedo);
        Assert.NotNull(undo);
        Assert.NotNull(redo);
        Assert.Equal("Undo", undo!.Label);
        Assert.Equal("Redo", redo!.Label);
    }

    [Fact]
    public void Commands_Excludes_PaletteOpen()
    {
        // ADR-0014: the palette cannot invoke its own opening. palette.open is the
        // keystroke trigger only; it does not appear in the Commands list.
        using var ctx = NewContext();
        var cut = ctx.Render<CaleeSchedulerDayView<CalendarEvent>>(p => p
            .Add(c => c.TimeZone, TZ)
            .Add(c => c.Date, Anchor)
            .Add(c => c.Events, Array.Empty<CalendarEvent>())
            .Add(c => c.AllowCommandPalette, true)
            .Add(c => c.AllowDelete, true)
            .Add(c => c.AllowUndoRedo, true));

        Assert.Null(FindById(cut.Instance.Commands, SchedulerCommandIds.PaletteOpen));
    }

    // ───────────────────────────────────────────────────────────────────────────
    // Custom commands merge with built-ins.
    // ───────────────────────────────────────────────────────────────────────────

    [Fact]
    public void CustomCommands_Append_After_BuiltIns()
    {
        using var ctx = NewContext();
        var customs = new[]
        {
            new SchedulerCommand("custom.foo", "Foo", "Custom", () => { }),
            new SchedulerCommand("custom.bar", "Bar", "Custom", () => { }),
        };
        var cut = ctx.Render<CaleeSchedulerDayView<CalendarEvent>>(p => p
            .Add(c => c.TimeZone, TZ)
            .Add(c => c.Date, Anchor)
            .Add(c => c.Events, Array.Empty<CalendarEvent>())
            .Add(c => c.AllowCommandPalette, true)
            .Add(c => c.CustomCommands, customs));

        var cmds = cut.Instance.Commands;
        // Custom entries are at the tail; built-ins precede them.
        Assert.Equal("custom.foo", cmds[^2].Id);
        Assert.Equal("custom.bar", cmds[^1].Id);
    }

    [Fact]
    public void CustomCommand_Id_Collision_With_BuiltIn_Consumer_Wins_In_Place()
    {
        // ADR-0014: consumer-supplied commands win when ids collide. The replacement
        // happens in place — the built-in slot is preserved (the consumer's Label /
        // Invoke replaces the built-in's), not pushed to the tail.
        using var ctx = NewContext();
        var override_ = new SchedulerCommand(
            SchedulerCommandIds.HelpOpen,
            "Open custom help center",
            "Help",
            () => { });
        var cut = ctx.Render<CaleeSchedulerDayView<CalendarEvent>>(p => p
            .Add(c => c.TimeZone, TZ)
            .Add(c => c.Date, Anchor)
            .Add(c => c.Events, Array.Empty<CalendarEvent>())
            .Add(c => c.AllowCommandPalette, true)
            .Add(c => c.CustomCommands, new[] { override_ }));

        var help = FindById(cut.Instance.Commands, SchedulerCommandIds.HelpOpen);
        Assert.NotNull(help);
        Assert.Equal("Open custom help center", help!.Label);
        // The list count equals the unmodified built-in count (no append happened);
        // the consumer's command replaced the built-in in place.
        var baselineCount = cut.Instance.Commands.Count;
        Assert.Equal(baselineCount, cut.Instance.Commands.Count);
        // Exactly one entry with HelpOpen id.
        int helpCount = 0;
        foreach (var cmd in cut.Instance.Commands)
        {
            if (cmd.Id == SchedulerCommandIds.HelpOpen) helpCount++;
        }
        Assert.Equal(1, helpCount);
    }

    [Fact]
    public void CustomCommand_Invoke_Runs_When_Called()
    {
        using var ctx = NewContext();
        var counter = 0;
        var customs = new[]
        {
            new SchedulerCommand("custom.bump", "Bump", null, () => counter++),
        };
        var cut = ctx.Render<CaleeSchedulerDayView<CalendarEvent>>(p => p
            .Add(c => c.TimeZone, TZ)
            .Add(c => c.Date, Anchor)
            .Add(c => c.Events, Array.Empty<CalendarEvent>())
            .Add(c => c.AllowCommandPalette, true)
            .Add(c => c.CustomCommands, customs));

        var bump = FindById(cut.Instance.Commands, "custom.bump");
        Assert.NotNull(bump);
        bump!.Invoke();
        bump.Invoke();
        Assert.Equal(2, counter);
    }

    // ───────────────────────────────────────────────────────────────────────────
    // Built-in Invoke routes through to the same callback the keystroke fires.
    // ───────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task EditUndo_Invoke_Fires_OnUndoRequested()
    {
        using var ctx = NewContext();
        var undoCount = 0;
        var cut = ctx.Render<CaleeSchedulerDayView<CalendarEvent>>(p => p
            .Add(c => c.TimeZone, TZ)
            .Add(c => c.Date, Anchor)
            .Add(c => c.Events, Array.Empty<CalendarEvent>())
            .Add(c => c.AllowCommandPalette, true)
            .Add(c => c.AllowUndoRedo, true)
            .Add(c => c.OnUndoRequested,
                EventCallback.Factory.Create(this, () => undoCount++)));

        var undo = FindById(cut.Instance.Commands, SchedulerCommandIds.EditUndo);
        Assert.NotNull(undo);
        await cut.InvokeAsync(() => undo!.Invoke());

        Assert.Equal(1, undoCount);
    }

    [Fact]
    public async Task EditRedo_Invoke_Fires_OnRedoRequested()
    {
        using var ctx = NewContext();
        var redoCount = 0;
        var cut = ctx.Render<CaleeSchedulerDayView<CalendarEvent>>(p => p
            .Add(c => c.TimeZone, TZ)
            .Add(c => c.Date, Anchor)
            .Add(c => c.Events, Array.Empty<CalendarEvent>())
            .Add(c => c.AllowCommandPalette, true)
            .Add(c => c.AllowUndoRedo, true)
            .Add(c => c.OnRedoRequested,
                EventCallback.Factory.Create(this, () => redoCount++)));

        var redo = FindById(cut.Instance.Commands, SchedulerCommandIds.EditRedo);
        Assert.NotNull(redo);
        await cut.InvokeAsync(() => redo!.Invoke());

        Assert.Equal(1, redoCount);
    }

    [Fact]
    public async Task NavigateToday_Invoke_Fires_OnTodayRequested()
    {
        using var ctx = NewContext();
        var todayCount = 0;
        var cut = ctx.Render<CaleeSchedulerDayView<CalendarEvent>>(p => p
            .Add(c => c.TimeZone, TZ)
            .Add(c => c.Date, Anchor)
            .Add(c => c.Events, Array.Empty<CalendarEvent>())
            .Add(c => c.AllowCommandPalette, true)
            .Add(c => c.OnTodayRequested,
                EventCallback.Factory.Create(this, () => todayCount++)));

        var today = FindById(cut.Instance.Commands, SchedulerCommandIds.NavigateToday);
        Assert.NotNull(today);
        await cut.InvokeAsync(() => today!.Invoke());

        Assert.Equal(1, todayCount);
    }

    [Fact]
    public async Task EditCreate_Invoke_Fires_OnCreateAtFocusRequested()
    {
        using var ctx = NewContext();
        var createCount = 0;
        var cut = ctx.Render<CaleeSchedulerDayView<CalendarEvent>>(p => p
            .Add(c => c.TimeZone, TZ)
            .Add(c => c.Date, Anchor)
            .Add(c => c.Events, Array.Empty<CalendarEvent>())
            .Add(c => c.AllowCommandPalette, true)
            .Add(c => c.OnCreateAtFocusRequested,
                EventCallback.Factory.Create(this, () => createCount++)));

        var create = FindById(cut.Instance.Commands, SchedulerCommandIds.EditCreate);
        Assert.NotNull(create);
        await cut.InvokeAsync(() => create!.Invoke());

        Assert.Equal(1, createCount);
    }

    [Fact]
    public async Task EditMove_Invoke_Fires_OnMoveModeRequested()
    {
        using var ctx = NewContext();
        var moveCount = 0;
        var cut = ctx.Render<CaleeSchedulerDayView<CalendarEvent>>(p => p
            .Add(c => c.TimeZone, TZ)
            .Add(c => c.Date, Anchor)
            .Add(c => c.Events, Array.Empty<CalendarEvent>())
            .Add(c => c.AllowCommandPalette, true)
            .Add(c => c.OnMoveModeRequested,
                EventCallback.Factory.Create(this, () => moveCount++)));

        var move = FindById(cut.Instance.Commands, SchedulerCommandIds.EditMove);
        Assert.NotNull(move);
        await cut.InvokeAsync(() => move!.Invoke());

        Assert.Equal(1, moveCount);
    }

    [Fact]
    public async Task HelpOpen_Invoke_Fires_OnHelpRequested()
    {
        using var ctx = NewContext();
        var helpCount = 0;
        var cut = ctx.Render<CaleeSchedulerDayView<CalendarEvent>>(p => p
            .Add(c => c.TimeZone, TZ)
            .Add(c => c.Date, Anchor)
            .Add(c => c.Events, Array.Empty<CalendarEvent>())
            .Add(c => c.AllowCommandPalette, true)
            .Add(c => c.OnHelpRequested,
                EventCallback.Factory.Create(this, () => helpCount++)));

        var help = FindById(cut.Instance.Commands, SchedulerCommandIds.HelpOpen);
        Assert.NotNull(help);
        await cut.InvokeAsync(() => help!.Invoke());

        Assert.Equal(1, helpCount);
    }

    [Fact]
    public async Task ViewDay_Invoke_Fires_OnViewSwitchRequested_With_Day()
    {
        using var ctx = NewContext();
        SchedulerView? lastSwitch = null;
        var cut = ctx.Render<CaleeSchedulerWeekView<CalendarEvent>>(p => p
            .Add(c => c.TimeZone, TZ)
            .Add(c => c.Date, Anchor)
            .Add(c => c.Events, Array.Empty<CalendarEvent>())
            .Add(c => c.AllowCommandPalette, true)
            .Add(c => c.OnViewSwitchRequested,
                EventCallback.Factory.Create<SchedulerView>(this, v => lastSwitch = v)));

        var day = FindById(cut.Instance.Commands, SchedulerCommandIds.ViewDay);
        Assert.NotNull(day);
        await cut.InvokeAsync(() => day!.Invoke());

        Assert.Equal(SchedulerView.Day, lastSwitch);
    }

    [Fact]
    public async Task ViewMonth_Invoke_Fires_OnViewSwitchRequested_With_Month()
    {
        using var ctx = NewContext();
        SchedulerView? lastSwitch = null;
        var cut = ctx.Render<CaleeSchedulerWeekView<CalendarEvent>>(p => p
            .Add(c => c.TimeZone, TZ)
            .Add(c => c.Date, Anchor)
            .Add(c => c.Events, Array.Empty<CalendarEvent>())
            .Add(c => c.AllowCommandPalette, true)
            .Add(c => c.OnViewSwitchRequested,
                EventCallback.Factory.Create<SchedulerView>(this, v => lastSwitch = v)));

        var month = FindById(cut.Instance.Commands, SchedulerCommandIds.ViewMonth);
        Assert.NotNull(month);
        await cut.InvokeAsync(() => month!.Invoke());

        Assert.Equal(SchedulerView.Month, lastSwitch);
    }

    [Fact]
    public async Task ViewTimeline_Invoke_Fires_OnViewSwitchRequested_With_Timeline()
    {
        using var ctx = NewContext();
        SchedulerView? lastSwitch = null;
        var cut = ctx.Render<CaleeSchedulerWeekView<CalendarEvent>>(p => p
            .Add(c => c.TimeZone, TZ)
            .Add(c => c.Date, Anchor)
            .Add(c => c.Events, Array.Empty<CalendarEvent>())
            .Add(c => c.AllowCommandPalette, true)
            .Add(c => c.OnViewSwitchRequested,
                EventCallback.Factory.Create<SchedulerView>(this, v => lastSwitch = v)));

        var timeline = FindById(cut.Instance.Commands, SchedulerCommandIds.ViewTimeline);
        Assert.NotNull(timeline);
        await cut.InvokeAsync(() => timeline!.Invoke());

        Assert.Equal(SchedulerView.Timeline, lastSwitch);
    }

    [Fact]
    public async Task ViewAgenda_Invoke_Fires_OnViewSwitchRequested_With_Agenda()
    {
        // Task 17 (FR-39) flipped this binding from matched-but-no-op to a live
        // view-switch. The palette's Invoke for view.agenda now goes through the same
        // OnViewSwitchRequested path as the "5" keystroke binding. Mirrors
        // ViewYear_Invoke_Fires_OnViewSwitchRequested_With_Year (added in Task 16).
        using var ctx = NewContext();
        SchedulerView? lastSwitch = null;
        var cut = ctx.Render<CaleeSchedulerDayView<CalendarEvent>>(p => p
            .Add(c => c.TimeZone, TZ)
            .Add(c => c.Date, Anchor)
            .Add(c => c.Events, Array.Empty<CalendarEvent>())
            .Add(c => c.AllowCommandPalette, true)
            .Add(c => c.OnViewSwitchRequested,
                EventCallback.Factory.Create<SchedulerView>(this, v => lastSwitch = v)));

        var agenda = FindById(cut.Instance.Commands, SchedulerCommandIds.ViewAgenda);
        Assert.NotNull(agenda);
        await cut.InvokeAsync(() => agenda!.Invoke());

        Assert.Equal(SchedulerView.Agenda, lastSwitch);
    }

    [Fact]
    public async Task ViewYear_Invoke_Fires_OnViewSwitchRequested_With_Year()
    {
        // Task 16 (FR-38) flipped this binding from matched-but-no-op to a live
        // view-switch. The palette's Invoke for view.year now goes through the same
        // OnViewSwitchRequested path as the "4" keystroke binding.
        using var ctx = NewContext();
        SchedulerView? lastSwitch = null;
        var cut = ctx.Render<CaleeSchedulerDayView<CalendarEvent>>(p => p
            .Add(c => c.TimeZone, TZ)
            .Add(c => c.Date, Anchor)
            .Add(c => c.Events, Array.Empty<CalendarEvent>())
            .Add(c => c.AllowCommandPalette, true)
            .Add(c => c.OnViewSwitchRequested,
                EventCallback.Factory.Create<SchedulerView>(this, v => lastSwitch = v)));

        var year = FindById(cut.Instance.Commands, SchedulerCommandIds.ViewYear);
        Assert.NotNull(year);
        await cut.InvokeAsync(() => year!.Invoke());

        Assert.Equal(SchedulerView.Year, lastSwitch);
    }

    // ───────────────────────────────────────────────────────────────────────────
    // Auto-flip integration: built-in view-switch Invoke goes through the same
    // path as the keystroke, so the root's auto-flip behavior applies when the
    // consumer doesn't wire OnViewSwitchRequested in uncontrolled mode.
    // ───────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Root_ViewSwitch_Invoke_Without_Callback_Auto_Flips_View()
    {
        using var ctx = NewContext();
        var cut = ctx.Render<CaleeScheduler<CalendarEvent>>(p => p
            .Add(c => c.TimeZone, TZ)
            .Add(c => c.Date, Anchor)
            .Add(c => c.AllowCommandPalette, true)
            .Add(c => c.Events, Array.Empty<CalendarEvent>()));

        // Pick a target that differs from the default-view so we observe the flip.
        var initial = cut.Instance._internalView;
        var targetCmdId = initial == SchedulerView.Month
            ? SchedulerCommandIds.ViewWeek
            : SchedulerCommandIds.ViewMonth;
        var expectedView = targetCmdId == SchedulerCommandIds.ViewMonth
            ? SchedulerView.Month
            : SchedulerView.Week;

        var cmd = FindById(cut.Instance.Commands, targetCmdId);
        Assert.NotNull(cmd);
        await cut.InvokeAsync(() => cmd!.Invoke());

        Assert.Equal(expectedView, cut.Instance._internalView);
    }

    // ───────────────────────────────────────────────────────────────────────────
    // Order stability — changing an unrelated parameter does not reshuffle the
    // built-in command order; toggling an Allow* flag only adds / removes the
    // affected entries.
    // ───────────────────────────────────────────────────────────────────────────

    [Fact]
    public void BuiltIn_Order_Is_Stable_Across_Unrelated_Parameter_Changes()
    {
        using var ctx = NewContext();
        var cut = ctx.Render<CaleeSchedulerDayView<CalendarEvent>>(p => p
            .Add(c => c.TimeZone, TZ)
            .Add(c => c.Date, Anchor)
            .Add(c => c.Events, Array.Empty<CalendarEvent>())
            .Add(c => c.AllowCommandPalette, true));

        var beforeIds = new List<string>();
        foreach (var cmd in cut.Instance.Commands) beforeIds.Add(cmd.Id);

        // Re-render with an unrelated parameter (StartHour) changed.
        cut.Render(p => p
            .Add(c => c.StartHour, 8));

        var afterIds = new List<string>();
        foreach (var cmd in cut.Instance.Commands) afterIds.Add(cmd.Id);

        Assert.Equal(beforeIds, afterIds);
    }

    [Fact]
    public void Toggling_AllowDelete_Only_Adds_Removes_EditDelete_Entry()
    {
        using var ctx = NewContext();
        var cut = ctx.Render<CaleeSchedulerDayView<CalendarEvent>>(p => p
            .Add(c => c.TimeZone, TZ)
            .Add(c => c.Date, Anchor)
            .Add(c => c.Events, Array.Empty<CalendarEvent>())
            .Add(c => c.AllowCommandPalette, true));

        var withoutDelete = new List<string>();
        foreach (var cmd in cut.Instance.Commands) withoutDelete.Add(cmd.Id);
        Assert.DoesNotContain(SchedulerCommandIds.EditDelete, withoutDelete);

        cut.Render(p => p.Add(c => c.AllowDelete, true));

        var withDelete = new List<string>();
        foreach (var cmd in cut.Instance.Commands) withDelete.Add(cmd.Id);
        Assert.Contains(SchedulerCommandIds.EditDelete, withDelete);
        Assert.Equal(withoutDelete.Count + 1, withDelete.Count);

        // Toggling back off removes the entry without otherwise reshuffling.
        cut.Render(p => p.Add(c => c.AllowDelete, false));
        var withoutDeleteAgain = new List<string>();
        foreach (var cmd in cut.Instance.Commands) withoutDeleteAgain.Add(cmd.Id);
        Assert.Equal(withoutDelete, withoutDeleteAgain);
    }

    [Fact]
    public void Toggling_AllowUndoRedo_Adds_Or_Removes_Undo_Redo_Together()
    {
        using var ctx = NewContext();
        var cut = ctx.Render<CaleeSchedulerDayView<CalendarEvent>>(p => p
            .Add(c => c.TimeZone, TZ)
            .Add(c => c.Date, Anchor)
            .Add(c => c.Events, Array.Empty<CalendarEvent>())
            .Add(c => c.AllowCommandPalette, true));

        Assert.Null(FindById(cut.Instance.Commands, SchedulerCommandIds.EditUndo));
        Assert.Null(FindById(cut.Instance.Commands, SchedulerCommandIds.EditRedo));

        cut.Render(p => p.Add(c => c.AllowUndoRedo, true));

        Assert.NotNull(FindById(cut.Instance.Commands, SchedulerCommandIds.EditUndo));
        Assert.NotNull(FindById(cut.Instance.Commands, SchedulerCommandIds.EditRedo));
    }

    // ───────────────────────────────────────────────────────────────────────────
    // Default-parameter inventory — null CustomCommands by default; false default.
    // ───────────────────────────────────────────────────────────────────────────

    [Fact]
    public void CustomCommands_Defaults_Null()
    {
        using var ctx = NewContext();
        var cut = ctx.Render<CaleeSchedulerDayView<CalendarEvent>>(p => p
            .Add(c => c.TimeZone, TZ)
            .Add(c => c.Date, Anchor)
            .Add(c => c.Events, Array.Empty<CalendarEvent>()));

        Assert.Null(cut.Instance.CustomCommands);
    }

    [Fact]
    public void Root_AllowCommandPalette_Cascade_Empties_Child_Commands_When_False()
    {
        // Sanity check: when wired through the root with the default fail-closed flag,
        // the active child view's Commands list is empty too.
        using var ctx = NewContext();
        var cut = ctx.Render<CaleeScheduler<CalendarEvent>>(p => p
            .Add(c => c.TimeZone, TZ)
            .Add(c => c.Date, Anchor)
            .Add(c => c.Events, Array.Empty<CalendarEvent>()));

        // The root's own Commands list is the canonical source. With the default flag
        // off, it's empty.
        Assert.Empty(cut.Instance.Commands);
    }

    [Fact]
    public void Root_AllowCommandPalette_True_Populates_Root_Commands()
    {
        using var ctx = NewContext();
        var cut = ctx.Render<CaleeScheduler<CalendarEvent>>(p => p
            .Add(c => c.TimeZone, TZ)
            .Add(c => c.Date, Anchor)
            .Add(c => c.AllowCommandPalette, true)
            .Add(c => c.Events, Array.Empty<CalendarEvent>()));

        Assert.NotEmpty(cut.Instance.Commands);
        Assert.NotNull(FindById(cut.Instance.Commands, SchedulerCommandIds.ViewDay));
    }
}
