using System.Buffers.Binary;
using System.Diagnostics;
using System.Runtime.Versioning;
using System.Security.Cryptography;
using OpenClaw.BrowserBootstrap.Contracts;
using OpenClaw.Shared.Browser;

namespace OpenClaw.BrowserBootstrap;

[SupportedOSPlatform("windows")]
internal sealed class GenerationStore(WindowsAuthority authority)
{
    public Installation Descriptor(string generation)
    {
        if (!ManagementContract.IsGuid(generation)) throw new ContractException("binding_invalid");
        var dir=Path.Combine(authority.Root,generation);
        var d=new Installation(generation,Path.Combine(dir,ManagementContract.ManifestName),Path.Combine(dir,ManagementContract.ExeName),
            Path.Combine(dir,ManagementContract.BindingName),Path.Combine(dir,ManagementContract.ReceiptName));
        if(!new[]{d.ManifestPath,d.LauncherPath,d.BindingPath,d.ReceiptPath}.All(ManagementContract.IsPath))throw new ContractException("unsafe_path");
        return d;
    }
    public Generation Read(string manifestPath)
    {
        try
        {
            if(!ManagementContract.IsPath(manifestPath))throw new ContractException("foreign_registration");
            var dir=Path.GetDirectoryName(manifestPath)!;var id=Path.GetFileName(dir);
            if(!ManagementContract.IsGuid(id)||!ManagementContract.PathEquals(Path.GetDirectoryName(dir)!,authority.Root)||Path.GetFileName(manifestPath)!=ManagementContract.ManifestName)
                throw new ContractException("foreign_registration");
            var d=Descriptor(id);
            using var root=authority.Admit(authority.Root,true,true);using var generation=authority.Admit(dir,true,true);
            var names=Directory.EnumerateFileSystemEntries(dir).Select(Path.GetFileName).Order(StringComparer.Ordinal).ToArray();
            ManagementContract.ValidateFileNames(names!);
            var bindingBytes=authority.Read(d.BindingPath,ManagementContract.Limit,true);
            var manifestBytes=authority.Read(d.ManifestPath,ManagementContract.Limit,true);
            var receiptBytes=authority.Read(d.ReceiptPath,ManagementContract.Limit,true);
            var receipt=ManagementContract.ParseReceipt(receiptBytes);
            var binding=ManagementContract.ParseBinding(bindingBytes);var manifest=ManagementContract.ParseManifest(manifestBytes);
            if(receipt.OwnerSid!=authority.Sid||receipt.Generation!=id)throw new ContractException("unsafe_acl");
            ManagementContract.ValidateMetadata(d,authority.Sid,bindingBytes,receiptBytes,manifestBytes,authority.Read(d.LauncherPath,256*1024*1024,true));
            return new(binding,receipt,d);
        }
        catch(ContractException e) when(e.Code=="invalid_request"){throw new ContractException("binding_invalid");}
    }
    public IDisposable RuntimeLease(Generation generation)
    {
        var leases=new List<IDisposable>();
        try
        {
            var fresh=Read(generation.Installation.ManifestPath);
            if(fresh.Receipt!=generation.Receipt)throw new ContractException("binding_invalid");
            foreach(var file in new[]{fresh.Installation.LauncherPath,fresh.Installation.BindingPath,fresh.Installation.ReceiptPath,fresh.Installation.ManifestPath})
                leases.Add(authority.Admit(file,false,true));
            if(fresh.Binding.NativeWindows is {} c)
            {
                leases.Add(authority.Admit(c.NodePath,false,false));leases.Add(authority.Admit(c.CliPath,false,false));
                leases.Add(authority.Admit(c.StateDir,true,true,true));leases.Add(authority.Admit(c.ConfigPath,false,true,true));
            }
            return new Leases(leases);
        }
        catch{foreach(var l in leases)l.Dispose();throw;}
    }
    public Generation Prepare(ManagementRequest request,CancellationToken ct,Generation[]? existing=null)
    {
        var source=Environment.ProcessPath??throw new ContractException("unsafe_path");
        var bytes=authority.Read(source,256*1024*1024,false);
        if(bytes.Length<64||bytes[0]!='M'||bytes[1]!='Z')throw new ContractException("unsafe_path");
        var pe=BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(60));
        if(pe<64||pe>bytes.Length-6||BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(pe))!=0x4550||
            BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(pe+4)) is not (0x8664 or 0xaa64))throw new ContractException("unsafe_path");
        foreach(var previous in existing ?? [])
            if(ManagementContract.Matches(request,previous.Binding) && previous.Receipt.ExecutableSha256==Hash(bytes))
            { using var admitted=RuntimeLease(previous); return previous; }
        var d=Descriptor(Guid.NewGuid().ToString("D"));
        var binding=new HostBinding(1,request.Mode,d.ManifestPath,request.ExpectedOrigins,request.Context);
        var manifest=new HostManifest(ManagementContract.HostName,ManagementContract.Description,d.LauncherPath,"stdio",request.ExpectedOrigins);
        var b=ManagementContract.Serialize(binding);var m=ManagementContract.Serialize(manifest);
        var receipt=new HostReceipt(ManagementContract.Owner,1,d.Generation,authority.Sid,true,Hash(bytes),Hash(b),Hash(m));
        var r=ManagementContract.Serialize(receipt);
        if(new[]{b.Length,m.Length,r.Length}.Any(n=>n>ManagementContract.Limit))throw new ContractException("unsafe_path");
        try { _=ManagementContract.ResponseBytes(new(1,true,"ok","owned",request.Mode,"requested",d)); }
        catch(ContractException) { throw new ContractException("unsafe_path"); }
        if(request.Context is {} context)
        {
            using var node=authority.Admit(context.NodePath,false,false);using var cli=authority.Admit(context.CliPath,false,false);
            using var state=authority.Admit(context.StateDir,true,true,true);using var config=authority.Admit(context.ConfigPath,false,true,true);
        }
        ct.ThrowIfCancellationRequested();
        authority.EnsurePrivateDirectory(authority.Root);authority.EnsurePrivateDirectory(Path.GetDirectoryName(d.ManifestPath)!);
        var files=new[]{(d.LauncherPath,bytes),(d.BindingPath,b),(d.ManifestPath,m),(d.ReceiptPath,r)};
        string? receiptIdentity=null;
        foreach(var (file,data) in files)
        {
            ct.ThrowIfCancellationRequested();using var stream=new FileStream(file,FileMode.CreateNew,FileAccess.Write,FileShare.None);
            if(file==d.ReceiptPath)receiptIdentity=authority.FileIdentity(stream.SafeFileHandle);
            stream.Write(data);stream.Flush(true);authority.CheckHandle(stream.SafeFileHandle,true);
        }
        try
        {
            // This private candidate is never reported as ACTIVE. Both keyless child probes must finish before any registry publication.
            Probe(d.LauncherPath,request.ExpectedOrigins[0],invalid:true,ct).GetAwaiter().GetResult();
            var denied=ManagementContract.RejectionOrigin(request.ExpectedOrigins);
            Probe(d.LauncherPath,denied,invalid:false,ct).GetAwaiter().GetResult();
            return Read(d.ManifestPath);
        }
        catch(Exception failure)
        {
            // No cleanup of foreign replacements or referenced generations. Make our unchanged candidate receipt unpublishable.
            try
            {
                using var admitted=authority.Admit(d.ReceiptPath,false,true);
                using var file=new FileStream(d.ReceiptPath,FileMode.Open,FileAccess.ReadWrite,FileShare.None);
                authority.CheckHandle(file.SafeFileHandle,true);
                if(receiptIdentity is not null && authority.FileIdentity(file.SafeFileHandle)==receiptIdentity && file.Length==r.Length)
                {
                    var currentBytes=new byte[r.Length];file.ReadExactly(currentBytes);
                    if(currentBytes.SequenceEqual(r)){file.Position=0;var failed=ManagementContract.Serialize(receipt with{TransportVerified=false});file.Write(failed);file.SetLength(failed.Length);file.Flush(true);}
                }
            }
            catch{/* Uncertain ownership is never permission to remove or overwrite data. */}
            if(failure is OperationCanceledException)throw;
            throw new ContractException("transport_failed");
        }
    }
    private static async Task Probe(string exe,string origin,bool invalid,CancellationToken ct)
    {
        var info=new ProcessStartInfo(exe){UseShellExecute=false,CreateNoWindow=true,RedirectStandardInput=true,RedirectStandardOutput=true,RedirectStandardError=true};
        info.ArgumentList.Add(origin);info.ArgumentList.Add("--parent-window=0");
        using var child=Process.Start(info)??throw new ContractException("transport_failed");
        using var timeout=CancellationTokenSource.CreateLinkedTokenSource(ct);timeout.CancelAfter(TimeSpan.FromSeconds(15));
        try
        {
            var request=ManagementContract.Serialize(new{v=1,op="bootstrap",nonce=invalid?"!":Convert.ToBase64String(new byte[16]).TrimEnd('=')});
            await BrowserNativeProtocol.WriteAsync(child.StandardInput.BaseStream,request,timeout.Token); // Do not close Chrome stdin.
            var response=await BrowserNativeProtocol.ReadAsync(child.StandardOutput.BaseStream,4096,timeout.Token);
            var expected=BrowserNativeProtocol.Failure(invalid?"invalid_request":"origin_forbidden");
            var extra=new byte[1];
            if(!response.SequenceEqual(expected)||await child.StandardOutput.BaseStream.ReadAsync(extra,timeout.Token)!=0||
                await child.StandardError.BaseStream.ReadAsync(extra,timeout.Token)!=0)throw new ContractException("transport_failed");
            await child.WaitForExitAsync(timeout.Token);if(child.ExitCode!=0)throw new ContractException("transport_failed");
        }
        finally{if(!child.HasExited)child.Kill(true);using var end=new CancellationTokenSource(TimeSpan.FromSeconds(2));await child.WaitForExitAsync(end.Token);}
    }
    public static string Hash(byte[] data)=>Convert.ToHexStringLower(SHA256.HashData(data));
    private sealed class Leases(List<IDisposable> leases):IDisposable{public void Dispose(){foreach(var l in leases)l.Dispose();}}
}
