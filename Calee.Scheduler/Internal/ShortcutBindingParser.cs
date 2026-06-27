#nullable enable
using Calee.Scheduler.Contracts;
using Microsoft.AspNetCore.Components.Web;

namespace Calee.Scheduler.Internal;

/// <summary>
/// Internal parser for <see cref="ShortcutBinding.Key"/> strings.
/// </summary>
/// <remarks>
/// <para>
/// <strong>Grammar:</strong> <c>[modifier+]*key</c>. Modifiers are <c>Cmd</c> (matches
/// <see cref="KeyboardEventArgs.MetaKey"/>), <c>Ctrl</c>
/// (<see cref="KeyboardEventArgs.CtrlKey"/>), <c>Alt</c>
/// (<see cref="KeyboardEventArgs.AltKey"/>), and <c>Shift</c>
/// (<see cref="KeyboardEventArgs.ShiftKey"/>). Modifier matching is case-sensitive
/// (capital first letter). The final non-modifier token is the key; the parser
/// case-normalizes single-character letters so <c>"z"</c> and <c>"Z"</c> match the same
/// canonical form (the parser stores the lowercase letter; the <c>Shift</c> flag is the
/// canonical discriminator).
/// </para>
/// <para>
/// Names match <see cref="KeyboardEventArgs.Key"/> values: <c>"Enter"</c>, <c>"Escape"</c>,
/// <c>"ArrowUp"</c>, <c>"Delete"</c>, <c>"Backspace"</c>, <c>"Tab"</c>, etc. Single-character
/// tokens like <c>"?"</c>, <c>" "</c>, <c>"a"</c>, <c>"1"</c> match the corresponding
/// <c>e.Key</c> verbatim (modulo the letter case-normalization above).
/// </para>
/// <para>
/// <strong>Internal visibility.</strong> The parser is internal because the parsed-key
/// shape is an implementation detail; the public contract is the string format documented
/// on <see cref="ShortcutBinding"/>. Tests reach this type via <c>InternalsVisibleTo</c>.
/// </para>
/// </remarks>
internal static class ShortcutBindingParser
{
    /// <summary>
    /// Parse a <see cref="ShortcutBinding.Key"/> string into a canonical match key. Returns
    /// <see langword="false"/> when the input is empty or has zero tokens — defensive: a
    /// malformed binding should be a no-op rather than crashing the dispatch loop.
    /// </summary>
    /// <param name="key">The keystroke specification (e.g., <c>"Cmd+Shift+Z"</c>).</param>
    /// <param name="parsed">The parsed key, or <c>default</c> on failure.</param>
    /// <returns><see langword="true"/> when parsing succeeded.</returns>
    public static bool TryParse(string? key, out ParsedShortcut parsed)
    {
        parsed = default;
        if (string.IsNullOrEmpty(key))
        {
            return false;
        }

        // Split into modifier(s) + final key. We don't use String.Split('+') because the
        // key itself may legitimately be the literal "+" — we walk left-to-right, only
        // recognizing the special separator BEFORE the final token. A token is a modifier
        // iff it matches one of the four canonical names exactly; everything else is
        // treated as the key token.
        var cmd = false;
        var ctrl = false;
        var alt = false;
        var shift = false;

        // Walk the string identifying segments separated by '+'. The last segment is
        // always the key (never a modifier), even if it textually equals a modifier name
        // (uncommon; defensive). The exception: a literal lone "+" key is parsed as the
        // key directly when the string is just "+".
        var i = 0;
        var len = key.Length;
        string? keyToken = null;

        while (i < len)
        {
            // Find the next '+' that separates a modifier from the rest. The last
            // segment never has a trailing '+', so when we don't find one we've reached
            // the key.
            var nextPlus = key.IndexOf('+', i);
            if (nextPlus < 0 || nextPlus == len - 1)
            {
                // Treat the remainder as the key token. nextPlus == len-1 means the
                // string ends with '+', which only happens when the key itself is '+'.
                keyToken = key.Substring(i);
                break;
            }

            var segment = key.Substring(i, nextPlus - i);
            switch (segment)
            {
                case "Cmd": cmd = true; break;
                case "Ctrl": ctrl = true; break;
                case "Alt": alt = true; break;
                case "Shift": shift = true; break;
                default:
                    // Non-modifier segment in a position that expected a modifier — treat
                    // everything from this segment onward as the key (rare; defensive
                    // for keys that legitimately contain '+').
                    keyToken = key.Substring(i);
                    i = len; // Exit outer loop.
                    break;
            }
            if (keyToken is not null) break;
            i = nextPlus + 1;
        }

        if (string.IsNullOrEmpty(keyToken))
        {
            return false;
        }

        parsed = new ParsedShortcut(Canonicalize(keyToken), ctrl, cmd, alt, shift);
        return true;
    }

    /// <summary>
    /// Match a parsed shortcut against a runtime <see cref="KeyboardEventArgs"/>. Returns
    /// <see langword="true"/> when the keystroke matches the binding.
    /// </summary>
    public static bool Matches(in ParsedShortcut binding, KeyboardEventArgs e)
    {
        if (!string.Equals(Canonicalize(e.Key), binding.Key, StringComparison.Ordinal))
        {
            return false;
        }
        // Strict modifier matching: a binding without Cmd must NOT match a keystroke with
        // Cmd held (otherwise "z" would accidentally match Cmd+Z, which is a different
        // binding for Undo). Same for Ctrl. Shift's contract is asymmetric per
        // ADR-0013: a binding without Shift can fire on either Shift state for non-letter
        // keys (the binding's Shift flag is the canonical discriminator only when the
        // binding explicitly asks for Shift). However, we keep strict matching across all
        // modifiers to avoid the ambiguity — bindings that want Shift to be optional list
        // both variants (the default map's "z" / "Z" example: "Cmd+Z" without Shift binds
        // edit.undo; "Cmd+Shift+Z" with Shift binds edit.redo). Alt is reserved for the
        // command palette / future bindings; same strict policy.
        if (e.CtrlKey != binding.Ctrl) return false;
        if (e.MetaKey != binding.Cmd) return false;
        if (e.AltKey != binding.Alt) return false;
        if (e.ShiftKey != binding.Shift) return false;
        return true;
    }

    /// <summary>
    /// Lowercase single-letter keys so <c>"z"</c> and <c>"Z"</c> collapse onto one
    /// canonical form. Non-letter / multi-character keys pass through unchanged.
    /// </summary>
    private static string Canonicalize(string key)
    {
        if (key.Length == 1 && char.IsLetter(key[0]))
        {
            return char.ToLowerInvariant(key[0]).ToString();
        }
        return key;
    }
}

/// <summary>
/// Parsed representation of a <see cref="ShortcutBinding.Key"/>. The <see cref="Key"/>
/// is the canonicalized non-modifier token (lowercased for single-letter keys); the
/// modifier flags are the canonical discriminators for distinctions like undo-vs-redo.
/// </summary>
internal readonly record struct ParsedShortcut(string Key, bool Ctrl, bool Cmd, bool Alt, bool Shift);
