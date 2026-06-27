#nullable enable
using System.Collections;

namespace Calee.Scheduler.Internal;

/// <summary>
/// An ordered set of selected event ids. Iteration order matches insertion order,
/// which preserves "anchor" semantics for Shift+click range select (Task 11 keyboard
/// support reuses the same anchor — the most-recently-added id — to extend selection
/// via Shift+Arrow). The last id in iteration order is the active anchor.
/// </summary>
/// <remarks>
/// <para>
/// <strong>Why an ordered set, not a plain <see cref="HashSet{T}"/>.</strong>
/// Shift+click range select needs a stable "from" point. The most ergonomic choice
/// is "the most recent plain click" — a single-id selection that the user grew via
/// modifiers afterwards. Tracking that without an ordered structure would require
/// a separate scalar field, which then has to be kept in sync with the set through
/// every mutation path; an ordered set collapses both concerns to one container.
/// </para>
/// <para>
/// <strong>Persistence by id, not by index/row.</strong> Selection stores event ids
/// — not row positions, chunk indices, or positioned-event references. When the
/// consumer pushes a parameter update that re-buckets events (a selected event's
/// <c>LaneKey</c> projection moves it to a different lane in
/// <c>CaleeSchedulerTimelineView</c>; see the focus-row clamp at
/// <c>CaleeSchedulerTimelineView.razor.cs:416</c>), the selection set is unchanged
/// because nothing in it references row indices.
/// </para>
/// <para>
/// <strong>Visibility.</strong> Internal. The selection storage shape is a Phase 2
/// implementation detail; consumers observe the typed event list via
/// <c>OnSelectionChanged</c>.
/// </para>
/// </remarks>
internal sealed class SchedulerSelection : IReadOnlyCollection<string>
{
    // OrderedDictionary-style storage: a List for ordered iteration, a HashSet for
    // O(1) Contains. Selection sizes are small in practice (tens of events), but
    // ToHashSet diffs in Task 11 will benefit from the constant-time membership check.
    private readonly List<string> _orderedIds = new();
    private readonly HashSet<string> _idSet = new(StringComparer.Ordinal);

    /// <summary>Number of selected event ids.</summary>
    public int Count => _orderedIds.Count;

    /// <summary>True when no events are selected.</summary>
    public bool IsEmpty => _orderedIds.Count == 0;

    /// <summary>
    /// The most-recently-added id (the active anchor for Shift+click range select),
    /// or <see langword="null"/> when the selection is empty.
    /// </summary>
    public string? Anchor => _orderedIds.Count == 0 ? null : _orderedIds[^1];

    /// <summary>Returns true when the supplied id is currently selected.</summary>
    public bool Contains(string id) => _idSet.Contains(id);

    /// <summary>Iterate selected ids in insertion order (oldest first; anchor last).</summary>
    public IEnumerator<string> GetEnumerator() => _orderedIds.GetEnumerator();

    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();

    /// <summary>
    /// Materialize the selection as an immutable snapshot in insertion order. Used
    /// when firing <c>OnSelectionChanged</c> so the consumer sees a stable list that
    /// won't mutate underneath it (the live selection may change before the consumer
    /// finishes processing).
    /// </summary>
    public IReadOnlyList<string> ToOrderedList() => _orderedIds.ToArray();

    /// <summary>
    /// Replace the current selection with the supplied ordered set. Returns
    /// <see langword="true"/> when the new contents differ from the previous contents
    /// — order-sensitive: the same ids in a different order count as a change because
    /// the anchor moved, which affects Shift+click semantics in Task 11. Returns
    /// <see langword="false"/> when the new contents exactly match the previous
    /// contents (same ids, same order), so callers can suppress no-op
    /// <c>OnSelectionChanged</c> fires.
    /// </summary>
    /// <param name="newIds">The new selection, in the desired anchor order.</param>
    public bool Replace(IReadOnlyList<string> newIds)
    {
        if (SequenceEqualOrdered(_orderedIds, newIds))
        {
            return false;
        }
        _orderedIds.Clear();
        _idSet.Clear();
        for (var i = 0; i < newIds.Count; i++)
        {
            var id = newIds[i];
            if (_idSet.Add(id))
            {
                _orderedIds.Add(id);
            }
        }
        return true;
    }

    private static bool SequenceEqualOrdered(List<string> a, IReadOnlyList<string> b)
    {
        if (a.Count != b.Count) return false;
        for (var i = 0; i < a.Count; i++)
        {
            if (!string.Equals(a[i], b[i], StringComparison.Ordinal)) return false;
        }
        return true;
    }
}
