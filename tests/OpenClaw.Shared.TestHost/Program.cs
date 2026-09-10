using OpenClaw.Shared;
using System.Diagnostics;
using System.Reflection;
using System.Text;
using System.Text.Json;

if (args is ["--echo-args", .. var echoedArgs])
{
    Console.WriteLine(JsonSerializer.Serialize(echoedArgs));
    return 0;
}

if (args is ["--process-fixture", .. var fixtureArgs])
{
    return await RunProcessFixtureAsync(fixtureArgs);
}

if (args is ["-xjf", var fixtureArchive, "-C", var fixtureDestination, "--strip-components=1"]
    && fixtureArchive == "fixture-hold")
{
    Directory.CreateDirectory(fixtureDestination);
    File.WriteAllText(
        Path.Combine(fixtureDestination, "fixture.pid"),
        Environment.ProcessId.ToString());
    Console.Out.Write("fixture-stdout");
    Console.Error.Write("fixture-stderr");
    Console.Out.Flush();
    Console.Error.Flush();
    await Task.Delay(TimeSpan.FromSeconds(30));
    return 0;
}

if (args.Length != 1)
{
    Console.Error.WriteLine(
        "Usage: OpenClaw.Shared.TestHost <identity-directory> | --echo-args [args...]");
    return 64;
}

try
{
    var identity = new DeviceIdentity(args[0]);
    identity.Initialize();
    Console.WriteLine(identity.DeviceId);
    return 0;
}
catch (DeviceIdentityLoadException ex)
{
    Console.Error.WriteLine(ex.Message);
    return 2;
}

static async Task<int> RunProcessFixtureAsync(string[] args)
{
    if (args is ["success"])
    {
        Console.Out.Write("stdout-first");
        Console.Error.Write("stderr-first");
        await Task.Delay(25);
        Console.Out.Write("-stdout-last");
        Console.Error.Write("-stderr-last");
        return 0;
    }

    if (args is ["late-output", var delayText] && int.TryParse(delayText, out var delayMs))
    {
        await Task.Delay(delayMs);
        Console.Out.Write("late-stdout");
        Console.Error.Write("late-stderr");
        return 0;
    }

    if (args is ["bom-output"])
    {
        await WriteEncodedAsync(Console.OpenStandardOutput(), Encoding.Unicode, "wide-stdout");
        await WriteEncodedAsync(Console.OpenStandardError(), Encoding.BigEndianUnicode, "wide-stderr");
        return 0;
    }

    if (args is ["fragmented-bom-output"])
    {
        var stdout = Console.OpenStandardOutput();
        await stdout.WriteAsync(new byte[] { 0xFF, 0xFE, 0x00 });
        await stdout.FlushAsync();
        await Task.Delay(25);
        await stdout.WriteAsync(new byte[] { 0x00 });
        await stdout.WriteAsync(new UTF32Encoding(false, false).GetBytes("utf32-stdout"));
        await stdout.FlushAsync();

        var stderr = Console.OpenStandardError();
        await stderr.WriteAsync(new byte[] { 0x00, 0x00, 0xFE });
        await stderr.FlushAsync();
        await Task.Delay(25);
        await stderr.WriteAsync(Encoding.ASCII.GetBytes(new string('x', 4_096)));
        await stderr.FlushAsync();
        return 0;
    }

    if (args is ["hold", var holdText] && int.TryParse(holdText, out var holdMs))
    {
        Console.Out.Write("holding-stdout");
        Console.Error.Write("holding-stderr");
        Console.Out.Flush();
        Console.Error.Flush();
        await Task.Delay(holdMs);
        return 0;
    }

    if (args is ["inherit-handles", var childHoldText, var pidFile]
        && int.TryParse(childHoldText, out var childHoldMs))
    {
        using var child = StartSelf("--process-fixture", "hold", childHoldMs.ToString());
        File.WriteAllText(pidFile, child.Id.ToString());
        Console.Out.Write("parent-stdout");
        Console.Error.Write("parent-stderr");
        return 0;
    }

    if (args is ["inherit-handles-identity", var identityChildHoldText, var identityPidFile]
        && int.TryParse(identityChildHoldText, out var identityChildHoldMs))
    {
        using var child = StartSelf("--process-fixture", "hold", identityChildHoldMs.ToString());
        WriteProcessIdentity(identityPidFile, child);
        Console.Out.Write("parent-stdout");
        Console.Error.Write("parent-stderr");
        return 0;
    }

    if (args is ["inherit-handles-sized", var sizedChildHoldText, var sizedPidFile, var outputLengthText]
        && int.TryParse(sizedChildHoldText, out var sizedChildHoldMs)
        && int.TryParse(outputLengthText, out var outputLength))
    {
        using var child = StartSelf("--process-fixture", "hold", sizedChildHoldMs.ToString());
        WriteProcessIdentity(sizedPidFile, child);
        Console.Out.Write(new string('o', outputLength));
        Console.Error.Write(new string('e', outputLength));
        Console.Out.Flush();
        Console.Error.Flush();
        return 0;
    }

    Console.Error.WriteLine("Unknown process fixture.");
    return 64;
}

static async Task WriteEncodedAsync(Stream stream, Encoding encoding, string value)
{
    await stream.WriteAsync(encoding.GetPreamble());
    await stream.WriteAsync(encoding.GetBytes(value));
    await stream.FlushAsync();
}

static void WriteProcessIdentity(string path, Process process)
{
    var identity = $"{process.Id}|{process.StartTime.ToUniversalTime().Ticks}\n";
    var temporaryPath = path + ".tmp";
    File.WriteAllText(temporaryPath, identity);
    File.Move(temporaryPath, path, overwrite: true);
}

static Process StartSelf(params string[] arguments)
{
    var processPath = Environment.ProcessPath
        ?? throw new InvalidOperationException("Current process path is unavailable.");
    var startInfo = new ProcessStartInfo
    {
        FileName = processPath,
        UseShellExecute = false,
        CreateNoWindow = true,
    };

    if (string.Equals(
        Path.GetFileNameWithoutExtension(processPath),
        "dotnet",
        StringComparison.OrdinalIgnoreCase))
    {
        startInfo.ArgumentList.Add(Assembly.GetExecutingAssembly().Location);
    }

    foreach (var argument in arguments)
        startInfo.ArgumentList.Add(argument);

    return Process.Start(startInfo)
        ?? throw new InvalidOperationException("Could not start process fixture child.");
}
