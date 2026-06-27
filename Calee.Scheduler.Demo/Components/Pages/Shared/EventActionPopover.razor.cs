#nullable enable
using System.Globalization;
using Calee.Scheduler.Contracts;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.JSInterop;

namespace Calee.Scheduler.Demo.Components.Pages.Shared;

/// <summary>
/// Demo-only calendar-style click-popover. Opens next to the clicked
/// chip with Edit + Delete actions; the parent page wires
/// <see cref="OnEditRequested"/> to its existing <c>EventEditorDialog</c>
/// flow and <see cref="OnDeleteRequested"/> to its existing delete handler.
/// Per ADR-0010 the library ships no popovers — this lives entirely in the
/// demo as one consumer-rendered action menu pattern.
/// </summary>
public partial class EventActionPopover : ComponentBase, IAsyncDisposable
{
    [Inject] private IJSRuntime JS { get; set; } = default!;

    /// <summary>Whether the popover is rendered. Caller toggles this from its own state.</summary>
    [Parameter] public bool IsVisible { get; set; }

    /// <summary>The event the popover is acting on. The popover is generic across
    /// <see cref="CalendarEvent"/> and <c>SchedulerSeed.FleetEvent</c> via the
    /// <see cref="ICalendarEvent"/> interface (no <c>TEvent</c> generic parameter
    /// needed — the popover only reads the four read-only properties).</summary>
    [Parameter] public ICalendarEvent? Event { get; set; }

    /// <summary>Time zone the popover formats <see cref="ICalendarEvent.Start"/> and
    /// <c>End</c> in. Defaults to <see cref="TimeZoneInfo.Local"/> when unset.</summary>
    [Parameter] public TimeZoneInfo? TimeZone { get; set; }

    /// <summary>Fires when the user clicks Edit. The caller opens its own
    /// <c>EventEditorDialog</c> with the event pre-filled.</summary>
    [Parameter] public EventCallback<ICalendarEvent> OnEditRequested { get; set; }

    /// <summary>Fires when the user clicks Delete. The caller removes the event
    /// (typically via the same path the library's Delete key handler triggers).</summary>
    [Parameter] public EventCallback<ICalendarEvent> OnDeleteRequested { get; set; }

    /// <summary>Fires when the popover dismisses without acting (Esc, outside click,
    /// or the X button). The caller sets its <c>_popoverVisible = false</c> field.</summary>
    [Parameter] public EventCallback OnDismissed { get; set; }

    /// <summary>JS module path — kept here as a constant so the wwwroot/js layout
    /// is documented in exactly one place. The path is relative to the app's
    /// <c>&lt;base href&gt;</c> (Calee.Scheduler.Demo/wwwroot/), not the library's
    /// _content folder (this script lives in the demo, not the library). The
    /// leading "./" keeps it working when the app is served from a sub-path
    /// (e.g. GitHub Pages at /calee-scheduler/) rather than the domain root.</summary>
    private const string JsModulePath = "./js/event-popover.js";

    /// <summary>Default to a position that reads "centered-ish" before the first
    /// anchor measurement comes back from JS — keeps the popover visible even
    /// when the JS module fails to load (e.g., during prerender).</summary>
    private const string FallbackInlineStyle =
        "top: 50%; left: 50%; transform: translate(-50%, -50%);";

    /// <summary>Gap between the chip's edge and the popover's edge in the anchor
    /// computation.</summary>
    private const double AnchorGapPx = 8.0;

    /// <summary>Popover footprint — used to flip / clamp during anchor compute.
    /// The CSS sets a fixed width (288 px / w-72-ish) and a height that comes
    /// out around 132 px for the default content; these are nominal values for
    /// the layout math. The final on-screen size is whatever CSS resolves it
    /// to; the clamp below tolerates slight overflow by keeping the popover at
    /// least 8 px inside each viewport edge.</summary>
    private const double NominalWidthPx = 288.0;
    private const double NominalHeightPx = 132.0;
    private const double ViewportMarginPx = 8.0;

    private ElementReference _popoverRef;
    private ElementReference _editButtonRef;
    private ElementReference _closeButtonRef;

    private IJSObjectReference? _module;

    private string? _previousTitleForFocusRestore;
    private bool _previouslyVisible;
    private bool _needsAnchorAndFocus;
    private bool _needsFocusAfterPosition;
    private bool _positionReady = true;
    private string _inlineStyle = FallbackInlineStyle;

    private string PopoverInlineStyle =>
        _positionReady ? _inlineStyle : $"{_inlineStyle} visibility: hidden;";

    protected override void OnParametersSet()
    {
        // Transition: hidden -> visible. Remember the event's title so we can
        // restore focus to the chip on close (the chip's accessible name
        // starts with the title). Schedule the anchor + focus pass for the
        // next OnAfterRenderAsync, when the popover element is in the DOM.
        if (IsVisible && !_previouslyVisible)
        {
            _previousTitleForFocusRestore = Event?.Title;
            _needsAnchorAndFocus = true;
            _needsFocusAfterPosition = false;
            _positionReady = false;
            // Reset to a placeholder until JS hands back the chip rect.
            _inlineStyle = FallbackInlineStyle;
        }
        _previouslyVisible = IsVisible;
    }

    protected override async Task OnAfterRenderAsync(bool firstRender)
    {
        if (firstRender)
        {
            await EnsureModuleAsync();
            if (_module is not null)
            {
                try
                {
                    await _module.InvokeVoidAsync("ensureTracking");
                }
                catch (JSException)
                {
                    // Prerender/static render can skip tracking; the popover
                    // still works with its centered fallback.
                }
                catch (JSDisconnectedException)
                {
                    // Circuit is going away.
                }
            }
        }

        if (!IsVisible || !_needsAnchorAndFocus)
        {
            if (IsVisible && _positionReady && _needsFocusAfterPosition)
            {
                _needsFocusAfterPosition = false;
                try
                {
                    await _editButtonRef.FocusAsync();
                }
                catch
                {
                    // Edit button may not be attached yet on a slow render.
                }
            }
            return;
        }
        _needsAnchorAndFocus = false;

        await EnsureModuleAsync();
        await AnchorToLastChipAsync();
        _needsFocusAfterPosition = true;
    }

    private async Task EnsureModuleAsync()
    {
        if (_module is not null)
        {
            return;
        }
        try
        {
            _module = await JS.InvokeAsync<IJSObjectReference>("import", JsModulePath);
        }
        catch (JSException)
        {
            // Prerender or static-render environment — no JS available. The
            // popover renders centered via FallbackInlineStyle and the user
            // can still interact with it; we just skip anchoring.
        }
        catch (InvalidOperationException)
        {
            // IJSRuntime not available (server pre-render).
        }
    }

    private async Task AnchorToLastChipAsync()
    {
        if (_module is null)
        {
            _inlineStyle = FallbackInlineStyle;
            _positionReady = true;
            StateHasChanged();
            return;
        }

        ChipRect? rect;
        try
        {
            rect = await _module.InvokeAsync<ChipRect?>("consumeLastChipRect");
        }
        catch (JSException)
        {
            rect = null;
        }
        catch (JSDisconnectedException)
        {
            rect = null;
        }

        if (rect is null)
        {
            _inlineStyle = FallbackInlineStyle;
            _positionReady = true;
            StateHasChanged();
            return;
        }

        _inlineStyle = ComputeAnchorStyle(rect.Value);
        _positionReady = true;
        StateHasChanged();
    }

    /// <summary>Pure positioning math — unit-testable in principle, but kept inline
    /// since the popover is demo-only. Default: right of the chip with an 8 px
    /// gap. If that overflows the viewport right edge, flip to the left. If
    /// both sides overflow (narrow viewport), drop below the chip horizontally
    /// centered. Final clamp keeps the popover at least 8 px inside each
    /// viewport edge.</summary>
    internal static string ComputeAnchorStyle(ChipRect rect)
    {
        var width = NominalWidthPx;
        var height = NominalHeightPx;
        double top = rect.Top;
        double left;

        var rightOverflow = rect.Right + AnchorGapPx + width > rect.ViewportWidth;
        var leftOverflow = rect.Left - AnchorGapPx - width < 0;

        if (!rightOverflow)
        {
            left = rect.Right + AnchorGapPx;
        }
        else if (!leftOverflow)
        {
            left = rect.Left - AnchorGapPx - width;
        }
        else
        {
            // Both sides overflow — drop below the chip, horizontally centered.
            left = rect.Left + rect.Width / 2.0 - width / 2.0;
            top = rect.Bottom + AnchorGapPx;
        }

        // Clamp horizontally and vertically inside the viewport.
        left = Math.Max(ViewportMarginPx,
            Math.Min(left, rect.ViewportWidth - width - ViewportMarginPx));
        top = Math.Max(ViewportMarginPx,
            Math.Min(top, rect.ViewportHeight - height - ViewportMarginPx));

        return string.Format(
            CultureInfo.InvariantCulture,
            "top: {0:F2}px; left: {1:F2}px;",
            top,
            left);
    }

    /// <summary>Format the popover's time-range subtitle. Uses the same
    /// time-zone-aware convention as the library's chip aria-label
    /// (DayView/WeekView/TimelineView) — but the library's helper is internal,
    /// so we reimplement a demo-side formatter here. All-day single-day:
    /// "All day · Mon, May 18". All-day multi-day: "All day · Mon, May 18 →
    /// Fri, May 22". Timed single-day: "Mon, May 18 · 10:00 AM – 11:00 AM".
    /// Timed multi-day: "Mon, May 18 10:00 AM → Fri, May 22 11:00 AM".</summary>
    internal string FormatTimeRange(ICalendarEvent ev)
    {
        var tz = TimeZone ?? TimeZoneInfo.Local;
        var start = TimeZoneInfo.ConvertTime(ev.Start, tz);
        var end = TimeZoneInfo.ConvertTime(ev.End, tz);
        var sameDay = start.Date == end.Date;

        if (ev.IsAllDay)
        {
            // All-day events render as a date or date range.
            return sameDay
                ? $"All day · {FormatDate(start)}"
                : $"All day · {FormatDate(start)} → {FormatDate(end)}";
        }

        return sameDay
            ? $"{FormatDate(start)} · {FormatTime(start)} – {FormatTime(end)}"
            : $"{FormatDate(start)} {FormatTime(start)} → {FormatDate(end)} {FormatTime(end)}";
    }

    private static string FormatDate(DateTimeOffset dto) =>
        dto.ToString("ddd, MMM d", CultureInfo.InvariantCulture);

    private static string FormatTime(DateTimeOffset dto) =>
        dto.ToString("h:mm tt", CultureInfo.InvariantCulture);

    private async Task HandleEditAsync()
    {
        var ev = Event;
        if (ev is not null)
        {
            await OnEditRequested.InvokeAsync(ev);
        }
        await OnDismissed.InvokeAsync();
    }

    private async Task HandleDeleteAsync()
    {
        var ev = Event;
        if (ev is not null)
        {
            await OnDeleteRequested.InvokeAsync(ev);
        }
        await OnDismissed.InvokeAsync();
    }

    private async Task HandleDismissAsync() => await DismissAsync();

    private async Task HandleBackdropClickAsync() => await DismissAsync();

    private async Task HandleKeyDownAsync(KeyboardEventArgs e)
    {
        if (e.Key == "Escape")
        {
            await DismissAsync();
        }
    }

    private async Task DismissAsync()
    {
        // Restore focus to the originating chip before notifying the parent —
        // OnDismissed will likely re-render and unmount the popover, so we
        // want the focus request issued while the popover element still
        // exists in the DOM (the chip lookup itself runs after the unmount,
        // and is fine as long as the chip is still in the DOM).
        var titleForFocus = _previousTitleForFocusRestore;
        _previousTitleForFocusRestore = null;
        await OnDismissed.InvokeAsync();
        if (titleForFocus is not null && _module is not null)
        {
            try
            {
                await _module.InvokeAsync<bool>("focusChipByTitle", titleForFocus);
            }
            catch (JSException)
            {
                // Chip might have been deleted — ignore.
            }
            catch (JSDisconnectedException)
            {
                // SignalR circuit is going away — ignore.
            }
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_module is not null)
        {
            try
            {
                await _module.DisposeAsync();
            }
            catch (JSDisconnectedException)
            {
                // Circuit gone — nothing to dispose on the JS side.
            }
            _module = null;
        }
    }

    /// <summary>Wire shape returned by the JS helper's <c>consumeLastChipRect</c>.
    /// Mirrors the JS object literal — keep field names matching.</summary>
    internal readonly record struct ChipRect(
        double Top,
        double Left,
        double Right,
        double Bottom,
        double Width,
        double Height,
        double ViewportWidth,
        double ViewportHeight);
}
