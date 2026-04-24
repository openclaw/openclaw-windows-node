// Port of D3's force simulation for force-directed graph layouts.
// Implements velocity Verlet integration with configurable forces.

namespace OpenClawTray.Infrastructure.Charting.D3;

/// <summary>
/// A node in a force-directed graph with position and velocity.
/// </summary>
public sealed class ForceNode
{
    public int Index { get; internal set; }
    public double X { get; set; }
    public double Y { get; set; }
    public double Vx { get; set; }
    public double Vy { get; set; }

    // Optional user data
    public object? Data { get; set; }
    public string? Label { get; set; }
    public double Radius { get; set; } = 8;

    // Fixed position (if set, node won't move)
    public double? Fx { get; set; }
    public double? Fy { get; set; }
}

/// <summary>
/// An edge/link in a force-directed graph.
/// </summary>
public record struct ForceLink(int Source, int Target, double Strength = 1, double Distance = 50);

/// <summary>
/// A force-directed graph layout simulation using velocity Verlet integration.
/// Port of d3-force's simulation with charge (many-body), link, and center forces.
/// </summary>
public sealed class ForceSimulation
{
    private readonly List<ForceNode> _nodes = [];
    private readonly List<ForceLink> _links = [];

    private double _alpha = 1;
    private double _alphaMin = 0.001;
    private double _alphaDecay = 0.0228; // 1 - pow(0.001, 1/300)
    private double _alphaTarget = 0;
    private double _velocityDecay = 0.6;

    // Force parameters
    private double _chargeStrength = -30;
    private double _centerX = 0;
    private double _centerY = 0;
    private double _centerStrength = 0.1;
    private double _linkStrength = 1;
    private double _linkDistance = 50;
    private double _collisionRadius = 0;

    public IReadOnlyList<ForceNode> Nodes => _nodes;
    public IReadOnlyList<ForceLink> Links => _links;
    public double Alpha { get => _alpha; set => _alpha = value; }
    public double AlphaTarget { get => _alphaTarget; set => _alphaTarget = value; }
    public double AlphaMin { get => _alphaMin; set => _alphaMin = value; }

    public ForceSimulation SetNodes(IEnumerable<ForceNode> nodes)
    {
        _nodes.Clear();
        int i = 0;
        foreach (var n in nodes)
        {
            n.Index = i++;
            _nodes.Add(n);
        }
        return this;
    }

    public ForceSimulation SetLinks(IEnumerable<ForceLink> links)
    {
        _links.Clear();
        _links.AddRange(links);
        return this;
    }

    public ForceSimulation ChargeStrength(double strength) { _chargeStrength = strength; return this; }
    public ForceSimulation Center(double x, double y) { _centerX = x; _centerY = y; return this; }
    public ForceSimulation CenterStrength(double strength) { _centerStrength = strength; return this; }
    public ForceSimulation LinkDistance(double distance) { _linkDistance = distance; return this; }
    public ForceSimulation LinkStrength(double strength) { _linkStrength = strength; return this; }
    public ForceSimulation VelocityDecay(double decay) { _velocityDecay = decay; return this; }
    public ForceSimulation CollisionRadius(double radius) { _collisionRadius = radius; return this; }
    public ForceSimulation AlphaDecay(double decay) { _alphaDecay = decay; return this; }

    /// <summary>
    /// Initialize node positions in a phyllotaxis pattern (like d3-force).
    /// </summary>
    public ForceSimulation InitializePositions()
    {
        for (int i = 0; i < _nodes.Count; i++)
        {
            var n = _nodes[i];
            if (n.Fx.HasValue) { n.X = n.Fx.Value; n.Y = n.Fy ?? 0; continue; }
            // Phyllotaxis arrangement
            double radius = 10 * Math.Sqrt(0.5 + i);
            double angle = i * (Math.PI * (3 - Math.Sqrt(5)));
            n.X = _centerX + radius * Math.Cos(angle);
            n.Y = _centerY + radius * Math.Sin(angle);
        }
        return this;
    }

    /// <summary>
    /// Run the simulation for the given number of iterations.
    /// </summary>
    public ForceSimulation Run(int iterations = 300)
    {
        for (int i = 0; i < iterations; i++)
        {
            Tick();
            if (_alpha < _alphaMin) break;
        }
        return this;
    }

    /// <summary>
    /// Advance the simulation by one tick.
    /// </summary>
    public void Tick()
    {
        _alpha += (_alphaTarget - _alpha) * _alphaDecay;

        // Apply forces
        ApplyChargeForce();
        ApplyLinkForce();
        ApplyCenterForce();
        if (_collisionRadius > 0) ApplyCollisionForce();

        // Velocity Verlet integration
        for (int i = 0; i < _nodes.Count; i++)
        {
            var node = _nodes[i];
            if (node.Fx.HasValue)
            {
                node.X = node.Fx.Value;
                node.Y = node.Fy ?? node.Y;
                node.Vx = node.Vy = 0;
            }
            else
            {
                node.Vx *= _velocityDecay;
                node.Vy *= _velocityDecay;
                node.X += node.Vx;
                node.Y += node.Vy;
            }
        }
    }

    private void ApplyChargeForce()
    {
        // N-body charge force (simplified: O(n^2) — fine for <1000 nodes)
        double strength = _chargeStrength * _alpha;
        for (int i = 0; i < _nodes.Count; i++)
        {
            for (int j = i + 1; j < _nodes.Count; j++)
            {
                double dx = _nodes[j].X - _nodes[i].X;
                double dy = _nodes[j].Y - _nodes[i].Y;
                double d2 = dx * dx + dy * dy;
                if (d2 == 0) { dx = Jiggle(); dy = Jiggle(); d2 = dx * dx + dy * dy; }
                double d = Math.Sqrt(d2);
                double force = strength / d2;
                double fx = dx / d * force;
                double fy = dy / d * force;
                _nodes[i].Vx -= fx;
                _nodes[i].Vy -= fy;
                _nodes[j].Vx += fx;
                _nodes[j].Vy += fy;
            }
        }
    }

    private void ApplyLinkForce()
    {
        double alpha = _alpha;
        foreach (var link in _links)
        {
            if (link.Source < 0 || link.Source >= _nodes.Count ||
                link.Target < 0 || link.Target >= _nodes.Count) continue;

            var source = _nodes[link.Source];
            var target = _nodes[link.Target];
            double dx = target.X - source.X + Jiggle();
            double dy = target.Y - source.Y + Jiggle();
            double d = Math.Sqrt(dx * dx + dy * dy);
            if (d == 0) d = 1;

            double distance = link.Distance > 0 ? link.Distance : _linkDistance;
            double strength = link.Strength > 0 ? link.Strength : _linkStrength;
            double force = (d - distance) / d * alpha * strength;

            double fx = dx * force * 0.5;
            double fy = dy * force * 0.5;

            target.Vx -= fx;
            target.Vy -= fy;
            source.Vx += fx;
            source.Vy += fy;
        }
    }

    private void ApplyCenterForce()
    {
        if (_nodes.Count == 0) return;
        double sx = 0, sy = 0;
        for (int i = 0; i < _nodes.Count; i++)
        {
            sx += _nodes[i].X;
            sy += _nodes[i].Y;
        }
        sx = (sx / _nodes.Count - _centerX) * _centerStrength;
        sy = (sy / _nodes.Count - _centerY) * _centerStrength;
        for (int i = 0; i < _nodes.Count; i++)
        {
            _nodes[i].X -= sx;
            _nodes[i].Y -= sy;
        }
    }

    private void ApplyCollisionForce()
    {
        for (int i = 0; i < _nodes.Count; i++)
        {
            for (int j = i + 1; j < _nodes.Count; j++)
            {
                double dx = _nodes[j].X - _nodes[i].X;
                double dy = _nodes[j].Y - _nodes[i].Y;
                double d = Math.Sqrt(dx * dx + dy * dy);
                double r = _collisionRadius > 0 ? _collisionRadius : _nodes[i].Radius + _nodes[j].Radius;
                if (d < r && d > 0)
                {
                    double overlap = (r - d) / d * 0.5;
                    _nodes[i].X -= dx * overlap;
                    _nodes[i].Y -= dy * overlap;
                    _nodes[j].X += dx * overlap;
                    _nodes[j].Y += dy * overlap;
                }
            }
        }
    }

    private readonly Random _rng = new(42);
    private double Jiggle() => (_rng.NextDouble() - 0.5) * 1e-6;
}
