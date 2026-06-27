#nullable enable
namespace Calee.Scheduler.Contracts;

/// <summary>
/// The library's opinionated default keyboard shortcut map (per ADR-0013). Consumers
/// opt out per-command via the <c>DisabledShortcuts</c> parameter on the root scheduler
/// (or any view), and remap individual bindings via the <c>ShortcutMap</c> parameter.
/// </summary>
/// <remarks>
/// <para>
/// <strong>Ordering matches ADR-0013's binding table.</strong> Where ADR-0013 lists both
/// Mac (<c>Cmd+...</c>) and Windows (<c>Ctrl+...</c>) variants for the same command, both
/// entries appear here as separate <see cref="ShortcutBinding"/> records — the parser
/// dispatches each independently. Where ADR-0013 lists a single-letter binding (<c>1</c>,
/// <c>t</c>, <c>n</c>, etc.), the library's focus-scoping rule limits the binding to
/// keystrokes that originate inside the scheduler region (the DOM event must target a
/// scheduler chip or grid container) — this prevents collisions with text-input typing
/// on the host page.
/// </para>
/// <para>
/// <strong>The Backspace alias.</strong> Both <c>Delete</c> and <c>Backspace</c> map to
/// <see cref="SchedulerCommandIds.EditDelete"/> because on macOS the primary keyboard's
/// "Delete" key reports <c>"Backspace"</c> (Forward-Delete / fn+Backspace reports
/// <c>"Delete"</c>). The two bindings carry the same command id so disabling
/// <see cref="SchedulerCommandIds.EditDelete"/> disables both keys at once.
/// </para>
/// <para>
/// <strong>Consumer-facing iteration.</strong> Apps rendering their own help / shortcut
/// dialog typically iterate <see cref="DefaultMap"/> together with the consumer's overrides
/// to render the canonical hotkey for each command. Order-stable so a diff against a
/// remap is meaningful.
/// </para>
/// </remarks>
public static class SchedulerShortcuts
{
    /// <summary>
    /// The library's default shortcut bindings, in the order defined by ADR-0013's
    /// binding table. Immutable; consumers can build a new list and pass it as the
    /// <c>ShortcutMap</c> parameter to override bindings for specific command ids.
    /// </summary>
    public static IReadOnlyList<ShortcutBinding> DefaultMap { get; } = new ShortcutBinding[]
    {
        // View switches (single-digit row; ADR-0013 §"Default bindings" rows 1–6).
        new("1", SchedulerCommandIds.ViewDay),
        new("2", SchedulerCommandIds.ViewWeek),
        new("3", SchedulerCommandIds.ViewMonth),
        new("4", SchedulerCommandIds.ViewYear),
        new("5", SchedulerCommandIds.ViewAgenda),
        new("6", SchedulerCommandIds.ViewTimeline),

        // Navigate / create (rows 7–8).
        new("t", SchedulerCommandIds.NavigateToday),
        new("n", SchedulerCommandIds.EditCreate),

        // Delete + the Backspace alias for macOS (row 9 + the spirit-of-the-binding
        // alias documented on SchedulerCommandIds.EditDelete).
        new("Delete", SchedulerCommandIds.EditDelete),
        new("Backspace", SchedulerCommandIds.EditDelete),

        // Move-mode + keyboard resize (rows 10–11). Library exposes the trigger; the
        // move/resize behavior itself is not implemented in Task 14 (placeholders).
        new("m", SchedulerCommandIds.EditMove),
        new("Shift+ArrowUp", SchedulerCommandIds.EditResize),
        new("Shift+ArrowDown", SchedulerCommandIds.EditResize),

        // Selection toggle + cancel (rows 12–13).
        new(" ", SchedulerCommandIds.SelectToggle),
        new("Escape", SchedulerCommandIds.Cancel),

        // Command palette (row 14) — both Mac and Windows variants per ADR-0013.
        new("Cmd+K", SchedulerCommandIds.PaletteOpen),
        new("Ctrl+K", SchedulerCommandIds.PaletteOpen),

        // Undo (row 15) — both Cmd and Ctrl variants.
        new("Cmd+Z", SchedulerCommandIds.EditUndo),
        new("Ctrl+Z", SchedulerCommandIds.EditUndo),

        // Redo (row 16) — Cmd+Shift+Z (macOS), Ctrl+Shift+Z (mirrors the
        // Ctrl-or-Cmd discrimination from Task 13's hardcoded handler), and Ctrl+Y
        // (Windows convention). ADR-0013 intentionally omits Cmd+Y (a macOS system
        // gesture — see ADR-0013 + Task 13's commit body).
        new("Cmd+Shift+Z", SchedulerCommandIds.EditRedo),
        new("Ctrl+Shift+Z", SchedulerCommandIds.EditRedo),
        new("Ctrl+Y", SchedulerCommandIds.EditRedo),

        // Help (row 17). On a US keyboard "?" requires Shift; browsers report
        // e.Key="?" with ShiftKey=true. Most other layouts also need a modifier.
        // ADR-0013 lists the canonical form as "?" — internally we list both the
        // shifted and bare variants so the binding fires from layouts that report
        // "?" with or without ShiftKey set.
        new("Shift+?", SchedulerCommandIds.HelpOpen),
        new("?", SchedulerCommandIds.HelpOpen),
    };
}
