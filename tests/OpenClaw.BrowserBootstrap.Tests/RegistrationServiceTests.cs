using OpenClaw.BrowserBootstrap.Contracts;

namespace OpenClaw.BrowserBootstrap.Tests;

/// <summary>Portable state tuples use injected facts, never claim Windows ACL or registry execution.</summary>
public class RegistrationServiceTests
{
    public static IEnumerable<object[]> Cases()
    {
        yield return ["empty-inspect","inspect","preserve",true,"ok","missing",null!,"missing",false];
        yield return ["empty-uninstall","uninstall","preserve",true,"ok","missing",null!,"missing",false];
        yield return ["empty-remove","uninstall","remove",true,"ok","missing",null!,"missing",false];
        yield return ["owned-inspect","inspect","preserve",true,"ok","owned",ManagementContract.Native,"missing",true];
        yield return ["owned-store","inspect","preserve",true,"ok","owned",ManagementContract.Native,"requested",true];
        yield return ["native-denied","inspect","preserve",false,"unsafe_acl",null!,null!,null!,false];
        yield return ["store-denied","inspect","preserve",false,"unsafe_acl","owned",ManagementContract.Native,null!,true];
        yield return ["orphan-inspect","inspect","preserve",true,"ok","missing",null!,"requested",false];
        yield return ["preserve-foreign","install","preserve",true,"ok","owned",ManagementContract.Native,"foreign",true];
        yield return ["preserve-unknown","install","preserve",true,"ok","owned",ManagementContract.Native,null!,true];
        yield return ["request","install","request",true,"ok","owned",ManagementContract.Native,"requested",true];
        yield return ["late-optout","install","request",false,"browser_control_disabled","owned",ManagementContract.Native,"missing",true];
        yield return ["late-optout-existing","install","request",false,"browser_control_disabled","owned",ManagementContract.Native,"requested",true];
        yield return ["early-optout","install","request",false,"browser_control_disabled","missing",null!,"missing",false];
        yield return ["request-foreign","install","request",false,"foreign_registration","owned",ManagementContract.Native,"foreign",true];
        yield return ["partial-store","install","request",false,"io_error","owned",ManagementContract.Native,"invalid",true];
        yield return ["unknown-store","install","request",false,"io_error","owned",ManagementContract.Native,null!,true];
        yield return ["remove-native-fails","uninstall","remove",false,"io_error","owned",ManagementContract.Native,"missing",true];
        yield return ["remove-foreign","uninstall","remove",false,"foreign_registration","owned",ManagementContract.Native,"foreign",true];
        yield return ["uninstall-preserve","uninstall","preserve",true,"ok","missing",null!,"requested",false];
        yield return ["remove-orphan","uninstall","remove",true,"ok","missing",null!,"missing",false];
        yield return ["other-mode","install","preserve",false,"context_conflict","owned",ManagementContract.Companion,"missing",false];
        yield return ["changed-runtime","inspect","preserve",false,"binding_invalid","owned",ManagementContract.Native,"missing",false];
        yield return ["other-profile","install","preserve",false,"context_conflict","owned",ManagementContract.Native,"missing",false];
        yield return ["mixed-view","inspect","preserve",false,"binding_invalid","invalid",null!,"missing",false];
        yield return ["foreign-native","install","preserve",false,"foreign_registration","foreign",null!,"missing",false];
        yield return ["busy","install","preserve",false,"busy",null!,null!,null!,false];
    }
    [Theory][MemberData(nameof(Cases))]
    public void FrozenStateTuple(string scenario,string action,string store,bool ok,string code,string? registration,string? mode,string? observedStore,bool descriptor)
    {
        var request=ManagementContractTests.Request() with{Action=action,Store=store};var fake=new Fake(scenario,request);
        var response=new RegistrationService(fake).Execute(request,default);
        Assert.Equal(ok,response.Ok);Assert.Equal(code,response.Code);Assert.Equal(registration,response.Registration);
        Assert.Equal(mode,response.Mode);Assert.Equal(observedStore,response.Store);Assert.Equal(descriptor,response.Installation is not null);
        _=ManagementContract.ResponseBytes(response);
        if(scenario is "other-mode" or "other-profile" or "foreign-native" or "early-optout" or "busy")Assert.Empty(fake.Mutations);
        if(scenario=="remove-foreign")Assert.DoesNotContain("remove-native",fake.Mutations);
        if(scenario=="remove-native-fails")Assert.Equal(new[]{"remove-store","remove-native"},fake.Mutations);
        if(action=="inspect")Assert.Empty(fake.Mutations);
    }
    [Fact]public void EmptyUninstallRemainsIdempotent()
    {
        var r=ManagementContractTests.Request() with{Action="uninstall",Store="remove"};var f=new Fake("empty-remove",r);var service=new RegistrationService(f);
        Assert.True(service.Execute(r,default).Ok);Assert.True(service.Execute(r,default).Ok);Assert.DoesNotContain("prepare",f.Mutations);
    }
    [Fact]public void SameContextRuntimeUpgradeIsExplicit()
    {
        var r=ManagementContractTests.Request() with{Action="install"};var f=new Fake("changed-runtime",r);
        Assert.True(new RegistrationService(f).Execute(r,default).Ok);Assert.Contains("prepare",f.Mutations);
    }
    private sealed class Fake:IRegistrationPlatform
    {
        private readonly string scenario;private readonly ManagementRequest request;
        private NativeInventory native=new("missing",[]);private StoreInventory store=new("missing");
        private bool enabled=true;public List<string> Mutations{get;}=[];
        public Fake(string scenario,ManagementRequest request)
        {
            this.scenario=scenario;this.request=request;
            if(scenario is "owned-inspect" or "owned-store" or "store-denied" or "remove-native-fails" or "remove-foreign" or "uninstall-preserve" or "changed-runtime" or "other-profile")native=new("owned",[Make(request)]);
            if(scenario is "owned-store" or "late-optout-existing" or "orphan-inspect" or "uninstall-preserve" or "remove-orphan" or "remove-native-fails")store=new("requested");
            if(scenario is "preserve-foreign" or "request-foreign" or "remove-foreign")store=new("foreign");
            if(scenario is "preserve-unknown" or "store-denied")store=new(null,"unsafe_acl");
            if(scenario=="native-denied"){native=new(null,[],"unsafe_acl");store=new(null,"unsafe_acl");}
            if(scenario=="early-optout")enabled=false;
            if(scenario=="other-mode")native=new("owned",[Make(request with{Mode=ManagementContract.Companion,Context=null})]);
            if(scenario=="other-profile")native=new("owned",[Make(request with{Context=request.Context! with{BrowserProfile="other"}})]);
            if(scenario=="changed-runtime")native=new("owned",[Make(request with{Context=request.Context! with{NodePath=@"C:\Old\node.exe"}})]);
            if(scenario=="mixed-view")native=new("invalid",[Make(request)]);
            if(scenario=="foreign-native")native=new("foreign",[]);
        }
        public IDisposable Acquire(){if(scenario=="busy")throw new ContractException("busy");return new Gate();}
        public Inventory Observe(ManagementRequest r)=>new(native,store);
        public bool BrowserControlAllowsRequest()=>enabled;
        public void ValidateRuntime(Generation g){}
        public Generation Prepare(ManagementRequest r,CancellationToken ct){Mutations.Add("prepare");return Make(r);}
        public void PublishNative(ManagementRequest r,Generation g,CancellationToken ct){Mutations.Add("publish-native");native=new("owned",[g]);if(scenario.StartsWith("late-optout",StringComparison.Ordinal))enabled=false;}
        public void RequestStore(ManagementRequest r,Generation g,CancellationToken ct)
        {
            Mutations.Add("request-store");
            if(scenario=="partial-store"){store=new("invalid");throw new IOException();}
            if(scenario=="unknown-store"){store=new(null,"io_error");throw new IOException();}
            store=new("requested");
        }
        public void RemoveStore(ManagementRequest r,CancellationToken ct){Mutations.Add("remove-store");store=new("missing");}
        public void RemoveNative(ManagementRequest r,CancellationToken ct){Mutations.Add("remove-native");if(scenario=="remove-native-fails")throw new IOException();native=new("missing",[]);}
        private sealed class Gate:IDisposable{public void Dispose(){}}
    }
    internal static Generation Make(ManagementRequest request)
    {
        const string guid="12345678-1234-4234-8234-123456789abc";
        const string root=@"C:\Users\Fixture\AppData\Local\OpenClawTray\browser-native\generations\"+guid+@"\";
        var d=new Installation(guid,root+ManagementContract.ManifestName,root+ManagementContract.ExeName,root+ManagementContract.BindingName,root+ManagementContract.ReceiptName);
        return new(new(1,request.Mode,d.ManifestPath,request.ExpectedOrigins,request.Context),
            new(ManagementContract.Owner,1,guid,"S-1-5-21-111-222-333-1001",true,new('0',64),new('0',64),new('0',64)),d);
    }
}
