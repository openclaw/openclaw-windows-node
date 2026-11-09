using System.Text.Json;
using Microsoft.Diagnostics.Tracing;
using Microsoft.Diagnostics.Tracing.Parsers;
using OpenClaw.Trace;

if (args.Length != 3)
{
    Console.Error.WriteLine("Usage: GatewayTraceAnalyzer <private.etl> <private-windows.json> <summary.json>");
    return 1;
}
try
{
    var options = new JsonSerializerOptions { PropertyNameCaseInsensitive = true, WriteIndented = true };
    var request = JsonSerializer.Deserialize<Request>(File.ReadAllText(args[1]), options)
        ?? throw new InvalidDataException("Missing analysis request.");
    if (request.Windows.Length == 0 || request.Windows.Any(w => w.EndTicks <= w.StartTicks))
        throw new InvalidDataException("Analysis requires positive-duration windows.");
    var paths = new PayloadPaths(request.PayloadRoots);
    var windows = request.Windows.Select(w => new WindowResult(w)).ToArray();
    var lifetimes = new List<Lifetime>();
    var pending = new Dictionary<int, Lifetime>();
    using (var inventory = new ETWTraceEventSource(args[0]))
    {
        inventory.Kernel.ProcessStart += e =>
        {
            var ticks = e.TimeStamp.ToUniversalTime().ToFileTimeUtc();
            if (pending.Remove(e.ProcessID, out var previous)) previous.End = ticks;
            var lifetime = new Lifetime(e.ProcessID, ticks);
            lifetimes.Add(lifetime);
            pending[e.ProcessID] = lifetime;
        };
        inventory.Kernel.ProcessStop += e =>
        {
            var ticks = e.TimeStamp.ToUniversalTime().ToFileTimeUtc();
            if (pending.Remove(e.ProcessID, out var lifetime)) lifetime.End = ticks;
            else lifetimes.Add(new Lifetime(e.ProcessID, long.MinValue) { End = ticks });
        };
        inventory.Process();
    }
    // Match a sampled native creation identity to the ETL lifetime containing that
    // observation. This also admits a process already alive when recording began.
    var selections = ProcessSelection.Match(request.Processes, lifetimes);
    var modules = new Dictionary<string, List<Module>>();
    Selection? Select(int pid, long ticks) => selections.FirstOrDefault(p => p.Pid == pid && p.Start <= ticks && ticks < p.End);
    string Identity(int pid, long ticks)
    {
        return Select(pid, ticks)?.Label ?? "unselected";
    }
    using var source = new ETWTraceEventSource(args[0]);
    var kernel = source.Kernel;
    kernel.ImageLoad += e =>
    {
        var identity = Identity(e.ProcessID, e.TimeStamp.ToUniversalTime().ToFileTimeUtc());
        if (identity == "unselected") return;
        if (!modules.TryGetValue(identity, out var list)) modules[identity] = list = [];
        list.Add(new Module(e.ImageBase, (ulong)e.ImageSize, paths.Group(e.FileName)));
    };
    kernel.ImageDCStart += e =>
    {
        var identity = Identity(e.ProcessID, e.TimeStamp.ToUniversalTime().ToFileTimeUtc());
        if (identity == "unselected") return;
        if (!modules.TryGetValue(identity, out var list)) modules[identity] = list = [];
        list.Add(new Module(e.ImageBase, (ulong)e.ImageSize, paths.Group(e.FileName)));
    };
    kernel.ImageUnload += e =>
    {
        var identity = Identity(e.ProcessID, e.TimeStamp.ToUniversalTime().ToFileTimeUtc());
        if (modules.TryGetValue(identity, out var images)) images.RemoveAll(image => image.Base == e.ImageBase);
    };
    kernel.PerfInfoSample += e =>
    {
        var ticks = e.TimeStamp.ToUniversalTime().ToFileTimeUtc();
        foreach (var window in windows.Where(w => w.Contains(ticks)))
        {
            window.MachineCpuSamples++;
            var identity = Identity(e.ProcessID, ticks);
            if (identity == "unselected") continue;
            var group = "unresolved";
            if (modules.TryGetValue(identity, out var images))
                group = images.LastOrDefault(m => e.InstructionPointer >= m.Base &&
                    e.InstructionPointer - m.Base < m.Size)?.Group ?? "unresolved";
            window.CpuSamples++;
            window.ProcessSamples[identity] = window.ProcessSamples.GetValueOrDefault(identity) + 1;
            window.CpuGroups[group] = window.CpuGroups.GetValueOrDefault(group) + 1;
        }
    };
    void FileIo(TraceEvent e, int pid, string fileName, long bytes, string operation)
    {
        var ticks = e.TimeStamp.ToUniversalTime().ToFileTimeUtc();
        if (Select(pid, ticks) is null) return;
        var group = paths.Group(fileName);
        var relative = paths.Relative(fileName);
        foreach (var window in windows.Where(w => w.Contains(ticks)))
        {
            window.AddFile(group, relative, bytes, operation);
        }
    }
    kernel.FileIORead += e => FileIo(e, e.ProcessID, e.FileName, e.IoSize, "read");
    kernel.FileIOWrite += e => FileIo(e, e.ProcessID, e.FileName, e.IoSize, "write");
    kernel.FileIOCreate += e => FileIo(e, e.ProcessID, e.FileName, 0, "open");
    void DiskIo(TraceEvent e, long bytes, double latencyMs, bool write)
    {
        var ticks = e.TimeStamp.ToUniversalTime().ToFileTimeUtc();
        foreach (var window in windows.Where(w => w.Contains(ticks)))
        {
            if (write) { window.MachineDisk.WriteOperations++; window.MachineDisk.WriteBytes += bytes; }
            else { window.MachineDisk.ReadOperations++; window.MachineDisk.ReadBytes += bytes; }
            if (latencyMs >= 0 && double.IsFinite(latencyMs))
            {
                window.DiskLatencyTotalMs += latencyMs;
                window.DiskLatencyCount++;
                window.DiskLatencyMaximumMs = Math.Max(window.DiskLatencyMaximumMs, latencyMs);
            }
        }
    }
    kernel.DiskIORead += e => DiskIo(e, e.TransferSize, e.ElapsedTimeMSec, false);
    kernel.DiskIOWrite += e => DiskIo(e, e.TransferSize, e.ElapsedTimeMSec, true);
    source.Process();
    var result = new
    {
        schema = "openclaw-etl-resources/v1",
        events_lost = source.EventsLost,
        event_loss_free = source.EventsLost == 0,
        provider_coverage = "unverified; inspect per-window observation flags",
        process_selection = "sampled native creation identities matched to ETL process lifetime; no descendant inference",
        windows = windows.Select(w => new
        {
            phase = w.Definition.Name,
            elapsed_ms = (w.Definition.EndTicks - w.Definition.StartTicks) / 10000.0,
            cpu_samples = w.CpuSamples,
            machine_cpu_samples = w.MachineCpuSamples,
            cpu_observation = w.MachineCpuSamples == 0 ? "unobserved-provider-coverage-unknown" :
                w.CpuSamples == 0 ? "selected-processes-unobserved" : "sampled",
            process_cpu_samples = w.ProcessSamples,
            sampled_cpu_groups = w.CpuGroups.OrderByDescending(g => g.Value).Take(15)
                .Select(g => new { group = g.Key, samples = g.Value }),
            file_io_groups = w.FileGroups.OrderByDescending(g => g.Value.ReadBytes + g.Value.WriteBytes).Take(15)
                .Select(g => new { group = g.Key, io = g.Value }),
            file_io_observation = w.FileGroups.Count > 0 ? "observed" : "unobserved-provider-or-process-coverage-unknown",
            top_payload_files = w.PayloadFiles.OrderByDescending(g => g.Value.ReadBytes + g.Value.WriteBytes).Take(15)
                .Select(g => new { path = g.Key, io = g.Value }),
            machine_disk_io = w.MachineDisk.ReadOperations + w.MachineDisk.WriteOperations > 0 ? w.MachineDisk : null,
            disk_observation = w.MachineDisk.ReadOperations + w.MachineDisk.WriteOperations > 0 ? "observed" : "unobserved-provider-coverage-unknown",
            machine_disk_mean_response_ms = w.DiskLatencyCount > 0 ? (double?)(w.DiskLatencyTotalMs / w.DiskLatencyCount) : null,
            machine_disk_maximum_response_ms = w.DiskLatencyCount > 0 ? (double?)w.DiskLatencyMaximumMs : null
        }),
        limitations = new[]
        {
            "CPU sample counts are not CPU milliseconds, exclusive wall-clock contributions or JS stack attribution.",
            "File I/O is logical activity, not physical disk traffic. Missing file names remain unresolved.",
            "Disk events are assigned by completion time. Response time is ETW HighResResponseTime, not independently correlated issue-to-completion latency or application wait.",
            "Disk totals and response times are machine-wide completed requests, not causal delay attributable to a selected process.",
            "No matching events means unobserved, not proof of no activity. Inspect provider capture and event loss.",
            "Native image groups require image events before the sample. JIT code and unavailable images remain unresolved.",
            "Sleep/resume requires the companion sampled awake-time evidence; timestamp gaps alone do not prove sleep."
        }
    };
    File.WriteAllText(args[2], JsonSerializer.Serialize(result, options));
    return source.EventsLost == 0 ? 0 : 2;
}
catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException or ArgumentException)
{
    Console.Error.WriteLine($"Trace analysis failed ({ex.GetType().Name}). Private inputs were not copied to the report.");
    return 1;
}

sealed record Request(Window[] Windows, ProcessIdentity[] Processes, string[] PayloadRoots);
sealed record Module(ulong Base, ulong Size, string Group);
