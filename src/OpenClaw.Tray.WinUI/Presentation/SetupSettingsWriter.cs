namespace OpenClawTray.Presentation;

/// <summary>Applies only reviewed setup fields through the authoritative store; rejects stale same-field intent.</summary>
internal sealed class SetupSettingsWriter
{
    private readonly ISettingsStore _store;
    private readonly SettingsWriteOrigin _origin;
    private readonly Dictionary<string, bool> _baseline;
    private readonly object _gate = new();

    public SetupSettingsWriter(ISettingsStore store)
    {
        _store = store;
        _origin = store.CreateOrigin();
        _baseline = Values(store.Current);
    }

    public void Apply(IReadOnlyDictionary<string, bool> patch)
    {
        lock (_gate)
        {
            if (patch.Count == 0) return;
            _store.Update(_origin, editor =>
            {
                var current = Values(_store.Current);
                foreach (var (key, value) in patch)
                    if (current[key] != _baseline[key] && current[key] != value)
                        throw new InvalidOperationException($"The setup setting {key} changed while setup was open. Review it again.");
                foreach (var (key, value) in patch) Set(editor, key, value);
            });
            foreach (var (key, value) in patch) _baseline[key] = value;
        }
    }

    private static Dictionary<string, bool> Values(SettingsSnapshot settings) => new()
    {
        ["AutoStart"] = settings.AutoStart, ["EnableNodeMode"] = settings.EnableNodeMode,
        ["EnableMcpServer"] = settings.EnableMcpServer,
        ["NodeSystemRunEnabled"] = settings.NodeSystemRunEnabled,
        ["NodeCanvasEnabled"] = settings.NodeCanvasEnabled, ["NodeScreenEnabled"] = settings.NodeScreenEnabled,
        ["NodeCameraEnabled"] = settings.NodeCameraEnabled, ["NodeLocationEnabled"] = settings.NodeLocationEnabled,
        ["NodeBrowserProxyEnabled"] = settings.NodeBrowserProxyEnabled,
        ["NodeTtsEnabled"] = settings.NodeTtsEnabled, ["NodeSttEnabled"] = settings.NodeSttEnabled,
        ["NodeOllamaInferenceEnabled"] = settings.NodeOllamaInferenceEnabled,
        ["EnableManagedLocalGatewayAutoRepair"] = settings.EnableManagedLocalGatewayAutoRepair,
    };

    private static void Set(ISettingsEditor editor, string key, bool value)
    {
        switch (key)
        {
            case "AutoStart": editor.AutoStart = value; break;
            case "EnableNodeMode": editor.EnableNodeMode = value; break;
            case "EnableMcpServer": editor.EnableMcpServer = value; break;
            case "NodeSystemRunEnabled": editor.NodeSystemRunEnabled = value; break;
            case "NodeCanvasEnabled": editor.NodeCanvasEnabled = value; break;
            case "NodeScreenEnabled": editor.NodeScreenEnabled = value; break;
            case "NodeCameraEnabled": editor.NodeCameraEnabled = value; break;
            case "NodeLocationEnabled": editor.NodeLocationEnabled = value; break;
            case "NodeBrowserProxyEnabled": editor.NodeBrowserProxyEnabled = value; break;
            case "NodeTtsEnabled": editor.NodeTtsEnabled = value; break;
            case "NodeSttEnabled": editor.NodeSttEnabled = value; break;
            case "NodeOllamaInferenceEnabled": editor.NodeOllamaInferenceEnabled = value; break;
            case "EnableManagedLocalGatewayAutoRepair": editor.EnableManagedLocalGatewayAutoRepair = value; break;
            default: throw new ArgumentOutOfRangeException(nameof(key));
        }
    }
}
