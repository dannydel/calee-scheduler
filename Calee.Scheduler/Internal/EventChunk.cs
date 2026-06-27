namespace Calee.Scheduler.Internal;

using Calee.Scheduler.Contracts;

/// <summary>
/// A renderer-facing fragment of an event. See CONTEXT.md "Event chunk" entry.
/// Carries a chunk-local Start/End plus the original consumer event reference;
/// delegates non-temporal fields (Id, Title, IsAllDay, Color) to that underlying
/// event so the layout engine can consume chunks via the <see cref="ICalendarEvent"/>
/// interface without knowing whether it has a whole event or a fragment.
/// </summary>
/// <typeparam name="TEvent">Consumer event type implementing <see cref="ICalendarEvent"/>.</typeparam>
/// <param name="Event">The original consumer event reference; click handlers fire with this.</param>
/// <param name="Start">Chunk start, clipped to the chunk's day (PerDay) or range (Continuous).</param>
/// <param name="End">Chunk end, clipped to the chunk's day (PerDay) or range (Continuous).</param>
/// <param name="ClippedAtTimeStart">True when the original event extended before this chunk's start (continues-from-earlier).</param>
/// <param name="ClippedAtTimeEnd">True when the original event extended past this chunk's end (continues-to-later).</param>
internal sealed record EventChunk<TEvent>(
    TEvent Event,
    DateTimeOffset Start,
    DateTimeOffset End,
    bool ClippedAtTimeStart,
    bool ClippedAtTimeEnd) : ICalendarEvent
    where TEvent : ICalendarEvent
{
    /// <inheritdoc/>
    public string Id => Event.Id;

    /// <inheritdoc/>
    public string Title => Event.Title;

    /// <inheritdoc/>
    public bool IsAllDay => Event.IsAllDay;

    /// <inheritdoc/>
    public string? Color => Event.Color;

    /// <summary>
    /// True when the supplied <see cref="PositionedEvent"/> represents an event clipped at
    /// the time-start edge — either by the layout engine (visible-hour range) OR by the
    /// chunk-split (a continues-from-earlier multi-day fragment). Views compose these two
    /// causes into a single ↑/← indicator on the rendered element.
    /// </summary>
    public static bool IsClippedAtStart(PositionedEvent pe) =>
        pe.ClippedAtTimeStart || (pe.Event is EventChunk<TEvent> c && c.ClippedAtTimeStart);

    /// <summary>
    /// True when the supplied <see cref="PositionedEvent"/> represents an event clipped at
    /// the time-end edge — either by the layout engine OR by the chunk-split (a
    /// continues-into-later multi-day fragment).
    /// </summary>
    public static bool IsClippedAtEnd(PositionedEvent pe) =>
        pe.ClippedAtTimeEnd || (pe.Event is EventChunk<TEvent> c && c.ClippedAtTimeEnd);

    /// <summary>
    /// Return the consumer's authoritative <typeparamref name="TEvent"/> behind the supplied
    /// <see cref="ICalendarEvent"/>: the underlying <see cref="Event"/> when <paramref name="ev"/>
    /// is an <see cref="EventChunk{TEvent}"/> wrapper, otherwise a direct cast.
    /// </summary>
    public static TEvent Unwrap(ICalendarEvent ev) =>
        ev is EventChunk<TEvent> chunk ? chunk.Event : (TEvent)ev;
}
