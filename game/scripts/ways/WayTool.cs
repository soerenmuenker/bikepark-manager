using Bikepark.Game.Camera;
using Bikepark.Game.Terrain;
using Bikepark.Sim;
using Bikepark.Sim.Commands;
using Bikepark.Sim.Crew;
using Bikepark.Sim.Terrain;
using Bikepark.Sim.Trails;
using Godot;

namespace Bikepark.Game.Ways;

/// <summary>A wooden feature the player can pick for a rock on the trail being drawn.</summary>
public readonly record struct WayToolRockOption(TrailFeatureType Type, long LengthCm, string? Reason, Action Pick);

/// <summary>
/// Drawing tool for access paths (P) and trails (T). Left-click places a point; holding the button and dragging
/// places points continuously. A live preview is validated every frame with the Sim's <see cref="WayPlanner"/>
/// (red = invalid section). Backspace removes the last point, Enter plans it for the crew (enqueues a <see cref="BuildWayCommand"/>),
/// Esc cancels. Camera controls stay as they are (right-drag orbit, middle-drag pan).
/// </summary>
public partial class WayTool : Node3D
{
    public enum ToolMode
    {
        None,
        AccessPath,
        Trail,
        TarmacRoad,
    }

    [Export] public NodePath SimHostPath { get; set; } = "../SimHost";
    [Export] public NodePath TerrainPath { get; set; } = "../TerrainView";
    [Export] public NodePath CameraPath { get; set; } = "../RtsCamera";

    /// <summary>Minimum distance between points placed while dragging.</summary>
    [Export] public float DragSpacingMeters { get; set; } = 8f;

    private SimHost _host = null!;
    private TerrainView _terrain = null!;
    private RtsCamera _camera = null!;
    private ShaderMaterial _material = null!;
    private MeshInstance3D _preview = null!;
    private MultiMeshInstance3D _markers = null!;
    private readonly List<PointCm> _points = [];
    private PointCm? _cursor;
    private bool _dragging;

    public ToolMode Mode { get; private set; }

    /// <summary>Plan of the placed points plus the cursor (what Enter would build, if the cursor were clicked).</summary>
    public WayPlan? Plan { get; private set; }

    public int PointCount => _points.Count;

    /// <summary>Crew work the planned way would take (felling its corridor, then digging); null without a valid plan.</summary>
    public WorkEstimate? Estimate { get; private set; }

    /// <summary>Last action result, for the HUD ("Built Trail 2", "Too steep ...").</summary>
    public string Status { get; private set; } = "";

    public override void _Ready()
    {
        _host = GetNode<SimHost>(SimHostPath);
        _terrain = GetNode<TerrainView>(TerrainPath);
        _camera = GetNode<RtsCamera>(CameraPath);
        _material = WayMeshes.CreateMaterial(0.0025f);
        _preview = new MeshInstance3D { Name = "Preview", MaterialOverride = _material, CastShadow = GeometryInstance3D.ShadowCastingSetting.Off };
        AddChild(_preview);
        _markers = new MultiMeshInstance3D
        {
            Name = "Markers",
            Multimesh = new MultiMesh
            {
                TransformFormat = MultiMesh.TransformFormatEnum.Transform3D,
                UseColors = true,
                Mesh = new SphereMesh { Radius = 0.6f, Height = 1.2f, RadialSegments = 8, Rings = 4 },
            },
            MaterialOverride = new StandardMaterial3D { VertexColorUseAsAlbedo = true, ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded, NoDepthTest = true },
        };
        AddChild(_markers);
    }

    public void SetMode(ToolMode mode)
    {
        Mode = mode;
        _points.Clear();
        _dragging = false;
        EndRock();
        Plan = null;
        Status = mode == ToolMode.None ? "" : $"Drawing a {(mode == ToolMode.AccessPath ? "gravel access path" : mode == ToolMode.TarmacRoad ? "tarmac road" : "trail")}";
    }

    public override void _UnhandledInput(InputEvent @event)
    {
        if (_rockPlan is not null && @event is InputEventKey { Pressed: true, Echo: false, PhysicalKeycode: Key.Backspace or Key.Enter or Key.KpEnter })
        {
            GetViewport().SetInputAsHandled();
            return;
        }
        if (_dropType is not null && @event is InputEventMouseButton { ButtonIndex: MouseButton.Left, Pressed: true })
        {
            ClickDrop();
            GetViewport().SetInputAsHandled();
            return;
        }
        if (@event is InputEventKey { Pressed: true, Echo: false } key)
        {
            switch (key.PhysicalKeycode)
            {
                case Key.L or Key.K when Mode != ToolMode.None:
                    SetMode(ToolMode.None);
                    return; // let the structure tool take it
                case Key.P:
                    SetMode(Mode == ToolMode.AccessPath ? ToolMode.None : ToolMode.AccessPath);
                    break;
                case Key.T:
                    SetMode(Mode == ToolMode.Trail ? ToolMode.None : ToolMode.Trail);
                    break;
                case Key.Escape when _rockPlan is not null:
                    EndRock();
                    Status = "Keep drawing, then Enter.";
                    break;
                case Key.Escape when Mode != ToolMode.None:
                    if (_points.Count > 0) _points.Clear(); else SetMode(ToolMode.None);
                    break;
                case Key.Backspace when _points.Count > 0:
                    _points.RemoveAt(_points.Count - 1);
                    break;
                case Key.Enter or Key.KpEnter when Mode != ToolMode.None:
                    Build();
                    break;
                default:
                    return;
            }
            GetViewport().SetInputAsHandled();
            return;
        }

        if (Mode == ToolMode.None) return;
        if (@event is InputEventMouseButton { ButtonIndex: MouseButton.Left } button)
        {
            _dragging = button.Pressed;
            if (button.Pressed && CursorPoint() is { } p)
                _points.Add(p);
            GetViewport().SetInputAsHandled();
        }
    }

    public override void _Process(double delta)
    {
        var grid = _terrain.Grid;
        if (Mode == ToolMode.None || grid is null)
        {
            _preview.Visible = false;
            _markers.Multimesh.VisibleInstanceCount = 0;
            return;
        }

        _cursor = CursorPoint();
        if (_dragging && _cursor is { } c && _points.Count > 0)
        {
            long dx = c.X - _points[^1].X, dz = c.Z - _points[^1].Z;
            float spacing = DragSpacingMeters * 100;
            if (dx * dx + dz * dz >= spacing * spacing) _points.Add(c);
        }

        var candidate = _points.ToList();
        if (_rockPlan is null && !_dragging && _cursor is { } cursor && candidate.Count > 0 && candidate[^1] != cursor)
            candidate.Add(cursor);
        var sim = _host.Sim;
        Plan = candidate.Count >= 2
            ? WayPlanner.Plan(grid, sim.Network, sim.State.TrailRules, Kind, candidate, Bikepark.Sim.Land.LandMath.OwnedPredicate(sim.State), rockChoice: RockChoice())
            : null;
        UpdateDropGhost(grid);
        UpdateEstimate(sim, grid, candidate);

        UpdatePreview(grid);
        UpdateMarkers(grid, candidate);
    }

    private WayKind Kind => Mode == ToolMode.Trail ? WayKind.Trail : WayKind.AccessPath;

    private string _estimateKey = "";

    /// <summary>Recomputes the work estimate when the plan's points (or the world) changed.</summary>
    private void UpdateEstimate(Simulation sim, TerrainGrid grid, List<PointCm> candidate)
    {
        if (Plan is not { IsValid: true, Geometry: { } g } plan)
        {
            Estimate = null;
            _estimateKey = "";
            return;
        }
        var state = sim.State;
        string key = $"{Kind}:{state.WaysRevision}:{state.Jobs.Count}:{state.FelledTrees.Count}:{string.Join(';', plan.Points)}";
        if (key == _estimateKey) return;
        _estimateKey = key;
        var trees = Forest.TreesAlong(grid, sim.Network, Forest.Taken(state), g, WayNetwork.CorridorWidth(state.TrailRules, Kind));
        Estimate = WorkCosts.Way(state.CrewRules, state.TrailRules, Kind, g, trees.Count);
    }

    private void Build()
    {
        var grid = _terrain.Grid;
        if (grid is null || _points.Count < 2)
        {
            Status = "Place at least two points.";
            return;
        }
        var sim = _host.Sim;
        var plan = WayPlanner.Plan(grid, sim.Network, sim.State.TrailRules, Kind, _points, Bikepark.Sim.Land.LandMath.OwnedPredicate(sim.State), rockChoice: RockChoice());
        if (!plan.IsValid)
        {
            Status = plan.FirstError ?? "Not valid.";
            return;
        }
        if (plan.RockCrossings.Count > 0)
        {
            _rockPlan = plan;
            _placements.Clear();
            NextCrossing(0);
            return;
        }
        Finish(plan, null);
    }

    // ---- Rock on the line: a wooden feature for each crossing, one at a time, then the trail is ordered.

    private WayPlan? _rockPlan;
    private readonly List<RockFeaturePlacement> _placements = [];
    private int _dropIndex;
    private RockCrossing? _dropCrossing;
    private TrailFeatureType? _dropType;
    private long? _dropLip;
    private MeshInstance3D? _dropGhost;

    /// <summary>Asks which wooden feature crosses a rock (set by the HUD): title, options, keep drawing.</summary>
    public Action<string, IReadOnlyList<WayToolRockOption>, Action>? AskRock { get; set; }

    private RockFeatureChoice RockChoice()
    {
        var state = _host.Sim.State;
        if (_host.InstantBuild) return new RockFeatureChoice(state.TrailFeatureTypes, _ => true);
        int level = Bikepark.Sim.Reputation.ParkProgress.CurrentLevel(state, _host.Sim.Network);
        return new RockFeatureChoice(state.TrailFeatureTypes, t => TrailFeatures.CannotPlace(state, level, t) is null);
    }

    private void EndRock()
    {
        ShowRock(null);
        _rockPlan = null;
        _dropCrossing = null;
        _dropType = null;
        _dropLip = null;
        _placements.Clear();
        if (_dropGhost is not null) _dropGhost.Visible = false;
    }

    private void NextCrossing(int index)
    {
        var plan = _rockPlan!;
        if (index >= plan.RockCrossings.Count)
        {
            var placements = _placements.ToList();
            EndRock();
            Finish(plan, placements);
            return;
        }
        var crossing = plan.RockCrossings[index];
        ShowRock(crossing);
        var options = RockFeatures.Options(_terrain.Grid!, plan.Geometry!, _host.Sim.State.TrailRules, RockChoice(), crossing)
            .Select(o => new WayToolRockOption(o.Type, o.Placement.LengthCm, o.Reason, () =>
            {
                if (RockFeatures.IsFlatDrop(o.Type))
                {
                    _dropIndex = index;
                    _dropCrossing = crossing;
                    _dropType = o.Type;
                    _dropLip = null;
                    Status = "Flat drop: click on the red rock where the deck ends (its lip). Esc goes back to drawing.";
                    return;
                }
                _placements.Add(o.Placement);
                NextCrossing(index + 1);
            })).ToList();
        string count = plan.RockCrossings.Count > 1 ? $"Rock {index + 1} of {plan.RockCrossings.Count}, in red, at" : "The rock in red, at";
        string title = $"{count} {crossing.FromCm / 100} m: {crossing.SpanCm / 100} m wide, {crossing.DropCm / 100} m down. How does the trail cross it?";
        if (AskRock is { } ask) ask(title, options, EndRock);
        else EndRock();
    }

    /// <summary>The spot on the drawn trail closest to the cursor.</summary>
    private long? DistanceAtCursor()
    {
        if (_rockPlan?.Geometry is not { } g || _cursor is not { } c) return null;
        long best = long.MaxValue, at = 0;
        for (int i = 0; i < g.SampleCount; i++)
        {
            long dx = g.Xs[i] - c.X, dz = g.Zs[i] - c.Z, d = dx * dx + dz * dz;
            if (d < best)
            {
                best = d;
                at = g.Distances[i];
            }
        }
        return at;
    }

    private RockFeaturePlacement DropAt(long lip, long landing) =>
        RockFeatures.FlatDropAt(_terrain.Grid!, _rockPlan!.Geometry!, _dropType!, _dropCrossing!, lip, landing);

    private void ClickDrop()
    {
        if (DistanceAtCursor() is not { } at || _dropCrossing is not { } crossing) return;
        if (_dropLip is not { } lip)
        {
            _dropLip = Math.Clamp(at, crossing.FromCm, crossing.ToCm);
            Status = "Flat drop: now click where the landing starts, below the rock.";
            return;
        }
        var placement = DropAt(lip, at);
        if (RockFeatures.CannotPlace(_terrain.Grid!, _rockPlan!.Geometry!, _host.Sim.State.TrailRules, _dropType!, placement) is { } reason)
        {
            Status = reason;
            return;
        }
        _placements.Add(placement);
        _dropCrossing = null;
        _dropType = null;
        _dropLip = null;
        if (_dropGhost is not null) _dropGhost.Visible = false;
        NextCrossing(_dropIndex + 1);
    }

    /// <summary>The flat drop being placed: the deck to the cursor, then deck and landing, green if it fits.</summary>
    private void UpdateDropGhost(Bikepark.Sim.Terrain.TerrainGrid grid)
    {
        if (_dropType is not { } type || _rockPlan?.Geometry is not { } g || DistanceAtCursor() is not { } at)
        {
            if (_dropGhost is not null) _dropGhost.Visible = false;
            return;
        }
        _dropGhost ??= AddGhost();
        var crossing = _dropCrossing!;
        var placement = _dropLip is { } lip ? DropAt(lip, at) : DropAt(at, crossing.ToCm + 100);
        string? why = RockFeatures.CannotPlace(grid, g, _host.Sim.State.TrailRules, type, placement);
        if (placement.StartCm < 0 || placement.LengthCm <= 0 || placement.StartCm + placement.LengthCm > g.LengthCm)
        {
            _dropGhost.Visible = false;
            return;
        }
        if (_dropLip is not null && why is not null) Status = why;
        _dropGhost.Mesh = FeatureMeshes.Build(grid, g, type, placement.StartCm, why is null ? new Color(0.3f, 1f, 0.45f, 0.6f) : new Color(1f, 0.3f, 0.25f, 0.6f), placement.LengthCm, placement.RampCm);
        _dropGhost.Visible = true;
    }

    private static readonly Color RockRed = new(1f, 0.08f, 0.05f, 0.62f);
    private Node3D? _rockMarker;

    /// <summary>The rock being asked about: its boulders and its stretch of trail in red, in the middle of the view. Null clears it.</summary>
    private void ShowRock(RockCrossing? crossing)
    {
        _rockMarker?.QueueFree();
        _rockMarker = null;
        if (crossing is null || _rockPlan?.Geometry is not { } g || _terrain.Grid is not { } grid) return;
        var material = FeatureMeshes.CreateGhostMaterial();
        material.AlbedoColor = RockRed;
        material.VertexColorUseAsAlbedo = false;
        _rockMarker = _terrain.BuildRockHighlight(RockFeatures.RocksOn(grid, g, crossing.FromCm, crossing.ToCm), material);
        _rockMarker.AddChild(new MeshInstance3D
        {
            Mesh = FeatureMeshes.BuildLanding(grid, g, crossing.FromCm, crossing.ToCm, RockRed),
            MaterialOverride = FeatureMeshes.CreateGhostMaterial(),
            CastShadow = GeometryInstance3D.ShadowCastingSetting.Off,
        });
        AddChild(_rockMarker);
        var middle = g.PositionAt((crossing.FromCm + crossing.ToCm) / 2);
        _camera.FocusOn(new Vector2(middle.X / 100f, middle.Z / 100f));
    }

    private MeshInstance3D AddGhost()
    {
        var ghost = new MeshInstance3D { Name = "DropGhost", MaterialOverride = FeatureMeshes.CreateGhostMaterial(), CastShadow = GeometryInstance3D.ShadowCastingSetting.Off };
        AddChild(ghost);
        return ghost;
    }

    private void Finish(WayPlan plan, List<RockFeaturePlacement>? rockFeatures)
    {
        var sim = _host.Sim;
        var points = _points.ToList();
        var kind = Kind;
        var surface = Mode == ToolMode.TarmacRoad ? WaySurface.Tarmac : WaySurface.Gravel;
        string what = $"{plan.LengthCm / 100} m{(kind == WayKind.Trail ? $", {plan.Geometry!.Rating}" : "")}";
        _points.Clear();
        void Order(string name)
        {
            _host.Enqueue(new BuildWayCommand(kind, name, points, Instant: _host.InstantBuild, Surface: surface, RockFeatures: rockFeatures));
            Status = _host.InstantBuild
                ? $"Built ({what}). Draw the next one or press Esc."
                : $"Planned ({what}): the crew will build it (Crew menu). Draw the next one or press Esc.";
        }
        // A finished trail gets a name, unless it continues a trail (then it becomes part of that one); paths need none.
        if (kind == WayKind.Trail && !WayEditing.ContinuesTrail(sim.Network, plan) && AskName is { } ask)
        {
            Status = "Name the trail (Enter), or Esc to keep drawing.";
            ask(BuildWayCommand.DefaultTrailName(sim.State), Order, () =>
            {
                _points.Clear();
                _points.AddRange(points);
                Status = "Not planned yet: keep drawing, then Enter.";
            });
            return;
        }
        Order("");
    }

    /// <summary>Asks for a finished trail's name (set by the HUD): suggestion, OK, cancel. Without it trails get default names.</summary>
    public Action<string, Action<string>, Action>? AskName { get; set; }

    private PointCm? CursorPoint()
    {
        var mouse = GetViewport().GetMousePosition();
        var camera = _camera.Camera;
        if (!_terrain.TryRaycast(camera.ProjectRayOrigin(mouse), camera.ProjectRayNormal(mouse), 4000f, out var hit))
            return null;
        return new PointCm((int)MathF.Round(hit.X * 100), (int)MathF.Round(hit.Z * 100));
    }

    private void UpdatePreview(Bikepark.Sim.Terrain.TerrainGrid grid)
    {
        if (Plan?.Geometry is not { } g)
        {
            _preview.Visible = false;
            return;
        }

        // Color by gradient per segment; a problem with the whole way (e.g. not connected) dims it.
        var rules = _host.Sim.State.TrailRules;
        bool wholeWayProblem = Plan.Issues.Any(i => i.Severity == IssueSeverity.Error && i.AtCm < 0);
        Color ColorAt(int i)
        {
            var segment = g.Segments[g.SegmentIndexAt(g.Distances[i])];
            var color = WayMeshes.GradientColor(Kind, segment.GradientTenths, rules);
            return wholeWayProblem ? color.Lerp(new Color(0.35f, 0.35f, 0.35f), 0.55f) : color;
        }

        _preview.Mesh = WayMeshes.Ribbon(grid, g, Kind == WayKind.AccessPath ? 3.0f : 1.4f, 0.15f,
            followGround: Kind == WayKind.Trail, ColorAt);
        _preview.Visible = true;
    }

    private void UpdateMarkers(Bikepark.Sim.Terrain.TerrainGrid grid, List<PointCm> candidate)
    {
        var markers = new List<(Vector3 Position, Color Color, float Scale)>();
        foreach (var p in _points)
            markers.Add((new Vector3(p.X / 100f, grid.HeightAt(p.X, p.Z) / 100f + 0.6f, p.Z / 100f), new Color(1, 1, 1), 1f));

        // Snap rings: where the ends attach to the network.
        var sim = _host.Sim;
        if (Plan is not null)
        {
            foreach (var join in new[] { Plan.StartJoin, Plan.EndJoin })
            {
                if (join is null || !sim.Network.TryGetGeometry(join.WayId, out var target)) continue;
                markers.Add((WayMeshes.ToWorld(target.PositionAt(join.DistanceCm)) + Vector3.Up * 0.8f, new Color(0.2f, 1f, 0.4f), 2.2f));
            }
            // Ends snapped onto a plateau or station platform.
            foreach (var (hubId, point) in new[] { (Plan.StartHubId, Plan.Points[0]), (Plan.EndHubId, Plan.Points[^1]) })
                if (hubId != 0 && sim.Network.FindHub(hubId) is { } hub)
                    markers.Add((new Vector3(point.X / 100f, hub.Pad.TargetHeightCm / 100f + 0.8f, point.Z / 100f), new Color(0.2f, 1f, 0.4f), 2.2f));
        }
        if (_cursor is { } c)
        {
            int snap = sim.State.TrailRules.SnapRadiusMeters * 100;
            bool nearNetwork = sim.Network.Nearest(c.X, c.Z, snap) is not null || sim.Network.HubAt(c.X, c.Z, snap) is not null;
            markers.Add((new Vector3(c.X / 100f, grid.HeightAt(c.X, c.Z) / 100f + 0.6f, c.Z / 100f),
                nearNetwork ? new Color(0.2f, 1f, 0.4f) : new Color(1f, 0.9f, 0.3f), 1.4f));
        }

        var mm = _markers.Multimesh;
        if (mm.InstanceCount < markers.Count)
            mm.InstanceCount = Math.Max(markers.Count, mm.InstanceCount * 2);
        for (int i = 0; i < markers.Count; i++)
        {
            mm.SetInstanceTransform(i, new Transform3D(Basis.Identity.Scaled(Vector3.One * markers[i].Scale), markers[i].Position));
            mm.SetInstanceColor(i, markers[i].Color);
        }
        mm.VisibleInstanceCount = markers.Count;
    }
}
