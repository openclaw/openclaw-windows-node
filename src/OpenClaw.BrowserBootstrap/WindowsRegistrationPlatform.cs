using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;
using System.Runtime.Versioning;
using System.Security.AccessControl;
using System.Text.Json;
using Microsoft.Win32;
using OpenClaw.BrowserBootstrap.Contracts;

namespace OpenClaw.BrowserBootstrap;

/// <summary>Sole native and Store registry writer. No alternate TS/PowerShell mutation backend.</summary>
[SupportedOSPlatform("windows")]
internal sealed class WindowsRegistrationPlatform : IRegistrationPlatform
{
    internal const string StoreKey = @"Software\Google\Chrome\Extensions\kcdjddhmeafeomebliikmbpblkmkfoig";
    internal const string StoreOwner = "openclaw_native_host", UpdateUrl = "https://clients2.google.com/service/update2/crx";
    private static readonly string[] NativeKeys = [@"Software\Google\Chrome\NativeMessagingHosts\ai.openclaw.browser_bootstrap",@"Software\Chromium\NativeMessagingHosts\ai.openclaw.browser_bootstrap"];
    private readonly WindowsAuthority authority;
    internal GenerationStore Generations { get; }
    private readonly string[] nativeKeys=NativeKeys;
    internal WindowsRegistrationPlatform(){authority=new();Generations=new(authority);}
    public IDisposable Acquire()=>authority.Lock();
    public void ValidateRuntime(Generation generation){using var lease=Generations.RuntimeLease(generation);}
    public Generation Prepare(ManagementRequest request,CancellationToken ct)=>Generations.Prepare(request,ct,ObserveNative().Generations);
    internal sealed record Value(string Name,RegistryValueKind Kind,string? Text);
    internal sealed record Row(RegistryHive Hive,RegistryView View,string Path,bool Exists,Value[] Values);
    private RegistryView[] Views=>Environment.Is64BitOperatingSystem?[RegistryView.Registry32,RegistryView.Registry64]:[RegistryView.Registry32];
    private void CheckKey(RegistryKey key)=>authority.CheckAcl(new RawSecurityDescriptor(key.GetAccessControl().GetSecurityDescriptorBinaryForm(),0),false,true);
    private Row FromKey(RegistryHive hive,RegistryView view,string path,RegistryKey key)
    {
        CheckKey(key);
        if(key.SubKeyCount!=0)throw new ContractException("foreign_registration");
        return new(hive,view,path,true,key.GetValueNames().Order(StringComparer.Ordinal).Select(n=>new Value(n,key.GetValueKind(n),key.GetValue(n,null,RegistryValueOptions.DoNotExpandEnvironmentNames) as string)).ToArray());
    }
    private Row ReadRow(RegistryHive hive,RegistryView view,string path)
    {
        using var root=RegistryKey.OpenBaseKey(hive,view);using var key=root.OpenSubKey(path,false);
        if(key is null)return new(hive,view,path,false,[]);
        return FromKey(hive,view,path,key);
    }
    private Row[] Rows(string[] paths)=>(from path in paths from hive in new[]{RegistryHive.CurrentUser,RegistryHive.LocalMachine} from view in Views select ReadRow(hive,view,path)).ToArray();
    private static bool Same(Row a,Row b)=>a.Hive==b.Hive&&a.View==b.View&&a.Path==b.Path&&a.Exists==b.Exists&&a.Values.SequenceEqual(b.Values);
    public Inventory Observe(ManagementRequest request)=>new(ObserveNative(),ObserveStore(request));
    private NativeInventory ObserveNative(Row[]? snapshot=null)
    {
        try
        {
            var rows=snapshot??Rows(nativeKeys);
            if(rows.Any(r=>r.Values.Any(v=>v.Name!=""||v.Kind!=RegistryValueKind.String||v.Text is null)||r.Hive==RegistryHive.LocalMachine&&r.Values.Length!=0))return new("foreign",[]);
            var paths=rows.SelectMany(r=>r.Values).Select(v=>v.Text!).Distinct(StringComparer.Ordinal).ToArray();
            if(paths.Length==0)return new("missing",[]);
            var generations=paths.Select(Generations.Read).ToArray();
            var user=rows.Where(r=>r.Hive==RegistryHive.CurrentUser).ToArray();
            var complete=generations.Length==1&&user.All(r=>r.Values.Length==1&&r.Values[0].Text==generations[0].Installation.ManifestPath);
            return new(complete?"owned":"invalid",generations);
        }
        catch(ContractException e) when(e.Code=="foreign_registration"){return new("foreign",[]);}
        catch(ContractException e) when(e.Code=="binding_invalid"||e.Code=="invalid_request"){return new("invalid",[],"binding_invalid");}
        catch(Exception e){return new(null,[],RegistrationService.Code(e));}
    }
    private StoreInventory ObserveStore(ManagementRequest request,Row[]? snapshot=null)
    {
        try
        {
            var rows=snapshot??Rows([StoreKey]);
            if(rows.Any(r=>r.Hive==RegistryHive.LocalMachine&&r.Values.Length!=0))return new("foreign");
            var states=new List<string>();
            foreach(var row in rows.Where(r=>r.Hive==RegistryHive.CurrentUser))
            {
                if(!row.Exists){states.Add("missing");continue;}
                if(row.Values.Length is <1 or >2||row.Values.Any(v=>v.Name is not (StoreOwner or "update_url")||v.Kind!=RegistryValueKind.String||v.Text is null))return new("foreign");
                var owner=row.Values.SingleOrDefault(v=>v.Name==StoreOwner)?.Text;
                if(owner is null||!ManagementContract.IsPath(owner)||Path.GetFileName(owner)!=ManagementContract.ExeName)return new("foreign");
                Generation generation;
                try{generation=Generations.Read(Path.Combine(Path.GetDirectoryName(owner)!,ManagementContract.ManifestName));}
                catch(ContractException e) when(e.Code is "binding_invalid" or "foreign_registration" or "invalid_request"){return new("foreign");}
                if(owner!=generation.Installation.LauncherPath||!ManagementContract.SameOwnership(request,generation.Binding))return new("foreign");
                var update=row.Values.SingleOrDefault(v=>v.Name=="update_url");
                if(update is not null&&update.Text!=UpdateUrl)return new("foreign");
                states.Add(update is null?"invalid":"requested");
            }
            return new(states.All(s=>s=="missing")?"missing":states.All(s=>s=="requested")?"requested":"invalid");
        }
        catch(ContractException e) when(e.Code=="foreign_registration"){return new("foreign");}
        catch(Exception e){return new(null,RegistrationService.Code(e));}
    }
    public bool BrowserControlAllowsRequest()
    {
        try
        {
            var file=Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData,Environment.SpecialFolderOption.DoNotVerify),"OpenClawTray","settings.json");
            if(!File.Exists(file))return true;
            return BrowserControlPreference.Allows(authority.Read(file,1024*1024,false));
        }
        catch{return false;}
    }
    public void PublishNative(ManagementRequest request,Generation generation,CancellationToken ct)
    {
        var snapshot=Rows(nativeKeys);
        var current=ObserveNative(snapshot);
        if(current.Error is {} error)throw new ContractException(error);
        if(current.State=="foreign")throw new ContractException("foreign_registration");
        if(current.Generations.Any(g=>!ManagementContract.SameOwnership(request,g.Binding)))throw new ContractException("context_conflict");
        _=Generations.Read(generation.Installation.ManifestPath);
        Change(nativeKeys,snapshot,[new("",RegistryValueKind.String,generation.Installation.ManifestPath)],false,ct);
    }
    public void RequestStore(ManagementRequest request,Generation generation,CancellationToken ct)
    {
        var snapshot=Rows([StoreKey]);
        var state=ObserveStore(request,snapshot);
        if(state.Error is {} error)throw new ContractException(error);
        if(state.State=="foreign")throw new ContractException("foreign_registration");
        if(!BrowserControlAllowsRequest())throw new ContractException("browser_control_disabled");
        var live=ObserveNative();
        if(live.Active?.Installation!=generation.Installation)throw new ContractException("binding_invalid");
        _=Generations.Read(generation.Installation.ManifestPath);
        // Owner is published first; update_url is the separately observable Store request.
        Change([StoreKey],snapshot,[new(StoreOwner,RegistryValueKind.String,generation.Installation.LauncherPath),new("update_url",RegistryValueKind.String,UpdateUrl)],false,ct,guard:()=>
        {
            if(!BrowserControlAllowsRequest())throw new ContractException("browser_control_disabled");
            if(ObserveNative().Active?.Installation!=generation.Installation)throw new ContractException("binding_invalid");
            _=Generations.Read(generation.Installation.ManifestPath);
        });
    }
    public void RemoveStore(ManagementRequest request,CancellationToken ct)
    {
        var snapshot=Rows([StoreKey]);
        var state=ObserveStore(request,snapshot);if(state.Error is {} e)throw new ContractException(e);
        if(state.State=="foreign")throw new ContractException("foreign_registration");
        Change([StoreKey],snapshot,[],true,ct);
    }
    public void RemoveNative(ManagementRequest request,CancellationToken ct)
    {
        var snapshot=Rows(nativeKeys);
        var state=ObserveNative(snapshot);if(state.Error is {} e)throw new ContractException(e);
        if(state.State=="foreign")throw new ContractException("foreign_registration");
        if(state.Generations.Any(g=>!ManagementContract.SameOwnership(request,g.Binding)))throw new ContractException("context_conflict");
        Change(nativeKeys,snapshot,[],true,ct);
        // Retain generations. Store/other views may reference them, and GC is not an ABI postcondition.
    }
    private void Change(string[] paths,Row[] expected,Value[] values,bool remove,CancellationToken ct,Action? guard=null)
    {
        foreach(var identity in expected.Where(r=>r.Hive==RegistryHive.CurrentUser).Select(r=>(r.Hive,r.View,r.Path)).ToArray())
        {
            foreach(var value in remove ? new Value?[]{null} : values.Cast<Value?>())
            {
                var target=expected.Single(r=>r.Hive==identity.Hive&&r.View==identity.View&&r.Path==identity.Path);
                ct.ThrowIfCancellationRequested();
                var current=Rows(paths);
                if(!expected.Zip(current).All(p=>Same(p.First,p.Second)))throw new ContractException("io_error");
                // Empty keys are absence under the acknowledged ABI, not ownership or permission to delete them.
                if(remove&&target.Values.Length==0)continue;
                guard?.Invoke();
                MutateAtomically(target,value,ct);
                var desired=value is null?Array.Empty<Value>():target.Values.Where(v=>v.Name!=value.Name).Append(value).OrderBy(v=>v.Name,StringComparer.Ordinal).ToArray();
                var after=Rows(paths);
                for(var i=0;i<after.Length;i++)
                {
                    if(after[i].Hive==RegistryHive.CurrentUser&&after[i].Path==target.Path)
                    {
                        var intended=remove?!after[i].Exists:after[i].Values.SequenceEqual(desired);
                        if(!intended&&!Same(after[i],expected[i]))throw new ContractException("io_error");
                    }
                    else if(!Same(after[i],expected[i]))throw new ContractException("io_error");
                }
                expected=after;
            }
        }
    }
    internal void MutateAtomically(Row expected,Value? value,CancellationToken ct,Action? beforeMutation=null)
    {
        using var transaction=CreateTransaction(IntPtr.Zero,IntPtr.Zero,0,0,0,5000,null);
        if(transaction.IsInvalid)throw new ContractException("io_error");
        using var root=RegistryKey.OpenBaseKey(expected.Hive,expected.View);
        var view=expected.View==RegistryView.Registry64?0x0100u:0x0200u;
        // TxR rolls back if a non-transacted writer changes the enrolled key before commit.
        // Per-key CAS does not promise a cross-product/view transaction or rollback.
        var status=RegOpenKeyTransacted(root.Handle,expected.Path,0,0x3001f|view,out var handle,transaction,IntPtr.Zero);
        var created=false;
        if(status is 2 or 3)
        {
            handle.Dispose();
            if(expected.Exists)throw new ContractException("io_error");
            if(value is null)return;
            var prefix="";
            foreach(var part in expected.Path.Split('\\'))
            {
                prefix=prefix.Length==0?part:prefix+"\\"+part;
                using var ancestor=root.OpenSubKey(prefix,false);if(ancestor is not null)CheckKey(ancestor);
            }
            status=RegCreateKeyTransacted(root.Handle,expected.Path,0,null,0,0x3001f|view,IntPtr.Zero,out handle,out var disposition,transaction,IntPtr.Zero);
            created=disposition==1;
        }
        using(handle)
        {
            if(status!=0)throw new ContractException(status==5?"unsafe_acl":"io_error");
            using var key=RegistryKey.FromHandle(handle,expected.View);
            var observed=FromKey(expected.Hive,expected.View,expected.Path,key);
            if(created)observed=observed with{Exists=false};
            if(!Same(expected,observed))throw new ContractException("foreign_registration");
            ct.ThrowIfCancellationRequested();
            beforeMutation?.Invoke();
            if(value is null)
            {
                var removed=RegDeleteKeyTransacted(root.Handle,expected.Path,view,0,transaction,IntPtr.Zero);
                if(removed!=0)throw new ContractException(removed==5?"unsafe_acl":"io_error");
            }
            else key.SetValue(value.Name,value.Text!,value.Kind);
            // TxR reads are not a snapshot lock. A foreign write can win after our first
            // comparison but before we stage the mutation. Our staged changes remain
            // invisible to this non-transacted read; compare again while holding the
            // transactional write conflict boundary, before allowing commit.
            if(!Same(expected,ReadRow(expected.Hive,expected.View,expected.Path)))throw new ContractException("foreign_registration");
            ct.ThrowIfCancellationRequested();
            if(!CommitTransaction(transaction))throw new ContractException("io_error");
        }
    }
    [DllImport("KtmW32.dll",CharSet=CharSet.Unicode,SetLastError=true)]
    private static extern SafeFileHandle CreateTransaction(IntPtr attributes,IntPtr unitOfWork,uint options,uint isolationLevel,uint isolationFlags,uint timeout,string? description);
    [DllImport("KtmW32.dll",SetLastError=true)] [return:MarshalAs(UnmanagedType.Bool)]
    private static extern bool CommitTransaction(SafeFileHandle transaction);
    [DllImport("advapi32.dll",CharSet=CharSet.Unicode,EntryPoint="RegOpenKeyTransactedW")]
    private static extern int RegOpenKeyTransacted(SafeRegistryHandle root,string path,uint options,uint desired,out SafeRegistryHandle key,SafeFileHandle transaction,IntPtr extended);
    [DllImport("advapi32.dll",CharSet=CharSet.Unicode,EntryPoint="RegCreateKeyTransactedW")]
    private static extern int RegCreateKeyTransacted(SafeRegistryHandle root,string path,int reserved,string? @class,uint options,uint desired,IntPtr security,
        out SafeRegistryHandle key,out uint disposition,SafeFileHandle transaction,IntPtr extended);
    [DllImport("advapi32.dll",CharSet=CharSet.Unicode,EntryPoint="RegDeleteKeyTransactedW")]
    private static extern int RegDeleteKeyTransacted(SafeRegistryHandle root,string path,uint view,int reserved,SafeFileHandle transaction,IntPtr extended);
}
