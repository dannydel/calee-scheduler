namespace Calee.Scheduler.Contracts;

/// <summary>
/// Arrangement of the twelve mini-months inside <c>CaleeSchedulerYearView&lt;TEvent&gt;</c>
/// (Phase 2 Task 16, FR-38). Per phase-2-plan §5.3 Q15 the supported layouts are five
/// fixed shapes — picking a layout that doesn't multiply to twelve is disallowed at
/// compile-time by enumerating the legal combinations here.
/// </summary>
/// <remarks>
/// The first dimension is columns, the second dimension is rows. <see cref="Grid4x3"/>
/// is the default — it matches the natural "calendar wall" layout used by most desktop
/// calendar apps.
/// </remarks>
public enum YearViewLayout
{
    /// <summary>Four columns × three rows — the default "calendar wall" layout.</summary>
    Grid4x3,

    /// <summary>Three columns × four rows — portrait variant.</summary>
    Grid3x4,

    /// <summary>Two columns × six rows — narrow viewport friendly.</summary>
    Grid2x6,

    /// <summary>Six columns × two rows — landscape strip.</summary>
    Grid6x2,

    /// <summary>Single-column stack of twelve months — narrow viewport / accessibility.</summary>
    Column,
}
