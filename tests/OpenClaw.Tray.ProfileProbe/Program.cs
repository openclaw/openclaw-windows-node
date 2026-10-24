using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.Loader;
using System.Text.Json;

var assemblyPath = Path.GetFullPath(args[0]);
var assemblyDirectory = Path.GetDirectoryName(assemblyPath)!;
AssemblyLoadContext.Default.Resolving += (context, name) =>
{
    var path = Path.Combine(assemblyDirectory, name.Name + ".dll");
    return File.Exists(path) ? context.LoadFromAssemblyPath(path) : null;
};
var assembly = AssemblyLoadContext.Default.LoadFromAssemblyPath(assemblyPath);
RuntimeHelpers.RunModuleConstructor(assembly.ManifestModule.ModuleHandle);

const BindingFlags flags = BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;
var identity = assembly.GetType("OpenClawTray.AppIdentity", throwOnError: true)!;
string Resolve(string method) => (string)identity.GetMethod(method, flags)!.Invoke(null, null)!;
var localData = Resolve("ResolveLocalDataDirectory");
var roamingData = Resolve("ResolveRoamingDataDirectory");
var setupLocal = Resolve("ResolveSetupLocalDataDirectory");
var data = Environment.GetEnvironmentVariable("OPENCLAW_TRAY_DATA_DIR");
// The negative control must fail using strings only, before any file/log owner is initialized.
if (string.IsNullOrWhiteSpace(data) || localData != data || roamingData != data)
{
    Console.Error.WriteLine("Startup isolation did not establish an isolated direct profile.");
    return 1;
}
foreach (var root in new[]
         {
             Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
             Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData)
         })
{
    foreach (var name in new[] { "OpenClawTray", "OpenClawTray-Dev" })
    {
        var realProfile = Path.Combine(root, name);
        foreach (var path in new[] { localData, roamingData, setupLocal })
        {
            if (path.Equals(realProfile, StringComparison.OrdinalIgnoreCase) ||
                path.StartsWith(realProfile + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            {
                Console.Error.WriteLine("Startup isolation resolved a real tray profile.");
                return 1;
            }
        }
    }
}

var metadata = assembly.GetType("OpenClawTray.Chat.ChatMetadataStore", throwOnError: true)!;
string ReadField(string name) => (string)metadata.GetField(name, flags)!.GetValue(null)!;
var settings = assembly.GetType("OpenClawTray.Services.SettingsManager", throwOnError: true)!;
Console.WriteLine(JsonSerializer.Serialize(new
{
    Data = data,
    Roaming = Environment.GetEnvironmentVariable("OPENCLAW_TRAY_APPDATA_DIR"),
    Local = Environment.GetEnvironmentVariable("OPENCLAW_TRAY_LOCALAPPDATA_DIR"),
    Legacy = Environment.GetEnvironmentVariable("OPENCLAW_TRAY_LOCAL_DATA_DIR"),
    IsIsolated = (bool)identity.GetProperty("IsIsolated", flags)!.GetValue(null)!,
    LocalData = localData,
    RoamingData = roamingData,
    SetupLocal = setupLocal,
    SettingsPath = (string)settings.GetProperty("SettingsPath", flags)!.GetValue(null)!,
    LastChatState = ReadField("LastChatStateFilePath"),
    AbortedIds = ReadField("AbortedIdsFilePath"),
    ToolMetadata = (string)metadata.GetProperty("DefaultToolMetaCacheFilePath", flags)!.GetValue(null)!
}));
return 0;
