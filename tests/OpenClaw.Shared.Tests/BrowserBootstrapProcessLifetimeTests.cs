using System.Diagnostics;
using OpenClaw.Shared.Browser;

namespace OpenClaw.Shared.Tests;

public class BrowserBootstrapProcessLifetimeTests
{
    [Fact]public async Task ExpiredCleanupBudgetCannotCompleteWhileOwnedWorkIsPending()
    {
        var work=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var budget=new CancellationTokenSource();budget.Cancel();
        var wait=BrowserBootstrapProcessLifetime.AwaitOwnedAsync(work.Task,budget.Token);
        try{Assert.False(wait.IsCompleted);work.SetResult();await Assert.ThrowsAnyAsync<OperationCanceledException>(()=>wait);}
        finally{work.TrySetResult();}
    }
    [Fact]public async Task ExpiredCleanupBudgetStillJoinsTheRealOwnedProcess()
    {
        var info=new ProcessStartInfo{UseShellExecute=false,CreateNoWindow=true};
        if(OperatingSystem.IsWindows())
        {
            info.FileName=Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System),"WindowsPowerShell","v1.0","powershell.exe");
            foreach(var a in new[]{"-NoLogo","-NoProfile","-NonInteractive","-Command","Start-Sleep -Seconds 60"})info.ArgumentList.Add(a);
        }
        else{info.FileName="/bin/sh";info.ArgumentList.Add("-c");info.ArgumentList.Add("sleep 60");}
        using var process=Process.Start(info)!;using var budget=new CancellationTokenSource();budget.Cancel();
        var wait=BrowserBootstrapProcessLifetime.JoinAsync(process,budget.Token);
        try
        {
            Assert.False(process.HasExited);Assert.False(wait.IsCompleted);
            process.Kill(true);
            await Assert.ThrowsAnyAsync<OperationCanceledException>(()=>wait.WaitAsync(TimeSpan.FromSeconds(5)));
            Assert.True(wait.IsCompleted);Assert.True(process.HasExited);
        }
        finally{if(!process.HasExited)process.Kill(true);await process.WaitForExitAsync();}
    }
}
