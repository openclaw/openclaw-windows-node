using OpenClaw.Shared;
using System.Text.Json;

if (args is ["--echo-args", .. var echoedArgs])
{
    Console.WriteLine(JsonSerializer.Serialize(echoedArgs));
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
