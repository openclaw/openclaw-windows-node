using System.Collections.ObjectModel;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace OpenClaw.Shared.ExecApprovals;

public static class ExecApprovalSecretRedactor
{
    private const int DefaultMinLength = 18;
    private const int DefaultKeepStart = 6;
    private const int DefaultKeepEnd = 4;
    private const string Mask = "***";
    private static readonly TimeSpan RegexTimeout = TimeSpan.FromSeconds(1);
    private static readonly RegexOptions DefaultRegexOptions = RegexOptions.CultureInvariant;
    private static readonly RegexOptions IgnoreCaseRegexOptions = DefaultRegexOptions | RegexOptions.IgnoreCase;

    private const string PaymentCredentialEnvKeys = @"CARD[_-]?NUMBER|CARD[_-]?CVC|CARD[_-]?CVV|CVC|CVV|SECURITY[_-]?CODE|PAYMENT[_-]?CREDENTIAL|SHARED[_-]?PAYMENT[_-]?TOKEN";
    private const string PaymentCredentialQueryKeys = @"card[-_]?number|card[-_]?cvc|card[-_]?cvv|cvc|cvv|security[-_]?code|payment[-_]?credential|shared[-_]?payment[-_]?token";
    private const string AuthQueryKeys = @"access[-_]?token|auth[-_]?token|hook[-_]?token|refresh[-_]?token|id[-_]?token|api[-_]?key|apikey|client[-_]?secret|app[-_]?secret|private[-_]?key|credential|authorization|token|key|secret|password|pass|passwd|auth|jwt|session|code|signature|x[-_]?amz[-_]?(?:signature|security[-_]?token)";
    private const string FormBodyFirstPairKeys = AuthQueryKeys + @"|app[-_]?secret|credential|" + PaymentCredentialQueryKeys;
    private const string StandaloneAssignmentSecretKeys = @"access_token|refresh_token|id_token|auth[-_]?token|hook[-_]?token|api[-_]?key|client[-_]?secret|app[-_]?secret|private[-_]?key|authorization|jwt|token|secret|password|pass|passwd|credential|" + PaymentCredentialQueryKeys;
    private const string FormBodyKeyInvisibleChars = @"\p{Cc}\p{Cf}\u00A0\u1680\u2000-\u200A\u202F\u205F\u3000\u115F\u1160\u3164\uFFA0";
    private const string FormBodyKey = @"[" + FormBodyKeyInvisibleChars + @"+]*(?:[A-Za-z_]|%[0-9A-Fa-f]{2})(?:[A-Za-z0-9_.-]|%[0-9A-Fa-f]{2}|[" + FormBodyKeyInvisibleChars + @"+])*";
    private const string FormBodyValue = @"[^&\s<>]*";
    private const string UrlQueryValue = @"[^&#\s<>]*";
    private const string FormBodyPair = FormBodyKey + "=" + FormBodyValue;
    private const string PaymentCredentialJsonKeys = @"cardNumber|card_number|cardCvc|card_cvc|cardCvv|card_cvv|cvc|cvv|securityCode|security_code|paymentCredential|payment_credential|sharedPaymentToken|shared_payment_token";
    private const string Base64SafeTokenBoundary = @"(^|[^A-Za-z0-9])";
    private const string IdentifierSafeTokenBoundary = @"(^|[^A-Za-z0-9_])";

    private static readonly HashSet<string> BodySecretKeys = new(StringComparer.Ordinal)
    {
        "access_token", "auth_token", "hook_token", "refresh_token", "id_token", "token", "api_key",
        "apikey", "client_secret", "app_secret", "password", "pass", "passwd", "auth", "jwt",
        "session", "code", "signature", "x_amz_signature", "x_amz_security_token", "secret",
        "credential", "private_key", "authorization", "key", "card_number", "card_cvc", "card_cvv",
        "cvc", "cvv", "security_code", "payment_credential", "shared_payment_token",
    };

    private static readonly HashSet<char> SecretValueQuoteChars = new() { '"', '\'', '`' };

    private static readonly Regex FormBodyKeyObfuscationRe = CreateRegex(@"[" + FormBodyKeyInvisibleChars + @"+]", DefaultRegexOptions);
    private static readonly Regex FormBodyKeySeparatorRe = CreateRegex(@"[\p{Cc}\p{Cf}\p{Z}\u115F\u1160\u3164\uFFA0+]", DefaultRegexOptions);
    private static readonly Regex FormBodyPercentEscapeRe = CreateRegex(@"%[0-9A-Fa-f]{2}", DefaultRegexOptions);
    private static readonly Regex FormBodyRe = CreateRegex(@"^" + FormBodyPair + @"(?:&" + FormBodyPair + @")+$", DefaultRegexOptions);
    private static readonly Regex FormBodySubstringRe = CreateRegex(@"(^|[\s:({\[,=""'`])(" + FormBodyPair + @"(?:&" + FormBodyPair + @")+)", DefaultRegexOptions);
    private static readonly Regex EncodedFormPairRe = CreateRegex(@"(^|[\s:({\[,=""'`&])(" + FormBodyKey + @")=(" + FormBodyValue + ")", DefaultRegexOptions);
    private static readonly Regex FormBodyContextSinglePairRe = CreateRegex(@"(\b(?:body|form(?:[-_\s]?body)?)\s*[:=]\s*([""'\x60]?))(" + FormBodyKey + @")=(" + FormBodyValue + @")([""'\x60]?)", IgnoreCaseRegexOptions);
    private static readonly Regex UrlQueryPairRe = CreateRegex(@"([?&])(" + FormBodyKey + @")=(" + UrlQueryValue + ")", DefaultRegexOptions);
    private static readonly Regex SecretValueTrailingDelimiterRe = CreateRegex(@"([""'`,;)}\]]+)$", DefaultRegexOptions);
    private static readonly Regex SecretValueSuffixRe = CreateRegex(@"^[""'`,;)}\]]*$", DefaultRegexOptions);
    private static readonly Regex FormBodyLineBreakSplitRe = CreateRegex(@"(\r\n|\r|\n)", DefaultRegexOptions);
    private static readonly Regex FormBodyLineBreakSegmentRe = CreateRegex(@"^(?:\r\n|\r|\n)$", DefaultRegexOptions);
    private static readonly Regex ShellReferenceBareRe = CreateRegex(@"^\$([A-Z_][A-Z0-9_]*)$", DefaultRegexOptions);
    private static readonly Regex ShellReferenceBracedRe = CreateRegex(@"^\$\{([A-Z_][A-Z0-9_]*)(?::[-=?+])?\}$", DefaultRegexOptions);
    private static readonly Regex EnvAssignmentKeyRe = CreateRegex(@"\b([A-Z_][A-Z0-9_]*)\b\s*[=:]", DefaultRegexOptions);
    private static readonly Regex EmptyShellParameterExpansionTailRe = CreateRegex(@"^[-=?+]\}$", DefaultRegexOptions);
    private static readonly Regex AuthHeaderStartRe = CreateRegex(@"(?:Authorization|Proxy-Authorization)(?:\\+[""'])?\s*[:=]\s*(?:\\+[""'])?", IgnoreCaseRegexOptions);
    private static readonly Regex StructuredAuthHeaderSerializedKeyRe = CreateRegex(@"[""'](?:Authorization|Proxy-Authorization)[""']\s*[:=]\s*[""']", IgnoreCaseRegexOptions);
    private static readonly Regex PemLineBreakRe = CreateRegex(@"\r?\n|\\r\\n|\\n", DefaultRegexOptions);

    internal static IReadOnlyCollection<string> RegisteredSecretValues { get; set; } = Array.Empty<string>();

    private static readonly ReadOnlyCollection<RedactRegex> DefaultPatterns = BuildDefaultPatterns().AsReadOnly();

    public static string Redact(string text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return text;
        }

        var next = RedactRegisteredSecretValues(text);
        next = RedactStructuredAuthHeaders(next);
        next = RedactUrlQueryPairs(next);
        next = RedactFormBody(next);

        foreach (var pattern in DefaultPatterns)
        {
            next = ReplacePattern(next, pattern);
        }

        return next;
    }

    public static bool[] ComputeRedactionBitmap(string text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return Array.Empty<bool>();
        }

        var bitmap = new bool[text.Length];
        MarkRegisteredSecretValueRedactions(text, bitmap);
        MarkStructuredAuthHeaderRedactions(text, bitmap);
        MarkUrlQueryPairRedactions(text, bitmap);
        MarkFormBodyRedactions(text, bitmap);
        foreach (var pattern in DefaultPatterns)
        {
            MarkPatternRedactions(text, bitmap, pattern);
        }

        return bitmap;
    }

    internal static string MaskToken(string token)
    {
        if (token == Mask)
        {
            return token;
        }

        if (token.Length < DefaultMinLength)
        {
            return Mask;
        }

        return SliceUtf16Safe(token, 0, DefaultKeepStart) + "…" + SliceUtf16Safe(token, -DefaultKeepEnd);
    }

    private static string RedactRegisteredSecretValues(string text)
    {
        var values = GetRegisteredSecretValues();
        if (values.Length == 0 || !values.Select(value => value[0]).Distinct().Any(text.Contains))
        {
            return text;
        }

        var matcher = CreateRegex(string.Join("|", values.Select(Regex.Escape)), DefaultRegexOptions);
        return matcher.Replace(text, match => MaskToken(match.Value));
    }

    private static string[] GetRegisteredSecretValues() =>
        RegisteredSecretValues
            .Where(value => !string.IsNullOrEmpty(value))
            .Distinct(StringComparer.Ordinal)
            .OrderByDescending(value => value.Length)
            .ToArray();

    private static string SliceUtf16Safe(string value, int start, int? end = null)
    {
        var actualStart = start < 0 ? Math.Max(value.Length + start, 0) : Math.Min(start, value.Length);
        var actualEnd = end is null ? value.Length : end.Value < 0 ? Math.Max(value.Length + end.Value, 0) : Math.Min(end.Value, value.Length);
        if (actualEnd < actualStart)
        {
            actualEnd = actualStart;
        }

        if (actualEnd > actualStart && actualEnd < value.Length && char.IsHighSurrogate(value[actualEnd - 1]) && char.IsLowSurrogate(value[actualEnd]))
        {
            actualEnd--;
        }

        if (actualStart > 0 && actualStart < value.Length && char.IsLowSurrogate(value[actualStart]) && char.IsHighSurrogate(value[actualStart - 1]))
        {
            actualStart++;
        }

        return value.Substring(actualStart, actualEnd - actualStart);
    }

    private static string ReplacePattern(string text, RedactRegex pattern)
    {
        try
        {
            return pattern.Regex.Replace(text, match => RedactMatch(match, pattern, text));
        }
        catch (RegexMatchTimeoutException)
        {
            // Fail closed: a timed-out redaction pass must never return the unredacted text
            // (which could leak a secret the pattern would have masked). Propagate so the display
            // sanitizer/prompt handler fails closed (denies) rather than showing raw content.
            throw;
        }
    }

    private static string RedactMatch(Match match, RedactRegex pattern, string input)
    {
        if (match.Value.Contains("PRIVATE KEY-----", StringComparison.OrdinalIgnoreCase))
        {
            return RedactPemBlock(match.Value);
        }

        var selected = SelectSecretCapture(match);
        var token = selected.Value;
        if (SplitSecretValueForMask(token).Maskable == Mask)
        {
            return match.Value;
        }

        if (pattern.Base64Boundary && IsInsideBase64Payload(input, match.Index + selected.Start))
        {
            return match.Value;
        }

        if (pattern.ShellReferencePreserving && (ShouldPreserveShellReferenceMatch(match.Value, token) || EmptyShellParameterExpansionTailRe.IsMatch(token)))
        {
            return match.Value;
        }

        var masked = pattern.ShellReferencePreserving ? MaskToken(token) : MaskSecretValue(token, hinted: true);
        if (selected.Start < 0)
        {
            return match.Value;
        }

        return match.Value[..selected.Start] + masked + match.Value[(selected.Start + token.Length)..];
    }

    private static bool IsInsideBase64Payload(string input, int tokenStart)
    {
        if (tokenStart <= 0)
        {
            return false;
        }

        var left = input[..tokenStart];
        var marker = left.LastIndexOf(";base64,", StringComparison.OrdinalIgnoreCase);
        if (marker < 0)
        {
            return false;
        }

        for (var i = marker + ";base64,".Length; i < left.Length; i++)
        {
            var c = left[i];
            if (!(char.IsAsciiLetterOrDigit(c) || c is '+' or '/' or '='))
            {
                return false;
            }
        }

        return true;
    }

    private static SecretCaptureSelection SelectSecretCapture(Match match)
    {
        var tokens = new List<SecretCaptureSelection>();
        for (var index = 1; index < match.Groups.Count; index++)
        {
            var group = match.Groups[index];
            if (group.Success && group.Length > 0)
            {
                tokens.Add(new SecretCaptureSelection(index - 1, group.Value, group.Index - match.Index));
            }
        }

        return tokens.Count switch
        {
            0 => new SecretCaptureSelection(-1, match.Value, 0),
            1 => tokens[0],
            _ => tokens[^1],
        };
    }

    private static SecretValueParts SplitSecretValueForMask(string token)
    {
        var openingQuote = token.Length > 0 ? token[0] : '\0';
        if (SecretValueQuoteChars.Contains(openingQuote))
        {
            var closingQuoteIndex = token.LastIndexOf(openingQuote);
            if (closingQuoteIndex > 0)
            {
                var quotedSuffix = token[(closingQuoteIndex + 1)..];
                if (SecretValueSuffixRe.IsMatch(quotedSuffix))
                {
                    return new SecretValueParts(token[1..closingQuoteIndex], quotedSuffix, 0, closingQuoteIndex + 1);
                }
            }

            var withoutLeadingQuote = token[1..];
            var trailingDelimiter = SecretValueTrailingDelimiterRe.Match(withoutLeadingQuote);
            var delimiter = trailingDelimiter.Success ? trailingDelimiter.Groups[1].Value : string.Empty;
            var hasDelimiter = delimiter.Length > 0 && delimiter.Length < withoutLeadingQuote.Length;
            var maskable = hasDelimiter ? withoutLeadingQuote[..^delimiter.Length] : withoutLeadingQuote;
            return new SecretValueParts(maskable, hasDelimiter ? delimiter : string.Empty, 0, 1 + maskable.Length);
        }

        var trailing = SecretValueTrailingDelimiterRe.Match(token);
        var suffix = trailing.Success ? trailing.Groups[1].Value : string.Empty;
        var hasSuffix = suffix.Length > 0 && suffix.Length < token.Length;
        var bareMaskable = hasSuffix ? token[..^suffix.Length] : token;
        return new SecretValueParts(bareMaskable, hasSuffix ? suffix : string.Empty, 0, bareMaskable.Length);
    }

    private static string MaskSecretValue(string token, bool hinted = false)
    {
        var parts = SplitSecretValueForMask(token);
        return (hinted ? MaskToken(parts.Maskable) : Mask) + parts.Suffix;
    }

    private static string NormalizeSensitiveKeyName(string value)
    {
        var stripped = FormBodyKeySeparatorRe.Replace(value, string.Empty);
        try
        {
            return FormBodyKeySeparatorRe.Replace(Uri.UnescapeDataString(stripped), string.Empty)
                .ToLowerInvariant()
                .Replace("-", "_", StringComparison.Ordinal);
        }
        catch (UriFormatException)
        {
            return stripped.ToLowerInvariant().Replace("-", "_", StringComparison.Ordinal);
        }
    }

    private static bool IsSensitiveBodyKey(string key) => BodySecretKeys.Contains(NormalizeSensitiveKeyName(key));

    private static bool HasEncodedOrInvisibleFormKey(string key) =>
        FormBodyPercentEscapeRe.IsMatch(key) || FormBodyKeyObfuscationRe.Replace(key, string.Empty) != key;

    private static string RedactFormEncodedPairs(string value, bool maskValuesHinted = false, bool onlyEncodedOrInvisibleKeys = false)
    {
        return string.Join("&", value.Split('&').Select(pair =>
        {
            var equalsIndex = pair.IndexOf('=', StringComparison.Ordinal);
            if (equalsIndex < 0)
            {
                return pair;
            }

            var key = pair[..equalsIndex];
            if (onlyEncodedOrInvisibleKeys && !HasEncodedOrInvisibleFormKey(key))
            {
                return pair;
            }

            if (!IsSensitiveBodyKey(key))
            {
                return pair;
            }

            var token = pair[(equalsIndex + 1)..];
            return key + "=" + MaskSecretValue(token, maskValuesHinted);
        }));
    }

    private static string RedactUrlQueryPairs(string text)
    {
        if (string.IsNullOrEmpty(text) || !text.Contains('?', StringComparison.Ordinal))
        {
            return text;
        }

        return UrlQueryPairRe.Replace(text, match =>
        {
            var prefix = match.Groups[1].Value;
            var key = match.Groups[2].Value;
            var token = match.Groups[3].Value;
            if (!HasEncodedOrInvisibleFormKey(key) || !IsSensitiveBodyKey(key))
            {
                return match.Value;
            }

            return prefix + key + "=" + MaskSecretValue(token, hinted: true);
        });
    }

    private static string RedactEncodedFormPairs(string text)
    {
        if (string.IsNullOrEmpty(text) || (!text.Contains('%', StringComparison.Ordinal) && FormBodyKeyObfuscationRe.Replace(text, string.Empty) == text))
        {
            return text;
        }

        return EncodedFormPairRe.Replace(text, match =>
        {
            var prefix = match.Groups[1].Value;
            var key = match.Groups[2].Value;
            var token = match.Groups[3].Value;
            if (!HasEncodedOrInvisibleFormKey(key) || !IsSensitiveBodyKey(key))
            {
                return match.Value;
            }

            return prefix + key + "=" + MaskSecretValue(token);
        });
    }

    private static string RedactFormBodyContextSinglePairs(string text)
    {
        if (string.IsNullOrEmpty(text) || !(text.Contains('=', StringComparison.Ordinal) || text.Contains(':', StringComparison.Ordinal)))
        {
            return text;
        }

        return FormBodyContextSinglePairRe.Replace(text, match =>
        {
            var prefix = match.Groups[1].Value;
            var key = match.Groups[3].Value;
            var token = match.Groups[4].Value;
            var suffix = match.Groups[5].Value;
            return IsSensitiveBodyKey(key) ? prefix + key + "=" + MaskSecretValue(token) + suffix : match.Value;
        });
    }

    private static string RedactFormBodyLine(string text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return text;
        }

        var contextRedacted = RedactFormBodyContextSinglePairs(RedactEncodedFormPairs(text));
        if (!contextRedacted.Contains('&', StringComparison.Ordinal))
        {
            return contextRedacted;
        }

        if (FormBodyRe.IsMatch(contextRedacted))
        {
            return RedactFormEncodedPairs(contextRedacted);
        }

        var redacted = FormBodySubstringRe.Replace(contextRedacted, match =>
        {
            var prefix = match.Groups[1].Value;
            var body = match.Groups[2].Value;
            var redactedBody = RedactFormEncodedPairs(body);
            return redactedBody == body ? match.Value : prefix + redactedBody;
        });

        return RedactFormBodyContextSinglePairs(RedactEncodedFormPairs(redacted));
    }

    private static string RedactFormBody(string text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return text;
        }

        if (!FormBodyLineBreakSplitRe.IsMatch(text))
        {
            return RedactFormBodyLine(text);
        }

        return string.Concat(FormBodyLineBreakSplitRe.Split(text).Select(segment =>
            FormBodyLineBreakSegmentRe.IsMatch(segment) ? segment : RedactFormBodyLine(segment)));
    }

    private static string RedactPemBlock(string block)
    {
        var lines = PemLineBreakRe.Split(block).Where(line => line.Length > 0).ToArray();
        if (lines.Length < 2)
            return Mask;

        var separator = block.Contains("\r\n", StringComparison.Ordinal)
            ? "\r\n"
            : block.Contains('\n')
                ? "\n"
                : block.Contains("\\r\\n", StringComparison.Ordinal)
                    ? "\\r\\n"
                    : "\\n";
        return lines[0] + separator + "…redacted…" + separator + lines[^1];
    }

    internal static bool IsShellReference(string value) =>
        ShellReferenceBareRe.IsMatch(value) || ShellReferenceBracedRe.IsMatch(value);

    internal static string RedactReviewSafeUrlQueryValues(string text)
    {
        if (string.IsNullOrEmpty(text) || !text.Contains('?'))
        {
            return text;
        }

        return UrlQueryPairRe.Replace(text, match =>
        {
            var key = match.Groups[2].Value;
            var token = match.Groups[3].Value;
            if (!IsSensitiveBodyKey(key) || !IsReviewSafeQueryToken(token))
                return match.Value;

            return match.Groups[1].Value
                + key
                + "="
                + MaskSecretValue(token, hinted: true);
        });
    }

    private static bool IsReviewSafeQueryToken(string token)
    {
        var value = SplitSecretValueForMask(token).Maskable;
        if (string.IsNullOrEmpty(value) || IsShellReference(value))
            return true;

        foreach (var character in value)
        {
            if (!char.IsAsciiLetterOrDigit(character)
                && character is not ('-' or '_' or '.' or '~' or '+' or '/' or '=' or ':'))
            {
                return false;
            }
        }

        return true;
    }

    private static bool IsShellReferenceToKey(string key, string value)
    {
        if (!Regex.IsMatch(key, @"^[A-Z_][A-Z0-9_]*$", DefaultRegexOptions, RegexTimeout))
        {
            return false;
        }

        var bare = ShellReferenceBareRe.Match(value);
        if (bare.Success)
        {
            return string.Equals(bare.Groups[1].Value, key, StringComparison.Ordinal);
        }

        var braced = ShellReferenceBracedRe.Match(value);
        return braced.Success && string.Equals(braced.Groups[1].Value, key, StringComparison.Ordinal);
    }

    private static string? ReadEnvAssignmentKey(string match)
    {
        var key = EnvAssignmentKeyRe.Match(match);
        return key.Success ? key.Groups[1].Value : null;
    }

    private static bool ShouldPreserveShellReferenceMatch(string match, string token)
    {
        var key = ReadEnvAssignmentKey(match);
        return key is not null && IsShellReferenceToKey(key, token);
    }

    private static string RedactStructuredAuthHeaders(string text)
    {
        if (!text.Contains("uthorization", StringComparison.OrdinalIgnoreCase))
        {
            return text;
        }

        text = RedactSerializedAuthHeaderFields(text);
        var builder = new StringBuilder(text.Length);
        var cursor = 0;
        foreach (Match match in AuthHeaderStartRe.Matches(text))
        {
            if (match.Index < cursor)
            {
                continue;
            }

            var valueStart = match.Index + match.Length;
            var replacement = TryRedactStructuredAuthHeader(text, match.Index, valueStart, out var end);
            if (replacement is null)
            {
                continue;
            }

            builder.Append(text, cursor, valueStart - cursor);
            builder.Append(replacement);
            cursor = end;
        }

        if (cursor == 0)
        {
            return text;
        }

        builder.Append(text, cursor, text.Length - cursor);
        return builder.ToString();
    }

    private static string RedactSerializedAuthHeaderFields(string text)
    {
        var prefixRe = CreateRegex(@"(?<prefix>(?:\\+)?[""'](?:Authorization|Proxy-Authorization)(?:\\+)?[""']\s*:\s*(?:\\+)?[""'])", IgnoreCaseRegexOptions);
        var builder = new StringBuilder(text.Length);
        var cursor = 0;
        foreach (Match match in prefixRe.Matches(text))
        {
            if (match.Index < cursor)
            {
                continue;
            }

            var valueStart = match.Index + match.Length;
            var escaped = match.Value.Contains('\\', StringComparison.Ordinal);
            var valueEnd = FindSerializedHeaderValueEnd(text, valueStart, escaped);
            if (valueEnd < valueStart)
            {
                continue;
            }

            var value = text[valueStart..valueEnd];
            var redacted = RedactSerializedAuthHeaderValue(value, plainJson: !escaped);
            if (redacted == value)
            {
                continue;
            }

            builder.Append(text, cursor, valueStart - cursor);
            builder.Append(redacted);
            cursor = valueEnd;
        }

        if (cursor == 0)
        {
            return text;
        }

        builder.Append(text, cursor, text.Length - cursor);
        return builder.ToString();
    }

    private static int FindSerializedHeaderValueEnd(string text, int start, bool escaped)
    {
        for (var index = start; index < text.Length; index++)
        {
            if (text[index] != '"' && text[index] != '\'')
            {
                continue;
            }

            var slashCount = 0;
            for (var back = index - 1; back >= 0 && text[back] == '\\'; back--)
            {
                slashCount++;
            }

            if (!escaped)
            {
                if (slashCount % 2 == 0)
                {
                    return index;
                }

                continue;
            }

            if (slashCount == 0)
            {
                continue;
            }

            var next = index + 1;
            if (next >= text.Length || text[next] == '}')
            {
                return index - slashCount;
            }

            if (text[next] == ',')
            {
                var afterComma = next + 1;
                while (afterComma < text.Length && char.IsWhiteSpace(text[afterComma]))
                {
                    afterComma++;
                }

                if (afterComma < text.Length && text[afterComma] == '\\')
                {
                    return index - slashCount;
                }
            }
        }

        return -1;
    }

    private static string RedactSerializedAuthHeaderValue(string value, bool plainJson)
    {
        var split = value.IndexOf(' ');
        if (split < 0)
        {
            return MaskSecretValue(value, hinted: true);
        }

        var scheme = value[..split];
        var rest = value[(split + 1)..];
        if (scheme.Equals("Basic", StringComparison.OrdinalIgnoreCase) ||
            scheme.Equals("Bearer", StringComparison.OrdinalIgnoreCase) ||
            scheme.Equals("Bot", StringComparison.OrdinalIgnoreCase))
        {
            return scheme + " " + MaskSecretValue(rest, hinted: true);
        }

        if (rest.Contains('=', StringComparison.Ordinal) || rest.Contains(',', StringComparison.Ordinal))
        {
            return plainJson ? Mask : scheme + " " + Mask;
        }

        return scheme + " " + MaskSecretValue(rest, hinted: true);
    }

    private static string? TryRedactStructuredAuthHeader(string text, int headerStart, int valueStart, out int end)
    {
        end = valueStart;
        var scan = valueStart;
        while (scan < text.Length && IsAuthWhitespaceAt(text, scan, out var whitespaceLength))
        {
            scan += whitespaceLength;
        }

        var schemeStart = scan;
        while (scan < text.Length && IsSchemeChar(text[scan]))
        {
            scan++;
        }

        var scheme = text[schemeStart..scan];
        if (scheme.Length == 0)
        {
            end = FindOpaqueAuthEnd(text, schemeStart);
            return end > schemeStart ? MaskSecretValue(text[schemeStart..end], hinted: true) : null;
        }

        var afterScheme = scan;
        while (scan < text.Length && IsAuthWhitespaceAt(text, scan, out var ws))
        {
            scan += ws;
        }

        if (scan == afterScheme)
        {
            return null;
        }

        var valueEnd = FindStructuredAuthValueEnd(text, scan, headerStart);
        if (valueEnd <= scan)
        {
            return null;
        }

        var rest = text[scan..valueEnd];
        if (!LooksLikeAuthSecretRest(scheme, rest))
        {
            return null;
        }

        var serializedField = headerStart > 0 && text[headerStart - 1] == '"' && StructuredAuthHeaderSerializedKeyRe.IsMatch(text[Math.Max(0, headerStart - 1)..Math.Min(text.Length, valueStart)]);
        end = valueEnd;
        if (serializedField && (headerStart < 2 || text[headerStart - 2] != '\\'))
        {
            return Mask;
        }

        var suffixStart = FindStructuredAuthDiagnosticSuffix(text, scan, valueEnd);
        if (suffixStart >= 0)
        {
            var secret = text[scan..suffixStart];
            if (!secret.Contains('=', StringComparison.Ordinal) && !secret.Contains(',', StringComparison.Ordinal))
            {
                return null;
            }

            end = suffixStart;
            return scheme + text[afterScheme..scan] + Mask;
        }

        var finalReplacement = rest.Contains('=', StringComparison.Ordinal) || rest.Contains(',', StringComparison.Ordinal)
            ? Mask
            : MaskSecretValue(rest, hinted: true);
        return scheme + text[afterScheme..scan] + finalReplacement;
    }

    private static bool LooksLikeAuthSecretRest(string scheme, string rest)
    {
        if (rest.Length == 0)
        {
            return false;
        }

        if (scheme.Equals("Basic", StringComparison.OrdinalIgnoreCase) ||
            scheme.Equals("Bearer", StringComparison.OrdinalIgnoreCase) ||
            scheme.Equals("Bot", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        return rest.Contains('=', StringComparison.Ordinal) ||
               rest.Contains(',', StringComparison.Ordinal) ||
               rest.Length >= 18 ||
               scheme.Contains('+', StringComparison.Ordinal);
    }

    private static int FindStructuredAuthDiagnosticSuffix(string text, int start, int end)
    {
        var candidates = new[] { "; status=", "; request_id=", ", status=", ";status=", ";request_id=", ",status=" };
        var best = -1;
        foreach (var candidate in candidates)
        {
            var found = text.IndexOf(candidate, start, end - start, StringComparison.OrdinalIgnoreCase);
            if (found >= 0 && (best < 0 || found < best))
            {
                best = found;
            }
        }

        return best;
    }

    private static int FindStructuredAuthValueEnd(string text, int start, int headerStart)
    {
        var enclosingQuote = headerStart > 0 && text[headerStart - 1] is '\'' or '"' ? text[headerStart - 1] : '\0';
        var index = start;
        while (index < text.Length)
        {
            var c = text[index];
            if (c is '\r' or '\n')
            {
                var newlineLength = c == '\r' && index + 1 < text.Length && text[index + 1] == '\n' ? 2 : 1;
                if (index + newlineLength < text.Length && text[index + newlineLength] is ' ' or '\t')
                {
                    index += newlineLength;
                    continue;
                }

                return index;
            }

            if (c is ')' or '}')
            {
                return index;
            }

            if (c is '"' or '\'' && index == text.Length - 1)
            {
                return index;
            }

            if (enclosingQuote != '\0' && c == enclosingQuote && (index == 0 || text[index - 1] != '\\'))
            {
                return index;
            }

            index++;
        }

        return index;
    }

    private static int FindOpaqueAuthEnd(string text, int start)
    {
        var index = start;
        while (index < text.Length && !char.IsWhiteSpace(text[index]) && text[index] is not '"' and not '\'' and not ',' and not ';' and not ')' and not '}' and not ']')
        {
            index++;
        }

        return index;
    }

    private static bool IsAuthWhitespaceAt(string text, int index, out int length)
    {
        length = 0;
        if (index >= text.Length)
        {
            return false;
        }

        if (text[index] is ' ' or '\t')
        {
            length = 1;
            return true;
        }

        if (text[index] == '\\' && index + 1 < text.Length && text[index + 1] is 't' or 'n' or 'r')
        {
            length = 2;
            return true;
        }

        if (text[index] is '\r' or '\n')
        {
            length = text[index] == '\r' && index + 1 < text.Length && text[index + 1] == '\n' ? 2 : 1;
            if (index + length < text.Length && text[index + length] is ' ' or '\t')
            {
                length++;
            }

            return true;
        }

        return false;
    }

    private static bool IsSchemeChar(char c) => char.IsAsciiLetterOrDigit(c) || c is '-' or '+' or '.';

    private static void MarkBitmapRange(bool[] bitmap, int start, int end)
    {
        var boundedStart = Math.Max(0, start);
        var boundedEnd = Math.Min(bitmap.Length, end);
        for (var i = boundedStart; i < boundedEnd; i++)
        {
            bitmap[i] = true;
        }
    }

    private static void MarkPatternRedactions(string text, bool[] bitmap, RedactRegex pattern)
    {
        try
        {
            foreach (Match match in pattern.Regex.Matches(text))
            {
                MarkPatternMatchRedaction(bitmap, text, match, pattern);
            }
        }
        catch (RegexMatchTimeoutException)
        {
        }
    }

    private static void MarkPatternMatchRedaction(bool[] bitmap, string input, Match match, RedactRegex pattern)
    {
        if (match.Value.Contains("PRIVATE KEY-----", StringComparison.OrdinalIgnoreCase))
        {
            MarkBitmapRange(bitmap, match.Index, match.Index + match.Length);
            return;
        }

        var selected = SelectSecretCapture(match);
        if (selected.Start < 0 || (pattern.Base64Boundary && IsInsideBase64Payload(input, match.Index + selected.Start)))
        {
            return;
        }

        var secretValue = SplitSecretValueForMask(selected.Value);
        MarkBitmapRange(bitmap, match.Index + selected.Start + secretValue.MaskStart, match.Index + selected.Start + secretValue.MaskEnd);
    }

    private static void MarkUrlQueryPairRedactions(string text, bool[] bitmap)
    {
        if (string.IsNullOrEmpty(text) || !text.Contains('?', StringComparison.Ordinal))
        {
            return;
        }

        foreach (Match match in UrlQueryPairRe.Matches(text))
        {
            var key = match.Groups[2].Value;
            if (!HasEncodedOrInvisibleFormKey(key) || !IsSensitiveBodyKey(key))
            {
                continue;
            }

            var token = match.Groups[3].Value;
            var secretValue = SplitSecretValueForMask(token);
            var valueOffset = match.Groups[3].Index;
            MarkBitmapRange(bitmap, valueOffset + secretValue.MaskStart, valueOffset + secretValue.MaskEnd);
        }
    }

    private static void MarkRegisteredSecretValueRedactions(string text, bool[] bitmap)
    {
        foreach (var value in GetRegisteredSecretValues())
        {
            var start = 0;
            while (start < text.Length)
            {
                var index = text.IndexOf(value, start, StringComparison.Ordinal);
                if (index < 0)
                    break;

                MarkBitmapRange(bitmap, index, index + value.Length);
                start = index + value.Length;
            }
        }
    }

    private static void MarkEncodedFormPairRedactions(string text, bool[] bitmap, int offset = 0)
    {
        if (string.IsNullOrEmpty(text) || (!text.Contains('%', StringComparison.Ordinal) && FormBodyKeyObfuscationRe.Replace(text, string.Empty) == text))
        {
            return;
        }

        foreach (Match match in EncodedFormPairRe.Matches(text))
        {
            var key = match.Groups[2].Value;
            if (!HasEncodedOrInvisibleFormKey(key) || !IsSensitiveBodyKey(key))
            {
                continue;
            }

            var secretValue = SplitSecretValueForMask(match.Groups[3].Value);
            MarkBitmapRange(bitmap, offset + match.Groups[3].Index + secretValue.MaskStart, offset + match.Groups[3].Index + secretValue.MaskEnd);
        }
    }

    private static void MarkFormBodyContextSinglePairRedactions(string text, bool[] bitmap, int offset = 0)
    {
        if (string.IsNullOrEmpty(text) || !(text.Contains('=', StringComparison.Ordinal) || text.Contains(':', StringComparison.Ordinal)))
        {
            return;
        }

        foreach (Match match in FormBodyContextSinglePairRe.Matches(text))
        {
            var key = match.Groups[3].Value;
            if (!IsSensitiveBodyKey(key))
            {
                continue;
            }

            var secretValue = SplitSecretValueForMask(match.Groups[4].Value);
            MarkBitmapRange(bitmap, offset + match.Groups[4].Index + secretValue.MaskStart, offset + match.Groups[4].Index + secretValue.MaskEnd);
        }
    }

    private static void MarkSensitiveFormEncodedPairValues(bool[] bitmap, string value, int offset, bool onlyEncodedOrInvisibleKeys = false)
    {
        var cursor = 0;
        foreach (var pair in value.Split('&'))
        {
            var pairStart = cursor;
            cursor = pairStart + pair.Length + 1;
            var equalsIndex = pair.IndexOf('=', StringComparison.Ordinal);
            if (equalsIndex < 0)
            {
                continue;
            }

            var key = pair[..equalsIndex];
            if (onlyEncodedOrInvisibleKeys && !HasEncodedOrInvisibleFormKey(key))
            {
                continue;
            }

            if (!IsSensitiveBodyKey(key))
            {
                continue;
            }

            var secretValue = SplitSecretValueForMask(pair[(equalsIndex + 1)..]);
            var valueStart = pairStart + equalsIndex + 1 + secretValue.MaskStart;
            var valueEnd = pairStart + equalsIndex + 1 + secretValue.MaskEnd;
            MarkBitmapRange(bitmap, offset + valueStart, offset + valueEnd);
        }
    }

    private static void MarkFormBodyLineRedactions(string text, bool[] bitmap, int offset)
    {
        if (string.IsNullOrEmpty(text))
        {
            return;
        }

        MarkEncodedFormPairRedactions(text, bitmap, offset);
        MarkFormBodyContextSinglePairRedactions(text, bitmap, offset);
        if (!text.Contains('&', StringComparison.Ordinal))
        {
            return;
        }

        if (FormBodyRe.IsMatch(text))
        {
            MarkSensitiveFormEncodedPairValues(bitmap, text, offset);
            return;
        }

        foreach (Match match in FormBodySubstringRe.Matches(text))
        {
            MarkSensitiveFormEncodedPairValues(bitmap, match.Groups[2].Value, offset + match.Groups[2].Index);
        }
    }

    private static void MarkFormBodyRedactions(string text, bool[] bitmap)
    {
        if (string.IsNullOrEmpty(text))
        {
            return;
        }

        if (!FormBodyLineBreakSplitRe.IsMatch(text))
        {
            MarkFormBodyLineRedactions(text, bitmap, 0);
            return;
        }

        var offset = 0;
        foreach (var segment in FormBodyLineBreakSplitRe.Split(text))
        {
            if (!FormBodyLineBreakSegmentRe.IsMatch(segment))
            {
                MarkFormBodyLineRedactions(segment, bitmap, offset);
            }

            offset += segment.Length;
        }
    }

    private static void MarkStructuredAuthHeaderRedactions(string text, bool[] bitmap)
    {
        if (!text.Contains("uthorization", StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        foreach (Match match in AuthHeaderStartRe.Matches(text))
        {
            var valueStart = match.Index + match.Length;
            var replacement = TryRedactStructuredAuthHeader(text, match.Index, valueStart, out var end);
            if (replacement is not null)
            {
                MarkBitmapRange(bitmap, valueStart, end);
            }
        }
    }

    private static Regex CreateRegex(string pattern, RegexOptions options) => new(pattern, options, RegexTimeout);

    private static List<RedactRegex> BuildDefaultPatterns()
    {
        var envAssignmentPattern = @"\b[A-Z0-9_]*(?:KEY|TOKEN|SECRET|PASSWORD|PASSWD|" + PaymentCredentialEnvKeys + @")\b\s*[=:]\s*([""']?)([^\s""'\\]+)\1";
        var escapedEnvAssignmentPattern = @"\b[A-Z0-9_]*(?:KEY|TOKEN|SECRET|PASSWORD|PASSWD|" + PaymentCredentialEnvKeys + @")\b\s*[=:]\s*\\+([""'])([^\s""'\\]+)\\+\1";
        var standaloneAssignmentQuotedPattern = @"(^|[\s,;])(?:" + StandaloneAssignmentSecretKeys + @")=([""'\x60])((?:(?!\2)[^\r\n])+)\2";
        var standaloneAssignmentPattern = @"(^|[\s,;])(?:" + StandaloneAssignmentSecretKeys + @")=([""'\x60]?[^\s&#""'\x60<>]+)";
        var telegramBotTokenPattern = @"\bbot(\d{6,}:[A-Za-z0-9_-]{20,})\b";
        var telegramTokenPattern = @"\b(\d{6,}:[A-Za-z0-9_-]{20,})\b";
        var authorizationBearerPattern = @"Authorization(?:\\+)?[""']?[ \t]*[:=](?:[ \t]|\\[trn]|\r?\n[ \t]*)*(?:\\+)?[""']?Bearer(?:[ \t]|\\[trn]|\r?\n[ \t]*)+((?>[-A-Za-z0-9._~+/=:]+))(?!…)";
        var authorizationBasicPattern = @"Authorization(?:\\+)?[""']?[ \t]*[:=](?:[ \t]|\\[trn]|\r?\n[ \t]*)*(?:\\+)?[""']?Basic(?:[ \t]|\\[trn]|\r?\n[ \t]*)+((?>[-A-Za-z0-9._~+/=:]+))(?!…)";
        var authorizationBotPattern = @"Authorization(?:\\+)?[""']?[ \t]*[:=](?:[ \t]|\\[trn]|\r?\n[ \t]*)*(?:\\+)?[""']?Bot(?:[ \t]|\\[trn]|\r?\n[ \t]*)+((?>[-A-Za-z0-9._~+/=:]+))(?!…)";
        var standaloneBearerPattern = @"\bBearer\s+([-A-Za-z0-9._~+/=]{18,})(?![-A-Za-z0-9._~+/=])";
        var pemNewlinePattern = @"(?:\r?\n|\\r\\n|\\n)";
        var pemBase64LinePattern =
            @"(?=[A-Za-z0-9+/])(?:[A-Za-z0-9+/]{4})*"
            + @"(?:[A-Za-z0-9+/]{2}==|[A-Za-z0-9+/]{3}=)?";
        var pemPrivateKeyPattern =
            @"-----BEGIN (?<pemType>(?:[A-Z0-9]+ )*PRIVATE KEY)-----"
            + pemNewlinePattern
            + @"(?:Proc-Type:[ \t]*4,ENCRYPTED" + pemNewlinePattern
            + @"DEK-Info:[ \t]*[A-Z0-9-]{3,32},[A-F0-9]{16,64}"
            + pemNewlinePattern + pemNewlinePattern + @")?"
            + @"(?:" + pemBase64LinePattern + pemNewlinePattern + @"){1,4096}"
            + @"-----END \k<pemType>-----";

        var patterns = new List<RedactRegex>
        {
            Add(envAssignmentPattern, DefaultRegexOptions, shellReferencePreserving: true),
            Add(escapedEnvAssignmentPattern, DefaultRegexOptions, shellReferencePreserving: true),
            Add(@"[?&](?:" + AuthQueryKeys + @"|" + PaymentCredentialQueryKeys + @")=([^&#\s<>]+)", IgnoreCaseRegexOptions),
            Add(@"""(?:apiKey|api_key|apiToken|api_token|bearerToken|bearer_token|token|secret|password|passwd|credential|authorization|accessToken|access_token|refreshToken|refresh_token|idToken|id_token|authToken|auth_token|clientSecret|client_secret|privateKey|private_key|secret_value|raw_secret|secret_input|key_material|" + PaymentCredentialJsonKeys + @")""\s*:\s*""([^""]+)""", IgnoreCaseRegexOptions),
            Add(@"(^|[\s,{])[""']?(?:api[-_]key|access[-_]token|refresh[-_]token|id[-_]token|authToken|auth[-_]token|clientSecret|client[-_]secret|appSecret|app[-_]secret|private[-_]key|credential|authorization|secret[-_]value|raw[-_]secret|secret[-_]input|key[-_]material)[""']?\s*[:=]\s*([""'])([^""'\r\n]+)\2", IgnoreCaseRegexOptions),
            Add(@"(^|[\s,{])[""']?(?:authorization|proxy-authorization|cookie|set-cookie|x-api-key|x-auth-token)[""']?\s*[:=]\s*([""'])([^""'\r\n]+)\2", IgnoreCaseRegexOptions),
            Add(@"--(?:api[-_]?key|hook[-_]?token|access[-_]?token|refresh[-_]?token|id[-_]?token|token|secret|password|passwd|credential|private[-_]?key|client[-_]?secret|" + PaymentCredentialQueryKeys + @")\s+(?!(?:or|and)\b(?=\s+--))([""']?)([^\s""']+)\1", IgnoreCaseRegexOptions),
            Add(authorizationBearerPattern, IgnoreCaseRegexOptions),
            Add(authorizationBasicPattern, IgnoreCaseRegexOptions),
            Add(authorizationBotPattern, IgnoreCaseRegexOptions),
            Add(@"(?:^|[\s({\[,])Proxy-Authorization(?:\\+)?[""']?[ \t]*[:=](?:[ \t]|\\[trn]|\r?\n[ \t]*)*(?:\\+)?[""']?(?![A-Za-z][A-Za-z0-9+.-]*\s+\*\*\*)(?:[A-Za-z][A-Za-z0-9+.-]*\s+((?>[-A-Za-z0-9._~+/=:]+))|(?![A-Za-z][A-Za-z0-9+.-]*\s+[-A-Za-z0-9._~+/=:])((?>[-A-Za-z0-9._~+/=:]+)))(?!…)", IgnoreCaseRegexOptions),
            Add(@"(?:^|[\s({\[,])Authorization(?:\\+)?[""']?[ \t]*[:=](?:[ \t]|\\[trn]|\r?\n[ \t]*)*(?:\\+)?[""']?(?!(?:Bearer|Basic|Bot)(?:[ \t]|\\[trn]|\r?\n[ \t]*))(?![A-Za-z][A-Za-z0-9+.-]*\s+\*\*\*)(?:[A-Za-z][A-Za-z0-9+.-]*\s+((?>[-A-Za-z0-9._~+/=:]+))|(?![A-Za-z][A-Za-z0-9+.-]*\s+[-A-Za-z0-9._~+/=:])((?>[-A-Za-z0-9._~+/=:]+)))(?!…)", IgnoreCaseRegexOptions),
            Add(@"(^|[\s,{])(?:x-authorization|api-key|x-goog-api-key|x-access-token|x-api-key|x-auth-token)\s*[:=]\s*([^\s""',;]+)", IgnoreCaseRegexOptions),
            Add(@"(?:X-OpenClaw-Token|x-pomerium-jwt-assertion|X-Api-Key|X-Auth-Token)\s*[:=]\s*([^\s""',;]+)", IgnoreCaseRegexOptions),
            Add(standaloneBearerPattern, IgnoreCaseRegexOptions),
            Add(@"\b(?:https?|wss?|ftp):\/\/[^\/\s:@]*:([^\/\s@]+)@", IgnoreCaseRegexOptions),
            Add(@"\b(?:postgres(?:ql)?|mysql|mongodb(?:\+srv)?|rediss?|amqps?):\/\/[^:\s/@]*:([^@\s]+)@", IgnoreCaseRegexOptions),
            Add(@"(^|[\s,;])(?:" + FormBodyFirstPairKeys + @")=([^&\s]+)(?=&[A-Za-z_][A-Za-z0-9_.-]*=)", IgnoreCaseRegexOptions),
            Add(standaloneAssignmentQuotedPattern, IgnoreCaseRegexOptions, shellReferencePreserving: true),
            Add(standaloneAssignmentPattern, IgnoreCaseRegexOptions, shellReferencePreserving: true),
            Add(pemPrivateKeyPattern, IgnoreCaseRegexOptions),
            Add(@"(?<![A-Za-z0-9_])(sk-[A-Za-z0-9_-]{8,})(?![A-Za-z0-9_])", IgnoreCaseRegexOptions),
            Add(@"(ghp_[A-Za-z0-9]{10,})", IgnoreCaseRegexOptions),
            Add(@"(github_pat_[A-Za-z0-9_]{10,})", IgnoreCaseRegexOptions),
            Add(@"(gho_[A-Za-z0-9]{10,})", IgnoreCaseRegexOptions),
            Add(@"(ghu_[A-Za-z0-9]{10,})", IgnoreCaseRegexOptions),
            Add(@"(ghs_[A-Za-z0-9]{10,})", IgnoreCaseRegexOptions),
            Add(@"(ghr_[A-Za-z0-9]{10,})", IgnoreCaseRegexOptions),
            Add(@"(glpat-[A-Za-z0-9._=\-]{20,})", IgnoreCaseRegexOptions),
            Add(@"(gloas-[A-Fa-f0-9]{32,})", IgnoreCaseRegexOptions),
            Add(@"(xox[baprs]-[A-Za-z0-9-]{10,})", IgnoreCaseRegexOptions),
            Add(@"(xapp-[A-Za-z0-9-]{10,})", IgnoreCaseRegexOptions),
            Add(@"(https:\/\/hooks\.slack\.com\/(?:services\/T[A-Z0-9]+\/B[A-Z0-9]+|workflows\/T[A-Z0-9]+\/A[A-Z0-9]+\/[0-9]{17,19})\/[A-Za-z0-9]{20,})", IgnoreCaseRegexOptions),
            Add(@"(https:\/\/discord(?:app)?\.com\/api\/webhooks\/[0-9]{17,20}\/[A-Za-z0-9_-]{60,})", IgnoreCaseRegexOptions),
            Add(@"discord[\s\S]{0,40}?\b([A-Za-z0-9_-]{24}\.[A-Za-z0-9_-]{6}\.[A-Za-z0-9_-]{27})\b", IgnoreCaseRegexOptions),
            Add(@"(gsk_[A-Za-z0-9_-]{10,})", IgnoreCaseRegexOptions),
            Add(@"(AIza[0-9A-Za-z\-_]{20,})", IgnoreCaseRegexOptions),
            Add(@"(ya29\.[0-9A-Za-z_\-./+=]{10,})", IgnoreCaseRegexOptions),
            Add(@"(1//0[0-9A-Za-z_\-./+=]{10,})", IgnoreCaseRegexOptions),
            Add(@"(eyJ[A-Za-z0-9_-]{10,}\.[A-Za-z0-9_-]{10,}\.[A-Za-z0-9_-]{10,})", IgnoreCaseRegexOptions),
            Add(@"(pplx-[A-Za-z0-9_-]{10,})", IgnoreCaseRegexOptions),
            Add(@"(fal_[A-Za-z0-9_-]{10,})", IgnoreCaseRegexOptions),
            Add(@"(fc-[A-Za-z0-9]{10,})", IgnoreCaseRegexOptions),
            Add(@"(bb_live_[A-Za-z0-9_-]{10,})", IgnoreCaseRegexOptions),
            Add(Base64SafeTokenBoundary + @"(gAAAA[A-Za-z0-9_=-]{20,})", IgnoreCaseRegexOptions, base64Boundary: true),
            Add(@"(sk_live_[A-Za-z0-9]{10,})", IgnoreCaseRegexOptions),
            Add(@"(sk_test_[A-Za-z0-9]{10,})", IgnoreCaseRegexOptions),
            Add(@"(rk_live_[A-Za-z0-9]{10,})", IgnoreCaseRegexOptions),
            Add(@"(SG\.[A-Za-z0-9_-]{10,}\.[A-Za-z0-9_-]{10,})", IgnoreCaseRegexOptions),
            Add(@"(npm_[A-Za-z0-9]{10,})", IgnoreCaseRegexOptions),
            Add(@"(pypi-[A-Za-z0-9_-]{10,})", IgnoreCaseRegexOptions),
            Add(@"(dop_v1_[A-Za-z0-9]{10,})", IgnoreCaseRegexOptions),
            Add(@"(doo_v1_[A-Za-z0-9]{10,})", IgnoreCaseRegexOptions),
            Add(@"(dor_v1_[A-Za-z0-9]{10,})", IgnoreCaseRegexOptions),
            Add(@"(dp\.(?:ct|pt|sa|scim|audit)\.[A-Za-z0-9]{40,44})", IgnoreCaseRegexOptions),
            Add(@"(dp\.st\.[A-Za-z0-9]{40,44})", IgnoreCaseRegexOptions),
            Add(@"(dp\.st\.[a-z0-9_-]{2,35}\.[A-Za-z0-9]{40,44})", IgnoreCaseRegexOptions),
            Add(@"(dckr_(?:pat|oat)_[A-Za-z0-9_-]{27,32})", IgnoreCaseRegexOptions),
            Add(@"(bkua_[a-z0-9]{40})", IgnoreCaseRegexOptions),
            Add(@"(CCIPAT_[A-Za-z0-9]{22}_[A-Fa-f0-9]{40})", IgnoreCaseRegexOptions),
            Add(@"(sbp_[a-z0-9]{40})", IgnoreCaseRegexOptions),
            Add(Base64SafeTokenBoundary + @"(dapi[0-9a-f]{32}(?:-\d)?)", IgnoreCaseRegexOptions, base64Boundary: true),
            Add(@"(dd[pw]_[A-Za-z0-9]{36})", IgnoreCaseRegexOptions),
            Add(@"(glsa_[A-Za-z0-9_]{41})", IgnoreCaseRegexOptions),
            Add(@"(glc_eyJ[A-Za-z0-9+/=]{60,160})", IgnoreCaseRegexOptions),
            Add(@"(nfp_[A-Za-z0-9_]{36})", IgnoreCaseRegexOptions),
            Add(@"(CFPAT-[A-Za-z0-9_\-]{40,})", IgnoreCaseRegexOptions),
            Add(Base64SafeTokenBoundary + @"(ATCTT3xFfG[A-Za-z0-9+/=_-]+=[A-Za-z0-9]{8})", IgnoreCaseRegexOptions, base64Boundary: true),
            Add(Base64SafeTokenBoundary + @"(ATATT[A-Za-z0-9+/=_-]+=[A-Za-z0-9]{8})", IgnoreCaseRegexOptions, base64Boundary: true),
            Add(Base64SafeTokenBoundary + @"(ATBB[A-Za-z0-9_=.-]{16,})", IgnoreCaseRegexOptions, base64Boundary: true),
            Add(@"(BBDC-[A-Za-z0-9+/@_-]{40,50})", IgnoreCaseRegexOptions),
            Add(@"(HRKU-AA[A-Za-z0-9_-]{20,})", IgnoreCaseRegexOptions),
            Add(@"(pat-(?:eu|na)1-[A-Za-z0-9]{8}\-[A-Za-z0-9]{4}\-[A-Za-z0-9]{4}\-[A-Za-z0-9]{4}\-[A-Za-z0-9]{12})", IgnoreCaseRegexOptions),
            Add(@"(apify_api_[A-Za-z0-9\-]{20,})", IgnoreCaseRegexOptions),
            Add(@"(FlyV1 fm\d+_[A-Za-z0-9+/=,_-]{100,})", IgnoreCaseRegexOptions),
            Add(@"(fio-u-[A-Za-z0-9_-]{40,})", IgnoreCaseRegexOptions),
            Add(@"(^|[^A-Za-z0-9_])(am_[A-Za-z0-9_-]{10,})", IgnoreCaseRegexOptions),
            Add(@"(^|[^A-Za-z0-9_])(sk_[A-Za-z0-9_]{10,})", IgnoreCaseRegexOptions),
            Add(@"(tvly-[A-Za-z0-9]{10,})", IgnoreCaseRegexOptions),
            Add(@"(exa_[A-Za-z0-9]{10,})", IgnoreCaseRegexOptions),
            Add(@"(syt_[A-Za-z0-9]{10,})", IgnoreCaseRegexOptions),
            Add(@"(retaindb_[A-Za-z0-9]{10,})", IgnoreCaseRegexOptions),
            Add(@"(hsk-[A-Za-z0-9]{10,})", IgnoreCaseRegexOptions),
            Add(@"(mem0_[A-Za-z0-9]{10,})", IgnoreCaseRegexOptions),
            Add(@"(brv_[A-Za-z0-9]{10,})", IgnoreCaseRegexOptions),
            Add(@"(xai-[A-Za-z0-9]{30,})", IgnoreCaseRegexOptions),
            Add(IdentifierSafeTokenBoundary + @"(fw-[A-Za-z0-9]{30,})", IgnoreCaseRegexOptions),
            Add(IdentifierSafeTokenBoundary + @"(fw_[A-Za-z0-9]{30,})", IgnoreCaseRegexOptions),
            Add(IdentifierSafeTokenBoundary + @"(fpk_[A-Za-z0-9]{30,})", IgnoreCaseRegexOptions),
            Add(Base64SafeTokenBoundary + @"(AKIA[A-Z0-9]{16})", IgnoreCaseRegexOptions, base64Boundary: true),
            Add(Base64SafeTokenBoundary + @"(ASIA[A-Z0-9]{16})", IgnoreCaseRegexOptions, base64Boundary: true),
            Add(@"(AKID[A-Za-z0-9]{10,})", IgnoreCaseRegexOptions),
            Add(@"(LTAI[A-Za-z0-9]{10,})", IgnoreCaseRegexOptions),
            Add(@"(hf_[A-Za-z0-9]{10,})", IgnoreCaseRegexOptions),
            Add(@"(api_org_[A-Za-z0-9]{20,})", IgnoreCaseRegexOptions),
            Add(@"(r8_[A-Za-z0-9]{10,})", IgnoreCaseRegexOptions),
            Add(telegramBotTokenPattern, IgnoreCaseRegexOptions),
            Add(telegramTokenPattern, IgnoreCaseRegexOptions),
        };

        return patterns;
    }

    private static RedactRegex Add(string source, RegexOptions options, bool shellReferencePreserving = false, bool base64Boundary = false) =>
        new(CreateRegex(source, options), shellReferencePreserving, base64Boundary);

    private sealed record RedactRegex(Regex Regex, bool ShellReferencePreserving = false, bool Base64Boundary = false);

    private sealed record SecretCaptureSelection(int Index, string Value, int Start);

    private sealed record SecretValueParts(string Maskable, string Suffix, int MaskStart, int MaskEnd);
}
