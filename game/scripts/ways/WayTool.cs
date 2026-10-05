using Bikepark.Game.Camera;
using Bikepark.Game.Terrain;
using Bikepark.Sim.Commands;
using Bikepark.Sim.Trails;
using Godot;

namespace Bikepark.Game.Ways;

/// <summary>
/// Drawing tool for access paths (P) and trails (T). Left-click places a point; holding the button and dragging
/// places points continuously. A live preview is validated every frame with the Sim's <see cref="WayPlanner"/>
/// (red = invalid section). Backspace removes the last point, Enter builds (enqueues a <see cref="BuildWayCommand"/>),
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
            ? WayPlanner.Plan(grid, sim.Network, sim.State.TrailRules, Kind, candidate)
            : null;

        UpdatePreview(grid);
        UpdateMarkers(grid, candidate);
    }

    private WayKind Kind => Mode == ToolMode.Trail ? WayKind.Trail : WayKind.AccessPath;

    private void Build()
    {
        var grid = _terrain.Grid;
        if (grid is null || _points.Count < 2)
        {
            Status = "Place at least two points.";
            return;
        }
        var sim = _host.Sim;
        var plan = WayPlanner.Plan(grid, sim.Network, sim.State.TrailRules, Kind, _points);
        if (!plan.IsValid)
        {
            Status = plan.FirstError ?? "Not valid.";
            return;
        }
        _host.Enqueue(new BuildWayCommand(Kind, "", _points.ToList()));
        Status = $"Built ({plan.LengthCm / 100} m{(Kind == WayKind.Trail ? $", {plan.Geometry!.Rating}" : "")}). Draw the next one or press Esc.";
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

        var errors = Plan.Issues.Where(i => i.Severity == IssueSeverity.Error && i.AtCm >= 0).ToList();
        bool invalid = !Plan.IsValid;
        var ok = Kind == WayKind.AccessPath ? WayMeshes.Gravel : WayMeshes.RatingColor(g.Rating);
        var bad = new Color(0.95f, 0.1f, 0.1f);
        var warn = new Color(1f, 0.6f, 0.1f);
        Color ColorAt(int i)
        {
            long d = g.Distances[i];
            if (errors.Any(e => d >= e.AtCm && d <= e.ToCm)) return bad;
            return invalid ? warn : ok; // whole-way problem (e.g. not connected): orange
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
        }
        if (_cursor is { } c)
        {
            bool nearNetwork = sim.Network.Nearest(c.X, c.Z, sim.State.TrailRules.SnapRadiusMeters * 100) is not null;
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
