#nullable enable
namespace Calee.Scheduler.Contracts;

/// <summary>
/// One entry in a keyboard shortcut map: a keystroke specification paired with the
/// stable command identifier the library dispatches when the user presses it.
/// </summary>
/// <param name="Key">
/// The keystroke specification. Single token (<c>"z"</c>, <c>"Delete"</c>, <c>"Escape"</c>,
/// <c>"Backspace"</c>, <c>"?"</c>, <c>" "</c>) or a modifier-prefixed token
/// (<c>"Cmd+Z"</c>, <c>"Cmd+Shift+Z"</c>, <c>"Ctrl+Y"</c>, <c>"Shift+ArrowUp"</c>,
/// <c>"Cmd+K"</c>).
/// <para>
/// <strong>Syntax:</strong> <c>[modifier+]*key</c>. Modifiers (case-sensitive,
/// capital first letter): <c>Cmd</c> (matches <c>e.MetaKey</c>), <c>Ctrl</c>
/// (<c>e.CtrlKey</c>), <c>Alt</c> (<c>e.AltKey</c>), <c>Shift</c> (<c>e.ShiftKey</c>).
/// The <c>+</c> separator is literal; key tokens that themselves contain a <c>+</c>
/// (none are bound today) must appear last so the parser can identify the trailing
/// non-modifier token. <c>Cmd|Ctrl</c> is <strong>not</strong> a syntax — to bind both
/// Cmd+Z and Ctrl+Z, list two separate <see cref="ShortcutBinding"/> entries.
/// </para>
/// <para>
/// <strong>Key names</strong> match <c>KeyboardEventArgs.Key</c> values after the
/// library's case-normalization rule for letters: <c>"z"</c> and <c>"Z"</c> resolve to
/// the same canonical letter — the <see cref="Microsoft.AspNetCore.Components.Web.KeyboardEventArgs.ShiftKey"/>
/// flag is the canonical discriminator for binding distinctions like undo-vs-redo, NOT
/// the letter's printable case. Non-letter keys (<c>"Delete"</c>, <c>"Escape"</c>,
/// <c>"ArrowUp"</c>, <c>"?"</c>, <c>" "</c>, etc.) match their <c>e.Key</c> value verbatim.
/// </para>
/// </param>
/// <param name="CommandId">
/// Stable string identifier for the command this binding dispatches — e.g.,
/// <c>"view.day"</c>, <c>"edit.undo"</c>, <c>"navigate.today"</c>. Use the well-known
/// constants on <see cref="SchedulerCommandIds"/> for the built-in commands so misspellings
/// become compile-time errors. Consumer-defined command ids (Phase 2 Task 15 <c>Commands</c>
/// surface, per ADR-0014) may also appear here; ids not present in the default map are
/// no-ops for <see cref="ShortcutBinding"/> entries supplied to the library today.
/// </param>
public sealed record ShortcutBinding(string Key, string CommandId);
