using System.Runtime.CompilerServices;

namespace OpenClawTray.Services;

internal static class ReleaseUpdateBackendFactory
{
    // Keep optional assembly/type resolution in this callee, outside the
    // loader's catch frame. No startup-facing signature may name vendor types.
    [MethodImpl(MethodImplOptions.NoInlining)]
    public static IUpdateBackend Create() => new UpdatumUpdateBackend();
}
