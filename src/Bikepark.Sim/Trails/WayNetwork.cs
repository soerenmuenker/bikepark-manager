using Bikepark.Sim.Terrain;

namespace Bikepark.Sim.Trails;

/// <summary>How a route leg is travelled.</summary>
public enum LegKind : byte
{
    /// <summary>Along a way's geometry (<see cref="RouteLeg.WayId"/> is the way).</summary>
    Way = 0,

    /// <summary>Up a lift line (<see cref="RouteLeg.WayId"/> is the lift).</summary>
    Lift = 1,

    /// <summary>On foot between a parking lot and its station (<see cref="RouteLeg.WayId"/> is the parking lot).</summary>
    Walk = 2,
}

/// <summary>
/// One stretch of a route: along a way from one distance to another (either direction on access paths), or along a
/// lift/walk link (<see cref="Kind"/>; then <see cref="WayId"/> is the lift or parking lot and distances run along the link).
/// </summary>
public readonly record struct RouteLeg(int WayId, long FromCm, long ToCm, LegKind Kind = LegKind.Way)
{
    public long LengthCm => Math.Abs(ToCm - FromCm);
}

public enum HubKind : byte
{
    ValleyStation = 0,
    MountainStation = 1,
    Parking = 2,

    /// <summary>A small gravel platform the player built to connect paths and trails.</summary>
    Platform = 3,
}

/// <summary>A network node that is an area, not a point on a way: a station platform, plateau or parking lot.</summary>
public sealed record NetworkHub(int Id, HubKind Kind, int OwnerId, TerrainPad Pad);

/// <summary>A connection between two hubs that is not a way: a lift line (one-way up) or a walk (two-way).</summary>
/// <param name="Towed">A surface lift (T-bar): riders are pulled up a straight track on the ground between its stations,
/// which ways can cross (see <see cref="WayNetwork.TowCrossingsOn"/>).</param>
public sealed record NetworkLink(LegKind Kind, int Id, int FromHubId, int ToHubId, long LengthCm, long Cost, int CorridorCm,
    bool Towed = false, string Name = "");

/// <summary>
/// Everything derived from <see cref="State.WorldState.Ways"/> and the structures: geometries, the junction graph and
/// spatial lookups. Rebuilt whenever ways or terrain edits change (see <see cref="Simulation.Network"/>); never saved.
/// <para>
/// Graph: nodes are way endpoints, junctions and hubs (all way ends on one hub are the same node); edges are the
/// stretches of a way between consecutive nodes plus lift and walk links. Access paths and walks are two-way, trails
/// and lifts one-way. The base, where guests arrive, is the parking lot if there is one, else the valley station,
/// else the start of the first access path.
/// </para>
/// </summary>
public sealed class WayNetwork
{
    private readonly List<Way> _ways;
    private readonly Dictionary<int, WayGeometry> _geometries;
    private readonly List<NetworkHub> _hubs;
    private readonly List<NetworkLink> _links;
    private readonly Dictionary<(int WayId, long DistanceCm), int> _nodeIndex = [];
    private readonly List<(int WayId, long DistanceCm)> _nodes = [];
    private readonly List<List<Edge>> _edges = [];
    private readonly CorridorIndex _corridors = new();
    private readonly CorridorIndex _centerlines = new();
    private readonly List<WayCrossing> _crossings = [];
    private readonly Dictionary<int, List<(long Cm, int OtherWay, long OtherCm)>> _crossingsByWay = [];
    private readonly Dictionary<int, List<(long Cm, int LiftId, long LiftCm)>> _towCrossingsByWay = [];
    private readonly Dictionary<int, List<PlacedFeature>> _features;

    private readonly record struct Edge(int To, LegKind Kind, int Id, long FromCm, long ToCm, long Cost);

    public static WayNetwork Empty { get; } = new([], [], [], [], [], new TrailRules());

    private WayNetwork(
        List<Way> ways, Dictionary<int, WayGeometry> geometries, Dictionary<int, List<PlacedFeature>> features,
        List<NetworkHub> hubs, List<NetworkLink> links, TrailRules rules)
    {
        _ways = ways;
        _geometries = geometries;
        _features = features;
        _hubs = hubs;
        _links = links;
        foreach (var way in ways)
        {
            var geometry = geometries[way.Id];
            if (way.Built) // a planned way's trees are still standing
                _corridors.Add(way.Id, geometry, CorridorWidth(rules, way.Kind));
            _centerlines.Add(way.Id, geometry, 0);
        }
        BuildGraph();
        FindCrossings();
        FindTowCrossings();

        var baseHub = hubs.FirstOrDefault(h => h.Kind == HubKind.Parking) ?? hubs.FirstOrDefault(h => h.Kind == HubKind.ValleyStation);
        BaseHub = baseHub;
        BaseWay = baseHub is null ? ways.FirstOrDefault(w => w.Kind == WayKind.AccessPath && w.Built) : null;
        BaseNode = baseHub is not null ? HubNode(baseHub.Id) : BaseWay is null ? -1 : NodeAt(BaseWay.Id, 0);
    }

    /// <summary>Builds the network for the given ways (in id order), hubs and links on the terrain.</summary>
    public static WayNetwork Build(IReadOnlyList<Way> ways, TerrainGrid grid, TrailRules rules) => Build(ways, [], [], grid, rules);

    /// <summary>Builds the network; trail features are resolved against <paramref name="featureTypes"/> (unknown types are ignored).</summary>
    public static WayNetwork Build(
        IReadOnlyList<Way> ways, IReadOnlyList<NetworkHub> hubs, IReadOnlyList<NetworkLink> links, TerrainGrid grid, TrailRules rules,
        IReadOnlyList<TrailFeatureType>? featureTypes = null)
    {
        if (ways.Count == 0 && hubs.Count == 0) return Empty;
        var ordered = ways.OrderBy(w => w.Id).ToList();
        var geometries = new Dictionary<int, WayGeometry>();
        var features = new Dictionary<int, List<PlacedFeature>>();
        foreach (var way in ordered)
        {
            var placed = way.Kind == WayKind.Trail ? TrailFeatures.Resolve(way, featureTypes ?? []) : [];
            features[way.Id] = placed;
            // Only built features shape the trail (difficulty, rating); planned ones are listed but have no effect.
            var built = placed.Where(f => f.Feature.Built).ToList();
            geometries[way.Id] = WayGeometry.Build(grid, way.Kind, way.Points, rules.SegmentLengthMeters * 100, rules.PathGradingMeters, built);
        }
        return new WayNetwork(ordered, geometries, features, hubs.OrderBy(h => h.Id).ToList(), links.ToList(), rules);
    }

    /// <summary>All ways in id order.</summary>
    public IReadOnlyList<Way> Ways => _ways;

    /// <summary>Built trails in id order (what riders can choose).</summary>
    public IEnumerable<Way> Trails => _ways.Where(w => w.Kind == WayKind.Trail && w.Built);

    /// <summary>Where built ways cross without a junction (by way id pair, then along the first way).</summary>
    public IReadOnlyList<WayCrossing> Crossings => _crossings;

    /// <summary>The crossings on a way, in distance order along it.</summary>
    public IReadOnlyList<(long Cm, int OtherWay, long OtherCm)> CrossingsOn(int wayId) =>
        _crossingsByWay.TryGetValue(wayId, out var list) ? list : [];

    /// <summary>
    /// Where a built way crosses the ground track of a towed lift (T-bar), in distance order along the way:
    /// (distance along the way, the lift, distance along the lift leg).
    /// </summary>
    public IReadOnlyList<(long Cm, int LiftId, long LiftCm)> TowCrossingsOn(int wayId) =>
        _towCrossingsByWay.TryGetValue(wayId, out var list) ? list : [];

    /// <summary>Hubs in id order.</summary>
    public IReadOnlyList<NetworkHub> Hubs => _hubs;

    public IReadOnlyList<NetworkLink> Links => _links;

    /// <summary>True if there are no ways and no hubs.</summary>
    public bool IsEmpty => _ways.Count == 0 && _hubs.Count == 0;

    /// <summary>The first access path, if guests arrive at its start (no parking lot or station yet).</summary>
    public Way? BaseWay { get; }

    /// <summary>The hub guests arrive at (parking lot, else valley station), if any.</summary>
    public NetworkHub? BaseHub { get; }

    public bool HasBase => BaseNode >= 0;

    public int BaseNode { get; }

    public int NodeCount => _nodes.Count;

    public WayGeometry Geometry(int wayId) => _geometries[wayId];

    public bool TryGetGeometry(int wayId, out WayGeometry geometry) => _geometries.TryGetValue(wayId, out geometry!);

    public Way? FindWay(int wayId) => _ways.FirstOrDefault(w => w.Id == wayId);

    /// <summary>
    /// The trail's features as of this build, resolved and in distance order (empty for paths and unknown ways),
    /// planned ones included (<see cref="TrailFeature.Built"/>).
    /// </summary>
    public IReadOnlyList<PlacedFeature> FeaturesOn(int wayId) => _features.TryGetValue(wayId, out var list) ? list : [];

    public NetworkHub? FindHub(int hubId) => _hubs.FirstOrDefault(h => h.Id == hubId);

    public NetworkLink? FindLink(LegKind kind, int id) => _links.FirstOrDefault(l => l.Kind == kind && l.Id == id);

    /// <summary>Node at a way position, or -1 if there is no node exactly there.</summary>
    public int NodeAt(int wayId, long distanceCm) =>
        _nodeIndex.TryGetValue((wayId, distanceCm), out int node) ? node : -1;

    /// <summary>Node of a hub, or -1.</summary>
    public int HubNode(int hubId) => NodeAt(HubKey(hubId), 0);

    /// <summary>
    /// A built trail riders can use: its start can be reached from where guests arrive, and from its end they can get
    /// back there. Trails left with a loose end (after renaturalizing a section) are not, and stay closed until reconnected.
    /// </summary>
    public bool IsConnected(Way way)
    {
        if (!HasBase || !way.Built || !_geometries.ContainsKey(way.Id)) return false;
        _reachable ??= Reach(forward: true);
        _returnable ??= Reach(forward: false);
        int start = StartNode(way), end = EndNode(way);
        return start >= 0 && end >= 0 && _reachable[start] && _returnable[end];
    }

    private bool[]? _reachable, _returnable;

    /// <summary>Riders may ride it: paths always; trails when open (<see cref="Way.IsRideable"/>) and connected.</summary>
    public bool IsRideable(Way way) => way.Kind != WayKind.Trail || way.IsRideable && IsConnected(way);

    /// <summary>Nodes reachable from the base (forward) or from which the base can be reached (backward), over all edges.</summary>
    private bool[] Reach(bool forward)
    {
        var seen = new bool[_edges.Count];
        List<List<int>>? reverse = null;
        if (!forward)
        {
            reverse = _edges.Select(_ => new List<int>()).ToList();
            for (int a = 0; a < _edges.Count; a++)
                foreach (var edge in _edges[a])
                    reverse[edge.To].Add(a);
        }
        var stack = new Stack<int>();
        stack.Push(BaseNode);
        seen[BaseNode] = true;
        while (stack.Count > 0)
        {
            int node = stack.Pop();
            IEnumerable<int> next = forward ? _edges[node].Select(e => e.To) : reverse![node];
            foreach (int n in next)
                if (!seen[n])
                {
                    seen[n] = true;
                    stack.Push(n);
                }
        }
        return seen;
    }

    public int StartNode(Way way) => NodeAt(way.Id, 0);

    public int EndNode(Way way) => NodeAt(way.Id, Geometry(way.Id).LengthCm);

    /// <summary>A position of the node: the way and distance it was first created at (hubs: negative id, 0).</summary>
    public (int WayId, long DistanceCm) NodePosition(int node) => _nodes[node];

    /// <summary>True if (x, z) lies inside the cleared corridor of any built way or lift line.</summary>
    public bool IsInCorridor(int xCm, int zCm)
    {
        if (_corridors.Contains(xCm, zCm)) return true;
        foreach (var link in _links)
        {
            if (link.Kind != LegKind.Lift || link.CorridorCm <= 0) continue;
            var a = FindHub(link.FromHubId)!.Pad;
            var b = FindHub(link.ToHubId)!.Pad;
            if (DistanceToSegment(xCm, zCm, a.CenterX, a.CenterZ, b.CenterX, b.CenterZ) <= link.CorridorCm / 2) return true;
        }
        return false;
    }

    /// <summary>Closest point on any way's centerline within the radius.</summary>
    public (int WayId, long DistanceCm, PointCm Point)? Nearest(int xCm, int zCm, int radiusCm) =>
        _centerlines.Nearest(xCm, zCm, radiusCm);

    /// <summary>Closest point on any trail's centerline within the radius (access paths are skipped).</summary>
    public (int WayId, long DistanceCm, PointCm Point)? NearestTrail(int xCm, int zCm, int radiusCm) =>
        _centerlines.Nearest(xCm, zCm, radiusCm, id => FindWay(id)?.Kind == WayKind.Trail);

    /// <summary>The hub whose flat area is closest to (x, z), within the radius (0 = inside). Ties: lower id.</summary>
    public NetworkHub? HubAt(int xCm, int zCm, int radiusCm)
    {
        NetworkHub? best = null;
        long bestDistance = long.MaxValue;
        foreach (var hub in _hubs)
        {
            long d = hub.Pad.DistanceOutside(xCm, zCm);
            if (d > radiusCm || d >= bestDistance) continue;
            best = hub;
            bestDistance = d;
        }
        return best;
    }

    /// <summary>
    /// Cheapest route between two nodes (cost = distance plus a penalty for climbing; links have their own cost), as
    /// merged legs. Lift links are only used if <paramref name="liftUsable"/> allows them, ways only if
    /// <paramref name="wayUsable"/> does (null: all). Returns an empty list if from == to, null if unreachable. Deterministic.
    /// </summary>
    public List<RouteLeg>? Route(int from, int to, Func<int, bool>? liftUsable = null) => Route(from, to, liftUsable, out _);

    public List<RouteLeg>? Route(int from, int to, Func<int, bool>? liftUsable, out long totalCost, Func<int, bool>? wayUsable = null)
    {
        totalCost = 0;
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
                if (edge.Kind == LegKind.Lift && liftUsable is not null && !liftUsable(edge.Id)) continue;
                if (edge.Kind == LegKind.Way && wayUsable is not null && !wayUsable(edge.Id)) continue;
                long next = cost[node] + edge.Cost;
                if (next >= cost[edge.To]) continue;
                cost[edge.To] = next;
                via[edge.To] = edge;
                previous[edge.To] = node;
                queue.Enqueue(edge.To, Key(next, edge.To));
            }
        }

        if (cost[to] == long.MaxValue) return null;
        totalCost = cost[to];

        var legs = new List<RouteLeg>();
        for (int n = to; n != from; n = previous[n])
        {
            var e = via[n]!.Value;
            legs.Add(new RouteLeg(e.Id, e.FromCm, e.ToCm, e.Kind));
        }
        legs.Reverse();

        // Merge consecutive legs on the same way in the same direction.
        var merged = new List<RouteLeg>();
        foreach (var leg in legs)
        {
            if (merged.Count > 0 && merged[^1] is var last && last.Kind == leg.Kind && last.WayId == leg.WayId && last.ToCm == leg.FromCm
                && Math.Sign(last.ToCm - last.FromCm) == Math.Sign(leg.ToCm - leg.FromCm))
                merged[^1] = last with { ToCm = leg.ToCm };
            else
                merged.Add(leg);
        }
        return merged;

        static long Key(long c, int n) => (c << 20) | (uint)n;
    }

    /// <summary>Union-find key of a hub (way ids are positive, so negative ids never collide).</summary>
    private static int HubKey(int hubId) => -hubId;

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
        var hubIds = _hubs.Select(h => h.Id).ToHashSet();

        // Union way endpoints with the point (or hub) they join.
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
            if (way.StartHubId != 0 && hubIds.Contains(way.StartHubId))
                Union((way.Id, 0), (HubKey(way.StartHubId), 0));
            else if (way.StartJoin is { } s && stops.ContainsKey(s.WayId))
                Union((way.Id, 0), (s.WayId, Math.Clamp(s.DistanceCm, 0, _geometries[s.WayId].LengthCm)));
            if (way.EndHubId != 0 && hubIds.Contains(way.EndHubId))
                Union((way.Id, length), (HubKey(way.EndHubId), 0));
            else if (way.EndJoin is { } e && stops.ContainsKey(e.WayId))
                Union((way.Id, length), (e.WayId, Math.Clamp(e.DistanceCm, 0, _geometries[e.WayId].LengthCm)));
        }

        // Nodes in deterministic order (hubs by id, then ways by id, stops by distance).
        void AddNode((int, long) key)
        {
            var root = Find(key);
            if (!_nodeIndex.TryGetValue(root, out int node))
            {
                node = _nodes.Count;
                _nodes.Add(root);
                _edges.Add([]);
                _nodeIndex[root] = node;
            }
            _nodeIndex[key] = node;
        }
        foreach (var hub in _hubs)
            AddNode((HubKey(hub.Id), 0));
        foreach (var way in _ways)
            foreach (long d in stops[way.Id])
                AddNode((way.Id, d));

        foreach (var way in _ways)
        {
            if (!way.Built) continue; // planned ways have nodes (others can join them) but can't be travelled yet
            var geometry = _geometries[way.Id];
            long[] d = stops[way.Id].ToArray();
            for (int i = 0; i + 1 < d.Length; i++)
            {
                int a = _nodeIndex[(way.Id, d[i])], b = _nodeIndex[(way.Id, d[i + 1])];
                if (a == b) continue;
                _edges[a].Add(new Edge(b, LegKind.Way, way.Id, d[i], d[i + 1], Cost(geometry, d[i], d[i + 1])));
                if (way.Kind == WayKind.AccessPath)
                    _edges[b].Add(new Edge(a, LegKind.Way, way.Id, d[i + 1], d[i], Cost(geometry, d[i + 1], d[i])));
            }
        }

        foreach (var link in _links)
        {
            int a = HubNode(link.FromHubId), b = HubNode(link.ToHubId);
            if (a < 0 || b < 0 || a == b) continue;
            _edges[a].Add(new Edge(b, link.Kind, link.Id, 0, link.LengthCm, link.Cost));
            if (link.Kind == LegKind.Walk)
                _edges[b].Add(new Edge(a, link.Kind, link.Id, link.LengthCm, 0, link.Cost));
        }
    }

    /// <summary>Full width cleared of trees along a way of this kind.</summary>
    public static int CorridorWidth(TrailRules rules, WayKind kind) =>
        kind == WayKind.AccessPath ? rules.PathCorridorCm : rules.TrailCorridorCm;

    /// <summary>Travel cost: distance plus 10x the height gained (climbing is slow).</summary>
    private static long Cost(WayGeometry geometry, long from, long to)
    {
        long gain = Math.Max(0, geometry.HeightAt(to) - geometry.HeightAt(from));
        return Math.Abs(to - from) + 10 * gain;
    }

    private void FindCrossings()
    {
        var built = _ways.Where(w => w.Built).ToList();
        for (int i = 0; i < built.Count; i++)
        for (int j = i + 1; j < built.Count; j++)
            foreach (var (cmA, cmB) in global::Bikepark.Sim.Trails.Crossings.Find(_geometries[built[i].Id], _geometries[built[j].Id]))
                _crossings.Add(new WayCrossing(built[i].Id, cmA, built[j].Id, cmB));
        foreach (var crossing in _crossings)
            foreach (int wayId in new[] { crossing.WayA, crossing.WayB })
            {
                if (!_crossingsByWay.TryGetValue(wayId, out var list))
                    _crossingsByWay[wayId] = list = [];
                list.Add(crossing.From(wayId));
            }
        foreach (var list in _crossingsByWay.Values)
            list.Sort((p, q) => p.Cm.CompareTo(q.Cm));
    }

    private void FindTowCrossings()
    {
        foreach (var link in _links.Where(l => l.Towed))
        {
            var from = FindHub(link.FromHubId)?.Pad;
            var to = FindHub(link.ToHubId)?.Pad;
            if (from is null || to is null) continue;
            foreach (var way in _ways.Where(w => w.Built))
                foreach (var (cm, fractionPpm) in global::Bikepark.Sim.Trails.Crossings.FindOnTrack(_geometries[way.Id], from, to))
                {
                    if (!_towCrossingsByWay.TryGetValue(way.Id, out var list))
                        _towCrossingsByWay[way.Id] = list = [];
                    list.Add((cm, link.Id, link.LengthCm * fractionPpm / 1_000_000));
                }
        }
        foreach (var list in _towCrossingsByWay.Values)
            list.Sort((p, q) => p.Cm.CompareTo(q.Cm));
    }

    private static long DistanceToSegment(long px, long pz, long ax, long az, long bx, long bz)
    {
        long dx = bx - ax, dz = bz - az;
        long len2 = dx * dx + dz * dz;
        long t = len2 == 0 ? 0 : Math.Clamp(((px - ax) * dx + (pz - az) * dz) * 1024 / len2, 0, 1024);
        long cx = ax + dx * t / 1024 - px, cz = az + dz * t / 1024 - pz;
        return FixedMath.ISqrt(cx * cx + cz * cz);
    }
}
