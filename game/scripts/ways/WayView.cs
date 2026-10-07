using Bikepark.Game.Terrain;
using Bikepark.Sim;
using Bikepark.Sim.Crew;
using Bikepark.Sim.Events;
using Bikepark.Sim.State;
using Bikepark.Sim.Terrain;
using Bikepark.Sim.Trails;
using Godot;

namespace Bikepark.Game.Ways;

/// <summary>
/// Draws the way network: gravel paths, dirt trails with a rating stripe and their features, name labels and the base
/// marker. Planned ways are see-through blueprints (the part already dug turns solid), planned features are light blue
/// ghosts that fill in as the crew builds them. Rebuilds when ways, structures, the terrain or job progress change, and
/// tells the terrain which trees and rocks are gone (built corridors, felled trees). Pure view.
/// </summary>
public partial class WayView : Node3D
{
    [Export] public NodePath SimHostPath { get; set; } = "../SimHost";
    [Export] public NodePath TerrainPath { get; set; } = "../TerrainView";

    /// <summary>How often job progress is checked (real seconds); also the shortest gap between tree updates.</summary>
    [Export] public double PollSeconds { get; set; } = 1.0;

    private SimHost _host = null!;
    private TerrainView _terrain = null!;
    private ShaderMaterial _material = null!;
    private ShaderMaterial _blueprintMaterial = null!;
    private ShaderMaterial _featureMaterial = null!;
    private StandardMaterial3D _plannedFeatureMaterial = null!;
    private Node3D? _content;
    private readonly List<IDisposable> _subscriptions = [];
    private bool _dirty = true;
    private double _pollTimer;
    private string _progressSignature = "";

    // Trees: a full refresh when built ways change, otherwise only the chunks around newly felled trees.
    private int _scatterRevision = -1;
    private Rect2? _felledArea;

    public override void _Ready()
    {
        _host = GetNode<SimHost>(SimHostPath);
        _terrain = GetNode<TerrainView>(TerrainPath);
        _material = WayMeshes.CreateMaterial();
        _blueprintMaterial = WayMeshes.CreateBlueprintMaterial();
        _featureMaterial = FeatureMeshes.CreateMaterial();
        _plannedFeatureMaterial = FeatureMeshes.CreatePlannedMaterial();
        _terrain.TerrainBuilt += _ =>
        {
            _dirty = true;
            _scatterRevision = -1;
        };
        _host.SimulationReplaced += OnSimulationReplaced;
        OnSimulationReplaced(_host.Sim);
    }

    public override void _ExitTree()
    {
        _host.SimulationReplaced -= OnSimulationReplaced;
        foreach (var s in _subscriptions) s.Dispose();
    }

    public override void _Process(double delta)
    {
        var sim = _host.Sim;
        _pollTimer -= delta;
        bool poll = _pollTimer <= 0;
        if (poll)
        {
            _pollTimer = PollSeconds;
            string signature = ProgressSignature(sim);
            if (signature != _progressSignature)
            {
                _progressSignature = signature;
                _dirty = true;
            }
        }

        if (_dirty)
        {
            _dirty = false;
            Rebuild(sim);
        }

        if (sim.State.WaysRevision != _scatterRevision)
        {
            _scatterRevision = sim.State.WaysRevision;
            _felledArea = null;
            _terrain.SetScatterFilter(Forest.GoneFilter(sim.Network, sim.State));
        }
        else if (poll && _felledArea is { } area)
        {
            _felledArea = null;
            _terrain.SetScatterFilter(Forest.GoneFilter(sim.Network, sim.State), area.Grow(TerrainScatterMargin));
        }
    }

    /// <summary>A tree's scatter cell can lie up to one cell away from the tree.</summary>
    private const float TerrainScatterMargin = 6f;

    private void OnSimulationReplaced(Simulation sim)
    {
        foreach (var s in _subscriptions) s.Dispose();
        _subscriptions.Clear();
        _subscriptions.Add(sim.Events.Subscribe<WayBuilt>(_ => _dirty = true));
        _subscriptions.Add(sim.Events.Subscribe<WayDeleted>(_ => _dirty = true));
        _subscriptions.Add(sim.Events.Subscribe<TrailFeaturePlaced>(_ => _dirty = true));
        _subscriptions.Add(sim.Events.Subscribe<TrailFeatureRemoved>(_ => _dirty = true));
        _subscriptions.Add(sim.Events.Subscribe<JobCompleted>(_ => _dirty = true));
        _subscriptions.Add(sim.Events.Subscribe<LiftBuilt>(_ => _dirty = true));
        _subscriptions.Add(sim.Events.Subscribe<LiftDeleted>(_ => _dirty = true));
        _subscriptions.Add(sim.Events.Subscribe<ParkingLotBuilt>(_ => _dirty = true));
        _subscriptions.Add(sim.Events.Subscribe<ParkingLotDeleted>(_ => _dirty = true));
        _subscriptions.Add(sim.Events.Subscribe<TreeFelled>(e =>
        {
            var tree = new Rect2(e.Tree.X / 100f, e.Tree.Z / 100f, 0, 0);
            _felledArea = _felledArea is { } area ? area.Merge(tree) : tree;
        }));
        _dirty = true;
        _scatterRevision = -1;
    }

    /// <summary>
    /// Progress of the jobs that build ways and features (2 % steps), trail condition (10 % steps per segment), closures
    /// and ground wetness (25 % steps): redraw when it changes.
    /// </summary>
    private static string ProgressSignature(Simulation sim)
    {
        var state = sim.State;
        var jobs = string.Join(';', state.Jobs.Where(j => j.Kind is JobKind.BuildWay or JobKind.BuildFeature)
            .Select(j => $"{j.Id}:{j.TreesFelled}:{WorkCosts.ProgressPermille(state.CrewRules, j) / 20}"));
        var wear = string.Join(';', state.Ways.Where(w => w.Kind == WayKind.Trail)
            .Select(w => $"{w.Id}{(w.IsRideable ? "" : "x")}:{string.Concat(w.Features.Select(f => (char)('0' + f.Condition / 100_001)))}"));
        return $"{jobs}|{wear}|{state.Weather.WetnessPermille / 250}";
    }

    private static readonly Color Rutted = new Color(0.27f, 0.18f, 0.11f).SrgbToLinear();
    private static readonly Color WornOutDirt = new Color(0.55f, 0.20f, 0.12f).SrgbToLinear();
    private static readonly Color ClosedStripe = new(0.55f, 0.55f, 0.55f);

    /// <summary>Dirt colour of a trail sample: only under a worn feature it gets darker and rutted, reddish at the warning level and below; darker when wet.</summary>
    private static Color DirtAt(WorldState state, Way way, WayGeometry g, int sample)
    {
        var rules = state.WearRules;
        long at = g.Distances[sample];
        var worn = way.Features.FirstOrDefault(f => f.Built && at >= f.DistanceCm
            && TrailFeatures.FindType(state.TrailFeatureTypes, f.TypeId) is { } type && at < f.DistanceCm + type.LengthCm);
        int condition = worn is null ? 1000 : TrailCondition.Permille(worn);
        var color = condition < rules.WarnBelowPermille ? WornOutDirt
            : condition < rules.RoughBelowPermille ? WayMeshes.Dirt.Lerp(Rutted, (rules.RoughBelowPermille - condition) / (float)rules.RoughBelowPermille)
            : WayMeshes.Dirt;
        return color.Darkened(0.35f * state.Weather.WetnessPermille / 1000f);
    }

    private void Rebuild(Simulation sim)
    {
        _content?.QueueFree();
        _content = new Node3D { Name = "Ways" };
        AddChild(_content);

        var network = sim.Network;
        if (network.IsEmpty) return;
        var grid = sim.Terrain;
        var state = sim.State;

        foreach (var way in network.Ways)
        {
            var g = network.Geometry(way.Id);
            if (!way.Built)
            {
                AddPlannedWay(sim, way, g);
                continue;
            }
            if (way.Kind == WayKind.AccessPath)
            {
                AddRibbon(WayMeshes.Ribbon(grid, g, 3.0f, 0.10f, followGround: false, _ => WayMeshes.Gravel), way.Name);
                continue;
            }

            AddRibbon(WayMeshes.Ribbon(grid, g, 1.4f, 0.06f, followGround: true, i => DirtAt(state, way, g, i)), way.Name);
            var stripe = way.IsRideable ? WayMeshes.RatingColor(g.Rating) : ClosedStripe;
            AddRibbon(WayMeshes.Ribbon(grid, g, 0.35f, 0.09f, followGround: true, _ => stripe), way.Name + " stripe");
            string status = way.WornOut ? "\nCLOSED · worn out" : way.Repairing ? "\nCLOSED · crew at work" : way.Closed ? "\nCLOSED" : "";
            AddLabel($"{way.Name}\n{g.Rating} · {g.LengthCm / 100} m{status}", WayMeshes.ToWorld(g.PositionAt(0)) + Vector3.Up * 4f,
                way.IsRideable ? stripe : new Color(0.95f, 0.35f, 0.25f));
            AddFeatures(state, grid, network, way, g);
        }

        Vector3? basePosition = network.BaseHub is { } hub
            ? new Vector3(hub.Pad.CenterX / 100f, hub.Pad.TargetHeightCm / 100f, hub.Pad.CenterZ / 100f)
            : network.BaseWay is { } baseWay ? WayMeshes.ToWorld(network.Geometry(baseWay.Id).PositionAt(0)) : null;
        if (basePosition is { } b)
        {
            var pole = new MeshInstance3D
            {
                Name = "Base",
                Mesh = new CylinderMesh { TopRadius = 0.3f, BottomRadius = 0.3f, Height = 8f },
                MaterialOverride = new StandardMaterial3D { AlbedoColor = new Color(0.95f, 0.55f, 0.1f) },
                Position = b + Vector3.Up * 4f,
            };
            _content.AddChild(pole);
            AddLabel("Base", b + Vector3.Up * 9.5f, new Color(0.95f, 0.55f, 0.1f));
        }
    }

    /// <summary>A planned way: dashed blueprint; while it is dug, the finished part (from the start) is solid.</summary>
    private void AddPlannedWay(Simulation sim, Way way, WayGeometry g)
    {
        var state = sim.State;
        var job = Jobs.ForWay(state, way.Id);
        long doneCm = job is { IsFelling: false, WorkMinutes: > 0 }
            ? g.LengthCm * Math.Min(job.Progress, job.WorkMinutes * 1000) / (job.WorkMinutes * 1000)
            : 0;
        var distances = g.Distances.ToArray();
        bool path = way.Kind == WayKind.AccessPath;
        var solid = path ? WayMeshes.Gravel : WayMeshes.Dirt;
        Color ColorAt(int i) => distances[i] <= doneCm
            ? solid
            : WayMeshes.Blueprint with { A = i / 3 % 2 == 0 ? 0.7f : 0.3f }; // ~3 m dashes
        var mesh = WayMeshes.Ribbon(sim.Terrain, g, path ? 3.0f : 1.4f, path ? 0.10f : 0.06f, followGround: !path, ColorAt);
        if (mesh is not null)
            _content!.AddChild(new MeshInstance3D { Name = way.Name + " (planned)", Mesh = mesh, MaterialOverride = _blueprintMaterial, CastShadow = GeometryInstance3D.ShadowCastingSetting.Off });

        string progress = job is null ? "planned"
            : job.IsFelling ? $"planned · felling {job.TreesFelled}/{job.Trees.Count} trees"
            : $"planned · {WorkCosts.ProgressPermille(state.CrewRules, job) / 10} % built";
        AddLabel($"{way.Name}\n{progress}", WayMeshes.ToWorld(g.PositionAt(0)) + Vector3.Up * 4f, WayMeshes.Blueprint);
        if (way.Kind == WayKind.Trail)
            AddFeatures(state, sim.Terrain, sim.Network, way, g);
    }

    private void AddFeatures(WorldState state, TerrainGrid grid, WayNetwork network, Way way, WayGeometry g)
    {
        foreach (var feature in network.FeaturesOn(way.Id))
        {
            bool built = feature.Feature.Built;
            var job = built ? null : Jobs.ForFeature(state, feature.Feature.Id);
            float progress = job is null ? 0f : WorkCosts.ProgressPermille(state.CrewRules, job) / 1000f;
            _content!.AddChild(new MeshInstance3D
            {
                Name = $"{way.Name} {feature.Type.Name} {feature.Feature.Id}",
                Mesh = FeatureMeshes.Build(grid, g, feature.Type, feature.StartCm,
                    built ? FeatureMeshes.BaseColor(feature.Type.Material) : FeatureMeshes.ProgressColor(feature.Type.Material, progress)),
                MaterialOverride = built ? _featureMaterial : _plannedFeatureMaterial,
                CastShadow = GeometryInstance3D.ShadowCastingSetting.Off, // the depth bias would self-shadow
            });
        }
    }

    private void AddRibbon(ArrayMesh? mesh, string name)
    {
        if (mesh is null) return;
        _content!.AddChild(new MeshInstance3D { Name = name, Mesh = mesh, MaterialOverride = _material, CastShadow = GeometryInstance3D.ShadowCastingSetting.Off });
    }

    private void AddLabel(string text, Vector3 position, Color color)
    {
        _content!.AddChild(new Label3D
        {
            Text = text,
            Position = position,
            Billboard = BaseMaterial3D.BillboardModeEnum.Enabled,
            FontSize = 48,
            PixelSize = 0.03f,
            Modulate = color.Lerp(Colors.White, 0.35f),
            OutlineSize = 12,
            NoDepthTest = true,
            VisibilityRangeEnd = 700f,
        });
    }
}
