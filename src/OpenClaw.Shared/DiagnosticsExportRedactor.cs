using System.Text;

namespace OpenClaw.Shared;

/// <summary>
/// Final defense-in-depth sanitizer for shareable diagnostics exports.
/// Primary protection happens before values are logged; this class handles
/// older/raw diagnostic text without making a large regex set the privacy boundary.
/// </summary>
public static class DiagnosticsExportRedactor
{
    private static readonly string[] SensitiveKeyFragments =
    [
        "authorization",
        "api-key",
        "apikey",
        "bearer",
        "bot-token",
        "bottoken",
        "browser-password",
        "browserpassword",
        "client-secret",
        "clientsecret",
        "cookie",
        "device-id",
        "deviceid",
        "dpapi",
        "identity",
        "jwt",
        "nonce",
        "node",
        "nsec",
        "openclawid",
        "password",
        "private-key",
        "privatekey",
        "raw-error-response",
        "raw_error_response",
        "relay-url",
        "relayurl",
        "request-id",
        "requestid",
        "secret",
        "session-key",
        "sessionkey",
        "setup-code",
        "setupcode",
        "signing",
        "token",
        "webhook",
        "x-api-key",
        "xapikey"
    ];

    private static readonly string[] SensitiveHeaders =
    [
        "authorization",
        "cookie",
        "proxy-authorization",
        "set-cookie",
        "x-api-key",
        "x-openclaw-token"
    ];

    public static string Sanitize(string? text)
    {
        if (string.IsNullOrEmpty(text))
            return text ?? string.Empty;

        var sanitized = DecodeCommonJsonEscapes(text);
        sanitized = RedactPrivateKeyBlocks(sanitized);
        sanitized = RedactSignedHandshakeLines(sanitized);
        sanitized = RedactDpapiBlobs(sanitized);
        sanitized = RedactAgentSessionKeys(sanitized);
        sanitized = RedactSensitiveCommandOptions(sanitized);
        sanitized = RedactSensitiveKeyValues(sanitized);
        sanitized = RedactGuidTokens(sanitized);
        return TokenSanitizer.SanitizeLogMessage(sanitized);
    }

    public static string RedactPath(string? pathOrText) =>
        TokenSanitizer.SanitizeLogMessage(pathOrText);

    private static string DecodeCommonJsonEscapes(string text)
    {
        var decoded = text;
        for (var pass = 0; pass < 3; pass++)
        {
            var next = DecodeCommonJsonEscapePass(decoded);
            if (string.Equals(next, decoded, StringComparison.Ordinal))
                return next;

            decoded = next;
        }

        return decoded;
    }

    private static string DecodeCommonJsonEscapePass(string text)
    {
        var builder = new StringBuilder(text.Length);
        for (var i = 0; i < text.Length; i++)
        {
            var current = text[i];
            if (current != '\\' || i + 1 >= text.Length)
            {
                builder.Append(current);
                continue;
            }

            var next = text[i + 1];
            switch (next)
            {
                case '"':
                    builder.Append('"');
                    i++;
                    break;
                case ':':
                case ',':
                case '{':
                case '}':
                case '[':
                case ']':
                    builder.Append(next);
                    i++;
                    break;
                case 'r' when i + 3 < text.Length && text[i + 2] == '\\' && text[i + 3] == 'n':
                    builder.Append('\n');
                    i += 3;
                    break;
                case 'u' when TryDecodeUnicodeEscape(text, i + 2, out var decoded):
                    builder.Append(decoded);
                    i += 5;
                    break;
                default:
                    builder.Append(current);
                    break;
            }
        }

        return builder.ToString();
    }

    private static bool TryDecodeUnicodeEscape(string text, int start, out char decoded)
    {
        decoded = default;
        if (start + 4 > text.Length)
            return false;

        var value = 0;
        for (var i = start; i < start + 4; i++)
        {
            var digit = HexValue(text[i]);
            if (digit < 0)
                return false;
            value = (value << 4) + digit;
        }

        if (value < ' ' || value == '\\')
            return false;

        decoded = (char)value;
        return true;
    }

    private static int HexValue(char c) =>
        c switch
        {
            >= '0' and <= '9' => c - '0',
            >= 'a' and <= 'f' => c - 'a' + 10,
            >= 'A' and <= 'F' => c - 'A' + 10,
            _ => -1
        };

    private static string RedactPrivateKeyBlocks(string text)
    {
        const string beginMarker = "-----BEGIN ";
        const string endPrefix = "-----END ";
        const string endSuffix = "-----";

        var builder = new StringBuilder(text.Length);
        var index = 0;
        while (index < text.Length)
        {
            var begin = text.IndexOf(beginMarker, index, StringComparison.OrdinalIgnoreCase);
            if (begin < 0)
            {
                builder.Append(text, index, text.Length - index);
                break;
            }

            var beginLineEnd = text.IndexOf(endSuffix, begin + beginMarker.Length, StringComparison.Ordinal);
            if (beginLineEnd < 0 ||
                !text.AsSpan(begin, beginLineEnd + endSuffix.Length - begin).Contains("PRIVATE KEY", StringComparison.OrdinalIgnoreCase))
            {
                builder.Append(text, index, begin + beginMarker.Length - index);
                index = begin + beginMarker.Length;
                continue;
            }

            var end = text.IndexOf(endPrefix, beginLineEnd + endSuffix.Length, StringComparison.OrdinalIgnoreCase);
            if (end < 0)
            {
                builder.Append(text, index, begin - index);
                builder.Append("[REDACTED_PRIVATE_KEY]");
                break;
            }

            var endLineEnd = text.IndexOf(endSuffix, end + endPrefix.Length, StringComparison.Ordinal);
            builder.Append(text, index, begin - index);
            builder.Append("[REDACTED_PRIVATE_KEY]");
            index = endLineEnd < 0 ? text.Length : endLineEnd + endSuffix.Length;
        }

        return builder.ToString();
    }

    private static string RedactSignedHandshakeLines(string text)
    {
        const string marker = "signed:";
        var builder = new StringBuilder(text.Length);
        var index = 0;
        while (index < text.Length)
        {
            var start = text.IndexOf(marker, index, StringComparison.OrdinalIgnoreCase);
            if (start < 0)
            {
                builder.Append(text, index, text.Length - index);
                break;
            }

            var lineEnd = FindLineEnd(text, start);
            builder.Append(text, index, start - index);
            builder.Append("signed: [REDACTED_HANDSHAKE]");
            builder.Append(text, lineEnd, FindLineBreakLength(text, lineEnd));
            index = lineEnd + FindLineBreakLength(text, lineEnd);
        }

        return builder.ToString();
    }

    private static string RedactDpapiBlobs(string text) =>
        RedactPrefixedToken(text, "dpapi:", "dpapi:[REDACTED]");

    private static string RedactAgentSessionKeys(string text) =>
        RedactPrefixedToken(text, "agent:", "[REDACTED_SESSION_KEY]");

    private static string RedactPrefixedToken(string text, string prefix, string replacement)
    {
        var builder = new StringBuilder(text.Length);
        var index = 0;
        while (index < text.Length)
        {
            var start = text.IndexOf(prefix, index, StringComparison.OrdinalIgnoreCase);
            if (start < 0)
            {
                builder.Append(text, index, text.Length - index);
                break;
            }

            var end = start + prefix.Length;
            while (end < text.Length && !IsValueTerminator(text[end]))
                end++;

            builder.Append(text, index, start - index);
            builder.Append(replacement);
            index = end;
        }

        return builder.ToString();
    }

    private static string RedactSensitiveCommandOptions(string text)
    {
        var builder = new StringBuilder(text.Length);
        var index = 0;
        while (index < text.Length)
        {
            var optionStart = text.IndexOf("--", index, StringComparison.Ordinal);
            if (optionStart < 0)
            {
                builder.Append(text, index, text.Length - index);
                break;
            }

            var optionEnd = optionStart + 2;
            while (optionEnd < text.Length && IsKeyChar(text[optionEnd]))
                optionEnd++;

            var optionName = text[(optionStart + 2)..optionEnd];
            if (!IsSensitiveKey(optionName))
            {
                builder.Append(text, index, optionEnd - index);
                index = optionEnd;
                continue;
            }

            var valueStart = optionEnd;
            while (valueStart < text.Length && char.IsWhiteSpace(text[valueStart]))
                valueStart++;

            if (valueStart >= text.Length || IsValueTerminator(text[valueStart]))
            {
                builder.Append(text, index, valueStart - index);
                index = valueStart;
                continue;
            }

            var (valueContentStart, valueEnd) = FindValueSpan(text, valueStart);
            if (IsAlreadyRedacted(text, valueContentStart, valueEnd))
            {
                builder.Append(text, index, valueEnd - index);
                index = valueEnd;
                continue;
            }

            builder.Append(text, index, valueContentStart - index);
            builder.Append("[REDACTED]");
            if (valueEnd < text.Length && IsQuote(text[valueEnd]))
            {
                builder.Append(text[valueEnd]);
                valueEnd++;
            }

            index = valueEnd;
        }

        return builder.ToString();
    }

    private static string RedactSensitiveKeyValues(string text)
    {
        var builder = new StringBuilder(text.Length);
        var index = 0;
        while (index < text.Length)
        {
            var delimiter = FindNextKeyValueDelimiter(text, index);
            if (delimiter < 0)
            {
                builder.Append(text, index, text.Length - index);
                break;
            }

            var keyStart = FindKeyStart(text, delimiter - 1);
            var key = NormalizeKey(text[keyStart..delimiter]);
            var isSensitiveHeader = IsSensitiveHeader(key);
            if (!IsSensitiveKey(key) && !isSensitiveHeader)
            {
                builder.Append(text, index, delimiter + 1 - index);
                index = delimiter + 1;
                continue;
            }

            var valueStart = delimiter + 1;
            while (valueStart < text.Length && char.IsWhiteSpace(text[valueStart]))
                valueStart++;

            if (valueStart >= text.Length)
            {
                builder.Append(text, index, valueStart - index);
                index = valueStart;
                continue;
            }

            var (valueContentStart, valueEnd) = isSensitiveHeader
                ? (valueStart, FindLineEnd(text, valueStart))
                : FindValueSpan(text, valueStart);
            if (IsAlreadyRedacted(text, valueContentStart, valueEnd))
            {
                builder.Append(text, index, valueEnd - index);
                index = valueEnd;
                continue;
            }

            builder.Append(text, index, valueContentStart - index);
            builder.Append("[REDACTED]");
            if (valueEnd < text.Length && IsQuote(text[valueEnd]))
            {
                builder.Append(text[valueEnd]);
                valueEnd++;
            }

            index = valueEnd;
        }

        return builder.ToString();
    }

    private static string RedactGuidTokens(string text)
    {
        var builder = new StringBuilder(text.Length);
        var index = 0;
        while (index < text.Length)
        {
            if (!IsGuidCandidateStart(text, index) ||
                index + 36 > text.Length ||
                !Guid.TryParse(text.AsSpan(index, 36), out _))
            {
                builder.Append(text[index]);
                index++;
                continue;
            }

            builder.Append("[REDACTED_ID]");
            index += 36;
        }

        return builder.ToString();
    }

    private static int FindNextKeyValueDelimiter(string text, int start)
    {
        for (var i = start; i < text.Length; i++)
        {
            if ((text[i] == ':' || text[i] == '=') && i > 0 && IsKeyCharOrQuote(text[i - 1]))
                return i;
        }

        return -1;
    }

    private static int FindKeyStart(string text, int index)
    {
        while (index >= 0 && IsKeyCharOrQuote(text[index]))
            index--;
        return index + 1;
    }

    private static string NormalizeKey(string key) =>
        key.Trim().Trim('"', '\'').Replace("_", "-", StringComparison.Ordinal).ToLowerInvariant();

    private static bool IsSensitiveKey(string key)
    {
        var normalized = NormalizeKey(key);
        if (normalized == "id" || normalized.EndsWith("-id", StringComparison.Ordinal))
            return true;

        foreach (var fragment in SensitiveKeyFragments)
        {
            if (normalized.Contains(fragment, StringComparison.Ordinal))
                return true;
        }

        return false;
    }

    private static bool IsSensitiveHeader(string key)
    {
        var normalized = NormalizeKey(key);
        foreach (var header in SensitiveHeaders)
        {
            if (string.Equals(normalized, header, StringComparison.Ordinal))
                return true;
        }

        return false;
    }

    private static (int ContentStart, int End) FindValueSpan(string text, int valueStart)
    {
        if (valueStart >= text.Length)
            return (valueStart, valueStart);

        if (text.AsSpan(valueStart).StartsWith("[REDACTED", StringComparison.Ordinal))
        {
            var markerEnd = text.IndexOf(']', valueStart);
            if (markerEnd >= 0)
                return (valueStart, markerEnd + 1);
        }

        if (IsQuote(text[valueStart]))
        {
            var quote = text[valueStart];
            var endQuote = valueStart + 1;
            while (endQuote < text.Length && text[endQuote] != quote)
                endQuote++;
            return (valueStart + 1, endQuote);
        }

        var end = valueStart;
        while (end < text.Length && !IsValueTerminator(text[end]))
            end++;
        return (valueStart, end);
    }

    private static bool IsAlreadyRedacted(string text, int valueContentStart, int valueEnd) =>
        valueContentStart < valueEnd &&
        text.AsSpan(valueContentStart, valueEnd - valueContentStart).StartsWith("[REDACTED", StringComparison.Ordinal);

    private static int FindLineEnd(string text, int start)
    {
        var index = start;
        while (index < text.Length && text[index] != '\r' && text[index] != '\n')
            index++;
        return index;
    }

    private static int FindLineBreakLength(string text, int lineEnd)
    {
        if (lineEnd >= text.Length)
            return 0;
        if (text[lineEnd] == '\r' && lineEnd + 1 < text.Length && text[lineEnd + 1] == '\n')
            return 2;
        return 1;
    }

    private static bool IsGuidCandidateStart(string text, int index) =>
        (index == 0 || !IsHexOrDash(text[index - 1])) && IsHex(text[index]);

    private static bool IsHexOrDash(char c) => IsHex(c) || c == '-';

    private static bool IsHex(char c) =>
        c is >= '0' and <= '9' or >= 'a' and <= 'f' or >= 'A' and <= 'F';

    private static bool IsKeyCharOrQuote(char c) => IsKeyChar(c) || IsQuote(c);

    private static bool IsKeyChar(char c) =>
        char.IsLetterOrDigit(c) || c is '_' or '-' or '.';

    private static bool IsQuote(char c) => c is '"' or '\'';

    private static bool IsValueTerminator(char c) =>
        char.IsWhiteSpace(c) || c is ',' or ';' or '}' or ']';
}
