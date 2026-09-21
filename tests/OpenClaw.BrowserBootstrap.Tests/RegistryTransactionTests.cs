using Microsoft.Win32;

namespace OpenClaw.BrowserBootstrap.Tests;

public sealed class WindowsRegistryFactAttribute:FactAttribute
{
    public WindowsRegistryFactAttribute(){if(!OperatingSystem.IsWindows())Skip="Requires a native Windows registry; never simulated as passing.";}
}
[System.Runtime.Versioning.SupportedOSPlatform("windows")]
public class RegistryTransactionTests
{
    [WindowsRegistryFact]
    public void TrustedInstallerIsOnlyAnExistingPublicAncestorException()
    {
        if(!OperatingSystem.IsWindows())return;
        var sid=(System.Security.Principal.SecurityIdentifier)new System.Security.Principal.NTAccount("NT SERVICE","TrustedInstaller").Translate(typeof(System.Security.Principal.SecurityIdentifier));
        var sd=new System.Security.AccessControl.RawSecurityDescriptor($"O:{sid.Value}G:SYD:(A;;FA;;;{sid.Value})");
        var authority=new WindowsAuthority();
        authority.CheckAcl(sd,false,protectedAncestor:true);
        var siblings=new System.Security.AccessControl.RawSecurityDescriptor($"O:{authority.Sid}G:SYD:(A;;FA;;;{authority.Sid})(A;;0x6;;;WD)");
        authority.CheckAcl(siblings,false,protectedAncestor:true);
        Assert.ThrowsAny<Exception>(()=>authority.CheckAcl(siblings,false));
        var replacement=new System.Security.AccessControl.RawSecurityDescriptor($"O:{authority.Sid}G:SYD:(A;;FA;;;{authority.Sid})(A;;0x10000;;;WD)");
        Assert.ThrowsAny<Exception>(()=>authority.CheckAcl(replacement,false,protectedAncestor:true));
        Assert.ThrowsAny<Exception>(()=>authority.CheckAcl(sd,true,protectedAncestor:true));
        Assert.ThrowsAny<Exception>(()=>authority.CheckAcl(sd,false));
        Assert.ThrowsAny<Exception>(()=>authority.CheckAcl(sd,false,registry:true,protectedAncestor:true));
    }
    [WindowsRegistryFact]
    public void UncontendedTransactionsCreateUpdateAndRemoveOwnedValues()
    {
        if(!OperatingSystem.IsWindows())return;
        var path=@"Software\OpenClawBootstrapTests\"+Guid.NewGuid().ToString("N");
        using var root=RegistryKey.OpenBaseKey(RegistryHive.CurrentUser,RegistryView.Registry32);
        try
        {
            var platform=new WindowsRegistrationPlatform();
            var missing=new WindowsRegistrationPlatform.Row(RegistryHive.CurrentUser,RegistryView.Registry32,path,false,[]);
            platform.MutateAtomically(missing,new("",RegistryValueKind.String,"owned"),default);
            var owned=missing with{Exists=true,Values=[new("",RegistryValueKind.String,"owned")]};
            platform.MutateAtomically(owned,new("",RegistryValueKind.String,"updated"),default);
            using(var key=root.OpenSubKey(path))Assert.Equal("updated",key!.GetValue(""));
            platform.MutateAtomically(owned with{Values=[new("",RegistryValueKind.String,"updated")]},null,default);
            using var after=root.OpenSubKey(path);Assert.Null(after);
        }
        finally{root.DeleteSubKeyTree(path,false);}
    }
    [WindowsRegistryFact]
    public void ForeignChangeBeforeTransactionalAdmissionIsPreserved()
    {
        if(!OperatingSystem.IsWindows())return;
        var path=@"Software\OpenClawBootstrapTests\"+Guid.NewGuid().ToString("N");
        using var root=RegistryKey.OpenBaseKey(RegistryHive.CurrentUser,RegistryView.Registry32);
        try
        {
            using(var key=root.CreateSubKey(path)){key.SetValue("","owned");}
            var expected=new WindowsRegistrationPlatform.Row(RegistryHive.CurrentUser,RegistryView.Registry32,path,true,[new("",RegistryValueKind.String,"owned")]);
            using(var key=root.OpenSubKey(path,true)){key!.SetValue("","foreign");}
            Assert.ThrowsAny<Exception>(()=>new WindowsRegistrationPlatform().MutateAtomically(expected,new("",RegistryValueKind.String,"replacement"),default));
            using var after=root.OpenSubKey(path);Assert.Equal("foreign",after!.GetValue(""));
        }
        finally{root.DeleteSubKeyTree(path,false);}
    }
    [WindowsRegistryFact]
    public async Task ForeignValueAddedDuringDeleteCannotBeDeletedByOurTransaction()
    {
        if(!OperatingSystem.IsWindows())return;
        var path=@"Software\OpenClawBootstrapTests\"+Guid.NewGuid().ToString("N");
        using var root=RegistryKey.OpenBaseKey(RegistryHive.CurrentUser,RegistryView.Registry32);
        Task? foreign=null;
        try
        {
            using(var key=root.CreateSubKey(path)){key.SetValue("","owned");}
            var expected=new WindowsRegistrationPlatform.Row(RegistryHive.CurrentUser,RegistryView.Registry32,path,true,[new("",RegistryValueKind.String,"owned")]);
            var error=Record.Exception(()=>new WindowsRegistrationPlatform().MutateAtomically(expected,null,default,()=>
            {
                foreign=Task.Run(()=>{if(!OperatingSystem.IsWindows())return;using var other=RegistryKey.OpenBaseKey(RegistryHive.CurrentUser,RegistryView.Registry32);using var key=other.CreateSubKey(path);key.SetValue("foreign","retained");});
                // If the kernel serializes the writer, commit first and let it proceed afterwards.
                // If the foreign writer wins, TxR must abort our stale delete.
                _=foreign.Wait(TimeSpan.FromSeconds(2));
            }));
            Assert.NotNull(foreign);await foreign!.WaitAsync(TimeSpan.FromSeconds(10));
            using var after=root.OpenSubKey(path);Assert.NotNull(after);Assert.Equal("retained",after.GetValue("foreign"));
            _=error; // Either serialization order is valid; foreign data must survive.
        }
        finally{if(foreign is not null)await foreign.WaitAsync(TimeSpan.FromSeconds(10));root.DeleteSubKeyTree(path,false);}
    }
}
