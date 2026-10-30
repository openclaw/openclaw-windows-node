using System;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Text.RegularExpressions;
using System.Threading.Tasks;

namespace OpenClaw.GatewayCleanupHost
{
    internal static class Program
    {
        private static int Main(string[] args)
        {
            var directory = Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location);
            var errorPath = Path.Combine(directory, "cleanup-host-error.txt");
            try
            {
                if (args.Length != 1 || !Regex.IsMatch(args[0], @"\A[A-Za-z0-9._-]+\z"))
                    throw new ArgumentException("A valid Companion package family is required.");

                // The GUI subsystem host never allocates a console. Suppress it for the worker at creation too.
                var start = new ProcessStartInfo
                {
                    FileName = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System),
                        "WindowsPowerShell", "v1.0", "powershell.exe"),
                    Arguments = "-NoProfile -NonInteractive -ExecutionPolicy Bypass -File \"" +
                        Path.Combine(directory, "cleanup.ps1") + "\" -Family " + args[0],
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true
                };
                using (var process = Process.Start(start))
                {
                    var stdout = process.StandardOutput.ReadToEndAsync();
                    var stderr = process.StandardError.ReadToEndAsync();
                    process.WaitForExit();
                    Task.WaitAll(stdout, stderr);
                    if (process.ExitCode != 0)
                        File.WriteAllText(errorPath, stderr.Result);
                    return process.ExitCode;
                }
            }
            catch (Exception error) when (error is ArgumentException || error is Win32Exception ||
                error is InvalidOperationException || error is IOException || error is UnauthorizedAccessException)
            {
                File.WriteAllText(errorPath, error.ToString());
                return 1;
            }
        }
    }
}
