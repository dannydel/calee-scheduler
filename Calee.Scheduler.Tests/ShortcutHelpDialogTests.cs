using Bunit;
using Calee.Scheduler.Demo.Components.Pages.Shared;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;

namespace Calee.Scheduler.Tests;

public class ShortcutHelpDialogTests
{
    private const string FocusModulePath = "./js/dialog-focus.js";

    [Fact]
    public void Opening_Dialog_Captures_Invoker_And_Focuses_Close_Button()
    {
        using var ctx = new BunitContext();
        var module = ctx.JSInterop.SetupModule(FocusModulePath);
        module.Setup<bool>("captureInvokerAndFocus", _ => true).SetResult(true);

        var cut = ctx.Render<ShortcutHelpDialog>(p => p
            .Add(c => c.IsVisible, true));

        Assert.NotNull(cut.Find("[role='dialog']"));
        var invocation = Assert.Single(module.Invocations["captureInvokerAndFocus"]);
        var focusTarget = Assert.IsType<ElementReference>(invocation.Arguments[0]);
        Assert.False(string.IsNullOrWhiteSpace(focusTarget.Id));
    }

    [Fact]
    public async Task Escape_Restores_Invoker_Then_Closes_Dialog()
    {
        using var ctx = new BunitContext();
        var module = ctx.JSInterop.SetupModule(FocusModulePath);
        module.Setup<bool>("captureInvokerAndFocus", _ => true).SetResult(true);
        module.Setup<bool>("restoreCapturedInvoker", _ => true).SetResult(true);
        var closeCount = 0;

        var cut = ctx.Render<ShortcutHelpDialog>(p => p
            .Add(c => c.IsVisible, true)
            .Add(c => c.OnClose, EventCallback.Factory.Create(this, () => closeCount++)));

        await cut.InvokeAsync(() =>
            cut.Find("[role='dialog']").KeyDown(new KeyboardEventArgs { Key = "Escape" }));

        Assert.Equal(1, closeCount);
        Assert.Single(module.Invocations["restoreCapturedInvoker"]);
    }

    [Fact]
    public async Task NonEscape_Key_Does_Not_Close_Dialog()
    {
        using var ctx = new BunitContext();
        var module = ctx.JSInterop.SetupModule(FocusModulePath);
        module.Setup<bool>("captureInvokerAndFocus", _ => true).SetResult(true);
        var closeCount = 0;

        var cut = ctx.Render<ShortcutHelpDialog>(p => p
            .Add(c => c.IsVisible, true)
            .Add(c => c.OnClose, EventCallback.Factory.Create(this, () => closeCount++)));

        await cut.InvokeAsync(() =>
            cut.Find("[role='dialog']").KeyDown(new KeyboardEventArgs { Key = "Enter" }));

        Assert.Equal(0, closeCount);
        Assert.Empty(module.Invocations["restoreCapturedInvoker"]);
    }
}
