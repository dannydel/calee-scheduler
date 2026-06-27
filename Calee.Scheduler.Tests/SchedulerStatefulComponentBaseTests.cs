using Bunit;
using Calee.Scheduler.Contracts;
using Calee.Scheduler.Extensions;
using Calee.Scheduler.Internal;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Rendering;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.JSInterop;

namespace Calee.Scheduler.Tests;

/// <summary>
/// Tests for <see cref="SchedulerStatefulComponentBase{TEvent}"/> covering the
/// controllable <c>Date</c> pattern: uncontrolled seeding from "today in <c>TimeZone</c>"
/// (FR-09a), controlled-mode passthrough, and the <c>SetCurrentDateAsync</c> behavior
/// in both modes (grilling Q4's "controllable" pattern).
/// </summary>
public class SchedulerStatefulComponentBaseTests
{
    /// <summary>Concrete trivial view for tests.</summary>
    private sealed class TestStatefulView : SchedulerStatefulComponentBase<CalendarEvent>
    {
        public DateTimeOffset CurrentDateForTest => CurrentDate;

        public DateTimeOffset InternalDateForTest => _internalDate;

        public Task SetCurrentDateAsyncForTest(DateTimeOffset newDate) =>
            SetCurrentDateAsync(newDate);

        // Test-only forwarder for the base's `private protected` drag entry point.
        // Lives in a derived class in the friend assembly (InternalsVisibleTo) so
        // `private protected` is satisfied.
        public Task BeginDragOnPointerAsyncForTest(
            PointerEventArgs args,
            ElementReference element,
            DragMode mode,
            double snapPixelsX,
            double snapPixelsY,
            string ghostClass,
            Func<DropPayload, Task> onDrop,
            Func<Task> onCancel)
            => BeginDragOnPointerAsync(args, element, mode, snapPixelsX, snapPixelsY, ghostClass, onDrop, onCancel);

        protected override void BuildRenderTree(RenderTreeBuilder builder)
        {
            builder.OpenElement(0, "div");
            builder.AddContent(1, CurrentDate.ToString("O"));
            builder.CloseElement();
        }
    }

    private static BunitContext NewContext()
    {
        var ctx = new BunitContext();
        ctx.Services.AddCaleeScheduler();
        return ctx;
    }

    [Fact]
    public void Uncontrolled_DefaultsToToday_InTimeZone()
    {
        using var ctx = NewContext();
        var ny = TimeZoneInfo.FindSystemTimeZoneById("America/New_York");
        var expectedToday = TimeZoneInfo.ConvertTime(DateTimeOffset.UtcNow, ny).Date;

        var cut = ctx.Render<TestStatefulView>(p => p
            .Add(c => c.TimeZone, ny));

        Assert.Equal(expectedToday, cut.Instance.CurrentDateForTest.Date);
    }

    [Fact]
    public void Controlled_UsesPassedDate()
    {
        using var ctx = NewContext();
        var tz = TimeZoneInfo.Utc;
        var anchor = new DateTimeOffset(2026, 6, 15, 0, 0, 0, TimeSpan.Zero);

        var cut = ctx.Render<TestStatefulView>(p => p
            .Add(c => c.TimeZone, tz)
            .Add(c => c.Date, anchor));

        Assert.Equal(anchor, cut.Instance.CurrentDateForTest);
    }

    [Fact]
    public async Task SetCurrentDate_Controlled_FiresDateChangedButDoesNotMutateInternal()
    {
        using var ctx = NewContext();
        var tz = TimeZoneInfo.Utc;
        var initial = new DateTimeOffset(2026, 6, 15, 0, 0, 0, TimeSpan.Zero);
        DateTimeOffset? fired = null;

        var cut = ctx.Render<TestStatefulView>(p => p
            .Add(c => c.TimeZone, tz)
            .Add(c => c.Date, initial)
            .Add(c => c.DateChanged, EventCallback.Factory.Create<DateTimeOffset>(
                this, d => fired = d)));

        var internalBefore = cut.Instance.InternalDateForTest;
        var newDate = new DateTimeOffset(2026, 7, 1, 0, 0, 0, TimeSpan.Zero);

        await cut.InvokeAsync(() => cut.Instance.SetCurrentDateAsyncForTest(newDate));

        // DateChanged fired with the new value.
        Assert.Equal(newDate, fired);
        // _internalDate was NOT mutated — controlled mode defers state to the consumer.
        Assert.Equal(internalBefore, cut.Instance.InternalDateForTest);
    }

    [Fact]
    public async Task SetCurrentDate_Uncontrolled_UpdatesInternalState()
    {
        using var ctx = NewContext();
        var tz = TimeZoneInfo.Utc;
        DateTimeOffset? fired = null;

        var cut = ctx.Render<TestStatefulView>(p => p
            .Add(c => c.TimeZone, tz)
            .Add(c => c.DateChanged, EventCallback.Factory.Create<DateTimeOffset>(
                this, d => fired = d)));

        var newDate = new DateTimeOffset(2026, 9, 9, 0, 0, 0, TimeSpan.Zero);

        await cut.InvokeAsync(() => cut.Instance.SetCurrentDateAsyncForTest(newDate));

        // CurrentDate reflects the new value (via _internalDate, since Date param is null).
        Assert.Equal(newDate, cut.Instance.CurrentDateForTest);
        Assert.Equal(newDate, cut.Instance.InternalDateForTest);
        // DateChanged still fires so external observers can react even without @bind.
        Assert.Equal(newDate, fired);
    }

    // ---------------------------------------------------------------------------
    // Drag-infrastructure tests (Phase 2 Task 3). The base lazy-loads the JS drag
    // module on the first BeginDragOnPointerAsync call; subsequent drags reuse it.
    // We exercise the three production code paths plus the no-JS-runtime fallback:
    //   1. JS module fails to load → no-op (no exception, no callback).
    //   2. Mouse pointer → startDrag invoked immediately (no awaitLongPress).
    //   3. Touch pointer + long-press succeeds → startDrag invoked.
    //   4. Touch pointer + long-press fails → onCancel invoked; startDrag NOT.
    // ---------------------------------------------------------------------------

    private const string ModulePath = "./_content/Calee.Scheduler/calee-scheduler.js";

    /// <summary>
    /// Renders the test view with a fully-configured bUnit JSInterop module so the
    /// base's lazy <see cref="PointerDragInterop.CreateAsync"/> succeeds and we can
    /// assert on the underlying JS invocations. Returns the rendered component +
    /// the module handler so tests can register additional return values.
    /// </summary>
    private static (IRenderedComponent<TestStatefulView> Cut, BunitJSModuleInterop Module) RenderWithJsInterop(BunitContext ctx)
    {
        var module = ctx.JSInterop.SetupModule(ModulePath);
        // Cover both drag paths so any test in the group can opt either branch.
        module.Setup<string>("startDrag", _ => true).SetResult("handle-1");
        module.SetupVoid("abortDrag", _ => true).SetVoidResult();

        var cut = ctx.Render<TestStatefulView>(p => p.Add(c => c.TimeZone, TimeZoneInfo.Utc));
        return (cut, module);
    }

    [Fact]
    public async Task BeginDragOnPointerAsync_WithoutJsRuntime_NoOps_ButDoesNotThrow()
    {
        // No SetupModule — the import fails with a missing-invocation under bUnit's
        // strict mode. The base catches and returns a null wrapper, then no-ops.
        // To simulate the production "no JS runtime" fallback we use the same
        // ThrowingJSRuntime stub from PointerDragInteropTests via an inline copy
        // because we need control over the runtime injected into the component.
        using var ctx = new BunitContext();
        ctx.Services.AddCaleeScheduler();
        ctx.Services.AddSingleton<IJSRuntime>(new ThrowingJSRuntime());
        // Strict JSInterop is still on — but the only call would be the import
        // attempted by PointerDragInterop.CreateAsync, which our runtime stub
        // intercepts before bUnit ever sees it.

        var cut = ctx.Render<TestStatefulView>(p => p.Add(c => c.TimeZone, TimeZoneInfo.Utc));

        var dropFired = 0;
        var cancelFired = 0;
        var args = new PointerEventArgs { PointerType = "mouse", PointerId = 1 };

        await cut.InvokeAsync(() => cut.Instance.BeginDragOnPointerAsyncForTest(
            args, default, DragMode.Move, 0, 0, "g",
            _ => { dropFired++; return Task.CompletedTask; },
            () => { cancelFired++; return Task.CompletedTask; }));

        // Neither callback fires — the base silently no-ops when no module is loadable.
        Assert.Equal(0, dropFired);
        Assert.Equal(0, cancelFired);
    }

    [Fact]
    public async Task BeginDragOnPointerAsync_MousePointer_StartsDragImmediately()
    {
        using var ctx = NewContext();
        var (cut, module) = RenderWithJsInterop(ctx);

        var args = new PointerEventArgs { PointerType = "mouse", PointerId = 11 };

        await cut.InvokeAsync(() => cut.Instance.BeginDragOnPointerAsyncForTest(
            args, default, DragMode.Move, 0, 0, "g",
            _ => Task.CompletedTask,
            () => Task.CompletedTask));

        // Mouse path: startDrag invoked exactly once; awaitLongPress NOT invoked.
        Assert.Single(module.Invocations["startDrag"]);
        Assert.Empty(module.Invocations["awaitLongPress"]);
    }

    [Fact]
    public async Task BeginDragOnPointerAsync_TouchPointer_WaitsForLongPress()
    {
        using var ctx = NewContext();
        var (cut, module) = RenderWithJsInterop(ctx);
        module.Setup<bool>("awaitLongPress", _ => true).SetResult(true);

        var args = new PointerEventArgs { PointerType = "touch", PointerId = 22 };

        await cut.InvokeAsync(() => cut.Instance.BeginDragOnPointerAsyncForTest(
            args, default, DragMode.Move, 0, 0, "g",
            _ => Task.CompletedTask,
            () => Task.CompletedTask));

        // Touch + held: awaitLongPress fires first with the pointerId/duration/tolerance,
        // then startDrag is invoked.
        var longPress = Assert.Single(module.Invocations["awaitLongPress"]);
        Assert.Equal(22L, Convert.ToInt64(longPress.Arguments[0]));
        Assert.Equal(300, Convert.ToInt32(longPress.Arguments[1]));
        Assert.Equal(5, Convert.ToInt32(longPress.Arguments[2]));
        Assert.Single(module.Invocations["startDrag"]);
    }

    [Fact]
    public async Task BeginDragOnPointerAsync_TouchPointer_LongPressFails_InvokesCancel()
    {
        using var ctx = NewContext();
        var (cut, module) = RenderWithJsInterop(ctx);
        module.Setup<bool>("awaitLongPress", _ => true).SetResult(false);

        var args = new PointerEventArgs { PointerType = "touch", PointerId = 33 };

        var dropFired = 0;
        var cancelFired = 0;

        await cut.InvokeAsync(() => cut.Instance.BeginDragOnPointerAsyncForTest(
            args, default, DragMode.Move, 0, 0, "g",
            _ => { dropFired++; return Task.CompletedTask; },
            () => { cancelFired++; return Task.CompletedTask; }));

        // Failed long-press: onCancel fires; startDrag is NOT invoked.
        Assert.Equal(0, dropFired);
        Assert.Equal(1, cancelFired);
        Assert.Single(module.Invocations["awaitLongPress"]);
        Assert.Empty(module.Invocations["startDrag"]);
    }

    /// <summary>Minimal IJSRuntime stub whose import throws JSException — the production
    /// failure mode the wrapper degrades from. Mirrors the stub in PointerDragInteropTests
    /// (kept inline so each test file is self-contained).</summary>
    private sealed class ThrowingJSRuntime : IJSRuntime
    {
        public ValueTask<TValue> InvokeAsync<TValue>(string identifier, object?[]? args) =>
            throw new JSException("simulated import failure");

        public ValueTask<TValue> InvokeAsync<TValue>(string identifier,
            System.Threading.CancellationToken cancellationToken, object?[]? args) =>
            throw new JSException("simulated import failure");
    }
}
