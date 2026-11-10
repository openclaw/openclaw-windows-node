using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using Axe.Windows.Core.Enums;
using OpenClaw.TestSupport;

namespace OpenClaw.Tray.UITests;

public sealed class ExternalAxeScanTests
{
    private const string TargetProcess = "OPENCLAW_UI_AXE_TARGET_PID";
    private const string TargetWindow = "OPENCLAW_UI_AXE_TARGET_HWND";
    private const string Context = "OPENCLAW_UI_AXE_CONTEXT";
    private const string Exclusions = "OPENCLAW_UI_AXE_EXCLUSIONS";
    private const string Result = "OPENCLAW_UI_AXE_RESULT";

    internal static void AssertOwnedWindowHasNoAccessibilityErrors(
        IntPtr handle, IEnumerable<RuleId>? exclusions, string? context)
    {
        OnboardingNativeProof.AssertIsolatedRoots();
        Assert.Equal(ApartmentState.MTA, Thread.CurrentThread.GetApartmentState());
        GetWindowThreadProcessId(handle, out var owner);
        Assert.Equal((uint)Environment.ProcessId, owner);
        using var temp = new TempDirectory("onboarding-axe-");
        var result = Path.Combine(temp.Path, "result.txt");
        var start = new ProcessStartInfo(Environment.GetEnvironmentVariable("DOTNET_HOST_PATH") ?? "dotnet")
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            WorkingDirectory = AppContext.BaseDirectory,
        };
        start.ArgumentList.Add("vstest");
        start.ArgumentList.Add(typeof(ExternalAxeScanTests).Assembly.Location);
        start.ArgumentList.Add("/TestCaseFilter:FullyQualifiedName=OpenClaw.Tray.UITests.ExternalAxeScanTests.ScanOwnedWindow");
        start.ArgumentList.Add("/Logger:console;verbosity=detailed");
        start.Environment[TargetProcess] = owner.ToString(CultureInfo.InvariantCulture);
        start.Environment[TargetWindow] = handle.ToInt64().ToString(CultureInfo.InvariantCulture);
        start.Environment[Context] = context ?? "";
        start.Environment[Exclusions] = string.Join(",", exclusions ?? []);
        start.Environment[Result] = result;
        // Axe.Windows explicitly discards elements from its own process.
        using var process = Process.Start(start) ?? throw new InvalidOperationException("Could not start the external Axe scan.");
        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();
        if (!process.WaitForExit(90_000))
        {
            process.Kill(entireProcessTree: true);
            process.WaitForExit();
            throw new TimeoutException($"External Axe scan timed out for {context}.\n{stdout.GetAwaiter().GetResult()}\n{stderr.GetAwaiter().GetResult()}");
        }
        Assert.True(process.ExitCode == 0,
            $"External Axe scan failed for {context}.\n{stdout.GetAwaiter().GetResult()}\n{stderr.GetAwaiter().GetResult()}");
        Assert.True(File.Exists(result), "The external Axe scan must execute and write its success receipt.");
        Assert.Equal($"{owner}:{handle.ToInt64()}", File.ReadAllText(result));
    }

    [ExternalScanFact]
    public void ScanOwnedWindow()
    {
        OnboardingNativeProof.AssertIsolatedRoots();
        var processId = int.Parse(Environment.GetEnvironmentVariable(TargetProcess)!, CultureInfo.InvariantCulture);
        var handle = new IntPtr(long.Parse(Environment.GetEnvironmentVariable(TargetWindow)!, CultureInfo.InvariantCulture));
        Assert.NotEqual(Environment.ProcessId, processId);
        Assert.NotEqual(IntPtr.Zero, handle);
        GetWindowThreadProcessId(handle, out var owner);
        Assert.Equal((uint)processId, owner);
        var exclusions = (Environment.GetEnvironmentVariable(Exclusions) ?? "").Split(
            ',', StringSplitOptions.RemoveEmptyEntries).Select(value => Enum.Parse<RuleId>(value)).ToArray();
        AxeHelper.Initialize(processId);
        AxeHelper.AssertNoAccessibilityErrors(handle, exclusions, Environment.GetEnvironmentVariable(Context));
        File.WriteAllText(Environment.GetEnvironmentVariable(Result)!, $"{owner}:{handle.ToInt64()}");
    }

    private sealed class ExternalScanFactAttribute : FactAttribute
    {
        public ExternalScanFactAttribute()
        {
            if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(TargetProcess)))
                Skip = "Runs only as the external scanner for an owned onboarding test window.";
        }
    }

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr handle, out uint processId);
}
