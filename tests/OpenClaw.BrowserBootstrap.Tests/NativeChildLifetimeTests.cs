using System.Diagnostics;

namespace OpenClaw.BrowserBootstrap.Tests;

public class NativeChildLifetimeTests
{
    [Fact]public async Task CancellationKillsAndJoinsSilentOwnedChildBeforeReturning()
    {
        var directory=Path.Combine(Path.GetTempPath(),"OpenClawNativeChildTests-"+Guid.NewGuid().ToString("N"));Directory.CreateDirectory(directory);
        var marker=Path.Combine(directory,"started");
        using var stop=new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var info=new ProcessStartInfo {UseShellExecute=false,CreateNoWindow=true,RedirectStandardInput=true,RedirectStandardOutput=true,RedirectStandardError=true,WorkingDirectory=directory};
        if(OperatingSystem.IsWindows())
        {
            info.FileName=Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System),"WindowsPowerShell","v1.0","powershell.exe");
            foreach(var a in new[]{"-NoLogo","-NoProfile","-NonInteractive","-Command","[IO.File]::WriteAllText('started.tmp',[string]$PID);[IO.File]::Move('started.tmp','started');Start-Sleep -Seconds 60"})info.ArgumentList.Add(a);
        }
        else
        {
            info.FileName="/bin/sh";info.ArgumentList.Add("-c");info.ArgumentList.Add("printf '%s' $$ > started.tmp; mv started.tmp started; sleep 60");
        }
        var run=NativeTransport.ChildAsync(NativeKeylessProbe.Request(false),info,stop.Token);
        try
        {
            while(!File.Exists(marker)){stop.Token.ThrowIfCancellationRequested();await Task.Delay(10,stop.Token);}
            // The marker is a fixture synchronization point, not a timing-only race.
            var pid=int.Parse(await File.ReadAllTextAsync(marker,stop.Token));
            using var owned=Process.GetProcessById(pid);stop.Cancel();
            await Assert.ThrowsAnyAsync<Exception>(()=>run.WaitAsync(TimeSpan.FromSeconds(5)));
            Assert.True(run.IsCompleted);Assert.True(owned.HasExited);
        }
        finally{stop.Cancel();try{await run;}catch{}Directory.Delete(directory,true);}
    }
}
