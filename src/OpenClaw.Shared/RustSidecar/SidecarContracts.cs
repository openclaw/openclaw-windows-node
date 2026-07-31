using System.Buffers;
using System.Globalization;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

namespace OpenClaw.Shared.RustSidecar;

internal sealed record SidecarLimits(
    uint MaxFrameBytes,
    ushort MaxInFlight,
    uint BootstrapTimeoutMs);

internal sealed record SidecarPeerIdentity(
    SidecarPeerRole Role,
    string Name,
    string Version,
    string ArtifactIdentity);

internal sealed record SidecarProtocolOffer(
    ushort ProtocolMajor,
    ushort ProtocolMinor,
    SidecarPeerIdentity Peer,
    ulong FeatureBits,
    SidecarLimits Limits);

internal sealed record SidecarProtocolSelection(
    ushort ProtocolMajor,
    ushort ProtocolMinor,
    ulong FeatureBits,
    SidecarLimits Limits);

internal sealed record SidecarRuntimeConfiguration(
    ulong ManifestGeneration,
    IReadOnlyList<string> Capabilities,
    IReadOnlyList<string> Commands,
    ushort MaxConcurrency,
    uint MaxInputBytes,
    uint MaxOutputBytes,
    uint DefaultTimeoutMs,
    uint MaxTimeoutMs,
    uint ResultGraceMs)
{
    internal JsonObject ToConfigureMessage()
    {
        var capabilities = new JsonArray(
            Capabilities.Select(value => (JsonNode?)JsonValue.Create(value)).ToArray());
        var commands = new JsonArray(
            Commands.Select(name => (JsonNode)new JsonObject { ["name"] = name }).ToArray());
        return new JsonObject
        {
            ["type"] = "configure",
            ["configuration"] = new JsonObject
            {
                ["manifestGeneration"] = ManifestGeneration,
                ["capabilities"] = capabilities,
                ["commands"] = commands,
                ["maxConcurrency"] = MaxConcurrency,
                ["maxInputBytes"] = MaxInputBytes,
                ["maxOutputBytes"] = MaxOutputBytes,
                ["defaultTimeoutMs"] = DefaultTimeoutMs,
                ["maxTimeoutMs"] = MaxTimeoutMs,
                ["resultGraceMs"] = ResultGraceMs
            }
        };
    }

    internal JsonObject ToManifest() => new()
    {
        ["manifestGeneration"] = ManifestGeneration,
        ["capabilities"] = new JsonArray(
            Capabilities.Select(value => (JsonNode?)JsonValue.Create(value)).ToArray()),
        ["commands"] = new JsonArray(
            Commands.Select(value => (JsonNode?)JsonValue.Create(value)).ToArray())
    };
}

internal static class SidecarJson
{
    internal const ulong MaxPortableInteger = 9_007_199_254_740_991;
    // serde_json starts with 128 remaining levels and rejects the 128th container.
    internal const int MaxDepth = 127;
    private const long MinimumCanonicalizationHeadroomBytes = 4 * 1024;
    private const long MaximumCanonicalizationHeadroomBytes = 64 * 1024 * 1024;
    private static readonly AsyncLocal<CanonicalizationBudget?> CurrentCanonicalizationBudget = new();

    internal static readonly JsonSerializerOptions SerializerOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = false,
        MaxDepth = MaxDepth,
        Encoder = SerdeJsonEncoder.Instance,
        Converters =
        {
            SerdeDoubleConverter.Instance,
            SerdeSingleConverter.Instance,
            SerdeDecimalConverter.Instance,
            SerdeJsonElementConverter.Instance,
            SerdeJsonDocumentConverter.Instance,
            SerdeJsonNodeConverterFactory.Instance
        }
    };

    internal static byte[] Serialize(JsonNode node) =>
        JsonSerializer.SerializeToUtf8Bytes(node, SerializerOptions);

    internal static IDisposable BeginCanonicalizationBudget(uint maxOutputBytes)
    {
        var previous = CurrentCanonicalizationBudget.Value;
        var proportionalHeadroom = checked((long)maxOutputBytes * 7);
        var headroom = Math.Clamp(
            proportionalHeadroom,
            MinimumCanonicalizationHeadroomBytes,
            MaximumCanonicalizationHeadroomBytes);
        CurrentCanonicalizationBudget.Value = new CanonicalizationBudget(
            checked((long)maxOutputBytes + headroom));
        return new CanonicalizationBudgetScope(previous);
    }

    internal static JsonElement Parse(ReadOnlySpan<byte> json)
    {
        using var document = JsonDocument.Parse(
            json.ToArray(),
            new JsonDocumentOptions { MaxDepth = MaxDepth });
        return document.RootElement.Clone();
    }

    internal static bool ValueEquals(JsonElement left, JsonElement right)
    {
        if (left.ValueKind != right.ValueKind)
            return false;
        return left.ValueKind switch
        {
            JsonValueKind.Object => ObjectEquals(left, right),
            JsonValueKind.Array => left.EnumerateArray().SequenceEqual(
                right.EnumerateArray(),
                JsonElementValueComparer.Instance),
            JsonValueKind.String => left.GetString() == right.GetString(),
            JsonValueKind.Number => NumberEquals(left, right),
            JsonValueKind.True or JsonValueKind.False => left.GetBoolean() == right.GetBoolean(),
            JsonValueKind.Null => true,
            _ => false
        };
    }

    internal static JsonElement NormalizeValue(JsonElement value)
    {
        var normalized = NormalizeNode(value);
        return normalized is null ? Parse("null"u8) : Parse(Serialize(normalized));
    }

    private static JsonNode? NormalizeNode(JsonElement value)
    {
        switch (value.ValueKind)
        {
            case JsonValueKind.Object:
                var jsonObject = new JsonObject();
                foreach (var property in value.EnumerateObject())
                    jsonObject[property.Name] = NormalizeNode(property.Value);
                return jsonObject;
            case JsonValueKind.Array:
                return new JsonArray(value.EnumerateArray().Select(NormalizeNode).ToArray());
            case JsonValueKind.Null:
                return null;
            case JsonValueKind.Number:
                return NormalizeNumber(value);
            default:
                return JsonNode.Parse(
                    value.GetRawText(),
                    documentOptions: new JsonDocumentOptions { MaxDepth = MaxDepth });
        }
    }

    private static JsonNode NormalizeNumber(JsonElement value)
    {
        return GetNumberKind(value) switch
        {
            JsonNumberKind.PositiveInteger when value.TryGetUInt64(out var unsigned) =>
                JsonValue.Create(unsigned),
            JsonNumberKind.NegativeInteger when value.TryGetInt64(out var signed) =>
                JsonValue.Create(signed),
            JsonNumberKind.Float when value.TryGetDouble(out var floating) &&
                double.IsFinite(floating) => JsonNode.Parse(FormatSerdeFloat(floating))!,
            _ => JsonNode.Parse(
                value.GetRawText(),
                documentOptions: new JsonDocumentOptions { MaxDepth = MaxDepth })!
        };
    }

    private static bool ObjectEquals(JsonElement left, JsonElement right)
    {
        var leftProperties = ToPropertyMap(left);
        var rightProperties = ToPropertyMap(right);
        return leftProperties.Count == rightProperties.Count &&
            leftProperties.All(property =>
                rightProperties.TryGetValue(property.Key, out var value) &&
                ValueEquals(property.Value, value));
    }

    private static Dictionary<string, JsonElement> ToPropertyMap(JsonElement value)
    {
        var properties = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
        foreach (var property in value.EnumerateObject())
            properties[property.Name] = property.Value;
        return properties;
    }

    private static bool NumberEquals(JsonElement left, JsonElement right)
    {
        var leftKind = GetNumberKind(left);
        if (leftKind != GetNumberKind(right))
            return false;
        return leftKind switch
        {
            JsonNumberKind.PositiveInteger =>
                left.TryGetUInt64(out var leftUnsigned) &&
                right.TryGetUInt64(out var rightUnsigned) &&
                leftUnsigned == rightUnsigned,
            JsonNumberKind.NegativeInteger =>
                left.TryGetInt64(out var leftSigned) &&
                right.TryGetInt64(out var rightSigned) &&
                leftSigned == rightSigned,
            _ => left.TryGetDouble(out var leftFloat) &&
                right.TryGetDouble(out var rightFloat) &&
                leftFloat.Equals(rightFloat)
        };
    }

    private static JsonNumberKind GetNumberKind(JsonElement value)
    {
        var raw = value.GetRawText();
        if (raw.Contains('.') || raw.Contains('e') || raw.Contains('E') || raw == "-0")
            return JsonNumberKind.Float;
        return raw[0] == '-' ? JsonNumberKind.NegativeInteger : JsonNumberKind.PositiveInteger;
    }

    private enum JsonNumberKind
    {
        PositiveInteger,
        NegativeInteger,
        Float
    }

    private sealed class JsonElementValueComparer : IEqualityComparer<JsonElement>
    {
        internal static readonly JsonElementValueComparer Instance = new();
        public bool Equals(JsonElement x, JsonElement y) => ValueEquals(x, y);
        public int GetHashCode(JsonElement obj) => throw new NotSupportedException();
    }

    private static string FormatSerdeFloat(double value)
    {
        if (!double.IsFinite(value))
            throw new JsonException("Sidecar JSON cannot encode non-finite floating-point values.");
        var text = value.ToString("R", CultureInfo.InvariantCulture);
        if (!double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed) ||
            BitConverter.DoubleToInt64Bits(parsed) != BitConverter.DoubleToInt64Bits(value))
            text = value.ToString("G17", CultureInfo.InvariantCulture);
        return FormatSerdeFloatText(
            text,
            minimumFixedExponent: -5,
            maximumFixedExponent: 15,
            maximumFixedIntegerDigits: 16);
    }

    private static string FormatSerdeFloat(float value)
    {
        if (!float.IsFinite(value))
            throw new JsonException("Sidecar JSON cannot encode non-finite floating-point values.");
        var text = value.ToString("R", CultureInfo.InvariantCulture);
        if (!float.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed) ||
            BitConverter.SingleToInt32Bits(parsed) != BitConverter.SingleToInt32Bits(value))
            text = value.ToString("G9", CultureInfo.InvariantCulture);
        return FormatSerdeFloatText(
            text,
            minimumFixedExponent: -6,
            maximumFixedExponent: 12,
            maximumFixedIntegerDigits: 13);
    }

    private static string FormatSerdeFloatText(
        string text,
        int minimumFixedExponent,
        int maximumFixedExponent,
        int maximumFixedIntegerDigits)
    {
        var exponentIndex = text.IndexOf('E');
        if (exponentIndex >= 0)
        {
            var exponent = int.Parse(
                text.AsSpan(exponentIndex + 1),
                NumberStyles.AllowLeadingSign,
                CultureInfo.InvariantCulture);
            var mantissa = text[..exponentIndex];
            if (exponent >= minimumFixedExponent && exponent <= maximumFixedExponent)
                return ExpandSerdeFloat(mantissa, exponent);
            return string.Concat(
                mantissa,
                exponent >= 0 ? "e+" : "e",
                exponent.ToString(CultureInfo.InvariantCulture));
        }
        var unsigned = text[0] == '-' ? text[1..] : text;
        var decimalPoint = unsigned.IndexOf('.');
        var integerDigits = decimalPoint >= 0 ? decimalPoint : unsigned.Length;
        if (integerDigits > maximumFixedIntegerDigits)
        {
            var digits = unsigned.Replace(".", string.Empty, StringComparison.Ordinal)
                .TrimEnd('0');
            var mantissa = digits.Length == 1
                ? digits
                : string.Concat(digits.AsSpan(0, 1), ".", digits.AsSpan(1));
            return string.Concat(
                text[0] == '-' ? "-" : string.Empty,
                mantissa,
                "e+",
                (integerDigits - 1).ToString(CultureInfo.InvariantCulture));
        }
        return text.Contains('.') ? text : string.Concat(text, ".0");
    }

    private static string ExpandSerdeFloat(string mantissa, int exponent)
    {
        var negative = mantissa[0] == '-';
        var digits = (negative ? mantissa[1..] : mantissa)
            .Replace(".", string.Empty, StringComparison.Ordinal);
        var decimalPoint = 1 + exponent;
        string expanded;
        if (decimalPoint <= 0)
        {
            expanded = string.Concat("0.", new string('0', -decimalPoint), digits);
        }
        else if (decimalPoint >= digits.Length)
        {
            expanded = string.Concat(
                digits,
                new string('0', decimalPoint - digits.Length),
                ".0");
        }
        else
        {
            expanded = string.Concat(
                digits.AsSpan(0, decimalPoint),
                ".",
                digits.AsSpan(decimalPoint));
        }
        return negative ? string.Concat("-", expanded) : expanded;
    }

    private sealed class SerdeDoubleConverter : JsonConverter<double>
    {
        internal static readonly SerdeDoubleConverter Instance = new();

        public override double Read(
            ref Utf8JsonReader reader,
            Type typeToConvert,
            JsonSerializerOptions options) => reader.GetDouble();

        public override void Write(
            Utf8JsonWriter writer,
            double value,
            JsonSerializerOptions options)
        {
            if (!double.IsFinite(value))
            {
                writer.WriteNullValue();
                return;
            }
            writer.WriteRawValue(FormatSerdeFloat(value));
        }
    }

    private sealed class SerdeSingleConverter : JsonConverter<float>
    {
        internal static readonly SerdeSingleConverter Instance = new();

        public override float Read(
            ref Utf8JsonReader reader,
            Type typeToConvert,
            JsonSerializerOptions options) => reader.GetSingle();

        public override void Write(
            Utf8JsonWriter writer,
            float value,
            JsonSerializerOptions options)
        {
            if (!float.IsFinite(value))
            {
                writer.WriteNullValue();
                return;
            }
            writer.WriteRawValue(FormatSerdeFloat(value));
        }
    }

    private sealed class SerdeDecimalConverter : JsonConverter<decimal>
    {
        internal static readonly SerdeDecimalConverter Instance = new();

        public override decimal Read(
            ref Utf8JsonReader reader,
            Type typeToConvert,
            JsonSerializerOptions options) => reader.GetDecimal();

        public override void Write(
            Utf8JsonWriter writer,
            decimal value,
            JsonSerializerOptions options) =>
            writer.WriteRawValue(FormatSerdeFloat(decimal.ToDouble(value)));
    }

    private sealed class SerdeJsonElementConverter : JsonConverter<JsonElement>
    {
        internal static readonly SerdeJsonElementConverter Instance = new();

        public override JsonElement Read(
            ref Utf8JsonReader reader,
            Type typeToConvert,
            JsonSerializerOptions options)
        {
            using var document = JsonDocument.ParseValue(ref reader);
            return document.RootElement.Clone();
        }

        public override void Write(
            Utf8JsonWriter writer,
            JsonElement value,
            JsonSerializerOptions options)
        {
            PreflightRawValue(value);
            WriteNormalizedValue(writer, value);
        }

        internal static void PreflightRawValue(JsonElement value)
        {
            var budget = CurrentCanonicalizationBudget.Value;
            if (budget is null)
                return;
            var buffer = new CanonicalizationBudgetBufferWriter(budget);
            using var rawWriter = new Utf8JsonWriter(buffer, new JsonWriterOptions
            {
                Encoder = SerdeJsonEncoder.Instance,
                MaxDepth = MaxDepth,
                SkipValidation = true
            });
            value.WriteTo(rawWriter);
            rawWriter.Flush();
        }

        internal static void WriteNormalizedValue(Utf8JsonWriter writer, JsonElement value)
        {
            switch (value.ValueKind)
            {
                case JsonValueKind.Object:
                    writer.WriteStartObject();
                    var properties = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
                    foreach (var property in value.EnumerateObject())
                        properties[property.Name] = property.Value;
                    foreach (var property in properties)
                    {
                        writer.WritePropertyName(property.Key);
                        WriteNormalizedValue(writer, property.Value);
                    }
                    writer.WriteEndObject();
                    return;
                case JsonValueKind.Array:
                    writer.WriteStartArray();
                    foreach (var item in value.EnumerateArray())
                        WriteNormalizedValue(writer, item);
                    writer.WriteEndArray();
                    return;
                case JsonValueKind.String:
                    writer.WriteStringValue(value.GetString());
                    return;
                case JsonValueKind.Number:
                    WriteNormalizedNumber(writer, value);
                    return;
                case JsonValueKind.True:
                    writer.WriteBooleanValue(true);
                    return;
                case JsonValueKind.False:
                    writer.WriteBooleanValue(false);
                    return;
                case JsonValueKind.Null:
                    writer.WriteNullValue();
                    return;
                default:
                    throw new JsonException("Sidecar JSON contains an unsupported value kind.");
            }
        }

        private static void WriteNormalizedNumber(Utf8JsonWriter writer, JsonElement value)
        {
            switch (GetNumberKind(value))
            {
                case JsonNumberKind.PositiveInteger when value.TryGetUInt64(out var unsigned):
                    writer.WriteNumberValue(unsigned);
                    return;
                case JsonNumberKind.NegativeInteger when value.TryGetInt64(out var signed):
                    writer.WriteNumberValue(signed);
                    return;
                case JsonNumberKind.Float when value.TryGetDouble(out var floating) &&
                    double.IsFinite(floating):
                    writer.WriteRawValue(FormatSerdeFloat(floating));
                    return;
                default:
                    // Preserve integers outside the portable range so the caller's
                    // post-serialization validation can return its stable error.
                    writer.WriteRawValue(value.GetRawText());
                    return;
            }
        }
    }

    private sealed class SerdeJsonNodeConverterFactory : JsonConverterFactory
    {
        internal static readonly SerdeJsonNodeConverterFactory Instance = new();

        public override bool CanConvert(Type typeToConvert) =>
            typeof(JsonNode).IsAssignableFrom(typeToConvert);

        public override JsonConverter CreateConverter(
            Type typeToConvert,
            JsonSerializerOptions options) =>
            (JsonConverter)Activator.CreateInstance(
                typeof(SerdeJsonNodeConverter<>).MakeGenericType(typeToConvert))!;
    }

    private sealed class SerdeJsonDocumentConverter : JsonConverter<JsonDocument>
    {
        internal static readonly SerdeJsonDocumentConverter Instance = new();

        public override JsonDocument Read(
            ref Utf8JsonReader reader,
            Type typeToConvert,
            JsonSerializerOptions options) => JsonDocument.ParseValue(ref reader);

        public override void Write(
            Utf8JsonWriter writer,
            JsonDocument value,
            JsonSerializerOptions options)
        {
            SerdeJsonElementConverter.PreflightRawValue(value.RootElement);
            SerdeJsonElementConverter.WriteNormalizedValue(writer, value.RootElement);
        }
    }

    private sealed class SerdeJsonNodeConverter<TNode> : JsonConverter<TNode>
        where TNode : JsonNode
    {
        public override TNode? Read(
            ref Utf8JsonReader reader,
            Type typeToConvert,
            JsonSerializerOptions options)
        {
            using var document = JsonDocument.ParseValue(ref reader);
            return JsonNode.Parse(document.RootElement.GetRawText()) as TNode
                ?? throw new JsonException($"JSON value is not a {typeToConvert.Name}.");
        }

        public override void Write(
            Utf8JsonWriter writer,
            TNode value,
            JsonSerializerOptions options) => WriteNormalizedNode(writer, value);

        private static void WriteNormalizedNode(Utf8JsonWriter writer, JsonNode? node)
        {
            switch (node)
            {
                case null:
                    writer.WriteNullValue();
                    return;
                case JsonObject jsonObject:
                    writer.WriteStartObject();
                    foreach (var property in jsonObject)
                    {
                        writer.WritePropertyName(property.Key);
                        WriteNormalizedNode(writer, property.Value);
                    }
                    writer.WriteEndObject();
                    return;
                case JsonArray jsonArray:
                    writer.WriteStartArray();
                    foreach (var item in jsonArray)
                        WriteNormalizedNode(writer, item);
                    writer.WriteEndArray();
                    return;
                case JsonValue jsonValue when jsonValue.TryGetValue<JsonElement>(out var element):
                    SerdeJsonElementConverter.PreflightRawValue(element);
                    SerdeJsonElementConverter.WriteNormalizedValue(writer, element);
                    return;
                case JsonValue jsonValue when jsonValue.TryGetValue<byte>(out var byteValue):
                    writer.WriteNumberValue(byteValue);
                    return;
                case JsonValue jsonValue when jsonValue.TryGetValue<sbyte>(out var sbyteValue):
                    writer.WriteNumberValue(sbyteValue);
                    return;
                case JsonValue jsonValue when jsonValue.TryGetValue<short>(out var shortValue):
                    writer.WriteNumberValue(shortValue);
                    return;
                case JsonValue jsonValue when jsonValue.TryGetValue<ushort>(out var ushortValue):
                    writer.WriteNumberValue(ushortValue);
                    return;
                case JsonValue jsonValue when jsonValue.TryGetValue<int>(out var intValue):
                    writer.WriteNumberValue(intValue);
                    return;
                case JsonValue jsonValue when jsonValue.TryGetValue<uint>(out var uintValue):
                    writer.WriteNumberValue(uintValue);
                    return;
                case JsonValue jsonValue when jsonValue.TryGetValue<long>(out var longValue):
                    writer.WriteNumberValue(longValue);
                    return;
                case JsonValue jsonValue when jsonValue.TryGetValue<ulong>(out var ulongValue):
                    writer.WriteNumberValue(ulongValue);
                    return;
                case JsonValue jsonValue when jsonValue.TryGetValue<decimal>(out var decimalValue):
                    writer.WriteRawValue(FormatSerdeFloat(decimal.ToDouble(decimalValue)));
                    return;
                case JsonValue jsonValue when jsonValue.TryGetValue<double>(out var doubleValue):
                    SerdeDoubleConverter.Instance.Write(writer, doubleValue, SerializerOptions);
                    return;
                case JsonValue jsonValue when jsonValue.TryGetValue<float>(out var singleValue):
                    SerdeSingleConverter.Instance.Write(writer, singleValue, SerializerOptions);
                    return;
                case JsonValue jsonValue:
                    jsonValue.WriteTo(writer);
                    return;
                default:
                    throw new JsonException("Sidecar JSON contains an unsupported node type.");
            }
        }
    }

    private sealed class CanonicalizationBudget(long remainingBytes)
    {
        internal long RemainingBytes { get; private set; } = remainingBytes;

        internal void Consume(int bytes)
        {
            if (bytes < 0 || bytes > RemainingBytes)
                throw new SidecarCanonicalizationLimitException();
            RemainingBytes -= bytes;
        }
    }

    private sealed class CanonicalizationBudgetScope(CanonicalizationBudget? previous) : IDisposable
    {
        private bool _disposed;

        public void Dispose()
        {
            if (_disposed)
                return;
            CurrentCanonicalizationBudget.Value = previous;
            _disposed = true;
        }
    }

    private sealed class CanonicalizationBudgetBufferWriter(CanonicalizationBudget budget) : IBufferWriter<byte>
    {
        private byte[] _buffer = Array.Empty<byte>();
        private int _available;

        public void Advance(int count)
        {
            if (count < 0 || count > _available)
                throw new ArgumentOutOfRangeException(nameof(count));
            budget.Consume(count);
            _available = 0;
        }

        public Memory<byte> GetMemory(int sizeHint = 0)
        {
            var requested = Math.Max(sizeHint, 256);
            if (requested > budget.RemainingBytes)
                requested = checked((int)budget.RemainingBytes);
            if (requested == 0 || sizeHint > requested)
                throw new SidecarCanonicalizationLimitException();
            if (_buffer.Length < requested)
                _buffer = new byte[requested];
            _available = requested;
            return _buffer.AsMemory(0, requested);
        }

        public Span<byte> GetSpan(int sizeHint = 0) => GetMemory(sizeHint).Span;
    }

    private sealed unsafe class SerdeJsonEncoder : JavaScriptEncoder
    {
        internal static readonly SerdeJsonEncoder Instance = new();

        public override int MaxOutputCharactersPerInputCharacter => 6;

        public override bool WillEncode(int unicodeScalar) =>
            unicodeScalar is >= 0 and <= 0x1f or '"' or '\\';

        public override int FindFirstCharacterToEncode(char* text, int textLength)
        {
            if (text == null)
                throw new ArgumentNullException(nameof(text));
            for (var index = 0; index < textLength; index++)
            {
                var character = text[index];
                if (WillEncode(character))
                    return index;
                if (char.IsHighSurrogate(character))
                {
                    if (index + 1 < textLength && char.IsLowSurrogate(text[index + 1]))
                    {
                        index++;
                        continue;
                    }
                    return index;
                }
                if (char.IsLowSurrogate(character))
                    return index;
            }
            return -1;
        }

        public override bool TryEncodeUnicodeScalar(
            int unicodeScalar,
            char* buffer,
            int bufferLength,
            out int numberOfCharactersWritten)
        {
            if (buffer == null)
                throw new ArgumentNullException(nameof(buffer));
            ReadOnlySpan<char> escape = unicodeScalar switch
            {
                '"' => "\\\"",
                '\\' => "\\\\",
                '\b' => "\\b",
                '\t' => "\\t",
                '\n' => "\\n",
                '\f' => "\\f",
                '\r' => "\\r",
                _ => default
            };
            if (!escape.IsEmpty)
            {
                if (bufferLength < escape.Length)
                {
                    numberOfCharactersWritten = 0;
                    return false;
                }
                escape.CopyTo(new Span<char>(buffer, bufferLength));
                numberOfCharactersWritten = escape.Length;
                return true;
            }
            if (unicodeScalar is < 0 or > 0x1f || bufferLength < 6)
            {
                numberOfCharactersWritten = 0;
                return false;
            }
            const string hex = "0123456789abcdef";
            buffer[0] = '\\';
            buffer[1] = 'u';
            buffer[2] = '0';
            buffer[3] = '0';
            buffer[4] = hex[(unicodeScalar >> 4) & 0xf];
            buffer[5] = hex[unicodeScalar & 0xf];
            numberOfCharactersWritten = 6;
            return true;
        }
    }

    internal static JsonElement RequiredObject(JsonElement parent, string name)
    {
        if (!parent.TryGetProperty(name, out var value) || value.ValueKind != JsonValueKind.Object)
            throw new SidecarProtocolException($"Sidecar field '{name}' must be an object.");
        return value;
    }

    internal static string RequiredString(JsonElement parent, string name)
    {
        if (!parent.TryGetProperty(name, out var value) || value.ValueKind != JsonValueKind.String)
            throw new SidecarProtocolException($"Sidecar field '{name}' must be a string.");
        return value.GetString()!;
    }

    internal static ulong RequiredUInt64(JsonElement parent, string name)
    {
        if (!parent.TryGetProperty(name, out var value) ||
            !value.TryGetUInt64(out var result) ||
            result > MaxPortableInteger)
        {
            throw new SidecarProtocolException(
                $"Sidecar field '{name}' must be a portable unsigned integer.");
        }
        return result;
    }

    internal static void EnsureObjectShape(JsonElement value, params string[] expected)
    {
        if (value.ValueKind != JsonValueKind.Object)
            throw new SidecarProtocolException("Sidecar value must be an object.");
        var allowed = expected.ToHashSet(StringComparer.Ordinal);
        var count = 0;
        foreach (var property in value.EnumerateObject())
        {
            count++;
            if (!allowed.Contains(property.Name))
                throw new SidecarProtocolException($"Unknown sidecar field '{property.Name}'.");
        }
        if (count != allowed.Count || allowed.Any(name => !value.TryGetProperty(name, out _)))
            throw new SidecarProtocolException("Sidecar value is missing a required field.");
    }

    internal static bool IsPortableJson(JsonElement value) => value.ValueKind switch
    {
        JsonValueKind.Object => value.EnumerateObject().All(property => IsPortableJson(property.Value)),
        JsonValueKind.Array => value.EnumerateArray().All(IsPortableJson),
        JsonValueKind.Number => IsPortableNumber(value),
        _ => true
    };

    private static bool IsPortableNumber(JsonElement value)
    {
        if (value.TryGetInt64(out var signed))
            return signed >= -checked((long)MaxPortableInteger) &&
                signed <= checked((long)MaxPortableInteger);
        if (value.TryGetUInt64(out var unsigned))
            return unsigned <= MaxPortableInteger;
        if (!value.TryGetDouble(out var floating) || !double.IsFinite(floating))
            return false;
        return floating != Math.Truncate(floating) || Math.Abs(floating) <= MaxPortableInteger;
    }
}

internal sealed class SidecarCanonicalizationLimitException : Exception;
