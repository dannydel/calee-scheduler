namespace Calee.Scheduler.Contracts;

/// <summary>
/// Default <see cref="ILane"/> implementation suitable for demos and simple
/// consumer scenarios. Consumers with richer domain types (e.g., a <c>Driver</c>
/// with phone/photo/etc.) implement <see cref="ILane"/> directly on their own
/// type instead of wrapping into this record. See ADR-0011.
/// </summary>
/// <param name="Id">Stable identifier matched against the view's <c>LaneKey</c> projection.</param>
/// <param name="Name">Display name shown in the lane row header.</param>
/// <param name="Color">Optional CSS color tint; <see langword="null"/> falls back to the theme default.</param>
public sealed record Lane(
    string Id,
    string Name,
    string? Color = null
) : ILane;
