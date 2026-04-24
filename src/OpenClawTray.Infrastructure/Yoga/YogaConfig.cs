// C# port of Meta's Yoga layout engine Config.
// Ported from yoga/config/Config.h, yoga/config/Config.cpp

using System.Diagnostics;
using OpenClawTray.Infrastructure.Layout;

namespace OpenClawTray.Infrastructure.Layout;

/// <summary>
/// Configuration for the Yoga layout engine. Controls experimental features,
/// errata modes, point scale factor, and web defaults.
/// </summary>
internal sealed class YogaConfig
{
    private bool _useWebDefaults;
    private YogaErrata _errata = YogaErrata.None;
    private float _pointScaleFactor = 1.0f;
    private uint _version;
    private readonly bool[] _experimentalFeatures = new bool[2]; // ExperimentalFeature count
    private bool _frozen;

    private static readonly YogaConfig s_default = new() { _frozen = true };

    public static YogaConfig Default => s_default;

    /// <summary>
    /// Marks this config as frozen. Mutating a frozen config triggers a Debug.Assert failure.
    /// </summary>
    public void Freeze() => _frozen = true;

    public bool UseWebDefaults
    {
        get => _useWebDefaults;
        set
        {
            Debug.Assert(!_frozen, "Cannot mutate a frozen YogaConfig (e.g. YogaConfig.Default).");
            _useWebDefaults = value;
        }
    }

    public float PointScaleFactor
    {
        get => _pointScaleFactor;
        set
        {
            Debug.Assert(!_frozen, "Cannot mutate a frozen YogaConfig (e.g. YogaConfig.Default).");
            if (_pointScaleFactor != value)
            {
                _pointScaleFactor = value;
                _version++;
            }
        }
    }

    public uint Version => _version;

    public void SetExperimentalFeatureEnabled(YogaExperimentalFeature feature, bool enabled)
    {
        Debug.Assert(!_frozen, "Cannot mutate a frozen YogaConfig (e.g. YogaConfig.Default).");
        int idx = (int)feature;
        if (_experimentalFeatures[idx] != enabled)
        {
            _experimentalFeatures[idx] = enabled;
            _version++;
        }
    }

    public bool IsExperimentalFeatureEnabled(YogaExperimentalFeature feature)
    {
        return _experimentalFeatures[(int)feature];
    }

    public void SetErrata(YogaErrata errata)
    {
        Debug.Assert(!_frozen, "Cannot mutate a frozen YogaConfig (e.g. YogaConfig.Default).");
        if (_errata != errata)
        {
            _errata = errata;
            _version++;
        }
    }

    public void AddErrata(YogaErrata errata)
    {
        Debug.Assert(!_frozen, "Cannot mutate a frozen YogaConfig (e.g. YogaConfig.Default).");
        if (!HasErrata(errata))
        {
            _errata |= errata;
            _version++;
        }
    }

    public void RemoveErrata(YogaErrata errata)
    {
        Debug.Assert(!_frozen, "Cannot mutate a frozen YogaConfig (e.g. YogaConfig.Default).");
        if (HasErrata(errata))
        {
            _errata &= ~errata;
            _version++;
        }
    }

    public YogaErrata Errata => _errata;

    public bool HasErrata(YogaErrata errata) => (_errata & errata) != YogaErrata.None;

    /// <summary>
    /// Whether changing from oldConfig to newConfig would invalidate cached layouts.
    /// </summary>
    public static bool ConfigUpdateInvalidatesLayout(YogaConfig oldConfig, YogaConfig newConfig)
    {
        return oldConfig._errata != newConfig._errata ||
               oldConfig._pointScaleFactor != newConfig._pointScaleFactor ||
               oldConfig._useWebDefaults != newConfig._useWebDefaults ||
               !ExperimentalFeaturesEqual(oldConfig, newConfig);
    }

    private static bool ExperimentalFeaturesEqual(YogaConfig a, YogaConfig b)
    {
        for (int i = 0; i < a._experimentalFeatures.Length; i++)
        {
            if (a._experimentalFeatures[i] != b._experimentalFeatures[i])
                return false;
        }
        return true;
    }
}
