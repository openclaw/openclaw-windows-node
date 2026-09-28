using OpenClaw.BrowserBootstrap.Contracts;
using OpenClaw.Shared.Browser;

namespace OpenClaw.BrowserBootstrap;

/// <summary>Only the exact existing negative probes can run while management owns the mutex.</summary>
internal static class NativeKeylessProbe
{
    internal static byte[] Request(bool invalid) => invalid
        ? """{"v":1,"op":"bootstrap","nonce":"!"}"""u8.ToArray()
        : """{"v":1,"op":"bootstrap","nonce":"AAAAAAAAAAAAAAAAAAAAAA"}"""u8.ToArray();

    internal static string? ExpectedFailure(byte[] request, HostBinding binding, string caller)
    {
        if (binding.Mode != ManagementContract.Native) return null;
        if (caller == binding.ExpectedOrigins[0] && request.AsSpan().SequenceEqual(Request(true))) return "invalid_request";
        if (caller == ManagementContract.RejectionOrigin(binding.ExpectedOrigins) && request.AsSpan().SequenceEqual(Request(false))) return "origin_forbidden";
        return null;
    }

    internal static void RequireFailure(byte[] response, string expected)
    {
        // In particular, never forward an unexpected success or fabricate the expected probe pass.
        if (!response.AsSpan().SequenceEqual(BrowserNativeProtocol.Failure(expected)))
            throw new ContractException("transport_failed");
    }
}
