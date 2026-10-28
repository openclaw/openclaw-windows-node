using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using OpenClaw.Shared;
using OpenClaw.Shared.Systems;
using OpenClawTray.Helpers;

namespace OpenClawTray.Systems;

/// <summary>Localized labels and formatting for the Systems page. Keeps resource keys in one place.</summary>
internal static class SystemsText
{
    public static string S(string key) => LocalizationHelper.GetString("Systems_" + key);

    public static string F(string key, params object?[] args) => LocalizationHelper.Format("Systems_" + key, args);

    public static string DisplayName(SystemRow row) => row.Name ?? (row.Id == SystemsProjection.GatewayId ? S("Kind_Host") : row.Id);

    public static string Kind(SystemRow row) => row.Kind switch
    {
        SystemKind.Host => S("Kind_Host"),
        SystemKind.Worker => S("Kind_Worker"),
        SystemKind.Node => S("Kind_Node"),
        _ => S("Kind_Client"),
    };

    public static string Status(SystemStatus status) => status switch
    {
        SystemStatus.Online => S("Status_Online"),
        SystemStatus.Offline => S("Status_Offline"),
        SystemStatus.Starting => S("Status_Starting"),
        SystemStatus.Stopping => S("Status_Stopping"),
        SystemStatus.Error => S("Status_Error"),
        _ => S("Status_Unknown"),
    };

    public static SystemStatus EffectiveStatus(SystemRow row, bool connected) =>
        SystemsProjection.EffectiveStatus(row, connected);

    public static string Relation(SessionRelationKind kind) => kind switch
    {
        SessionRelationKind.Placement => S("Relation_Placement"),
        SessionRelationKind.RetainedPlacement => S("Relation_RetainedPlacement"),
        SessionRelationKind.Runner => S("Relation_Runner"),
        SessionRelationKind.ExecBinding => S("Relation_ExecBinding"),
        _ => S("Relation_Gateway"),
    };

    public static string BackupKind(BackupKind kind) => kind switch
    {
        OpenClaw.Shared.Systems.BackupKind.Archive => S("Backups_Kind_Archive"),
        OpenClaw.Shared.Systems.BackupKind.SqliteSnapshot => S("Backups_Kind_SqliteSnapshot"),
        OpenClaw.Shared.Systems.BackupKind.Git => S("Backups_Kind_Git"),
        OpenClaw.Shared.Systems.BackupKind.External => S("Backups_Kind_External"),
        _ => S("Status_Unknown"),
    };

    public static string BackupHealth(BackupHealth health) => health switch
    {
        OpenClaw.Shared.Systems.BackupHealth.Ok => S("Backups_Ok"),
        OpenClaw.Shared.Systems.BackupHealth.PushFailed => S("Backups_PushFailed"),
        _ => S("Backups_Failed"),
    };

    public static string Probe(StorageProbeState state) => state switch
    {
        StorageProbeState.Ok => S("Backups_Probe_Ok"),
        StorageProbeState.Unavailable => S("Backups_Probe_Unavailable"),
        StorageProbeState.Uninitialized => S("Backups_Probe_Uninitialized"),
        StorageProbeState.WrongKey => S("Backups_Probe_WrongKey"),
        _ => S("Backups_Probe_Error"),
    };

    public static string DesktopTitle(OpenClaw.Shared.Systems.DesktopTitle title) => S("Desktop_Title_" + title);

    public static string DesktopHint(OpenClaw.Shared.Systems.DesktopHint hint) => S("Desktop_Hint_" + hint);

    public static string Presence(PresenceStatus status) => status switch
    {
        PresenceStatus.Active => LocalizationHelper.GetString("InstancesPage_Status_Active"),
        PresenceStatus.Idle => LocalizationHelper.GetString("InstancesPage_Status_Idle"),
        PresenceStatus.Stale => LocalizationHelper.GetString("InstancesPage_Status_Inactive"),
        PresenceStatus.Offline => LocalizationHelper.GetString("InstancesPage_Status_Disconnected"),
        _ => "",
    };

    /// <summary>Short provenance for a presence update, matching the former Instances page.</summary>
    public static string Reason(string? reason)
    {
        var trimmed = (reason ?? "").Trim();
        return trimmed.ToLowerInvariant() switch
        {
            "" => "",
            "self" => LocalizationHelper.GetString("InstancesPage_Reason_Self"),
            "connect" => LocalizationHelper.GetString("InstancesPage_Reason_Connect"),
            "disconnect" => LocalizationHelper.GetString("InstancesPage_Reason_Disconnect"),
            "node-connected" => LocalizationHelper.GetString("InstancesPage_Reason_NodeConnect"),
            "node-disconnected" => LocalizationHelper.GetString("InstancesPage_Reason_NodeDisconnect"),
            "launch" => LocalizationHelper.GetString("InstancesPage_Reason_Launch"),
            "periodic" => LocalizationHelper.GetString("InstancesPage_Reason_Heartbeat"),
            "instances-refresh" => LocalizationHelper.GetString("InstancesPage_Reason_Refresh"),
            "seq gap" => LocalizationHelper.GetString("InstancesPage_Reason_Resync"),
            _ => trimmed,
        };
    }

    public static string Ago(DateTimeOffset at, DateTimeOffset now) =>
        AgoSeconds((long)Math.Max(0, (now - at).TotalSeconds));

    public static string AgoSeconds(long seconds)
    {
        var secs = (int)Math.Min(seconds, int.MaxValue);
        if (secs < 60)
            return string.Format(LocalizationHelper.GetString("InstancesPage_TimeAgo_Seconds_Format"), secs);
        if (secs < 3600)
            return string.Format(LocalizationHelper.GetString("InstancesPage_TimeAgo_Minutes_Format"), secs / 60);
        if (secs < 86400)
            return string.Format(LocalizationHelper.GetString("InstancesPage_TimeAgo_Hours_Format"), secs / 3600);
        return string.Format(LocalizationHelper.GetString("InstancesPage_TimeAgo_Days_Format"), secs / 86400);
    }

    public static string FormatDateTime(DateTimeOffset value) =>
        value.ToLocalTime().ToString("g", CultureInfo.CurrentCulture);

    public static string Duration(long milliseconds)
    {
        var span = TimeSpan.FromMilliseconds(Math.Max(0, milliseconds));
        return span.TotalDays >= 1
            ? F("Duration_DaysHours", (int)span.TotalDays, span.Hours)
            : span.TotalHours >= 1
                ? F("Duration_HoursMinutes", (int)span.TotalHours, span.Minutes)
                : F("Duration_Minutes", Math.Max(1, (int)span.TotalMinutes));
    }

    /// <summary>Binary byte sizes with unit symbols that read the same in every supported language.</summary>
    public static string Bytes(long bytes)
    {
        string[] units = ["B", "KB", "MB", "GB", "TB", "PB"];
        double value = Math.Max(0, bytes);
        var unit = 0;
        while (value >= 1024 && unit < units.Length - 1)
        {
            value /= 1024;
            unit++;
        }
        var format = unit == 0 || value >= 100 ? "N0" : "N1";
        return value.ToString(format, CultureInfo.CurrentCulture) + " " + units[unit];
    }

    public static string Load(double value) => value.ToString("N2", CultureInfo.CurrentCulture);

    /// <summary>Fallback copy-debug text used when a presence beacon has no text, carried over from Instances.</summary>
    public static string DebugSummary(SystemRow row)
    {
        if (!string.IsNullOrWhiteSpace(row.Instance?.DebugText))
            return row.Instance!.DebugText!;
        var lines = new List<string> { $"Machine: {DisplayName(row)}", $"Id: {row.Id}", $"Kind: {row.Kind}", $"Status: {row.Status}" };
        if (row.Node is { } node) lines.Add($"NodeId: {node.NodeId}");
        if (row.Instance?.Presence?.DeviceId is { Length: > 0 } deviceId) lines.Add($"DeviceId: {deviceId}");
        if (row.Instance?.Presence?.InstanceId is { Length: > 0 } instanceId) lines.Add($"InstanceId: {instanceId}");
        if ((row.Instance?.Ip ?? row.Node?.RemoteIp) is { Length: > 0 } ip) lines.Add($"IP: {ip}");
        if ((row.Instance?.Version ?? row.Node?.Version) is { Length: > 0 } version) lines.Add($"Version: {version}");
        if (row.Platform is { Length: > 0 } platform) lines.Add($"Platform: {platform}");
        if ((row.Instance?.DeviceFamily ?? row.Node?.DeviceFamily) is { Length: > 0 } family) lines.Add($"DeviceFamily: {family}");
        if ((row.Instance?.ModelIdentifier ?? row.Node?.ModelIdentifier) is { Length: > 0 } model) lines.Add($"Model: {model}");
        if (row.Instance?.Mode is { Length: > 0 } mode) lines.Add($"Mode: {mode}");
        if (row.Instance?.LastInputSeconds is { } seconds) lines.Add($"LastInputSeconds: {seconds}");
        if (row.Instance?.Reason is { Length: > 0 } reason) lines.Add($"Reason: {reason}");
        if (row.Instance?.Timestamp is { } timestamp) lines.Add($"Timestamp: {timestamp:o}");
        if (row.Environment?.LastSeenAt is { } seen) lines.Add($"LastSeen: {seen:o}");
        return string.Join("\n", lines);
    }
}
