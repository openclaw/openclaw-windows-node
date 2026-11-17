using System.Text;

namespace OpenClaw.Shared.Tests.Mxc;

public sealed class MxcNamespaceLaunchTests
{
    [Fact]
    public void FileLaunchKeepsTheOriginalScriptAndArgumentVector()
    {
        var arguments = MxcRuntimeNamespaceNativeTests.BuildLaunchArguments(
            @"C:\Windows\powershell.exe", @"D:\owned fixture\probe.ps1", "unused inline text",
            [@"D:\runs", @"D:\sibling", @"D:\sensitive", @"D:\scratch", @"D:\code"], inlinePayload: false);

        Assert.Equal(new[]
        {
            @"C:\Windows\powershell.exe", "-NoLogo", "-NoProfile", "-NonInteractive",
            "-File", @"D:\owned fixture\probe.ps1",
            @"D:\runs", @"D:\sibling", @"D:\sensitive", @"D:\scratch", @"D:\code",
        }, arguments);
    }

    [Fact]
    public void InlineControlPreservesScriptAndLiteralParametersWithoutPolicyOverride()
    {
        const string script = "param($Path, $Other)\n[Console]::WriteLine('READY')";
        var arguments = MxcRuntimeNamespaceNativeTests.BuildLaunchArguments(
            @"C:\Windows\powershell.exe", @"D:\owned fixture\probe.ps1", script,
            [@"D:\O'Brien\$literal", "semi;$(throw 'not executed')"], inlinePayload: true);

        Assert.Equal(new[]
        {
            @"C:\Windows\powershell.exe", "-NoLogo", "-NoProfile", "-NonInteractive", "-EncodedCommand",
        }, arguments[..^1]);
        var decoded = Encoding.Unicode.GetString(Convert.FromBase64String(arguments[^1]));
        Assert.Equal("& {\nparam($Path, $Other)\n[Console]::WriteLine('READY')\n} " +
            "'D:\\O''Brien\\$literal' 'semi;$(throw ''not executed'')'", decoded);
    }
}
