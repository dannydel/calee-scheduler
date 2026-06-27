namespace Calee.Scheduler.Tests.Accessibility;

/// <summary>
/// Programmatic WCAG 2.1 AA contrast verification for the library's default theme
/// tokens (NFR-06, PRD Task 14).
///
/// The hex values asserted here come directly from <c>CaleeScheduler.razor.css</c>
/// and the per-view <c>*.razor.css</c> files. Any change to a default token must
/// either keep contrast at or above the threshold below, or update this test
/// deliberately — the test exists to flag accidental regressions in the default
/// palette.
///
/// WCAG 2.1 AA thresholds applied (Success Criteria 1.4.3, 1.4.11):
/// <list type="bullet">
///   <item>Normal body text: ≥ 4.5:1</item>
///   <item>Large text (≥ 18pt, or ≥ 14pt bold): ≥ 3.0:1</item>
///   <item>Non-text UI components (button borders, focus outlines, icon glyphs): ≥ 3.0:1</item>
/// </list>
///
/// Relative-luminance + contrast formulas: https://www.w3.org/TR/WCAG21/#dfn-contrast-ratio
/// </summary>
public class DefaultThemeContrastTests
{
    private const double NormalTextRatio = 4.5;
    private const double LargeTextRatio = 3.0;
    private const double NonTextRatio = 3.0;

    [Theory]
    // Root + view body text (#18181b) on every background it lands on.
    [InlineData("body text on root bg", "#18181b", "#ffffff", NormalTextRatio)]
    [InlineData("body text on gutter", "#18181b", "#fafafa", NormalTextRatio)]
    [InlineData("body text on all-day row", "#18181b", "#fdfdfd", NormalTextRatio)]
    [InlineData("body text on today bg", "#18181b", "#eef4ff", NormalTextRatio)]
    [InlineData("body text on muted-cell bg", "#18181b", "#fafafa", NormalTextRatio)]
    [InlineData("body text on overflow chip", "#18181b", "#f4f4f5", NormalTextRatio)]
    [InlineData("body text on unassigned row", "#52525b", "#f4f4f5", NormalTextRatio)]
    // Year view out-of-month ("muted") day numbers on the white cell background.
    [InlineData("year muted date on cell bg", "#71717a", "#ffffff", NormalTextRatio)]
    // Subtle text (#52525b) — date secondary labels, time-gutter labels, event-time strip.
    [InlineData("subtle text on gutter", "#52525b", "#fafafa", NormalTextRatio)]
    [InlineData("subtle text on root bg", "#52525b", "#ffffff", NormalTextRatio)]
    [InlineData("subtle text on today bg", "#52525b", "#eef4ff", NormalTextRatio)]
    [InlineData("subtle text on all-day row", "#52525b", "#fdfdfd", NormalTextRatio)]
    // Toolbar palette (declared on .calee-scheduler-toolbar).
    [InlineData("toolbar text on toolbar bg", "#18181b", "#ffffff", NormalTextRatio)]
    [InlineData("toolbar text on button bg", "#18181b", "#f4f4f5", NormalTextRatio)]
    [InlineData("toolbar text on button hover bg", "#18181b", "#e4e4e7", NormalTextRatio)]
    [InlineData("toolbar text on active view btn", "#18181b", "#dbeafe", NormalTextRatio)]
    // Non-text contrast — focus outline must stand out against every background it draws on.
    [InlineData("focus outline on root bg", "#2563eb", "#ffffff", NonTextRatio)]
    [InlineData("focus outline on gutter", "#2563eb", "#fafafa", NonTextRatio)]
    [InlineData("focus outline on today bg", "#2563eb", "#eef4ff", NonTextRatio)]
    [InlineData("focus outline on muted-cell", "#2563eb", "#fafafa", NonTextRatio)]
    // Non-text contrast — current-time indicator vs surrounding bg.
    [InlineData("current-time on root bg", "#dc2626", "#ffffff", NonTextRatio)]
    [InlineData("current-time on today bg", "#dc2626", "#eef4ff", NonTextRatio)]
    public void Default_Theme_Pair_Meets_Threshold(
        string label, string foreground, string background, double minimumRatio)
    {
        var ratio = ComputeContrast(foreground, background);
        Assert.True(
            ratio >= minimumRatio,
            $"[{label}] {foreground} on {background} = {ratio:F2}:1 (need ≥ {minimumRatio:F1}:1)");
    }

    /// <summary>
    /// The month-view muted-day text is rendered as `color: #18181b` with `opacity:
    /// var(--calee-scheduler-month-cell-muted-opacity)` on the muted cell's `#fafafa`
    /// background. The effective foreground color is therefore the alpha-blend of the
    /// declared color over the background.
    /// </summary>
    [Fact]
    public void Muted_Day_Composite_Color_Meets_Body_Text_Contrast()
    {
        // The token default. If this changes in CaleeSchedulerMonthView.razor.css the test
        // must change too — and the new default must still satisfy the threshold.
        const double mutedOpacity = 0.65;

        var foreground = HexToRgb("#18181b");
        var background = HexToRgb("#fafafa");
        var composite = AlphaBlend(foreground, mutedOpacity, background);

        var ratio = ComputeContrast(composite, background);
        Assert.True(
            ratio >= NormalTextRatio,
            $"Muted-day composite (opacity {mutedOpacity}) yields ratio {ratio:F2}:1 — need ≥ {NormalTextRatio:F1}:1");
    }

    // ----- WCAG contrast math --------------------------------------------------------

    private static double ComputeContrast(string foregroundHex, string backgroundHex)
        => ComputeContrast(HexToRgb(foregroundHex), HexToRgb(backgroundHex));

    private static double ComputeContrast((double R, double G, double B) fg, (double R, double G, double B) bg)
    {
        var lFg = RelativeLuminance(fg);
        var lBg = RelativeLuminance(bg);
        var (l1, l2) = lFg > lBg ? (lFg, lBg) : (lBg, lFg);
        return (l1 + 0.05) / (l2 + 0.05);
    }

    // WCAG 2.x relative-luminance formula.
    private static double RelativeLuminance((double R, double G, double B) c)
    {
        double F(double channel)
        {
            channel /= 255.0;
            return channel <= 0.03928
                ? channel / 12.92
                : Math.Pow((channel + 0.055) / 1.055, 2.4);
        }
        return 0.2126 * F(c.R) + 0.7152 * F(c.G) + 0.0722 * F(c.B);
    }

    private static (double R, double G, double B) HexToRgb(string hex)
    {
        var h = hex.TrimStart('#');
        if (h.Length != 6)
        {
            throw new ArgumentException($"Expected #RRGGBB, got {hex}", nameof(hex));
        }
        return (
            Convert.ToInt32(h.Substring(0, 2), 16),
            Convert.ToInt32(h.Substring(2, 2), 16),
            Convert.ToInt32(h.Substring(4, 2), 16));
    }

    /// <summary>
    /// Alpha-blend a foreground over an opaque background, matching the CSS `opacity`
    /// composite that the browser performs at paint time.
    /// </summary>
    private static (double R, double G, double B) AlphaBlend(
        (double R, double G, double B) fg, double alpha, (double R, double G, double B) bg) =>
        (
            alpha * fg.R + (1 - alpha) * bg.R,
            alpha * fg.G + (1 - alpha) * bg.G,
            alpha * fg.B + (1 - alpha) * bg.B
        );
}
