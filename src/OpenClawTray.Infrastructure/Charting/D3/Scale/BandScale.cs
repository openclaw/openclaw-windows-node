// Port of d3-scale/src/band.js — ISC License, Copyright 2010-2023 Mike Bostock

namespace OpenClawTray.Infrastructure.Charting.D3;

/// <summary>
/// A band scale maps discrete domain values to continuous bands within a range.
/// Direct port of d3.scaleBand().
/// </summary>
public sealed class BandScale<TDomain> where TDomain : notnull
{
    private readonly List<TDomain> _domain = [];
    private readonly Dictionary<TDomain, int> _index = [];
    private double _r0 = 0, _r1 = 1;
    private double _paddingInner = 0;
    private double _paddingOuter = 0;
    private double _align = 0.5;
    private int _round = 0; // 0 = false

    // Cached values
    private double _step;
    private double _bandwidth;
    private double _start;

    public BandScale() { Rescale(); }

    /// <summary>Maps a domain value to the start of its band.</summary>
    public double Map(TDomain x)
    {
        if (!_index.TryGetValue(x, out int i)) return double.NaN;
        double val = _start + _step * i;
        return _round != 0 ? Math.Round(val) : val;
    }

    /// <summary>Gets the bandwidth (width of each band).</summary>
    public double Bandwidth => _bandwidth;

    /// <summary>Gets the step (distance between band starts).</summary>
    public double Step => _step;

    /// <summary>Gets or sets the domain.</summary>
    public TDomain[] Domain
    {
        get => [.. _domain];
        set
        {
            _domain.Clear();
            _index.Clear();
            foreach (var d in value)
            {
                if (_index.ContainsKey(d)) continue;
                _index[d] = _domain.Count;
                _domain.Add(d);
            }
            Rescale();
        }
    }

    /// <summary>Gets or sets the range [start, stop].</summary>
    public double[] Range
    {
        get => [_r0, _r1];
        set { _r0 = value[0]; _r1 = value[1]; Rescale(); }
    }

    /// <summary>Gets or sets the inner padding [0, 1].</summary>
    public double PaddingInner
    {
        get => _paddingInner;
        set { _paddingInner = Math.Clamp(value, 0, 1); Rescale(); }
    }

    /// <summary>Gets or sets the outer padding [0, 1].</summary>
    public double PaddingOuter
    {
        get => _paddingOuter;
        set { _paddingOuter = value; Rescale(); }
    }

    /// <summary>Sets both inner and outer padding to the same value.</summary>
    public double Padding
    {
        set { _paddingInner = Math.Clamp(value, 0, 1); _paddingOuter = value; Rescale(); }
    }

    /// <summary>Gets or sets the alignment [0, 1].</summary>
    public double Align
    {
        get => _align;
        set { _align = Math.Clamp(value, 0, 1); Rescale(); }
    }

    public BandScale<TDomain> SetDomain(params TDomain[] domain) { Domain = domain; return this; }
    public BandScale<TDomain> SetRange(params double[] range) { Range = range; return this; }
    public BandScale<TDomain> SetPaddingInner(double p) { PaddingInner = p; return this; }
    public BandScale<TDomain> SetPaddingOuter(double p) { PaddingOuter = p; return this; }
    public BandScale<TDomain> SetPadding(double p) { Padding = p; return this; }
    public BandScale<TDomain> SetAlign(double a) { Align = a; return this; }
    public BandScale<TDomain> SetRound(bool round) { _round = round ? 1 : 0; Rescale(); return this; }

    public BandScale<TDomain> Copy()
    {
        var copy = new BandScale<TDomain>();
        copy._domain.AddRange(_domain);
        foreach (var kv in _index) copy._index[kv.Key] = kv.Value;
        copy._r0 = _r0;
        copy._r1 = _r1;
        copy._paddingInner = _paddingInner;
        copy._paddingOuter = _paddingOuter;
        copy._align = _align;
        copy._round = _round;
        copy.Rescale();
        return copy;
    }

    private void Rescale()
    {
        int n = _domain.Count;
        bool reverse = _r1 < _r0;
        double r0 = reverse ? _r1 : _r0;
        double r1 = reverse ? _r0 : _r1;

        _step = n > 0 ? (r1 - r0) / Math.Max(1, n - _paddingInner + _paddingOuter * 2) : 0;
        if (_round != 0) _step = Math.Floor(_step);

        double offset = (r1 - r0 - _step * (n - _paddingInner)) * _align;
        _start = r0 + offset;
        if (_round != 0) _start = Math.Round(_start);

        _bandwidth = _step * (1 - _paddingInner);
        if (_round != 0) _bandwidth = Math.Round(_bandwidth);

        if (reverse)
        {
            // Reverse domain order: mirror index mapping so domain[0] maps to the
            // position that domain[n-1] would normally occupy, matching D3's behavior
            // of reversing the values array.
            for (int i = 0; i < _domain.Count; i++)
                _index[_domain[i]] = n - 1 - i;
        }
    }
}

/// <summary>
/// A point scale is a band scale with zero bandwidth.
/// Direct port of d3.scalePoint().
/// </summary>
public sealed class PointScale<TDomain> where TDomain : notnull
{
    private readonly BandScale<TDomain> _band = new();

    public PointScale()
    {
        _band.PaddingInner = 1;
    }

    /// <summary>Maps a domain value to a point position.</summary>
    public double Map(TDomain x) => _band.Map(x);

    /// <summary>Gets the step between points.</summary>
    public double Step => _band.Step;

    public TDomain[] Domain { get => _band.Domain; set => _band.Domain = value; }
    public double[] Range { get => _band.Range; set => _band.Range = value; }
    public double Padding { get => _band.PaddingOuter; set => _band.PaddingOuter = value; }
    public double Align { get => _band.Align; set => _band.Align = value; }

    public PointScale<TDomain> SetDomain(params TDomain[] domain) { Domain = domain; return this; }
    public PointScale<TDomain> SetRange(params double[] range) { Range = range; return this; }
    public PointScale<TDomain> SetPadding(double p) { Padding = p; return this; }
    public PointScale<TDomain> SetAlign(double a) { Align = a; return this; }

    public PointScale<TDomain> Copy()
    {
        var copy = new PointScale<TDomain>();
        copy.Domain = Domain;
        copy.Range = Range;
        copy.Padding = Padding;
        copy.Align = Align;
        return copy;
    }
}

public static class BandScale
{
    public static BandScale<string> Create() => new();
    public static BandScale<TDomain> Create<TDomain>(params TDomain[] domain) where TDomain : notnull
        => new BandScale<TDomain>().SetDomain(domain);
}

public static class PointScale
{
    public static PointScale<string> Create() => new();
    public static PointScale<TDomain> Create<TDomain>(params TDomain[] domain) where TDomain : notnull
        => new PointScale<TDomain>().SetDomain(domain);
}
