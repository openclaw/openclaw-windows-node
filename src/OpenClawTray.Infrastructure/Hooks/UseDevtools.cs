using OpenClawTray.Infrastructure.Core;

namespace OpenClawTray.Infrastructure.Hooks;

public static class UseDevtoolsExtensions
{
    /// <summary>
    /// Returns <c>true</c> when the current process is running with the in-app
    /// devtools UI enabled. This is the AND of two independent signals:
    /// <list type="number">
    ///   <item><see cref="ReactorApp.Run{TRoot}(string,int,int,bool,bool,bool,System.Action{ReactorHost}?)"/>
    ///   was called with <c>devtools: true</c> (build-time capability gate).</item>
    ///   <item>The process was launched with <c>--devtools app</c> or
    ///   <c>--devtools run</c> (session-scoped opt-in by the user running the app).</item>
    /// </list>
    ///
    /// The value is frozen for the session; this call does not consume a hook
    /// slot and does not cause re-renders. Components use it to gate dev-only
    /// UX so the subtree is never constructed in retail sessions:
    /// <code>
    /// var dev = ctx.UseDevtools();
    /// return VStack(Content(), dev ? DebugOverlay() : null);
    /// </code>
    /// </summary>
    public static bool UseDevtools(this RenderContext ctx) =>
        ReactorApp.DevtoolsEnabled;
}
