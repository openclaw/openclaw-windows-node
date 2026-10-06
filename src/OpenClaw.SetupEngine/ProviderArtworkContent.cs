using System.Globalization;
using System.Text;
using System.Xml;
using System.Xml.Linq;

namespace OpenClaw.SetupEngine;

/// <summary>
/// Remote vectors use a deliberately small static subset, never HTML/WebView or an
/// unrestricted SVG document. Reconstructed XML cannot name resources, scripts, fonts,
/// styles, filters, animation, DTDs or entities. Native decode remains a separate gate.
/// </summary>
internal static class ProviderArtworkContent
{
    internal const uint MaxDimension = 1024;
    internal const ulong MaxPixels = 1024 * 1024;
    internal const int MaxSvgNodes = 256;
    internal const int MaxSvgDepth = 16;
    internal const int MaxPathCharacters = 32768;
    private const string SvgNamespace = "http://www.w3.org/2000/svg";
    private static readonly HashSet<string> Shapes = ["svg", "g", "path", "rect", "circle", "ellipse", "line", "polyline", "polygon"];
    private static readonly HashSet<string> Numbers = ["x", "y", "x1", "x2", "y1", "y2", "width", "height", "rx", "ry", "cx", "cy", "r", "stroke-width", "opacity", "fill-opacity", "stroke-opacity", "stroke-miterlimit"];

    internal static bool AreDimensionsAllowed(uint width, uint height) =>
        width is > 0 and <= MaxDimension && height is > 0 and <= MaxDimension && (ulong)width * height <= MaxPixels;

    internal static ProviderArtworkResult Validate(byte[] bytes, string mime)
    {
        if (bytes.Length > ProviderArtworkLoader.MaxBytes)
            return new(ProviderArtworkStatus.TooLarge);
        if (mime.Equals("image/png", StringComparison.OrdinalIgnoreCase) &&
            bytes.AsSpan().StartsWith(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 }))
            return new(ProviderArtworkStatus.Loaded, new(bytes, ProviderArtworkFormat.Png));
        if (mime.Equals("image/jpeg", StringComparison.OrdinalIgnoreCase) &&
            bytes.AsSpan().StartsWith(new byte[] { 255, 216, 255 }))
            return new(ProviderArtworkStatus.Loaded, new(bytes, ProviderArtworkFormat.Jpeg));
        if (!mime.Equals("image/svg+xml", StringComparison.OrdinalIgnoreCase))
            return new(ProviderArtworkStatus.InvalidImage);
        try
        {
            var sanitized = ValidateSvg(bytes);
            return sanitized is null ? new(ProviderArtworkStatus.Unsupported) :
                new(ProviderArtworkStatus.Loaded, new(sanitized, ProviderArtworkFormat.Svg));
        }
        catch (XmlException) { return new(ProviderArtworkStatus.InvalidImage); }
    }

    private static byte[]? ValidateSvg(byte[] bytes)
    {
        var settings = new XmlReaderSettings
        {
            DtdProcessing = DtdProcessing.Prohibit,
            XmlResolver = null,
            MaxCharactersInDocument = ProviderArtworkLoader.MaxBytes,
            MaxCharactersFromEntities = 0,
            IgnoreComments = true,
            IgnoreWhitespace = true
        };
        using var stream = new MemoryStream(bytes, writable: false);
        using var reader = XmlReader.Create(stream, settings);
        XElement? root = null;
        var parents = new Stack<XElement>();
        var nodes = 0;
        var pathCharacters = 0;
        while (reader.Read())
        {
            if (++nodes > MaxSvgNodes || reader.Depth > MaxSvgDepth)
                return null;
            if (reader.NodeType == XmlNodeType.XmlDeclaration)
                continue;
            if (reader.NodeType == XmlNodeType.EndElement)
            {
                if (parents.Count == 0)
                    return null;
                parents.Pop();
                continue;
            }
            if (reader.NodeType != XmlNodeType.Element ||
                reader.NamespaceURI != SvgNamespace || !Shapes.Contains(reader.LocalName) ||
                reader.AttributeCount > 16 || root is null && reader.LocalName != "svg" ||
                root is not null && (parents.Count == 0 || reader.LocalName == "svg"))
                return null;
            var element = new XElement(XName.Get(reader.LocalName, SvgNamespace));
            if (reader.MoveToFirstAttribute())
            {
                do
                {
                    if (reader.Name == "xmlns" && reader.Value == SvgNamespace)
                        continue;
                    if (reader.NamespaceURI.Length > 0 || !IsSafeAttribute(reader.LocalName, reader.Value, ref pathCharacters))
                        return null;
                    var value = reader.LocalName is "fill" or "stroke" && reader.Value != "none"
                        ? "#FFFFFF" : reader.Value;
                    element.SetAttributeValue(reader.LocalName, value);
                } while (reader.MoveToNextAttribute());
                reader.MoveToElement();
            }
            if (root is null)
            {
                root = element;
                var viewBox = NumberList((string?)root.Attribute("viewBox"), 4);
                if (viewBox is null || viewBox.Length != 4 ||
                    viewBox[2] <= 0 || viewBox[3] <= 0 || viewBox[2] > MaxDimension || viewBox[3] > MaxDimension)
                    return null;
                // Fixed render extent avoids intrinsic-size allocation and inherited giant viewports.
                root.SetAttributeValue("width", "24");
                root.SetAttributeValue("height", "24");
                if (root.Attribute("fill") is null)
                    root.SetAttributeValue("fill", "#FFFFFF");
            }
            else
                parents.Peek().Add(element);
            if (!reader.IsEmptyElement)
                parents.Push(element);
        }
        if (root is null || parents.Count > 0)
            return null;
        return Encoding.UTF8.GetBytes(root.ToString(SaveOptions.DisableFormatting));
    }

    private static bool IsSafeAttribute(string name, string value, ref int pathCharacters)
    {
        if (value.Length > MaxPathCharacters)
            return false;
        if (name == "d")
        {
            pathCharacters += value.Length;
            if (pathCharacters > MaxPathCharacters)
                return false;
            // Path grammar itself is checked by the native decoder. The numeric and command
            // budgets bound parser/tessellator work before any native parsing happens.
            var numeric = new StringBuilder(value.Length);
            var commands = 0;
            foreach (var c in value)
            {
                if ("MmZzLlHhVvCcSsQqTtAa".Contains(c))
                {
                    if (++commands > 2048)
                        return false;
                    numeric.Append(' ');
                }
                else if (char.IsAsciiDigit(c) || c is ' ' or ',' or '.' or '-' or '+' or 'e' or 'E' or '\r' or '\n' or '\t')
                    numeric.Append(c);
                else
                    return false;
            }
            return commands > 0 && NumberList(numeric.ToString(), 4096) is not null;
        }
        if (Numbers.Contains(name))
            return NumberList(value, 1) is { Length: 1 };
        if (name == "viewBox")
            return NumberList(value, 4) is { Length: 4 };
        if (name == "points")
            return NumberList(value, 4096) is { Length: > 0 };
        return name switch
        {
            "fill" or "stroke" => value is "none" or "currentColor" or "black" or "white" ||
                value.Length is 4 or 7 && value[0] == '#' && value.Skip(1).All(char.IsAsciiHexDigit),
            "fill-rule" or "clip-rule" => value is "nonzero" or "evenodd",
            "stroke-linecap" => value is "butt" or "round" or "square",
            "stroke-linejoin" => value is "miter" or "round" or "bevel",
            _ => false
        };
    }

    private static double[]? NumberList(string? text, int limit)
    {
        if (text is null)
            return null;
        var parts = text.Split([' ', ',', '\r', '\n', '\t'], StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length > limit)
            return null;
        var values = new double[parts.Length];
        for (var i = 0; i < parts.Length; ++i)
            if (!double.TryParse(parts[i], NumberStyles.Float, CultureInfo.InvariantCulture, out values[i]) ||
                !double.IsFinite(values[i]) || Math.Abs(values[i]) > 10000)
                return null;
        return values;
    }
}
