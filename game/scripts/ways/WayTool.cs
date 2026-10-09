using Bikepark.Game.Camera;
using Bikepark.Game.Terrain;
using Bikepark.Sim;
using Bikepark.Sim.Commands;
using Bikepark.Sim.Crew;
using Bikepark.Sim.Terrain;
using Bikepark.Sim.Trails;
using Godot;

namespace Bikepark.Game.Ways;

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
        Plan = null;
        Status = mode == ToolMode.None ? "" : $"Drawing a {(mode == ToolMode.AccessPath ? "gravel access path" : "trail")}";
    }

    public override void _UnhandledInput(InputEvent @event)
    {
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
        if (!_dragging && _cursor is { } cursor && candidate.Count > 0 && candidate[^1] != cursor)
            candidate.Add(cursor);
        var sim = _host.Sim;
        Plan = candidate.Count >= 2
            ? WayPlanner.Plan(grid, sim.Network, sim.State.TrailRules, Kind, candidate, Bikepark.Sim.Land.LandMath.OwnedPredicate(sim.State))
            : null;
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
        var plan = WayPlanner.Plan(grid, sim.Network, sim.State.TrailRules, Kind, _points, Bikepark.Sim.Land.LandMath.OwnedPredicate(sim.State));
        if (!plan.IsValid)
        {
            Status = plan.FirstError ?? "Not valid.";
            return;
        }
        _host.Enqueue(new BuildWayCommand(Kind, "", _points.ToList(), Instant: _host.InstantBuild));
        string what = $"{plan.LengthCm / 100} m{(Kind == WayKind.Trail ? $", {plan.Geometry!.Rating}" : "")}";
        Status = _host.InstantBuild
            ? $"Built ({what}). Draw the next one or press Esc."
            : $"Planned ({what}): the crew will build it (Crew menu). Draw the next one or press Esc.";
        _points.Clear();
    }

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
