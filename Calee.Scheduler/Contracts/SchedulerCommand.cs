#nullable enable
namespace Calee.Scheduler.Contracts;

/// <summary>
/// One entry in the command palette's command list: a stable identifier, a display label,
/// an optional grouping, and a parameter-less action that executes the command. Consumers
/// query the scheduler's <c>Commands</c> property to populate their consumer-rendered
/// palette (ADR-0010 / ADR-0014); hotkeys for each command are not carried here — they
/// live separately in the <see cref="ShortcutBinding"/> map (ADR-0013), looked up by the
/// same <see cref="Id"/>.
/// </summary>
/// <param name="Id">
/// Stable command identifier. Built-in commands use the constants on
/// <see cref="SchedulerCommandIds"/> (e.g., <c>view.day</c>, <c>navigate.today</c>,
/// <c>edit.undo</c>); custom commands supplied via <c>CustomCommands</c> use any
/// consumer-defined string that does NOT collide with the library's
/// <c>view.*</c> / <c>navigate.*</c> / <c>edit.*</c> / <c>palette.*</c> / <c>help.*</c>
/// id namespaces.
/// <para>
/// When a custom command's <see cref="Id"/> shadows a built-in id, the consumer-supplied
/// command wins (last-write — see ADR-0014). This lets consumers override a built-in's
/// label or <see cref="Invoke"/> behavior wholesale.
/// </para>
/// </param>
/// <param name="Label">
/// Display string the consumer's palette renders for this command. English only —
/// localization is out of scope for v1 (PRD §10). Built-in commands ship a concise
/// English label; consumers can override via a custom command that shadows the same
/// <see cref="Id"/>.
/// </param>
/// <param name="Group">
/// Optional grouping suggestion for the consumer's palette UI. The library populates this
/// for built-ins with <c>"View"</c>, <c>"Navigate"</c>, <c>"Edit"</c>, or <c>"Help"</c>;
/// the consumer's palette is free to render group headers, sort by group, or ignore the
/// field entirely. <see langword="null"/> is valid for both built-in and custom commands.
/// </param>
/// <param name="Invoke">
/// Parameter-less <see cref="Action"/> that executes the command when the consumer's
/// palette invokes it. For library built-ins this routes into the same internal dispatch
/// the keystroke handlers use — pressing <c>Cmd+Z</c> and clicking the "Undo" command in
/// the palette both fire <c>OnUndoRequested</c>. For consumer-supplied commands, the
/// closure does whatever the consumer wires.
/// <para>
/// <strong>Synchronous shape.</strong> <see cref="Action"/> is sync; the underlying
/// library callbacks (<c>EventCallback.InvokeAsync</c>) are async. Built-in
/// <see cref="Invoke"/> implementations fire the async callback in a fire-and-forget
/// pattern (discarded <see cref="System.Threading.Tasks.Task"/> return value). Consumers
/// who need to <see langword="await"/> a command's completion should wire their palette
/// to call the consumer-side handler directly rather than going through this
/// <see cref="Action"/>; for telemetry / progress signaling, the existing per-callback
/// async surface remains the canonical channel.
/// </para>
/// </param>
public sealed record SchedulerCommand(
    string Id,
    string Label,
    string? Group,
    Action Invoke);
