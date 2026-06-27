namespace Calee.Scheduler.Contracts;

/// <summary>
/// Identifies which kind of overflow chip was clicked, so the consumer's
/// <c>OnDayOverflowClicked</c> handler can route accordingly.
/// </summary>
/// <remarks>
/// <para><see cref="Month"/> is fired by the Month view when a day cell has more events than fit.</para>
/// <para>
/// <see cref="Earlier"/> and <see cref="Later"/> are fired by the Day/Week/Timeline time grids
/// when events fall before <c>StartHour</c> or after <c>EndHour</c> respectively. See FR-19a / FR-19b.
/// </para>
/// </remarks>
public enum OverflowKind
{
    /// <summary>The "+N more" chip in a Month view day cell.</summary>
    Month,

    /// <summary>The "+N earlier" chip when events fall before the visible time range.</summary>
    Earlier,

    /// <summary>The "+N later" chip when events fall after the visible time range.</summary>
    Later,

    /// <summary>The "+N" block in a time-grid column/lane when overlapping events exceed the column cap.</summary>
    Overlap,
}
