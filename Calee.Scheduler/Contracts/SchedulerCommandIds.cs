#nullable enable
namespace Calee.Scheduler.Contracts;

/// <summary>
/// Well-known stable identifiers for the library's built-in keyboard commands. Use these
/// constants in <c>DisabledShortcuts</c> / <c>ShortcutMap</c> values (and, when Task 15
/// lands, in the <c>SchedulerCommand.Id</c> field per ADR-0014) so misspellings are
/// compile-time errors rather than silent no-ops.
/// </summary>
/// <remarks>
/// <para>
/// The id taxonomy mirrors ADR-0013's binding table:
/// <list type="bullet">
///   <item><description><c>view.*</c> — view-switch commands.</description></item>
///   <item><description><c>navigate.*</c> — anchor / range navigation.</description></item>
///   <item><description><c>edit.*</c> — CRUD and selection / undo / redo.</description></item>
///   <item><description><c>palette.*</c> — command palette triggers (Task 15 wires the API).</description></item>
///   <item><description><c>help.*</c> — help / documentation triggers.</description></item>
/// </list>
/// </para>
/// <para>
/// Consumer-defined command ids should NOT use these prefixes (per ADR-0014).
/// </para>
/// </remarks>
public static class SchedulerCommandIds
{
    /// <summary>Switch to Day view (<c>"view.day"</c>; default binding <c>1</c>).</summary>
    public const string ViewDay = "view.day";

    /// <summary>Switch to Week view (<c>"view.week"</c>; default binding <c>2</c>).</summary>
    public const string ViewWeek = "view.week";

    /// <summary>Switch to Month view (<c>"view.month"</c>; default binding <c>3</c>).</summary>
    public const string ViewMonth = "view.month";

    /// <summary>
    /// Switch to Year view (<c>"view.year"</c>; default binding <c>4</c>). Fires
    /// <c>OnViewSwitchRequested</c> with <see cref="SchedulerView"/> <c>Year</c> from both
    /// the keystroke and the palette since Phase 2 Task 16 wired the view live.
    /// </summary>
    public const string ViewYear = "view.year";

    /// <summary>
    /// Switch to Agenda view (<c>"view.agenda"</c>; default binding <c>5</c>). Fires
    /// <c>OnViewSwitchRequested</c> with <see cref="SchedulerView"/> <c>Agenda</c> from both
    /// the keystroke and the palette since Phase 2 Task 17 wired the view live.
    /// </summary>
    public const string ViewAgenda = "view.agenda";

    /// <summary>Switch to Timeline view (<c>"view.timeline"</c>; default binding <c>6</c>).</summary>
    public const string ViewTimeline = "view.timeline";

    /// <summary>Jump anchor to today (<c>"navigate.today"</c>; default binding <c>t</c>).</summary>
    public const string NavigateToday = "navigate.today";

    /// <summary>
    /// Create a new event at the focused slot (<c>"edit.create"</c>; default binding
    /// <c>n</c>). Grid-scope only — fires <c>OnCreateAtFocusRequested</c>; the consumer
    /// resolves the focused slot and pushes a new event into <c>Events</c>.
    /// </summary>
    public const string EditCreate = "edit.create";

    /// <summary>
    /// Delete the focused event (<c>"edit.delete"</c>; default bindings <c>Delete</c> and
    /// <c>Backspace</c>). Chip-scope only — fires <c>OnEventDeleted</c> (or
    /// <c>OnEventsDeleted</c> for batch) when <c>AllowDelete</c> is true.
    /// </summary>
    public const string EditDelete = "edit.delete";

    /// <summary>
    /// Enter move-mode on the focused event (<c>"edit.move"</c>; default binding
    /// <c>m</c>). Chip-scope only — fires <c>OnMoveModeRequested</c>. Library does not
    /// implement the mode itself (Task 14 placeholder); consumer or Phase 3 wires the
    /// behavior.
    /// </summary>
    public const string EditMove = "edit.move";

    /// <summary>
    /// Resize the focused event via the keyboard (<c>"edit.resize"</c>; default bindings
    /// <c>Shift+ArrowUp</c> / <c>Shift+ArrowDown</c>). Chip-scope only — fires
    /// <c>OnResizeKeystrokeRequested</c>. Library does not implement the behavior itself
    /// (Task 14 placeholder); consumer or Phase 3 wires the slot delta and calls
    /// <c>OnEventResized</c>.
    /// </summary>
    public const string EditResize = "edit.resize";

    /// <summary>Undo the last operation (<c>"edit.undo"</c>; default bindings <c>Cmd+Z</c> / <c>Ctrl+Z</c>).</summary>
    public const string EditUndo = "edit.undo";

    /// <summary>Redo (<c>"edit.redo"</c>; default bindings <c>Cmd+Shift+Z</c> / <c>Ctrl+Y</c>).</summary>
    public const string EditRedo = "edit.redo";

    /// <summary>
    /// Toggle the focused event in/out of the selection (<c>"select.toggle"</c>; default
    /// binding <c>Space</c>). Chip-scope only. Currently routes through the same path as
    /// Ctrl+click — the existing FR-34 Space binding is preserved verbatim under the new
    /// dispatch.
    /// </summary>
    public const string SelectToggle = "select.toggle";

    /// <summary>
    /// Clear selection / cancel move-mode / blur grid (<c>"cancel"</c>; default binding
    /// <c>Escape</c>). Fires across both chip and grid scopes; the per-view Escape helper
    /// owns the multi-step fallback (clear selection → blur).
    /// </summary>
    public const string Cancel = "cancel";

    /// <summary>
    /// Open the command palette (<c>"palette.open"</c>; default bindings <c>Cmd+K</c> /
    /// <c>Ctrl+K</c>). Fires <c>OnCommandPaletteRequested</c>. The actual palette overlay
    /// is consumer-rendered per ADR-0010 / ADR-0014; Task 15 wires the <c>SchedulerCommand</c>
    /// API the consumer queries to render their palette.
    /// </summary>
    public const string PaletteOpen = "palette.open";

    /// <summary>
    /// Open help (<c>"help.open"</c>; default binding <c>?</c>). Fires
    /// <c>OnHelpRequested</c>; the consumer renders whatever help surface their app uses
    /// (often a modal listing the shortcut map — consumers can iterate
    /// <see cref="SchedulerShortcuts.DefaultMap"/> + their own overrides to render it).
    /// </summary>
    public const string HelpOpen = "help.open";
}
