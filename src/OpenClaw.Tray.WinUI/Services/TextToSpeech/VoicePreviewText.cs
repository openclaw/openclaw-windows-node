using System;
using System.Collections.Generic;

namespace OpenClawTray.Services;

/// <summary>
/// Preview sentences in the language a voice speaks. Local neural voices (Kokoro, Piper) only sound right
/// reading their own language, so Voice Settings picks the preview text by voice language, not UI language.
/// </summary>
/// <remarks>
/// To add a language, add an entry keyed by its BCP-47 primary language subtag (for example "fr").
/// A full tag (for example "pt-BR") overrides the primary subtag for that region.
/// Voices whose language has no entry read the caller's UI-localized fallback.
/// </remarks>
internal static class VoicePreviewText
{
    private static readonly Dictionary<string, string> s_byLanguage = new(StringComparer.OrdinalIgnoreCase)
    {
        ["en"] = "Hello! This is your Companion speaking.",
        ["es"] = "¡Hola! Esta es la voz de tu Compañero.",
        ["zh"] = "您好!我是您的 Companion。",
    };

    /// <summary>Preview sentence for a voice whose BCP-47 language is <paramref name="languageTag"/>, e.g. "es-ES".</summary>
    public static string For(string? languageTag, string fallback)
    {
        if (string.IsNullOrWhiteSpace(languageTag)) return fallback;
        if (s_byLanguage.TryGetValue(languageTag, out var text)) return text;
        var dash = languageTag.IndexOf('-');
        return dash > 0 && s_byLanguage.TryGetValue(languageTag[..dash], out text) ? text : fallback;
    }
}
