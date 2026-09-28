using System.Security.Cryptography;
using System.Text;
using OpenClaw.BrowserBootstrap.Contracts;

namespace OpenClaw.BrowserBootstrap.Tests;

public class GenerationMetadataTests
{
    private static string Hash(byte[] b)=>Convert.ToHexStringLower(SHA256.HashData(b));
    [Fact]
    public void ExactBytesAndCanonicalLinksAreRequired()
    {
        var g=RegistrationServiceTests.Make(ManagementContractTests.Request());var d=g.Installation;
        var image=Encoding.UTF8.GetBytes("synthetic metadata fixture, not executable proof");
        var b=ManagementContract.Serialize(g.Binding);
        var m=ManagementContract.Serialize(new HostManifest(ManagementContract.HostName,ManagementContract.Description,d.LauncherPath,"stdio",g.Binding.ExpectedOrigins));
        var receipt=g.Receipt with{ExecutableSha256=Hash(image),BindingSha256=Hash(b),ManifestSha256=Hash(m)};
        var r=ManagementContract.Serialize(receipt);
        ManagementContract.ValidateMetadata(d,receipt.OwnerSid,b,r,m,image);
        Assert.Throws<ContractException>(()=>ManagementContract.ValidateMetadata(d,receipt.OwnerSid,[..b,(byte)' '],r,m,image));
        Assert.Throws<ContractException>(()=>ManagementContract.ValidateMetadata(d,receipt.OwnerSid,b,r,m,[..image,0]));
        Assert.Throws<ContractException>(()=>ManagementContract.ValidateMetadata(d,"S-1-5-21-1-2-3-4",b,r,m,image));
        Assert.Throws<ContractException>(()=>ManagementContract.ValidateMetadata(d,receipt.OwnerSid,b,ManagementContract.Serialize(receipt with{Generation=Guid.NewGuid().ToString("D")}),m,image));
        Assert.Throws<ContractException>(()=>ManagementContract.ValidateMetadata(d with{BindingPath=@"C:\another\OpenClaw.BrowserBootstrap.binding.json"},receipt.OwnerSid,b,r,m,image));
    }
    [Theory][InlineData("OpenClaw Companion browser pairing")][InlineData("OpenClaw browser extension bootstrap\n")]
    public void OldOrNormalizedManifestDescriptionIsNotOwned(string description)
    {
        var d=RegistrationServiceTests.Make(ManagementContractTests.Request()).Installation;
        Assert.Throws<ContractException>(()=>ManagementContract.ParseManifest(ManagementContract.Serialize(new HostManifest(ManagementContract.HostName,description,d.LauncherPath,"stdio",[ManagementContract.Origin]))));
    }
    [Fact]public void DiscriminatedShapeAndCompletedReceipt()
    {
        var g=RegistrationServiceTests.Make(ManagementContractTests.Request());
        Assert.Throws<ContractException>(()=>ManagementContract.ParseBinding(ManagementContract.Serialize(g.Binding with{NativeWindows=null})));
        Assert.Throws<ContractException>(()=>ManagementContract.ParseBinding(ManagementContract.Serialize(g.Binding with{Mode=ManagementContract.Companion})));
        Assert.Throws<ContractException>(()=>ManagementContract.ParseReceipt(ManagementContract.Serialize(g.Receipt with{TransportVerified=false})));
        var old="""{"version":1,"nodePath":"C:\\node.exe","cliPath":"C:\\openclaw.mjs","manifestPath":"C:\\host.json","stateDir":"C:\\state","configPath":"C:\\state\\openclaw.json","expectedOrigins":["chrome-extension://kcdjddhmeafeomebliikmbpblkmkfoig/"]}""";
        Assert.Throws<ContractException>(()=>ManagementContract.ParseBinding(Encoding.UTF8.GetBytes(old)));
        var names=new[]{ManagementContract.ExeName,ManagementContract.BindingName,ManagementContract.ReceiptName,ManagementContract.ManifestName};
        ManagementContract.ValidateFileNames(names);
        Assert.Throws<ContractException>(()=>ManagementContract.ValidateFileNames([..names,"foreign.txt"]));
        Assert.Throws<ContractException>(()=>ManagementContract.ValidateFileNames(names.Select(x=>x.ToLowerInvariant())));
    }
    [Theory][InlineData(null,true)][InlineData("{}",true)][InlineData("{\"NodeBrowserProxyEnabled\":false}",false)]
    [InlineData("{\"NodeBrowserProxyEnabled\":true}",true)][InlineData("{\"NodeBrowserProxyEnabled\":true,\"NodeBrowserProxyEnabled\":false}",false)]
    [InlineData("{\"NodeBrowserProxyEnabled\":\"true\"}",false)][InlineData("[]",false)][InlineData("broken",false)]
    public void ExistingPreferenceAuthority(string? value,bool allowed)=>Assert.Equal(allowed,BrowserControlPreference.Allows(value is null?null:Encoding.UTF8.GetBytes(value)));
    [Fact]public void SettingsSizeIsNotTheManagementWireSize()
    {
        var json="{\"Other\":\""+new string('x',40000)+"\",\"NodeBrowserProxyEnabled\":false}";
        Assert.False(BrowserControlPreference.Allows(Encoding.UTF8.GetBytes(json)));
        Assert.True(BrowserControlPreference.Allows(Encoding.UTF8.GetBytes(json.Replace("false","true"))));
    }
    [Fact]public void CanonicalNativeChildHasNoAmbientOverrides()
    {
        var generation=RegistrationServiceTests.Make(ManagementContractTests.Request());
        var info=NativeTransport.CreateStartInfo(generation,ManagementContract.Origin);
        Assert.Equal(generation.Binding.NativeWindows!.NodePath,info.FileName);
        Assert.False(info.UseShellExecute);Assert.True(info.RedirectStandardInput);
        Assert.Equal(new[]{generation.Binding.NativeWindows.CliPath,"browser","extension","native-host","--manifest",generation.Installation.ManifestPath,
            "--launcher",generation.Installation.LauncherPath,"--expected-origin",ManagementContract.Origin,"--browser-profile","chrome",ManagementContract.Origin},info.ArgumentList);
        Assert.Equal("1",info.Environment["OPENCLAW_NO_RESPAWN"]);
        Assert.Equal(generation.Binding.NativeWindows.StateDir,info.Environment["OPENCLAW_STATE_DIR"]);
        Assert.DoesNotContain(info.Environment.Keys,k=>k.StartsWith("NODE_",StringComparison.OrdinalIgnoreCase));
    }
}
