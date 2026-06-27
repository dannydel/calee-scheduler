#nullable enable
using Calee.Scheduler.Contracts;
using Microsoft.AspNetCore.Components.Web;

namespace Calee.Scheduler.Internal;

/// <summary>
/// The resolved keyboard shortcut map for a single render pass — the result of merging
/// <see cref="SchedulerShortcuts.DefaultMap"/> with consumer-supplied
/// <c>DisabledShortcuts</c> and <c>ShortcutMap</c> overrides per the precedence rules
/// documented in ADR-0013.
/// </summary>
/// <remarks>
/// <para>
/// <strong>Precedence (deterministic, order-stable):</strong>
/// <list type="number">
///   <item><description>Drop default entries whose <see cref="ShortcutBinding.CommandId"/>
///     appears in <c>DisabledShortcuts</c>.</description></item>
///   <item><description>For each default entry remaining: when <c>ShortcutMap</c>
///     contains a binding with the same <c>CommandId</c>, replace the default's
///     <c>Key</c> with the override's (the default's keystroke is discarded —
///     remapping replaces, not augments).</description></item>
///   <item><description>Append <c>ShortcutMap</c> entries whose <c>CommandId</c> is
///     NOT in the default map (forward-compat for consumer-defined ids — Task 15's
///     <c>SchedulerCommand</c> API).</description></item>
/// </list>
/// </para>
/// <para>
/// Cached on the cascading <see cref="SchedulerStateContainer"/> so descendant views read
/// the same resolved snapshot without re-parsing on every keystroke. Standalone views
/// (no cascade) build their own instance in <c>OnParametersSet</c>.
/// </para>
/// </remarks>
internal sealed class ResolvedShortcutMap
{
    private readonly List<(ParsedShortcut Parsed, string CommandId)> _entries;

    private ResolvedShortcutMap(List<(ParsedShortcut Parsed, string CommandId)> entries, IReadOnlyList<ShortcutBinding> snapshot)
    {
        _entries = entries;
        Snapshot = snapshot;
    }

    /// <summary>
    /// Returns the empty resolved map. Used as the sentinel until the first
    /// <c>OnParametersSet</c> wires the consumer overrides.
    /// </summary>
    public static ResolvedShortcutMap Empty { get; } = new(new List<(ParsedShortcut, string)>(), Array.Empty<ShortcutBinding>());

    /// <summary>
    /// Order-stable snapshot of the resolved bindings as <see cref="ShortcutBinding"/>
    /// records (in the order produced by the resolution algorithm above). Used by
    /// consumers iterating the resolved map to render help / palette UI, and by tests
    /// asserting on the resolution output.
    /// </summary>
    public IReadOnlyList<ShortcutBinding> Snapshot { get; }

    /// <summary>
    /// Try to find a binding whose parsed shape matches the supplied
    /// <see cref="KeyboardEventArgs"/>. Returns the matched command id, or
    /// <see langword="null"/> when no binding matches.
    /// </summary>
    /// <remarks>
    /// O(N) over the resolved entries. The typical N is ~22 (the default map size); the
    /// constant factor is a handful of bool comparisons per entry. Hashing on the parsed
    /// shape would save microseconds but complicates the override semantics — the linear
    /// walk is the simpler, more predictable choice.
    /// </remarks>
    public string? Match(KeyboardEventArgs e)
    {
        for (var i = 0; i < _entries.Count; i++)
        {
            var (parsed, commandId) = _entries[i];
            if (ShortcutBindingParser.Matches(in parsed, e))
            {
                return commandId;
            }
        }
        return null;
    }

    /// <summary>
    /// Build a fresh resolved map from the supplied consumer overrides. Both inputs may
    /// be <see langword="null"/>; <see langword="null"/> means "no override" and the
    /// returned map equals the default map (filtered by no-op disabled set).
    /// </summary>
    public static ResolvedShortcutMap Resolve(
        IReadOnlyList<string>? disabled,
        IReadOnlyList<ShortcutBinding>? overrides)
    {
        var defaults = SchedulerShortcuts.DefaultMap;

        // Hash the consumer overrides by command id so we can do O(1) per-default lookup
        // and so the trailing "append consumer-defined ids" step can identify which
        // overrides were already consumed by a default replacement.
        Dictionary<string, ShortcutBinding>? overridesByCommandId = null;
        if (overrides is not null && overrides.Count > 0)
        {
            overridesByCommandId = new Dictionary<string, ShortcutBinding>(overrides.Count, StringComparer.Ordinal);
            for (var i = 0; i < overrides.Count; i++)
            {
                var ov = overrides[i];
                // Last-write-wins for duplicate command ids in the override list. The
                // documented contract is "one binding per command id" but consumer code
                // may accidentally supply duplicates; rather than throw, prefer the
                // last entry (matches how ADR-0014's CustomCommands resolves
                // shadow-conflicts: last-write wins).
                overridesByCommandId[ov.CommandId] = ov;
            }
        }

        HashSet<string>? disabledSet = null;
        if (disabled is not null && disabled.Count > 0)
        {
            disabledSet = new HashSet<string>(disabled, StringComparer.Ordinal);
        }

        // Track which command ids have a default entry, so we know which overrides are
        // "new" command ids to append at the tail (instead of replacing a default).
        var defaultCommandIds = new HashSet<string>(StringComparer.Ordinal);
        for (var i = 0; i < defaults.Count; i++)
        {
            defaultCommandIds.Add(defaults[i].CommandId);
        }

        var resolved = new List<(ParsedShortcut Parsed, string CommandId)>(defaults.Count + (overrides?.Count ?? 0));
        var snapshot = new List<ShortcutBinding>(defaults.Count + (overrides?.Count ?? 0));

        // A default entry contributes when:
        //   - its command id is not in DisabledShortcuts, AND
        //   - either there is no override for its command id (use the default Key) OR
        //     there is and we use the override's Key.
        // We emit one entry PER override key when the override is supplied — the
        // override replaces the default's keystrokes. If the default map had multiple
        // entries for the same command id (e.g., Cmd+Z + Ctrl+Z both bind edit.undo),
        // the override replaces ALL of them with a single override keystroke. Iterate
        // the default list ONCE per command id by tracking which command ids we've
        // already emitted; subsequent default entries with the same id are skipped.
        var emittedCommandIds = new HashSet<string>(StringComparer.Ordinal);
        for (var i = 0; i < defaults.Count; i++)
        {
            var d = defaults[i];
            if (disabledSet is not null && disabledSet.Contains(d.CommandId))
            {
                continue;
            }
            if (overridesByCommandId is not null && overridesByCommandId.TryGetValue(d.CommandId, out var ov))
            {
                if (emittedCommandIds.Add(d.CommandId))
                {
                    // Emit the override exactly once for this command id, at the
                    // position of the first default entry with this id. Subsequent
                    // default entries with the same command id are silently dropped.
                    if (ShortcutBindingParser.TryParse(ov.Key, out var parsed))
                    {
                        resolved.Add((parsed, ov.CommandId));
                        snapshot.Add(ov);
                    }
                    // Malformed override key (TryParse=false) — silently no-op.
                }
                continue;
            }
            // No override: keep the default verbatim. Multiple default entries for the
            // same command id (e.g., Delete + Backspace both bind edit.delete) all
            // contribute their own match entry — we do NOT collapse on command id when
            // the user hasn't supplied an override.
            if (ShortcutBindingParser.TryParse(d.Key, out var pd))
            {
                resolved.Add((pd, d.CommandId));
                snapshot.Add(d);
            }
        }

        // Append overrides whose command id has no default-map entry. These are
        // forward-compat for Task 15 consumer-defined commands.
        if (overridesByCommandId is not null)
        {
            // Iterate `overrides` (not the dictionary) to preserve consumer-supplied
            // order — the dictionary doesn't guarantee enumeration order.
            for (var i = 0; i < overrides!.Count; i++)
            {
                var ov = overrides[i];
                if (defaultCommandIds.Contains(ov.CommandId)) continue;
                // Last-write-wins for duplicate consumer-defined ids: only emit the
                // dictionary's final entry per id.
                if (!ReferenceEquals(overridesByCommandId[ov.CommandId], ov)) continue;
                if (ShortcutBindingParser.TryParse(ov.Key, out var parsed))
                {
                    resolved.Add((parsed, ov.CommandId));
                    snapshot.Add(ov);
                }
            }
        }

        return new ResolvedShortcutMap(resolved, snapshot);
    }
}
