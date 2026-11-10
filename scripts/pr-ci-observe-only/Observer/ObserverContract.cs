using System.Runtime.Versioning;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Xml;

namespace OpenClaw.ObserveOnly;

internal sealed record ObserverConfiguration(string Root, string Invocation, string ExpectedSource,
    string ExpectedSourceSha256, string ExpectedObjectModelSha256, string ExpectedCollectorSha256,
    string ExpectedHostImageSha256, string ExpectedDotnetSha256, string ExpectedBlameSha256)
{
    public const string ExpectedFramework = ".NETCoreApp,Version=v10.0";
    private const int MaximumFrameworkCharacters = 128;
    private const int MaximumPathCharacters = 32768;
    private static readonly string[] OwnedNames = ["Root", "Invocation", "ExpectedSource", "ExpectedSourceSha256",
        "ExpectedObjectModelSha256", "ExpectedCollectorSha256", "ExpectedHostImageSha256", "ExpectedDotnetSha256", "ExpectedBlameSha256"];

    public static ObserverConfiguration Parse(XmlElement? configuration)
    {
        if (configuration is null || configuration.Name != "Configuration" || configuration.NamespaceURI.Length != 0 || configuration.HasAttributes)
            throw new InvalidDataException("observer-configuration");
        var values = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (XmlNode node in configuration.ChildNodes)
        {
            if (node is XmlWhitespace || node is XmlSignificantWhitespace || (node is XmlText && string.IsNullOrWhiteSpace(node.Value))) continue;
            if (node is not XmlElement element || element.NamespaceURI.Length != 0 || element.HasAttributes
                || (element.Name != "Framework" && !OwnedNames.Contains(element.Name, StringComparer.Ordinal))
                || element.ChildNodes.Cast<XmlNode>().Any(child => child is not XmlText))
                throw new InvalidDataException("observer-configuration");
            if (!values.TryAdd(element.Name, element.InnerText)) throw new InvalidDataException("observer-configuration");
        }
        // VSTest 18.6/18.7 supplies one Framework annotation before Initialize. It is not a source selector.
        if (values.Count != OwnedNames.Length + 1 || OwnedNames.Any(name => !values.ContainsKey(name)) || !values.ContainsKey("Framework"))
            throw new InvalidDataException("observer-configuration");
        var frameworkText = values["Framework"];
        if (string.IsNullOrWhiteSpace(frameworkText) || frameworkText.Length > MaximumFrameworkCharacters)
            throw new InvalidDataException("observer-framework");
        FrameworkName framework;
        try { framework = new FrameworkName(frameworkText); }
        catch (ArgumentException) { throw new InvalidDataException("observer-framework"); }
        var expected = new FrameworkName(ExpectedFramework);
        if (!framework.Identifier.Equals(expected.Identifier, StringComparison.OrdinalIgnoreCase)
            || framework.Version != expected.Version || framework.Profile.Length != 0)
            throw new InvalidDataException("observer-framework");
        foreach (var name in new[] { "Root", "ExpectedSource" })
            if (string.IsNullOrWhiteSpace(values[name]) || values[name].Length > MaximumPathCharacters) throw new InvalidDataException("observer-path");
        if (!ReceiptCustody.ValidInvocation(values["Invocation"])) throw new InvalidDataException("observer-invocation");
        foreach (var name in OwnedNames.Where(name => name.EndsWith("Sha256", StringComparison.Ordinal)))
            if (!Regex.IsMatch(values[name], "\\A[a-f0-9]{64}\\z", RegexOptions.CultureInvariant)) throw new InvalidDataException("observer-digest");
        return new(values["Root"], values["Invocation"], values["ExpectedSource"], values["ExpectedSourceSha256"],
            values["ExpectedObjectModelSha256"], values["ExpectedCollectorSha256"], values["ExpectedHostImageSha256"],
            values["ExpectedDotnetSha256"], values["ExpectedBlameSha256"]);
    }
}

// Sole owner of the observer's fixed private files. No relative/default-root fallback exists.
internal sealed class ReceiptCustody
{
    private const int MaximumReceiptBytes = 16384;
    private sealed record BoundRoot(string Root, string Invocation);
    private BoundRoot? bound;
    public bool IsBound => bound is not null;
    public static bool ValidInvocation(string? value) => value is not null && Regex.IsMatch(value, "\\A[a-f0-9]{32}\\z", RegexOptions.CultureInvariant);
    public void Bind(string path, string nonce)
    {
        if (IsBound || !ValidInvocation(nonce)) throw new InvalidDataException("observer-custody");
        var canonical = Canonical(path, directory: true);
        bound = new BoundRoot(canonical, nonce); // Publish custody only after BOTH checks succeed.
    }
    private string BoundPath(string name)
    {
        var location = bound ?? throw new InvalidOperationException("observer-custody-unbound");
        if (!string.Equals(Canonical(location.Root, directory: true), location.Root, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("observer-custody");
        return Path.Combine(location.Root, name);
    }
    public void CreateClaim()
    {
        var path = BoundPath("collector.claim");
        using (new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.Read)) { }
    }
    public void MarkInvalid()
    {
        var path = BoundPath("observer-invalid");
        using var file = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.Read);
        file.Write(System.Text.Encoding.UTF8.GetBytes("invalid")); file.Flush(true);
    }
    public void WriteNew(string name, object value)
    {
        if (name is not ("observer-ready.json" or "observer.json")) throw new InvalidDataException("observer-receipt-name");
        var path = BoundPath(name); // Must precede serialization and every file open.
        var bytes = JsonSerializer.SerializeToUtf8Bytes(value);
        if (bytes.Length > MaximumReceiptBytes) throw new InvalidDataException("observer-receipt-limit");
        using var file = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.Read);
        file.Write(bytes); file.Flush(true);
    }
    internal static string Canonical(string path, bool directory = false)
    {
        if (!Path.IsPathFullyQualified(path)) throw new InvalidDataException();
        var full = Path.GetFullPath(path);
        for (var current = full; !string.IsNullOrEmpty(current); current = Path.GetDirectoryName(current))
            if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0) throw new InvalidDataException();
        if (directory ? !Directory.Exists(full) : !File.Exists(full)) throw new InvalidDataException();
        return full;
    }
}
