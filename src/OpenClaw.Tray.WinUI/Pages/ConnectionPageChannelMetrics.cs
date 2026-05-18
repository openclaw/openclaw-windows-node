using System.Collections.Generic;
using OpenClaw.Shared;

namespace OpenClawTray.Pages;

/// <summary>
/// Pure helpers for the channels glance chip on <see cref="ConnectionPage"/>.
/// Lives in its own file (no WinUI imports) so the test project — which is
/// pure net10.0 and cannot reference the WinUI tray assembly — can link
/// this source and assert real behavior instead of parsing text.
///
/// The chip historically used a bespoke healthy-status whitelist
/// (<c>ready/ok/connected</c>) AND required <c>IsLinked == true</c>. As a
/// result, a single channel reporting <c>Status = "running"</c> or
/// <c>"active"</c> — both canonical healthy statuses elsewhere
/// (<see cref="ChannelHealth.IsHealthyStatus"/>, tray tooltip, channels
/// page) — was counted as 0, so the chip read "0/1 channels" yellow even
/// when the gateway was perfectly happy.
///
/// <see cref="CountHealthyChannels"/> aligns to the canonical predicate and
/// drops the <c>IsLinked</c> gate. <c>IsLinked</c> stays where it belongs —
/// as auth-age detail in <see cref="ChannelHealth.DisplayText"/> and the
/// channels page — not as a health predicate.
/// </summary>
internal static class ConnectionPageChannelMetrics
{
    /// <summary>
    /// Returns the number of channels in <paramref name="channels"/> whose
    /// <c>Status</c> is recognized as healthy by
    /// <see cref="ChannelHealth.IsHealthyStatus"/>. Returns 0 when the input
    /// is <c>null</c> or empty.
    /// </summary>
    internal static int CountHealthyChannels(IReadOnlyList<ChannelHealth>? channels)
    {
        if (channels == null || channels.Count == 0) return 0;
        int count = 0;
        for (int i = 0; i < channels.Count; i++)
        {
            if (ChannelHealth.IsHealthyStatus(channels[i].Status))
                count++;
        }
        return count;
    }
}
