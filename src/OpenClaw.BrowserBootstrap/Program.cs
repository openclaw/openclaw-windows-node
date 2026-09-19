using OpenClaw.BrowserBootstrap.Contracts;

namespace OpenClaw.BrowserBootstrap;

internal static class Program
{
    internal static async Task<int> Main(string[] args)
    {
        var management=args.Length>0&&args[0]=="--manage";
        using var process=System.Diagnostics.Process.GetCurrentProcess();
        var elapsed=DateTime.UtcNow-process.StartTime.ToUniversalTime();
        using var overall=new CancellationTokenSource(TimeSpan.FromMilliseconds(Math.Max(1,60000-elapsed.TotalMilliseconds)));
        try
        {
            if(management)
            {
                ManagementResponse response;
                try
                {
                    ManagementContract.Require(args.Length==1);
                    using var inputDeadline=CancellationTokenSource.CreateLinkedTokenSource(overall.Token);
                    inputDeadline.CancelAfter(TimeSpan.FromMilliseconds(Math.Max(1,5000-elapsed.TotalMilliseconds)));
                    var bytes=await ReadManagementAsync(Console.OpenStandardInput(),inputDeadline.Token);
                    var request=ManagementContract.ParseRequest(bytes);
                    if(!OperatingSystem.IsWindows())response=ManagementResponse.Failure("platform_unsupported");
                    else
                    {
                        using var deadline=CancellationTokenSource.CreateLinkedTokenSource(overall.Token);
                        // Mutex acquisition, all mutations and release execute on the same thread.
                        response=await Task.Run(()=> OperatingSystem.IsWindows()
                            ? new RegistrationService(new WindowsRegistrationPlatform()).Execute(request,deadline.Token)
                            : ManagementResponse.Failure("platform_unsupported"));
                    }
                }
                catch(OperationCanceledException){response=ManagementResponse.Failure("invalid_request");}
                catch(Exception ex){response=ManagementResponse.Failure(ex is ContractException c?c.Code:"io_error");}
                var output=ManagementContract.ResponseBytes(response);
                using var writeDeadline=CancellationTokenSource.CreateLinkedTokenSource(overall.Token);
                writeDeadline.CancelAfter(TimeSpan.FromSeconds(2));
                await Console.OpenStandardOutput().WriteAsync(output,writeDeadline.Token);
                return response.Ok?0:1;
            }
            if(!OperatingSystem.IsWindows())return 1;
            var authority=new WindowsAuthority();var generations=new GenerationStore(authority);
            var exe=Environment.ProcessPath??throw new IOException();
            var manifest=Path.Combine(Path.GetDirectoryName(exe)!,ManagementContract.ManifestName);
            using var timeout=new CancellationTokenSource(TimeSpan.FromSeconds(30));
            await NativeTransport.RunAsync(Console.OpenStandardInput(),Console.OpenStandardOutput(),args,()=>
            {
                if(!OperatingSystem.IsWindows())throw new ContractException("platform_unsupported");
                var g=generations.Read(manifest);
                if(!ManagementContract.PathEquals(g.Installation.LauncherPath,exe))throw new ContractException("binding_invalid");
                return g;
            },generations.RuntimeLease,timeout.Token);
            return 0;
        }
        catch{return 1;} // No stdout/stderr diagnostics outside a complete typed response.
    }
    internal static async Task<byte[]> ReadManagementAsync(Stream input,CancellationToken ct)
    {
        var buffer=new byte[ManagementContract.Limit+1];var size=0;
        while(true)
        {
            var n=await input.ReadAsync(buffer.AsMemory(size),ct);
            if(n==0)return buffer[..size];
            size+=n;if(size>ManagementContract.Limit)throw new ContractException("invalid_request");
        }
    }
}
