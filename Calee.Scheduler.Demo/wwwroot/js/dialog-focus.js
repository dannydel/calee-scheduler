let capturedInvoker = null;

export function captureInvokerAndFocus(initialFocusTarget) {
    const activeElement = document.activeElement;
    capturedInvoker = activeElement instanceof HTMLElement && activeElement !== document.body
        ? activeElement
        : null;

    if (!(initialFocusTarget instanceof HTMLElement)) {
        return false;
    }

    initialFocusTarget.focus();
    return document.activeElement === initialFocusTarget;
}

export function restoreCapturedInvoker() {
    const invoker = capturedInvoker;
    capturedInvoker = null;

    if (!(invoker instanceof HTMLElement) || !invoker.isConnected || invoker.inert) {
        return false;
    }

    invoker.focus();
    return document.activeElement === invoker;
}
