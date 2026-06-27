namespace Calee.Scheduler.Demo.Components.Pages.Shared;

/// <summary>
/// Demo-only undo/redo stack. Each operation is a (Forward, Inverse) action pair —
/// the page's CRUD handler captures whatever state it needs in a closure before /
/// after applying the mutation, then pushes onto the stack. The library emits
/// undo/redo triggers via <c>OnUndoRequested</c> / <c>OnRedoRequested</c>; the
/// consumer (this demo) owns the history shape per ADR-0012.
/// </summary>
/// <remarks>
/// <para>
/// New edits clear the redo stack — the canonical history-tree pattern where a
/// fresh mutation "branches" from whatever the current state is and abandons any
/// outstanding redo entries. Same shape as text-editor undo (Cmd+Z, Cmd+Z, type
/// a character → Cmd+Y now has nothing to redo).
/// </para>
/// <para>
/// No size cap. The demo's edit volume is bounded by manual interaction; a
/// real consumer's history would likely cap depth or coalesce small adjacent
/// edits (e.g., consecutive resizes of the same event).
/// </para>
/// </remarks>
public sealed class SimpleUndoStack
{
    private readonly Stack<(Action Forward, Action Inverse)> _undo = new();
    private readonly Stack<(Action Forward, Action Inverse)> _redo = new();

    /// <summary>
    /// Record an operation. The caller has already applied <paramref name="forward"/>
    /// before calling this; the stack just remembers the (forward, inverse) pair so
    /// undo can replay the inverse and redo can re-apply the forward.
    /// </summary>
    /// <param name="forward">The mutation that was just applied. Re-running it must
    /// produce the same post-state from the current post-state's pre-state (idempotent
    /// on top of the inverse).</param>
    /// <param name="inverse">The mutation that reverses <paramref name="forward"/>.
    /// Running the inverse from the post-state must reproduce the pre-state.</param>
    public void Push(Action forward, Action inverse)
    {
        _undo.Push((forward, inverse));
        _redo.Clear();
    }

    /// <summary>
    /// Pop the most recent operation off the undo stack and apply its inverse. The
    /// operation moves to the redo stack so a subsequent <see cref="Redo"/> re-applies
    /// the forward action. No-op when the undo stack is empty.
    /// </summary>
    /// <returns><see langword="true"/> when an operation was applied; <see langword="false"/> when the stack was empty.</returns>
    public bool Undo()
    {
        if (_undo.Count == 0) return false;
        var op = _undo.Pop();
        op.Inverse();
        _redo.Push(op);
        return true;
    }

    /// <summary>
    /// Pop the most recent undone operation off the redo stack and re-apply its
    /// forward action. The operation moves back onto the undo stack. No-op when the
    /// redo stack is empty.
    /// </summary>
    /// <returns><see langword="true"/> when an operation was applied; <see langword="false"/> when the stack was empty.</returns>
    public bool Redo()
    {
        if (_redo.Count == 0) return false;
        var op = _redo.Pop();
        op.Forward();
        _undo.Push(op);
        return true;
    }

    /// <summary>Number of operations currently in the undo stack.</summary>
    public int UndoCount => _undo.Count;

    /// <summary>Number of operations currently in the redo stack.</summary>
    public int RedoCount => _redo.Count;
}
