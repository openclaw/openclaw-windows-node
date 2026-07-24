using System.Text;

namespace OpenClaw.Shared.ExecApprovals;

/// <summary>
/// Flags command text that mixes scripts which are visually confusable with Latin
/// (Cyrillic or Greek letters intermixed with Latin ones inside a single token), the
/// classic homoglyph-spoofing signal from Unicode UTS #39 mixed-script detection.
/// It does not escape or alter the text — escaping confusables would mangle legitimate
/// non-ASCII commands. It only reports whether a warning should be shown next to the
/// rendered command so the user is told the text may not read the way it looks.
///
/// Single-script text (all Latin, all Greek, all Cyrillic, all Han, …) is never flagged;
/// only a token drawing letters from two or more of the Latin-confusable scripts is.
/// </summary>
public static class ExecApprovalConfusableDetector
{
    private enum ConfusableScript { None, Latin, Cyrillic, Greek }

    public static bool HasMixedScriptConfusable(string? text)
    {
        if (string.IsNullOrEmpty(text))
            return false;

        // Whitespace separates command tokens; a homoglyph attack lives inside one
        // token (a spoofed executable name or argument), so scripts are only compared
        // within a token, never across the whole string.
        var scriptsInToken = ConfusableScript.None;
        var seenSecond = false;

        foreach (var rune in text.EnumerateRunes())
        {
            if (Rune.IsWhiteSpace(rune))
            {
                scriptsInToken = ConfusableScript.None;
                continue;
            }

            var script = Classify(rune.Value);
            if (script == ConfusableScript.None)
                continue;

            if (scriptsInToken == ConfusableScript.None)
            {
                scriptsInToken = script;
            }
            else if (scriptsInToken != script)
            {
                seenSecond = true;
                break;
            }
        }

        return seenSecond;
    }

    private static ConfusableScript Classify(int cp)
    {
        // Latin letters that carry ASCII-lookalikes (basic + accented ranges).
        if ((cp >= 0x0041 && cp <= 0x005A) || (cp >= 0x0061 && cp <= 0x007A)
            || (cp >= 0x00C0 && cp <= 0x024F)
            || (cp >= 0x1E00 && cp <= 0x1EFF))
            return ConfusableScript.Latin;

        // Greek and Coptic + Greek Extended.
        if ((cp >= 0x0370 && cp <= 0x03FF) || (cp >= 0x1F00 && cp <= 0x1FFF))
            return ConfusableScript.Greek;

        // Cyrillic + Cyrillic Supplement.
        if (cp >= 0x0400 && cp <= 0x052F)
            return ConfusableScript.Cyrillic;

        // Digits, punctuation, symbols, and every other script (Han, Hiragana, Arabic,
        // …) are script-neutral for this check: they carry no Latin homoglyph risk.
        return ConfusableScript.None;
    }
}
