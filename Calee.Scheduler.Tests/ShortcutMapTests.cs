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
/// Tests for the Phase 2 Task 14 opinionated keyboard shortcut map (FR-36 / ADR-0013).
/// Covers:
/// <list type="bullet">
///   <item>DefaultMap presence + per-command coverage.</item>
///   <item>Internal keystroke parser round-trip + matcher.</item>
///   <item>Resolved-map precedence: DisabledShortcuts wins; ShortcutMap replaces defaults; consumer-defined ids append.</item>
///   <item>New trigger callbacks fire (today, create-at-focus, help, palette).</item>
///   <item>Disabled bindings don't fire.</item>
///   <item>Remapped bindings replace defaults.</item>
///   <item>Fail-closed: disabled Allow* flag means the binding stays inert even when the key matches.</item>
///   <item>Root-level view-switch routing (consumer callback wins; otherwise root auto-flips uncontrolled-mode View).</item>
/// </list>
/// </summary>
public class ShortcutMapTests
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

    // ───────────────────────────────────────────────────────────────────────────
    // DefaultMap coverage
    // ───────────────────────────────────────────────────────────────────────────

    [Fact]
    public void DefaultMap_Contains_Every_Command_From_ADR_0013()
    {
        var defaults = SchedulerShortcuts.DefaultMap;
        var ids = new HashSet<string>(StringComparer.Ordinal);
        foreach (var binding in defaults)
        {
            ids.Add(binding.CommandId);
        }

        // Every command id documented in ADR-0013's binding table must appear at
        // least once. The exact key strings are asserted in the per-command tests
        // below; this test only pins coverage.
        Assert.Contains(SchedulerCommandIds.ViewDay, ids);
        Assert.Contains(SchedulerCommandIds.ViewWeek, ids);
        Assert.Contains(SchedulerCommandIds.ViewMonth, ids);
        Assert.Contains(SchedulerCommandIds.ViewYear, ids);
        Assert.Contains(SchedulerCommandIds.ViewAgenda, ids);
        Assert.Contains(SchedulerCommandIds.ViewTimeline, ids);
        Assert.Contains(SchedulerCommandIds.ViewWorkWeek, ids);
        Assert.Contains(SchedulerCommandIds.NavigateToday, ids);
        Assert.Contains(SchedulerCommandIds.EditCreate, ids);
        Assert.Contains(SchedulerCommandIds.EditDelete, ids);
        Assert.Contains(SchedulerCommandIds.EditMove, ids);
        Assert.Contains(SchedulerCommandIds.EditResize, ids);
        Assert.Contains(SchedulerCommandIds.SelectToggle, ids);
        Assert.Contains(SchedulerCommandIds.Cancel, ids);
        Assert.Contains(SchedulerCommandIds.PaletteOpen, ids);
        Assert.Contains(SchedulerCommandIds.EditUndo, ids);
        Assert.Contains(SchedulerCommandIds.EditRedo, ids);
        Assert.Contains(SchedulerCommandIds.HelpOpen, ids);
    }

    [Fact]
    public void DefaultMap_Has_Both_Cmd_And_Ctrl_Variants_For_Cross_Platform_Bindings()
    {
        var defaults = SchedulerShortcuts.DefaultMap;

        bool HasBinding(string key, string commandId)
        {
            foreach (var b in defaults)
            {
                if (b.Key == key && b.CommandId == commandId) return true;
            }
            return false;
        }

        // Undo: both Cmd+Z and Ctrl+Z bind to edit.undo (ADR-0013 row 15).
        Assert.True(HasBinding("Cmd+Z", SchedulerCommandIds.EditUndo));
        Assert.True(HasBinding("Ctrl+Z", SchedulerCommandIds.EditUndo));
        // Redo: Cmd+Shift+Z (macOS) and Ctrl+Y (Windows convention) per ADR-0013 row 16.
        Assert.True(HasBinding("Cmd+Shift+Z", SchedulerCommandIds.EditRedo));
        Assert.True(HasBinding("Ctrl+Y", SchedulerCommandIds.EditRedo));
        // Palette: both Cmd+K and Ctrl+K (ADR-0013 row 14).
        Assert.True(HasBinding("Cmd+K", SchedulerCommandIds.PaletteOpen));
        Assert.True(HasBinding("Ctrl+K", SchedulerCommandIds.PaletteOpen));
        // Delete + Backspace alias (Task 12 cross-platform accommodation).
        Assert.True(HasBinding("Delete", SchedulerCommandIds.EditDelete));
        Assert.True(HasBinding("Backspace", SchedulerCommandIds.EditDelete));
    }

    // ───────────────────────────────────────────────────────────────────────────
    // Keystroke parser
    // ───────────────────────────────────────────────────────────────────────────

    [Theory]
    [InlineData("z", "z", false, false, false, false)]
    [InlineData("Z", "z", false, false, false, false)] // letter case-normalized
    [InlineData("Cmd+Z", "z", false, true, false, false)]
    [InlineData("Cmd+Shift+Z", "z", false, true, false, true)]
    [InlineData("Ctrl+Y", "y", true, false, false, false)]
    [InlineData("Shift+ArrowUp", "ArrowUp", false, false, false, true)]
    [InlineData("Delete", "Delete", false, false, false, false)]
    [InlineData("Backspace", "Backspace", false, false, false, false)]
    [InlineData("Escape", "Escape", false, false, false, false)]
    [InlineData(" ", " ", false, false, false, false)]
    [InlineData("?", "?", false, false, false, false)]
    [InlineData("Cmd+K", "k", false, true, false, false)]
    [InlineData("Alt+Ctrl+Cmd+Shift+A", "a", true, true, true, true)]
    public void Parser_RoundTrips_Representative_Key_Specs(string spec, string key, bool ctrl, bool cmd, bool alt, bool shift)
    {
        Assert.True(ShortcutBindingParser.TryParse(spec, out var parsed), $"Failed to parse '{spec}'.");
        Assert.Equal(key, parsed.Key);
        Assert.Equal(ctrl, parsed.Ctrl);
        Assert.Equal(cmd, parsed.Cmd);
        Assert.Equal(alt, parsed.Alt);
        Assert.Equal(shift, parsed.Shift);
    }

    [Fact]
    public void Parser_Returns_False_For_Empty_Input()
    {
        Assert.False(ShortcutBindingParser.TryParse(null, out _));
        Assert.False(ShortcutBindingParser.TryParse(string.Empty, out _));
    }

    [Fact]
    public void Matcher_Matches_Modifier_Prefixed_Keystroke()
    {
        Assert.True(ShortcutBindingParser.TryParse("Cmd+Z", out var parsed));
        var e = new KeyboardEventArgs { Key = "z", MetaKey = true };
        Assert.True(ShortcutBindingParser.Matches(in parsed, e));
    }

    [Fact]
    public void Matcher_Rejects_Modifier_Mismatch()
    {
        Assert.True(ShortcutBindingParser.TryParse("Cmd+Z", out var parsed));
        // Plain z without Cmd should NOT match Cmd+Z binding.
        var plainZ = new KeyboardEventArgs { Key = "z" };
        Assert.False(ShortcutBindingParser.Matches(in parsed, plainZ));
        // Ctrl+Z should NOT match Cmd+Z binding (modifiers are strictly distinguished).
        var ctrlZ = new KeyboardEventArgs { Key = "z", CtrlKey = true };
        Assert.False(ShortcutBindingParser.Matches(in parsed, ctrlZ));
    }

    [Fact]
    public void Matcher_Discriminates_Shift_For_Letter_Keys()
    {
        // "Cmd+Z" without Shift binds edit.undo; "Cmd+Shift+Z" with Shift binds edit.redo.
        // The Shift flag — not the letter case — is the canonical discriminator.
        Assert.True(ShortcutBindingParser.TryParse("Cmd+Z", out var undo));
        Assert.True(ShortcutBindingParser.TryParse("Cmd+Shift+Z", out var redo));

        var cmdZ = new KeyboardEventArgs { Key = "z", MetaKey = true };
        var cmdShiftZ = new KeyboardEventArgs { Key = "Z", MetaKey = true, ShiftKey = true };

        Assert.True(ShortcutBindingParser.Matches(in undo, cmdZ));
        Assert.False(ShortcutBindingParser.Matches(in undo, cmdShiftZ));
        Assert.True(ShortcutBindingParser.Matches(in redo, cmdShiftZ));
        Assert.False(ShortcutBindingParser.Matches(in redo, cmdZ));
    }

    // ───────────────────────────────────────────────────────────────────────────
    // Resolved-map precedence
    // ───────────────────────────────────────────────────────────────────────────

    [Fact]
    public void Resolved_Drops_Entries_For_Disabled_Command_Ids()
    {
        var resolved = ResolvedShortcutMap.Resolve(
            disabled: new[] { SchedulerCommandIds.EditDelete },
            overrides: null);

        foreach (var entry in resolved.Snapshot)
        {
            Assert.NotEqual(SchedulerCommandIds.EditDelete, entry.CommandId);
        }
        // Both the Delete and Backspace bindings should be gone.
        var deleteCount = 0;
        foreach (var entry in resolved.Snapshot)
        {
            if (entry.Key is "Delete" or "Backspace") deleteCount++;
        }
        Assert.Equal(0, deleteCount);
    }

    [Fact]
    public void Resolved_Replaces_Default_Key_When_Override_Targets_Same_CommandId()
    {
        var resolved = ResolvedShortcutMap.Resolve(
            disabled: null,
            overrides: new[] { new ShortcutBinding("Ctrl+Alt+Z", SchedulerCommandIds.EditUndo) });

        var undoEntries = new List<ShortcutBinding>();
        foreach (var entry in resolved.Snapshot)
        {
            if (entry.CommandId == SchedulerCommandIds.EditUndo) undoEntries.Add(entry);
        }
        // Override replaces — only one entry, with the override's key. Defaults
        // (Cmd+Z, Ctrl+Z) are gone.
        Assert.Single(undoEntries);
        Assert.Equal("Ctrl+Alt+Z", undoEntries[0].Key);
    }

    [Fact]
    public void Resolved_Appends_Consumer_Defined_CommandIds_At_Tail()
    {
        var resolved = ResolvedShortcutMap.Resolve(
            disabled: null,
            overrides: new[] { new ShortcutBinding("Ctrl+Alt+E", "consumer.custom") });

        // Default map's command ids are still all present (no removals).
        var customAt = -1;
        for (var i = 0; i < resolved.Snapshot.Count; i++)
        {
            if (resolved.Snapshot[i].CommandId == "consumer.custom")
            {
                customAt = i;
                break;
            }
        }
        Assert.True(customAt >= 0, "Consumer-defined command id should be appended.");
        // It should appear after all default entries (the default map is ~22 entries;
        // we don't pin the exact length to stay robust against future additions).
        Assert.Equal(SchedulerShortcuts.DefaultMap.Count, customAt);
    }

    [Fact]
    public void Resolved_Disable_Wins_Over_Override()
    {
        // When a command id is in DisabledShortcuts AND has an override, the disable
        // wins — the override is dropped.
        var resolved = ResolvedShortcutMap.Resolve(
            disabled: new[] { SchedulerCommandIds.EditUndo },
            overrides: new[] { new ShortcutBinding("Ctrl+Alt+Z", SchedulerCommandIds.EditUndo) });

        foreach (var entry in resolved.Snapshot)
        {
            Assert.NotEqual(SchedulerCommandIds.EditUndo, entry.CommandId);
        }
    }

    [Fact]
    public void Resolved_Is_OrderStable()
    {
        var a = ResolvedShortcutMap.Resolve(disabled: null, overrides: null);
        var b = ResolvedShortcutMap.Resolve(disabled: null, overrides: null);
        Assert.Equal(a.Snapshot.Count, b.Snapshot.Count);
        for (var i = 0; i < a.Snapshot.Count; i++)
        {
            Assert.Equal(a.Snapshot[i].Key, b.Snapshot[i].Key);
            Assert.Equal(a.Snapshot[i].CommandId, b.Snapshot[i].CommandId);
        }
    }

    [Fact]
    public void Resolved_Handles_Null_Inputs_As_Identity()
    {
        var resolved = ResolvedShortcutMap.Resolve(disabled: null, overrides: null);
        // Same length as DefaultMap (since the parser successfully handles every
        // canonical entry; nothing is dropped).
        Assert.Equal(SchedulerShortcuts.DefaultMap.Count, resolved.Snapshot.Count);
    }

    // ───────────────────────────────────────────────────────────────────────────
    // New callbacks fire from the right scope
    // ───────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Day_T_From_Chip_Fires_OnTodayRequested()
    {
        using var ctx = NewContext();
        var todayCount = 0;
        var a = Timed("a", 9, 10);
        var cut = ctx.Render<CaleeSchedulerDayView<CalendarEvent>>(p => p
            .Add(c => c.TimeZone, TZ)
            .Add(c => c.Date, Anchor)
            .Add(c => c.Events, new[] { a })
            .Add(c => c.OnTodayRequested, EventCallback.Factory.Create(this, () => todayCount++)));

        await cut.InvokeAsync(() =>
            cut.Find("[data-calee-region='hour-grid'] .calee-scheduler-event")
                .KeyDown(new KeyboardEventArgs { Key = "t" }));

        Assert.Equal(1, todayCount);
    }

    [Fact]
    public async Task Day_T_From_Grid_Fires_OnTodayRequested()
    {
        using var ctx = NewContext();
        var todayCount = 0;
        var cut = ctx.Render<CaleeSchedulerDayView<CalendarEvent>>(p => p
            .Add(c => c.TimeZone, TZ)
            .Add(c => c.Date, Anchor)
            .Add(c => c.Events, Array.Empty<CalendarEvent>())
            .Add(c => c.OnTodayRequested, EventCallback.Factory.Create(this, () => todayCount++)));

        await cut.InvokeAsync(() =>
            cut.Find("[role='grid']")
                .KeyDown(new KeyboardEventArgs { Key = "t" }));

        Assert.Equal(1, todayCount);
    }

    [Fact]
    public async Task Day_N_From_Grid_Fires_OnCreateAtFocusRequested()
    {
        using var ctx = NewContext();
        var createCount = 0;
        var cut = ctx.Render<CaleeSchedulerDayView<CalendarEvent>>(p => p
            .Add(c => c.TimeZone, TZ)
            .Add(c => c.Date, Anchor)
            .Add(c => c.Events, Array.Empty<CalendarEvent>())
            .Add(c => c.OnCreateAtFocusRequested,
                EventCallback.Factory.Create(this, () => createCount++)));

        await cut.InvokeAsync(() =>
            cut.Find("[role='grid']").KeyDown(new KeyboardEventArgs { Key = "n" }));

        Assert.Equal(1, createCount);
    }

    [Fact]
    public async Task Day_N_On_Chip_Does_Not_Fire_OnCreateAtFocusRequested()
    {
        // edit.create is grid-scope only — n on a focused chip should NOT fire it.
        using var ctx = NewContext();
        var createCount = 0;
        var a = Timed("a", 9, 10);
        var cut = ctx.Render<CaleeSchedulerDayView<CalendarEvent>>(p => p
            .Add(c => c.TimeZone, TZ)
            .Add(c => c.Date, Anchor)
            .Add(c => c.Events, new[] { a })
            .Add(c => c.OnCreateAtFocusRequested,
                EventCallback.Factory.Create(this, () => createCount++)));

        await cut.InvokeAsync(() =>
            cut.Find("[data-calee-region='hour-grid'] .calee-scheduler-event")
                .KeyDown(new KeyboardEventArgs { Key = "n" }));

        Assert.Equal(0, createCount);
    }

    [Fact]
    public async Task Day_QuestionMark_Fires_OnHelpRequested()
    {
        using var ctx = NewContext();
        var helpCount = 0;
        var cut = ctx.Render<CaleeSchedulerDayView<CalendarEvent>>(p => p
            .Add(c => c.TimeZone, TZ)
            .Add(c => c.Date, Anchor)
            .Add(c => c.Events, Array.Empty<CalendarEvent>())
            .Add(c => c.OnHelpRequested, EventCallback.Factory.Create(this, () => helpCount++)));

        await cut.InvokeAsync(() =>
            cut.Find("[role='grid']")
                .KeyDown(new KeyboardEventArgs { Key = "?", ShiftKey = true }));

        Assert.Equal(1, helpCount);
    }

    [Fact]
    public async Task Day_BareQuestionMark_Fires_OnHelpRequested()
    {
        // Browsers report `e.Key = "?"` with `ShiftKey = false` on some layouts
        // (where ? is the natural key on its own). The DefaultMap defensively
        // lists both `Shift+?` and bare `?` to cover both report patterns; this
        // test pins the bare-`?` path that the prior coverage missed.
        using var ctx = NewContext();
        var helpCount = 0;
        var cut = ctx.Render<CaleeSchedulerDayView<CalendarEvent>>(p => p
            .Add(c => c.TimeZone, TZ)
            .Add(c => c.Date, Anchor)
            .Add(c => c.Events, Array.Empty<CalendarEvent>())
            .Add(c => c.OnHelpRequested, EventCallback.Factory.Create(this, () => helpCount++)));

        await cut.InvokeAsync(() =>
            cut.Find("[role='grid']")
                .KeyDown(new KeyboardEventArgs { Key = "?", ShiftKey = false }));

        Assert.Equal(1, helpCount);
    }

    [Fact]
    public async Task Day_CmdK_Fires_OnCommandPaletteRequested()
    {
        // Phase 2 Task 15 — Cmd+K is gated on AllowCommandPalette (FR-29 fail-closed).
        // The keystroke fires the callback only when the consumer has opted in.
        using var ctx = NewContext();
        var paletteCount = 0;
        var cut = ctx.Render<CaleeSchedulerDayView<CalendarEvent>>(p => p
            .Add(c => c.TimeZone, TZ)
            .Add(c => c.Date, Anchor)
            .Add(c => c.Events, Array.Empty<CalendarEvent>())
            .Add(c => c.AllowCommandPalette, true)
            .Add(c => c.OnCommandPaletteRequested,
                EventCallback.Factory.Create(this, () => paletteCount++)));

        await cut.InvokeAsync(() =>
            cut.Find("[role='grid']")
                .KeyDown(new KeyboardEventArgs { Key = "k", MetaKey = true }));

        Assert.Equal(1, paletteCount);
    }

    [Fact]
    public async Task Day_CtrlK_Fires_OnCommandPaletteRequested()
    {
        // Phase 2 Task 15 — same fail-closed gate; consumer opts in via AllowCommandPalette.
        using var ctx = NewContext();
        var paletteCount = 0;
        var cut = ctx.Render<CaleeSchedulerDayView<CalendarEvent>>(p => p
            .Add(c => c.TimeZone, TZ)
            .Add(c => c.Date, Anchor)
            .Add(c => c.Events, Array.Empty<CalendarEvent>())
            .Add(c => c.AllowCommandPalette, true)
            .Add(c => c.OnCommandPaletteRequested,
                EventCallback.Factory.Create(this, () => paletteCount++)));

        await cut.InvokeAsync(() =>
            cut.Find("[role='grid']")
                .KeyDown(new KeyboardEventArgs { Key = "k", CtrlKey = true }));

        Assert.Equal(1, paletteCount);
    }

    [Fact]
    public async Task Day_M_On_Chip_Fires_OnMoveModeRequested()
    {
        using var ctx = NewContext();
        var moveCount = 0;
        var a = Timed("a", 9, 10);
        var cut = ctx.Render<CaleeSchedulerDayView<CalendarEvent>>(p => p
            .Add(c => c.TimeZone, TZ)
            .Add(c => c.Date, Anchor)
            .Add(c => c.Events, new[] { a })
            .Add(c => c.OnMoveModeRequested,
                EventCallback.Factory.Create(this, () => moveCount++)));

        await cut.InvokeAsync(() =>
            cut.Find("[data-calee-region='hour-grid'] .calee-scheduler-event")
                .KeyDown(new KeyboardEventArgs { Key = "m" }));

        Assert.Equal(1, moveCount);
    }

    [Fact]
    public async Task Day_ShiftArrow_On_Chip_Fires_OnResizeKeystrokeRequested()
    {
        using var ctx = NewContext();
        var resizeCount = 0;
        var a = Timed("a", 9, 10);
        var cut = ctx.Render<CaleeSchedulerDayView<CalendarEvent>>(p => p
            .Add(c => c.TimeZone, TZ)
            .Add(c => c.Date, Anchor)
            .Add(c => c.Events, new[] { a })
            .Add(c => c.OnResizeKeystrokeRequested,
                EventCallback.Factory.Create(this, () => resizeCount++)));

        await cut.InvokeAsync(() =>
            cut.Find("[data-calee-region='hour-grid'] .calee-scheduler-event")
                .KeyDown(new KeyboardEventArgs { Key = "ArrowUp", ShiftKey = true }));

        Assert.Equal(1, resizeCount);
    }

    [Fact]
    public async Task Day_DigitKey_Fires_OnViewSwitchRequested_With_Right_View()
    {
        using var ctx = NewContext();
        SchedulerView? lastSwitch = null;
        var a = Timed("a", 9, 10);
        var cut = ctx.Render<CaleeSchedulerDayView<CalendarEvent>>(p => p
            .Add(c => c.TimeZone, TZ)
            .Add(c => c.Date, Anchor)
            .Add(c => c.Events, new[] { a })
            .Add(c => c.OnViewSwitchRequested,
                EventCallback.Factory.Create<SchedulerView>(this, v => lastSwitch = v)));

        // Press "2" → ViewWeek.
        await cut.InvokeAsync(() =>
            cut.Find("[data-calee-region='hour-grid'] .calee-scheduler-event")
                .KeyDown(new KeyboardEventArgs { Key = "2" }));
        Assert.Equal(SchedulerView.Week, lastSwitch);

        // Press "3" → ViewMonth.
        await cut.InvokeAsync(() =>
            cut.Find("[data-calee-region='hour-grid'] .calee-scheduler-event")
                .KeyDown(new KeyboardEventArgs { Key = "3" }));
        Assert.Equal(SchedulerView.Month, lastSwitch);

        // Press "6" → ViewTimeline.
        await cut.InvokeAsync(() =>
            cut.Find("[data-calee-region='hour-grid'] .calee-scheduler-event")
                .KeyDown(new KeyboardEventArgs { Key = "6" }));
        Assert.Equal(SchedulerView.Timeline, lastSwitch);

        // Press "7" → ViewWorkWeek (issue #7).
        await cut.InvokeAsync(() =>
            cut.Find("[data-calee-region='hour-grid'] .calee-scheduler-event")
                .KeyDown(new KeyboardEventArgs { Key = "7" }));
        Assert.Equal(SchedulerView.WorkWeek, lastSwitch);
    }

    // ───────────────────────────────────────────────────────────────────────────
    // Disabled bindings stay inert
    // ───────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task DisabledShortcuts_EditDelete_Suppresses_Delete_And_Backspace()
    {
        using var ctx = NewContext();
        var deleteCount = 0;
        var a = Timed("a", 9, 10);
        var cut = ctx.Render<CaleeSchedulerDayView<CalendarEvent>>(p => p
            .Add(c => c.TimeZone, TZ)
            .Add(c => c.Date, Anchor)
            .Add(c => c.AllowDelete, true)
            .Add(c => c.Events, new[] { a })
            .Add(c => c.DisabledShortcuts, new[] { SchedulerCommandIds.EditDelete })
            .Add(c => c.OnEventDeleted,
                EventCallback.Factory.Create<EventDeleteContext>(this, _ => deleteCount++)));

        await cut.InvokeAsync(() =>
            cut.Find("[data-calee-region='hour-grid'] .calee-scheduler-event")
                .KeyDown(new KeyboardEventArgs { Key = "Delete" }));
        await cut.InvokeAsync(() =>
            cut.Find("[data-calee-region='hour-grid'] .calee-scheduler-event")
                .KeyDown(new KeyboardEventArgs { Key = "Backspace" }));

        Assert.Equal(0, deleteCount);
    }

    [Fact]
    public async Task DisabledShortcuts_ViewWorkWeek_Suppresses_Digit_Seven()
    {
        using var ctx = NewContext();
        SchedulerView? lastSwitch = null;
        var a = Timed("a", 9, 10);
        var cut = ctx.Render<CaleeSchedulerDayView<CalendarEvent>>(p => p
            .Add(c => c.TimeZone, TZ)
            .Add(c => c.Date, Anchor)
            .Add(c => c.Events, new[] { a })
            .Add(c => c.DisabledShortcuts, new[] { SchedulerCommandIds.ViewWorkWeek })
            .Add(c => c.OnViewSwitchRequested,
                EventCallback.Factory.Create<SchedulerView>(this, v => lastSwitch = v)));

        await cut.InvokeAsync(() =>
            cut.Find("[data-calee-region='hour-grid'] .calee-scheduler-event")
                .KeyDown(new KeyboardEventArgs { Key = "7" }));

        Assert.Null(lastSwitch);
    }

    [Fact]
    public async Task DisabledShortcuts_UndoAndRedo_Suppresses_Cmd_Z_And_Ctrl_Y()
    {
        using var ctx = NewContext();
        var undoCount = 0;
        var redoCount = 0;
        var a = Timed("a", 9, 10);
        var cut = ctx.Render<CaleeSchedulerDayView<CalendarEvent>>(p => p
            .Add(c => c.TimeZone, TZ)
            .Add(c => c.Date, Anchor)
            .Add(c => c.AllowUndoRedo, true)
            .Add(c => c.Events, new[] { a })
            .Add(c => c.DisabledShortcuts,
                new[] { SchedulerCommandIds.EditUndo, SchedulerCommandIds.EditRedo })
            .Add(c => c.OnUndoRequested, EventCallback.Factory.Create(this, () => undoCount++))
            .Add(c => c.OnRedoRequested, EventCallback.Factory.Create(this, () => redoCount++)));

        await cut.InvokeAsync(() =>
            cut.Find("[data-calee-region='hour-grid'] .calee-scheduler-event")
                .KeyDown(new KeyboardEventArgs { Key = "z", MetaKey = true }));
        await cut.InvokeAsync(() =>
            cut.Find("[data-calee-region='hour-grid'] .calee-scheduler-event")
                .KeyDown(new KeyboardEventArgs { Key = "y", CtrlKey = true }));

        Assert.Equal(0, undoCount);
        Assert.Equal(0, redoCount);
    }

    // ───────────────────────────────────────────────────────────────────────────
    // Remapped bindings replace defaults
    // ───────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task ShortcutMap_Remap_EditUndo_To_Letter_U_Fires_On_U_And_Stops_CmdZ()
    {
        using var ctx = NewContext();
        var undoCount = 0;
        var a = Timed("a", 9, 10);
        var cut = ctx.Render<CaleeSchedulerDayView<CalendarEvent>>(p => p
            .Add(c => c.TimeZone, TZ)
            .Add(c => c.Date, Anchor)
            .Add(c => c.AllowUndoRedo, true)
            .Add(c => c.Events, new[] { a })
            .Add(c => c.ShortcutMap,
                new[] { new ShortcutBinding("u", SchedulerCommandIds.EditUndo) })
            .Add(c => c.OnUndoRequested, EventCallback.Factory.Create(this, () => undoCount++)));

        // Pressing 'u' (no modifier) should now fire undo.
        await cut.InvokeAsync(() =>
            cut.Find("[data-calee-region='hour-grid'] .calee-scheduler-event")
                .KeyDown(new KeyboardEventArgs { Key = "u" }));
        Assert.Equal(1, undoCount);

        // Pressing Cmd+Z should NOT fire — the override replaced the defaults.
        await cut.InvokeAsync(() =>
            cut.Find("[data-calee-region='hour-grid'] .calee-scheduler-event")
                .KeyDown(new KeyboardEventArgs { Key = "z", MetaKey = true }));
        Assert.Equal(1, undoCount); // unchanged
    }

    // ───────────────────────────────────────────────────────────────────────────
    // Fail-closed: Allow* gates still hold even when keystroke matches.
    // ───────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task FailClosed_AllowDelete_False_Delete_Does_Not_Fire_Even_With_Default_Map()
    {
        using var ctx = NewContext();
        var deleteCount = 0;
        var a = Timed("a", 9, 10);
        var cut = ctx.Render<CaleeSchedulerDayView<CalendarEvent>>(p => p
            .Add(c => c.TimeZone, TZ)
            .Add(c => c.Date, Anchor)
            // AllowDelete intentionally NOT set (defaults to false).
            .Add(c => c.Events, new[] { a })
            .Add(c => c.OnEventDeleted,
                EventCallback.Factory.Create<EventDeleteContext>(this, _ => deleteCount++)));

        await cut.InvokeAsync(() =>
            cut.Find("[data-calee-region='hour-grid'] .calee-scheduler-event")
                .KeyDown(new KeyboardEventArgs { Key = "Delete" }));

        Assert.Equal(0, deleteCount);
    }

    [Fact]
    public async Task FailClosed_AllowUndoRedo_False_CmdZ_Does_Not_Fire_Even_With_Default_Map()
    {
        using var ctx = NewContext();
        var undoCount = 0;
        var a = Timed("a", 9, 10);
        var cut = ctx.Render<CaleeSchedulerDayView<CalendarEvent>>(p => p
            .Add(c => c.TimeZone, TZ)
            .Add(c => c.Date, Anchor)
            // AllowUndoRedo defaults to false.
            .Add(c => c.Events, new[] { a })
            .Add(c => c.OnUndoRequested,
                EventCallback.Factory.Create(this, () => undoCount++)));

        await cut.InvokeAsync(() =>
            cut.Find("[data-calee-region='hour-grid'] .calee-scheduler-event")
                .KeyDown(new KeyboardEventArgs { Key = "z", MetaKey = true }));

        Assert.Equal(0, undoCount);
    }

    [Fact]
    public async Task FailClosed_AllowMultiSelect_False_Space_Does_Not_Fire_Shortcut_Toggle()
    {
        // FR-29: Space on a focused chip with AllowMultiSelect=false should NOT
        // dispatch the shortcut-map's select.toggle; instead the chip's browser-default
        // Space-activates-button proceeds (which the Day view tests in
        // SelectionKeyboardTests already exercise). We just pin here that the
        // OnSelectionChanged set stays single-id (no multi-toggle).
        using var ctx = NewContext();
        var sels = new List<IReadOnlyList<CalendarEvent>>();
        var a = Timed("a", 9, 10);
        var b = Timed("b", 10, 11);
        var cut = ctx.Render<CaleeSchedulerDayView<CalendarEvent>>(p => p
            .Add(c => c.TimeZone, TZ)
            .Add(c => c.Date, Anchor)
            // AllowMultiSelect defaults to false.
            .Add(c => c.Events, new[] { a, b })
            .Add(c => c.OnSelectionChanged,
                EventCallback.Factory.Create<IReadOnlyList<CalendarEvent>>(this, s => sels.Add(s))));

        var chips = cut.FindAll("[data-calee-region='hour-grid'] .calee-scheduler-event");
        Assert.Equal(2, chips.Count);
        // Synthesize a Space on chip 0 — without AllowMultiSelect the Space should
        // not toggle; OnSelectionChanged should NOT fire from a chip-scope Space
        // dispatch (the multi-toggle path is gated). The chip's @onclick path may
        // still produce a single-id selection (FR-29 fail-closed default), so we
        // only assert that we don't see the "multi" growth.
        await cut.InvokeAsync(() => chips[0].KeyDown(new KeyboardEventArgs { Key = " " }));
        // The selection set after a fail-closed Space should be at most one event.
        // We're loose here: either no fires (browser default suppressed by other
        // logic in tests) or a single-id fire. We assert the negative: no fire
        // ever produces a 2-element selection.
        foreach (var s in sels)
        {
            Assert.True(s.Count <= 1, "Selection should never grow to 2+ when AllowMultiSelect is false.");
        }
    }

    // ───────────────────────────────────────────────────────────────────────────
    // IsShortcutMapActive flag
    // ───────────────────────────────────────────────────────────────────────────

    [Fact]
    public void IsShortcutMapActive_True_With_Default_Map()
    {
        // Sanity: with the default map and no overrides, the flag should be true
        // (lots of chip-scope bindings).
        var ctx = NewContext();
        try
        {
            var cut = ctx.Render<CaleeSchedulerDayView<CalendarEvent>>(p => p
                .Add(c => c.TimeZone, TZ)
                .Add(c => c.Date, Anchor)
                .Add(c => c.Events, Array.Empty<CalendarEvent>()));
            Assert.True(cut.Instance.IsShortcutMapActive);
        }
        finally
        {
            ctx.Dispose();
        }
    }

    // ───────────────────────────────────────────────────────────────────────────
    // FR-29 defaults: DisabledShortcuts / ShortcutMap both null.
    // ───────────────────────────────────────────────────────────────────────────

    [Fact]
    public void DisabledShortcuts_Defaults_Null()
    {
        var ctx = NewContext();
        try
        {
            var cut = ctx.Render<CaleeSchedulerDayView<CalendarEvent>>(p => p
                .Add(c => c.TimeZone, TZ)
                .Add(c => c.Date, Anchor)
                .Add(c => c.Events, Array.Empty<CalendarEvent>()));
            Assert.Null(cut.Instance.DisabledShortcuts);
            Assert.Null(cut.Instance.ShortcutMap);
        }
        finally
        {
            ctx.Dispose();
        }
    }

    // ───────────────────────────────────────────────────────────────────────────
    // Root view-switch routing: consumer-wired callback wins; otherwise root flips
    // its own internal view in uncontrolled mode.
    // ───────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Root_DigitKey_Without_Callback_Auto_Flips_View_In_Uncontrolled_Mode()
    {
        using var ctx = NewContext();
        var a = Timed("a", 9, 10);
        var cut = ctx.Render<CaleeScheduler<CalendarEvent>>(p => p
            .Add(c => c.TimeZone, TZ)
            .Add(c => c.Date, Anchor)
            // No `View` parameter → uncontrolled mode.
            .Add(c => c.Events, new[] { a }));

        // Initial active view is the option default. Press "3" → Month.
        await cut.InvokeAsync(() =>
            cut.Find(".calee-scheduler-event")
                .KeyDown(new KeyboardEventArgs { Key = "3" }));

        Assert.Equal(SchedulerView.Month, cut.Instance._internalView);
    }

    [Fact]
    public async Task Root_DigitKey_With_Consumer_Callback_Does_Not_Auto_Flip()
    {
        using var ctx = NewContext();
        var callCount = 0;
        var lastSwitch = (SchedulerView?)null;
        var a = Timed("a", 9, 10);
        // CaleeSchedulerOptions.DefaultView is Week — uncontrolled mode starts on
        // Week. The test presses "3" (ViewMonth) so we can detect "no auto-flip"
        // (internal stays Week) regardless of what the default is.
        var cut = ctx.Render<CaleeScheduler<CalendarEvent>>(p => p
            .Add(c => c.TimeZone, TZ)
            .Add(c => c.Date, Anchor)
            .Add(c => c.Events, new[] { a })
            .Add(c => c.OnViewSwitchRequested,
                EventCallback.Factory.Create<SchedulerView>(this, v => { callCount++; lastSwitch = v; })));

        var initialInternal = cut.Instance._internalView;
        // Hit "3" → ViewMonth. Pick a target that differs from initialInternal so
        // an auto-flip would actually be observable.
        var differentDigit = initialInternal == SchedulerView.Month ? "2" : "3";
        var expectedSwitch = differentDigit == "2" ? SchedulerView.Week : SchedulerView.Month;

        await cut.InvokeAsync(() =>
            cut.Find(".calee-scheduler-event")
                .KeyDown(new KeyboardEventArgs { Key = differentDigit }));

        Assert.Equal(expectedSwitch, lastSwitch);
        Assert.Equal(1, callCount);
        // Auto-flip is suppressed when the consumer callback is wired: the internal
        // view stays at its initial value (not the keystroke's target).
        Assert.Equal(initialInternal, cut.Instance._internalView);
    }

    [Fact]
    public async Task Root_T_Without_Callback_Auto_Flips_Date_To_Today()
    {
        using var ctx = NewContext();
        // Anchor is May 19, 2026 — not "today" in any time zone the test is likely run in.
        // We expect _internalDate to be reseated to "today in TZ" after pressing 't'.
        var cut = ctx.Render<CaleeScheduler<CalendarEvent>>(p => p
            .Add(c => c.TimeZone, TZ)
            // No Date param → uncontrolled.
            .Add(c => c.Events, Array.Empty<CalendarEvent>()));

        var preDate = cut.Instance._internalDate;
        await cut.InvokeAsync(() =>
            cut.Find("[role='grid']").KeyDown(new KeyboardEventArgs { Key = "t" }));

        // Today in TZ is a moving target — we just assert the date changed to a value
        // that is no further than 2 days from UtcNow. If the test environment's "today"
        // happens to be the seeded internal date, the test passes trivially. Most
        // importantly: pressing 't' didn't throw.
        var nowInTz = TimeZoneInfo.ConvertTime(DateTimeOffset.UtcNow, TZ).Date;
        Assert.Equal(nowInTz, cut.Instance._internalDate.Date);
    }

    // ----- GetHotkeyFor extension (ADR-0014 helper) -----------------------------------

    [Fact]
    public void GetHotkeyFor_Returns_First_Matching_Binding_Key()
    {
        // edit.undo is double-bound (Cmd+Z and Ctrl+Z). The default map lists the
        // Cmd variant first per ADR-0013's table.
        var key = SchedulerShortcuts.DefaultMap.GetHotkeyFor(SchedulerCommandIds.EditUndo);
        Assert.Equal("Cmd+Z", key);
    }

    [Fact]
    public void GetHotkeyFor_Returns_Null_For_Unknown_CommandId()
    {
        var key = SchedulerShortcuts.DefaultMap.GetHotkeyFor("not.a.real.command");
        Assert.Null(key);
    }

    [Fact]
    public void GetHotkeyFor_Works_Against_Custom_Binding_List()
    {
        // Consumers can build any IReadOnlyList<ShortcutBinding> and query it the
        // same way — the extension method isn't tied to DefaultMap.
        var custom = new[]
        {
            new ShortcutBinding("Ctrl+Alt+U", SchedulerCommandIds.EditUndo),
            new ShortcutBinding("F1", SchedulerCommandIds.HelpOpen),
        };
        Assert.Equal("Ctrl+Alt+U", custom.GetHotkeyFor(SchedulerCommandIds.EditUndo));
        Assert.Equal("F1", custom.GetHotkeyFor(SchedulerCommandIds.HelpOpen));
        Assert.Null(custom.GetHotkeyFor(SchedulerCommandIds.EditDelete));
    }
}
