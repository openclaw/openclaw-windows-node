#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;

namespace OpenClaw.Trace;

public sealed class PayloadPaths
{
    private readonly string[] roots;
    private readonly Dictionary<string, string> deviceDrives = new(StringComparer.OrdinalIgnoreCase);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern uint QueryDosDevice(string device, StringBuilder target, int length);

    public PayloadPaths(string[] roots)
    {
        this.roots = roots.Select(p => Path.GetFullPath(p).TrimEnd('\\') + "\\").ToArray();
        foreach (var root in this.roots)
        {
            if (root.Length < 2 || root[1] != ':') continue;
            var buffer = new StringBuilder(32768);
            if (QueryDosDevice(root[..2], buffer, buffer.Capacity) != 0)
                deviceDrives[buffer.ToString().Split('\0')[0]] = root[..2];
        }
    }

    public string? Relative(string? path)
    {
        if (string.IsNullOrEmpty(path)) return null;
        var normalized = path.Replace('/', '\\');
        if (normalized.StartsWith(@"\\?\", StringComparison.Ordinal) || normalized.StartsWith(@"\??\", StringComparison.Ordinal))
            normalized = normalized[4..];
        foreach (var drive in deviceDrives)
            if (normalized.StartsWith(drive.Key + "\\", StringComparison.OrdinalIgnoreCase))
            {
                normalized = drive.Value + normalized[drive.Key.Length..];
                break;
            }
        foreach (var root in roots)
        {
            if (!normalized.StartsWith(root, StringComparison.OrdinalIgnoreCase)) continue;
            var relative = normalized[root.Length..];
            // Only payload-relative names can reach a shareable report.
            if (relative.Split('\\').Any(p => p is ".." or ".")) return null;
            return relative;
        }
        return null;
    }

    public string Group(string? path)
    {
        if (string.IsNullOrEmpty(path)) return "unresolved";
        var relative = Relative(path);
        if (relative is null) return "outside-payload";
        var match = Regex.Match(relative, @"^node_modules\\(@[^\\]+\\[^\\]+|[^\\]+)", RegexOptions.IgnoreCase);
        if (match.Success) return match.Value;
        match = Regex.Match(relative, @"^dist\\extensions\\[^\\]+", RegexOptions.IgnoreCase);
        if (match.Success) return match.Value;
        return relative.StartsWith("dist\\", StringComparison.OrdinalIgnoreCase) ? "dist\\other" : "payload-root";
    }
}

public sealed record ProcessIdentity(int Id, long StartedUtcTicks, long ObservedTicks, long LastObservedTicks = 0);
public sealed record Selection(string Label, int Pid, long Start, long End);
public sealed class Lifetime(int pid, long start)
{
    public int Pid { get; } = pid;
    public long Start { get; } = start;
    public long End { get; set; } = long.MaxValue;
}

public static class ProcessSelection
{
    public static Selection[] Match(ProcessIdentity[] processes, List<Lifetime> lifetimes) =>
        processes.Select((p, index) =>
        {
            var lifetime = lifetimes.FirstOrDefault(l => l.Pid == p.Id && l.Start <= p.ObservedTicks && p.ObservedTicks < l.End);
            var nextStart = lifetimes.Where(l => l.Pid == p.Id && l.Start > p.ObservedTicks)
                .Select(l => l.Start).DefaultIfEmpty(long.MaxValue).Min();
            var nextNativeStart = processes.Where(other => other.Id == p.Id && other.StartedUtcTicks > p.StartedUtcTicks)
                .Select(other => other.StartedUtcTicks).DefaultIfEmpty(long.MaxValue).Min();
            // Without a verified exit, only the native observation span is proven.
            var observedEnd = Math.Max(p.ObservedTicks, p.LastObservedTicks);
            var end = lifetime is not null && lifetime.End != long.MaxValue ? lifetime.End :
                observedEnd == long.MaxValue ? long.MaxValue : observedEnd + 1;
            return new Selection($"P{index + 1}", p.Id, Math.Max(p.StartedUtcTicks, lifetime?.Start ?? p.ObservedTicks),
                Math.Min(end, Math.Min(nextStart, nextNativeStart)));
        }).ToArray();
}

public sealed record Window(string Name, long StartTicks, long EndTicks);
public sealed class Io
{
    public long OpenRequests { get; set; }
    public long ReadOperations { get; set; }
    public long WriteOperations { get; set; }
    public long ReadBytes { get; set; }
    public long WriteBytes { get; set; }
}
public sealed class WindowResult(Window definition)
{
    public Window Definition { get; } = definition;
    public bool Contains(long ticks) => ticks >= Definition.StartTicks && ticks < Definition.EndTicks;
    public long CpuSamples, MachineCpuSamples, DiskLatencyCount;
    public double DiskLatencyTotalMs, DiskLatencyMaximumMs;
    public Io MachineDisk { get; } = new();
    public Dictionary<string, long> ProcessSamples { get; } = [];
    public Dictionary<string, long> CpuGroups { get; } = [];
    public Dictionary<string, Io> FileGroups { get; } = [];
    public Dictionary<string, Io> PayloadFiles { get; } = [];

    public void AddFile(string group, string? relative, long bytes, string operation)
    {
        if (!FileGroups.TryGetValue(group, out var aggregate)) FileGroups[group] = aggregate = new Io();
        AddIo(aggregate, bytes, operation);
        if (string.IsNullOrEmpty(relative)) return;
        if (!PayloadFiles.TryGetValue(relative, out var file)) PayloadFiles[relative] = file = new Io();
        AddIo(file, bytes, operation);
    }

    private static void AddIo(Io io, long bytes, string operation)
    {
        if (operation == "open") io.OpenRequests++;
        else if (operation == "write") { io.WriteOperations++; io.WriteBytes += bytes; }
        else { io.ReadOperations++; io.ReadBytes += bytes; }
    }
}
