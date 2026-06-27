using Calee.Scheduler.Contracts;

namespace Calee.Scheduler.Internal;

/// <summary>
/// Output of <see cref="EventLayoutEngine.Layout"/>. Holds the positioned events that the
/// renderer should draw inside the visible band, plus the two overflow buckets feeding the
/// per-band "+N earlier" / "+N later" chips.
/// </summary>
/// <param name="Positioned">
/// Events that fall (wholly or partially) inside the visible band. Sorted by
/// <see cref="PositionedEvent.StackIndex"/> then <see cref="PositionedEvent.TimeStartPercent"/>
/// for deterministic ordering.
/// </param>
/// <param name="EarlierOverflow">
/// Events that lie entirely before the visible band (i.e., before the configured
/// <c>rangeStartHour</c> on the band's day, when a floor is supplied). Empty when no floor
/// is configured.
/// </param>
/// <param name="LaterOverflow">
/// Events that lie entirely after the visible band (i.e., after the configured
/// <c>rangeEndHour</c> on the band's day, when a ceiling is supplied). Empty when no
/// ceiling is configured.
/// </param>
/// <param name="OverlapOverflow">
/// Collapsed "+N" blocks produced when a column cap is active and concurrency exceeds it.
/// Each block covers the time extent of its hidden events and sits in the reserved last
/// stack slot. Empty when no cap is configured (the default) or when concurrency never
/// exceeds the cap. See <see cref="OverlapOverflowBlock"/>.
/// </param>
internal sealed record LayoutResult(
    IReadOnlyList<PositionedEvent> Positioned,
    IReadOnlyList<ICalendarEvent> EarlierOverflow,
    IReadOnlyList<ICalendarEvent> LaterOverflow,
    IReadOnlyList<OverlapOverflowBlock> OverlapOverflow);

/// <summary>
/// A collapsed run of overlapping events that exceeded the configured column cap.
/// Rendered as a single "+N" block in the reserved last stack slot, spanning the time
/// extent of the events it hides. Carries those events so the consumer's overflow
/// callback can present a chooser. Field names are direction-agnostic — see
/// <see cref="PositionedEvent"/>. A block always hides at least two events (a lone
/// overflow event is promoted to a normal capped chip instead).
/// </summary>
internal sealed record OverlapOverflowBlock(
    double TimeStartPercent,
    double TimeSpanPercent,
    int StackIndex,
    int StackCount,
    DateTimeOffset RegionStart,
    DateTimeOffset RegionEnd,
    IReadOnlyList<ICalendarEvent> Events);

/// <summary>
/// A single event placed in a layout band. Field names are intentionally direction-agnostic
/// so the same record can serve both vertical-time (Day/Week) and horizontal-time
/// (TimelineView) renderers. See PRD §4.4.
/// </summary>
/// <param name="Event">The original consumer event reference.</param>
/// <param name="TimeStartPercent">
/// Distance from the start of the visible band along the time axis, in the range [0, 100].
/// Maps to CSS <c>top</c> for vertical layouts and CSS <c>left</c> for horizontal layouts.
/// </param>
/// <param name="TimeSpanPercent">
/// Span along the time axis, in the range [0, 100]. Maps to CSS <c>height</c> for vertical
/// layouts and CSS <c>width</c> for horizontal layouts. Zero-duration events report <c>0</c>;
/// the renderer is responsible for any minimum visual height.
/// </param>
/// <param name="StackIndex">
/// 0-based overlap stack position assigned by the sweep-line pass. Stack positions are
/// reused once an event ends, so this does not directly correspond to the size of any
/// transitive overlap group. The engine's internal "stack" concept is the perpendicular-
/// to-time sub-slot used when two or more events share time; in Day/Week views stacks
/// are sub-columns within a day column, in TimelineView stacks are sub-rows within a
/// lane's row. See CONTEXT.md "Stack" and ADR-0011.
/// </param>
/// <param name="StackCount">
/// Maximum number of concurrent events observed during this event's lifetime. The width of
/// a stack slot is <c>1 / StackCount</c>. Per-event, not per-group — see ADR-0003.
/// </param>
/// <param name="ClippedAtTimeStart">
/// True when the event extends past the start of the visible band (i.e., the renderer should
/// draw a continues-from indicator on the cut edge).
/// </param>
/// <param name="ClippedAtTimeEnd">
/// True when the event extends past the end of the visible band.
/// </param>
internal sealed record PositionedEvent(
    ICalendarEvent Event,
    double TimeStartPercent,
    double TimeSpanPercent,
    int StackIndex,
    int StackCount,
    bool ClippedAtTimeStart,
    bool ClippedAtTimeEnd);
