using Bikepark.Sim.Terrain;

namespace Bikepark.Sim.Trails;

/// <summary>One stretch of a route: along a way from one distance to another (either direction on access paths).</summary>
public readonly record struct RouteLeg(int WayId, long FromCm, long ToCm)
{
    public long LengthCm => Math.Abs(ToCm - FromCm);
}

/// <summary>
/// Everything derived from <see cref="State.WorldState.Ways"/>: geometries, the junction graph and spatial lookups.
/// Rebuilt whenever the ways change (see <see cref="Simulation.Network"/>); never saved.
/// <para>
/// Graph: nodes are the base (start of the first access path), way endpoints and junctions; edges are the stretches
/// of a way between consecutive nodes. Access paths are two-way, trails one-way (start → end). Validation keeps the
/// invariant that every point of every way is reachable from the base and can get back to it.
/// </para>
/// </summary>
public sealed class WayNetwork
{
    private readonly List<Way> _ways;
    private readonly Dictionary<int, WayGeometry> _geometries;
    private readonly Dictionary<(int WayId, long DistanceCm), int> _nodeIndex = [];
    private readonly List<(int WayId, long DistanceCm)> _nodes = [];
    private readonly List<List<Edge>> _edges = [];
    private readonly CorridorIndex _corridors = new();
    private readonly CorridorIndex _centerlines = new();

    private readonly record struct Edge(int To, int WayId, long FromCm, long ToCm, long Cost);

    public static WayNetwork Empty { get; } = new([], [], new TrailRules());

    private WayNetwork(List<Way> ways, Dictionary<int, WayGeometry> geometries, TrailRules rules)
    {
        _ways = ways;
        _geometries = geometries;
        BaseWay = ways.FirstOrDefault(w => w.Kind == WayKind.AccessPath);
        foreach (var way in ways)
        {
            var geometry = geometries[way.Id];
            _corridors.Add(way.Id, geometry, way.Kind == WayKind.AccessPath ? rules.PathCorridorCm : rules.TrailCorridorCm);
            _centerlines.Add(way.Id, geometry, 0);
        }
        BuildGraph();
        BaseNode = BaseWay is null ? -1 : NodeAt(BaseWay.Id, 0);
    }

    /// <summary>Builds the network for the given ways (in id order) on the terrain.</summary>
    public static WayNetwork Build(IReadOnlyList<Way> ways, TerrainGrid grid, TrailRules rules)
    {
        if (ways.Count == 0) return Empty;
        var ordered = ways.OrderBy(w => w.Id).ToList();
        var geometries = new Dictionary<int, WayGeometry>();
        foreach (var way in ordered)
            geometries[way.Id] = WayGeometry.Build(grid, way.Kind, way.Points, rules.SegmentLengthMeters * 100, rules.PathGradingMeters);
        return new WayNetwork(ordered, geometries, rules);
    }

    /// <summary>All ways in id order.</summary>
    public IReadOnlyList<Way> Ways => _ways;

    public IEnumerable<Way> Trails => _ways.Where(w => w.Kind == WayKind.Trail);

    /// <summary>The first access path; its start is where guests arrive.</summary>
    public Way? BaseWay { get; }

    public bool HasAccessPath => BaseWay is not null;

    public int BaseNode { get; }

    public int NodeCount => _nodes.Count;

    public WayGeometry Geometry(int wayId) => _geometries[wayId];

    public bool TryGetGeometry(int wayId, out WayGeometry geometry) => _geometries.TryGetValue(wayId, out geometry!);

    public Way? FindWay(int wayId) => _ways.FirstOrDefault(w => w.Id == wayId);

    /// <summary>Node at a way position, or -1 if there is no node exactly there.</summary>
    public int NodeAt(int wayId, long distanceCm) =>
        _nodeIndex.TryGetValue((wayId, distanceCm), out int node) ? node : -1;

    public int StartNode(Way way) => NodeAt(way.Id, 0);

    public int EndNode(Way way) => NodeAt(way.Id, Geometry(way.Id).LengthCm);

    /// <summary>A position of the node: the way and distance it was first created at.</summary>
    public (int WayId, long DistanceCm) NodePosition(int node) => _nodes[node];

    /// <summary>True if (x, z) lies inside the cleared corridor of any way.</summary>
    public bool IsInCorridor(int xCm, int zCm) => _corridors.Contains(xCm, zCm);

    /// <summary>Closest point on any way's centerline within the radius.</summary>
    public (int WayId, long DistanceCm, PointCm Point)? Nearest(int xCm, int zCm, int radiusCm) =>
        _centerlines.Nearest(xCm, zCm, radiusCm);

    /// <summary>
    /// Cheapest route between two nodes (cost = distance plus a penalty for climbing), as merged legs.
    /// Returns an empty list if from == to, null if unreachable. Deterministic.
    /// </summary>
    public List<RouteLeg>? Route(int from, int to)
    {
        if (from < 0 || to < 0) return null;
        if (from == to) return [];

        var cost = new long[_nodes.Count];
        var via = new Edge?[_nodes.Count];
        var previous = new int[_nodes.Count];
        Array.Fill(cost, long.MaxValue);
        cost[from] = 0;
        var queue = new PriorityQueue<int, long>();
        queue.Enqueue(from, Key(0, from));

        while (queue.TryDequeue(out int node, out long key))
        {
            if (key >> 20 != cost[node]) continue; // stale entry
            if (node == to) break;
            foreach (var edge in _edges[node])
            {
                long next = cost[node] + edge.Cost;
                if (next >= cost[edge.To]) continue;
                cost[edge.To] = next;
                via[edge.To] = edge;
                previous[edge.To] = node;
                queue.Enqueue(edge.To, Key(next, edge.To));
            }
        }

        if (cost[to] == long.MaxValue) return null;

        var legs = new List<RouteLeg>();
        for (int n = to; n != from; n = previous[n])
        {
            var e = via[n]!.Value;
            legs.Add(new RouteLeg(e.WayId, e.FromCm, e.ToCm));
        }
        legs.Reverse();

        // Merge consecutive legs on the same way in the same direction.
        var merged = new List<RouteLeg>();
        foreach (var leg in legs)
        {
            if (merged.Count > 0 && merged[^1] is var last && last.WayId == leg.WayId && last.ToCm == leg.FromCm
                && Math.Sign(last.ToCm - last.FromCm) == Math.Sign(leg.ToCm - leg.FromCm))
                merged[^1] = last with { ToCm = leg.ToCm };
            else
                merged.Add(leg);
        }
        return merged;

        static long Key(long c, int n) => (c << 20) | (uint)n;
    }

    private void BuildGraph()
    {
        // Stops per way: its ends plus every point where another way joins it.
        var stops = _ways.ToDictionary(w => w.Id, w => new SortedSet<long> { 0, _geometries[w.Id].LengthCm });
        foreach (var way in _ways)
        {
            foreach (var join in new[] { way.StartJoin, way.EndJoin })
                if (join is not null && stops.TryGetValue(join.WayId, out var set))
                    set.Add(Math.Clamp(join.DistanceCm, 0, _geometries[join.WayId].LengthCm));
        }

        // Union way endpoints with the point they join.
        var parent = new Dictionary<(int, long), (int, long)>();
        (int, long) Find((int, long) k)
        {
            while (parent.TryGetValue(k, out var p) && p != k) k = p;
            return k;
        }
        void Union((int, long) a, (int, long) b)
        {
            var ra = Find(a);
            var rb = Find(b);
            if (ra == rb) return;
            // Deterministic: the smaller key becomes the root.
            if (ra.CompareTo(rb) < 0) parent[rb] = ra; else parent[ra] = rb;
        }
        foreach (var way in _ways)
        {
            long length = _geometries[way.Id].LengthCm;
            if (way.StartJoin is { } s && stops.ContainsKey(s.WayId))
                Union((way.Id, 0), (s.WayId, Math.Clamp(s.DistanceCm, 0, _geometries[s.WayId].LengthCm)));
            if (way.EndJoin is { } e && stops.ContainsKey(e.WayId))
                Union((way.Id, length), (e.WayId, Math.Clamp(e.DistanceCm, 0, _geometries[e.WayId].LengthCm)));
        }

        // Nodes in deterministic order (ways by id, stops by distance).
        foreach (var way in _ways)
        {
            foreach (long d in stops[way.Id])
            {
                var root = Find((way.Id, d));
                if (!_nodeIndex.TryGetValue(root, out int node))
                {
                    node = _nodes.Count;
                    _nodes.Add(root);
                    _edges.Add([]);
                    _nodeIndex[root] = node;
                }
                _nodeIndex[(way.Id, d)] = node;
            }
        }

        foreach (var way in _ways)
        {
            var geometry = _geometries[way.Id];
            long[] d = stops[way.Id].ToArray();
            for (int i = 0; i + 1 < d.Length; i++)
            {
                int a = _nodeIndex[(way.Id, d[i])], b = _nodeIndex[(way.Id, d[i + 1])];
                if (a == b) continue;
                _edges[a].Add(new Edge(b, way.Id, d[i], d[i + 1], Cost(geometry, d[i], d[i + 1])));
                if (way.Kind == WayKind.AccessPath)
                    _edges[b].Add(new Edge(a, way.Id, d[i + 1], d[i], Cost(geometry, d[i + 1], d[i])));
            }
        }
    }

    /// <summary>Travel cost: distance plus 10x the height gained (climbing is slow).</summary>
    private static long Cost(WayGeometry geometry, long from, long to)
    {
        long gain = Math.Max(0, geometry.HeightAt(to) - geometry.HeightAt(from));
        return Math.Abs(to - from) + 10 * gain;
    }
}
