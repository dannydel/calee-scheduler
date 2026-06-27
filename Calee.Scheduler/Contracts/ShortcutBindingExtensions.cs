namespace Calee.Scheduler.Contracts;

/// <summary>
/// Read-side helpers consumers use when iterating a shortcut binding list (the
/// public <see cref="SchedulerShortcuts.DefaultMap"/> or an effective resolved
/// snapshot they've built from <c>DefaultMap</c> + their own <c>DisabledShortcuts</c>
/// + <c>ShortcutMap</c>). Ships the lookup helper ADR-0014's example snippet
/// references — consumers rendering a command palette query
/// <c>bindings.GetHotkeyFor(cmd.Id)</c> alongside the matching
/// <see cref="SchedulerCommand"/> to display the hotkey label next to the command row.
/// </summary>
public static class ShortcutBindingExtensions
{
    /// <summary>
    /// Returns the first <see cref="ShortcutBinding.Key"/> in <paramref name="bindings"/>
    /// whose <see cref="ShortcutBinding.CommandId"/> matches <paramref name="commandId"/>,
    /// or <see langword="null"/> when no binding matches.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A command can have multiple bindings (e.g., <c>edit.undo</c> binds both
    /// <c>Cmd+Z</c> and <c>Ctrl+Z</c>; <c>edit.delete</c> binds both <c>Delete</c> and
    /// <c>Backspace</c>). This helper returns the first binding found in iteration
    /// order — the default map's order matches ADR-0013's table, so the Mac variant
    /// returns first for Mac-first bindings. Consumers who want the platform-aware
    /// label should iterate themselves and pick by platform.
    /// </para>
    /// <para>
    /// Comparison is ordinal (case-sensitive on the id portion); command ids are
    /// stable identifiers, not user-facing strings, so case-sensitivity is correct.
    /// </para>
    /// </remarks>
    /// <param name="bindings">The binding list to search — typically
    /// <see cref="SchedulerShortcuts.DefaultMap"/> or a consumer's resolved snapshot.</param>
    /// <param name="commandId">The command id to look up (e.g.,
    /// <see cref="SchedulerCommandIds.EditUndo"/>).</param>
    /// <returns>The matching binding's <see cref="ShortcutBinding.Key"/>, or
    /// <see langword="null"/>.</returns>
    public static string? GetHotkeyFor(this IReadOnlyList<ShortcutBinding> bindings, string commandId)
    {
        ArgumentNullException.ThrowIfNull(bindings);
        ArgumentNullException.ThrowIfNull(commandId);
        for (var i = 0; i < bindings.Count; i++)
        {
            if (string.Equals(bindings[i].CommandId, commandId, StringComparison.Ordinal))
            {
                return bindings[i].Key;
            }
        }
        return null;
    }
}
