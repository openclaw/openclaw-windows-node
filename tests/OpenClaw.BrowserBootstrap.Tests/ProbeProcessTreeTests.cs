using System.Diagnostics;
using System.Text;

namespace OpenClaw.BrowserBootstrap.Tests;

[System.Runtime.Versioning.SupportedOSPlatform("windows")]
public class ProbeProcessTreeTests
{
    [WindowsRegistryFact]
    public async Task LauncherExitDoesNotSettleItsStillRunningDescendant()
    {
        if(!OperatingSystem.IsWindows())return;
        var shell=Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System),"WindowsPowerShell","v1.0","powershell.exe");
        const string script="""
            $ErrorActionPreference='Stop'
            [Console]::ReadLine() | Out-Null
            $info=[Diagnostics.ProcessStartInfo]::new()
            $info.FileName=Join-Path $PSHOME 'powershell.exe'
            $info.Arguments='-NoLogo -NoProfile -NonInteractive -Command Start-Sleep -Seconds 60'
            $info.UseShellExecute=$false
            $info.CreateNoWindow=$true
            $child=[Diagnostics.Process]::Start($info)
            [Console]::Out.WriteLine($child.Id)
            """;
        var start=new ProcessStartInfo(shell){UseShellExecute=false,CreateNoWindow=true,RedirectStandardInput=true,RedirectStandardOutput=true,RedirectStandardError=true};
        foreach(var arg in new[]{"-NoLogo","-NoProfile","-NonInteractive","-EncodedCommand",Convert.ToBase64String(Encoding.Unicode.GetBytes(script))})start.ArgumentList.Add(arg);
        using var root=Process.Start(start)!;using var timeout=new CancellationTokenSource(TimeSpan.FromSeconds(15));
        using var tree=new ProbeProcessTree(root);
        try
        {
            // Existing input gating makes assignment happen before any descendant spawn.
            await root.StandardInput.WriteLineAsync("start");await root.StandardInput.FlushAsync();
            var line=await root.StandardOutput.ReadLineAsync(timeout.Token);
            using var descendant=Process.GetProcessById(int.Parse(line!));
            _=descendant.Handle;
            await root.WaitForExitAsync(timeout.Token);root.Dispose();Assert.False(descendant.HasExited);
            var settled=tree.JoinAsync();Assert.False(settled.IsCompleted);
            tree.Terminate();await descendant.WaitForExitAsync(timeout.Token);Assert.True(descendant.HasExited);
            descendant.Dispose();await settled.WaitAsync(timeout.Token);
        }
        finally{tree.Terminate();await tree.JoinAsync().WaitAsync(timeout.Token);}
    }
}
