using Bunit;
using Calee.Scheduler.Internal;
using Microsoft.JSInterop;

namespace Calee.Scheduler.Tests;

/// <summary>
/// Tests for <see cref="PointerDragInterop"/> (Phase 2 Task 2, ADR-0015). The
/// JS-side behavior of the pointer-drag module is exercised manually (browser
/// + cross-browser pass in Task 19); xUnit + bUnit don't have a real DOM with
/// pointer events. What we CAN verify here:
///
/// <list type="bullet">
///   <item><description>The wrapper loads the JS module via <c>import</c> and disposes it cleanly.</description></item>
///   <item><description>The argument object passed to <c>startDrag</c> carries the correct mode string,
///     snap pixels, ghost class, callback method names, and the DotNetObjectReference.</description></item>
///   <item><description><c>OnDropAsync</c> / <c>OnCancelAsync</c> route to the registered callback exactly once
///     and clear internal state so a second call does not double-fire.</description></item>
///   <item><description>Starting a drag while one is active aborts the prior drag and fires no callbacks for it.</description></item>
///   <item><description><c>DisposeAsync</c> aborts the active drag and disposes the module + DotNet reference.</description></item>
///   <item><description><c>CreateAsync</c> returns null when the JS module load fails (test-env fallback).</description></item>
/// </list>
/// </summary>
public class PointerDragInteropTests
{
    private const string ModulePath = "./_content/Calee.Scheduler/calee-scheduler.js";

    [Fact]
    public async Task CreateAsync_WhenJsModuleLoadFails_ReturnsNull()
    {
        // Production failure mode: the JS runtime throws JSException (e.g., the
        // module file is missing in a deployed environment, or the prerender pass
        // happens before JS interop is wired). TryLoadJsModuleAsync catches both
        // JSException and InvalidOperationException; CreateAsync forwards null.
        var failingRuntime = new ThrowingJSRuntime();
        var interop = await PointerDragInterop.CreateAsync(failingRuntime);
        Assert.Null(interop);
    }

    /// <summary>Minimal IJSRuntime stub whose import throws JSException — the production
    /// failure mode the wrapper degrades from.</summary>
    private sealed class ThrowingJSRuntime : IJSRuntime
    {
        public ValueTask<TValue> InvokeAsync<TValue>(string identifier, object?[]? args) =>
            throw new JSException("simulated import failure");

        public ValueTask<TValue> InvokeAsync<TValue>(string identifier,
            System.Threading.CancellationToken cancellationToken, object?[]? args) =>
            throw new JSException("simulated import failure");
    }

    [Fact]
    public async Task StartDragAsync_PassesCorrectOptionsObjectToJs()
    {
        using var ctx = new BunitContext();
        var moduleHandler = ctx.JSInterop.SetupModule(ModulePath);
        // startDrag returns the handle string the wrapper tracks.
        moduleHandler.Setup<string>("startDrag", _ => true).SetResult("handle-abc");
        // DisposeAsync will abort the active drag on its way out — register so
        // bUnit's strict-mode JSInterop accepts the invocation.
        moduleHandler.SetupVoid("abortDrag", _ => true).SetVoidResult();

        var interop = await PointerDragInterop.CreateAsync(ctx.JSInterop.JSRuntime);
        Assert.NotNull(interop);

        await using (interop!)
        {
            await interop.StartDragAsync(
                elementRef: default,
                mode: DragMode.ResizeEnd,
                snapPixelsX: 12,
                snapPixelsY: 34,
                ghostClass: "my-ghost",
                onDrop: _ => Task.CompletedTask,
                onCancel: () => Task.CompletedTask,
                resizeAxis: ResizeAxis.Y);

            // Module handler captures the actual InvokeAsync<string>("startDrag", elementRef, options).
            var invocation = moduleHandler.Invocations["startDrag"].Single();
            Assert.Equal("startDrag", invocation.Identifier);
            // Args: [elementRef, optionsAnonymous]
            Assert.Equal(2, invocation.Arguments.Count);

            // The options object is anonymous; reflect into it for assertions.
            var options = invocation.Arguments[1];
            Assert.NotNull(options);
            var type = options!.GetType();
            string ReadString(string name) =>
                (string)type.GetProperty(name)!.GetValue(options)!;
            double ReadDouble(string name) =>
                Convert.ToDouble(type.GetProperty(name)!.GetValue(options)!);

            Assert.Equal("resize-end", ReadString("mode"));
            Assert.Equal("y", ReadString("axis"));
            Assert.Equal("my-ghost", ReadString("ghostClass"));
            Assert.Equal("OnDropAsync", ReadString("onDropMethodName"));
            Assert.Equal("OnCancelAsync", ReadString("onCancelMethodName"));
            Assert.Equal(12, ReadDouble("snapPixelsX"));
            Assert.Equal(34, ReadDouble("snapPixelsY"));

            // The dotnetRef property is a DotNetObjectReference<PointerDragInterop>; we don't
            // unwrap the value, just verify it's present and the right shape.
            var dotnetRefValue = type.GetProperty("dotnetRef")!.GetValue(options);
            Assert.NotNull(dotnetRefValue);
            Assert.IsAssignableFrom<DotNetObjectReference<PointerDragInterop>>(dotnetRefValue);
        }
    }

    [Theory]
    [InlineData("Move", "move")]
    public async Task StartDragAsync_TranslatesDragModeToExpectedString(string modeName, string expected)
    {
        // DragMode is internal — xUnit theory data must be public-API-compatible, so the
        // mode is supplied by name and parsed here. ResizeEnd has its own theory
        // (axis-emitting path); CreateRegion has a dedicated test below since it requires
        // additional anchor + axis options that the simple no-options branch can't supply.
        var mode = Enum.Parse<DragMode>(modeName);

        using var ctx = new BunitContext();
        var moduleHandler = ctx.JSInterop.SetupModule(ModulePath);
        moduleHandler.Setup<string>("startDrag", _ => true).SetResult("h");
        moduleHandler.SetupVoid("abortDrag", _ => true).SetVoidResult();

        var interop = await PointerDragInterop.CreateAsync(ctx.JSInterop.JSRuntime);
        Assert.NotNull(interop);
        await using var _ = interop;

        await interop.StartDragAsync(default, mode, 0, 0, "g",
            _ => Task.CompletedTask, () => Task.CompletedTask);

        var options = moduleHandler.Invocations["startDrag"].Single().Arguments[1]!;
        var actualMode = (string)options.GetType().GetProperty("mode")!.GetValue(options)!;
        Assert.Equal(expected, actualMode);
    }

    [Fact]
    public async Task StartDragAsync_CreateRegion_EmitsAxisAndAnchorAndThresholdOptions()
    {
        // CreateRegion has its own branch in the wrapper because the JS module requires
        // the axis option (which way the ghost grows) AND the anchor coordinates (where
        // the ghost is anchored — there's no source element to clone). This test pins
        // the C#-emitted option shape so consumer-facing drag-to-create UX stays stable.
        using var ctx = new BunitContext();
        var moduleHandler = ctx.JSInterop.SetupModule(ModulePath);
        moduleHandler.Setup<string>("startDrag", _ => true).SetResult("h");
        moduleHandler.SetupVoid("abortDrag", _ => true).SetVoidResult();

        var interop = await PointerDragInterop.CreateAsync(ctx.JSInterop.JSRuntime);
        Assert.NotNull(interop);
        await using var _ = interop;

        await interop.StartDragAsync(
            elementRef: default,
            mode: DragMode.CreateRegion,
            snapPixelsX: 25,
            snapPixelsY: 28,
            ghostClass: "g",
            onDrop: _ => Task.CompletedTask,
            onCancel: () => Task.CompletedTask,
            resizeAxis: ResizeAxis.Y,
            anchorViewportX: 100,
            anchorViewportY: 200,
            thresholdPx: 5);

        var options = moduleHandler.Invocations["startDrag"].Single().Arguments[1]!;
        var type = options.GetType();
        string ReadString(string name) => (string)type.GetProperty(name)!.GetValue(options)!;
        double ReadDouble(string name) => Convert.ToDouble(type.GetProperty(name)!.GetValue(options)!);
        int ReadInt(string name) => Convert.ToInt32(type.GetProperty(name)!.GetValue(options)!);

        Assert.Equal("create-region", ReadString("mode"));
        Assert.Equal("y", ReadString("axis"));
        Assert.Equal(100, ReadDouble("anchorX"));
        Assert.Equal(200, ReadDouble("anchorY"));
        Assert.Equal(5, ReadInt("thresholdPx"));
        Assert.Equal(25, ReadDouble("snapPixelsX"));
        Assert.Equal(28, ReadDouble("snapPixelsY"));
    }

    [Fact]
    public async Task StartDragAsync_CreateRegion_WithoutAxis_Throws()
    {
        using var ctx = new BunitContext();
        var moduleHandler = ctx.JSInterop.SetupModule(ModulePath);
        moduleHandler.Setup<string>("startDrag", _ => true).SetResult("h");
        moduleHandler.SetupVoid("abortDrag", _ => true).SetVoidResult();

        var interop = (await PointerDragInterop.CreateAsync(ctx.JSInterop.JSRuntime))!;
        await using var _ = interop;

        await Assert.ThrowsAsync<ArgumentException>(() => interop.StartDragAsync(
            default, DragMode.CreateRegion, 0, 0, "g",
            _ => Task.CompletedTask, () => Task.CompletedTask,
            resizeAxis: null,
            anchorViewportX: 10, anchorViewportY: 10));
    }

    [Fact]
    public async Task StartDragAsync_CreateRegion_WithoutAnchor_Throws()
    {
        using var ctx = new BunitContext();
        var moduleHandler = ctx.JSInterop.SetupModule(ModulePath);
        moduleHandler.Setup<string>("startDrag", _ => true).SetResult("h");
        moduleHandler.SetupVoid("abortDrag", _ => true).SetVoidResult();

        var interop = (await PointerDragInterop.CreateAsync(ctx.JSInterop.JSRuntime))!;
        await using var _ = interop;

        await Assert.ThrowsAsync<ArgumentException>(() => interop.StartDragAsync(
            default, DragMode.CreateRegion, 0, 0, "g",
            _ => Task.CompletedTask, () => Task.CompletedTask,
            resizeAxis: ResizeAxis.Y,
            anchorViewportX: null, anchorViewportY: null));
    }

    [Theory]
    [InlineData("X", "x")]
    [InlineData("Y", "y")]
    public async Task StartDragAsync_ResizeEnd_EmitsAxisOptionMatchingResizeAxis(string axisName, string expectedAxis)
    {
        // The axis option is only emitted for mode='resize-end' (the JS side validates
        // its presence + value). This test pins the C#-emitted string per axis enum value.
        var axis = Enum.Parse<ResizeAxis>(axisName);

        using var ctx = new BunitContext();
        var moduleHandler = ctx.JSInterop.SetupModule(ModulePath);
        moduleHandler.Setup<string>("startDrag", _ => true).SetResult("h");
        moduleHandler.SetupVoid("abortDrag", _ => true).SetVoidResult();

        var interop = await PointerDragInterop.CreateAsync(ctx.JSInterop.JSRuntime);
        Assert.NotNull(interop);
        await using var _ = interop;

        await interop.StartDragAsync(default, DragMode.ResizeEnd, 0, 0, "g",
            _ => Task.CompletedTask, () => Task.CompletedTask, axis);

        var options = moduleHandler.Invocations["startDrag"].Single().Arguments[1]!;
        var modeStr = (string)options.GetType().GetProperty("mode")!.GetValue(options)!;
        var axisStr = (string)options.GetType().GetProperty("axis")!.GetValue(options)!;
        Assert.Equal("resize-end", modeStr);
        Assert.Equal(expectedAxis, axisStr);
    }

    [Fact]
    public async Task StartDragAsync_ResizeEnd_WithoutResizeAxis_Throws()
    {
        // Defensive guard in the wrapper: ResizeEnd needs an axis to render the
        // ghost. Calling without one is a coding bug, not a runtime degradation.
        using var ctx = new BunitContext();
        var moduleHandler = ctx.JSInterop.SetupModule(ModulePath);
        moduleHandler.Setup<string>("startDrag", _ => true).SetResult("h");
        moduleHandler.SetupVoid("abortDrag", _ => true).SetVoidResult();

        var interop = await PointerDragInterop.CreateAsync(ctx.JSInterop.JSRuntime);
        Assert.NotNull(interop);
        await using var _ = interop;

        await Assert.ThrowsAsync<ArgumentException>(() => interop.StartDragAsync(
            default, DragMode.ResizeEnd, 0, 0, "g",
            _ => Task.CompletedTask, () => Task.CompletedTask));
    }

    [Fact]
    public async Task OnDropAsync_InvokesRegisteredCallbackAndClearsState()
    {
        using var ctx = new BunitContext();
        var moduleHandler = ctx.JSInterop.SetupModule(ModulePath);
        moduleHandler.Setup<string>("startDrag", _ => true).SetResult("handle-1");
        moduleHandler.SetupVoid("abortDrag", _ => true).SetVoidResult();

        var interop = (await PointerDragInterop.CreateAsync(ctx.JSInterop.JSRuntime))!;
        await using var _ = interop;

        var callCount = 0;
        DropPayload? captured = null;
        await interop.StartDragAsync(default, DragMode.Move, 0, 0, "g",
            payload => { callCount++; captured = payload; return Task.CompletedTask; },
            () => Task.CompletedTask);

        var payload = new DropPayload(100, 200, 10, 20, "move");
        await interop.OnDropAsync(payload);

        Assert.Equal(1, callCount);
        Assert.Equal(payload, captured);

        // A second OnDropAsync after state was cleared must not re-fire the original callback.
        await interop.OnDropAsync(new DropPayload(1, 2, 3, 4, "move"));
        Assert.Equal(1, callCount);
    }

    [Fact]
    public async Task OnCancelAsync_InvokesRegisteredCallbackAndClearsState()
    {
        using var ctx = new BunitContext();
        var moduleHandler = ctx.JSInterop.SetupModule(ModulePath);
        moduleHandler.Setup<string>("startDrag", _ => true).SetResult("handle-1");
        moduleHandler.SetupVoid("abortDrag", _ => true).SetVoidResult();

        var interop = (await PointerDragInterop.CreateAsync(ctx.JSInterop.JSRuntime))!;
        await using var _ = interop;

        var dropCalls = 0;
        var cancelCalls = 0;
        await interop.StartDragAsync(default, DragMode.Move, 0, 0, "g",
            _ => { dropCalls++; return Task.CompletedTask; },
            () => { cancelCalls++; return Task.CompletedTask; });

        await interop.OnCancelAsync();

        Assert.Equal(1, cancelCalls);
        Assert.Equal(0, dropCalls);

        // A second cancel must not re-fire.
        await interop.OnCancelAsync();
        Assert.Equal(1, cancelCalls);
    }

    [Fact]
    public async Task StartDragAsync_WhileActive_AbortsPreviousAndStartsNewWithoutFiringCallbacks()
    {
        using var ctx = new BunitContext();
        var moduleHandler = ctx.JSInterop.SetupModule(ModulePath);
        // Both starts return the same handle string for simplicity — what matters
        // is that the prior drag's handle was passed to abortDrag.
        moduleHandler.Setup<string>("startDrag", _ => true).SetResult("handle-1");
        moduleHandler.SetupVoid("abortDrag", _ => true).SetVoidResult();

        var interop = (await PointerDragInterop.CreateAsync(ctx.JSInterop.JSRuntime))!;
        await using var _ = interop;

        var firstDropCalls = 0;
        var firstCancelCalls = 0;
        await interop.StartDragAsync(default, DragMode.Move, 0, 0, "g",
            _ => { firstDropCalls++; return Task.CompletedTask; },
            () => { firstCancelCalls++; return Task.CompletedTask; });

        var secondDropCalls = 0;
        await interop.StartDragAsync(default, DragMode.Move, 0, 0, "g",
            _ => { secondDropCalls++; return Task.CompletedTask; },
            () => Task.CompletedTask);

        // The first drag's handle must have been aborted via the JS module.
        var abortCalls = moduleHandler.Invocations["abortDrag"];
        Assert.Single(abortCalls);
        Assert.Equal("handle-1", abortCalls[0].Arguments[0]);

        // Neither of the first drag's callbacks should have fired during the swap.
        Assert.Equal(0, firstDropCalls);
        Assert.Equal(0, firstCancelCalls);

        // Drop the second drag — only the second's onDrop fires.
        await interop.OnDropAsync(new DropPayload(0, 0, 0, 0, "move"));
        Assert.Equal(0, firstDropCalls);
        Assert.Equal(1, secondDropCalls);
    }

    [Fact]
    public async Task AbortDragAsync_AbortsActiveDragAndFiresNoCallback()
    {
        using var ctx = new BunitContext();
        var moduleHandler = ctx.JSInterop.SetupModule(ModulePath);
        moduleHandler.Setup<string>("startDrag", _ => true).SetResult("handle-1");
        moduleHandler.SetupVoid("abortDrag", _ => true).SetVoidResult();

        var interop = (await PointerDragInterop.CreateAsync(ctx.JSInterop.JSRuntime))!;
        await using var _ = interop;

        var dropCalls = 0;
        var cancelCalls = 0;
        await interop.StartDragAsync(default, DragMode.Move, 0, 0, "g",
            _ => { dropCalls++; return Task.CompletedTask; },
            () => { cancelCalls++; return Task.CompletedTask; });

        await interop.AbortDragAsync();

        var abortCalls = moduleHandler.Invocations["abortDrag"];
        Assert.Single(abortCalls);
        Assert.Equal("handle-1", abortCalls[0].Arguments[0]);
        // Neither callback fires when C# initiated the abort.
        Assert.Equal(0, dropCalls);
        Assert.Equal(0, cancelCalls);

        // Idempotency: a second abort when no drag is active is a no-op.
        await interop.AbortDragAsync();
        Assert.Single(moduleHandler.Invocations["abortDrag"]);
    }

    [Fact]
    public async Task DisposeAsync_AbortsActiveDragAndIsIdempotent()
    {
        using var ctx = new BunitContext();
        var moduleHandler = ctx.JSInterop.SetupModule(ModulePath);
        moduleHandler.Setup<string>("startDrag", _ => true).SetResult("handle-1");
        moduleHandler.SetupVoid("abortDrag", _ => true).SetVoidResult();

        var interop = (await PointerDragInterop.CreateAsync(ctx.JSInterop.JSRuntime))!;

        await interop.StartDragAsync(default, DragMode.Move, 0, 0, "g",
            _ => Task.CompletedTask, () => Task.CompletedTask);

        await interop.DisposeAsync();

        // Dispose must have aborted the active drag.
        var abortCalls = moduleHandler.Invocations["abortDrag"];
        Assert.Single(abortCalls);
        Assert.Equal("handle-1", abortCalls[0].Arguments[0]);

        // A second DisposeAsync is a no-op (idempotent).
        await interop.DisposeAsync();
        Assert.Single(moduleHandler.Invocations["abortDrag"]);
    }

    [Fact]
    public async Task StartDragAsync_AfterDispose_Throws()
    {
        using var ctx = new BunitContext();
        var moduleHandler = ctx.JSInterop.SetupModule(ModulePath);
        moduleHandler.Setup<string>("startDrag", _ => true).SetResult("handle-1");

        var interop = (await PointerDragInterop.CreateAsync(ctx.JSInterop.JSRuntime))!;
        await interop.DisposeAsync();

        await Assert.ThrowsAsync<ObjectDisposedException>(() =>
            interop.StartDragAsync(default, DragMode.Move, 0, 0, "g",
                _ => Task.CompletedTask, () => Task.CompletedTask));
    }

    [Fact]
    public async Task AwaitLongPressAsync_PassesPointerIdAndDurationToJs()
    {
        using var ctx = new BunitContext();
        var moduleHandler = ctx.JSInterop.SetupModule(ModulePath);
        // awaitLongPress is invoked via InvokeAsync<bool> — register both true and false
        // result paths in distinct tests; here only the argument shape matters.
        moduleHandler.Setup<bool>("awaitLongPress", _ => true).SetResult(true);

        var interop = (await PointerDragInterop.CreateAsync(ctx.JSInterop.JSRuntime))!;
        await using var _ = interop;

        var result = await interop.AwaitLongPressAsync(pointerId: 42, durationMs: 300, moveTolerancePx: 5);
        Assert.True(result);

        // Verify the JS invocation carried the three expected positional arguments.
        var invocation = moduleHandler.Invocations["awaitLongPress"].Single();
        Assert.Equal("awaitLongPress", invocation.Identifier);
        Assert.Equal(3, invocation.Arguments.Count);
        Assert.Equal(42L, Convert.ToInt64(invocation.Arguments[0]));
        Assert.Equal(300, Convert.ToInt32(invocation.Arguments[1]));
        Assert.Equal(5, Convert.ToInt32(invocation.Arguments[2]));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task AwaitLongPressAsync_ReturnsJsResult(bool jsResult)
    {
        using var ctx = new BunitContext();
        var moduleHandler = ctx.JSInterop.SetupModule(ModulePath);
        moduleHandler.Setup<bool>("awaitLongPress", _ => true).SetResult(jsResult);

        var interop = (await PointerDragInterop.CreateAsync(ctx.JSInterop.JSRuntime))!;
        await using var _ = interop;

        var result = await interop.AwaitLongPressAsync(7, 300, 5);
        Assert.Equal(jsResult, result);
    }
}
