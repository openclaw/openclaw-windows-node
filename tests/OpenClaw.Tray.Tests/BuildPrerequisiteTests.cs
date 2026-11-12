using System.Diagnostics;

namespace OpenClaw.Tray.Tests;

public sealed class BuildPrerequisiteTests
{
    [Theory]
    [InlineData(null, "x64", false)]
    [InlineData("14.29.30139", "x64", false)]
    [InlineData("14.51.36247", "arm64", false)]
    [InlineData("14.51.36247", "x64", true)]
    public async Task CheckOnly_UsesTheAppLocalRuntimeResolver(
        string? version, string runtimeArchitecture, bool supported)
    {
        if (!OperatingSystem.IsWindows())
            return;

        var repository = TestRepositoryPaths.GetRepositoryRoot();
        var fixture = Path.Combine(Path.GetTempPath(), "OpenClaw-vc-prerequisite-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(fixture);
        try
        {
            var install = Path.Combine(fixture, "vs");
            Directory.CreateDirectory(install);
            // The resolver accepts a caller-selected install. This marker is
            // never executed, and the fixture DLLs are only resolved, not loaded.
            var vswhere = Path.Combine(fixture, "vswhere.exe");
            File.WriteAllText(vswhere, "resolver fixture");
            if (version is not null)
            {
                var versionDirectory = Path.Combine(install, "VC", "Auxiliary", "Build");
                Directory.CreateDirectory(versionDirectory);
                File.WriteAllText(Path.Combine(versionDirectory, "Microsoft.VCRedistVersion.default.txt"), version);
                var crt = Path.Combine(install, "VC", "Redist", "MSVC", version,
                    runtimeArchitecture, "Microsoft.VC145.CRT");
                Directory.CreateDirectory(crt);
                File.WriteAllText(Path.Combine(crt, "vcruntime140.dll"), "resolver fixture");
                File.WriteAllText(Path.Combine(crt, "msvcp140.dll"), "resolver fixture");
            }

            // Hosted checkouts may be owned by Administrators. Trust this
            // checkout only in the child, without changing the user's config.
            var gitConfig = Path.Combine(fixture, "gitconfig");
            File.WriteAllText(gitConfig, $"[safe]\n\tdirectory = \"{repository.Replace('\\', '/')}\"\n");

            var start = new ProcessStartInfo("powershell.exe")
            {
                WorkingDirectory = repository,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
            };
            // PowerShell resets the inherited architecture at startup, so select the x64 fixture inside the child.
            foreach (var argument in new[] { "-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass",
                "-Command", "$env:PROCESSOR_ARCHITECTURE = 'AMD64'; & '.\\build.ps1' -CheckOnly -NoTrustRepository" })
                start.ArgumentList.Add(argument);
            // A PowerShell 7 parent can otherwise supply modules that Windows
            // PowerShell 5.1 cannot load, before the runtime check is reached.
            start.Environment.Remove("PSModulePath");
            start.Environment["GIT_CONFIG_GLOBAL"] = gitConfig;
            start.Environment["OpenClawVsInstallRoot"] = install;
            start.Environment["OpenClawVsWherePath"] = vswhere;
            foreach (var property in new[] { "OpenClawVCRedistVersion", "OpenClawVCRuntimeVersionIsSupported",
                "OpenClawVCRedistCrtDir", "OpenClawVCRuntimeMinimumVersion" })
                start.Environment.Remove(property);

            using var process = Process.Start(start)!;
            var stdout = process.StandardOutput.ReadToEndAsync();
            var stderr = process.StandardError.ReadToEndAsync();
            using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(2));
            try
            {
                await process.WaitForExitAsync(timeout.Token);
            }
            catch (OperationCanceledException)
            {
                process.Kill(entireProcessTree: true);
                await process.WaitForExitAsync();
                throw;
            }
            var output = await stdout + await stderr;
            Assert.Contains("Architecture: AMD64", output);
            if (supported)
            {
                Assert.True(output.Contains("Compatible app-local Visual C++ runtime detected", StringComparison.Ordinal), output);
                Assert.True(process.ExitCode == 0, output);
            }
            else
            {
                Assert.True(output.Contains("Missing compatible Visual Studio C++ Redistributable", StringComparison.Ordinal), output);
                Assert.DoesNotContain("All prerequisites met!", output);
                Assert.NotEqual(0, process.ExitCode);
            }
        }
        finally
        {
            Directory.Delete(fixture, recursive: true);
        }
    }
}
