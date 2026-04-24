// Port of d3-scale/src/ordinal.js — ISC License, Copyright 2010-2023 Mike Bostock

namespace OpenClawTray.Infrastructure.Charting.D3;

/// <summary>
/// A discrete ordinal scale that maps a set of discrete domain values to a range.
/// Direct port of d3.scaleOrdinal().
/// </summary>
public sealed class OrdinalScale<TDomain> where TDomain : notnull
{
    private readonly List<TDomain> _domain = [];
    private readonly Dictionary<TDomain, int> _index = [];
    private double[] _range = [];
    private double _unknown = double.NaN;

    public OrdinalScale() { }

    public OrdinalScale(IEnumerable<TDomain> domain, double[] range)
    {
        foreach (var d in domain) AddToDomain(d);
        _range = [.. range];
    }

    /// <summary>
    /// Maps a domain value to a range value.
    /// If <paramref name="x"/> is not in the domain and <see cref="Unknown"/> is NaN,
    /// <paramref name="x"/> is implicitly added to the domain (matching D3 behavior).
    /// Set <see cref="Unknown"/> to a finite value to disable implicit domain growth.
    /// </summary>
    public double Map(TDomain x)
    {
        if (!_index.TryGetValue(x, out int i))
        {
            if (!double.IsNaN(_unknown)) return _unknown;
            i = _domain.Count;
            AddToDomain(x);
        }
        return _range.Length > 0 ? _range[i % _range.Length] : double.NaN;
    }

    /// <summary>Gets or sets the domain.</summary>
    public TDomain[] Domain
    {
        get => [.. _domain];
        set
        {
            _domain.Clear();
            _index.Clear();
            foreach (var d in value) AddToDomain(d);
        }
    }

    /// <summary>Gets or sets the range.</summary>
    public double[] Range
    {
        get => [.. _range];
        set => _range = [.. value];
    }

    /// <summary>Gets or sets the value returned for unknown domain values.</summary>
    public double Unknown
    {
        get => _unknown;
        set => _unknown = value;
    }

    public OrdinalScale<TDomain> SetDomain(params TDomain[] domain) { Domain = domain; return this; }
    public OrdinalScale<TDomain> SetRange(params double[] range) { Range = range; return this; }
    public OrdinalScale<TDomain> SetUnknown(double unknown) { Unknown = unknown; return this; }

    public OrdinalScale<TDomain> Copy()
    {
        var copy = new OrdinalScale<TDomain>();
        copy._domain.AddRange(_domain);
        foreach (var kv in _index) copy._index[kv.Key] = kv.Value;
        copy._range = [.. _range];
        copy._unknown = _unknown;
        return copy;
    }

    private void AddToDomain(TDomain d)
    {
        if (_index.ContainsKey(d)) return;
        _index[d] = _domain.Count;
        _domain.Add(d);
    }
}

public static class OrdinalScale
{
    public static OrdinalScale<string> Create() => new();
    public static OrdinalScale<TDomain> Create<TDomain>(IEnumerable<TDomain> domain, double[] range) where TDomain : notnull
        => new(domain, range);
}
