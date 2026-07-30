using OpenClaw.Shared;

if (args.Length != 1)
{
    Console.Error.WriteLine("Usage: OpenClaw.Shared.TestHost <identity-directory>");
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
