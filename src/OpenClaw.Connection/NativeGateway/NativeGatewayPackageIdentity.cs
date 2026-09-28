namespace OpenClaw.Connection.NativeGateway;

/// <summary>Exact registration identities accepted before resolving package-qualified aliases.</summary>
public static class NativeGatewayPackageIdentity
{
    public const string StoreName = "OpenClawFoundation.OpenClawGateway";
    public const string StorePublisher = "CN=4BA40A7A-B719-4C40-BF91-84AF4F1136FC";

    // Retain already installed development packages and their saved profiles, not local acquisition.
    public const string DevelopmentName = "OpenClaw.Gateway";
    public const string DevelopmentPublisher =
        "CN=OpenClaw Foundation, O=OpenClaw Foundation, L=Mill Valley, S=California, C=US";

    public static bool IsTrusted(string name, string publisher) =>
        (name == StoreName && publisher == StorePublisher) ||
        (name == DevelopmentName && publisher == DevelopmentPublisher);
}
